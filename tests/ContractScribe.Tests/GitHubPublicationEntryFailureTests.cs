using System.Diagnostics;
using System.Net;
using ContractScribe.Cli;
using ContractScribe.Core;
using ContractScribe.GitHub.Coordination;
using ContractScribe.GitHub.Transport;
using Branch = ContractScribe.Tests.GitHubProposalBranchTests;

namespace ContractScribe.Tests;

public sealed partial class GitHubPublicationReconcilerTests
{
    [Theory]
    [InlineData("host", false)]
    [InlineData("caller", false)]
    [InlineData("owner", false)]
    [InlineData("host", true)]
    [InlineData("caller", true)]
    [InlineData("owner", true)]
    public async Task Reconciler_entry_retains_original_cancellation_source(string source, bool afterRefPartial)
    {
        var remote = new Remote();
        var authority = Branch.Authority(remote.Git);
        using var session = new Session(remote, authority);
        var clock = new GitHubPublicationFinalizationTests.DeadlineClock();
        using var caller = new CancellationTokenSource();
        using var owner = new CancellationTokenSource();
        using var host = new GitHubPublicationFinalization(caller.Token, clock);
        var token = host.Begin(owner.Token);
        session.Client.BindFinalizationDeadline(host.DeadlineToken, () => host.TimedOut);
        if (afterRefPartial)
            Assert.Equal(GitHubPublicationResultKind.RecoveredRefPartial, (await session.Observe(token)).Result.Kind);
        var requests = remote.Attempts.Count;
        var writes = remote.Writes;
        if (source == "caller") caller.Cancel();
        else if (source == "owner") owner.Cancel();
        clock.Expire();
        var result = await session.Observe(token);
        Assert.Equal(source == "host" ? GitHubPublicationResultKind.Timeout : GitHubPublicationResultKind.Cancelled,
            result.Result.Kind);
        Assert.Equal(requests, remote.Attempts.Count);
        Assert.Equal(writes, remote.Writes);
        Assert.Equal(0, remote.PrAttempts);
    }

    [Theory]
    [InlineData("created-in-pass", 403)]
    [InlineData("created-in-pass", 404)]
    [InlineData("replayed-content", 403)]
    [InlineData("replayed-content", 404)]
    [InlineData("replayed-ref", 403)]
    [InlineData("replayed-ref", 404)]
    public async Task Later_PR_preflight_failure_retains_authenticated_resource(string path, int status)
    {
        var remote = new Remote();
        var authority = Branch.Authority(remote.Git);
        var injected = false;
        var writesAtFailure = -1;
        if (path != "replayed-ref") remote.BeforeRequest = (request, _) =>
        {
            if (request.Method != HttpMethod.Get || request.RequestUri!.AbsolutePath != "/repos/Owner/repo/pulls"
                || !new StackTrace().GetFrames().Any(frame => frame.GetMethod()?.DeclaringType?.FullName?
                    .Contains("AdvanceProposal", StringComparison.Ordinal) == true)
                || remote.State(authority).Stage != GitHubCoordinationStage.ContentCreated) return Task.CompletedTask;
            injected = true;
            writesAtFailure = remote.Writes;
            remote.RejectRead = name => name == "/repos/Owner/repo/pulls" ? (HttpStatusCode)status : null;
            return Task.CompletedTask;
        };
        using var session = new Session(remote, authority);
        var result = await session.Observe();
        if (path != "created-in-pass")
        {
            if (path == "replayed-ref")
                Assert.Equal(GitHubPublicationResultKind.RecoveredRefPartial, result.Result.Kind);
            else
            {
                Assert.True(injected);
                Assert.Equal(GitHubCoordinationStage.ContentCreated, remote.State(authority).Stage);
            }
            remote.BeforeRequest = null;
            injected = false;
            remote.RejectRead = name =>
            {
                if (name != "/repos/Owner/repo/pulls") return null;
                injected = true;
                writesAtFailure = remote.Writes;
                return (HttpStatusCode)status;
            };
            using var replay = new Session(remote, authority);
            result = await replay.Observe();
        }
        Assert.True(injected);
        Assert.Equal(status == 403 ? GitHubPublicationResultKind.Permission : GitHubPublicationResultKind.Conflict,
            result.Result.Kind);
        Assert.Equal(status, result.Diagnostic!.TransportHttpStatus);
        Assert.Equal(writesAtFailure, remote.Writes);
        Assert.Equal(0, remote.PrAttempts);
        var expected = remote.State(authority).ContentCommitOid;
        Assert.NotNull(expected);
        Assert.True(remote.Git.Commits.ContainsKey(expected));
        if (path == "replayed-ref")
        {
            var residual = Assert.IsType<GitHubPublicationRefResidual>(result.Result.RefResidual);
            Assert.Equal(expected, residual.ObservedOid);
            Assert.Equal(remote.Git.Refs[residual.RefName], residual.ObservedOid);
            Assert.Null(result.Result.ContentResidual);
        }
        else
        {
            var residual = Assert.IsType<GitHubPublicationContentResidual>(result.Result.ContentResidual);
            Assert.Equal(expected, residual.ExpectedOid);
            Assert.Null(result.Result.RefResidual);
        }
        Assert.NotNull(result.Result.RemoteFailure);
    }
}
