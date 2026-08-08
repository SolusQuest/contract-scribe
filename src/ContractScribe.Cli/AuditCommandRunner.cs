using System.Security.Cryptography;
using System.Text.Json;
using ContractScribe.Core;
using ContractScribe.Core.Hosting;
using ContractScribe.Roslyn;

namespace ContractScribe.Cli;

internal static class AuditCommandRunner
{
    public static async Task<CliExecutionResult> RunAsync(
        CliBuildIdentity identity,
        CliPreflightResult preflight,
        CancellationToken cancellationToken)
    {
        var host = new ProductionAuditHost(
            new HostBuildProvenance(identity.SourceRevision));
        var outcome = await host.RunAsync(
            new ProductionAuditRequest(
                preflight.RepositoryRoot,
                preflight.InputPath,
                preflight.PolicyBytes,
                preflight.PublicationTarget),
            new ProductionAuditHostControls(),
            cancellationToken).ConfigureAwait(false);

        return Adapt(identity, outcome);
    }

    internal static CliExecutionResult Adapt(
        CliBuildIdentity identity,
        ProductionAuditOutcome outcome)
    {
        var terminal = outcome.Terminal;
        if (terminal.ExecutionOutcome == HostExecutionOutcome.Succeeded)
        {
            return AdaptSuccess(identity, outcome);
        }
        if (!IsValidNonSuccess(outcome))
        {
            return CliPresentation.HostContractError(identity);
        }

        var executionClass = HostVocabulary.GetId(terminal.ExecutionOutcome);
        var exitCode = terminal.ExecutionOutcome switch
        {
            HostExecutionOutcome.InvalidInput => 4,
            HostExecutionOutcome.EnvironmentUnavailable => 4,
            HostExecutionOutcome.LoadFailure => 5,
            HostExecutionOutcome.AuditError => 5,
            HostExecutionOutcome.PublicationFailure => 5,
            HostExecutionOutcome.Cancelled => 6,
            HostExecutionOutcome.Timeout => 7,
            _ => -1,
        };
        if (exitCode < 0 || !HasPermittedToolchainState(terminal))
        {
            return CliPresentation.HostContractError(identity);
        }

        IReadOnlyList<CliDiagnostic> diagnostics =
            terminal.ExecutionOutcome == HostExecutionOutcome.Cancelled
                ? [CliDiagnostics.Create("cli.cancel.requested")]
                : terminal.Diagnostics
                    .Select(diagnostic => CliDiagnostics.Host(diagnostic, outcome.LoaderFact))
                    .ToArray();
        if (diagnostics.Count == 0)
        {
            return CliPresentation.HostContractError(identity);
        }
        return CliPresentation.Execution(
            identity,
            terminal,
            exitCode,
            executionClass,
            diagnostics);
    }

    private static CliExecutionResult AdaptSuccess(
        CliBuildIdentity identity,
        ProductionAuditOutcome outcome)
    {
        var terminal = outcome.Terminal;
        var bytes = outcome.CanonicalResult;
        if (terminal.TerminalState != HostTerminalState.CommittedResult
            || terminal.Failure is not null
            || terminal.AuditOutcome is null
            || terminal.Toolchain.SelectionState != HostToolchainSelectionState.Selected
            || terminal.OutputCommit.State != HostArtifactState.Published
            || terminal.OutputCommit.Sha256 is null
            || bytes is null
            || terminal.OutputCommit.ByteCount != bytes.LongLength
            || !string.Equals(
                terminal.OutputCommit.Sha256,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                StringComparison.Ordinal))
        {
            return CliPresentation.HostContractError(identity);
        }

        AuditCounts counts;
        SortedDictionary<string, int> skippedReasons;
        try
        {
            AuditParser.Parse(bytes);
            (counts, skippedReasons) = ReadCounts(bytes);
        }
        catch (Exception exception) when (exception is AuditValidationException or JsonException)
        {
            return CliPresentation.HostContractError(identity);
        }

        var expectedOutcome = counts.Violation > 0
            ? AuditOutcome.Violation
            : counts.Compliant > 0
                ? AuditOutcome.Compliant
                : AuditOutcome.Skipped;
        if (terminal.AuditOutcome != expectedOutcome)
        {
            return CliPresentation.HostContractError(identity);
        }

        var (disposition, exitCode) = Classify(counts);
        IReadOnlyList<CliDiagnostic> diagnostics = skippedReasons.Count == 0
            ? []
            : [CliDiagnostics.SkippedSummary(string.Join(
                ",",
                skippedReasons.Select(pair => $"{pair.Key}={pair.Value}")))];
        return CliPresentation.Audit(
            identity,
            terminal,
            exitCode,
            disposition,
            counts,
            diagnostics);
    }

    private static bool IsValidNonSuccess(ProductionAuditOutcome outcome)
    {
        var terminal = outcome.Terminal;
        return terminal.TerminalState == HostTerminalState.CommittedNonSuccess
            && terminal.ExecutionOutcome != HostExecutionOutcome.Succeeded
            && terminal.Failure is not null
            && terminal.AuditOutcome is null
            && outcome.CanonicalResult is null
            && terminal.OutputCommit.State != HostArtifactState.Published
            && terminal.OutputCommit.Sha256 is null;
    }

    private static bool HasPermittedToolchainState(HostTerminalRecord terminal)
    {
        var selected = terminal.Toolchain.SelectionState
            == HostToolchainSelectionState.Selected;
        return terminal.ExecutionOutcome switch
        {
            HostExecutionOutcome.InvalidInput => !selected,
            HostExecutionOutcome.EnvironmentUnavailable => !selected,
            HostExecutionOutcome.LoadFailure => selected,
            HostExecutionOutcome.AuditError => selected,
            HostExecutionOutcome.PublicationFailure => true,
            HostExecutionOutcome.Cancelled => true,
            HostExecutionOutcome.Timeout => true,
            _ => false,
        };
    }

    private static (AuditCounts Counts, SortedDictionary<string, int> SkippedReasons)
        ReadCounts(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var compliant = 0;
        var violation = 0;
        var skipped = 0;
        var reasons = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var result in document.RootElement.GetProperty("results").EnumerateArray())
        {
            var outcome = result.GetProperty("auditOutcome").GetString();
            switch (outcome)
            {
                case "audit.outcome.compliant":
                    compliant++;
                    break;
                case "audit.outcome.violation":
                    violation++;
                    break;
                case "audit.outcome.skipped":
                    skipped++;
                    var reason = result.GetProperty("reasonCode").GetString()!;
                    reasons.TryGetValue(reason, out var count);
                    reasons[reason] = count + 1;
                    break;
                default:
                    throw new JsonException("Unknown audit outcome.");
            }
        }
        return (new AuditCounts(compliant, violation, skipped), reasons);
    }

    private static (string Disposition, int ExitCode) Classify(AuditCounts counts) =>
        counts switch
        {
            { Violation: > 0, Skipped: > 0 } => ("violations-with-skipped", 1),
            { Violation: > 0 } => ("violations", 1),
            { Compliant: > 0, Skipped: > 0 } => ("compliant-with-skipped", 0),
            { Compliant: > 0 } => ("compliant", 0),
            { Skipped: > 0 } => ("skipped-only", 3),
            _ => ("no-results", 3),
        };
}
