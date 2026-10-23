using ContractScribe.Agent.Runtime;
using ContractScribe.Cli;
using ContractScribe.Core;
using ContractScribe.GitHub.Coordination;

namespace ContractScribe.Tests;

public sealed partial class CampaignStateContractTests
{
    [Fact]
    public async Task Conditional_checkpoint_reuses_only_authenticated_created_document_objects_within_its_gate()
    {
        var trial = await SolusTrialAsync();
        var remote = new GitHubCoordinationRefTests.CoordinationRemote();
        using var handler = new GitHubCoordinationRefTests.CheckpointInterceptHandler(remote);
        using var client = GitHubCoordinationRefTests.CheckpointClient(remote, trial.Scenario.InitialState.CampaignLineage, handler);
        await using var store = GitHubCoordinationStore.Create(client);
        Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
        Assert.True(await store.BindPlanAsync(CampaignPlanner.CreateRecoveryProjection(trial.Scenario.Plan), default));
        var initial = CampaignStateJson.CreateArtifact(trial.Scenario.InitialState);
        Assert.Equal(CampaignCheckpointWriteKind.Written, (await store.CreateIfAbsentAsync(initial.ExactUtf8Json.AsMemory(),
            initial.CheckpointRevision, initial.Sha256, default)).Kind);
        var allowance = new DocumentationScribeInvocationAllowance(trial.Coordinator.Scope.Allowance.Limits);
        var admission = CampaignStateReducer.AdmitProviderInvocation(initial, trial.Scenario.ExecutionAuthority, "style.synthetic",
            trial.Scenario.StyleProjection, trial.Scenario.Input, trial.Scenario.Plan, trial.Scenario.Plan.WorkItems[0].WorkItemKey,
            trial.Request, CampaignStateFactory.CreateInvocationTargetAllowance(initial.State, new(100)), allowance);
        var accepted = await CampaignCheckpointAcceptance.AcceptAsync(store, admission);
        var authority = CampaignStateReducer.CreateProviderInvocationAuthority(accepted.AcceptedCheckpoint!, trial.Scenario.ExecutionAuthority,
            "style.synthetic", trial.Scenario.StyleProjection, trial.Scenario.Input, trial.Scenario.Plan, trial.Request);
        _ = new CampaignScribeExecutionCoordinator(authority, allowance, store, default, null, allowance.Clock.GetTimestamp());
        Assert.True(authority.TryBeginDispatch(out _));
        var calls = new List<(string Method, string Path)>();
        handler.BeforeRequest = message =>
        {
            calls.Add((message.Method.Method, message.RequestUri!.AbsolutePath));
            return Task.CompletedTask;
        };
        var transition = CampaignStateReducer.AdvanceHostOperation(authority,
            DocumentationScribeHostOperation.TerminalSubmission, 0, 90000, completing: false);
        Assert.Equal(CampaignTransitionKind.Applied, transition.Kind);
        var result = await CampaignCheckpointAcceptance.AcceptAsync(store, transition);
        Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted, result.Kind);
        var transaction = calls.ToArray();
        Assert.Equal(47, transaction.Length); // Before this correction: Core14 + conditional37 =51.
        Assert.Equal(7, transaction.Count(call => call.Method != "GET"));
        Assert.Equal(3, transaction.Count(call => call.Method == "GET" && call.Path == "/repos/Owner/repo"));
        Assert.Equal(7, transaction.Count(call => call.Method == "GET" && call.Path.Contains("/git/ref/heads/contract-scribe/")));
        Assert.Single(transaction, call => call.Method == "GET" && call.Path.EndsWith("/git/ref/heads/main"));
        var newCommit = remote.CoordinationHead!;
        Assert.Single(transaction, call => call.Method == "GET" && call.Path.EndsWith("/git/commits/" + newCommit));
        var checkpoint = GitHubCoordinationObjects.ObjectOid("blob", transition.Artifact.ExactUtf8Json.AsSpan());
        Assert.Equal(2, transaction.Count(call => call.Method == "GET" && call.Path.EndsWith("/git/blobs/" + checkpoint)));
        Assert.True(Array.FindLastIndex(transaction, call => call.Path.EndsWith("/git/blobs/" + checkpoint))
            > Array.FindIndex(transaction, call => call.Path == "/graphql"));
        calls.Clear();
        Assert.Equal(CampaignCheckpointReadKind.Found, (await store.ReadAsync(default)).Kind);
        Assert.Contains(calls, call => call.Path.EndsWith("/git/commits/" + newCommit));
        Assert.Contains(calls, call => call.Path.EndsWith("/git/blobs/" + checkpoint));
    }

    [Theory]
    [InlineData(125, false, 0, true)]
    [InlineData(125, true, 0, true)]
    [InlineData(175, false, 0, true)]
    [InlineData(175, true, 0, true)]
    [InlineData(175, false, 13000, true)]
    public async Task GitHub_checkpoint_transaction_allows_bounded_semantic_progress_without_a_second_complete_proof(
        int millisecondsPerRequest, bool duplicateReadback, int additionalDelay, bool reachesPort)
    {
        // Every actual HTTP roundtrip remains charged to the original clock.
        // Authorization may exceed 10s while the query retains its separate
        // at-most10s window. Duplicate proof remains a measured counterfactual.
        var clock = new SolusDeadlineClock();
        var trial = await SolusTrialAsync(clock: clock, targetMilliseconds: 120000, invocationMilliseconds: 120000);
        var remote = new GitHubCoordinationRefTests.CoordinationRemote();
        using var handler = new GitHubCoordinationRefTests.CheckpointInterceptHandler(remote);
        using var client = GitHubCoordinationRefTests.CheckpointClient(remote, trial.Scenario.InitialState.CampaignLineage, handler);
        await using var actual = GitHubCoordinationStore.Create(client);
        Assert.Equal(CampaignCheckpointReadKind.Found, await actual.AcquireCheckpointAsync(default));
        Assert.True(await actual.BindPlanAsync(CampaignPlanner.CreateRecoveryProjection(trial.Scenario.Plan), default));
        var initial = CampaignStateJson.CreateArtifact(trial.Scenario.InitialState);
        Assert.Equal(CampaignCheckpointWriteKind.Written, (await actual.CreateIfAbsentAsync(
            initial.ExactUtf8Json.AsMemory(), initial.CheckpointRevision, initial.Sha256, default)).Kind);
        var allowance = new DocumentationScribeInvocationAllowance(trial.Coordinator.Scope.Allowance.Limits, clock);
        var admission = CampaignStateReducer.AdmitProviderInvocation(initial, trial.Scenario.ExecutionAuthority, "style.synthetic",
            trial.Scenario.StyleProjection, trial.Scenario.Input, trial.Scenario.Plan, trial.Scenario.Plan.WorkItems[0].WorkItemKey,
            trial.Request, CampaignStateFactory.CreateInvocationTargetAllowance(initial.State, new(100)), allowance);
        var accepted = await CampaignCheckpointAcceptance.AcceptAsync(actual, admission);
        Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted, accepted.Kind);
        var authority = CampaignStateReducer.CreateProviderInvocationAuthority(accepted.AcceptedCheckpoint!, trial.Scenario.ExecutionAuthority,
            "style.synthetic", trial.Scenario.StyleProjection, trial.Scenario.Input, trial.Scenario.Plan, trial.Request);
        var trace = new RuntimeCheckpointTrace(actual, duplicateReadback, additionalDelay, clock, clock.Advance);
        handler.BeforeRequest = request =>
        {
            clock.Advance(millisecondsPerRequest);
            remote.ServerNow = remote.ServerNow.AddMilliseconds(millisecondsPerRequest);
            trace.Observe(request);
            return Task.CompletedTask;
        };
        var coordinator = new CampaignScribeExecutionCoordinator(authority, allowance,
            trace, default, () => !actual.CheckpointToken.IsCancellationRequested, clock.GetTimestamp());
        Assert.True(authority.TryBeginDispatch(out _));
        var port = new ReviewBoundaryPort();
        var session = new ScribeSessionTestDriver(trial.Request, trial.Attempt, trial.Prompt,
            ReviewBoundaryRegistry(trial.Request, port), ReviewBoundaryToolExchange(), coordinator.Scope);
        var result = await session.RunAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(port.Calls == (reachesPort ? 1 : 0),
            $"port={port.Calls}; observations={string.Join(';', trace.SemanticWrites)}; clock={clock.GetTimestamp()}");
        Assert.Equal(reachesPort ? 1 : 0, result.RunEnvelope.ToolCallCount);
        Assert.Single(trace.SemanticWrites);
        var observation = trace.SemanticWrites[0];
        Assert.Equal(duplicateReadback ? 3 : 2, observation.PredecessorCommitReads);
        Assert.True(observation.Requests > 0);
        Assert.True(reachesPort ? observation.ElapsedMilliseconds < 30000 : observation.ElapsedMilliseconds >= 30000,
            $"duplicate={duplicateReadback}; requests={observation.Requests}; elapsed={observation.ElapsedMilliseconds}");
        if (!reachesPort)
            Assert.Equal(DocumentationScribeFailureCode.Timeout, Assert.IsType<DocumentationScribeFailureTerminal>(result.Stop).Code);
        Assert.False(coordinator.Conflict);
    }

}

