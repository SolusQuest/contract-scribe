using System.Security.Cryptography;
using ContractScribe.Core;
using ContractScribe.GitHub.Coordination;

namespace ContractScribe.Tests;

public sealed partial class GitHubCoordinationRefTests
{
    [Fact]
    public async Task Stale_publication_intent_cannot_reopen_provider_work_in_the_frozen_batch()
    {
        var remote = new CoordinationRemote();
        using var client = CheckpointClient(remote);
        await using var store = GitHubCoordinationStore.Create(client);
        Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
        SeedPublicationCheckpoint(remote);
        var original = PublicationCheckpointFixture("campaign-1");
        var authority = GitHubPublicationTestBatch.BindCheckpoint(Authority(), original);
        await store.WithPublicationAsync(async () =>
        {
            client.BindPublication(authority);
            try
            {
                var read = await store.ReadCurrentAsync();
                var claim = await store.ClaimAsync(read.Read!);
                remote.TargetHead = Oid('9');
                var stale = await store.AdvanceAsync(claim.State!, GitHubCoordinationStageUpdate.Stale(Oid('1'), Oid('9')));
                Assert.Equal(GitHubCoordinationStage.Stale, stale.State!.Stage);
                return true;
            }
            finally { client.UnbindPublication(); }
        }, default);
        var expanded = CampaignStateContractTests.AcceptedPublicationProviderReservation("campaign-1");
        var head = remote.CoordinationHead;
        var writes = remote.ObjectMutationAttempts + remote.RefMutationAttempts;
        var result = await store.ReplaceIfCurrentAsync(original.CheckpointRevision, original.Sha256,
            expanded.ExactUtf8Json.AsMemory(), expanded.CheckpointRevision, expanded.Sha256, default);
        Assert.Equal(CampaignCheckpointWriteKind.Unsafe, result.Kind);
        Assert.Equal(head, remote.CoordinationHead);
        Assert.Equal(writes, remote.ObjectMutationAttempts + remote.RefMutationAttempts);
        Assert.Equal(original.Sha256, (await store.ReadAsync(default)).Sha256);
    }

