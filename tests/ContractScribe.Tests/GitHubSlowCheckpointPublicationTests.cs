using System.Reflection;
using System.Security.Cryptography;
using ContractScribe.Core;
using ContractScribe.GitHub.Coordination;
using ContractScribe.GitHub.Publication;
using ContractScribe.GitHub.Transport;
using Branch = ContractScribe.Tests.GitHubProposalBranchTests;

namespace ContractScribe.Tests;

public sealed partial class GitHubPublicationReconcilerTests
{
    [Fact]
    public async Task Slow_checkpoint_publication_renews_through_real_guards_and_atomic_ref_publication()
    {
        var remote = new Remote { ServerNow = DateTimeOffset.UtcNow };
        var artifact = GitHubCoordinationRefTests.PublicationCheckpointFixture("campaign");
        var file = Assert.Single(artifact.State.CandidateObservation!.ChangedFiles);
        var original = Assert.Single(Enumerable.Range(0, 2).Select(index =>
            System.Text.Encoding.UTF8.GetBytes($"public void Operation{index:D3}(string value) {{ }}")),
            bytes => Convert.ToHexStringLower(SHA256.HashData(bytes)) == file.OriginalFileSha256);
        byte[] candidate = System.Text.Encoding.UTF8.GetBytes(file.Path + ":candidate");
        Assert.Equal(file.OriginalFileSha256, Convert.ToHexStringLower(SHA256.HashData(original)));
        Assert.Equal(file.CandidateFileSha256, Convert.ToHexStringLower(SHA256.HashData(candidate)));
        remote.Git.SetCheckpointFixtureFile(file.Path, original);
        var authority = GitHubPublicationTestBatch.BindCheckpoint(Branch.Authority(remote.Git), artifact);
        var payload = GitHubPublicationFactory.CreatePayload(authority, [new(file.Path, candidate)]);
        using var hook = (IDisposable)typeof(GitHubTransportTestHook).GetMethod("Register", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [new Uri("http://127.0.0.1:43219/"), new Handler(remote), 30_000])!;
        using var client = GitHubApiClient.CreateCheckpoint(ValidatedGitHubCheckpointAuthority.Create(
            authority.RepositoryOwner, authority.RepositoryName, authority.TargetRef,
            authority.CampaignLineage, authority.ExpectedBaseCommitOid), GitHubTransportTestHook.Placeholder);
        var store = GitHubCoordinationStore.Create(client);
        try
        {
            Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
            var reference = GitHubPublicationFactory.CreateCoordinationRef(authority);
            var head = remote.Git.Refs[reference];
            var commit = remote.Git.Commits[head];
            var leaf = remote.Git.Trees[commit.TreeOid].Single().Oid;
            var document = GitHubCoordinationDocument.Decode(remote.Git.Blobs[
                remote.Git.Trees[leaf].Single(entry => entry.Path == GitHubCoordinationObjects.StatePath).Oid]);
            var bytes = artifact.ExactUtf8Json.ToArray();
            var blobOid = GitHubCoordinationObjects.ObjectOid("blob", bytes);
            remote.Git.Blobs[blobOid] = bytes;
            byte[] plan = "{}"u8.ToArray();
            var planOid = GitHubCoordinationObjects.ObjectOid("blob", plan);
            remote.Git.Blobs[planOid] = plan;
            var seeded = GitHubCoordinationObjects.Prepare(document.RetainAt(head) with
            {
                PredecessorOid = head,
                Checkpoint = new(blobOid, artifact.Sha256, artifact.CheckpointRevision),
                Plan = new(planOid, Convert.ToHexStringLower(SHA256.HashData(plan)),
                    artifact.State.Snapshot.ExecutionCommitmentSha256, artifact.State.Batch.Identity),
            });
            remote.Git.Blobs[seeded.BlobOid] = seeded.StateBytes.ToArray();
            remote.Git.Trees[seeded.LeafTreeOid] = GitHubCoordinationObjects.LeafEntries(seeded);
            remote.Git.Trees[seeded.RootTreeOid] = GitHubCoordinationObjects.RootEntries(seeded);
            var created = GitHubCoordinationObjects.CommitRequest(seeded);
            remote.Git.Commits[seeded.CommitOid] = new(seeded.CommitOid, seeded.RootTreeOid, [created.ParentOid],
                seeded.Message, created.Author, created.Committer);
            remote.Git.Refs[reference] = seeded.CommitOid;
            var start = remote.ServerNow;
            remote.SecondsPerRequest = 1;
            var observed = await store.WithPublicationAsync(async () =>
            {
                client.BindPublication(authority);
                try
                {
                    var reconciler = GitHubPublicationReconciler.Create(client, Publisher, store);
                    var result = await reconciler.PublishObservedAsync(authority, payload);
                    if (result.Result.Kind == GitHubPublicationResultKind.RecoveredRefPartial)
                        result = await reconciler.PublishObservedAsync(authority, payload);
                    return result;
                }
                finally { client.UnbindPublication(); }
            }, default);
            Assert.True(observed.Result.Kind == GitHubPublicationResultKind.Published,
                observed.Result.Kind + " " + System.Text.Json.JsonSerializer.Serialize(observed.Diagnostic));
            Assert.True(remote.ServerNow - start > TimeSpan.FromSeconds(300));
            Assert.Equal(1, remote.PrAttempts);
            Assert.Equal(1, remote.Git.ProposalWrites);
            remote.SecondsPerRequest = 0;
            var retained = await store.ReadAsync(default);
            Assert.Equal(CampaignCheckpointReadKind.Found, retained.Kind);
            Assert.Equal(artifact.Sha256, retained.Sha256);
        }
        finally { await store.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
    }
}
