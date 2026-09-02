using System.Collections.Immutable;
using ContractScribe.Cli;
using ContractScribe.Core.Hosting;
using ContractScribe.Roslyn;

namespace ContractScribe.Tests;

public sealed class AuditCommandRunnerTests
{
    private const string Revision = "1111111111111111111111111111111111111111";

    [Fact]
    public void Adapt_UnknownHostTerminal_FailsClosedWithoutHostProvenance()
    {
        var revision = Revision;
        var identity = CliBuildIdentity.Create($"0.1.0-test+{revision}");
        var inconsistent = new HostTerminalRecord(
            HostExecutionOutcome.Succeeded,
            null,
            HostTerminalState.CommittedNonSuccess,
            null,
            new HostBuildProvenance(revision),
            HostToolchainFact.NotSelected,
            new HostOutputCommit(HostArtifactState.Invalidated, null, 0),
            ImmutableArray<HostDiagnosticFact>.Empty,
            ImmutableArray<HostMeasuredBound>.Empty,
            1);

        var result = AuditCommandRunner.Adapt(
            identity,
            new ProductionAuditOutcome(inconsistent, null, null, []));

        Assert.Equal(5, result.ExitCode);
        Assert.Equal(
            "cli.host.unknown-terminal: the host reported an unknown or unmapped terminal class\n",
            Assert.Single(result.Diagnostics).ToLine());
        Assert.Equal(
            $"{{\"envelopeVersion\":1,\"terminalLayer\":\"host-contract-error\",\"cliContractBaseline\":\"{revision}\",\"toolVersion\":\"0.1.0-test+{revision}\",\"diagnosticCodes\":[\"cli.host.unknown-terminal\"]}}\n",
            result.StandardOutput);
        Assert.DoesNotContain("sourceRevision", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("toolchain", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void Adapt_AcceptsEveryClosedFailureRegistryShape()
    {
        var identity = Identity();
        foreach (var failure in HostContractResources.FailureRegistry)
        {
            var result = AuditCommandRunner.Adapt(identity, ValidFailure(failure));

            Assert.DoesNotContain(
                "\"terminalLayer\":\"host-contract-error\"",
                result.StandardOutput,
                StringComparison.Ordinal);
        }

        var invalidationWindowCancellation = ValidFailure(
            HostContractResources.RequireFailure("host.publication.cancelled"));
        var notSelected = invalidationWindowCancellation with
        {
            Terminal = invalidationWindowCancellation.Terminal with
            {
                Toolchain = HostToolchainFact.NotSelected,
            },
        };
        Assert.DoesNotContain(
            "\"terminalLayer\":\"host-contract-error\"",
            AuditCommandRunner.Adapt(identity, notSelected).StandardOutput,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("host.sdk-discovery.cancelled", 6)]
    [InlineData("host.sdk-discovery.timeout", 7)]
    public void Adapt_PreservesCancellationAndTimeoutExitCodes(string code, int expectedExitCode)
    {
        var outcome = ValidFailure(HostContractResources.RequireFailure(code));

        var result = AuditCommandRunner.Adapt(Identity(), outcome);

        Assert.Equal(expectedExitCode, result.ExitCode);
        Assert.DoesNotContain(
            "\"terminalLayer\":\"host-contract-error\"",
            result.StandardOutput,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Adapt_RejectsContradictoryFailureTerminalShapes()
    {
        var identity = Identity();
        var baseline = ValidFailure(HostContractResources.RequireFailure(
            "host.workspace-load.failed"));
        var terminal = baseline.Terminal;
        var otherFailure = HostContractResources.RequireFailure(
            "host.classification.failed");
        var malformed = new ProductionAuditOutcome[]
        {
            baseline with
            {
                Terminal = terminal with
                {
                    Provenance = new HostBuildProvenance(new string('2', 40)),
                },
            },
            baseline with
            {
                Terminal = terminal with { Failure = otherFailure },
            },
            baseline with
            {
                Terminal = terminal with
                {
                    ExecutionOutcome = HostExecutionOutcome.Cancelled,
                },
            },
            baseline with
            {
                Terminal = terminal with
                {
                    Diagnostics =
                    [new HostDiagnosticFact(
                        terminal.Failure!.Code,
                        terminal.Failure.Stage,
                        HostDiagnosticSeverity.Warning,
                        terminal.Failure.Code)],
                },
            },
            baseline with
            {
                Terminal = terminal with
                {
                    Diagnostics =
                    [new HostDiagnosticFact(
                        terminal.Failure!.Code,
                        terminal.Failure.Stage,
                        HostDiagnosticSeverity.Error,
                        "host.internal.unexpected")],
                },
            },
            baseline with
            {
                Terminal = terminal with
                {
                    Diagnostics =
                    [new HostDiagnosticFact(
                        terminal.Failure!.Code,
                        terminal.Failure.Stage,
                        HostDiagnosticSeverity.Error,
                        terminal.Failure.Code,
                        ["unexpected"])],
                },
            },
            baseline with
            {
                Terminal = terminal with
                {
                    OutputCommit = new HostOutputCommit(
                        HostArtifactState.Invalidated,
                        null,
                        1),
                },
            },
            baseline with
            {
                Terminal = terminal with { Toolchain = HostToolchainFact.NotSelected },
            },
            ValidFailure(HostContractResources.RequireFailure(
                "host.publication.invalidation-failed")) with
            {
                Terminal = ValidFailure(HostContractResources.RequireFailure(
                    "host.publication.invalidation-failed")).Terminal with
                {
                    Toolchain = HostToolchainFact.Selected(
                        "10.0.100",
                        "10.0.0",
                        "18.0",
                        "x64"),
                },
            },
            ValidFailure(HostContractResources.RequireFailure(
                "host.publication.finalization-failed")) with
            {
                Terminal = ValidFailure(HostContractResources.RequireFailure(
                    "host.publication.finalization-failed")).Terminal with
                {
                    Toolchain = HostToolchainFact.NotSelected,
                },
            },
            baseline with { CanonicalResult = [0x7b, 0x7d] },
            baseline with
            {
                Terminal = terminal with
                {
                    ExecutionOutcome = (HostExecutionOutcome)999,
                },
            },
            baseline with { Terminal = null! },
        };

        foreach (var outcome in malformed)
        {
            AssertHostContractError(identity, outcome);
        }
    }

    private static CliBuildIdentity Identity() =>
        CliBuildIdentity.Create($"0.1.0-test+{Revision}");

    private static ProductionAuditOutcome ValidFailure(
        HostFailureRegistryEntry failure)
    {
        var toolchain = failure.Stage is HostStage.Input
            or HostStage.Environment
            or HostStage.SdkDiscovery
            || failure.Code == "host.publication.invalidation-failed"
            ? HostToolchainFact.NotSelected
            : HostToolchainFact.Selected("10.0.100", "10.0.0", "18.0", "x64");
        return new ProductionAuditOutcome(
            new HostTerminalRecord(
                failure.ExecutionOutcome,
                null,
                HostTerminalState.CommittedNonSuccess,
                failure,
                new HostBuildProvenance(Revision),
                toolchain,
                new HostOutputCommit(HostArtifactState.Invalidated, null, 0),
                [new HostDiagnosticFact(
                    failure.Code,
                    failure.Stage,
                    HostDiagnosticSeverity.Error,
                    failure.Code)],
                [],
                1),
            null,
            null,
            []);
    }

    private static void AssertHostContractError(
        CliBuildIdentity identity,
        ProductionAuditOutcome outcome)
    {
        var result = AuditCommandRunner.Adapt(identity, outcome);
        Assert.Equal(5, result.ExitCode);
        Assert.Contains(
            "\"terminalLayer\":\"host-contract-error\"",
            result.StandardOutput,
            StringComparison.Ordinal);
    }
}
