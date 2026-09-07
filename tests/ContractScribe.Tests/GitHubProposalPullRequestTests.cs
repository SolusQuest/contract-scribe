using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Core;
using ContractScribe.GitHub.Coordination;
using ContractScribe.GitHub.PullRequests;
using ContractScribe.GitHub.Transport;
using static ContractScribe.Tests.GitHubCoordinationRefTests;

namespace ContractScribe.Tests;

[Collection("GitHub transport hook")]
public sealed class GitHubProposalPullRequestTests
{
    private static string Oid(char c) => new(c, 40);
    private static readonly GitHubActor Publisher = new(99, "BOT_99", "github-actions[bot]", GitHubActorKind.Bot);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retained_stale_base_blocks_equality_and_allows_exact_terminal(bool persist)
    {
        using var h = await Harness.Create();
        h.Remote.BeforePost = () => h.Remote.Coordination.TargetHead = Oid('9');
        var stale = await h.Store.CreateAsync(h.State, h.Head);
        Assert.Equal(GitHubProposalOutcome.StaleDraft, stale.Outcome);
        if (persist) await h.PublishStale(stale);
        using var restart = await Harness.Create(h.Remote, h.Authority, seed: false);
        var store = persist ? restart.Store : h.Store;
        var state = persist ? restart.State : h.State;
        var previous = persist ? null : stale.Observation;
        h.Remote.Prs[0]["base"]!["sha"] = Oid('1');
        h.Remote.Coordination.TargetHead = Oid('1');
        Assert.Equal(GitHubProposalOutcome.Conflict, (await store.ObserveAsync(state, h.Head, previous)).Outcome);
        h.Remote.Prs[0]["base"]!["sha"] = Oid('9');
        h.Remote.Coordination.TargetHead = Oid('9');
        foreach (var terminal in new[] { "closed", "merged" })
        {
            Lifecycle(h.Remote.Prs[0], terminal);
            var result = await store.ObserveAsync(state, h.Head, previous);
            Assert.Equal(terminal == "closed" ? GitHubProposalOutcome.ClosedUnmerged : GitHubProposalOutcome.Merged, result.Outcome);
            h.Remote.Prs[0]["base"]!["sha"] = Oid('8');
            Assert.Equal(GitHubProposalOutcome.Conflict, (await store.ObserveAsync(state, h.Head, previous)).Outcome);
            h.Remote.Prs[0]["base"]!["sha"] = Oid('9');
            Lifecycle(h.Remote.Prs[0], "draft");
            Assert.Equal(GitHubProposalOutcome.Conflict, (await store.ObserveAsync(state, h.Head, result.Observation)).Outcome);
        }
        if (persist)
        {
            Lifecycle(h.Remote.Prs[0], "closed");
            var terminal = await restart.Coordination.AdvanceAsync(restart.State,
                GitHubCoordinationStageUpdate.Terminal(GitHubCoordinationStage.ClosedUnmerged));
            Assert.Null(terminal.Failure);
            using var final = await Harness.Create(h.Remote, h.Authority, seed: false);
            Assert.Equal(GitHubProposalOutcome.ClosedUnmerged, (await final.Store.ObserveAsync(final.State, final.Head)).Outcome);
            Lifecycle(h.Remote.Prs[0], "draft");
            Assert.Equal(GitHubProposalOutcome.Conflict, (await final.Store.ObserveAsync(final.State, final.Head)).Outcome);
        }
        Assert.Equal(1, h.Remote.Posts);
    }

    [Theory]
    [InlineData("base-ref")]
    [InlineData("marker")]
    [InlineData("author")]
    [InlineData("head")]
    public async Task Retained_stale_terminal_does_not_excuse_other_edits(string kind)
    {
        using var h = await Harness.Create();
        h.Remote.BeforePost = () => h.Remote.Coordination.TargetHead = Oid('9');
        var stale = await h.Store.CreateAsync(h.State, h.Head);
        await h.PublishStale(stale);
        Lifecycle(h.Remote.Prs[0], "closed");
        switch (kind)
        {
            case "base-ref": h.Remote.Prs[0]["base"]!["ref"] = "other"; break;
            case "marker": h.Remote.Prs[0]["body"] = "removed"; break;
            case "author": h.Remote.Prs[0]["user"]!["id"] = 100; break;
            case "head": h.Remote.Prs[0]["head"]!["sha"] = Oid('8'); break;
        }
        Assert.Equal(GitHubProposalOutcome.Conflict, (await h.Store.ObserveAsync(h.State, h.Head)).Outcome);
        Assert.Equal(1, h.Remote.Posts);
    }

