using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ContractScribe.Core;
using ContractScribe.GitHub.Coordination;
using ContractScribe.GitHub.NativeGit;
using ContractScribe.GitHub.Transport;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed class GitHubCoordinationGitTransportTests
{
    [Fact]
    public async Task Native_data_heads_observe_remote_changes_and_permission_loss_without_inventing_identity()
    {
        if (OperatingSystem.IsWindows()) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = deadline.Token;
        var directory = Directory.CreateTempSubdirectory("contract-scribe-native-data-test-").FullName;
        try
        {
            var remote = Path.Join(directory, "remote.git");
            await Git(directory, ["init", "--bare", "--object-format=sha1", remote], token);
            await Git(remote, ["config", "http.receivepack", "true"], token);
            var tree = await Git(remote, ["mktree"], token, "");
            var basis = await Git(remote, ["commit-tree", tree], token, "Synthetic base\n");
            await Git(remote, ["update-ref", "refs/heads/main", basis], token);
            await using var server = new NativeServer(directory, token);
            var authority = GitHubPublicationFactory.CreateCampaignReadAuthority("Owner", "repo", "refs/heads/main", "campaign.git.native.data");
            await using var reader = await GitHubCoordinationGitTransport.CreateLoopbackAsync(authority, directory, server.Endpoint, token);
            Assert.Null(await reader.ReadObservedCommitTreeAsync(basis, token));
            Assert.Equal(basis, await reader.ReadDataHeadAsync(authority.TargetRef, token));
            var proposal = "refs/heads/contract-scribe/proposals/" + new string('a', 64) + "/" + new string('b', 64);
            Assert.Null(await reader.ReadDataHeadAsync(proposal, token));
            await Git(remote, ["update-ref", proposal, basis], token);
            Assert.Equal(basis, await reader.ReadDataHeadAsync(proposal, token));
            var successor = await Git(remote, ["commit-tree", tree, "-p", basis], token, "Synthetic successor\n");
            await Git(remote, ["update-ref", authority.TargetRef, successor, basis], token);
            Assert.Equal(successor, await reader.ReadDataHeadAsync(authority.TargetRef, token));
            var commands = reader.RemoteCommands;
            await Assert.ThrowsAsync<GitHubProtocolException>(async () => await reader.ReadDataHeadAsync("refs/heads/unrelated", token));
            Assert.Equal(commands, reader.RemoteCommands);
            server.DenyReads = true;
            await Assert.ThrowsAsync<GitHubProtocolException>(async () => await reader.ReadDataHeadAsync(authority.TargetRef, token));
            await Assert.ThrowsAsync<GitHubProtocolException>(async () => await reader.ReadDataHeadAsync(proposal, token));
            Assert.True(reader.RemoteCommands > commands);
            Assert.Equal(0, server.ReceivePackRequests);
            Assert.Equal(0, server.NonGitRequests);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("updated")]
    [InlineData("stale")]
    [InlineData("already-desired")]
    [InlineData("lost-reply")]
    [InlineData("parent-cancel")]
    public async Task Exact_native_update_requires_its_own_acknowledgement_and_independent_remote_bytes(string outcome)
    {
        if (OperatingSystem.IsWindows()) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = deadline.Token;
        var directory = Directory.CreateTempSubdirectory("contract-scribe-native-git-test-").FullName;
        try
        {
            var remote = Path.Join(directory, "remote.git");
            await Git(directory, ["init", "--bare", "--object-format=sha1", remote], token);
            await Git(remote, ["config", "http.receivepack", "true"], token);
            var blob = await Git(remote, ["hash-object", "-w", "--stdin"], token, "synthetic base\n");
            var tree = await Git(remote, ["mktree"], token, "100644 blob " + blob + "\tApp.cs\n");
            var basis = await Git(remote, ["commit-tree", tree], token, "Synthetic base\n");
            await Git(remote, ["update-ref", "refs/heads/main", basis], token);
            await using var server = new NativeServer(directory, token);
            var authority = GitHubPublicationFactory.CreateCampaignReadAuthority("Owner", "repo", "refs/heads/main", "campaign.git.native");
            using var adapterDeadline = new CancellationTokenSource();
            await using var first = await GitHubCoordinationGitTransport.CreateLoopbackAsync(authority, directory, server.Endpoint, token, () => adapterDeadline.Token);
            if (outcome == "parent-cancel")
            {
                adapterDeadline.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await first.ObserveAsync(CancellationToken.None));
                Assert.Equal(0, first.RemoteCommands);
                return;
            }
            var document = new GitHubCoordinationDocument(GitHubCoordinationDocument.CurrentFormat,
                "Owner/repo", "refs/heads/main", basis, "campaign.git.native", GitHubCoordinationDocument.ZeroOid,
                null, null, null, null, new(new string('a', 64), 1, DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()));
            var seed = GitHubCoordinationObjects.Prepare(document);
            var seeded = await first.AdvanceAsync(new(seed, []), token);
            Assert.Equal(GitHubGitAdvance.ActualUpdated, seeded.Outcome);
            Assert.Equal(seed.CommitOid, seeded.Observation!.HeadOid);
            await using var second = await GitHubCoordinationGitTransport.CreateLoopbackAsync(authority, directory, server.Endpoint, token);
            var before = await second.ObserveAsync(token);
            Assert.True(seed.StateBytes.AsSpan().SequenceEqual(before!.Blobs[GitHubCoordinationObjects.StatePath].Bytes.AsSpan()));
            Assert.Equal(tree, await second.ReadObservedCommitTreeAsync(basis, token));
            var intended = GitHubCoordinationObjects.Prepare(document with
            {
                PredecessorOid = seed.CommitOid,
                Claim = document.Claim! with { ExpiresUnixSeconds = document.Claim.ExpiresUnixSeconds + 1 }
            });
            if (outcome == "stale")
            {
                var winner = GitHubCoordinationObjects.Prepare(document with
                {
                    PredecessorOid = seed.CommitOid,
                    Claim = new(new string('b', 64), 2, document.Claim!.ExpiresUnixSeconds + 1)
                });
                Assert.Equal(GitHubGitAdvance.ActualUpdated, (await first.AdvanceAsync(new(winner, []), token)).Outcome);
            }
            else if (outcome == "already-desired")
                Assert.Equal(GitHubGitAdvance.ActualUpdated, (await first.AdvanceAsync(new(intended, []), token)).Outcome);
            server.DropNextMutationReply = outcome == "lost-reply";
            var result = await second.AdvanceAsync(new(intended, []), token);
            Assert.Equal(outcome == "updated" ? GitHubGitAdvance.ActualUpdated : GitHubGitAdvance.Observer, result.Outcome);
            Assert.NotNull(result.Observation);
            if (outcome != "stale") Assert.Equal(intended.CommitOid, result.Observation.HeadOid);
            else Assert.NotEqual(intended.CommitOid, result.Observation.HeadOid);
            Assert.Equal(result.Observation.HeadOid,
                await Git(remote, ["rev-parse", GitHubPublicationFactory.CreateCoordinationRef(authority)], token));
            Assert.True(server.ReceivePackRequests >= 2);
            Assert.True(first.RemoteCommands > 0 && second.RemoteCommands > 0);
            Assert.Equal(0, server.NonGitRequests);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task<string> Git(string directory, string[] arguments, CancellationToken token, string? input = null)
    {
        var start = new ProcessStartInfo("/usr/bin/git")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.Environment.Clear();
        foreach (var (key, value) in new[] { ("GIT_CONFIG_NOSYSTEM", "1"), ("GIT_CONFIG_GLOBAL", "/dev/null"),
            ("GIT_CONFIG_SYSTEM", "/dev/null"), ("GIT_TERMINAL_PROMPT", "0"),
            ("GIT_AUTHOR_NAME", "Synthetic"), ("GIT_AUTHOR_EMAIL", "synthetic@example.invalid"),
            ("GIT_COMMITTER_NAME", "Synthetic"), ("GIT_COMMITTER_EMAIL", "synthetic@example.invalid") }) start.Environment[key] = value;
        start.ArgumentList.Add("-c"); start.ArgumentList.Add("core.hooksPath=/dev/null");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), token);
        process.StandardInput.Close();
        try { await process.WaitForExitAsync(token); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        Assert.True(process.ExitCode == 0, await error);
        return (await output).TrimEnd('\n');
    }

    internal sealed class NativeServer : IAsyncDisposable
    {
        private readonly HttpListener listener = new();
        private readonly Task loop;
        private readonly string directory;
        private readonly CancellationToken token;
        internal Uri Endpoint { get; }
        internal bool DropNextMutationReply { get; set; }
        internal bool DenyReads { get; set; }
        internal int ReceivePackRequests { get; private set; }
        internal int RequestCount { get; private set; }
        internal int MutationCompletions { get; private set; }
        internal int NonGitRequests { get; private set; }

        internal NativeServer(string directory, CancellationToken token)
        {
            this.directory = directory; this.token = token;
            var reserve = new TcpListener(IPAddress.Loopback, 0); reserve.Start();
            var port = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
            listener.Prefixes.Add("http://127.0.0.1:" + port + "/"); listener.Start();
            Endpoint = new("http://127.0.0.1:" + port + "/remote.git");
            loop = ServeAsync();
        }

        private async Task ServeAsync()
        {
            try
            {
                while (listener.IsListening)
                {
                    var context = await listener.GetContextAsync().WaitAsync(token);
                    var request = context.Request;
                    RequestCount++;
                    if (!request.Url!.AbsolutePath.StartsWith("/remote.git/", StringComparison.Ordinal))
                    { NonGitRequests++; context.Response.StatusCode = 404; context.Response.Close(); continue; }
                    if (DenyReads) { context.Response.StatusCode = 403; context.Response.Close(); continue; }
                    var mutation = request.Url.AbsolutePath.EndsWith("/git-receive-pack", StringComparison.Ordinal);
                    if (mutation) ReceivePackRequests++;
                    var start = new ProcessStartInfo("/usr/bin/git")
                    {
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    start.Environment.Clear();
                    foreach (var (key, value) in new[] { ("GIT_PROJECT_ROOT", directory), ("GIT_HTTP_EXPORT_ALL", "1"),
                        ("GIT_CONFIG_NOSYSTEM", "1"), ("GIT_CONFIG_GLOBAL", "/dev/null"), ("GIT_CONFIG_SYSTEM", "/dev/null"),
                        ("PATH_INFO", request.Url.AbsolutePath), ("QUERY_STRING", request.Url.Query.TrimStart('?')),
                        ("REQUEST_METHOD", request.HttpMethod), ("CONTENT_TYPE", request.ContentType ?? ""),
                        ("CONTENT_LENGTH", request.ContentLength64.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        ("REMOTE_ADDR", "127.0.0.1") }) start.Environment[key] = value;
                    if (request.Headers["Git-Protocol"] is { } protocol) start.Environment["HTTP_GIT_PROTOCOL"] = protocol;
                    start.ArgumentList.Add("-c"); start.ArgumentList.Add("core.hooksPath=/dev/null"); start.ArgumentList.Add("http-backend");
                    using var process = Process.Start(start)!;
                    using var output = new MemoryStream();
                    var copied = process.StandardOutput.BaseStream.CopyToAsync(output, token);
                    var error = process.StandardError.ReadToEndAsync(token);
                    await request.InputStream.CopyToAsync(process.StandardInput.BaseStream, token); process.StandardInput.Close();
                    try { await process.WaitForExitAsync(token); }
                    finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
                    await copied; _ = await error; Assert.Equal(0, process.ExitCode);
                    if (mutation) MutationCompletions++;
                    if (mutation && DropNextMutationReply)
                    { DropNextMutationReply = false; context.Response.Abort(); continue; }
                    var bytes = output.ToArray();
                    var split = bytes.AsSpan().IndexOf("\r\n\r\n"u8);
                    Assert.True(split >= 0);
                    foreach (var line in Encoding.ASCII.GetString(bytes, 0, split).Split("\r\n"))
                    {
                        var colon = line.IndexOf(':');
                        var key = line[..colon]; var value = line[(colon + 1)..].Trim();
                        if (key == "Status") context.Response.StatusCode = int.Parse(value.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
                        else if (key == "Content-Type") context.Response.ContentType = value;
                    }
                    context.Response.ContentLength64 = bytes.Length - split - 4;
                    await context.Response.OutputStream.WriteAsync(bytes.AsMemory(split + 4), token);
                    context.Response.Close();
                }
            }
            catch (Exception error) when (!listener.IsListening && error is HttpListenerException or ObjectDisposedException) { }
        }

        public async ValueTask DisposeAsync()
        { listener.Close(); await loop; }
    }
}
