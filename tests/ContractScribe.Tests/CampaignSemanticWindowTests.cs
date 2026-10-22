using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Core;

namespace ContractScribe.Tests;

public sealed partial class CampaignStateContractTests
{
    [Theory]
    [InlineData(30000, false)]
    [InlineData(30001, true)]
    [InlineData(70000, true)]
    [InlineData(70001, false)]
    public async Task Semantic_scalar_envelope_is_coherent_in_canonical_reader_and_published_schema(int elapsed, bool valid)
    {
        var trial = await SolusTrialAsync();
        var transition = CampaignStateReducer.AdvanceHostOperation(trial.Authority,
            DocumentationScribeHostOperation.SemanticTool, 0, 70000, completing: false);
        Assert.Equal(CampaignTransitionKind.Applied, transition.Kind);
        using var original = JsonDocument.Parse(transition.Artifact.ExactUtf8Json.AsMemory());
        var originalSchema = EvaluateCampaignSchema(original.RootElement);
        Assert.True(originalSchema.IsValid, DescribeSchemaFailures(originalSchema));
        var originalParsed = CampaignStateJson.Parse(transition.Artifact.ExactUtf8Json.AsMemory());
        Assert.True(originalParsed.IsValid);
        Assert.Equal(transition.Artifact.ExactUtf8Json.ToArray(),
            CampaignStateJson.CreateArtifact(originalParsed.Artifact!.State).ExactUtf8Json.ToArray());
        var node = JsonNode.Parse(transition.Artifact.ExactUtf8Json.AsSpan())!;
        node["activeReservation"]!["exposure"]!["elapsedMilliseconds"] = elapsed;
        var bytes = Encoding.UTF8.GetBytes(node.ToJsonString() + "\n");
        var parsed = CampaignStateJson.Parse(bytes);
        var schema = EvaluateCampaignSchema(JsonSerializer.SerializeToElement(node));
        Assert.Equal(valid, parsed.IsValid);
        Assert.Equal(valid, schema.IsValid);
        if (!valid) Assert.Equal(CampaignStateValidationCode.InvalidBound, parsed.FailureCode);
    }

