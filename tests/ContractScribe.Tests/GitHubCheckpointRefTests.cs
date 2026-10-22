using System.Reflection;
using ContractScribe.Core;
using ContractScribe.GitHub.Coordination;
using ContractScribe.GitHub.Transport;

namespace ContractScribe.Tests;

public sealed partial class GitHubCoordinationRefTests
{
    internal static GitHubApiClient CheckpointClient(CoordinationRemote remote, string lineage = "campaign-1", HttpMessageHandler? handler = null,
        int timeoutMilliseconds = 30_000)
    {
        using var registration = (IDisposable)typeof(GitHubTransportTestHook)
            .GetMethod("Register", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [Origin, handler ?? new RemoteHandler(remote), timeoutMilliseconds])!;
        return GitHubApiClient.CreateCheckpoint(ValidatedGitHubCheckpointAuthority.Create(
            "Owner", "repo", "refs/heads/main", lineage, Oid('1')), GitHubTransportTestHook.Placeholder);
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("blob")]
    [InlineData("target")]
    [InlineData("ref")]
    [InlineData("expiry")]
    public async Task Conditional_write_returns_only_actual_stable_owned_remote_readback(string fault)
    {
        var remote = new CoordinationRemote();
        using var handler = new CheckpointInterceptHandler(remote);
        using var client = CheckpointClient(remote, handler: handler);
        await using var store = GitHubCoordinationStore.Create(client);
        Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
        SeedPublicationCheckpoint(remote);
        var predecessor = PublicationCheckpointFixture("campaign-1");
        var next = CampaignStateReducer.Stop(predecessor, CampaignTerminalKind.Cancelled);
        Assert.Equal(CampaignTransitionKind.Applied, next.Kind);
        var nextBlob = GitHubCoordinationObjects.ObjectOid("blob", next.Artifact.ExactUtf8Json.AsSpan());
        var priorHead = remote.CoordinationHead!;
        var priorWrites = remote.RefMutationAttempts;
        var observedBlobs = 0;
        var commitGets = new Dictionary<string, int>();
        handler.BeforeRequest = request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method != HttpMethod.Get) return Task.CompletedTask;
            if (path.Contains("/git/commits/", StringComparison.Ordinal))
                commitGets[path] = commitGets.GetValueOrDefault(path) + 1;
            if (remote.RefMutationAttempts > priorWrites && path.EndsWith(nextBlob, StringComparison.Ordinal))
            {
                observedBlobs++;
                if (fault == "blob") remote.CorruptBlob(nextBlob);
                if (fault == "target") remote.TargetHead = Oid('2');
                if (fault == "ref") remote.ForceCoordinationHead(Oid('9'));
                if (fault == "expiry") remote.ServerNow = remote.ServerNow.AddSeconds(301);
            }
            return Task.CompletedTask;
        };
        var result = await store.ReplaceIfCurrentAsync(predecessor.CheckpointRevision, predecessor.Sha256,
            next.Artifact.ExactUtf8Json.AsMemory(), next.Artifact.CheckpointRevision, next.Artifact.Sha256, default);
        Assert.Equal(1, observedBlobs);
        Assert.Equal(priorWrites + 1, remote.RefMutationAttempts);
        if (fault != "exact")
        {
            Assert.NotEqual(CampaignCheckpointWriteKind.Written, result.Kind);
            Assert.Null(result.Readback);
            return;
        }
        Assert.Equal(CampaignCheckpointWriteKind.Written, result.Kind);
        var readback = Assert.IsType<CampaignCheckpointReadResult>(result.Readback);
        Assert.Equal(next.Artifact.Sha256, readback.Sha256);
        Assert.Equal(next.Artifact.CheckpointRevision, readback.CheckpointRevision);
        Assert.True(readback.ExactUtf8Json.AsSpan().SequenceEqual(next.Artifact.ExactUtf8Json.AsSpan()));
        // The predecessor and actual successor are each authenticated once in this
        // conditional transaction. A later independent read starts a fresh proof.
        Assert.Equal(1, commitGets.Single(pair => pair.Key.EndsWith(priorHead, StringComparison.Ordinal)).Value);
        Assert.All(commitGets.Where(pair => !pair.Key.EndsWith(remote.CoordinationHead!, StringComparison.Ordinal)),
            pair => Assert.Equal(1, pair.Value));
        // CreateObjects also authenticates the prepared commit before CAS.
        Assert.Equal(2, commitGets.Single(pair => pair.Key.EndsWith(remote.CoordinationHead!, StringComparison.Ordinal)).Value);
        var first = commitGets.ToDictionary();
        Assert.Equal(CampaignCheckpointReadKind.Found, (await store.ReadAsync(default)).Kind);
        Assert.Contains(first, pair => commitGets[pair.Key] > pair.Value);
    }

    [Fact]
    public async Task Confirmed_readback_lease_expiry_cancels_execution_even_while_checkpoint_gate_is_held()
    {
        var remote = new CoordinationRemote();
        using var handler = new CheckpointInterceptHandler(remote);
        using var client = CheckpointClient(remote, handler: handler);
        await using var store = GitHubCoordinationStore.Create(client);
        Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
        SeedPublicationCheckpoint(remote);
        var predecessor = PublicationCheckpointFixture("campaign-1");
        var next = CampaignStateReducer.Stop(predecessor, CampaignTerminalKind.Cancelled).Artifact;
        var blob = GitHubCoordinationObjects.ObjectOid("blob", next.ExactUtf8Json.AsSpan());
        var priorWrites = remote.RefMutationAttempts;
        handler.BeforeRequest = request =>
        {
            if (remote.RefMutationAttempts > priorWrites && request.Method == HttpMethod.Get
                && request.RequestUri!.AbsolutePath.EndsWith(blob, StringComparison.Ordinal))
                remote.ServerNow = remote.ServerNow.AddSeconds(297);
            return Task.CompletedTask;
        };
        var result = await store.ReplaceIfCurrentAsync(predecessor.CheckpointRevision, predecessor.Sha256,
            next.ExactUtf8Json.AsMemory(), next.CheckpointRevision, next.Sha256, default);
        Assert.Equal(CampaignCheckpointWriteKind.Written, result.Kind);
        var gate = (SemaphoreSlim)typeof(GitHubCoordinationStore).GetField("checkpointGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        await gate.WaitAsync();
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, store.CheckpointToken).WaitAsync(TimeSpan.FromSeconds(4));
        }
        catch (OperationCanceledException) when (store.CheckpointToken.IsCancellationRequested) { }
        finally { gate.Release(); }
        Assert.True(store.CheckpointToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Checkpoint_proof_reuses_authenticated_blobs_only_until_the_next_read_gate()
    {
        var remote = new CoordinationRemote();
        using var handler = new CheckpointInterceptHandler(remote);
        using var client = CheckpointClient(remote, handler: handler);
        await using var store = GitHubCoordinationStore.Create(client);
        Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
        var checkpoint = SeedPublicationCheckpoint(remote);
        var blobReads = new Dictionary<string, int>(StringComparer.Ordinal);
        handler.BeforeRequest = request =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.Contains("/git/blobs/", StringComparison.Ordinal))
            {
                var path = request.RequestUri.AbsolutePath;
                blobReads[path] = blobReads.GetValueOrDefault(path) + 1;
            }
            return Task.CompletedTask;
        };
        Assert.Equal(CampaignCheckpointReadKind.Found, (await store.ReadAsync(default)).Kind);
        Assert.Equal(1, blobReads.Single(pair => pair.Key.EndsWith(checkpoint.Oid, StringComparison.Ordinal)).Value);
        Assert.All(blobReads.Values, count => Assert.Equal(1, count));
        var first = blobReads.ToDictionary();
        Assert.Equal(CampaignCheckpointReadKind.Found, (await store.ReadAsync(default)).Kind);
        Assert.All(first, pair => Assert.Equal(pair.Value + 1, blobReads[pair.Key]));
        var mutations = remote.RefMutationAttempts;
        remote.Tamper(ObjectTamper.Blob);
        Assert.Equal(CampaignCheckpointReadKind.Invalid, (await store.ReadAsync(default)).Kind);
        Assert.Equal(mutations, remote.RefMutationAttempts);
    }

    [Fact]
    public async Task Disposal_waits_for_inflight_renewal_then_releases_claim_for_immediate_successor()
    {
        var remote = new CoordinationRemote();
        using var handler = new CheckpointInterceptHandler(remote);
        using var firstClient = CheckpointClient(remote, handler: handler);
        using var nextClient = CheckpointClient(remote);
        var first = GitHubCoordinationStore.Create(firstClient);
        Task? disposal = null;
        try
        {
            Assert.Equal(CampaignCheckpointReadKind.Found, await first.AcquireCheckpointAsync(default));
            handler.PauseNextRequest = true;
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(75));
            disposal = first.DisposeAsync().AsTask();
            // Normal shutdown must not cancel an already-dispatched renewal read
            // and convert it into a fail-closed lease loss that skips release.
            await Task.Delay(100);
            Assert.False(handler.Cancelled.Task.IsCompleted);
            handler.Release.TrySetResult();
            await disposal.WaitAsync(TimeSpan.FromSeconds(10));
            await using var next = GitHubCoordinationStore.Create(nextClient);
            Assert.Equal(CampaignCheckpointReadKind.Found, await next.AcquireCheckpointAsync(default));
            Assert.Equal(2, FixtureDocuments[remote.CoordinationHead!].Claim!.Epoch);
        }
        finally
        {
            handler.Release.TrySetResult();
            if (disposal is null) await first.DisposeAsync();
            else await disposal;
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Coordination_ref_movement_during_read_is_a_retryable_conflict_not_corrupt_state(bool acquiring, bool disappears)
    {
        var remote = new CoordinationRemote();
        using var firstClient = CheckpointClient(remote);
        var first = GitHubCoordinationStore.Create(firstClient);
        Task? release = null;
        try
        {
            Assert.Equal(CampaignCheckpointReadKind.Found, await first.AcquireCheckpointAsync(default));
            using var handler = new CheckpointInterceptHandler(remote);
            using var readerClient = CheckpointClient(remote, handler: handler);
            await using var reader = GitHubCoordinationStore.Create(readerClient);
            var reads = 0;
            var writesAfterRelease = 0;
            handler.BeforeRequest = async request =>
            {
                if (request.RequestUri!.AbsolutePath.Contains("/git/ref/heads/contract-scribe/coordination/", StringComparison.Ordinal)
                    && ++reads == 2)
                {
                    release = first.DisposeAsync().AsTask();
                    await release;
                    writesAfterRelease = remote.RefMutationAttempts;
                    if (disappears) remote.ForceCoordinationHead(null!);
                }
            };
            var kind = acquiring ? await reader.AcquireCheckpointAsync(default) : (await reader.ReadAsync(default)).Kind;
            Assert.NotNull(release);
            Assert.Equal(CampaignCheckpointReadKind.LeaseConflict, kind);
            Assert.Equal(writesAfterRelease, remote.RefMutationAttempts);
            using var nextClient = CheckpointClient(remote);
            await using var next = GitHubCoordinationStore.Create(nextClient);
            Assert.Equal(CampaignCheckpointReadKind.Found, await next.AcquireCheckpointAsync(default));
        }
        finally
        {
            if (release is null) await first.DisposeAsync();
            else await release;
        }
    }

    internal sealed class CheckpointInterceptHandler(CoordinationRemote remote) : HttpMessageHandler
    {
        internal Func<HttpRequestMessage, Task>? BeforeRequest;
        internal bool PauseNextRequest;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (BeforeRequest is { } before) await before(request);
            if (PauseNextRequest)
            {
                PauseNextRequest = false;
                Entered.TrySetResult();
                try { await Release.Task.WaitAsync(token); }
                catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            }
            var response = await remote.Reply(request);
            if (remote.IncludeServerDate) response.Headers.Date = remote.ServerNow;
            return response;
        }
    }

    [Fact]
    public async Task Publication_scope_exit_restores_checkpoint_only_transport_authority()
    {
        var remote = new CoordinationRemote();
        using var client = CheckpointClient(remote);
        await using var store = GitHubCoordinationStore.Create(client);
        Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
        client.BindPublication(Authority());
        Assert.True(client.HasMutationAuthority);
        client.UnbindPublication();
        Assert.False(client.HasMutationAuthority);
        var objects = remote.ObjectMutationAttempts;
        var refs = remote.RefMutationAttempts;
        var arbitrary = await client.CreateBlobAsync(Oid('9'), new byte[] { 1, 2, 3 });
        Assert.Equal(GitHubDelivery.NotDispatched, arbitrary.Delivery);
        var paired = await client.UpdatePublicationRefsAsync(
            new(GitHubPublicationFactory.CreateCoordinationRef(client.ReadAuthority), Oid('1'), Oid('2'), false),
            new(GitHubPublicationFactory.CreateProposalRef(Authority()), Oid('0'), Oid('3'), true), default);
        Assert.Equal(GitHubDelivery.NotDispatched, paired.Delivery);
        Assert.Equal(objects, remote.ObjectMutationAttempts);
        Assert.Equal(refs, remote.RefMutationAttempts);
    }

    [Theory]
    [InlineData("remove-link")]
    [InlineData("older-event")]
    [InlineData("new-invalid-event")]
    public async Task Canonical_head_must_retain_current_publication_provenance_before_checkpoint_admission(string tamper)
    {
        var remote = new G1Remote();
        var chain = PublishedChain(Authority());
        remote.Coordination.SeedChain(chain);
        var initialHead = PrepareFixture(chain[0]).CommitOid;
        if (tamper == "new-invalid-event")
        {
            // Canonical objects and a real parent do not authorize restarting an
            // already published operation with a new initial claim event.
            var head = remote.Coordination.CoordinationHead!;
            var invalid = GitHubCoordinationObjects.PreparePublication(
                GitHubCoordinationCodec.CreateClaim(Authority(), head), FixtureDocuments[head].RetainAt(head));
            remote.Coordination.SeedObjects(invalid);
            remote.Coordination.ForceCoordinationHead(invalid.CommitOid);
        }
        else remote.Coordination.AppendDocument(document => document with
        {
            Publication = tamper == "remove-link" ? null : FixtureDocuments[initialHead].RetainAt(initialHead).Publication,
        });
        var writes = remote.Coordination.RefMutationAttempts;
        using var client = CheckpointClient(remote.Coordination);
        await using var store = GitHubCoordinationStore.Create(client);
        Assert.Equal(CampaignCheckpointReadKind.Invalid, await store.AcquireCheckpointAsync(default));
        Assert.Equal(writes, remote.Coordination.RefMutationAttempts);
        using var observer = remote.Observer();
        Assert.NotNull((await observer.ObserveAsync()).Failure);
        Assert.Equal(writes, remote.Coordination.RefMutationAttempts);
    }

    [Theory]
    [InlineData("drop")]
    [InlineData("rewind")]
    [InlineData("replace-owner")]
    public async Task Canonical_claim_successor_cannot_erase_or_reuse_an_epoch(string tamper)
    {
        var remote = new CoordinationRemote();
        for (var index = 0; index < 2; index++)
        {
            using var client = CheckpointClient(remote);
            await using var store = GitHubCoordinationStore.Create(client);
            Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
        }
        remote.AppendDocument(document => document with
        {
            Claim = tamper switch
            {
                "drop" => null,
                "rewind" => document.Claim! with { Epoch = 1 },
                _ => document.Claim! with { Owner = new string('f', 64) },
            },
        });
        var writes = remote.RefMutationAttempts;
        using var freshClient = CheckpointClient(remote);
        await using var fresh = GitHubCoordinationStore.Create(freshClient);
        Assert.Equal(CampaignCheckpointReadKind.Invalid, await fresh.AcquireCheckpointAsync(default));
        Assert.Equal(writes, remote.RefMutationAttempts);
    }

    [Fact]
    public async Task Missing_server_time_prevents_claim_creation_before_any_mutation()
    {
        var remote = new CoordinationRemote { IncludeServerDate = false };
        using var client = CheckpointClient(remote);
        await using var store = GitHubCoordinationStore.Create(client);
        Assert.Equal(CampaignCheckpointReadKind.LeaseUnverifiable, await store.AcquireCheckpointAsync(default));
        Assert.Equal(0, remote.ObjectMutationAttempts);
        Assert.Equal(0, remote.RefMutationAttempts);
        Assert.Null(remote.CoordinationHead);
    }

    [Fact]
    public async Task Expired_claim_takeover_increments_epoch_without_granting_candidate_or_arbitrary_object_authority()
    {
        var remote = new CoordinationRemote();
        using var firstClient = CheckpointClient(remote);
        using var secondClient = CheckpointClient(remote);
        await using var first = GitHubCoordinationStore.Create(firstClient);
        await using var second = GitHubCoordinationStore.Create(secondClient);
        Assert.Equal(CampaignCheckpointReadKind.Found, await first.AcquireCheckpointAsync(default));
        var writes = remote.ObjectMutationAttempts;
        var arbitrary = await firstClient.CreateBlobAsync(Oid('9'), new byte[] { 1, 2, 3 });
        Assert.Equal(GitHubDelivery.NotDispatched, arbitrary.Delivery);
        Assert.Equal(writes, remote.ObjectMutationAttempts);
        remote.ServerNow = remote.ServerNow.AddSeconds(301);
        Assert.Equal(CampaignCheckpointReadKind.Found, await second.AcquireCheckpointAsync(default));
        var successor = remote.CoordinationHead;
        var firstDocument = FixtureDocuments[successor!];
        Assert.Equal(2, firstDocument.Claim!.Epoch);
        Assert.False(firstClient.HasMutationAuthority);
        Assert.False(secondClient.HasMutationAuthority);
        // A stale lifetime may observe the successor but has no right to replace its claim.
        Assert.Equal(CampaignCheckpointReadKind.NotFound, (await first.ReadAsync(default)).Kind);
        Assert.Equal(successor, remote.CoordinationHead);
    }

    [Fact]
    public async Task G1_observes_candidate_free_claim_without_inventing_publication_authority()
    {
        var remote = new G1Remote();
        using var client = CheckpointClient(remote.Coordination);
        await using var store = GitHubCoordinationStore.Create(client);
        Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
        var objectWrites = remote.Coordination.ObjectMutationAttempts;
        var refWrites = remote.Coordination.RefMutationAttempts;
        using var observer = remote.Observer();
        var result = await observer.ObserveAsync();
        Assert.Null(result.Failure);
        Assert.Equal(GitHubCampaignKind.Unpublished, result.Kind);
        Assert.Equal(GitHubCampaignLedgerKind.Current, result.LedgerKind);
        Assert.Equal(remote.Coordination.CoordinationHead, result.CoordinationOid);
        Assert.Null(result.Ledger);
        Assert.Empty(result.PullRequests);
        Assert.True(result.CollectionExhausted);
        Assert.Equal(objectWrites, remote.Coordination.ObjectMutationAttempts);
        Assert.Equal(refWrites, remote.Coordination.RefMutationAttempts);
        Assert.DoesNotContain("/user", remote.Requests);
    }

    [Fact]
    public async Task Checkpoint_claim_exists_before_any_candidate_and_only_one_process_owns_it()
    {
        var remote = new CoordinationRemote();
        using var firstClient = CheckpointClient(remote);
        using var secondClient = CheckpointClient(remote);
        await using var first = GitHubCoordinationStore.Create(firstClient);
        await using var second = GitHubCoordinationStore.Create(secondClient);
        Assert.False(firstClient.HasMutationAuthority);
        Assert.Equal(CampaignCheckpointReadKind.Found, await first.AcquireCheckpointAsync(default));
        Assert.NotNull(remote.CoordinationHead);
        Assert.Equal(CampaignCheckpointReadKind.NotFound, (await first.ReadAsync(default)).Kind);
        Assert.Equal(CampaignCheckpointReadKind.LeaseConflict, await second.AcquireCheckpointAsync(default));
        Assert.Equal(1, remote.SuccessfulRefMutations);
        Assert.False(firstClient.HasMutationAuthority);
    }

    [Fact]
    public async Task Simultaneous_checkpoint_claims_have_one_exact_CAS_winner()
    {
        var remote = new CoordinationRemote();
        remote.RequireConcurrentRefMutations(2);
        using var firstClient = CheckpointClient(remote);
        using var secondClient = CheckpointClient(remote);
        await using var first = GitHubCoordinationStore.Create(firstClient);
        await using var second = GitHubCoordinationStore.Create(secondClient);
        var results = await Task.WhenAll(first.AcquireCheckpointAsync(default).AsTask(), second.AcquireCheckpointAsync(default).AsTask());
        Assert.Single(results, result => result == CampaignCheckpointReadKind.Found);
        Assert.Equal(1, remote.SuccessfulRefMutations);
    }
}