internal sealed class RuntimeCheckpointTrace(ICampaignCheckpointStore inner, bool duplicate, int delay,
    TimeProvider clock, Action<int> advance) : ICampaignSemanticCheckpointStore
{
    private ICampaignSemanticCheckpointStore SemanticStore => (ICampaignSemanticCheckpointStore)inner;
    public object SemanticCheckpointIdentity => SemanticStore.SemanticCheckpointIdentity;
    public CampaignSemanticCheckpointStoreKind SemanticCheckpointKind => SemanticStore.SemanticCheckpointKind;
    public IDisposable EnterSemanticCheckpointWindow(CampaignSemanticCheckpointWindow window) =>
        SemanticStore.EnterSemanticCheckpointWindow(window);
    internal List<(int Requests, long ElapsedMilliseconds, int PredecessorCommitReads)> SemanticWrites { get; } = [];
    private bool measuring;
    private int requests;
    private long readStarted;
    private readonly Dictionary<string, int> commits = new(StringComparer.Ordinal);
    internal void Observe(HttpRequestMessage request)
    {
        if (!measuring) return;
        requests++;
        if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.Contains("/git/commits/", StringComparison.Ordinal))
        {
            var path = request.RequestUri.AbsolutePath;
            commits[path] = commits.GetValueOrDefault(path) + 1;
        }
    }
    public ValueTask<CampaignCheckpointReadResult> ReadAsync(CancellationToken token)
    {
        readStarted = clock.GetTimestamp();
        requests = 0;
        commits.Clear();
        measuring = true;
        return inner.ReadAsync(token);
    }
    public ValueTask<CampaignCheckpointWriteResult> CreateIfAbsentAsync(ReadOnlyMemory<byte> bytes, long revision, string sha,
        CancellationToken token) => inner.CreateIfAbsentAsync(bytes, revision, sha, token);
    public async ValueTask<CampaignCheckpointWriteResult> ReplaceIfCurrentAsync(long expectedRevision, string expectedSha,
        ReadOnlyMemory<byte> bytes, long revision, string sha, CancellationToken token)
    {
        var next = CampaignStateJson.Parse(bytes).Artifact!;
        // Only admission to SemanticTool, not its completion, reconstructs
        // the removed caller-side full proof in the counterfactual row.
        measuring = next.State.ActiveReservation is CampaignProviderReservation { HostPhase: CampaignProviderHostPhase.SemanticTool };
        var started = readStarted;
        try
        {
            var result = await inner.ReplaceIfCurrentAsync(expectedRevision, expectedSha, bytes, revision, sha, token);
            if (measuring && result.Kind == CampaignCheckpointWriteKind.Written)
            {
                if (duplicate) result = CampaignCheckpointWriteResult.Written(await inner.ReadAsync(token));
                advance(delay);
                // First distinct commit read belongs to the predecessor.
                SemanticWrites.Add((requests, clock.GetTimestamp() - started, commits.First().Value));
            }
            return result;
        }
        finally { measuring = false; }
    }
}
