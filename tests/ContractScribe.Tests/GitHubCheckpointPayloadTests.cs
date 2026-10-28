using System.Net;
using System.Reflection;
using ContractScribe.Core;
using ContractScribe.GitHub.Coordination;

namespace ContractScribe.Tests;

public sealed partial class GitHubCoordinationRefTests
{
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
        SeedPublicationCheckpoint(remote);
        Assert.Equal(CampaignCheckpointReadKind.Found, (await store.ReadAsync(default)).Kind);
        var path = PayloadPath(remote.CoordinationHead!, role);
        var fetched = 0;
        handler.AfterResponse = (request, response) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(path, StringComparison.Ordinal))
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
            if (request.RequestUri!.AbsolutePath.EndsWith(path, StringComparison.Ordinal))
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
                && ++refs == 2) cancellation.Cancel();
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