    [Fact]
    public async Task Publication_guards_renew_live_claim_beyond_initial_deadline_and_preserve_checkpoint()
    {
        var remote = new CoordinationRemote();
        using var client = CheckpointClient(remote);
        await using var store = GitHubCoordinationStore.Create(client);
        Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
        var checkpoint = SeedPublicationCheckpoint(remote);
        var started = remote.ServerNow;
        await store.WithPublicationAsync(async () =>
        {
            client.BindPublication(GitHubPublicationTestBatch.BindCheckpoint(Authority(), PublicationCheckpointFixture("campaign-1")));
            try
            {
                var read = await store.ReadCurrentAsync();
                Assert.NotNull(read.Read);
                var claim = await store.ClaimAsync(read.Read);
                Assert.NotNull(claim.State);
                for (var i = 0; i < 6; i++)
                {
                    remote.ServerNow = remote.ServerNow.AddSeconds(70);
                    var guard = await store.ReadClaimAsync(claim.State);
                    Assert.Equal(GitHubCoordinationOutcome.Guarded, guard.Outcome);
                    using var rivalClient = CheckpointClient(remote);
                    await using var rival = GitHubCoordinationStore.Create(rivalClient);
                    Assert.Equal(CampaignCheckpointReadKind.LeaseConflict, await rival.AcquireCheckpointAsync(default));
                    Assert.Equal(checkpoint, FixtureDocuments[remote.CoordinationHead!].Checkpoint);
                }
                var advanced = await store.AdvanceAsync(claim.State,
                    GitHubCoordinationStageUpdate.ContentCreated(Oid('4')));
                Assert.Equal(GitHubCoordinationOutcome.Advanced, advanced.Outcome);
                Assert.NotNull(advanced.ProposalRefEntitlement);
                Assert.Equal(checkpoint, FixtureDocuments[remote.CoordinationHead!].Checkpoint);
                return true;
            }
            finally { client.UnbindPublication(); }
        }, default);
        Assert.True(remote.ServerNow - started > TimeSpan.FromSeconds(300));
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("takeover")]
    [InlineData("cancel")]
    [InlineData("late-renewal")]
    [InlineData("late-takeover")]
    [InlineData("conflict")]
    [InlineData("external")]
    public async Task Publication_renewal_stops_on_expiry_takeover_cancel_or_conflict(string failure)
    {
        var remote = new CoordinationRemote();
        using var handler = new CheckpointInterceptHandler(remote);
        using var client = CheckpointClient(remote, handler: handler);
        var store = GitHubCoordinationStore.Create(client);
        using var rivalClient = CheckpointClient(remote);
        await using var rival = GitHubCoordinationStore.Create(rivalClient);
        using var cancelled = new CancellationTokenSource();
        try
        {
            Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
            SeedPublicationCheckpoint(remote);
            await store.WithPublicationAsync(async () =>
            {
                client.BindPublication(GitHubPublicationTestBatch.BindCheckpoint(Authority(), PublicationCheckpointFixture("campaign-1")));
                try
                {
                    var read = await store.ReadCurrentAsync();
                    var claim = await store.ClaimAsync(read.Read!);
                    Assert.NotNull(claim.State);
                    remote.ServerNow = remote.ServerNow.AddSeconds(failure is "expired" or "takeover" ? 301 : 70);
                    if (failure == "takeover")
                        Assert.Equal(CampaignCheckpointReadKind.Found, await rival.AcquireCheckpointAsync(default));
                    if (failure == "cancel") cancelled.Cancel();
                    if (failure == "external") remote.AppendDocument(document => document with
                    { Claim = document.Claim! with { ExpiresUnixSeconds = document.Claim.ExpiresUnixSeconds + 1 } });
                    var writesBeforeGuard = remote.ObjectMutationAttempts + remote.RefMutationAttempts;
                    if (failure is "late-renewal" or "late-takeover" or "conflict")
                    {
                        handler.BeforeRequest = async request =>
                        {
                            if (request.Method != System.Net.Http.HttpMethod.Post) return;
                            handler.BeforeRequest = null;
                            if (failure is "late-renewal" or "late-takeover")
                            {
                                remote.ServerNow = remote.ServerNow.AddSeconds(301);
                                if (failure == "late-takeover")
                                    Assert.Equal(CampaignCheckpointReadKind.Found, await rival.AcquireCheckpointAsync(default));
                            }
                            else
                            {
                                // A competing movement after immutable-object preparation
                                // must not be mistaken for our acknowledged claim-only CAS.
                                remote.AppendDocument(document => document with
                                { Claim = document.Claim! with { ExpiresUnixSeconds = document.Claim.ExpiresUnixSeconds + 1 } });
                            }
                            await Task.CompletedTask;
                        };
                    }
                    if (failure == "cancel")
                        Assert.Null((await store.ReadClaimAsync(claim.State!, cancelled.Token)).State);
                    else
                        Assert.Equal(GitHubCoordinationFailureKind.Conflict,
                            (await store.ReadClaimAsync(claim.State!, cancelled.Token)).Failure!.Kind);
                    if (failure is "takeover" or "late-takeover")
                        Assert.Equal(2, FixtureDocuments[remote.CoordinationHead!].Claim!.Epoch);
                    if (failure == "external")
                        Assert.Equal(writesBeforeGuard, remote.ObjectMutationAttempts + remote.RefMutationAttempts);
                    var head = remote.CoordinationHead;
                    var mutations = remote.ObjectMutationAttempts + remote.RefMutationAttempts;
                    var late = await store.AdvanceAsync(claim.State!, GitHubCoordinationStageUpdate.ContentCreated(Oid('4')), cancelled.Token);
                    Assert.Null(late.State);
                    Assert.Equal(head, remote.CoordinationHead);
                    Assert.Equal(mutations, remote.ObjectMutationAttempts + remote.RefMutationAttempts);
                    return true;
                }
                finally { client.UnbindPublication(); }
            }, default);
        }
        finally { await store.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public async Task Renewing_before_replayed_original_claim_preserves_physical_publication_history()
    {
        var remote = new CoordinationRemote();
        using var client = CheckpointClient(remote);
        await using var store = GitHubCoordinationStore.Create(client);
        Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
        var checkpoint = SeedPublicationCheckpoint(remote);
        var authority = GitHubPublicationTestBatch.BindCheckpoint(Authority(), PublicationCheckpointFixture("campaign-1"));
        await store.WithPublicationAsync(async () =>
        {
            client.BindPublication(authority);
            try
            {
                var read = await store.ReadCurrentAsync();
                var claim = await store.ClaimAsync(read.Read!);
                Assert.NotNull(claim.State);
                var originalHead = claim.State!.HeadOid;
                var publication = FixtureDocuments[originalHead].RetainAt(originalHead).Publication;
                var before = await store.ReadCurrentAsync();
                remote.ServerNow = remote.ServerNow.AddSeconds(70);
                await client.GetRepositoryAsync();
                var replay = await store.ClaimAsync(before.Read!);
                Assert.Equal(GitHubCoordinationOutcome.Replayed, replay.Outcome);
                Assert.NotEqual(originalHead, replay.State!.HeadOid);
                Assert.Equal(publication, FixtureDocuments[replay.State.HeadOid].Publication);
                Assert.Equal(checkpoint, FixtureDocuments[replay.State.HeadOid].Checkpoint);
                Assert.NotNull((await store.ReadCurrentAsync()).State);
                remote.RemoveCommit(remote.State(originalHead));
                Assert.Null((await store.ReadCurrentAsync()).State);
                return true;
            }
            finally { client.UnbindPublication(); }
        }, default);
    }

    private static GitHubCheckpointPointer SeedPublicationCheckpoint(CoordinationRemote remote)
    {
        var artifact = PublicationCheckpointFixture("campaign-1");
        var state = artifact.State;
        var pointer = new GitHubCheckpointPointer(remote.SeedBlob(artifact.ExactUtf8Json.ToArray()),
            artifact.Sha256, artifact.CheckpointRevision);
        byte[] plan = "{}"u8.ToArray();
        var planOid = remote.SeedBlob(plan);
        remote.AppendDocument(document => document with
        {
            Checkpoint = pointer,
            Plan = new(planOid, Convert.ToHexStringLower(SHA256.HashData(plan)),
                state.Snapshot.ExecutionCommitmentSha256, state.Batch.Identity),
        });
        return pointer;
    }
    internal static CampaignCheckpointArtifact PublicationCheckpointFixture(string lineage) =>
        CampaignStateContractTests.AcceptedPublicationCheckpoint(lineage);

}
