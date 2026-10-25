using System.Net;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using ContractScribe.Core;
using ContractScribe.GitHub.Coordination;

namespace ContractScribe.Tests;

public sealed partial class GitHubCoordinationRefTests
{
    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task B_matched_checkpoint_ack_waits_for_physical_expected_and_fresh_authority(bool renew, int predecessors)
    {
        var remote = new CoordinationRemote();
        using var handler = new CheckpointInterceptHandler(remote);
        using var client = CheckpointClient(remote, handler: handler);
        await using var store = GitHubCoordinationStore.Create(client);
        Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
        SeedPublicationCheckpoint(remote);
        var prior = remote.CoordinationHead!;
        var writes = remote.RefMutationAttempts;
        var calls = new List<string>();
        var refReads = 0;
        var expectedIndex = -1;
        var callback = false;
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        remote.ServerNow = remote.ServerNow.AddSeconds(68);
        handler.AfterResponse = (request, response) =>
        {
            if (remote.RefMutationAttempts <= writes) return Task.FromResult(response);
            var path = request.RequestUri!.AbsolutePath;
            calls.Add(request.Method + " " + path);
            if (request.Method == HttpMethod.Get && path.Contains("/git/ref/heads/contract-scribe/", StringComparison.Ordinal))
            {
                if (++refReads <= predecessors)
                {
                    Assert.False(callback);
                    Assert.Equal(writes + 1, remote.RefMutationAttempts);
                    Assert.Null(store.CheckpointDiagnostic);
                    RefObservation(response, prior);
                    pending.TrySetResult();
                }
                else if (refReads == predecessors + 1) expectedIndex = calls.Count - 1;
            }
            return Task.FromResult(response);
        };
        var before = PublicationCheckpointFixture("campaign-1");
        var next = CampaignStateReducer.Stop(before, CampaignTerminalKind.Cancelled);
        async Task Execute()
        {
            if (renew) await store.WithPublicationAsync(() => { callback = true; return Task.FromResult(true); }, default);
            else
            {
                var accepted = await CampaignCheckpointAcceptance.AcceptAsync(store, next);
                Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted, accepted.Kind);
                Assert.Equal(next.Artifact.Sha256, accepted.AcceptedCheckpoint!.Artifact.Sha256);
                Assert.Equal(next.Artifact.CheckpointRevision, accepted.AcceptedCheckpoint.Artifact.CheckpointRevision);
            }
        }
        var execution = Execute();
        await pending.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(execution.IsCompleted);
        Assert.False(callback);
        await execution.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(renew, callback);
        Assert.Null(store.CheckpointDiagnostic);
        Assert.False(store.CheckpointToken.IsCancellationRequested);
        Assert.Equal(writes + 1, remote.RefMutationAttempts);
        // All three fresh physical authority reads occur after the expected observation.
        var repository = calls.FindIndex(expectedIndex + 1, call => call == "GET /repos/Owner/repo");
        var target = calls.FindIndex(repository + 1, call => call.EndsWith("/git/ref/heads/main", StringComparison.Ordinal));
        var final = calls.FindIndex(target + 1, call => call.Contains("/git/ref/heads/contract-scribe/", StringComparison.Ordinal));
        Assert.True(expectedIndex >= 0 && repository > expectedIndex && target > repository && final > target);
        Assert.Equal(predecessors + (renew ? 2 : 4), refReads);
        Assert.Single(calls, call => call.StartsWith("POST ", StringComparison.Ordinal));
        Assert.Equal(renew ? 1 : 2, calls.Count(call => call == "GET /repos/Owner/repo"));
        Assert.Equal(renew ? 1 : 2, calls.Count(call => call.EndsWith("/git/ref/heads/main", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("persistent", 3, "Predecessor")]
    [InlineData("window-room", 2, "Predecessor")]
    [InlineData("foreign", 2, "Other")]
    [InlineData("permission", 2, "Unavailable")]
    [InlineData("missing", 2, "Unavailable")]
    [InlineData("malformed", 2, "Unavailable")]
    [InlineData("noncommit", 2, "Unavailable")]
    [InlineData("backslide", 3, "Predecessor")]
    [InlineData("expiry", 2, null)]
    [InlineData("target", 2, null)]
    [InlineData("missing-date", 1, "Predecessor")]
    [InlineData("lost-ack", 1, "Predecessor")]
    public async Task B_terminal_observation_never_searches_past_failure_or_allows_cleanup(string fault, int expectedReads, string? relation)
    {
        var remote = new CoordinationRemote();
        using var handler = new CheckpointInterceptHandler(remote);
        using var client = CheckpointClient(remote, handler: handler);
        var store = GitHubCoordinationStore.Create(client);
        var disposed = false;
        try
        {
            Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
            SeedPublicationCheckpoint(remote);
            var prior = remote.CoordinationHead!;
            var writes = remote.RefMutationAttempts;
            var reads = 0;
            handler.BeforeRequestWithCancellation = async (request, token) =>
            {
                if (fault == "window-room" && reads == 1 && request.Method == HttpMethod.Get
                    && request.RequestUri!.AbsolutePath.Contains("/git/ref/heads/contract-scribe/", StringComparison.Ordinal))
                    await Task.Delay(2400, token); // remaining window cannot admit the750ms second backoff
            };
            handler.AfterResponse = (request, response) =>
            {
                if (remote.RefMutationAttempts <= writes) return Task.FromResult(response);
                if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/graphql" && fault == "lost-ack")
                    throw new HttpRequestException("synthetic lost acknowledgement");
                var path = request.RequestUri!.AbsolutePath;
                if (request.Method == HttpMethod.Get && path == "/repos/Owner/repo" && reads >= 2)
                {
                    if (fault == "expiry") remote.ServerNow = remote.ServerNow.AddSeconds(301);
                    if (fault == "target") remote.TargetHead = Oid('2');
                }
                if (request.Method != HttpMethod.Get || !path.Contains("/git/ref/heads/contract-scribe/", StringComparison.Ordinal))
                    return Task.FromResult(response);
                reads++;
                if (reads == 1 || fault is "persistent" or "window-room" || fault == "backslide" && reads == 3)
                {
                    RefObservation(response, prior);
                    if (fault == "missing-date") remote.IncludeServerDate = false;
                }
                else if (fault == "foreign") RefObservation(response, Oid('f'));
                else if (fault is "permission" or "missing")
                {
                    response.Dispose();
                    return Task.FromResult(new HttpResponseMessage(fault == "permission" ? HttpStatusCode.Forbidden : HttpStatusCode.NotFound));
                }
                else if (fault is "malformed" or "noncommit") RefObservation(response, null, fault);
                return Task.FromResult(response);
            };
            var before = PublicationCheckpointFixture("campaign-1");
            var next = CampaignStateReducer.Stop(before, CampaignTerminalKind.Cancelled);
            var accepted = await CampaignCheckpointAcceptance.AcceptAsync(store, next);
            Assert.Equal(CampaignCheckpointAcceptanceKind.PublicationFailure, accepted.Kind);
            Assert.Null(accepted.AcceptedCheckpoint);
            Assert.Equal(expectedReads, reads);
            Assert.True(store.CheckpointToken.IsCancellationRequested);
            Assert.Equal(writes + 1, remote.RefMutationAttempts);
            var first = Assert.IsType<GitHubCheckpointDiagnostic>(store.CheckpointDiagnostic);
            Assert.Equal(next.Artifact.CheckpointRevision, first.AttemptedRevision);
            Assert.Equal(GitHubCheckpointOperation.Write, first.Operation);
            Assert.Null(first.Cancellation);
            if (relation is not null) Assert.Equal(relation, first.RefComparison!.Relation.ToString());
            else Assert.Null(first.RefComparison);
            if (fault == "permission") { Assert.Equal(403, first.HttpStatus); Assert.Null(first.RefComparison!.ObservedCommit); }
            if (fault == "missing") Assert.Equal(404, first.HttpStatus);
            if (fault == "lost-ack") Assert.False(first.RefComparison!.Acknowledged);
            var observer = await CampaignCheckpointAcceptance.AcceptCurrentAsync(store, store.CheckpointToken);
            Assert.Equal(CampaignCheckpointAcceptanceKind.Cancelled, observer.Kind);
            Assert.Null(observer.AcceptedCheckpoint);
            handler.AfterResponse = null;
            handler.BeforeRequestWithCancellation = null;
            await store.DisposeAsync();
            disposed = true;
            Assert.Equal(writes + 1, remote.RefMutationAttempts);
            Assert.Same(first, store.CheckpointDiagnostic);
        }
        finally { if (!disposed) await store.DisposeAsync(); }
    }

    [Theory]
    [InlineData("caller-backoff", 1, "OperationToken", false)]
    [InlineData("caller-before-backoff", 1, "OperationToken", false)]
    [InlineData("owner-backoff", 1, "RecoveryToken", false)]
    [InlineData("owner-operation-backoff", 1, "RecoveryToken", false)]
    [InlineData("poll-timeout", 2, "RecoveryToken", true)]
    [InlineData("recovery-timeout", 2, "RecoveryToken", true)]
    public async Task B_pending_cancellation_retains_original_source_and_never_rearms_a_lease(string fault, int expectedReads,
        string cancellation, bool failedRead)
    {
        var remote = new CoordinationRemote();
        using var handler = new CheckpointInterceptHandler(remote);
        using var client = CheckpointClient(remote, handler: handler);
        using var caller = new CancellationTokenSource();
        using var recovery = new CancellationTokenSource();
        client.BindFinalizationDeadline(recovery.Token);
        var store = GitHubCoordinationStore.Create(client);
        var disposed = false;
        try
        {
            Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
            SeedPublicationCheckpoint(remote);
            var prior = remote.CoordinationHead!;
            var writes = remote.RefMutationAttempts;
            var reads = 0;
            handler.BeforeRequestWithCancellation = async (request, token) =>
            {
                if (remote.RefMutationAttempts <= writes || request.Method != HttpMethod.Get
                    || !request.RequestUri!.AbsolutePath.Contains("/git/ref/heads/contract-scribe/", StringComparison.Ordinal)) return;
                if (++reads == 2 && fault.EndsWith("timeout", StringComparison.Ordinal))
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
            };
            handler.AfterResponse = (request, response) =>
            {
                if (remote.RefMutationAttempts > writes && reads == 1 && request.Method == HttpMethod.Get
                    && request.RequestUri!.AbsolutePath.Contains("/git/ref/heads/contract-scribe/", StringComparison.Ordinal))
                {
                    RefObservation(response, prior);
                    if (fault == "caller-backoff") caller.CancelAfter(50);
                    if (fault == "caller-before-backoff") caller.Cancel();
                    if (fault is "owner-backoff" or "owner-operation-backoff")
                        ((CancellationTokenSource)typeof(GitHubCoordinationStore).GetField("checkpointExpiry", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .GetValue(store)!).CancelAfter(50); // shorten the independently confirmed expiry, never extend it
                    if (fault == "recovery-timeout") recovery.CancelAfter(500);
                }
                return Task.FromResult(response);
            };
            var callback = false;
            var started = Stopwatch.GetTimestamp();
            await Assert.ThrowsAnyAsync<Exception>(() => store.WithPublicationAsync(() =>
            { callback = true; return Task.FromResult(true); }, fault == "owner-operation-backoff" ? store.CheckpointToken : caller.Token));
            Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5));
            Assert.False(callback);
            Assert.Equal(expectedReads, reads);
            Assert.True(store.CheckpointToken.IsCancellationRequested);
            var first = Assert.IsType<GitHubCheckpointDiagnostic>(store.CheckpointDiagnostic);
            Assert.Equal(GitHubCheckpointReason.Cancelled, first.Reason);
            Assert.Equal(cancellation, first.Cancellation!.ToString());
            if (failedRead) Assert.Equal(GitHubCheckpointRefRelation.Unavailable, first.RefComparison!.Relation);
            else Assert.Null(first.RefComparison);
            handler.AfterResponse = null;
            handler.BeforeRequestWithCancellation = null;
            await store.DisposeAsync();
            disposed = true;
            Assert.Equal(writes + 1, remote.RefMutationAttempts);
            Assert.Same(first, store.CheckpointDiagnostic);
        }
        finally { if (!disposed) await store.DisposeAsync(); }
    }

    private static void RefObservation(HttpResponseMessage response, string? oid, string? fault = null)
    {
        var node = JsonNode.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult())!;
        if (fault == "malformed") node = new JsonObject();
        else if (fault == "noncommit") node["object"]!["type"] = "tag";
        else node["object"]!["sha"] = oid;
        response.Content.Dispose();
        response.Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json");
    }
}
