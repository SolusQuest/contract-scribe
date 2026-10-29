using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;
using ContractScribe.Core;
using ContractScribe.GitHub.Coordination;

namespace ContractScribe.Tests;

public sealed partial class GitHubCoordinationRefTests
{
    [Theory]
    [InlineData("clean", CampaignCheckpointReadKind.Found)]
    [InlineData("partial", CampaignCheckpointReadKind.Unreadable)]
    [InlineData("403", CampaignCheckpointReadKind.Unreadable)]
    [InlineData("null-document", CampaignCheckpointReadKind.Unreadable)]
    [InlineData("alias", CampaignCheckpointReadKind.Unreadable)]
    [InlineData("parent-page", CampaignCheckpointReadKind.Unreadable)]
    [InlineData("truncated", CampaignCheckpointReadKind.Unreadable)]
    [InlineData("hash", CampaignCheckpointReadKind.Invalid)]
    [InlineData("tree", CampaignCheckpointReadKind.Invalid)]
    [InlineData("cancel", CampaignCheckpointReadKind.Unreadable)]
    public async Task Historical_canonical_query_rechecks_exact_bounded_facts_before_any_acceptance(
        string fault, CampaignCheckpointReadKind expected)
    {
        var remote = new CoordinationRemote();
        using var ownerClient = CheckpointClient(remote);
        await using var owner = GitHubCoordinationStore.Create(ownerClient);
        Assert.Equal(CampaignCheckpointReadKind.Found, await owner.AcquireCheckpointAsync(default));
        var historical = remote.CoordinationHead!;
        SeedPublicationCheckpoint(remote);
        using var handler = new CheckpointInterceptHandler(remote);
        using var readerClient = CheckpointClient(remote, handler: handler);
        await using var reader = GitHubCoordinationStore.Create(readerClient);
        using var cancellation = new CancellationTokenSource();
        var hits = 0;
        handler.AfterResponse = (request, response) =>
        {
            if (!ReadsCanonicalDocument(request, historical)) return Task.FromResult(response);
            hits++;
            if (fault == "cancel") cancellation.Cancel();
            if (fault == "403")
            {
                response.Dispose();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            }
            var node = JsonNode.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult())!;
            var repository = node["data"]!["repository"]!;
            switch (fault)
            {
                case "partial": node["errors"] = new JsonArray(new JsonObject { ["message"] = "synthetic partial query" }); break;
                case "null-document": repository["document"] = null; break;
                case "alias": repository["document"]!["oid"] = Oid('9'); break;
                case "parent-page": repository["document"]!["parents"]!["pageInfo"]!["hasNextPage"] = true; break;
                case "truncated": repository["state"]!["isTruncated"] = true; break;
                case "hash": repository["state"]!["oid"] = Oid('9'); break;
                case "tree": repository["document"]!["tree"]!["oid"] = Oid('9'); break;
            }
            response.Content.Dispose();
            response.Content = new StringContent(node.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
            return Task.FromResult(response);
        };
        var writes = remote.RefMutationAttempts;
        if (fault == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(cancellation.Token).AsTask());
        else Assert.Equal(expected, (await reader.ReadAsync(cancellation.Token)).Kind);
        Assert.Equal(1, hits);
        Assert.Equal(writes, remote.RefMutationAttempts);
        Assert.False(readerClient.HasMutationAuthority);
    }

