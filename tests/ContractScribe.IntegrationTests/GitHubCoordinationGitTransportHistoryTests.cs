using System.Security.Cryptography;
using ContractScribe.Core;
using ContractScribe.GitHub.Coordination;
using ContractScribe.GitHub.NativeGit;
using ContractScribe.GitHub.Transport;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed partial class GitHubCoordinationGitTransportTests
{
    [ContractScribe.TestSupport.NativeFileSystemFact]
    public async Task Exact_source_objects_are_fetched_without_descendants_and_reused_only_as_immutable_bytes()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = deadline.Token;
        var directory = Directory.CreateTempSubdirectory("contract-scribe-native-source-").FullName;
        try
        {
            var remote = Path.Join(directory, "remote.git");
            await Git(directory, ["init", "--bare", "--object-format=sha1", remote], token);
            var blob = await Git(remote, ["hash-object", "-w", "--stdin"], token, "Selected source\n");
            var other = await Git(remote, ["hash-object", "-w", "--stdin"], token, "Unrelated source\n");
            var tree = await Git(remote, ["mktree"], token, "100644 blob " + blob + "\tApp.cs\n100644 blob " + other + "\tOther.cs\n");
            var basis = await Git(remote, ["commit-tree", tree], token, "Source\n");
            await Git(remote, ["update-ref", "refs/heads/main", basis], token);
            await using var server = new NativeServer(directory, token);
            var authority = GitHubPublicationFactory.CreateCampaignReadAuthority("Owner", "repo", "refs/heads/main", "campaign.git.source");
            await using var reader = await GitHubCoordinationGitTransport.CreateLoopbackAsync(authority, directory, server.Endpoint, token);
            Assert.Equal(tree, await reader.AcquireObservedBaseTreeAsync(basis, token));
            Assert.Equal(2, (await reader.ReadSourceTreeAsync(tree, token)).Entries.Length);
            Assert.Equal("Selected source\n", System.Text.Encoding.UTF8.GetString((await reader.ReadSourceBlobAsync(blob, token)).Bytes.AsSpan()));
            var commands = reader.RemoteCommands;
            Assert.Equal(tree, (await reader.ReadSourceTreeAsync(tree, token)).Oid);
            Assert.Equal(blob, (await reader.ReadSourceBlobAsync(blob, token)).Oid);
            Assert.Equal(commands, reader.RemoteCommands);
            foreach (var store in Directory.EnumerateDirectories(directory, "*.git", SearchOption.AllDirectories)
                .Where(path => Path.GetFileName(path) is "writer.git" or "observer.git"))
            {
                var objects = await Git(store, ["cat-file", "--batch-all-objects", "--batch-check=%(objectname)"], token);
                Assert.DoesNotContain(other, objects.Split('\n'));
                if (Path.GetFileName(store) == "writer.git") Assert.DoesNotContain(blob, objects.Split('\n'));
            }
            Assert.Equal(0, server.ReceivePackRequests);
            var document = new GitHubCoordinationDocument(GitHubCoordinationDocument.CurrentFormat,
                "Owner/repo", "refs/heads/main", basis, "campaign.git.source", GitHubCoordinationDocument.ZeroOid,
                null, null, null, null, new(new string('a', 64), 1, DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()));
            var pending = GitHubCoordinationObjects.Prepare(document);
            await reader.MaterializeAsync(new(pending, []), token);
            server.DenyReads = true;
            await Assert.ThrowsAsync<GitHubProtocolException>(async () => await reader.ReadSourceTreeAsync(pending.RootTreeOid, token));
            server.DenyReads = false;
            // Unsupported filters must not silently widen a later cold read.
            await using var unsupported = await GitHubCoordinationGitTransport.CreateLoopbackAsync(authority, directory, server.Endpoint, token);
            server.AllowFilter = false;
            await Assert.ThrowsAsync<GitHubProtocolException>(async () => await unsupported.ReadSourceTreeAsync(tree, token));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [ContractScribe.TestSupport.NativeFileSystemFact]
    public async Task Real_git_compressed_shallow_RPC_requests_reach_the_strict_backend_unchanged()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var token = deadline.Token;
        var directory = Directory.CreateTempSubdirectory("contract-scribe-native-gzip-").FullName;
        try
        {
            var remote = Path.Join(directory, "remote.git");
            await Git(directory, ["init", "--bare", "--object-format=sha1", remote], token);
            await Git(remote, ["config", "http.receivepack", "true"], token);
            var tree = await Git(remote, ["mktree"], token, "");
            var basis = await Git(remote, ["commit-tree", tree], token, "Source\n");
            await Git(remote, ["update-ref", "refs/heads/main", basis], token);
            await using var server = new NativeServer(directory, token);
            var authority = GitHubPublicationFactory.CreateCampaignReadAuthority("Owner", "repo", "refs/heads/main", "campaign.git.gzip");
            await using var writer = await GitHubCoordinationGitTransport.CreateLoopbackAsync(authority, directory, server.Endpoint, token);
            var document = new GitHubCoordinationDocument(GitHubCoordinationDocument.CurrentFormat,
                "Owner/repo", "refs/heads/main", basis, "campaign.git.gzip", GitHubCoordinationDocument.ZeroOid,
                null, null, null, null, new(new string('a', 64), 1, DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()));
            for (var i = 0; i < 40 && server.GzipRequests == 0; i++)
            {
                var prepared = GitHubCoordinationObjects.Prepare(document);
                Assert.Equal(GitHubGitAdvance.ActualUpdated, (await writer.AdvanceAsync(new(prepared, []), token)).Outcome);
                document = document with { PredecessorOid = prepared.CommitOid, Claim = document.Claim! with { ExpiresUnixSeconds = document.Claim.ExpiresUnixSeconds + 1 } };
            }
            Assert.True(server.GzipRequests > 0);
            Assert.Equal(0, server.NonGitRequests);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

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
            var sourceCommits = new List<(string Commit, string Tree)>();
            for (var i = 0; i < 2; i++)
            {
                RandomNumberGenerator.Fill(bytes);
                await File.WriteAllBytesAsync(file, bytes, token);
                var blob = await Git(remote, ["hash-object", "-w", file], token);
                unrelated.Add(blob);
                var tree = await Git(remote, ["mktree"], token, "100644 blob " + blob + "\tlarge.bin\n");
                targetTree = tree;
                parent = await Git(remote, parent is null ? ["commit-tree", tree] : ["commit-tree", tree, "-p", parent], token, "Source revision\n");
                sourceCommits.Add((parent, tree));
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
            // Reusing a local observed ref must also permit older or unrelated
            // metadata. This is data acquisition, never a remote CAS update.
            Assert.Equal(sourceCommits[0].Tree, await cold.AcquireObservedBaseTreeAsync(sourceCommits[0].Commit, token));
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
            Assert.Equal(targetTree, (await cold.ReadSourceTreeAsync(targetTree, token)).Oid);
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
