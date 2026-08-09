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
        if (terminal is null)
        {
            return CliPresentation.HostContractError(identity);
        }
        if (terminal.ExecutionOutcome == HostExecutionOutcome.Succeeded)
        {
            return AdaptSuccess(identity, outcome, terminal);
        }
        if (!IsValidNonSuccess(identity, outcome, terminal))
        {
            return CliPresentation.HostContractError(identity);
        }

        if (!TryMapExecution(terminal.ExecutionOutcome, out var executionClass, out var exitCode))
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
        ProductionAuditOutcome outcome,
        HostTerminalRecord terminal)
    {
        var bytes = outcome.CanonicalResult;
        if (terminal.TerminalState != HostTerminalState.CommittedResult
            || terminal.Failure is not null
            || terminal.AuditOutcome is null
            || terminal.Provenance is null
            || terminal.Toolchain is null
            || terminal.OutputCommit is null
            || terminal.AcceptedSequence <= 0
            || !string.Equals(
                terminal.Provenance.SourceRevision,
                identity.SourceRevision,
                StringComparison.Ordinal)
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

    private static bool IsValidNonSuccess(
        CliBuildIdentity identity,
        ProductionAuditOutcome outcome,
        HostTerminalRecord terminal)
    {
        if (terminal.TerminalState != HostTerminalState.CommittedNonSuccess
            || terminal.ExecutionOutcome == HostExecutionOutcome.Succeeded
            || terminal.Failure is null
            || terminal.Provenance is null
            || terminal.Toolchain is null
            || terminal.OutputCommit is null
            || terminal.AuditOutcome is not null
            || outcome.CanonicalResult is not null
            || terminal.OutputCommit.State != HostArtifactState.Invalidated
            || terminal.OutputCommit.Sha256 is not null
            || terminal.OutputCommit.ByteCount != 0
            || terminal.AcceptedSequence <= 0
            || !string.Equals(
                terminal.Provenance.SourceRevision,
                identity.SourceRevision,
                StringComparison.Ordinal)
            || !HasPermittedToolchainState(terminal))
        {
            return false;
        }

        var registryMatches = HostContractResources.FailureRegistry
            .Where(row => string.Equals(
                row.Code,
                terminal.Failure.Code,
                StringComparison.Ordinal))
            .ToArray();
        if (registryMatches.Length != 1
            || registryMatches[0] != terminal.Failure
            || registryMatches[0].Stage != terminal.Failure.Stage
            || registryMatches[0].ExecutionOutcome != terminal.ExecutionOutcome
            || terminal.Diagnostics.IsDefaultOrEmpty
            || terminal.Diagnostics.Any(diagnostic => diagnostic is null))
        {
            return false;
        }

        var primary = terminal.Diagnostics[0];
        return string.Equals(primary.Code, terminal.Failure.Code, StringComparison.Ordinal)
            && primary.Stage == terminal.Failure.Stage
            && primary.Severity == HostDiagnosticSeverity.Error
            && string.Equals(primary.TemplateId, terminal.Failure.Code, StringComparison.Ordinal)
            && primary.Arguments.IsEmpty
            && primary.RepositoryRelativePath is null;
    }

    private static bool HasPermittedToolchainState(HostTerminalRecord terminal)
    {
        if (terminal.Failure is null || terminal.Toolchain is null)
        {
            return false;
        }
        if (terminal.Failure.Code == "host.publication.invalidation-failed")
        {
            return terminal.Toolchain.SelectionState
                == HostToolchainSelectionState.NotSelected;
        }
        if (terminal.Failure.Code is "host.publication.finalization-failed"
            or "host.publication.cleanup-failed"
            or "host.publication.timeout")
        {
            return terminal.Toolchain.SelectionState
                == HostToolchainSelectionState.Selected;
        }

        var expected = terminal.Failure.Stage switch
        {
            HostStage.Input or HostStage.Environment or HostStage.SdkDiscovery =>
                HostToolchainSelectionState.NotSelected,
            HostStage.Publication => terminal.Toolchain.SelectionState,
            HostStage.WorkspaceLoad
                or HostStage.Classification
                or HostStage.DocumentationObservation
                or HostStage.PolicyEvidence
                or HostStage.Audit
                or HostStage.ResultValidation
                or HostStage.Shutdown
                or HostStage.Internal => HostToolchainSelectionState.Selected,
            _ => (HostToolchainSelectionState)(-1),
        };
        return expected == terminal.Toolchain.SelectionState;
    }

    private static bool TryMapExecution(
        HostExecutionOutcome outcome,
        out string executionClass,
        out int exitCode)
    {
        (executionClass, exitCode) = outcome switch
        {
            HostExecutionOutcome.InvalidInput => ("invalid-input", 4),
            HostExecutionOutcome.EnvironmentUnavailable => ("environment-unavailable", 4),
            HostExecutionOutcome.LoadFailure => ("load-failure", 5),
            HostExecutionOutcome.AuditError => ("audit-error", 5),
            HostExecutionOutcome.PublicationFailure => ("publication-failure", 5),
            HostExecutionOutcome.Cancelled => ("cancelled", 6),
            HostExecutionOutcome.Timeout => ("timeout", 7),
            _ => (string.Empty, -1),
        };
        return exitCode >= 0;
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
