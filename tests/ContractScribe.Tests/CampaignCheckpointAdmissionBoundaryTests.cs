using ContractScribe.Cli;
using ContractScribe.Core;
using ContractScribe.GitHub.Coordination;

namespace ContractScribe.Tests;

public sealed partial class CampaignStateContractTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(0, false)]
    [InlineData(0, true)]
    public Task Remote_host_admission_fences_new_CAS_but_keeps_already_dispatched_readback(
        int stopAtCheckpointRead, bool caller) => RemoteHostAdmissionBoundaryAsync(stopAtCheckpointRead, caller, false);

    [Fact]
    public Task Remote_admission_expired_observation_retains_its_actual_stop_and_never_advances_Writer() =>
        RemoteHostAdmissionBoundaryAsync(0, false, true);

    private static async Task RemoteHostAdmissionBoundaryAsync(int stopAtCheckpointRead, bool caller, bool expiredObservation)
    {
        var clock = new SolusDeadlineClock();
        var trial = await SolusTrialAsync(clock: clock, targetMilliseconds: 10000, invocationMilliseconds: 10000);
        var remote = new GitHubCoordinationRefTests.CoordinationRemote();
        using var handler = new GitHubCoordinationRefTests.CheckpointInterceptHandler(remote);
        using var client = GitHubCoordinationRefTests.CheckpointClient(remote, trial.Scenario.InitialState.CampaignLineage, handler);
        await using var store = GitHubCoordinationStore.Create(client);
        Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
        Assert.True(await store.BindPlanAsync(CampaignPlanner.CreateRecoveryProjection(trial.Scenario.Plan), default));
        var initial = CampaignStateJson.CreateArtifact(trial.Scenario.InitialState);
        Assert.Equal(CampaignCheckpointWriteKind.Written, (await store.CreateIfAbsentAsync(initial.ExactUtf8Json.AsMemory(),
            initial.CheckpointRevision, initial.Sha256, default)).Kind);
        var allowance = new DocumentationScribeInvocationAllowance(trial.Coordinator.Scope.Allowance.Limits, clock);
        var admission = CampaignStateReducer.AdmitProviderInvocation(initial, trial.Scenario.ExecutionAuthority, "style.synthetic",
            trial.Scenario.StyleProjection, trial.Scenario.Input, trial.Scenario.Plan, trial.Scenario.Plan.WorkItems[0].WorkItemKey,
            trial.Request, CampaignStateFactory.CreateInvocationTargetAllowance(initial.State, new(100)), allowance);
        var accepted = await CampaignCheckpointAcceptance.AcceptAsync(store, admission);
        Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted, accepted.Kind);
        var authority = CampaignStateReducer.CreateProviderInvocationAuthority(accepted.AcceptedCheckpoint!, trial.Scenario.ExecutionAuthority,
            "style.synthetic", trial.Scenario.StyleProjection, trial.Scenario.Input, trial.Scenario.Plan, trial.Request);
        var coordinator = new CampaignScribeExecutionCoordinator(authority, allowance, store, default, null, clock.GetTimestamp(),
            DocumentationScribeHostTimeouts.Create(terminalSubmissionMilliseconds: 100), 1000);
        coordinator.Scope.BindRuntimeWindow(trial.Request, clock, clock.GetTimestamp());
        Assert.True(authority.TryBeginDispatch(out _));
        var predecessor = authority.AcceptedCheckpoint.Artifact;
        var predecessorHead = remote.CoordinationHead;
        var checkpointOid = GitHubCoordinationObjects.ObjectOid("blob", predecessor.ExactUtf8Json.AsSpan());
        var writes = remote.RefMutationAttempts;
        var checkpointReads = 0;
        var stopped = false;
        using var cancellation = new CancellationTokenSource();
        void Stop()
        {
            Assert.False(stopped);
            stopped = true;
            if (caller) cancellation.Cancel();
            else clock.Advance(expiredObservation ? 1200 : 200);
        }
        handler.BeforeRequest = request =>
        {
            if (stopAtCheckpointRead != 0 && request.Method == HttpMethod.Get
                && request.RequestUri!.AbsolutePath.EndsWith("/git/blobs/" + checkpointOid, StringComparison.Ordinal)
                && ++checkpointReads == stopAtCheckpointRead) Stop();
            return Task.CompletedTask;
        };
        handler.AfterResponse = (request, response) =>
        {
            if (stopAtCheckpointRead == 0 && !stopped && request.RequestUri!.AbsolutePath == "/graphql"
                && remote.RefMutationAttempts > writes) Stop();
            return Task.FromResult(response);
        };
        var permit = await coordinator.Scope.BeginHostAsync(DocumentationScribeHostOperation.TerminalSubmission, cancellation.Token);
        Assert.True(stopped);
        Assert.Equal(0, coordinator.Scope.Allowance.ToolCallCount);
        if (stopAtCheckpointRead != 0)
        {
            Assert.Null(permit);
            Assert.Equal(writes, remote.RefMutationAttempts);
            Assert.Equal(predecessorHead, remote.CoordinationHead);
            Assert.Equal(predecessor.Sha256, authority.AcceptedCheckpoint.Artifact.Sha256);
            Assert.Equal(CampaignCheckpointAcceptanceKind.Cancelled, coordinator.CheckpointFailure);
            Assert.Equal(!caller, coordinator.CheckpointCancelledByHostDeadline);
        }
        else if (expiredObservation)
        {
            Assert.Null(permit);
            Assert.Equal(writes + 1, remote.RefMutationAttempts);
            Assert.Equal(predecessor.Sha256, authority.AcceptedCheckpoint.Artifact.Sha256);
            var failure = Assert.IsType<GitHubCheckpointDiagnostic>(store.CheckpointDiagnostic);
            Assert.Equal(GitHubCheckpointStage.FinalRef, failure.Stage);
            Assert.Equal(GitHubCheckpointReason.Cancelled, failure.Reason);
            Assert.Equal(GitHubCheckpointCancellation.OperationToken, failure.Cancellation);
            Assert.NotEqual(CampaignCheckpointAcceptanceKind.Conflict, coordinator.CheckpointFailure);
        }
        else
        {
            Assert.NotNull(permit);
            Assert.Equal(writes + 1, remote.RefMutationAttempts);
            Assert.NotEqual(predecessorHead, remote.CoordinationHead);
            Assert.Equal(predecessor.CheckpointRevision + 1, authority.AcceptedCheckpoint.Artifact.CheckpointRevision);
            Assert.Null(coordinator.CheckpointFailure);
            // The accepted factual write remains available for mandatory
            // settlement. Runtime's original caller stop still forbids work.
            if (caller) Assert.True(cancellation.IsCancellationRequested);
            else
            {
                Assert.Equal(0, permit.RemainingMilliseconds);
                Assert.False(permit.TryBeginOperation());
            }
        }
    }
}
