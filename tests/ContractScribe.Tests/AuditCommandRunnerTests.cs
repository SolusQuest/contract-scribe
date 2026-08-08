using System.Collections.Immutable;
using ContractScribe.Cli;
using ContractScribe.Core.Hosting;
using ContractScribe.Roslyn;

namespace ContractScribe.Tests;

public sealed class AuditCommandRunnerTests
{
    [Fact]
    public void Adapt_UnknownHostTerminal_FailsClosedWithoutHostProvenance()
    {
        var revision = new string('1', 40);
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
}
