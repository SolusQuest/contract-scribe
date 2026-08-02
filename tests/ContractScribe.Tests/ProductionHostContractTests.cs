using System.Collections.Immutable;
using System.Security.Cryptography;
using ContractScribe.Core;
using ContractScribe.Core.Hosting;

namespace ContractScribe.Tests;

public sealed class ProductionHostContractTests
{
    [Fact]
    public void EmbeddedFailureRegistry_IsClosedUniqueAndBoundToItsDigest()
    {
        var rows = HostContractResources.FailureRegistry;

        Assert.NotEmpty(rows);
        Assert.Equal(rows.Length, rows.Select(row => row.Code).Distinct(StringComparer.Ordinal).Count());
        Assert.All(rows, row => Assert.Same(row, HostContractResources.RequireFailure(row.Code)));
        Assert.Equal(64, HostContractResources.FailureRegistrySha256.Length);
        Assert.Equal(64, HostContractResources.CalibratedBoundsSha256.Length);
        Assert.Equal(64, HostContractResources.ContractBaselineSha256.Length);
    }

    [Fact]
    public void TerminalCoordinator_RejectsLateSuccessAfterCommittedFailure()
    {
        var coordinator = new HostTerminalCoordinator();
        var failure = Failure(coordinator, "host.audit.aggregation-failed");

        Assert.True(coordinator.TryCommitNonSuccess(failure, out var accepted));
        Assert.Same(failure, accepted);
        Assert.False(coordinator.TryBeginLatePublishedResultAttempt());
        Assert.False(coordinator.TryCommitNonSuccess(failure, out _));
        Assert.Same(failure, coordinator.Terminal);
    }

    [Fact]
    public void SuccessRecord_CanDeriveOnlyAfterPublishedBytesCommit()
    {
        var coordinator = new HostTerminalCoordinator();
        Assert.Throws<InvalidOperationException>(() => coordinator.DeriveSuccessRecord(
            AuditOutcome.Compliant,
            [],
            []));
        Assert.True(coordinator.TryAcquirePublicationDecision(out var decision));
        var bytes = "{}\n"u8.ToArray();
        var committed = new CommittedCanonicalResult(
            bytes,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            Provenance(),
            HostToolchainFact.Selected("10.0.102", "10.0.2", "18.0.0", "X64"));

        Assert.Same(committed, decision!.CommitRename(committed));
        var success = coordinator.DeriveSuccessRecord(AuditOutcome.Compliant, [], []);

        Assert.Equal(HostExecutionOutcome.Succeeded, success.ExecutionOutcome);
        Assert.Equal(HostTerminalState.CommittedResult, success.TerminalState);
        Assert.Equal(HostArtifactState.Published, success.OutputCommit.State);
        Assert.Equal(committed.Sha256, success.OutputCommit.Sha256);
        bytes[0] = (byte)'!';
        Assert.Equal((byte)'{', committed.Bytes[0]);
        Assert.Throws<ArgumentException>(() => new CommittedCanonicalResult(
            "{}\n"u8,
            new string('b', 64),
            Provenance(),
            committed.Toolchain));
        Assert.Same(success, coordinator.Terminal);
        Assert.False(coordinator.TryBeginLatePublishedResultAttempt());
        Assert.Throws<InvalidOperationException>(() => coordinator.DeriveSuccessRecord(
            AuditOutcome.Violation,
            [],
            []));
        Assert.False(coordinator.TryCommitNonSuccess(
            Failure(coordinator, "host.audit.aggregation-failed"),
            out var accepted));
        Assert.Same(success, accepted);
    }

    [Fact]
    public void DiagnosticEnvelope_IsDeterministicBoundedAndPublicSafe()
    {
        var facts = Enumerable.Range(0, 40)
            .Select(index => new HostDiagnosticFact(
                $"host.audit.fact-{index:D2}",
                HostStage.Audit,
                HostDiagnosticSeverity.Error,
                "host.audit.fixture"))
            .Reverse();

        var normalized = HostDiagnosticEnvelope.Normalize(facts, 4, 4096);

        Assert.Equal(4, normalized.Length);
        Assert.Equal(
            normalized.OrderBy(item => item.Code, StringComparer.Ordinal),
            normalized);
        Assert.Throws<ArgumentException>(() => new HostDiagnosticFact(
            "host.audit.unsafe",
            HostStage.Audit,
            HostDiagnosticSeverity.Error,
            "host.audit.fixture",
            ["access_token"]));

        var first = new HostDiagnosticFact(
            "host.audit.duplicate",
            HostStage.Audit,
            HostDiagnosticSeverity.Warning,
            "host.audit.fixture",
            ["safe-argument"],
            "src/Fixture.cs");
        var second = new HostDiagnosticFact(
            "host.audit.duplicate",
            HostStage.Audit,
            HostDiagnosticSeverity.Warning,
            "host.audit.fixture",
            [new string("safe-argument".ToCharArray())],
            "src\\Fixture.cs");

        Assert.Single(HostDiagnosticEnvelope.Normalize([first, second], 4, 4096));
    }

    private static HostTerminalRecord Failure(
        HostTerminalCoordinator coordinator,
        string code)
    {
        var row = HostContractResources.RequireFailure(code);
        return new HostTerminalRecord(
            row.ExecutionOutcome,
            null,
            HostTerminalState.CommittedNonSuccess,
            row,
            Provenance(),
            HostToolchainFact.NotSelected,
            new HostOutputCommit(HostArtifactState.Invalidated, null, 0),
            ImmutableArray<HostDiagnosticFact>.Empty,
            ImmutableArray<HostMeasuredBound>.Empty,
            coordinator.NextCauseSequence());
    }

    private static HostBuildProvenance Provenance() => new(
        new string('1', 40),
        "source." + new string('2', 64),
        "10.0.102",
        HostContractResources.ContractBaselineSha256,
        HostContractResources.FailureRegistrySha256,
        HostContractResources.CalibratedBoundsSha256);
}
