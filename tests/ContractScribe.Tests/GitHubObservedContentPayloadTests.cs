using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using ContractScribe.Cli;
using ContractScribe.Core;
using ContractScribe.GitHub.Coordination;
using ContractScribe.GitHub.Publication;
using ContractScribe.GitHub.PullRequests;
using ContractScribe.GitHub.Transport;
using Branch = ContractScribe.Tests.GitHubProposalBranchTests;

namespace ContractScribe.Tests;

public sealed partial class GitHubPublicationReconcilerTests
{
    [Theory]
    [InlineData(403)]
    [InlineData(404)]
    public async Task Observed_content_reuses_successful_owner_payloads_but_a_new_store_starts_cold(int status)
    {
        await WithObservedContent(async (remote, client, store, generation) =>
        {
            Assert.True(await store.VerifyObservedProposalIntegrityAsync(generation, default));
            var contentPath = "/git/commits/" + generation.HeadOid;
            var reads = remote.Attempts.Count(call => call.Path.EndsWith(contentPath, StringComparison.Ordinal));
            remote.RejectRead = path => path.EndsWith(contentPath, StringComparison.Ordinal) ? (HttpStatusCode)status : null;
            Assert.True(await store.VerifyObservedProposalIntegrityAsync(generation, default));
            Assert.Equal(reads, remote.Attempts.Count(call => call.Path.EndsWith(contentPath, StringComparison.Ordinal)));
            await using var cold = GitHubCoordinationStore.Create(client);
            if (status == 404) Assert.False(await cold.VerifyObservedProposalIntegrityAsync(generation, default));
            else await Assert.ThrowsAsync<GitHubCoordinationException>(async () =>
                await cold.VerifyObservedProposalIntegrityAsync(generation, default));
            remote.RejectRead = null;
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Observed_content_failed_or_cancelled_proof_never_promotes_its_prefix(bool cancelled)
    {
        await WithObservedContent(async (remote, _, store, generation) =>
        {
            using var stop = new CancellationTokenSource();
            var changed = generation with { TreeOid = new string('f', 40) };
            if (cancelled)
            {
                remote.BeforeRead = path =>
                {
                    if (path.EndsWith("/git/commits/" + generation.HeadOid, StringComparison.Ordinal)) stop.Cancel();
                };
                var failure = await Assert.ThrowsAsync<GitHubCoordinationException>(async () =>
                    await store.VerifyObservedProposalIntegrityAsync(generation, stop.Token));
                Assert.Equal(GitHubFailureCode.Cancelled, failure.TransportFailure!.Code);
                remote.BeforeRead = null;
            }
            else Assert.False(await store.VerifyObservedProposalIntegrityAsync(changed, default));
            remote.RejectRead = path => path.EndsWith("/git/commits/" + generation.OriginalBaseOid, StringComparison.Ordinal)
                ? HttpStatusCode.Forbidden : null;
            await Assert.ThrowsAsync<GitHubCoordinationException>(async () =>
                await store.VerifyObservedProposalIntegrityAsync(generation, default));
            remote.RejectRead = null;
        });
    }

    [Theory]
    [InlineData("head")]
    [InlineData("repository")]
    [InlineData("actor")]
    [InlineData("current-state")]
    [InlineData("reopened")]
    public async Task Observed_content_warm_payloads_never_replace_current_platform_authority(string change)
    {
        await WithObservedContent(async (remote, client, store, generation) =>
        {
            if (change == "reopened") remote.Lifecycle("merged");
            var first = await GitHubCampaignObserver.ObserveCheckpointAsync(client, store, default);
            Assert.Null(first.Failure);
            var before = remote.Attempts.Count;
            if (change == "head") remote.Git.Refs[generation.Ref] = generation.OriginalBaseOid;
            else if (change == "repository") remote.Git.RepositoryId = 99;
            else if (change == "actor") remote.Prs[0]["user"]!["id"] = 777;
            else if (change == "reopened") remote.Lifecycle("draft");
            else
            {
                var document = CurrentCheckpointDocument(remote, Branch.Authority(remote.Git));
                var oid = GitHubCoordinationObjects.Prepare(document).BlobOid;
                remote.RejectRead = path => path.EndsWith("/git/blobs/" + oid, StringComparison.Ordinal)
                    ? HttpStatusCode.Forbidden : null;
            }
            var result = await GitHubCampaignObserver.ObserveCheckpointAsync(client, store, default);
            Assert.True(remote.Attempts.Count > before);
            if (change == "reopened")
            {
                Assert.Null(result.Failure);
                Assert.Equal(GitHubCampaignKind.Active, result.Kind);
                Assert.Equal(GitHubCampaignPullRequestKind.OpenDraft, Assert.Single(result.PullRequests).Kind);
            }
            else Assert.NotNull(result.Failure);
            remote.RejectRead = null;
        });
    }

    private static async Task WithObservedContent(Func<Remote, GitHubApiClient, GitHubCoordinationStore,
        GitHubCampaignGeneration, Task> verify)
    {
        var remote = new Remote { ServerNow = DateTimeOffset.UtcNow, ExpectedPublisher = GitHubPublicationPrincipal.ActionsBot };
        var artifact = GitHubCoordinationRefTests.PublicationCheckpointFixture("campaign");
        var file = Assert.Single(artifact.State.CandidateObservation!.ChangedFiles);
        var original = Assert.Single(Enumerable.Range(0, 2).Select(index =>
            Encoding.UTF8.GetBytes($"public void Operation{index:D3}(string value) {{ }}")),
            bytes => Convert.ToHexStringLower(SHA256.HashData(bytes)) == file.OriginalFileSha256);
        remote.Git.SetCheckpointFixtureFile(file.Path, original);
        var authority = GitHubPublicationTestBatch.BindCheckpoint(Branch.Authority(remote.Git), artifact);
        var payload = GitHubPublicationFactory.CreatePayload(authority,
            [new(file.Path, Encoding.UTF8.GetBytes(file.Path + ":candidate"))]);
        using var hook = (IDisposable)typeof(GitHubTransportTestHook).GetMethod("Register", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [new Uri("http://127.0.0.1:43219/"), new Handler(remote), 30_000])!;
        using var client = GitHubApiClient.CreateCheckpoint(ValidatedGitHubCheckpointAuthority.Create(
            authority.RepositoryOwner, authority.RepositoryName, authority.TargetRef,
            authority.CampaignLineage, authority.ExpectedBaseCommitOid), GitHubTransportTestHook.Placeholder);
        await using var store = GitHubCoordinationStore.Create(client);
        Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
        SeedTerminalCheckpoint(remote, authority, artifact);
        await store.WithPublicationAsync(async () =>
        {
            client.BindPublication(authority);
            try
            {
                var reconciler = GitHubPublicationReconciler.Create(client, GitHubPublicationPrincipal.ActionsBot, store);
                var result = await reconciler.PublishObservedAsync(authority, payload);
                if (result.Result.Kind == GitHubPublicationResultKind.RecoveredRefPartial)
                    result = await reconciler.PublishObservedAsync(authority, payload);
                Assert.Equal(GitHubPublicationResultKind.Published, result.Result.Kind);
                return true;
            }
            finally { client.UnbindPublication(); }
        }, default);
        var target = (await client.GetRefAsync(authority.TargetRef)).Value!;
        var head = remote.Git.Refs[GitHubPublicationFactory.CreateCoordinationRef(authority)];
        var read = await store.ReadObservedStateAsync(client.AuthenticatedRepository!, target, head, default);
        var generation = Assert.IsType<GitHubCampaignGeneration>(store.ObservedCurrentGeneration(read.State!));
        await verify(remote, client, store, generation);
    }
}
