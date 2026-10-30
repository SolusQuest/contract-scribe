using ContractScribe.Core;

namespace ContractScribe.Tests;

public sealed partial class CampaignStateContractTests
{
    [Theory]
    [InlineData(20, 900, 10, false)]
    [InlineData(300, 90, 10, true)]
    [InlineData(100, 90, 10, true)]
    [InlineData(100, 90, 11, false)]
    public async Task Host_closure_folds_only_when_original_remaining_exposure_funds_the_whole_continuation(
        int terminal, int request, int elapsed, bool folded)
    {
        var clock = new SolusDeadlineClock();
        var trial = await SolusTrialAsync(clock: clock, targetMilliseconds: 1000,
            invocationMilliseconds: 1000, hostMilliseconds: request,
            hostTimeouts: DocumentationScribeHostTimeouts.Create(terminalSubmissionMilliseconds: terminal));
        var scope = trial.Coordinator.Scope;
        scope.BindRuntimeWindow(trial.Request, clock, clock.GetTimestamp());
        using var parent = new CancellationTokenSource();
        var permit = Assert.IsType<DocumentationScribeHostPermit>(
            await scope.BeginHostAsync(DocumentationScribeHostOperation.TerminalSubmission, parent.Token));
        permit.ObserveCancellation(default, parent.Token, parent.Token, parent.Token);
        Assert.True(permit.TryBeginOperation());
        var acknowledged = trial.Coordinator.LatestAcknowledgedArtifact;
        clock.Advance(elapsed);
        Assert.True(await scope.CompleteHostAsync(permit, default));
        Assert.Equal(folded, trial.Store.Current.Sha256 == acknowledged.Sha256);
        Assert.Equal(request, trial.Coordinator.CurrentRemaining);
        if (folded) Assert.Equal(acknowledged.Sha256, trial.Coordinator.LatestAcknowledgedArtifact.Sha256);
        clock.Advance(30);
        var dispatch = await scope.ReserveProviderAsync(new(new string('a', 64), 1, 1, 1), default);
        Assert.NotNull(dispatch);
        Assert.Equal(elapsed + 30, trial.Store.Current.State.LineageCharges.ActiveElapsedMilliseconds.Observed);
        Assert.Equal(1, trial.Store.Dispatches);
        Assert.True(scope.BeginPhysicalDispatch(dispatch));
        Assert.False(scope.BeginPhysicalDispatch(dispatch));
    }

    [Theory]
    [InlineData("crash")]
    [InlineData("cancel")]
    [InlineData("elapsed")]
    [InlineData("write-failed")]
    [InlineData("unknown-write")]
    [InlineData("concurrent-winner")]
    public async Task Deferred_closure_preserves_conservative_recovery_and_never_grants_a_send_before_actual_written(string fault)
    {
        var clock = new SolusDeadlineClock();
        var trial = await SolusTrialAsync(clock: clock, targetMilliseconds: 1000,
            invocationMilliseconds: 1000, hostMilliseconds: 90,
            hostTimeouts: DocumentationScribeHostTimeouts.Create(terminalSubmissionMilliseconds: 300));
        var scope = trial.Coordinator.Scope;
        scope.BindRuntimeWindow(trial.Request, clock, clock.GetTimestamp());
        using var parent = new CancellationTokenSource();
        var permit = Assert.IsType<DocumentationScribeHostPermit>(
            await scope.BeginHostAsync(DocumentationScribeHostOperation.TerminalSubmission, parent.Token));
        permit.ObserveCancellation(default, parent.Token, parent.Token, parent.Token);
        Assert.True(permit.TryBeginOperation());
        var durable = trial.Store.Current;
        clock.Advance(10);
        Assert.True(await scope.CompleteHostAsync(permit, default));
        Assert.Equal(durable.Sha256, trial.Store.Current.Sha256);
        clock.Advance(fault == "elapsed" ? 90 : 30);
        if (fault == "crash")
        {
            var observed = await CampaignCheckpointAcceptance.AcceptCurrentAsync(trial.Store);
            var retired = CampaignStateReducer.RetireInterruptedProviderAttempt(observed.AcceptedCheckpoint!);
            Assert.Equal(CampaignTransitionKind.Applied, retired.Kind);
            Assert.True(retired.Artifact.State.LineageCharges.ActiveElapsedMilliseconds.TotalCharged >= 40);
            Assert.Null(retired.Artifact.State.ActiveReservation);
            Assert.Equal(durable.Sha256, trial.Store.Current.Sha256);
            return;
        }
        if (fault == "cancel") parent.Cancel();
        if (fault == "write-failed") trial.Store.DenyDispatch = true;
        if (fault == "unknown-write") trial.Store.FailDispatchReadback = true;
        if (fault == "concurrent-winner") trial.Store.ConcurrentDispatchWinner = true;
        var dispatch = await scope.ReserveProviderAsync(new(new string('a', 64), 1, 1, 1), parent.Token);
        Assert.Null(dispatch);
        Assert.Equal(0, scope.PhysicalProviderRequestCount);
        Assert.Equal(durable.Sha256, trial.Coordinator.LatestAcknowledgedArtifact.Sha256);
        Assert.Equal(fault is "unknown-write" or "concurrent-winner", trial.Store.Current.Sha256 != durable.Sha256);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelled_or_expired_original_host_permits_keep_the_real_closure_write(bool expired)
    {
        var clock = new SolusDeadlineClock();
        var trial = await SolusTrialAsync(clock: clock, targetMilliseconds: 1000,
            invocationMilliseconds: 1000, hostMilliseconds: 90,
            hostTimeouts: DocumentationScribeHostTimeouts.Create(terminalSubmissionMilliseconds: 300));
        var scope = trial.Coordinator.Scope;
        scope.BindRuntimeWindow(trial.Request, clock, clock.GetTimestamp());
        using var parent = new CancellationTokenSource();
        var permit = Assert.IsType<DocumentationScribeHostPermit>(
            await scope.BeginHostAsync(DocumentationScribeHostOperation.TerminalSubmission, parent.Token));
        permit.ObserveCancellation(default, parent.Token, parent.Token, parent.Token);
        Assert.True(permit.TryBeginOperation());
        var before = trial.Store.Current;
        clock.Advance(expired ? 300 : 10);
        if (!expired) parent.Cancel();
        Assert.True(await scope.CompleteHostAsync(permit, default));
        Assert.NotEqual(before.Sha256, trial.Store.Current.Sha256);
        Assert.Equal(expired ? 300 : 10, trial.Store.Current.State.LineageCharges.ActiveElapsedMilliseconds.Observed);
    }
}
