using ContractScribe.Cli;
using ContractScribe.Core;
using ContractScribe.GitHub.Coordination;

namespace ContractScribe.Tests;

public sealed partial class CampaignStateContractTests
{
    [Theory]
    [InlineData(GitHubCoordinationRefTests.LostMutation.Blob)]
    [InlineData(GitHubCoordinationRefTests.LostMutation.Ref)]
    public async Task Semantic_GitHub_lost_response_recovery_uses_the_original_absolute_observation_cutoff(
        GitHubCoordinationRefTests.LostMutation loss)
    {
        var clock = new SolusDeadlineClock();
        var trial = await SolusTrialAsync(clock: clock);
        var remote = new GitHubCoordinationRefTests.CoordinationRemote();
        using var handler = new GitHubCoordinationRefTests.CheckpointInterceptHandler(remote);
        using var client = GitHubCoordinationRefTests.CheckpointClient(remote, trial.Scenario.InitialState.CampaignLineage, handler);
        await using var store = GitHubCoordinationStore.Create(client);
        Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
        Assert.True(await store.BindPlanAsync(CampaignPlanner.CreateRecoveryProjection(trial.Scenario.Plan), default));
        var initial = CampaignStateJson.CreateArtifact(trial.Scenario.InitialState);
        Assert.Equal(CampaignCheckpointWriteKind.Written, (await store.CreateIfAbsentAsync(
            initial.ExactUtf8Json.AsMemory(), initial.CheckpointRevision, initial.Sha256, default)).Kind);
        var allowance = new DocumentationScribeInvocationAllowance(trial.Coordinator.Scope.Allowance.Limits, clock);
        var transition = CampaignStateReducer.AdmitProviderInvocation(initial, trial.Scenario.ExecutionAuthority,
            "style.synthetic", trial.Scenario.StyleProjection, trial.Scenario.Input, trial.Scenario.Plan,
            trial.Scenario.Plan.WorkItems[0].WorkItemKey, trial.Request,
            CampaignStateFactory.CreateInvocationTargetAllowance(initial.State, new(100)), allowance);
        var accepted = await CampaignCheckpointAcceptance.AcceptAsync(store, transition);
        Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted, accepted.Kind);
        var invocation = CampaignStateReducer.CreateProviderInvocationAuthority(accepted.AcceptedCheckpoint!,
            trial.Scenario.ExecutionAuthority, "style.synthetic", trial.Scenario.StyleProjection,
            trial.Scenario.Input, trial.Scenario.Plan, trial.Request);
        var coordinator = new CampaignScribeExecutionCoordinator(invocation, allowance, store, default,
            () => !store.CheckpointToken.IsCancellationRequested, clock.GetTimestamp());
        coordinator.Scope.BindRuntimeWindow(trial.Request, clock, clock.GetTimestamp());
        Assert.True(invocation.TryBeginDispatch(out _));

        var head = remote.CoordinationHead;
        var objects = remote.ObjectMutationAttempts;
        var refs = remote.RefMutationAttempts;
        var mutationSeen = false;
        var recoverySeen = false;
        var recoveryCancelled = false;
        remote.LoseResponse = loss;
        handler.BeforeRequestWithCancellation = (request, token) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var selectedMutation = request.Method == HttpMethod.Post && (loss == GitHubCoordinationRefTests.LostMutation.Blob
                ? path.EndsWith("/git/blobs", StringComparison.Ordinal) : path.EndsWith("/graphql", StringComparison.Ordinal));
            if (!mutationSeen && selectedMutation)
            {
                mutationSeen = true;
                clock.Advance(29999);
            }
            else if (mutationSeen && !recoverySeen && request.Method == HttpMethod.Get &&
                (loss == GitHubCoordinationRefTests.LostMutation.Blob
                    ? path.Contains("/git/blobs/", StringComparison.Ordinal)
                    : path.Contains("/git/ref/heads/contract-scribe/coordination/", StringComparison.Ordinal)))
            {
                recoverySeen = true;
                clock.Advance(30001);
                // This is the actual transport token of nested recovery. A new
                // independent real-time30s timer would still be live here.
                recoveryCancelled = token.IsCancellationRequested;
            }
            return Task.CompletedTask;
        };
        var permit = await coordinator.Scope.BeginHostAsync(DocumentationScribeHostOperation.SemanticTool, default)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(permit);
        Assert.True(mutationSeen);
        Assert.True(recoverySeen);
        Assert.True(recoveryCancelled);
        Assert.True(coordinator.Scope.SemanticAuthorizationTimedOut);
        Assert.True(coordinator.Scope.Failed);
        Assert.True(coordinator.Conflict);
        Assert.False(coordinator.Scope.SemanticAuthorizationClosed);
        Assert.Equal(0, allowance.ToolCallCount);
        Assert.Equal(accepted.Artifact!.Sha256, invocation.AcceptedCheckpoint.Artifact.Sha256);
        Assert.Equal(refs + (loss == GitHubCoordinationRefTests.LostMutation.Ref ? 1 : 0), remote.RefMutationAttempts);
        if (loss == GitHubCoordinationRefTests.LostMutation.Blob)
        {
            Assert.Equal(objects + 1, remote.ObjectMutationAttempts);
            Assert.Equal(head, remote.CoordinationHead);
        }
        else Assert.NotEqual(head, remote.CoordinationHead);
        await coordinator.LateObservation.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