    [Fact]
    public async Task Independent_recovery_deadline_is_timeout_with_possible_delivery()
    {
        using var h = await Harness.Create();
        typeof(GitHubProposalPullRequestStore).GetField("recoveryTimeout", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(h.Store, TimeSpan.FromMilliseconds(50));
        h.Remote.DelayRecovery = true;
        var result = await h.Store.CreateAsync(h.State, h.Head);
        Assert.Equal(GitHubProposalOutcome.Failed, result.Outcome);
        Assert.Equal(GitHubFailureCode.Timeout, result.Failure!.Code);
        Assert.Equal(GitHubDelivery.NeedsReadback, result.Delivery);
        Assert.Null(result.Observation);
        Assert.Equal(1, h.Remote.Posts);
    }

    [Fact]
    public async Task Caller_cancellation_after_dispatch_preserves_independent_recovery()
    {
        using var h = await Harness.Create();
        using var caller = new CancellationTokenSource();
        h.Remote.AfterPost = caller.Cancel;
        var result = await h.Store.CreateAsync(h.State, h.Head, caller.Token);
        Assert.True(caller.IsCancellationRequested);
        Assert.Equal(GitHubProposalOutcome.Appendable, result.Outcome);
        Assert.Equal(GitHubDelivery.Ambiguous, result.Delivery);
        Assert.Equal(1, h.Remote.Posts);
    }

    [Fact]
    public async Task Metadata_matches_independently_framed_literal_fixture()
    {
        using var h = await Harness.Create();
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "ContractScribe.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var fixture = JsonNode.Parse(await File.ReadAllTextAsync(Path.Join(directory!.FullName,
            "tests/fixtures/github/pull-requests/creation-metadata.json")))!;
        var metadata = h.Store.Metadata(h.State)!;
        Assert.Equal(fixture["title"]!.GetValue<string>(), metadata.Title);
        Assert.Equal(fixture["body"]!.GetValue<string>(), metadata.Body);
        Assert.Equal(fixture["creationCommitment"]!.GetValue<string>(), metadata.CreationCommitment);
        Assert.Equal(fixture["markerSha256"]!.GetValue<string>(), metadata.MarkerHash);
    }

    [Fact]
    public async Task A_detail_edit_in_the_final_read_window_cannot_escape_readback()
    {
        using var h = await Harness.Create();
        await h.Store.CreateAsync(h.State, h.Head);
        h.Remote.AfterDetail = () => h.Remote.Prs[0]["title"] = "human edit";
        Assert.Equal(GitHubProposalOutcome.Conflict, (await h.Store.ObserveAsync(h.State, h.Head)).Outcome);
        Assert.Equal(1, h.Remote.Posts);
    }

    [Fact]
    public async Task Claim_movement_after_dispatch_cannot_authorize_completion()
    {
        using var h = await Harness.Create();
        h.Remote.AfterPost = () => h.Remote.Coordination.ForceCoordinationHead(Oid('f'));
        var result = await h.Store.CreateAsync(h.State, h.Head);
        Assert.Null(result.Observation);
        Assert.Single(h.Remote.Prs);
        Assert.Equal(1, h.Remote.Posts);
    }

    [Fact]
    public async Task Create_replay_and_restart_require_exact_owned_direct_readback()
    {
        using var h = await Harness.Create();
        var metadata = h.Store.Metadata(h.State)!;
        Assert.DoesNotContain("campaign-1", metadata.Body);
        Assert.DoesNotContain("operation-initial", metadata.Body);
        Assert.DoesNotContain(h.Head.CommitOid, metadata.Body);
        var result = await h.Store.CreateAsync(h.State, h.Head);
        Assert.Equal(GitHubProposalOutcome.Appendable, result.Outcome);
        Assert.Equal(metadata.Body, result.Observation!.PullRequest.Body);
        Assert.Equal(1, h.Remote.Posts);
        Assert.Contains(h.Remote.Requests, x => x == "GET /repos/Owner/repo/pulls/17");
        var replay = await h.Store.CreateAsync(h.State, h.Head);
        Assert.Equal(GitHubProposalOutcome.Appendable, replay.Outcome);
        using var restart = await Harness.Create(h.Remote, h.Authority, seed: false);
        Assert.Equal(GitHubProposalOutcome.Appendable, (await restart.Store.RecoverAsync(restart.State, restart.Head)).Outcome);
        Assert.Equal(1, h.Remote.Posts);
        Assert.All(h.Remote.Requests.Where(x => !x.StartsWith("GET ", StringComparison.Ordinal)), x => Assert.Equal("POST /repos/Owner/repo/pulls", x));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Response_loss_with_or_without_visible_residual_never_retries(bool hide)
    {
        using var h = await Harness.Create();
        h.Remote.LoseResponse = true;
        h.Remote.HideCreated = hide;
        var result = await h.Store.CreateAsync(h.State, h.Head);
        Assert.Equal(hide ? GitHubProposalOutcome.Unresolved : GitHubProposalOutcome.Appendable, result.Outcome);
        using var restart = await Harness.Create(h.Remote, h.Authority, seed: false);
        for (var i = 0; i < 2; i++)
            Assert.Equal(result.Outcome, (await restart.Store.RecoverAsync(restart.State, restart.Head)).Outcome);
        Assert.Equal(1, h.Remote.Posts);
    }

    [Fact]
    public async Task Two_independent_callers_recover_one_owned_remote_pr()
    {
        using var first = await Harness.Create();
        using var second = await Harness.Create(first.Remote, first.Authority, seed: false);
        first.Remote.ConcurrentPosts = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var results = await Task.WhenAll(first.Store.CreateAsync(first.State, first.Head).AsTask(),
            second.Store.CreateAsync(second.State, second.Head).AsTask()).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(results, r => Assert.Equal(GitHubProposalOutcome.Appendable, r.Outcome));
        Assert.Single(first.Remote.Prs);
        Assert.Equal(2, first.Remote.Posts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Base_mismatch_is_a_conservative_create_recovery_residual(bool loss)
    {
        using var h = await Harness.Create();
        h.Remote.LoseResponse = loss;
        h.Remote.BeforePost = () => h.Remote.Coordination.TargetHead = Oid('9');
        var created = await h.Store.CreateAsync(h.State, h.Head);
        Assert.Equal(GitHubProposalOutcome.StaleDraft, created.Outcome);
        Assert.Equal(h.State.HeadOid, h.Remote.Coordination.CoordinationHead);
        using var restart = await Harness.Create(h.Remote, h.Authority, seed: false);
        Assert.Equal(GitHubProposalOutcome.StaleDraft, (await restart.Store.CreateAsync(restart.State, restart.Head)).Outcome);
        Assert.Equal(GitHubProposalOutcome.StaleDraft, (await restart.Store.RecoverAsync(restart.State, restart.Head)).Outcome);
        Assert.Equal(1, h.Remote.Posts);
    }

    [Fact]
    public async Task Retained_success_distinguishes_later_drift_and_equal_unknown_histories_agree()
    {
        using var h = await Harness.Create();
        var success = await h.Store.CreateAsync(h.State, h.Head);
        h.Remote.Prs[0]["base"]!["sha"] = Oid('9');
        Assert.Equal(GitHubProposalOutcome.Conflict, (await h.Store.RecoverAsync(h.State, h.Head, success.Observation)).Outcome);
        // Without retained success these are the same allowed observations as the inside-create race.
        Assert.Equal(GitHubProposalOutcome.StaleDraft, (await h.Store.RecoverAsync(h.State, h.Head)).Outcome);
    }

    [Theory]
    [InlineData("title")]
    [InlineData("body")]
    [InlineData("marker")]
    [InlineData("bot")]
    [InlineData("user")]
    [InlineData("head")]
    [InlineData("head-ref")]
    [InlineData("head-repo")]
    [InlineData("base-ref")]
    [InlineData("generation")]
    [InlineData("multiple")]
    [InlineData("tree")]
    [InlineData("proposal")]
    [InlineData("merge")]
    public async Task Ownership_and_authority_mismatches_never_adopt_or_create(string kind)
    {
        using var h = await Harness.Create();
        await h.Store.CreateAsync(h.State, h.Head);
        var pr = h.Remote.Prs[0];
        switch (kind)
        {
            case "title": pr["title"] = "human edit"; break;
            case "body": pr["body"] = pr["body"]!.GetValue<string>() + "human edit"; break;
            case "marker": pr["body"] = "removed"; break;
            case "bot": pr["user"]!["id"] = 100; break;
            case "user": pr["user"]!["type"] = "User"; break;
            case "head": pr["head"]!["sha"] = Oid('7'); break;
            case "head-ref": pr["head"]!["ref"] = "foreign"; break;
            case "head-repo": pr["head"]!["repo"]!["id"] = 100; break;
            case "base-ref": pr["base"]!["ref"] = "other"; break;
            case "generation": pr["body"] = pr["body"]!.GetValue<string>().Replace("generation=", "changed="); break;
            case "multiple": var clone = (JsonObject)pr.DeepClone(); clone["id"] = 18; clone["node_id"] = "PR_18"; clone["number"] = 18; h.Remote.Prs.Add(clone); break;
            case "tree": h.Remote.Tree = Oid('8'); break;
            case "proposal": h.Remote.Head = Oid('8'); break;
            case "merge": pr["merged"] = true; break;
        }
        var result = await h.Store.CreateAsync(h.State, h.Head);
        Assert.Contains(result.Outcome, new[] { GitHubProposalOutcome.Conflict, GitHubProposalOutcome.Failed });
        Assert.Null(result.Observation);
        Assert.Equal(1, h.Remote.Posts);
    }

    [Fact]
    public async Task Genuine_claim_moved_claim_foreign_store_and_pre_dispatch_cancellation()
    {
        using var h = await Harness.Create();
        using var other = await Harness.Create();
        Assert.Equal(GitHubProposalOutcome.Conflict, (await h.Store.CreateAsync(other.State, h.Head)).Outcome);
        Assert.Throws<ArgumentException>(() => GitHubProposalPullRequestStore.Create(h.Client, other.Coordination, Publisher));
        Assert.Throws<ArgumentException>(() => GitHubProposalPullRequestStore.Create(h.Client, h.Coordination, null!));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.NotEqual(GitHubProposalOutcome.Appendable, (await h.Store.CreateAsync(h.State, h.Head, cancelled.Token)).Outcome);
        h.Remote.Coordination.ForceCoordinationHead(Oid('f'));
        Assert.NotEqual(GitHubProposalOutcome.Appendable, (await h.Store.CreateAsync(h.State, h.Head)).Outcome);
        Assert.Equal(0, h.Remote.Posts);
    }

    [Theory]
    [InlineData("ready", (int)GitHubProposalOutcome.Ready)]
    [InlineData("merged", (int)GitHubProposalOutcome.Merged)]
    [InlineData("closed", (int)GitHubProposalOutcome.ClosedUnmerged)]
    public async Task Human_lifecycle_change_during_recovery_is_preserved(string kind, int expectedValue)
    {
        using var h = await Harness.Create();
        h.Remote.AfterPost = () => Lifecycle(h.Remote.Prs[0], kind);
        h.Remote.LoseResponse = true;
        var result = await h.Store.CreateAsync(h.State, h.Head);
        Assert.Equal((GitHubProposalOutcome)expectedValue, result.Outcome);
        Lifecycle(h.Remote.Prs[0], "draft");
        Assert.Equal(GitHubProposalOutcome.Conflict, (await h.Store.ObserveAsync(h.State, h.Head, result.Observation)).Outcome);
        Assert.Equal(1, h.Remote.Posts);
    }

    [Fact]
    public async Task Budget_hold_is_not_ready_history_and_real_ready_history_still_rejects_conversion()
    {
        using var h = await Harness.Create();
        var result = await h.Store.CreateAsync(h.State, h.Head);
        await h.Publish(result);
        var held = await h.Coordination.AdvanceAsync(h.State, GitHubCoordinationStageUpdate.AwaitingReview());
        Assert.Null(held.Failure);
        h.State = held.State!;
        using var restart = await Harness.Create(h.Remote, h.Authority, seed: false);
        var observed = await restart.Store.ObserveAsync(restart.State, restart.Head);
        Assert.Equal(GitHubProposalOutcome.HeldDraft, observed.Outcome);
        Assert.True(observed.Observation!.PullRequest.Draft);
        Lifecycle(h.Remote.Prs[0], "ready");
        var ready = await restart.Store.ObserveAsync(restart.State, restart.Head);
        Assert.Equal(GitHubProposalOutcome.Ready, ready.Outcome);
        Lifecycle(h.Remote.Prs[0], "draft");
        Assert.Equal(GitHubProposalOutcome.Conflict, (await restart.Store.ObserveAsync(restart.State, restart.Head, ready.Observation)).Outcome);
    }

    [Fact]
    public async Task Real_append_and_second_append_keep_original_pr_bytes_and_current_cumulative_authority()
    {
        using var initial = await Harness.Create();
        var created = await initial.Store.CreateAsync(initial.State, initial.Head);
        await initial.Publish(created);
        var body = created.Observation!.Metadata.Body;
        var authority = initial.Authority;
        for (var i = 0; i < 2; i++)
        {
            authority = AppendAuthority(authority, "append-r5-" + i, (char)('6' + i), precedingFileCandidate: (char)('5' + i));
            using var h = await Harness.Create(initial.Remote, authority, seed: false);
            var read = await h.Coordination.ReadCurrentAsync();
            var claim = await h.Coordination.ClaimAsync(read.Read!);
            var head = Oid((char)('4' + i));
            var content = await h.Coordination.AdvanceAsync(claim.State!, GitHubCoordinationStageUpdate.ContentCreated(head));
            var advanced = await h.Coordination.AdvanceAsync(content.State!, GitHubCoordinationStageUpdate.ProposalRefAdvanced(head, Oid('3')));
            Assert.Null(advanced.Failure);
            h.State = advanced.State!;
            h.Remote.Head = head;
            h.Remote.Prs[0]["head"]!["sha"] = head;
            var observed = await h.Store.ObserveAsync(h.State, h.Head);
            Assert.Equal(GitHubProposalOutcome.Appendable, observed.Outcome);
            Assert.Equal(body, observed.Observation!.Metadata.Body);
            Assert.Equal(authority.OperationId, observed.Observation.Coordination.OperationId);
            Assert.Equal(authority.CumulativePatchBytes, observed.Observation.Coordination.CumulativePatchBytes);
            using (var partialRestart = await Harness.Create(initial.Remote, authority, seed: false))
            {
                h.Remote.Prs[0]["base"]!["sha"] = Oid('9');
                Assert.Equal(GitHubProposalOutcome.Conflict,
                    (await partialRestart.Store.RecoverAsync(partialRestart.State, partialRestart.Head)).Outcome);
                Assert.Equal(1, h.Remote.Posts);
                h.Remote.Prs[0]["base"]!["sha"] = Oid('1');
            }
            await h.Publish(observed);
            using var restarted = await Harness.Create(initial.Remote, authority, seed: false);
            Assert.Equal(body, (await restarted.Store.ObserveAsync(restarted.State, restarted.Head)).Observation!.Metadata.Body);
        }
        Assert.Equal(1, initial.Remote.Posts);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Pagination_exact_duplicates_and_late_failure(bool duplicate, bool failure)
    {
        using var h = await Harness.Create();
        await h.Store.CreateAsync(h.State, h.Head);
        var unrelated = (JsonObject)h.Remote.Prs[0].DeepClone();
        unrelated["id"] = 10; unrelated["node_id"] = "PR_10"; unrelated["number"] = 10;
        unrelated["head"]!["ref"] = "unrelated"; unrelated["body"] = "unrelated"; unrelated["user"] = null;
        h.Remote.Prs.Insert(0, unrelated);
        h.Remote.Paginate = true; h.Remote.Duplicate = duplicate; h.Remote.FailLastPage = failure;
        var result = await h.Store.ObserveAsync(h.State, h.Head);
        Assert.Equal(failure ? GitHubProposalOutcome.Failed : GitHubProposalOutcome.Appendable, result.Outcome);
        Assert.Equal(1, h.Remote.Posts);
    }

    private static void Lifecycle(JsonObject pr, string kind)
    {
        var closed = kind is "closed" or "merged";
        pr["state"] = closed ? "closed" : "open";
        pr["draft"] = kind == "draft";
        pr["merged"] = kind == "merged";
        pr["closed_at"] = closed ? "2026-01-02T00:00:00Z" : null;
        pr["merged_at"] = kind == "merged" ? "2026-01-02T00:00:00Z" : null;
    }

    private sealed class Harness : IDisposable
    {
        internal required Remote Remote { get; init; }
        internal required ValidatedGitHubPublicationAuthority Authority { get; init; }
        internal required GitHubApiClient Client { get; init; }
        internal required GitHubCoordinationStore Coordination { get; init; }
        internal required GitHubProposalPullRequestStore Store { get; init; }
        internal required IGitHubCoordinationStateCapability State { get; set; }
        internal GitHubProposalHead Head => new(GitHubPublicationFactory.CreateProposalRef(Authority), State.ProposalCommitOid!, State.ProposalTreeOid!);
        internal static async Task<Harness> Create(Remote? remote = null, ValidatedGitHubPublicationAuthority? authority = null, bool seed = true)
        {
            authority ??= InitialAuthority("operation-initial", '5');
            remote ??= new();
            if (seed) remote.Coordination.SeedChain(PublishedChain(authority)[..3]);
            using var registration = (IDisposable)typeof(GitHubTransportTestHook).GetMethod("Register", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [new Uri(Remote.Origin), new Handler(remote), 30_000])!;
            var client = GitHubApiClient.Create(authority, GitHubTransportTestHook.Placeholder);
            var coordination = GitHubCoordinationStore.Create(client);
            var current = await coordination.ReadCurrentAsync();
            Assert.Null(current.Failure);
            Assert.NotNull(current.State);
            return new()
            {
                Remote = remote,
                Authority = authority,
                Client = client,
                Coordination = coordination,
                Store = GitHubProposalPullRequestStore.Create(client, coordination, Publisher),
                State = current.State!
            };
        }
        internal async Task Publish(GitHubProposalResult result)
        {
            var metadata = result.Observation!.Metadata;
            var published = await Coordination.AdvanceAsync(State, GitHubCoordinationStageUpdate.PullRequestResult(
                GitHubCoordinationStage.Published, Head.CommitOid, Head.TreeOid, metadata.CreationCommitment,
                17, State.TargetCommitOid, State.TargetCommitOid, metadata.MarkerHash));
            Assert.Null(published.Failure); State = published.State!;
        }
        public void Dispose() => Client.Dispose();
        internal async Task PublishStale(GitHubProposalResult observation)
        {
            var metadata = observation.Observation!.Metadata;
            var stale = await Coordination.AdvanceAsync(State, GitHubCoordinationStageUpdate.PullRequestResult(
                GitHubCoordinationStage.StaleDraft, Head.CommitOid, Head.TreeOid, metadata.CreationCommitment,
                17, State.TargetCommitOid, observation.Observation.PullRequest.BaseOid, metadata.MarkerHash));
            Assert.Null(stale.Failure); State = stale.State!;
        }
    }

    private sealed class Handler(Remote remote) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => remote.Reply(request, cancellationToken);
    }

    private sealed class Remote
    {
        internal const string Origin = "http://127.0.0.1:18765/";
        internal CoordinationRemote Coordination { get; } = new();
        internal List<JsonObject> Prs { get; } = [];
        internal List<string> Requests { get; } = [];
        internal int Posts;
        internal string Head = Oid('2');
        internal string Tree = Oid('3');
        internal bool LoseResponse, HideCreated, Paginate, Duplicate, FailLastPage;
        internal bool DelayRecovery;
        internal Action? BeforePost, AfterPost, AfterDetail;
        internal TaskCompletionSource? ConcurrentPosts;
        private readonly object gate = new();
        internal async Task<HttpResponseMessage> Reply(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (DelayRecovery && Posts > 0) await Task.Delay(Timeout.Infinite, cancellationToken);
            var path = request.RequestUri!.AbsolutePath;
            lock (gate) Requests.Add(request.Method + " " + path);
            if (path == "/repos/Owner/repo/pulls" && request.Method == HttpMethod.Post)
            {
                var count = Interlocked.Increment(ref Posts);
                if (ConcurrentPosts is { } barrier)
                { if (count == 2) barrier.TrySetResult(); await barrier.Task; }
                var payload = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
                Assert.True(payload["draft"]!.GetValue<bool>()); Assert.False(payload["maintainer_can_modify"]!.GetValue<bool>());
                lock (gate)
                {
                    BeforePost?.Invoke(); BeforePost = null;
                    if (Prs.Any(p => p["state"]!.GetValue<string>() == "open")) return Json(HttpStatusCode.UnprocessableEntity, new { message = "duplicate" });
                    var pr = MakePr(payload);
                    if (!HideCreated) Prs.Add(pr);
                    AfterPost?.Invoke(); AfterPost = null;
                    cancellationToken.ThrowIfCancellationRequested();
                    if (LoseResponse) { LoseResponse = false; throw new IOException("synthetic private response"); }
                    return Json(HttpStatusCode.Created, pr);
                }
            }
            if (path == "/repos/Owner/repo/pulls")
            {
                lock (gate)
                {
                    var page2 = request.RequestUri.Query.Contains("page=2", StringComparison.Ordinal);
                    if (page2 && FailLastPage) return Json(HttpStatusCode.ServiceUnavailable, new { message = "synthetic" });
                    var list = Paginate && Prs.Count > 1 ? (page2 ? (Duplicate ? Prs : Prs.Skip(1)) : Prs.Take(1)) : Prs;
                    var response = Json(HttpStatusCode.OK, list.ToArray());
                    if (Paginate && Prs.Count > 1 && !page2)
                    {
                        var url = Origin + "repos/Owner/repo/pulls?state=all&sort=created&direction=asc&per_page=100&page=2";
                        response.Headers.TryAddWithoutValidation("Link", "<" + url + ">; rel=\"next\", <" + url + ">; rel=\"last\"");
                    }
                    return response;
                }
            }
            if (path.StartsWith("/repos/Owner/repo/pulls/", StringComparison.Ordinal))
            {
                lock (gate)
                {
                    var found = Prs.SingleOrDefault(p => p["number"]!.GetValue<int>().ToString() == path.Split('/')[^1]);
                    var response = found is null ? Json(HttpStatusCode.NotFound, new { message = "missing" }) : Json(HttpStatusCode.OK, found);
                    AfterDetail?.Invoke(); AfterDetail = null;
                    return response;
                }
            }
            if (path.Contains("/git/ref/heads/contract-scribe/proposals/", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, new { @ref = "refs/" + path[(path.IndexOf("/git/ref/", StringComparison.Ordinal) + 9)..], node_id = "PROPOSAL", @object = new { type = "commit", sha = Head } });
            if (path == "/repos/Owner/repo/git/commits/" + Head)
                return Json(HttpStatusCode.OK, new { sha = Head, tree = new { sha = Tree }, parents = new[] { new { sha = Oid('1') } }, message = "synthetic proposal", author = CommitActor(), committer = CommitActor() });
            if (path == "/repos/Owner/repo/git/trees/" + Tree)
                return Json(HttpStatusCode.OK, new { sha = Tree, truncated = false, tree = Array.Empty<object>() });
            return await Coordination.Reply(request);
        }
        private JsonObject MakePr(JsonNode payload) => JsonSerializer.SerializeToNode(new
        {
            id = 17,
            node_id = "PR_17",
            number = 17,
            state = "open",
            draft = true,
            merged = false,
            merged_at = (string?)null,
            closed_at = (string?)null,
            created_at = "2026-01-01T00:00:00Z",
            title = payload["title"]!.GetValue<string>(),
            body = payload["body"]!.GetValue<string>(),
            user = new { id = Publisher.Id, node_id = Publisher.NodeId, login = Publisher.Login, type = "Bot" },
            head = new { @ref = payload["head"]!.GetValue<string>(), sha = Head, repo = Repo() },
            @base = new { @ref = payload["base"]!.GetValue<string>(), sha = Coordination.TargetHead, repo = Repo() },
            maintainer_can_modify = false,
        })!.AsObject();
        private static object Repo() => new { id = 42, node_id = "R_42", name = "repo", full_name = "Owner/repo", owner = new { login = "Owner", id = 7, node_id = "U_7", type = "User" } };
        private static object CommitActor() => new { name = "Synthetic", email = "synthetic@example.test", date = "2000-01-01T00:00:00Z" };
        private static HttpResponseMessage Json(HttpStatusCode status, object value) => new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
