using System.Security.Cryptography;
using ContractScribe.Core;
using ContractScribe.GitHub.Coordination;
using ContractScribe.GitHub.NativeGit;
using ContractScribe.GitHub.Transport;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed partial class GitHubCoordinationGitTransportTests
{
    [ContractScribe.TestSupport.NativeFileSystemFact]
    public async Task Large_unrelated_history_and_current_blobs_do_not_enter_coordination_stores()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var token = deadline.Token;
        var directory = Directory.CreateTempSubdirectory("contract-scribe-native-large-history-").FullName;
        try
        {
            var remote = Path.Join(directory, "remote.git");
            await Git(directory, ["init", "--bare", "--object-format=sha1", remote], token);
            await Git(remote, ["config", "http.receivepack", "true"], token);
            var file = Path.Join(directory, "unrelated.bin");
            var bytes = new byte[70 * 1024 * 1024];
            string? parent = null;
            var targetTree = "";
            var unrelated = new List<string>();
            for (var i = 0; i < 2; i++)
            {
                RandomNumberGenerator.Fill(bytes);
                await File.WriteAllBytesAsync(file, bytes, token);
                var blob = await Git(remote, ["hash-object", "-w", file], token);
                unrelated.Add(blob);
                var tree = await Git(remote, ["mktree"], token, "100644 blob " + blob + "\tlarge.bin\n");
                targetTree = tree;
                parent = await Git(remote, parent is null ? ["commit-tree", tree] : ["commit-tree", tree, "-p", parent], token, "Source revision\n");
            }
            await Git(remote, ["update-ref", "refs/heads/main", parent!], token);
            await using var server = new NativeServer(directory, token);
            var authority = GitHubPublicationFactory.CreateCampaignReadAuthority("Owner", "repo", "refs/heads/main", "campaign.git.large");
            await using var writer = await GitHubCoordinationGitTransport.CreateLoopbackAsync(authority, directory, server.Endpoint, token);
            var document = new GitHubCoordinationDocument(GitHubCoordinationDocument.CurrentFormat,
                "Owner/repo", "refs/heads/main", parent!, "campaign.git.large", GitHubCoordinationDocument.ZeroOid,
                null, null, null, null, new(new string('a', 64), 1, DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()));
            var seed = GitHubCoordinationObjects.Prepare(document);
            Assert.Equal(GitHubGitAdvance.ActualUpdated, (await writer.AdvanceAsync(new(seed, []), token)).Outcome);
            var successor = GitHubCoordinationObjects.Prepare(document with
            {
                PredecessorOid = seed.CommitOid,
                Claim = document.Claim! with { ExpiresUnixSeconds = document.Claim.ExpiresUnixSeconds + 1 }
            });
            Assert.Equal(GitHubGitAdvance.ActualUpdated, (await writer.AdvanceAsync(new(successor, []), token)).Outcome);
            await using var cold = await GitHubCoordinationGitTransport.CreateLoopbackAsync(authority, directory, server.Endpoint, token);
            Assert.Equal(successor.CommitOid, (await cold.ObserveAsync(token))!.HeadOid);
            Assert.Equal(targetTree, await cold.AcquireObservedBaseTreeAsync(parent!, token));
            // Read a real historical coordination state after cold shallow acquisition.
            var commands = cold.RemoteCommands;
            Assert.True(await cold.AcquireObservedHistoryAsync(seed.CommitOid, token));
            Assert.Equal(document, (await cold.ReadObservedAsync(seed.CommitOid, token)).Document);
            Assert.Equal(commands + 1, cold.RemoteCommands);
            Assert.True(await cold.AcquireObservedHistoryAsync(seed.CommitOid, token));
            Assert.Equal(document, (await cold.ReadObservedAsync(seed.CommitOid, token)).Document);
            Assert.Equal(commands + 1, cold.RemoteCommands);
            var renewed = GitHubCoordinationObjects.Prepare(successor.Document with
            {
                PredecessorOid = successor.CommitOid,
                Claim = successor.Document.Claim! with { ExpiresUnixSeconds = successor.Document.Claim.ExpiresUnixSeconds + 1 }
            });
            Assert.Equal(GitHubGitAdvance.ActualUpdated, (await writer.AdvanceAsync(new(renewed, []), token)).Outcome);
            foreach (var store in Directory.EnumerateDirectories(directory, "*.git", SearchOption.AllDirectories)
                .Where(path => Path.GetFileName(path) is "writer.git" or "observer.git"))
            {
                var objects = await Git(store, ["cat-file", "--batch-all-objects", "--batch-check=%(objectname)"], token);
                Assert.All(unrelated, oid => Assert.DoesNotContain(oid, objects.Split('\n')));
            }
            var acquiredBytes = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Where(path => path.Contains("contract-scribe-coordination-", StringComparison.Ordinal)).Sum(path => new FileInfo(path).Length);
            Assert.InRange(acquiredBytes, 1, 4 * 1024 * 1024);
            Assert.True(writer.RemoteCommands > 0 && cold.RemoteCommands > 0);
            Assert.Equal(0, server.NonGitRequests);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [ContractScribe.TestSupport.NativeFileSystemFact]
    public async Task Ignored_filter_exit_zero_is_not_accepted_as_a_bounded_parent_acquisition()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = deadline.Token;
        var directory = Directory.CreateTempSubdirectory("contract-scribe-native-no-filter-").FullName;
        try
        {
            var remote = Path.Join(directory, "remote.git");
            await Git(directory, ["init", "--bare", "--object-format=sha1", remote], token);
            await Git(remote, ["config", "http.receivepack", "true"], token);
            var blob = await Git(remote, ["hash-object", "-w", "--stdin"], token, "Unrelated source\n");
            var tree = await Git(remote, ["mktree"], token, "100644 blob " + blob + "\tApp.cs\n");
            var basis = await Git(remote, ["commit-tree", tree], token, "Source\n");
            await Git(remote, ["update-ref", "refs/heads/main", basis], token);
            await using var server = new NativeServer(directory, token) { AllowFilter = false };
            var authority = GitHubPublicationFactory.CreateCampaignReadAuthority("Owner", "repo", "refs/heads/main", "campaign.git.filter");
            await using var writer = await GitHubCoordinationGitTransport.CreateLoopbackAsync(authority, directory, server.Endpoint, token);
            var document = new GitHubCoordinationDocument(GitHubCoordinationDocument.CurrentFormat,
                "Owner/repo", "refs/heads/main", basis, "campaign.git.filter", GitHubCoordinationDocument.ZeroOid,
                null, null, null, null, new(new string('a', 64), 1, DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()));
            await Assert.ThrowsAsync<GitHubProtocolException>(async () => await writer.AdvanceAsync(new(GitHubCoordinationObjects.Prepare(document), []), token));
            Assert.Equal(0, server.ReceivePackRequests);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
