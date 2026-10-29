using System.Collections.Immutable;
using System.Net;
using ContractScribe.Core;
using ContractScribe.GitHub.Coordination;
using ContractScribe.GitHub.GitData;
using ContractScribe.GitHub.Transport;

namespace ContractScribe.Tests;

public sealed partial class GitHubCoordinationRefTests
{
    [Theory]
    [InlineData("complete")]
    [InlineData("failed-prefix")]
    [InlineData("cancel-prefix")]
    [InlineData("cold-client")]
    [InlineData("wrong-expected-hash")]
    public async Task Owned_expected_content_proof_shares_only_complete_actual_raw_objects(string fault)
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
            var bytes = "fully bounded candidate content"u8.ToArray();
            var blob = GitHubProposalObjects.ObjectOid("blob", bytes);
            ImmutableArray<GitHubTreeEntry> entries = [new("file.txt", GitHubTreeMode.File, blob, null)];
            var tree = GitHubProposalObjects.TreeOid(entries);
            var expected = GitHubProposalObjects.Commit(tree, remote.TargetHead, new string('a', 64), new string('b', 64));
            remote.SeedProposalContent(expected, bytes, entries);
            var refs = remote.RefMutationAttempts;
            ImmutableArray<GitHubCoordinationChangedFile> files = [new("file.txt", GitHubProposalObjects.Sha256(bytes))];
            using var stopped = new CancellationTokenSource();
            handler.AfterResponse = (request, response) =>
            {
                if (fault == "cancel-prefix" && request.RequestUri!.AbsolutePath.EndsWith("/git/blobs/" + blob, StringComparison.Ordinal))
                    stopped.Cancel();
                return Task.FromResult(response);
            };
            var supplied = fault == "wrong-expected-hash" ? expected with { ExpectedOid = new string('f', 40) } : expected;
            var suppliedFiles = fault == "failed-prefix" ? ImmutableArray.Create(new GitHubCoordinationChangedFile("file.txt", new string('e', 64))) : files;
            if (fault == "cancel-prefix")
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.VerifyExpectedProposalIntegrityAsync(
                    supplied, suppliedFiles, null, stopped.Token).AsTask());
            else
            {
                var result = await store.VerifyExpectedProposalIntegrityAsync(supplied, suppliedFiles, null, default);
                Assert.Equal(fault is "complete" or "cold-client" ? (GitHubProposalFailureKind?)null : GitHubProposalFailureKind.Integrity, result);
            }
            var blockedReads = 0;
            handler.AfterResponse = (request, response) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/git/commits/" + expected.ExpectedOid, StringComparison.Ordinal)
                    || path.EndsWith("/git/trees/" + tree, StringComparison.Ordinal)
                    || path.EndsWith("/git/blobs/" + blob, StringComparison.Ordinal))
                {
                    blockedReads++;
                    response.Dispose();
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
                }
                return Task.FromResult(response);
            };
            try
            {
                Assert.NotNull((await store.ReadClaimAsync(claim.State!)).Guard); // Actual fresh Contents/owner gate.
                if (fault is "complete" or "cold-client")
                {
                    Assert.Null(await store.VerifyExpectedProposalIntegrityAsync(expected, files, null, default));
                    Assert.NotNull((await store.ReadVerifiedProposalCommitAsync(expected, default)).Value);
                    Assert.NotNull((await store.ReadVerifiedProposalTreeAsync(tree, default)).Value);
                    Assert.Equal(0, blockedReads);
                    if (fault == "cold-client")
                    {
                        using var otherHandler = new CheckpointInterceptHandler(remote) { AfterResponse = handler.AfterResponse };
                        using var otherClient = CheckpointClient(remote, handler: otherHandler);
                        Assert.NotNull((await otherClient.GetRepositoryAsync()).Value);
                        await using var otherStore = GitHubCoordinationStore.Create(otherClient);
                        await Assert.ThrowsAsync<GitHubCoordinationException>(() => otherStore.VerifyExpectedProposalIntegrityAsync(expected, files, null, default).AsTask());
                        Assert.True(blockedReads > 0);
                    }
                }
                else
                {
                    await Assert.ThrowsAsync<GitHubCoordinationException>(() => store.VerifyExpectedProposalIntegrityAsync(expected, files, null, default).AsTask());
                    Assert.True(blockedReads > 0); // No failed/cancelled prefix was promoted.
                }
                Assert.Equal(refs, remote.RefMutationAttempts); // Content proof never grants or executes a ref/PR effect.
            }
            finally { handler.AfterResponse = null; }
        });
    }
}