    [Fact]
    public async Task Historical_checkpoint_payload_is_reused_only_by_the_confirmed_owner_while_current_bytes_stay_fresh()
    {
        var remote = new CoordinationRemote();
        using var handler = new CheckpointInterceptHandler(remote);
        using var client = CheckpointClient(remote, handler: handler);
        await using var store = GitHubCoordinationStore.Create(client);
        Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
        SeedPublicationCheckpoint(remote);
        var before = PublicationCheckpointFixture("campaign-1");
        var next = CampaignStateReducer.Stop(before, CampaignTerminalKind.Cancelled).Artifact;
        Assert.Equal(CampaignCheckpointWriteKind.Written, (await store.ReplaceIfCurrentAsync(before.CheckpointRevision,
            before.Sha256, next.ExactUtf8Json.AsMemory(), next.CheckpointRevision, next.Sha256, default)).Kind);
        var oldOid = GitHubCoordinationObjects.ObjectOid("blob", before.ExactUtf8Json.AsSpan());
        var currentOid = GitHubCoordinationObjects.ObjectOid("blob", next.ExactUtf8Json.AsSpan());
        Assert.NotEqual(oldOid, currentOid);
        var historicalReads = 0;
        var currentReads = 0;
        handler.AfterResponse = (request, response) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/git/blobs/" + currentOid, StringComparison.Ordinal)
                || IsCheckpointQuery(request, currentOnly: true)) currentReads++;
            if (request.RequestUri.AbsolutePath.EndsWith("/git/blobs/" + oldOid, StringComparison.Ordinal))
            {
                historicalReads++;
                response.Dispose();
                response = new(HttpStatusCode.Forbidden) { Content = new StringContent("{}") };
            }
            return Task.FromResult(response);
        };
        Assert.Equal(CampaignCheckpointReadKind.Found, (await store.ReadAsync(default)).Kind);
        Assert.Equal(0, historicalReads);
        Assert.True(currentReads > 0);
        var writes = remote.RefMutationAttempts;
        using var otherClient = CheckpointClient(remote, handler: handler);
        await using var other = GitHubCoordinationStore.Create(otherClient);
        Assert.Equal(CampaignCheckpointReadKind.Unreadable, await other.AcquireCheckpointAsync(default));
        Assert.True(historicalReads > 0);
        Assert.Equal(writes, remote.RefMutationAttempts);
    }

    [Theory]
    [InlineData("commit")]
    [InlineData("root")]
    [InlineData("leaf")]
    public async Task Checkpoint_owner_reuses_authenticated_immutable_objects_across_read_gates(string role)
    {
        var remote = new CoordinationRemote();
        using var handler = new CheckpointInterceptHandler(remote);
        using var client = CheckpointClient(remote, handler: handler);
        await using var store = GitHubCoordinationStore.Create(client);
        Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
        var historicalHead = remote.CoordinationHead!;
        SeedPublicationCheckpoint(remote);
        Assert.Equal(CampaignCheckpointReadKind.Found, (await store.ReadAsync(default)).Kind);
        var path = PayloadPath(historicalHead, role);
        var fetched = 0;
        handler.AfterResponse = (request, response) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(path, StringComparison.Ordinal) || ReadsCanonicalDocument(request, historicalHead))
            {
                fetched++;
                response.Dispose();
                response = new(HttpStatusCode.Forbidden) { Content = new StringContent("{}") };
            }
            return Task.FromResult(response);
        };
        try
        {
            Assert.Equal(CampaignCheckpointReadKind.Found, (await store.ReadAsync(default)).Kind);
            Assert.Equal(0, fetched);
            Assert.False(client.HasMutationAuthority);
            var writes = remote.RefMutationAttempts;
            using var otherClient = CheckpointClient(remote, handler: handler);
            await using var other = GitHubCoordinationStore.Create(otherClient);
            Assert.Equal(CampaignCheckpointReadKind.Unreadable, await other.AcquireCheckpointAsync(default));
            Assert.True(fetched > 0);
            Assert.Equal(writes, remote.RefMutationAttempts);
        }
        finally { handler.AfterResponse = null; }
    }

    [Theory]
    [InlineData("state")]
    [InlineData("checkpoint")]
    public async Task Checkpoint_owner_always_fetches_current_claim_and_checkpoint_bytes(string role)
    {
        var remote = new CoordinationRemote();
        using var handler = new CheckpointInterceptHandler(remote);
        using var client = CheckpointClient(remote, handler: handler);
        await using var store = GitHubCoordinationStore.Create(client);
        Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
        SeedPublicationCheckpoint(remote);
        Assert.Equal(CampaignCheckpointReadKind.Found, (await store.ReadAsync(default)).Kind);
        var path = PayloadPath(remote.CoordinationHead!, role);
        var fetched = 0;
        handler.AfterResponse = (request, response) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(path, StringComparison.Ordinal) || IsCheckpointQuery(request, currentOnly: true))
            {
                fetched++;
                response.Dispose();
                response = new(HttpStatusCode.Forbidden) { Content = new StringContent("{}") };
            }
            return Task.FromResult(response);
        };
        try
        {
            Assert.Equal(CampaignCheckpointReadKind.Unreadable, (await store.ReadAsync(default)).Kind);
            Assert.True(fetched > 0);
        }
        finally { handler.AfterResponse = null; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Checkpoint_pool_is_detached_on_owner_stop_or_disposal(bool dispose)
    {
        var remote = new CoordinationRemote();
        using var client = CheckpointClient(remote);
        var store = GitHubCoordinationStore.Create(client);
        var lifetime = store.CheckpointToken;
        Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
        SeedPublicationCheckpoint(remote);
        Assert.Equal(CampaignCheckpointReadKind.Found, (await store.ReadAsync(default)).Kind);
        var field = typeof(GitHubCoordinationStore).GetField("checkpointPayloads", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.NotNull(field.GetValue(store));
        if (dispose) await store.DisposeAsync();
        else typeof(GitHubCoordinationStore).GetMethod("StopCheckpoint", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(store, null);
        Assert.Null(field.GetValue(store));
        Assert.True(lifetime.IsCancellationRequested);
        if (!dispose) await store.DisposeAsync();
    }

    [Fact]
    public async Task Checkpoint_pool_cannot_hide_a_new_foreign_owner_or_authorize_its_write()
    {
        var remote = new CoordinationRemote();
        using var client = CheckpointClient(remote);
        await using var store = GitHubCoordinationStore.Create(client);
        Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
        SeedPublicationCheckpoint(remote);
        Assert.Equal(CampaignCheckpointReadKind.Found, (await store.ReadAsync(default)).Kind);
        remote.AppendDocument(document => document with
        {
            Claim = document.Claim! with { Owner = new string('f', 64), Epoch = document.Claim.Epoch + 1 },
        });
        var before = PublicationCheckpointFixture("campaign-1");
        var next = CampaignStateReducer.Stop(before, CampaignTerminalKind.Cancelled).Artifact;
        var writes = remote.RefMutationAttempts;
        var result = await store.ReplaceIfCurrentAsync(before.CheckpointRevision, before.Sha256,
            next.ExactUtf8Json.AsMemory(), next.CheckpointRevision, next.Sha256, default);
        Assert.Equal(CampaignCheckpointWriteKind.LeaseConflict, result.Kind);
        Assert.Equal(writes, remote.RefMutationAttempts);
        Assert.Null(typeof(GitHubCoordinationStore).GetField("checkpointPayloads", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store));
    }

    [Fact]
    public async Task Cancelled_checkpoint_read_does_not_promote_staged_history()
    {
        var remote = new CoordinationRemote();
        using var handler = new CheckpointInterceptHandler(remote);
        using var client = CheckpointClient(remote, handler: handler);
        await using var store = GitHubCoordinationStore.Create(client);
        Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
        SeedPublicationCheckpoint(remote);
        Assert.Equal(CampaignCheckpointReadKind.Found, (await store.ReadAsync(default)).Kind);
        var pool = typeof(GitHubCoordinationStore).GetField("checkpointPayloads", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        var values = (System.Collections.IDictionary)pool.GetType().GetField("values", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pool)!;
        var before = values.Count;
        remote.AppendDocument(document => document with
        {
            Claim = document.Claim! with { ExpiresUnixSeconds = document.Claim.ExpiresUnixSeconds + 1 },
        });
        using var cancellation = new CancellationTokenSource();
        var refs = 0;
        handler.BeforeRequestWithCancellation = (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/git/ref/heads/contract-scribe/", StringComparison.Ordinal)
                && ++refs == 1) cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadAsync(cancellation.Token).AsTask());
        Assert.Equal(before, values.Count);
        handler.BeforeRequestWithCancellation = null;
        Assert.Equal(CampaignCheckpointReadKind.Found, (await store.ReadAsync(default)).Kind);
        Assert.True(values.Count > before);
    }
}
