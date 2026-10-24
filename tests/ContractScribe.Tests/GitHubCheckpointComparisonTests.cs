using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Cli;
using ContractScribe.Core;
using ContractScribe.GitHub.Coordination;
using ContractScribe.GitHub.Transport;

namespace ContractScribe.Tests;

public sealed partial class GitHubCoordinationRefTests
{
    [Theory]
    [InlineData("clean", false, 1)]
    [InlineData("lost-ack", false, 1)]
    [InlineData("caller-pre", false, 0)]
    [InlineData("caller-read", false, 0)]
    [InlineData("caller-confirmed", false, 1)]
    [InlineData("caller-uncertain", true, 1)]
    [InlineData("expired", true, 0)]
    [InlineData("foreign-owner", true, 0)]
    [InlineData("no-checkpoint", true, 0)]
    [InlineData("permission", true, 0)]
    public async Task C7_entry_authority_loss_and_caller_cleanup_remain_distinct(string fault, bool stopped, int entryCas)
    {
        var remote = new CoordinationRemote();
        using var handler = new CheckpointInterceptHandler(remote);
        using var client = CheckpointClient(remote, handler: handler);
        var store = GitHubCoordinationStore.Create(client);
        var disposed = false;
        using var caller = new CancellationTokenSource();
        try
        {
            Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
            if (fault != "no-checkpoint") SeedPublicationCheckpoint(remote);
            if (fault == "foreign-owner") remote.AppendDocument(document => document with
            { Claim = document.Claim! with { Owner = new string('e', 64), Epoch = document.Claim.Epoch + 1 } });
            remote.ServerNow = remote.ServerNow.AddSeconds(fault == "expired" ? 301 : 68);
            var initial = remote.RefMutationAttempts;
            var applied = false;
            var callback = false;
            handler.BeforeRequestWithCancellation = (request, token) =>
            {
                if (fault == "caller-read") { caller.Cancel(); token.ThrowIfCancellationRequested(); }
                return Task.CompletedTask;
            };
            handler.AfterResponse = (request, response) =>
            {
                if (fault == "permission")
                {
                    response.Dispose();
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
                }
                if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/graphql")
                {
                    applied = true;
                    if (fault == "lost-ack") throw new HttpRequestException("private-lost-ack-marker");
                    if (fault.StartsWith("caller-", StringComparison.Ordinal)) caller.Cancel();
                }
                if (applied && fault == "caller-uncertain" && request.Method == HttpMethod.Get
                    && request.RequestUri!.AbsolutePath.Contains("/git/ref/heads/contract-scribe/", StringComparison.Ordinal))
                {
                    var node = JsonNode.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult())!;
                    node["object"]!["sha"] = Oid('f');
                    response.Content.Dispose();
                    response.Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json");
                }
                return Task.FromResult(response);
            };
            if (fault == "caller-pre") caller.Cancel();
            var exception = await Record.ExceptionAsync(() => store.WithPublicationAsync(() =>
            { callback = true; return Task.FromResult(true); }, caller.Token));
            Assert.Equal(fault is "clean" or "lost-ack", callback);
            if (callback) { Assert.Null(exception); Assert.Null(store.CheckpointDiagnostic); }
            else Assert.True(exception is OperationCanceledException or GitHubCoordinationException);
            Assert.Equal(stopped, store.CheckpointToken.IsCancellationRequested);
            Assert.Equal(initial + entryCas, remote.RefMutationAttempts);
            var first = store.CheckpointDiagnostic;
            if (fault == "caller-uncertain")
            {
                Assert.Equal(GitHubCheckpointReason.RefMismatch, first!.Reason);
                Assert.Null(first.Cancellation); // our later Stop is not the failed GET's cause
            }
            handler.AfterResponse = null;
            handler.BeforeRequestWithCancellation = null;
            await store.DisposeAsync();
            disposed = true;
            Assert.Equal(initial + entryCas + (stopped ? 0 : 1), remote.RefMutationAttempts);
            Assert.Same(first, store.CheckpointDiagnostic);
        }
        finally { if (!disposed) await store.DisposeAsync(); }
    }

    [Fact]
    public void C7_comparison_is_closed_bounded_and_cannot_change_operation_stage_or_invent_absent_predecessors()
    {
        var ack = new GitHubApiResult<GitHubAcknowledgement>(new(), null, GitHubDelivery.NeedsReadback);
        var comparison = GitHubCheckpointRefComparison.Create(Oid('1'), Oid('0'), null, ack);
        Assert.True(comparison.IsValid);
        Assert.Null(comparison.PredecessorCommit);
        Assert.Null(comparison.ObservedCommit);
        Assert.Equal(GitHubCheckpointRefRelation.Unavailable, comparison.Relation);
        var valid = new GitHubCheckpointDiagnostic(1, GitHubCheckpointOperation.Acquire, null,
            GitHubCheckpointStage.FinalRef, GitHubCheckpointReason.RefMismatch, RefComparison: comparison);
        Assert.True(valid.IsValid);
        GitHubCheckpointRefComparison[] invalid = [comparison with { ExpectedCommit = Oid('1') },
            comparison with { ExpectedCommit = new string('A', 64) }, comparison with { PredecessorCommit = "private-marker" },
            comparison with { ObservedCommit = "private-marker" }, comparison with { Relation = (GitHubCheckpointRefRelation)999 },
            comparison with { Delivery = GitHubDelivery.Read }, comparison with { Acknowledged = false },
            comparison with { Relation = GitHubCheckpointRefRelation.Expected },
            comparison with { Relation = GitHubCheckpointRefRelation.Predecessor },
            comparison with { Relation = GitHubCheckpointRefRelation.Other },
            comparison with { ObservedCommit = new string('a', 64) }];
        Assert.All(invalid, rejected => Assert.False((valid with { RefComparison = rejected }).IsValid));
        Assert.False((valid with { Stage = GitHubCheckpointStage.Gate }).IsValid);
        var unavailable = valid with { RefComparison = null };
        Assert.True(unavailable.IsValid); // absence is not an assertion of equality
    }

    [Theory]
    [InlineData("predecessor", false)]
    [InlineData("foreign", false)]
    [InlineData("foreign", true)]
    [InlineData("missing", false)]
    [InlineData("permission", false)]
    [InlineData("malformed", false)]
    [InlineData("noncommit", false)]
    public async Task C7_entry_renewal_failure_stops_before_cleanup_and_retains_its_comparison(string fault, bool lostAck)
    {
        var remote = new CoordinationRemote();
        using var handler = new CheckpointInterceptHandler(remote);
        using var client = CheckpointClient(remote, handler: handler);
        var store = GitHubCoordinationStore.Create(client);
        var disposed = false;
        try
        {
            Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
            SeedPublicationCheckpoint(remote);
            var prior = remote.CoordinationHead!;
            var initialCas = remote.RefMutationAttempts;
            var refGets = 0;
            var injected = false;
            var callback = false;
            string? expected = null;
            string? observed = null;
            remote.ServerNow = remote.ServerNow.AddSeconds(68);
            handler.AfterResponse = (request, response) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (request.Method == HttpMethod.Post && path == "/graphql")
                {
                    expected = remote.CoordinationHead;
                    if (lostAck) throw new HttpRequestException("private-lost-ack-marker");
                }
                if (expected is not null && request.Method == HttpMethod.Get
                    && path.Contains("/git/ref/heads/contract-scribe/", StringComparison.Ordinal))
                {
                    refGets++;
                    injected = true;
                    if (fault is "missing" or "permission")
                    {
                        response.Dispose();
                        return Task.FromResult(new HttpResponseMessage(fault == "missing" ? HttpStatusCode.NotFound : HttpStatusCode.Forbidden));
                    }
                    var node = JsonNode.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult())!;
                    if (fault == "malformed") node = new JsonObject();
                    else if (fault == "noncommit") node["object"]!["type"] = "tag";
                    else node["object"]!["sha"] = observed = fault == "predecessor" ? prior : Oid('f');
                    response.Content.Dispose();
                    response.Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json");
                }
                return Task.FromResult(response);
            };
            await Assert.ThrowsAsync<GitHubCoordinationException>(() => store.WithPublicationAsync(() =>
            { callback = true; return Task.FromResult(true); }, store.CheckpointToken));
            Assert.True(injected);
            Assert.False(callback);
            Assert.True(store.CheckpointToken.IsCancellationRequested);
            Assert.Equal(initialCas + 1, remote.RefMutationAttempts);
            Assert.Equal(1, refGets);
            var first = Assert.IsType<GitHubCheckpointDiagnostic>(store.CheckpointDiagnostic);
            Assert.Equal(GitHubCheckpointOperation.Renew, first.Operation);
            Assert.Equal(GitHubCheckpointStage.FinalRef, first.Stage);
            Assert.Null(first.AttemptedRevision);
            Assert.Null(first.Cancellation);
            AssertComparison(first, expected!, prior, observed,
                fault == "predecessor" ? "Predecessor" : fault == "foreign" ? "Other" : "Unavailable",
                lostAck ? "Ambiguous" : "NeedsReadback", !lostAck);
            // Current Core cannot obtain a writer, a later observer cannot
            // replace the original first cause, and disposal cannot add a CAS.
            var read = await CampaignCheckpointAcceptance.AcceptCurrentAsync(store, store.CheckpointToken);
            Assert.Equal(CampaignCheckpointAcceptanceKind.Cancelled, read.Kind);
            Assert.Null(read.AcceptedCheckpoint);
            handler.AfterResponse = null;
            await store.DisposeAsync();
            disposed = true;
            Assert.Equal(initialCas + 1, remote.RefMutationAttempts);
            Assert.Same(first, store.CheckpointDiagnostic);
        }
        finally
        {
            // Dispose is deliberately called once even if a RED assertion fails.
            if (!disposed) await store.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("predecessor")]
    [InlineData("foreign")]
    public async Task C7_write_failure_compares_its_own_prepared_and_observed_commits(string fault)
    {
        var remote = new CoordinationRemote();
        using var handler = new CheckpointInterceptHandler(remote);
        using var client = CheckpointClient(remote, handler: handler);
        await using var store = GitHubCoordinationStore.Create(client);
        Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
        SeedPublicationCheckpoint(remote);
        var before = PublicationCheckpointFixture("campaign-1");
        var next = CampaignStateReducer.Stop(before, CampaignTerminalKind.Cancelled);
        var prior = remote.CoordinationHead!;
        string? expected = null;
        var observed = fault == "predecessor" ? prior : Oid('f');
        handler.AfterResponse = (request, response) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/graphql") expected = remote.CoordinationHead;
            if (expected is not null && request.Method == HttpMethod.Get
                && request.RequestUri!.AbsolutePath.Contains("/git/ref/heads/contract-scribe/", StringComparison.Ordinal))
            {
                var node = JsonNode.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult())!;
                node["object"]!["sha"] = observed;
                response.Content.Dispose();
                response.Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json");
            }
            return Task.FromResult(response);
        };
        var result = await CampaignCheckpointAcceptance.AcceptAsync(store, next);
        Assert.Equal(CampaignCheckpointAcceptanceKind.PublicationFailure, result.Kind);
        var first = Assert.IsType<GitHubCheckpointDiagnostic>(store.CheckpointDiagnostic);
        Assert.Equal(next.Artifact.CheckpointRevision, first.AttemptedRevision);
        AssertComparison(first, expected!, prior, observed, fault == "predecessor" ? "Predecessor" : "Other", "NeedsReadback", true);
    }

    private static void AssertComparison(GitHubCheckpointDiagnostic first, string expected, string? predecessor,
        string? observed, string relation, string delivery, bool acknowledged)
    {
        static string? Fingerprint(string? oid) => oid is null ? null : Convert.ToHexStringLower(
            SHA256.HashData(Encoding.ASCII.GetBytes("contract-scribe.checkpoint.commit\0" + oid)));
        var usage = new InvocationUsage();
        usage.ObserveCheckpoint(first);
        var result = GitHubProposalPresentation.WithUsage(GitHubProposalPresentation.Campaign(CliBuildIdentity.Current,
            new("execution", CampaignOperation.Resume, "campaign.state-publication-failure", 0)), usage);
        using var json = JsonDocument.Parse(result.StandardOutput);
        var failure = json.RootElement.GetProperty("invocationUsage").GetProperty("checkpointFailure");
        Assert.True(failure.TryGetProperty("refComparison", out var comparison));
        Assert.Equal(Fingerprint(expected), comparison.GetProperty("expectedCommit").GetString());
        Assert.Equal(Fingerprint(predecessor), comparison.GetProperty("predecessorCommit").GetString());
        Assert.Equal(Fingerprint(observed), comparison.GetProperty("observedCommit").GetString());
        Assert.Equal(relation, comparison.GetProperty("relation").GetString());
        Assert.Equal(delivery, comparison.GetProperty("delivery").GetString());
        Assert.Equal(acknowledged, comparison.GetProperty("acknowledged").GetBoolean());
        Assert.DoesNotContain(expected, result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("private-lost-ack-marker", result.StandardOutput, StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(result.StandardOutput) <= 16 * 1024);
    }
}
