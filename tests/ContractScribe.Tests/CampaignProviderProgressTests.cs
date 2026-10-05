using ContractScribe.Cli;
using ContractScribe.Core;

namespace ContractScribe.Tests;

public sealed partial class CampaignStateContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Readback_observer_recovers_retirement_only_from_the_exact_current_checkpoint(bool changed)
    {
        var scenario = UnlimitedScenario();
        var request = CreateScribeExchange(scenario.Plan.WorkItems[0], scenario: scenario).Request;
        var initial = CampaignStateJson.CreateArtifact(scenario.InitialState);
        var store = new TransitionCheckpointStore(initial);
        var admission = CampaignStateReducer.AdmitProviderInvocation(initial, scenario.ExecutionAuthority,
            "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan, scenario.Plan.WorkItems[0].WorkItemKey,
            request, CampaignStateFactory.CreateInvocationTargetAllowance(initial.State, new(100)), CampaignInvocationTestPolicy.Create(request.Limits));
        var admitted = await CampaignCheckpointAcceptance.AcceptAsync(store, admission);
        var restored = await CampaignCheckpointAcceptance.AcceptCurrentAsync(store);
        var unchanged = CampaignStateReducer.RefreshLifetimeCaps(restored.Artifact!, restored.AcceptedCheckpoint!,
            scenario.ExecutionAuthority, "style.synthetic", scenario.StyleProjection, "samples/Synthetic.csproj", scenario.Input, scenario.Plan);
        Assert.Equal(CampaignTransitionKind.Unchanged, unchanged.Kind);
        var observer = await CampaignCheckpointAcceptance.AcceptAsync(store, unchanged);
        Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted, observer.Kind);
        Assert.Equal(CampaignTransitionKind.Rejected, CampaignStateReducer.RetireInterruptedProviderAttempt(observer.AcceptedCheckpoint!).Kind);
        var state = admitted.Artifact!.State;
        var winner = changed ? CampaignStateJson.CreateArtifact(CampaignStateFactory.CreateValidated(state.ProductRevision,
            state.CampaignLineage, state.Snapshot, state.CheckpointRevision + 1, state.ConfiguredCeilings, state.LineageCharges,
            state.WorkItems, state.Batch, state.ActiveReservation, state.CandidateObservation, state.CumulativeOutcome,
            state.KnownCompletedOperations, state.TerminalOutcome, state.Predecessor)) : admitted.Artifact;
        var currentStore = new TransitionCheckpointStore(winner);
        var retirementAcceptance = await CampaignCommandRunner.AcceptProviderRetirementAsync(observer.AcceptedCheckpoint!, currentStore, default);
        if (changed)
        {
            Assert.Equal(CampaignCheckpointAcceptanceKind.Conflict, retirementAcceptance.Kind);
            Assert.Null(retirementAcceptance.AcceptedCheckpoint);
        }
        else
        {
            Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted, retirementAcceptance.Kind);
            Assert.Throws<ArgumentException>(() => CampaignStateReducer.CreateProviderInvocationAuthority(
                retirementAcceptance.AcceptedCheckpoint!, scenario.ExecutionAuthority, "style.synthetic", scenario.StyleProjection,
                scenario.Input, scenario.Plan, request));
            var retirement = CampaignStateReducer.RetireInterruptedProviderAttempt(retirementAcceptance.AcceptedCheckpoint!);
            Assert.Equal(CampaignTransitionKind.Applied, retirement.Kind);
            Assert.Null(retirement.Artifact.State.ActiveReservation);
            Assert.NotNull(retirement.Artifact.State.WorkItems[0].PausedProviderAttempt);
            Assert.Equal(1, retirement.Artifact.State.LineageCharges.OuterInvocations);
            Assert.Equal(0, retirement.Artifact.State.LineageCharges.ProviderRequests.TotalCharged);
            Assert.False(retirementAcceptance.AcceptedCheckpoint!.TryRetireReservation());
        }
        var preserved = await currentStore.ReadAsync(default);
        Assert.True(winner.ExactUtf8Json.AsSpan().SequenceEqual(preserved.ExactUtf8Json.AsSpan()));
    }

    [Fact]
    public async Task Accepted_host_and_dispatch_intervals_share_one_boundary_without_elapsed_overlap()
    {
        var scenario = UnlimitedScenario();
        var request = CreateScribeExchange(scenario.Plan.WorkItems[0], scenario: scenario).Request;
        var initial = CampaignStateJson.CreateArtifact(scenario.InitialState);
        var store = new TransitionCheckpointStore(initial);
        var clock = new TickingProgressClock();
        var allowance = CampaignInvocationTestPolicy.Create(request.Limits, clock);
        var admission = CampaignStateReducer.AdmitProviderInvocation(initial, scenario.ExecutionAuthority,
            "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan, scenario.Plan.WorkItems[0].WorkItemKey,
            request, CampaignStateFactory.CreateInvocationTargetAllowance(initial.State, new(100)), allowance);
        var accepted = await CampaignCheckpointAcceptance.AcceptAsync(store, admission);
        var parent = CampaignStateReducer.CreateProviderInvocationAuthority(accepted.AcceptedCheckpoint!, scenario.ExecutionAuthority,
            "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan, request);
        var coordinator = new CampaignScribeExecutionCoordinator(parent, allowance, store, default, null, 0);
        Assert.True(parent.TryBeginDispatch(out _));
        clock.TickOnRead = true;
        var permit = await coordinator.Scope.ReserveProviderAsync(new(Sha256("non-overlapping elapsed"), 1, 1,
            request.Limits.MaximumOutputTokens), default);
        Assert.NotNull(permit);
        Assert.True(coordinator.Scope.BeginPhysicalDispatch(permit));
        var settlementBoundary = clock.Timestamp + 1;
        Assert.True(await coordinator.Scope.SettleProviderAsync(permit,
            new(DocumentationScribeDispatchDisposition.Success, new(0, 0, 0, 0)), default));
        Assert.Equal(settlementBoundary, parent.AcceptedCheckpoint.Artifact.State.LineageCharges.ActiveElapsedMilliseconds.Observed);
        Assert.Equal(0, parent.AcceptedCheckpoint.Artifact.State.LineageCharges.ActiveElapsedMilliseconds.ConservativeUnobserved);
    }

    private sealed class TickingProgressClock : TimeProvider
    {
        internal bool TickOnRead { get; set; }
        internal long Timestamp { get; private set; }
        public override long TimestampFrequency => 1_000;
        public override long GetTimestamp() => TickOnRead ? ++Timestamp : Timestamp;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Accepted_failure_and_wait_progress_survive_takeover_without_a_new_outer_attempt(bool settleWait, bool freshContext)
    {
        var scenario = UnlimitedScenario();
        var request = CreateScribeExchange(scenario.Plan.WorkItems[0], scenario: scenario).Request;
        var initial = CampaignStateJson.CreateArtifact(scenario.InitialState);
        var store = new TransitionCheckpointStore(initial);
        var clock = new C4CampaignClock();
        var allowance = CampaignInvocationTestPolicy.Create(request.Limits, clock);
        var admitted = CampaignStateReducer.AdmitProviderInvocation(initial, scenario.ExecutionAuthority,
            "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan, scenario.Plan.WorkItems[0].WorkItemKey,
            request, CampaignStateFactory.CreateInvocationTargetAllowance(initial.State, new(100)), allowance);
        var accepted = await CampaignCheckpointAcceptance.AcceptAsync(store, admitted);
        Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted, accepted.Kind);
        var parent = CampaignStateReducer.CreateProviderInvocationAuthority(accepted.AcceptedCheckpoint!, scenario.ExecutionAuthority,
            "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan, request);
        var coordinator = new CampaignScribeExecutionCoordinator(parent, allowance, store, default, null, clock.GetTimestamp());
        Assert.True(parent.TryBeginDispatch(out _));
        var permit = await coordinator.Scope.ReserveProviderAsync(new(Sha256("accepted failure request"), 1, 1,
            request.Limits.MaximumOutputTokens), default);
        Assert.NotNull(permit); Assert.True(coordinator.Scope.BeginPhysicalDispatch(permit));
        Assert.True(await coordinator.Scope.SettleProviderAsync(permit,
            new(DocumentationScribeDispatchDisposition.RetryableFailure, new(0, 0, 0, 0), 100), default));
        if (settleWait)
        {
            var wait = await coordinator.Scope.BeginHostAsync(DocumentationScribeHostOperation.RetryWait, default);
            Assert.NotNull(wait); Assert.True(wait.TryBeginOperation());
            clock.Advance(40);
            Assert.True(await coordinator.Scope.CompleteHostAsync(wait, default));
        }
        var settled = parent.AcceptedCheckpoint.Artifact;
        var claim = Assert.IsType<CampaignProviderReservation>(settled.State.ActiveReservation);
        Assert.Equal(1, claim.RetryProgress.RetryableFailureCount);
        Assert.Equal(settleWait ? 60 : 100, claim.RetryProgress.PendingRetryAfterMilliseconds);
        Assert.Equal(1, settled.State.LineageCharges.ProviderRequests.Observed);
        var current = await CampaignCheckpointAcceptance.AcceptCurrentAsync(store);
        var retirement = CampaignStateReducer.RetireInterruptedProviderAttempt(current.AcceptedCheckpoint!);
        var retired = await CampaignCheckpointAcceptance.AcceptAsync(store, retirement);
        Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted, retired.Kind);
        var paused = retired.Artifact!.State.WorkItems[0].PausedProviderAttempt;
        Assert.NotNull(paused); Assert.Equal(claim.AttemptId, paused.AttemptId);
        Assert.Equal(claim.RetryProgress, paused.RetryProgress);
        var freshRequest = freshContext ? CreateFreshContextExchange(scenario.Plan.WorkItems[0], scenario: scenario).Request : request;
        if (freshContext) Assert.NotEqual(request.ArtifactSha256, freshRequest.ArtifactSha256);
        var foreignRequest = CreateFreshContextExchange(scenario.Plan.WorkItems[0],
            inputIdentity: "samples/Alternate.csproj", scenario: scenario).Request;
        var foreign = CampaignStateReducer.RetryProviderInvocation(retired.Artifact, retired.AcceptedCheckpoint,
            scenario.ExecutionAuthority, "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan,
            scenario.Plan.WorkItems[0].WorkItemKey, foreignRequest,
            CampaignStateFactory.CreateInvocationTargetAllowance(retired.Artifact.State, new(0)),
            CampaignInvocationTestPolicy.Create(foreignRequest.Limits));
        Assert.Equal(CampaignTransitionFailure.InvalidAuthority, foreign.Failure);
        Assert.Equal(retired.Artifact.ExactUtf8Json, foreign.Artifact.ExactUtf8Json);
        foreach (var resource in new[] { "requests", "calls", "cached", "uncached", "output", "elapsed", "request-output", "request-elapsed", "connect", "response-width" })
        {
            var stopped = DocumentationScribeInvocationLimits.Create(
                maximumProviderRequests: resource == "requests" ? 0 : 8,
                maximumToolCalls: resource == "calls" ? 0 : 16,
                maximumCachedInputTokens: resource == "cached" ? 0 : 32768,
                maximumUncachedInputTokens: resource == "uncached" ? 0 : 32768,
                maximumOutputTokens: resource == "output" ? 0 : 8192,
                maximumElapsedMilliseconds: resource == "elapsed" ? 0 : 120000,
                maximumRequestOutputTokens: resource == "request-output" ? 0 : 8192,
                maximumRequestElapsedMilliseconds: resource == "request-elapsed" ? 0 : 120000,
                maximumConnectMilliseconds: resource == "connect" ? 0 : 1000,
                maximumToolCallsPerResponse: resource == "response-width" ? 0 : 16);
            var noQuota = CampaignStateReducer.RetryProviderInvocation(retired.Artifact, retired.AcceptedCheckpoint,
                scenario.ExecutionAuthority, "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan,
                scenario.Plan.WorkItems[0].WorkItemKey, freshRequest,
                CampaignStateFactory.CreateInvocationTargetAllowance(retired.Artifact.State, new(0)),
                new DocumentationScribeInvocationAllowance(stopped));
            Assert.Equal(CampaignTransitionFailure.InvocationBudgetExhausted, noQuota.Failure);
            Assert.Equal(retired.Artifact.ExactUtf8Json, noQuota.Artifact.ExactUtf8Json);
            Assert.Equal(paused, noQuota.Artifact.State.WorkItems[0].PausedProviderAttempt);
            Assert.Null(noQuota.Artifact.State.TerminalOutcome);
        }
        var freshAllowance = CampaignInvocationTestPolicy.Create(freshRequest.Limits, new C4CampaignClock());
        var resume = CampaignStateReducer.RetryProviderInvocation(retired.Artifact, retired.AcceptedCheckpoint,
            scenario.ExecutionAuthority, "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan,
            scenario.Plan.WorkItems[0].WorkItemKey, freshRequest,
            CampaignStateFactory.CreateInvocationTargetAllowance(retired.Artifact.State, new(0)), freshAllowance);
        var resumed = await CampaignCheckpointAcceptance.AcceptAsync(store, resume);
        Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted, resumed.Kind);
        var resumedClaim = Assert.IsType<CampaignProviderReservation>(resumed.Artifact!.State.ActiveReservation);
        Assert.Equal(claim.AttemptId, resumedClaim.AttemptId);
        Assert.Equal(freshRequest.ArtifactSha256, resumedClaim.ScribeRequestSha256);
        Assert.Equal(claim.RetryProgress, resumedClaim.RetryProgress);
        Assert.Equal(1, resumedClaim.RestoredRetryableProviderFailures);
        Assert.Equal(0, resumedClaim.CurrentExecutionSettledProviderRequests);
        Assert.Equal(1, resumed.Artifact.State.LineageCharges.OuterInvocations);
        Assert.Equal(1, resumed.Artifact.State.WorkItems[0].OuterAttemptCount);
        Assert.Null(await coordinator.Scope.ReserveProviderAsync(new(Sha256("old late send"), 2, 2,
            request.Limits.MaximumOutputTokens), default));
        Assert.True(coordinator.Conflict);
        Assert.Equal(1, allowance.ProviderRequestCount);
        Assert.False(parent.TryBeginDispatch(out _));
    }

    [Fact]
    public async Task Accepted_exhausted_failures_close_after_crash_with_zero_new_physical_calls()
    {
        var template = ReadScribeRequest(root => root["limits"]!["maximumAttempts"] = 2);
        var scenario = CreateProposalScenario(targetLimit: 1, maximumAttemptsPerTarget: 1,
            scribeRequestTemplate: template, maximumElapsedMilliseconds: null);
        var request = CreateScribeExchange(scenario.Plan.WorkItems[0], requestMutation: root => root["limits"]!["maximumAttempts"] = 2, scenario: scenario).Request;
        var initial = CampaignStateJson.CreateArtifact(scenario.InitialState);
        var store = new TransitionCheckpointStore(initial);
        var allowance = CampaignInvocationTestPolicy.Create(request.Limits, new C4CampaignClock());
        var admission = CampaignStateReducer.AdmitProviderInvocation(initial, scenario.ExecutionAuthority,
            "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan, scenario.Plan.WorkItems[0].WorkItemKey,
            request, CampaignStateFactory.CreateInvocationTargetAllowance(initial.State, new(100)), allowance);
        Assert.Equal(CampaignTransitionKind.Applied, admission.Kind);
        var accepted = await CampaignCheckpointAcceptance.AcceptAsync(store, admission);
        Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted, accepted.Kind);
        var parent = CampaignStateReducer.CreateProviderInvocationAuthority(accepted.AcceptedCheckpoint!, scenario.ExecutionAuthority,
            "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan, request);
        var coordinator = new CampaignScribeExecutionCoordinator(parent, allowance, store, default, null, allowance.Clock.GetTimestamp());
        Assert.True(parent.TryBeginDispatch(out _));
        for (var physical = 1; physical <= 2; physical++)
        {
            var permit = await coordinator.Scope.ReserveProviderAsync(new(Sha256("failed physical " + physical), physical,
                physical, request.Limits.MaximumOutputTokens), default);
            Assert.NotNull(permit); Assert.True(coordinator.Scope.BeginPhysicalDispatch(permit));
            Assert.True(await coordinator.Scope.SettleProviderAsync(permit,
                new(DocumentationScribeDispatchDisposition.RetryableFailure, new(0, 0, 0, 0)), default));
        }
        var current = await CampaignCheckpointAcceptance.AcceptCurrentAsync(store);
        var retire = CampaignStateReducer.RetireInterruptedProviderAttempt(current.AcceptedCheckpoint!);
        var retired = await CampaignCheckpointAcceptance.AcceptAsync(store, retire);
        Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted, retired.Kind);
        var recovery = await CampaignCheckpointAcceptance.AcceptCurrentAsync(store);
        var closed = CampaignStateReducer.CompleteRecoveredProviderFailure(recovery.AcceptedCheckpoint!, scenario.Plan.WorkItems[0].WorkItemKey);
        Assert.Equal(CampaignTransitionKind.Applied, closed.Kind);
        var record = closed.Artifact.State.WorkItems[0];
        Assert.Equal(CampaignScribeCompletionSource.RecoveredDispatchFailure, record.ClosedOutcome!.ScribeCompletionSource);
        Assert.NotNull(record.ClosedOutcome.AcceptedDispatchFailureCommitmentSha256);
        Assert.Null(record.ClosedOutcome.ScribeResultCommitmentSha256);
        Assert.Equal(CampaignAttemptDisposition.SuppressedAtAttemptLimit, record.AttemptDisposition);
        Assert.Null(record.PausedProviderAttempt);
        Assert.Equal(2, closed.Artifact.State.LineageCharges.ProviderRequests.Observed);
        Assert.Equal(2, allowance.ProviderRequestCount);
        Assert.Equal(1, closed.Artifact.State.LineageCharges.OuterInvocations);
        Assert.Equal(CampaignTransitionFailure.InvalidAuthority,
            CampaignStateReducer.CompleteRecoveredProviderFailure(recovery.AcceptedCheckpoint!, record.WorkItemKey).Failure);
    }
}
