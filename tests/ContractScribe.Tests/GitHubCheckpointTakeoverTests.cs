using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ContractScribe.Agent.Runtime;
using ContractScribe.Cli;
using ContractScribe.Core;
using ContractScribe.GitHub.Coordination;

namespace ContractScribe.Tests;

public sealed partial class CampaignStateContractTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Acquire_B_confirms_expired_takeover_before_separate_Core_Writer_admission(int predecessors)
    {
        var trial = await SolusTrialAsync();
        var remote = new GitHubCoordinationRefTests.CoordinationRemote();
        using var firstClient = GitHubCoordinationRefTests.CheckpointClient(remote, trial.Scenario.InitialState.CampaignLineage);
        await using var first = GitHubCoordinationStore.Create(firstClient);
        Assert.Equal(CampaignCheckpointReadKind.Found, await first.AcquireCheckpointAsync(default));
        Assert.True(await first.BindPlanAsync(CampaignPlanner.CreateRecoveryProjection(trial.Scenario.Plan), default));
        var initial = CampaignStateJson.CreateArtifact(trial.Scenario.InitialState);
        Assert.Equal(CampaignCheckpointWriteKind.Written, (await first.CreateIfAbsentAsync(initial.ExactUtf8Json.AsMemory(),
            initial.CheckpointRevision, initial.Sha256, default)).Kind);
        remote.ServerNow = remote.ServerNow.AddSeconds(301);
        var prior = remote.CoordinationHead!;
        var writes = remote.RefMutationAttempts;
        using var handler = new GitHubCoordinationRefTests.CheckpointInterceptHandler(remote);
        using var client = GitHubCoordinationRefTests.CheckpointClient(remote, trial.Scenario.InitialState.CampaignLineage, handler);
        await using var store = GitHubCoordinationStore.Create(client);
        var reads = 0;
        var calls = new List<string>();
        var expectedIndex = -1;
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.AfterResponse = (request, response) =>
        {
            if (remote.RefMutationAttempts <= writes) return Task.FromResult(response);
            var path = request.RequestUri!.AbsolutePath;
            calls.Add(request.Method + " " + path);
            if (request.Method == HttpMethod.Get && path.Contains("/git/ref/heads/contract-scribe/", StringComparison.Ordinal))
            {
                if (++reads <= predecessors)
                {
                    Assert.Equal(writes + 1, remote.RefMutationAttempts);
                    Assert.Null(store.CheckpointDiagnostic);
                    Assert.False(client.HasMutationAuthority);
                    var node = JsonNode.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult())!;
                    node["object"]!["sha"] = prior;
                    response.Content.Dispose();
                    response.Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json");
                    pending.TrySetResult();
                }
                else if (reads == predecessors + 1) expectedIndex = calls.Count - 1;
            }
            return Task.FromResult(response);
        };
        var acquisition = store.AcquireCheckpointAsync(default).AsTask();
        await pending.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(acquisition.IsCompleted);
        Assert.Equal(CampaignCheckpointReadKind.Found, await acquisition.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(predecessors + 2, reads);
        Assert.Equal(writes + 1, remote.RefMutationAttempts);
        var repository = calls.FindIndex(expectedIndex + 1, call => call == "GET /repos/Owner/repo");
        var target = calls.FindIndex(repository + 1, call => call.EndsWith("/git/ref/heads/main", StringComparison.Ordinal));
        var final = calls.FindIndex(target + 1, call => call.Contains("/git/ref/heads/contract-scribe/", StringComparison.Ordinal));
        Assert.True(expectedIndex >= 0 && repository > expectedIndex && target > repository && final > target);
        Assert.False(client.HasMutationAuthority);
        Assert.Null(store.CheckpointDiagnostic);
        handler.AfterResponse = null;

        // Acquisition and a current observer are not the conditional Core Writer.
        var observed = await CampaignCheckpointAcceptance.AcceptCurrentAsync(store);
        Assert.False(observed.AcceptedCheckpoint!.TryIssueInvocation());
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
    }
}

