using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
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
        Assert.Equal(64, HostContractResources.CalibrationEvidenceSha256.Length);
        using var bounds = JsonDocument.Parse(HostContractResources.CalibratedBoundsBytes);
        Assert.All(
            bounds.RootElement.GetProperty("entries").EnumerateArray(),
            entry => Assert.Equal(
                HostContractResources.CalibrationEvidenceSha256,
                entry.GetProperty("calibrationEvidenceSha256").GetString()));
    }

    [Fact]
    public void BuildProvenance_RequiresAnExactLowercaseSourceRevision()
    {
        var provenance = new HostBuildProvenance(new string('a', 40));

        Assert.Equal(new string('a', 40), provenance.SourceRevision);
        Assert.Throws<ArgumentException>(() => new HostBuildProvenance(new string('a', 39)));
        Assert.Throws<ArgumentException>(() => new HostBuildProvenance(new string('A', 40)));
    }

    [Fact]
    public void ObservableOnlyBound_CanReportAboveItsCalibrationThreshold()
    {
        var threshold = HostContractResources.RequireBound("toolchain-subprocess-count");

        var observed = new HostMeasuredBound(
            "toolchain-subprocess-count",
            "count",
            threshold + 1,
            threshold,
            HostEnforcementClass.ObservableOnly);

        Assert.Equal(threshold + 1, observed.Measured);
        Assert.Throws<ArgumentException>(() => new HostMeasuredBound(
            "temporary-disk-bytes",
            "bytes",
            threshold + 1,
            threshold,
            HostEnforcementClass.InternallyEnforceable));
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
        foreach (var unsafeValue in new[]
                 {
                     "C:\\Users\\fixture\\secret.txt",
                     "D:/agent/_work/input.json",
                     "/home/runner/input.json",
                     "\\\\server\\share\\input.json",
                     "file:///tmp/input.json",
                     "Authorization: Bearer fixture",
                     "api_key=fixture",
                     "client-secret=fixture",
                 })
        {
            Assert.Throws<ArgumentException>(() => new HostDiagnosticFact(
                "host.audit.unsafe",
                HostStage.Audit,
                HostDiagnosticSeverity.Error,
                "host.audit.fixture",
                [unsafeValue]));
        }
        foreach (var unsafePath in new[]
                 {
                     "C:\\repo\\Fixture.cs",
                     "D:/repo/Fixture.cs",
                     "/repo/Fixture.cs",
                     "\\\\server\\share\\Fixture.cs",
                 })
        {
            Assert.Throws<ArgumentException>(() => new HostDiagnosticFact(
                "host.audit.unsafe-path",
                HostStage.Audit,
                HostDiagnosticSeverity.Error,
                "host.audit.fixture",
                repositoryRelativePath: unsafePath));
        }

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

    [Fact]
    public void DiagnosticEnvelope_EnforcesTheCalibratedProductionCaps()
    {
        var countLimit = checked((int)HostContractResources.RequireBound("diagnostic-count"));
        var byteLimit = checked((int)HostContractResources.RequireBound("diagnostic-utf8-bytes"));
        var countFacts = Enumerable.Range(0, countLimit + 8)
            .Select(index => new HostDiagnosticFact(
                $"host.audit.count-{index:D2}",
                HostStage.Audit,
                HostDiagnosticSeverity.Warning,
                "host.audit.fixture"));

        var countBounded = HostDiagnosticEnvelope.Normalize(countFacts, countLimit, byteLimit);
        Assert.Equal(countLimit, countBounded.Length);

        var byteFacts = Enumerable.Range(0, countLimit)
            .Select(index => new HostDiagnosticFact(
                $"host.audit.bytes-{index:D2}",
                HostStage.Audit,
                HostDiagnosticSeverity.Warning,
                "host.audit.fixture",
                Enumerable.Repeat(new string((char)('a' + index % 26), 128), 8)));
        var byteBounded = HostDiagnosticEnvelope.Normalize(byteFacts, countLimit, byteLimit);
        var serialized = JsonSerializer.SerializeToUtf8Bytes(
            byteBounded,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        Assert.True(byteBounded.Length < countLimit);
        Assert.True(serialized.Length <= byteLimit);
    }

    [Fact]
    public void DiagnosticEnvelope_ReservesThePrimaryFactBeforeSupportingCaps()
    {
        var primary = new HostDiagnosticFact(
            "host.workspace-load.failed",
            HostStage.WorkspaceLoad,
            HostDiagnosticSeverity.Error,
            "host.workspace-load.failed");
        var supporting = Enumerable.Range(0, 40)
            .Select(index => new HostDiagnosticFact(
                $"host.audit.supporting-{index:D2}",
                HostStage.Audit,
                HostDiagnosticSeverity.Warning,
                "host.audit.fixture"));

        var normalized = HostDiagnosticEnvelope.Normalize(
            supporting.Append(primary),
            maximumCount: 1,
            maximumUtf8Bytes: 4096,
            requiredFact: primary);

        Assert.Same(primary, Assert.Single(normalized));
    }

    [Fact]
    public void RegisteredCause_AtomicallyPreventsPublicationAcquisitionUntilCommitted()
    {
        var coordinator = new HostTerminalCoordinator();
        var cause = Failure(coordinator, "host.publication.cancelled");

        Assert.True(coordinator.TryRegisterCause(cause, out var registered));
        Assert.Same(cause, registered);
        Assert.False(coordinator.TryAcquirePublicationDecision(
            out var publication,
            out var winner));
        Assert.Null(publication);
        Assert.Same(cause, winner);
        Assert.True(coordinator.TryCommitRegisteredCause(cause, out var terminal));
        Assert.Same(cause, terminal);
        Assert.Same(cause, coordinator.Terminal);
    }

    [Fact]
    public void EveryFailureRegistryRow_HasOneReachableCommittedTerminalShape()
    {
        foreach (var row in HostContractResources.FailureRegistry)
        {
            var coordinator = new HostTerminalCoordinator();
            var candidate = Failure(coordinator, row.Code);

            Assert.True(coordinator.TryCommitNonSuccess(candidate, out var accepted));
            Assert.Same(candidate, accepted);
            Assert.Same(candidate, coordinator.Terminal);
            Assert.Same(row, candidate.Failure);
            Assert.Equal(row.Stage, candidate.Diagnostics[0].Stage);
            Assert.Equal(row.Code, candidate.Diagnostics[0].Code);
            Assert.Equal(row.ExecutionOutcome, candidate.ExecutionOutcome);
        }
    }

    [Fact]
    public void PublicationLinearization_RejectsEveryLaterNonSuccessCause()
    {
        var coordinator = new HostTerminalCoordinator();
        Assert.True(coordinator.TryAcquirePublicationDecision(out var decision));

        var lateCause = Failure(coordinator, "host.publication.cancelled");
        Assert.False(coordinator.TryRegisterCause(lateCause, out _));
        Assert.False(coordinator.TryCommitNonSuccess(lateCause, out _));

        var bytes = "{}\n"u8.ToArray();
        var committed = new CommittedCanonicalResult(
            bytes,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            Provenance(),
            HostToolchainFact.Selected("10.0.102", "10.0.2", "18.0.0", "X64"));
        decision!.CommitRename(committed);

        var success = coordinator.DeriveSuccessRecord(AuditOutcome.Compliant, [], []);
        Assert.Same(success, coordinator.Terminal);
        Assert.Equal(HostTerminalState.CommittedResult, success.TerminalState);
    }

    [Fact]
    public void RegisteredCause_MayGainSupportingFactsButCannotBeReclassified()
    {
        var coordinator = new HostTerminalCoordinator();
        var registered = Failure(coordinator, "host.publication.cancelled");
        Assert.True(coordinator.TryRegisterCause(registered, out _));

        var cleanup = new HostDiagnosticFact(
            "host.publication.cleanup-failed",
            HostStage.Publication,
            HostDiagnosticSeverity.Error,
            "host.publication.cleanup-failed");
        var final = registered with { Diagnostics = [.. registered.Diagnostics, cleanup] };

        Assert.True(coordinator.TryCommitRegisteredCause(registered, final, out var accepted));
        Assert.Same(final, accepted);
        Assert.Equal(HostExecutionOutcome.Cancelled, accepted.ExecutionOutcome);
        Assert.Equal("host.publication.cancelled", accepted.Failure!.Code);
        Assert.Contains(accepted.Diagnostics, item => item.Code == "host.publication.cleanup-failed");

        var second = new HostTerminalCoordinator();
        var original = Failure(second, "host.publication.cancelled");
        Assert.True(second.TryRegisterCause(original, out _));
        var reclassified = Failure(second, "host.publication.cleanup-failed") with
        {
            AcceptedSequence = original.AcceptedSequence,
        };
        Assert.Throws<ArgumentException>(() =>
            second.TryCommitRegisteredCause(original, reclassified, out _));
    }

    [Fact]
    public void CauseArbitration_UsesCausalSequenceInsteadOfCallbackLockOrder()
    {
        var timeoutFirst = new HostTerminalCoordinator();
        var timeout = Failure(timeoutFirst, "host.publication.timeout");
        var laterCancellation = Failure(timeoutFirst, "host.publication.cancelled");
        Assert.True(timeoutFirst.TryRegisterCause(timeout, out _));
        Assert.False(timeoutFirst.TryRegisterCause(laterCancellation, out var timeoutWinner));
        Assert.Same(timeout, timeoutWinner);

        var cancellationFirst = new HostTerminalCoordinator();
        var cancellation = Failure(cancellationFirst, "host.publication.cancelled");
        var laterTimeout = Failure(cancellationFirst, "host.publication.timeout");
        Assert.True(cancellationFirst.TryRegisterCause(cancellation, out _));
        Assert.False(cancellationFirst.TryRegisterCause(laterTimeout, out var cancellationWinner));
        Assert.Same(cancellation, cancellationWinner);

        var delayedEarlierCallback = new HostTerminalCoordinator();
        var earlier = Failure(delayedEarlierCallback, "host.publication.timeout");
        var later = Failure(delayedEarlierCallback, "host.publication.cancelled");
        Assert.True(delayedEarlierCallback.TryRegisterCause(later, out _));
        Assert.True(delayedEarlierCallback.TryRegisterCause(earlier, out var correctedWinner));
        Assert.Same(earlier, correctedWinner);
    }

    [Fact]
    public void CauseArbitration_UsesCancellationAsTheSameSequenceTieBreaker()
    {
        var coordinator = new HostTerminalCoordinator();
        var timeout = Failure(coordinator, "host.publication.timeout");
        var cancellation = Failure(coordinator, "host.publication.cancelled") with
        {
            AcceptedSequence = timeout.AcceptedSequence,
        };

        Assert.True(coordinator.TryRegisterCause(timeout, out _));
        Assert.True(coordinator.TryRegisterCause(cancellation, out var winner));
        Assert.Same(cancellation, winner);
    }

    [Fact]
    public void AcceptedInterruption_PrecedesLaterStageFailureAndPublication()
    {
        var coordinator = new HostTerminalCoordinator();
        var cancellation = Failure(coordinator, "host.audit.cancelled");
        var stageFailure = Failure(coordinator, "host.audit.aggregation-failed");

        Assert.True(coordinator.TryRegisterCause(cancellation, out _));
        Assert.False(coordinator.TryCommitNonSuccess(stageFailure, out var winner));
        Assert.Same(cancellation, winner);
        Assert.False(coordinator.TryAcquirePublicationDecision(out _, out var publicationWinner));
        Assert.Same(cancellation, publicationWinner);
        Assert.True(coordinator.TryCommitRegisteredCause(cancellation, out var terminal));
        Assert.Same(cancellation, terminal);
    }

    [Fact]
    public void AtomicCauseAcceptance_InstallsTheCauseBeforeLaterTerminalDecisions()
    {
        var coordinator = new HostTerminalCoordinator();
        var selected = HostToolchainFact.Selected(
            "10.0.102",
            "10.0.2",
            "18.0.0",
            "X64");
        coordinator.TransitionExecutionState(HostStage.Audit, selected);

        Assert.True(coordinator.TryAcceptCause(
            (stage, toolchain, sequence) =>
            {
                Assert.Equal(HostStage.Audit, stage);
                Assert.Same(selected, toolchain);
                return Failure(sequence, "host.audit.cancelled", toolchain);
            },
            out var cancellation));

        var stageFailure = Failure(coordinator, "host.audit.aggregation-failed");
        Assert.False(coordinator.TryCommitNonSuccess(stageFailure, out var failureWinner));
        Assert.Same(cancellation, failureWinner);
        Assert.False(coordinator.TryAcquirePublicationDecision(out _, out var publicationWinner));
        Assert.Same(cancellation, publicationWinner);
    }

    [Fact]
    public void AtomicExecutionStateTransition_PreservesBothSidesOfToolchainSelection()
    {
        var before = new HostTerminalCoordinator();
        before.TransitionExecutionState(
            HostStage.SdkDiscovery,
            HostToolchainFact.NotSelected);
        Assert.True(before.TryAcceptCause(
            (stage, toolchain, sequence) =>
                Failure(sequence, $"host.{HostVocabulary.GetId(stage)}.cancelled", toolchain),
            out var notSelected));
        Assert.Equal(HostStage.SdkDiscovery, notSelected.Failure!.Stage);
        Assert.Equal(HostToolchainSelectionState.NotSelected, notSelected.Toolchain.SelectionState);

        var after = new HostTerminalCoordinator();
        var selected = HostToolchainFact.Selected(
            "10.0.102",
            "10.0.2",
            "18.0.0",
            "X64");
        HostToolchainFact local = HostToolchainFact.NotSelected;
        after.TransitionExecutionState(
            HostStage.SdkDiscovery,
            selected,
            () => local = selected);
        Assert.Same(selected, local);
        Assert.True(after.TryAcceptCause(
            (stage, toolchain, sequence) =>
                Failure(sequence, $"host.{HostVocabulary.GetId(stage)}.cancelled", toolchain),
            out var selectedCause));
        Assert.Equal(HostStage.SdkDiscovery, selectedCause.Failure!.Stage);
        Assert.Same(selected, selectedCause.Toolchain);
    }

    [Fact]
    public async Task AcceptedCause_RetainsTheStageWhileItsCallbackIsPaused()
    {
        var coordinator = new HostTerminalCoordinator();
        var selected = HostToolchainFact.Selected(
            "10.0.102",
            "10.0.2",
            "18.0.0",
            "X64");
        coordinator.TransitionExecutionState(HostStage.Classification, selected);
        var acceptedSignal = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var callback = Task.Run(async () =>
        {
            Assert.True(coordinator.TryAcceptCause(
                (stage, toolchain, sequence) =>
                    Failure(sequence, $"host.{HostVocabulary.GetId(stage)}.cancelled", toolchain),
                out var accepted));
            acceptedSignal.SetResult();
            await releaseCallback.Task.ConfigureAwait(false);
            return accepted;
        });

        await acceptedSignal.Task;
        coordinator.TransitionExecutionState(HostStage.Audit, selected);
        releaseCallback.SetResult();

        var cause = await callback;
        Assert.Equal(HostStage.Classification, cause.Failure!.Stage);
        Assert.Same(selected, cause.Toolchain);
    }

    private static HostTerminalRecord Failure(
        HostTerminalCoordinator coordinator,
        string code)
        => Failure(
            coordinator.NextCauseSequence(),
            code,
            HostToolchainFact.NotSelected);

    private static HostTerminalRecord Failure(
        long acceptedSequence,
        string code,
        HostToolchainFact toolchain)
    {
        var row = HostContractResources.RequireFailure(code);
        var diagnostic = new HostDiagnosticFact(
            row.Code,
            row.Stage,
            HostDiagnosticSeverity.Error,
            row.Code);
        return new HostTerminalRecord(
            row.ExecutionOutcome,
            null,
            HostTerminalState.CommittedNonSuccess,
            row,
            Provenance(),
            toolchain,
            new HostOutputCommit(HostArtifactState.Invalidated, null, 0),
            [diagnostic],
            ImmutableArray<HostMeasuredBound>.Empty,
            acceptedSequence);
    }

    private static HostBuildProvenance Provenance() => new(new string('1', 40));
}