    [Theory]
    [InlineData(70000, 13164, 10000)]
    [InlineData(50000, 13164, 6836)]
    [InlineData(40000, 0, 10000)]
    [InlineData(30001, 0, 1)]
    [InlineData(30000, 0, 0)]
    [InlineData(70000, 29999, 10000)]
    [InlineData(70000, 30000, 0)]
    [InlineData(50000, 20000, 0)]
    [InlineData(40000, 10000, 0)]
    [InlineData(30001, 1, 0)]
    public async Task Semantic_envelope_reserves_closure_before_admission_and_never_borrows_it_for_query(
        int envelope, int authorizationElapsed, int expectedQuery)
    {
        var clock = new SolusDeadlineClock();
        var trial = await SolusTrialAsync(clock: clock, targetMilliseconds: 120000, invocationMilliseconds: envelope);
        trial.Coordinator.Scope.BindRuntimeWindow(trial.Request, clock, clock.GetTimestamp());
        trial.Store.DelayedReadbackPhase = CampaignProviderHostPhase.SemanticTool;
        trial.Store.DelayedReadback = () => clock.Advance(authorizationElapsed);
        var permit = await trial.Coordinator.Scope.BeginHostAsync(DocumentationScribeHostOperation.SemanticTool, default);
        Assert.False(trial.Coordinator.Conflict);
        if (expectedQuery == 0)
        {
            Assert.Null(permit);
            Assert.Equal(0, trial.Coordinator.Scope.Allowance.ToolCallCount);
            Assert.Equal(envelope <= 30000, trial.Coordinator.Scope.SemanticHeadroomReached);
            Assert.Equal(envelope > 30000, trial.Coordinator.Scope.SemanticAuthorizationTimedOut);
            Assert.Equal(envelope > 30000, trial.Authority.ProgressStopped);
            if (trial.Authority.ProgressStopped)
            {
                var rejected = CampaignStateReducer.AdvanceHostOperation(trial.Authority,
                    DocumentationScribeHostOperation.RegisteredTool, 0, 1000, completing: false);
                Assert.Equal(CampaignTransitionFailure.InvalidAuthority, rejected.Failure);
            }
            return;
        }
        Assert.NotNull(permit);
        Assert.Equal(envelope, Assert.IsType<CampaignProviderReservation>(trial.Store.Current.State.ActiveReservation).Exposure.ElapsedMilliseconds);
        Assert.Equal(expectedQuery, permit.RemainingMilliseconds);
        Assert.True(permit.TryBeginOperation());
        Assert.False(permit.TryBeginOperation());
        Assert.Equal(1, trial.Coordinator.Scope.Allowance.ToolCallCount);
        clock.Advance(expectedQuery - 1);
        Assert.Equal(1, permit.RemainingMilliseconds);
        Assert.True(await trial.Coordinator.Scope.CompleteHostAsync(permit, default, new SemanticReleaseValidator(_ => true)));
        Assert.True(permit.ResultReleaseAccepted);
        Assert.Equal(authorizationElapsed + expectedQuery - 1,
            trial.Store.Current.State.LineageCharges.ActiveElapsedMilliseconds.Observed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Semantic_query_deadline_is_fixed_at_actual_acceptance_and_never_renewed_at_callback(bool lateCallback)
    {
        var clock = new SolusDeadlineClock();
        var trial = await SolusTrialAsync(clock: clock);
        trial.Coordinator.Scope.BindRuntimeWindow(trial.Request, clock, clock.GetTimestamp());
        var permit = await trial.Coordinator.Scope.BeginHostAsync(DocumentationScribeHostOperation.SemanticTool, default);
        Assert.NotNull(permit);
        if (!lateCallback) Assert.True(permit.TryBeginOperation());
        clock.Advance(10000);
        Assert.Equal(0, permit.RemainingMilliseconds);
        Assert.False(permit.TryBeginOperation());
        Assert.Equal(lateCallback ? 0 : 1, trial.Coordinator.Scope.Allowance.ToolCallCount);
        Assert.True(await trial.Coordinator.Scope.CompleteHostAsync(permit, default));
    }

    [Theory]
    [InlineData(29999, true)]
    [InlineData(30000, false)]
    [InlineData(30001, false)]
    public async Task Semantic_closure_includes_result_release_validation_and_its_tail_is_charged_once(int closingElapsed, bool released)
    {
        var clock = new SolusDeadlineClock();
        var trial = await SolusTrialAsync(clock: clock);
        trial.Coordinator.Scope.BindRuntimeWindow(trial.Request, clock, clock.GetTimestamp());
        var permit = await trial.Coordinator.Scope.BeginHostAsync(DocumentationScribeHostOperation.SemanticTool, default);
        Assert.NotNull(permit);
        Assert.True(permit.TryBeginOperation());
        clock.Advance(100);
        var validationCalls = 0;
        Assert.True(await trial.Coordinator.Scope.CompleteHostAsync(permit, default, new SemanticReleaseValidator(_ =>
        { validationCalls++; clock.Advance(closingElapsed); return true; })));
        Assert.Equal(1, validationCalls);
        Assert.Equal(released, permit.ResultReleaseAccepted);
        Assert.Equal(!released, permit.ResultReleaseDeadlineReached);
        var next = await trial.Coordinator.Scope.BeginHostAsync(DocumentationScribeHostOperation.RegisteredTool, default);
        Assert.NotNull(next);
        Assert.Equal(100 + closingElapsed, trial.Store.Current.State.LineageCharges.ActiveElapsedMilliseconds.Observed);
        Assert.True(await trial.Coordinator.Scope.CompleteHostAsync(next, default));
        Assert.Equal(100 + closingElapsed, trial.Store.Current.State.LineageCharges.ActiveElapsedMilliseconds.Observed);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("foreign-store")]
    [InlineData("foreign-artifact")]
    [InlineData("missing-lease")]
    [InlineData("missing-owner-token")]
    [InlineData("owner-lost")]
    public async Task Semantic_remote_query_requires_exact_readback_and_a_fixed_live_lease(string fault)
    {
        var clock = new SolusDeadlineClock();
        var trial = await SolusTrialAsync(clock: clock);
        var transition = CampaignStateReducer.AdvanceHostOperation(trial.Authority,
            DocumentationScribeHostOperation.SemanticTool, 0, 70000, completing: false);
        Assert.Equal(CampaignTransitionKind.Applied, transition.Kind);
        var store = new RemoteSemanticFenceStore();
        using var window = new CampaignSemanticCheckpointWindow(store, transition.Artifact, clock, clock.GetTimestamp(), 70000);
        using var owner = new CancellationTokenSource();
        var actual = fault == "foreign-artifact" ? transition.Predecessor : transition.Artifact;
        var readback = CampaignCheckpointReadResult.Found(actual.ExactUtf8Json.AsSpan(), actual.CheckpointRevision, actual.Sha256);
        window.ObserveRemoteReadback(fault == "foreign-store" ? new object() : store, readback,
            fault == "missing-lease" ? 0 : 5000, fault == "missing-owner-token" ? default : owner.Token, () => true);
        if (fault == "owner-lost") owner.Cancel();
        Assert.Equal(fault == "valid", window.TryIssue(transition.Artifact, 120000, 120000, out var admitted));
        if (fault != "valid") { Assert.False(window.TryBeginQuery()); return; }
        Assert.Equal(5000, admitted);
        clock.Advance(4000);
        // A subsequent confirmed same-owner renewal cannot renew an issued Dq.
        window.ObserveRemoteReadback(store, readback, 300000, owner.Token, () => true);
        Assert.Equal(1000, window.RemainingQueryMilliseconds);
        Assert.True(window.TryBeginQuery());
        Assert.False(window.TryBeginQuery());
        owner.Cancel();
        Assert.True(window.OwnerLost.IsCancellationRequested);
        Assert.False(window.OwnerCurrent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restart_retires_the_actual_semantic_or_completion_successor_exposure_once(bool completionCommitted)
    {
        var clock = new SolusDeadlineClock();
        var trial = await SolusTrialAsync(clock: clock);
        trial.Coordinator.Scope.BindRuntimeWindow(trial.Request, clock, clock.GetTimestamp());
        var permit = await trial.Coordinator.Scope.BeginHostAsync(DocumentationScribeHostOperation.SemanticTool, default);
        Assert.NotNull(permit);
        Assert.True(permit.TryBeginOperation());
        clock.Advance(100);
        if (completionCommitted) Assert.True(await trial.Coordinator.Scope.CompleteHostAsync(permit, default, new SemanticReleaseValidator(_ => true)));
        var actual = trial.Store.Current;
        var exposure = Assert.IsType<CampaignProviderReservation>(actual.State.ActiveReservation).Exposure.ElapsedMilliseconds;
        var observed = await CampaignCheckpointAcceptance.AcceptCurrentAsync(trial.Store);
        Assert.Throws<ArgumentException>(() => CampaignStateReducer.CreateProviderInvocationAuthority(
            observed.AcceptedCheckpoint!, trial.Scenario.ExecutionAuthority, "style.synthetic", trial.Scenario.StyleProjection,
            trial.Scenario.Input, trial.Scenario.Plan, trial.Request));
        var retired = CampaignStateReducer.RetireInterruptedProviderAttempt(observed.AcceptedCheckpoint!);
        Assert.Equal(CampaignTransitionKind.Applied, retired.Kind);
        Assert.Null(retired.Artifact.State.ActiveReservation);
        Assert.Equal(completionCommitted ? 100 : 0, retired.Artifact.State.LineageCharges.ActiveElapsedMilliseconds.Observed);
        Assert.Equal(exposure, retired.Artifact.State.LineageCharges.ActiveElapsedMilliseconds.ConservativeUnobserved);
        Assert.Equal(CampaignTransitionFailure.InvalidAuthority,
            CampaignStateReducer.RetireInterruptedProviderAttempt(observed.AcceptedCheckpoint!).Failure);
        Assert.Equal(actual.ExactUtf8Json, trial.Store.Current.ExactUtf8Json);
        permit.SemanticWindow?.Dispose();
    }

    private sealed class SemanticReleaseValidator(Func<CancellationToken, bool> validate) : IDocumentationScribeToolResultReleaseValidator
    {
        public bool ValidateResultRelease(CancellationToken cancellationToken) => validate(cancellationToken);
    }

    [Fact]
    public async Task Semantic_delayed_start_rechecks_original_configuration_guard_before_any_debit()
    {
        var valid = true;
        var clock = new SolusDeadlineClock();
        var trial = await SolusTrialAsync(clock: clock, dispatchGuard: () => valid);
        trial.Coordinator.Scope.BindRuntimeWindow(trial.Request, clock, clock.GetTimestamp());
        var permit = await trial.Coordinator.Scope.BeginHostAsync(DocumentationScribeHostOperation.SemanticTool, default);
        Assert.NotNull(permit);
        Assert.Equal(10000, permit.RemainingMilliseconds);
        valid = false;
        Assert.False(permit.TryBeginOperation());
        Assert.Equal(0, trial.Coordinator.Scope.Allowance.ToolCallCount);
        Assert.True(await trial.Coordinator.Scope.CompleteHostAsync(permit, default, new SemanticReleaseValidator(_ => true)));
        Assert.False(permit.ResultReleaseAccepted);
    }

    [Fact]
    public async Task Uncooperative_late_semantic_acceptance_cannot_advance_authority_after_closure_or_takeover()
    {
        var clock = new SolusDeadlineClock();
        var trial = await SolusTrialAsync(clock: clock);
        trial.Coordinator.Scope.BindRuntimeWindow(trial.Request, clock, clock.GetTimestamp());
        var original = trial.Authority.AcceptedCheckpoint.Artifact;
        trial.Store.HoldSemanticReadback = true;
        var admission = trial.Coordinator.Scope.BeginHostAsync(DocumentationScribeHostOperation.SemanticTool, default).AsTask();
        await trial.Store.SemanticReadbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var window = Assert.IsType<CampaignSemanticCheckpointWindow>(trial.Store.SemanticWindowForTest);
        clock.Advance(30000);
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!window.IsClosingObservation) await Task.Delay(1, wait.Token);
        clock.Advance(30000);
        Assert.Null(await admission.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(trial.Coordinator.Conflict);
        Assert.True(trial.Coordinator.Scope.Failed);
        Assert.False(trial.Coordinator.Scope.SemanticAuthorizationClosed);
        Assert.False(window.ObservationOpen);
        Assert.Equal(original.Sha256, trial.Authority.AcceptedCheckpoint.Artifact.Sha256);
        Assert.Equal(0, trial.Coordinator.Scope.Allowance.ToolCallCount);

        var current = await CampaignCheckpointAcceptance.AcceptCurrentAsync(trial.Store);
        var retirement = CampaignStateReducer.RetireInterruptedProviderAttempt(current.AcceptedCheckpoint!);
        var retired = await CampaignCheckpointAcceptance.AcceptAsync(trial.Store, retirement);
        Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted, retired.Kind);
        Assert.Equal(70000, retired.Artifact!.State.LineageCharges.ActiveElapsedMilliseconds.ConservativeUnobserved);
        trial.Store.ReleaseSemanticReadback.TrySetResult();
        await trial.Store.SemanticReadbackFinished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await trial.Coordinator.LateObservation.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(await trial.Coordinator.Scope.BeginHostAsync(DocumentationScribeHostOperation.SemanticTool, default));
        Assert.Equal(original.Sha256, trial.Authority.AcceptedCheckpoint.Artifact.Sha256);
        Assert.Equal(retired.Artifact.Sha256, trial.Store.Current.Sha256);
        Assert.Equal(0, trial.Coordinator.Scope.Allowance.ToolCallCount);
    }

    [Fact]
    public async Task Semantic_result_without_a_bound_release_validator_never_reaches_a_second_model_request()
    {
        var trial = await SolusTrialAsync();
        var port = new MissingReleaseBoundaryPort();
        var exchange = ReviewBoundaryToolExchange();
        var session = trial.Session(exchange, ReviewBoundaryRegistry(trial.Request, port));
        var result = await session.RunAsync();
        Assert.Equal(1, port.Calls);
        Assert.Equal(1, result.RunEnvelope.ToolCallCount);
        Assert.Single(exchange.Requests);
        Assert.Empty(session.State.CompletedToolExchanges);
        Assert.Equal(DocumentationScribeFailureCode.ToolProtocol, Assert.IsType<DocumentationScribeFailureTerminal>(result.Stop).Code);
        await trial.CompleteAsync(result);
        Assert.Null(trial.Store.Current.State.ActiveReservation);
    }

    private sealed class MissingReleaseBoundaryPort : IDocumentationScribeToolPort<ReviewBoundaryRequest, ReviewBoundaryResult>
    {
        internal int Calls { get; private set; }
        public ValueTask<ReviewBoundaryResult> InvokeAsync(ReviewBoundaryRequest request, CancellationToken cancellationToken)
        { Calls++; return ValueTask.FromResult(new ReviewBoundaryResult(DocumentationScribeToolOutcome.Complete)); }
    }

    [Fact]
    public async Task Semantic_completion_at_the_closed_observation_boundary_never_advances_the_old_authority()
    {
        var clock = new SolusDeadlineClock();
        var trial = await SolusTrialAsync(clock: clock);
        trial.Coordinator.Scope.BindRuntimeWindow(trial.Request, clock, clock.GetTimestamp());
        var permit = await trial.Coordinator.Scope.BeginHostAsync(DocumentationScribeHostOperation.SemanticTool, default);
        Assert.NotNull(permit);
        Assert.True(permit.TryBeginOperation());
        var original = trial.Authority.AcceptedCheckpoint.Artifact;
        clock.Advance(100);
        trial.Store.HostCompletionReadback = phase =>
        { if (phase == CampaignProviderHostPhase.SemanticTool) clock.Advance(30000); };
        Assert.False(await trial.Coordinator.Scope.CompleteHostAsync(permit, default, new SemanticReleaseValidator(_ => true)));
        await trial.Coordinator.LateObservation.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(trial.Coordinator.Scope.Failed);
        Assert.True(trial.Coordinator.Conflict);
        Assert.Equal(original.Sha256, trial.Authority.AcceptedCheckpoint.Artifact.Sha256);
        Assert.NotEqual(original.Sha256, trial.Store.Current.Sha256);
        Assert.Equal(100, trial.Store.Current.State.LineageCharges.ActiveElapsedMilliseconds.Observed);
        var exposure = Assert.IsType<CampaignProviderReservation>(trial.Store.Current.State.ActiveReservation).Exposure.ElapsedMilliseconds;
        var observed = await CampaignCheckpointAcceptance.AcceptCurrentAsync(trial.Store);
        var retired = CampaignStateReducer.RetireInterruptedProviderAttempt(observed.AcceptedCheckpoint!);
        Assert.Equal(CampaignTransitionKind.Applied, retired.Kind);
        Assert.Equal(100, retired.Artifact.State.LineageCharges.ActiveElapsedMilliseconds.Observed);
        Assert.Equal(exposure, retired.Artifact.State.LineageCharges.ActiveElapsedMilliseconds.ConservativeUnobserved);
    }

    private sealed class RemoteSemanticFenceStore : ICampaignSemanticCheckpointStore
    {
        public object SemanticCheckpointIdentity => this;
        public CampaignSemanticCheckpointStoreKind SemanticCheckpointKind => CampaignSemanticCheckpointStoreKind.RemoteLeased;
        public IDisposable EnterSemanticCheckpointWindow(CampaignSemanticCheckpointWindow window) => throw new NotSupportedException();
        public ValueTask<CampaignCheckpointReadResult> ReadAsync(CancellationToken token) => throw new NotSupportedException();
        public ValueTask<CampaignCheckpointWriteResult> CreateIfAbsentAsync(ReadOnlyMemory<byte> bytes, long revision, string sha, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<CampaignCheckpointWriteResult> ReplaceIfCurrentAsync(long expected, string expectedSha, ReadOnlyMemory<byte> bytes, long revision, string sha, CancellationToken token) => throw new NotSupportedException();
    }
}
