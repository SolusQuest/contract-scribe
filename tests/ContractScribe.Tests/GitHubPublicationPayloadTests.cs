using System.Net;
using System.Reflection;
using ContractScribe.Cli;
using ContractScribe.Core;
using ContractScribe.GitHub.Coordination;
using ContractScribe.GitHub.Transport;

namespace ContractScribe.Tests;

public sealed partial class GitHubCoordinationRefTests
{
    [Theory]
    [InlineData("commit", 403)]
    [InlineData("root", 404)]
    [InlineData("leaf", 403)]
    [InlineData("event", 404)]
    [InlineData("commit", 200)]
    public async Task Publication_payload_reuses_only_earlier_authenticated_objects_in_the_same_scope(string role, int status)
    {
        var remote = new CoordinationRemote();
        using var handler = new CheckpointInterceptHandler(remote);
        using var client = CheckpointClient(remote, handler: handler);
        await using var store = GitHubCoordinationStore.Create(client);
        Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
        SeedPublicationCheckpoint(remote);
        await PayloadPublication(store, client, async () =>
        {
            var claim = await store.ClaimAsync((await store.ReadCurrentAsync()).Read!);
            Assert.NotNull(claim.State);
            Assert.NotNull((await store.ReadCurrentAsync()).State);
            var path = PayloadPath(remote.CoordinationHead!, role);
            var fetched = 0;
            handler.AfterResponse = (request, response) =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith(path, StringComparison.Ordinal))
                {
                    fetched++;
                    response.Dispose();
                    return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
                    { Content = new StringContent("{}") });
                }
                return Task.FromResult(response);
            };
            try
            {
                var current = await store.ReadCurrentAsync();
                Assert.True(current.State is not null, current.Failure?.Kind.ToString());
                Assert.Equal(0, fetched);
            }
            finally { handler.AfterResponse = null; }
        });
    }

    [Theory]
    [InlineData("state")]
    [InlineData("checkpoint")]
    public async Task Publication_payload_never_replaces_the_current_claim_or_checkpoint_body(string role)
    {
        var remote = new CoordinationRemote();
        using var handler = new CheckpointInterceptHandler(remote);
        using var client = CheckpointClient(remote, handler: handler);
        await using var store = GitHubCoordinationStore.Create(client);
        Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
        SeedPublicationCheckpoint(remote);
        await PayloadPublication(store, client, async () =>
        {
            Assert.NotNull((await store.ClaimAsync((await store.ReadCurrentAsync()).Read!)).State);
            Assert.NotNull((await store.ReadCurrentAsync()).State);
            var path = PayloadPath(remote.CoordinationHead!, role);
            var fetched = 0;
            handler.AfterResponse = (request, response) =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith(path, StringComparison.Ordinal))
                {
                    fetched++;
                    response.Dispose();
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
                }
                return Task.FromResult(response);
            };
            try
            {
                Assert.Null((await store.ReadCurrentAsync()).State);
                Assert.True(fetched > 0);
            }
            finally { handler.AfterResponse = null; }
        });
    }

    [Theory]
    [InlineData("commit")]
    [InlineData("root")]
    [InlineData("leaf")]
    [InlineData("state")]
    [InlineData("event")]
    public async Task Publication_payload_cannot_replace_an_actual_successor_readback(string role)
    {
        var remote = new CoordinationRemote();
        using var handler = new CheckpointInterceptHandler(remote);
        using var client = CheckpointClient(remote, handler: handler);
        await using var store = GitHubCoordinationStore.Create(client);
        Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
        SeedPublicationCheckpoint(remote);
        await PayloadPublication(store, client, async () =>
        {
            Assert.NotNull((await store.ClaimAsync((await store.ReadCurrentAsync()).Read!)).State);
            Assert.NotNull((await store.ReadCurrentAsync()).State);
            var head = remote.CoordinationHead!;
            var prepared = GitHubCoordinationObjects.Prepare(FixtureDocuments[head]);
            var path = PayloadPath(head, role);
            var fetched = 0;
            handler.AfterResponse = (request, response) =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith(path, StringComparison.Ordinal))
                {
                    fetched++;
                    response.Dispose();
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                }
                return Task.FromResult(response);
            };
            try
            {
                var target = (await client.GetRefAsync(client.ReadAuthority.TargetRef)).Value!;
                var result = await InvokePayloadReadback(store, client, target, prepared);
                Assert.Null(result.State);
                Assert.True(fetched > 0);
            }
            finally { handler.AfterResponse = null; }
        });
    }

    [Fact]
    public async Task Publication_payload_does_not_survive_callback_exit_or_supply_another_client()
    {
        var remote = new CoordinationRemote();
        using var handler = new CheckpointInterceptHandler(remote);
        using var client = CheckpointClient(remote, handler: handler);
        await using var store = GitHubCoordinationStore.Create(client);
        Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
        SeedPublicationCheckpoint(remote);
        string path = "";
        var fetched = 0;
        handler.AfterResponse = (request, response) =>
        {
            if (path.Length > 0 && request.RequestUri!.AbsolutePath.EndsWith(path, StringComparison.Ordinal))
            {
                fetched++;
                response.Dispose();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            }
            return Task.FromResult(response);
        };
        try
        {
            await PayloadPublication(store, client, async () =>
            {
                Assert.NotNull((await store.ClaimAsync((await store.ReadCurrentAsync()).Read!)).State);
                Assert.NotNull((await store.ReadCurrentAsync()).State);
                path = PayloadPath(remote.CoordinationHead!, "commit");
                using var otherHandler = new CheckpointInterceptHandler(remote) { AfterResponse = handler.AfterResponse };
                using var otherClient = CheckpointClient(remote, handler: otherHandler);
                await using var otherStore = GitHubCoordinationStore.Create(otherClient);
                otherClient.BindPublication(GitHubPublicationTestBatch.BindCheckpoint(Authority(), PublicationCheckpointFixture("campaign-1")));
                try { Assert.Null((await otherStore.ReadCurrentAsync()).State); }
                finally { otherClient.UnbindPublication(); }
                Assert.True(fetched > 0);
                fetched = 0;
            });
            Assert.NotEqual(CampaignCheckpointReadKind.Found, (await store.ReadAsync(default)).Kind);
            Assert.True(fetched > 0);
        }
        finally { handler.AfterResponse = null; }
    }

    [Fact]
    public async Task Publication_payload_stages_nothing_when_the_final_successor_comparison_fails()
    {
        var remote = new CoordinationRemote();
        using var handler = new CheckpointInterceptHandler(remote);
        using var client = CheckpointClient(remote, handler: handler);
        await using var store = GitHubCoordinationStore.Create(client);
        Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
        SeedPublicationCheckpoint(remote);
        await PayloadPublication(store, client, async () =>
            Assert.NotNull((await store.ClaimAsync((await store.ReadCurrentAsync()).Read!)).State));
        await PayloadPublication(store, client, async () =>
        {
            var head = remote.CoordinationHead!;
            var value = GitHubCoordinationObjects.Prepare(FixtureDocuments[head]);
            var wrong = new GitHubPreparedCoordination(value.Document, null, [.. value.StateBytes, (byte)' '],
                value.BlobOid, value.LeafTreeOid, value.RootTreeOid, value.ParentOid, value.Message, value.CommitOid);
            var target = (await client.GetRefAsync(client.ReadAuthority.TargetRef)).Value!;
            Assert.Null((await InvokePayloadReadback(store, client, target, wrong)).State);
            var fetched = 0;
            handler.AfterResponse = (request, response) =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/git/commits/" + head, StringComparison.Ordinal))
                {
                    fetched++;
                    response.Dispose();
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
                }
                return Task.FromResult(response);
            };
            try
            {
                Assert.Null((await store.ReadCurrentAsync()).State);
                Assert.True(fetched > 0);
            }
            finally { handler.AfterResponse = null; }
        });
    }

    [Theory]
    [InlineData(0, 67108864L)]
    [InlineData(1024, 1L)]
    public async Task Publication_payload_capacity_falls_back_to_actual_fetches(int entries, long bytes)
    {
        var remote = new CoordinationRemote();
        using var handler = new CheckpointInterceptHandler(remote);
        using var client = CheckpointClient(remote, handler: handler);
        await using var store = GitHubCoordinationStore.Create(client);
        Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
        SeedPublicationCheckpoint(remote);
        await PayloadPublication(store, client, async () =>
        {
            ReplacePayloadLimits(store, client, entries, bytes);
            Assert.NotNull((await store.ClaimAsync((await store.ReadCurrentAsync()).Read!)).State);
            Assert.NotNull((await store.ReadCurrentAsync()).State);
            Assert.Equal(0, PayloadEntries(store));
            var path = PayloadPath(remote.CoordinationHead!, "commit");
            var fetched = 0;
            handler.BeforeRequest = request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith(path, StringComparison.Ordinal)) fetched++;
                return Task.CompletedTask;
            };
            Assert.NotNull((await store.ReadCurrentAsync()).State);
            Assert.NotNull((await store.ReadCurrentAsync()).State);
            Assert.Equal(2, fetched);
            Assert.Equal(0, PayloadEntries(store));
        });
    }

    [Fact]
    public async Task Publication_payload_failed_proof_releases_staged_capacity_for_the_next_proof()
    {
        var remote = new CoordinationRemote();
        using var client = CheckpointClient(remote);
        await using var store = GitHubCoordinationStore.Create(client);
        Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
        SeedPublicationCheckpoint(remote);
        await PayloadPublication(store, client, async () =>
            Assert.NotNull((await store.ClaimAsync((await store.ReadCurrentAsync()).Read!)).State));
        await PayloadPublication(store, client, async () =>
        {
            ReplacePayloadLimits(store, client, 3, 67108864);
            var value = GitHubCoordinationObjects.Prepare(FixtureDocuments[remote.CoordinationHead!]);
            var wrong = new GitHubPreparedCoordination(value.Document, null, [.. value.StateBytes, (byte)' '],
                value.BlobOid, value.LeafTreeOid, value.RootTreeOid, value.ParentOid, value.Message, value.CommitOid);
            var target = (await client.GetRefAsync(client.ReadAuthority.TargetRef)).Value!;
            Assert.Null((await InvokePayloadReadback(store, client, target, wrong)).State);
            Assert.Equal(0, PayloadEntries(store));
            Assert.NotNull((await store.ReadCurrentAsync()).State);
            Assert.Equal(3, PayloadEntries(store));
        });
    }

    [Fact]
    public async Task Publication_payload_failed_parent_retention_never_admits_its_authenticated_prefix()
    {
        var remote = new CoordinationRemote();
        using var handler = new CheckpointInterceptHandler(remote);
        using var client = CheckpointClient(remote, handler: handler);
        await using var store = GitHubCoordinationStore.Create(client);
        Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
        SeedPublicationCheckpoint(remote);
        await PayloadPublication(store, client, async () =>
        {
            Assert.NotNull((await store.ClaimAsync((await store.ReadCurrentAsync()).Read!)).State);
            var original = remote.CoordinationHead!;
            remote.AppendDocument(document => document with { Publication = null });
            var invalid = remote.CoordinationHead!;
            try
            {
                Assert.Null((await store.ReadCurrentAsync()).State);
                var fetched = 0;
                handler.BeforeRequest = request =>
                {
                    if (request.RequestUri!.AbsolutePath.EndsWith("/git/commits/" + invalid, StringComparison.Ordinal)) fetched++;
                    return Task.CompletedTask;
                };
                Assert.Null((await store.ReadCurrentAsync()).State);
                Assert.Equal(1, fetched);
            }
            finally { handler.BeforeRequest = null; remote.ForceCoordinationHead(original); }
        });
    }

    [Fact]
    public async Task Publication_payload_absent_publication_proof_does_not_warm_a_later_gate()
    {
        var remote = new CoordinationRemote();
        using var handler = new CheckpointInterceptHandler(remote);
        using var client = CheckpointClient(remote, handler: handler);
        await using var store = GitHubCoordinationStore.Create(client);
        Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
        SeedPublicationCheckpoint(remote);
        await PayloadPublication(store, client, async () =>
        {
            Assert.NotNull((await store.ReadCurrentAsync()).Read);
            Assert.Equal(0, PayloadEntries(store));
            var fetched = 0;
            handler.BeforeRequest = request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/git/commits/" + remote.CoordinationHead, StringComparison.Ordinal)) fetched++;
                return Task.CompletedTask;
            };
            Assert.NotNull((await store.ReadCurrentAsync()).Read);
            Assert.Equal(1, fetched);
            Assert.Equal(0, PayloadEntries(store));
        });
    }

    [Theory]
    [InlineData("caller")]
    [InlineData("owner")]
    [InlineData("host")]
    public async Task Publication_payload_cancellation_detaches_the_warm_pool_without_new_authority(string cause)
    {
        var remote = new CoordinationRemote();
        using var client = CheckpointClient(remote);
        await using var store = GitHubCoordinationStore.Create(client);
        Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
        SeedPublicationCheckpoint(remote);
        using var caller = new CancellationTokenSource();
        var clock = new GitHubPublicationFinalizationTests.DeadlineClock();
        using var host = new GitHubPublicationFinalization(caller.Token, clock);
        var execution = host.Begin(store.CheckpointToken);
        client.BindFinalizationDeadline(host.DeadlineToken, () => host.TimedOut);
        await PayloadPublication(store, client, async () =>
        {
            Assert.NotNull((await store.ClaimAsync((await store.ReadCurrentAsync(execution)).Read!, execution)).State);
            Assert.NotNull((await store.ReadCurrentAsync(execution)).State);
            Assert.True(PayloadEntries(store) > 0);
            if (cause == "caller") caller.Cancel();
            else if (cause == "host") clock.Expire();
            else typeof(GitHubCoordinationStore).GetMethod("StopCheckpoint", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(store, null);
            Assert.Null(typeof(GitHubCoordinationStore).GetField("publicationPayloads", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store));
            var mutations = remote.ObjectMutationAttempts + remote.RefMutationAttempts;
            var result = await store.ReadCurrentAsync(execution);
            Assert.Null(result.State);
            Assert.Equal(cause == "host" ? GitHubFailureCode.Timeout : GitHubFailureCode.Cancelled, result.Failure!.TransportFailure!.Code);
            Assert.Equal(mutations, remote.ObjectMutationAttempts + remote.RefMutationAttempts);
        }, execution);
    }

    private static void ReplacePayloadLimits(GitHubCoordinationStore store, GitHubApiClient client, int entries, long bytes)
    {
        var field = typeof(GitHubCoordinationStore).GetField("publicationPayloads", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ((IDisposable)field.GetValue(store)!).Dispose();
        var type = typeof(GitHubCoordinationStore).GetNestedType("PublicationPayloadPool", BindingFlags.NonPublic)!;
        field.SetValue(store, Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, [store, client.AuthenticatedRepository!, CancellationToken.None, entries, bytes], null));
    }

    private static int PayloadEntries(GitHubCoordinationStore store)
    {
        var pool = typeof(GitHubCoordinationStore).GetField("publicationPayloads", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        return ((System.Collections.IDictionary)pool.GetType().GetField("values", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pool)!).Count;
    }

    private static string PayloadPath(string head, string role)
    {
        var document = FixtureDocuments[head];
        var value = GitHubCoordinationObjects.Prepare(document);
        return role switch
        {
            "commit" => "/git/commits/" + head,
            "root" => "/git/trees/" + value.RootTreeOid,
            "leaf" => "/git/trees/" + value.LeafTreeOid,
            "state" => "/git/blobs/" + value.BlobOid,
            "event" => "/git/blobs/" + document.Publication!.Oid,
            "checkpoint" => "/git/blobs/" + document.Checkpoint!.Oid,
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };
    }

    private static async Task PayloadPublication(GitHubCoordinationStore store, GitHubApiClient client, Func<Task> body,
        CancellationToken token = default)
    {
        await store.WithPublicationAsync(async () =>
        {
            client.BindPublication(GitHubPublicationTestBatch.BindCheckpoint(Authority(), PublicationCheckpointFixture("campaign-1")));
            try { await body(); return true; }
            finally { client.UnbindPublication(); }
        }, token);
    }

    private static ValueTask<GitHubCoordinationResult> InvokePayloadReadback(GitHubCoordinationStore store,
        GitHubApiClient client, GitHubRef target, GitHubPreparedCoordination prepared) =>
        (ValueTask<GitHubCoordinationResult>)typeof(GitHubCoordinationStore)
            .GetMethod("ReadPreparedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(store, [client.AuthenticatedRepository!, target, prepared, CancellationToken.None])!;
}