public sealed partial class GitHubCoordinationRefTests
{
    [Theory]
    [InlineData("foreign", 2)]
    [InlineData("permission", 2)]
    [InlineData("transient", 2)]
    [InlineData("persistent", 3)]
    [InlineData("lost-ack", 1)]
    [InlineData("missing-date", 1)]
    public async Task Acquire_B_terminal_sample_never_retries_a_failed_GET_or_performs_cleanup(string fault, int expectedReads)
    {
        var remote = await ExpiredTakeoverFixtureAsync();
        var prior = remote.CoordinationHead!;
        var writes = remote.RefMutationAttempts;
        using var handler = new CheckpointInterceptHandler(remote);
        using var client = CheckpointClient(remote, handler: handler);
        var store = GitHubCoordinationStore.Create(client);
        var reads = 0;
        handler.AfterResponse = (request, response) =>
        {
            if (remote.RefMutationAttempts <= writes) return Task.FromResult(response);
            if (fault == "lost-ack" && IsRefMutation(request))
                throw new HttpRequestException("synthetic lost acknowledgement");
            if (request.Method != HttpMethod.Get || !request.RequestUri!.AbsolutePath.Contains("/git/ref/heads/contract-scribe/", StringComparison.Ordinal))
                return Task.FromResult(response);
            reads++;
            if (reads == 1 || fault == "persistent")
            {
                RefObservation(response, prior);
                if (fault == "missing-date") remote.IncludeServerDate = false;
            }
            else if (fault == "foreign") RefObservation(response, Oid('f'));
            else if (fault is "permission" or "transient")
            {
                response.Dispose();
                return Task.FromResult(new HttpResponseMessage(fault == "permission" ? HttpStatusCode.Forbidden : HttpStatusCode.ServiceUnavailable));
            }
            return Task.FromResult(response);
        };
        try
        {
            Assert.NotEqual(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
            Assert.Equal(expectedReads, reads);
            Assert.Equal(writes + 1, remote.RefMutationAttempts);
            Assert.True(store.CheckpointToken.IsCancellationRequested);
            var diagnostic = Assert.IsType<GitHubCheckpointDiagnostic>(store.CheckpointDiagnostic);
            Assert.Equal(GitHubCheckpointOperation.Acquire, diagnostic.Operation);
            if (fault is "permission" or "transient") Assert.Equal(fault == "permission" ? 403 : 503, diagnostic.HttpStatus);
            handler.AfterResponse = null;
            remote.IncludeServerDate = true;
            var observed = await CampaignCheckpointAcceptance.AcceptCurrentAsync(store, store.CheckpointToken);
            Assert.Null(observed.AcceptedCheckpoint);
            await store.DisposeAsync();
            Assert.Equal(writes + 1, remote.RefMutationAttempts);
            Assert.Same(diagnostic, store.CheckpointDiagnostic);
        }
        finally { handler.AfterResponse = null; await store.DisposeAsync(); }
    }

    [Theory]
    [InlineData("caller", 1)]
    [InlineData("recovery", 1)]
    [InlineData("requested-expiry", 2)]
    public async Task Acquire_B_unconfirmed_expiry_and_original_cancellation_cannot_grant_or_rearm(string fault, int expectedReads)
    {
        var remote = await ExpiredTakeoverFixtureAsync();
        var prior = remote.CoordinationHead!;
        var writes = remote.RefMutationAttempts;
        using var caller = new CancellationTokenSource();
        using var recovery = new CancellationTokenSource();
        using var handler = new CheckpointInterceptHandler(remote);
        using var client = CheckpointClient(remote, handler: handler);
        client.BindFinalizationDeadline(recovery.Token);
        var store = GitHubCoordinationStore.Create(client);
        var reads = 0;
        handler.AfterResponse = (request, response) =>
        {
            if (remote.RefMutationAttempts <= writes || request.Method != HttpMethod.Get
                || !request.RequestUri!.AbsolutePath.Contains("/git/ref/heads/contract-scribe/", StringComparison.Ordinal)) return Task.FromResult(response);
            if (++reads == 1)
            {
                RefObservation(response, prior);
                if (fault == "caller") caller.CancelAfter(50);
                if (fault == "recovery") recovery.CancelAfter(50);
            }
            else if (fault == "requested-expiry") remote.ServerNow = remote.ServerNow.AddSeconds(301);
            return Task.FromResult(response);
        };
        try
        {
            var started = Stopwatch.GetTimestamp();
            Assert.NotEqual(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(caller.Token));
            Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5));
            Assert.Equal(expectedReads, reads);
            Assert.True(store.CheckpointToken.IsCancellationRequested);
            var diagnostic = Assert.IsType<GitHubCheckpointDiagnostic>(store.CheckpointDiagnostic);
            if (fault != "requested-expiry")
            {
                Assert.Equal(GitHubCheckpointReason.Cancelled, diagnostic.Reason);
                Assert.Equal(fault == "caller" ? GitHubCheckpointCancellation.OperationToken : GitHubCheckpointCancellation.RecoveryToken, diagnostic.Cancellation);
                Assert.Null(diagnostic.RefComparison);
            }
            handler.AfterResponse = null;
            await store.DisposeAsync();
            Assert.Equal(writes + 1, remote.RefMutationAttempts);
            Assert.Same(diagnostic, store.CheckpointDiagnostic);
        }
        finally { handler.AfterResponse = null; await store.DisposeAsync(); }
    }

    [Theory]
    [InlineData("canonical", 2)]
    [InlineData("target", 2)]
    [InlineData("backslide", 3)]
    [InlineData("fence-transient", 3)]
    public async Task Acquire_B_expected_requires_canonical_and_fresh_authority_before_first_lease(string fault, int expectedReads)
    {
        var remote = await ExpiredTakeoverFixtureAsync();
        var prior = remote.CoordinationHead!;
        var writes = remote.RefMutationAttempts;
        using var handler = new CheckpointInterceptHandler(remote);
        using var client = CheckpointClient(remote, handler: handler);
        var store = GitHubCoordinationStore.Create(client);
        var reads = 0;
        handler.AfterResponse = (request, response) =>
        {
            if (remote.RefMutationAttempts <= writes) return Task.FromResult(response);
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path.Contains("/git/ref/heads/contract-scribe/", StringComparison.Ordinal))
            {
                if (++reads == 1) RefObservation(response, prior);
                else if (reads == 2 && fault == "canonical") remote.Tamper(ObjectTamper.Blob);
                else if (reads == 3 && fault == "backslide") RefObservation(response, prior);
                else if (reads == 3 && fault == "fence-transient")
                {
                    response.Dispose();
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                }
            }
            if (reads == 2 && fault == "target" && request.Method == HttpMethod.Get && path == "/repos/Owner/repo")
                remote.TargetHead = Oid('2');
            return Task.FromResult(response);
        };
        try
        {
            Assert.NotEqual(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
            Assert.Equal(expectedReads, reads);
            Assert.True(store.CheckpointToken.IsCancellationRequested);
            var diagnostic = Assert.IsType<GitHubCheckpointDiagnostic>(store.CheckpointDiagnostic);
            if (fault == "backslide") Assert.Equal(GitHubCheckpointRefRelation.Predecessor, diagnostic.RefComparison!.Relation);
            if (fault == "fence-transient") Assert.Equal(503, diagnostic.HttpStatus);
            handler.AfterResponse = null;
            await store.DisposeAsync();
            Assert.Equal(writes + 1, remote.RefMutationAttempts);
            Assert.Same(diagnostic, store.CheckpointDiagnostic);
        }
        finally { handler.AfterResponse = null; await store.DisposeAsync(); }
    }

    private static async Task<CoordinationRemote> ExpiredTakeoverFixtureAsync()
    {
        var remote = new CoordinationRemote();
        using var client = CheckpointClient(remote);
        await using (var store = GitHubCoordinationStore.Create(client))
        {
            Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
            SeedPublicationCheckpoint(remote);
        }
        remote.ServerNow = remote.ServerNow.AddSeconds(301);
        return remote;
    }
}
