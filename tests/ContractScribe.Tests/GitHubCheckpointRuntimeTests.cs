using ContractScribe.Agent.Runtime;
using ContractScribe.Cli;
using ContractScribe.Core;
using ContractScribe.GitHub.Coordination;

namespace ContractScribe.Tests;

public sealed partial class CampaignStateContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Checkpoint_progress_does_not_repeat_admission_but_a_new_dispatch_requires_it(bool dispatch)
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
        Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted, accepted.Kind);
        var previousWrites = remote.RefMutationAttempts;
        var duplicate = await CampaignCheckpointAcceptance.AcceptAsync(store, admission);
        Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted, duplicate.Kind);
        Assert.False(duplicate.AcceptedCheckpoint!.TryIssueInvocation());
        Assert.Equal(previousWrites, remote.RefMutationAttempts);
        var authority = CampaignStateReducer.CreateProviderInvocationAuthority(accepted.AcceptedCheckpoint!, trial.Scenario.ExecutionAuthority,
            "style.synthetic", trial.Scenario.StyleProjection, trial.Scenario.Input, trial.Scenario.Plan, trial.Request);
        var coordinator = new CampaignScribeExecutionCoordinator(authority, allowance, store, default, null, allowance.Clock.GetTimestamp());
        Assert.True(authority.TryBeginDispatch(out _));
        var observations = 0;
        var writes = remote.RefMutationAttempts;
        handler.AfterResponse = (request, response) =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/repos/Owner/repo/pulls")
            {
                observations++;
                response.Dispose();
                response = new(System.Net.HttpStatusCode.Forbidden) { Content = new StringContent("{}") };
            }
            return Task.FromResult(response);
        };
        if (dispatch)
        {
            Assert.Null(await coordinator.Scope.ReserveProviderAsync(new(Sha256("fresh dispatch"), 1, 1, 4096), default));
            Assert.Equal(writes, remote.RefMutationAttempts);
            Assert.Equal(1, observations);
            Assert.Equal(0, allowance.ProviderRequestCount);
        }
        else
        {
            var progress = CampaignStateReducer.AdvanceHostOperation(authority,
                DocumentationScribeHostOperation.TerminalSubmission, 0, 90000, completing: false);
            Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted, (await CampaignCheckpointAcceptance.AcceptAsync(store, progress)).Kind);
            Assert.Equal(writes + 1, remote.RefMutationAttempts);
            Assert.Equal(0, observations);
        }
    }

    [Fact]
    public async Task B_delayed_checkpoint_grants_only_the_actual_original_Core_invocation()
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
        var before = remote.CoordinationHead!;
        var writes = remote.RefMutationAttempts;
        var refReads = 0;
        handler.AfterResponse = (request, response) =>
        {
            if (remote.RefMutationAttempts > writes && request.Method == HttpMethod.Get
                && request.RequestUri!.AbsolutePath.Contains("/git/ref/heads/contract-scribe/", StringComparison.Ordinal) && ++refReads == 1)
            {
                Assert.Equal(writes + 1, remote.RefMutationAttempts);
                Assert.Null(store.CheckpointDiagnostic);
                var node = System.Text.Json.Nodes.JsonNode.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult())!;
                node["object"]!["sha"] = before;
                response.Content.Dispose();
                response.Content = new StringContent(node.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
            }
            return Task.FromResult(response);
        };
        var allowance = new DocumentationScribeInvocationAllowance(trial.Coordinator.Scope.Allowance.Limits);
        var admission = CampaignStateReducer.AdmitProviderInvocation(initial, trial.Scenario.ExecutionAuthority, "style.synthetic",
            trial.Scenario.StyleProjection, trial.Scenario.Input, trial.Scenario.Plan, trial.Scenario.Plan.WorkItems[0].WorkItemKey,
            trial.Request, CampaignStateFactory.CreateInvocationTargetAllowance(initial.State, new(100)), allowance);
        var accepted = await CampaignCheckpointAcceptance.AcceptAsync(store, admission);
        Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted, accepted.Kind);
        var authority = CampaignStateReducer.CreateProviderInvocationAuthority(accepted.AcceptedCheckpoint!, trial.Scenario.ExecutionAuthority,
            "style.synthetic", trial.Scenario.StyleProjection, trial.Scenario.Input, trial.Scenario.Plan, trial.Request);
        var coordinator = new CampaignScribeExecutionCoordinator(authority, allowance, store, default, null, allowance.Clock.GetTimestamp());
        Assert.True(authority.TryBeginDispatch(out _));
        var port = new ReviewBoundaryPort();
        var session = new ScribeSessionTestDriver(trial.Request, trial.Attempt, trial.Prompt,
            ReviewBoundaryRegistry(trial.Request, port), ReviewBoundaryToolExchange(), coordinator.Scope);
        var result = await session.RunAsync();
        Assert.Equal(1, port.Calls);
        Assert.Equal(1, result.RunEnvelope.ToolCallCount);
        Assert.True(refReads > 3);
        Assert.Null(store.CheckpointDiagnostic);
    }

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
        var calls = new List<(string Method, string Path, bool Query, bool Effect)>();
        handler.BeforeRequest = message =>
        {
            calls.Add((message.Method.Method, message.RequestUri!.AbsolutePath,
                GitHubCoordinationRefTests.IsCheckpointQuery(message, currentOnly: true),
                message.Method != HttpMethod.Get && !GitHubCoordinationRefTests.IsCheckpointQuery(message)));
            return Task.CompletedTask;
        };
        var transition = CampaignStateReducer.AdvanceHostOperation(authority,
            DocumentationScribeHostOperation.TerminalSubmission, 0, 90000, completing: false);
        Assert.Equal(CampaignTransitionKind.Applied, transition.Kind);
        var result = await CampaignCheckpointAcceptance.AcceptAsync(store, transition);
        Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted, result.Kind);
        var transaction = calls.ToArray();
        Assert.InRange(transaction.Length, 1, 5); // Fresh mutable fences remain; immutable predecessors are already authenticated.
        Assert.Equal(1, transaction.Count(call => call.Effect)); // The authenticated unchanged plan is not uploaded again.
        Assert.DoesNotContain(transaction, call => call.Method == "GET" && call.Path == "/repos/Owner/repo");
        Assert.Equal(2, transaction.Count(call => call.Query)); // Fresh predecessor and actual successor packets each include repository, target, metadata and canonical bytes.
        Assert.Equal(2, transaction.Count(call => call.Method == "GET" && call.Path.Contains("/git/ref/heads/contract-scribe/")));
        Assert.DoesNotContain(transaction, call => call.Method == "GET" && call.Path.EndsWith("/git/ref/heads/main"));
        var newCommit = remote.CoordinationHead!;
        Assert.DoesNotContain(transaction, call => call.Method == "GET" && call.Path.EndsWith("/git/commits/" + newCommit));
        var checkpoint = GitHubCoordinationObjects.ObjectOid("blob", transition.Artifact.ExactUtf8Json.AsSpan());
        Assert.DoesNotContain(transaction, call => call.Method == "GET" && call.Path.EndsWith("/git/blobs/" + checkpoint));
        Assert.True(Array.FindLastIndex(transaction, call => call.Query)
            > Array.FindLastIndex(transaction, call => call.Effect));
        calls.Clear();
        Assert.Equal(CampaignCheckpointReadKind.Found, (await store.ReadAsync(default)).Kind);
        Assert.DoesNotContain(calls, call => call.Path.EndsWith("/git/commits/" + newCommit));
        Assert.Single(calls, call => call.Query);
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
        Assert.Equal(1, observation.PredecessorCommitReads);
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
    private int predecessorReads;
    private bool mutationSeen;
    internal void Observe(HttpRequestMessage request)
    {
        if (!measuring) return;
        requests++;
        if (GitHubCoordinationRefTests.IsRefMutation(request)) mutationSeen = true;
        // A canonical query physically carries the commit, tree and state in
        // one request. Count the predecessor phase, not a retired REST route.
        if (!mutationSeen && GitHubCoordinationRefTests.IsCheckpointQuery(request, currentOnly: true)) predecessorReads++;
    }
    public ValueTask<CampaignCheckpointReadResult> ReadAsync(CancellationToken token) => inner.ReadAsync(token);
    public ValueTask<CampaignCheckpointWriteResult> CreateIfAbsentAsync(ReadOnlyMemory<byte> bytes, long revision, string sha,
        CancellationToken token) => inner.CreateIfAbsentAsync(bytes, revision, sha, token);
    public async ValueTask<CampaignCheckpointWriteResult> ReplaceIfCurrentAsync(long expectedRevision, string expectedSha,
        ReadOnlyMemory<byte> bytes, long revision, string sha, CancellationToken token, CancellationToken mutationCancellationToken = default)
    {
        mutationCancellationToken.ThrowIfCancellationRequested();
        var next = CampaignStateJson.Parse(bytes).Artifact!;
        // Only admission to SemanticTool, not its completion, reconstructs
        // the removed caller-side full proof in the counterfactual row.
        measuring = next.State.ActiveReservation is CampaignProviderReservation { HostPhase: CampaignProviderHostPhase.SemanticTool };
        requests = 0;
        predecessorReads = 0;
        mutationSeen = false;
        var started = clock.GetTimestamp();
        try
        {
            var result = await inner.ReplaceIfCurrentAsync(expectedRevision, expectedSha, bytes, revision, sha, token, mutationCancellationToken);
            if (measuring && result.Kind == CampaignCheckpointWriteKind.Written)
            {
                if (duplicate) result = CampaignCheckpointWriteResult.Written(await inner.ReadAsync(token));
                advance(delay);
                // The actual pre-mutation canonical query is the predecessor proof.
                SemanticWrites.Add((requests, clock.GetTimestamp() - started, predecessorReads));
            }
            return result;
        }
        finally { measuring = false; }
    }
}
