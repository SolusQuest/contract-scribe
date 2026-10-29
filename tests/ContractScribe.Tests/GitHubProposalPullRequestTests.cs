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
    [Theory]
    [InlineData("clean")]
    [InlineData("reuse")]
    [InlineData("foreign-store")]
    [InlineData("forged")]
    [InlineData("effect")]
    [InlineData("unknown-effect")]
    [InlineData("cancel")]
    [InlineData("parent-cancel")]
    [InlineData("target-moved")]
    [InlineData("head-moved")]
    public async Task Same_gate_absence_uses_one_fresh_guard_but_keeps_its_closing_physical_fence(string change)
    {
        using var h = await Harness.Create();
        using var parent = new CancellationTokenSource();
        var guarded = await h.Coordination.ReadClaimAsync(h.State, parent.Token);
        var guard = Assert.IsAssignableFrom<IGitHubCoordinationGuardCapability>(guarded.Guard);
        var store = h.Store;
        if (change == "forged") guard = new UnownedGuard(guard.State);
        if (change == "foreign-store") store = GitHubProposalPullRequestStore.Create(h.Client, GitHubCoordinationStore.Create(h.Client), Publisher);
        if (change is "effect" or "unknown-effect")
        {
            if (change == "unknown-effect") h.Remote.Coordination.LoseResponse = LostMutation.Blob;
            var bytes = "bounded synthetic orphan"u8.ToArray();
            var mutation = await h.Client.CreateBlobAsync(GitHubCoordinationObjects.ObjectOid("blob", bytes), bytes);
            Assert.Equal(change == "effect" ? GitHubDelivery.NeedsReadback : GitHubDelivery.Ambiguous, mutation.Delivery);
        }
        using var cancellation = new CancellationTokenSource();
        if (change == "cancel") cancellation.Cancel();
        if (change == "parent-cancel") parent.Cancel();
        if (change == "target-moved") h.Remote.Coordination.TargetHead = Oid('9');
        if (change == "head-moved") h.Remote.Coordination.SeedChain(PublishedChain(h.Authority));
        var before = h.Remote.Requests.Count;
        if (change == "cancel")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.PreflightAsync(guard, cancellation.Token).AsTask());
            Assert.Equal(before, h.Remote.Requests.Count);
            Assert.Equal(GitHubProposalOutcome.Conflict, (await store.PreflightAsync(guard)).Outcome);
            Assert.Equal(before, h.Remote.Requests.Count);
        }
        else
        {
            var result = await store.PreflightAsync(guard);
            Assert.Equal(change is "clean" or "reuse" ? GitHubProposalOutcome.Absent : GitHubProposalOutcome.Conflict, result.Outcome);
            if (change is "effect" or "unknown-effect" or "forged" or "foreign-store" or "parent-cancel") Assert.Equal(before, h.Remote.Requests.Count);
            else
            {
                Assert.Contains(h.Remote.Requests.Skip(before), request => request == "GET /repos/Owner/repo/pulls");
                Assert.Contains(h.Remote.Requests.Skip(before), request => request.Contains("/git/ref/heads/contract-scribe/coordination/", StringComparison.Ordinal));
            }
            if (change == "reuse")
            {
                var completed = h.Remote.Requests.Count;
                Assert.Equal(GitHubProposalOutcome.Conflict, (await store.PreflightAsync(guard)).Outcome);
                Assert.Equal(completed, h.Remote.Requests.Count);
            }
        }
        Assert.Equal(0, h.Remote.Posts);
    }

    private sealed record UnownedGuard(IGitHubCoordinationStateCapability State) : IGitHubCoordinationGuardCapability;

    private static string Oid(char c) => new(c, 40);
    private static readonly GitHubActor Publisher = new(99, "BOT_99", "github-actions[bot]", GitHubActorKind.Bot);

    [Theory]
    [InlineData(401, (int)GitHubFailureCode.Authentication)]
    [InlineData(403, (int)GitHubFailureCode.Permission)]
    [InlineData(429, (int)GitHubFailureCode.RateLimit)]
    [InlineData(422, (int)GitHubFailureCode.Validation)]
    [InlineData(503, (int)GitHubFailureCode.HostFailure)]
    public async Task Empty_recovery_preserves_dispatched_creation_failure(int status, int failure)
    {
        using var h = await Harness.Create();
        h.Remote.PostError = (HttpStatusCode)status;
        var result = await h.SendAsync(h.State, h.Head);
        Assert.Equal(GitHubProposalOutcome.Unresolved, result.Outcome);
        Assert.Equal((GitHubFailureCode)failure, result.Failure!.Code);
        Assert.Equal(status, result.Failure.HttpStatus);
        Assert.Equal(GitHubDelivery.Ambiguous, result.Delivery);
        Assert.Null(result.Observation);
        Assert.Empty(h.Remote.Prs);
        Assert.Equal(1, h.Remote.Posts);
        Assert.DoesNotContain("synthetic", result.ToString());
        // An explicit read-only retry still cannot create; transient failure is not invented after restart.
        using var restart = await Harness.Create(h.Remote, h.Authority, seed: false);
        Assert.Equal(GitHubProposalOutcome.Unresolved, (await restart.Store.RecoverAsync(restart.State, restart.Head)).Outcome);
        Assert.Equal(1, h.Remote.Posts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_evidence_is_not_replaced_by_creation_error(bool recoveryFails)
    {
        using var h = await Harness.Create();
        h.Remote.PostError = HttpStatusCode.Forbidden;
        h.Remote.CreateBeforePostError = true;
        if (recoveryFails) h.Remote.RecoveryReadError = HttpStatusCode.ServiceUnavailable;
        var result = await h.SendAsync(h.State, h.Head);
        Assert.Equal(recoveryFails ? GitHubProposalOutcome.Failed : GitHubProposalOutcome.OpenDraft, result.Outcome);
        if (recoveryFails)
        {
            Assert.Equal(GitHubFailureCode.HostFailure, result.Failure!.Code);
            Assert.Equal(503, result.Failure.HttpStatus);
            Assert.Null(result.Observation);
        }
        else
        {
            Assert.Null(result.Failure);
            Assert.NotNull(result.Observation);
        }
        Assert.Equal(GitHubDelivery.Ambiguous, result.Delivery);
        Assert.Single(h.Remote.Prs);
        Assert.Equal(1, h.Remote.Posts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retained_stale_base_blocks_equality_and_allows_exact_terminal(bool persist)
    {
        using var h = await Harness.Create();
        h.Remote.BeforePost = () => h.Remote.Coordination.TargetHead = Oid('9');
        var stale = await h.SendAsync(h.State, h.Head);
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
        var stale = await h.SendAsync(h.State, h.Head);
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
        var result = await h.SendAsync(h.State, h.Head);
        Assert.Equal(GitHubProposalOutcome.Failed, result.Outcome);
        Assert.Equal(GitHubFailureCode.Timeout, result.Failure!.Code);
        Assert.Equal(GitHubDelivery.NeedsReadback, result.Delivery);
        Assert.Null(result.Observation);
        Assert.Equal(1, h.Remote.Posts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caller_cancellation_after_dispatch_preserves_independent_recovery(bool hide)
    {
        using var h = await Harness.Create();
        using var caller = new CancellationTokenSource();
        h.Remote.HideCreated = hide;
        h.Remote.AfterPost = caller.Cancel;
        var result = await h.SendAsync(h.State, h.Head, caller.Token);
        Assert.True(caller.IsCancellationRequested);
        Assert.Equal(hide ? GitHubProposalOutcome.Unresolved : GitHubProposalOutcome.OpenDraft, result.Outcome);
        Assert.Equal(hide ? GitHubFailureCode.Cancelled : (GitHubFailureCode?)null, result.Failure?.Code);
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
        await h.SendAsync(h.State, h.Head);
        h.Remote.AfterDetail = () => h.Remote.Prs[0]["title"] = "human edit";
        Assert.Equal(GitHubProposalOutcome.Conflict, (await h.Store.ObserveAsync(h.State, h.Head)).Outcome);
        Assert.Equal(1, h.Remote.Posts);
    }

    [Fact]
    public async Task Claim_movement_after_dispatch_cannot_authorize_completion()
    {
        using var h = await Harness.Create();
        h.Remote.AfterPost = () => h.Remote.Coordination.ForceCoordinationHead(Oid('f'));
        var result = await h.SendAsync(h.State, h.Head);
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
        var result = await h.SendAsync(h.State, h.Head);
        Assert.Equal(GitHubProposalOutcome.OpenDraft, result.Outcome);
        Assert.Equal(metadata.Body, result.Observation!.PullRequest.Body);
        Assert.Equal(1, h.Remote.Posts);
        Assert.Contains(h.Remote.Requests, x => x == "GET /repos/Owner/repo/pulls/17");
        var replay = await h.SendAsync(h.State, h.Head);
        Assert.Equal(GitHubProposalOutcome.Conflict, replay.Outcome);
        using var restart = await Harness.Create(h.Remote, h.Authority, seed: false);
        Assert.Equal(GitHubProposalOutcome.OpenDraft, (await restart.Store.RecoverAsync(restart.State, restart.Head)).Outcome);
        Assert.Equal(1, h.Remote.Posts);
        Assert.All(h.Remote.Requests.Where(x => !x.StartsWith("GET ", StringComparison.Ordinal) && x != "POST /graphql"), x => Assert.Equal("POST /repos/Owner/repo/pulls", x));
        Assert.Equal(0, h.Remote.Coordination.ObjectMutationAttempts);
        Assert.Equal(0, h.Remote.Coordination.RefMutationAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Response_loss_with_or_without_visible_residual_never_retries(bool hide)
    {
        using var h = await Harness.Create();
        h.Remote.LoseResponse = true;
        h.Remote.HideCreated = hide;
        var result = await h.SendAsync(h.State, h.Head);
        Assert.Equal(hide ? GitHubProposalOutcome.Unresolved : GitHubProposalOutcome.OpenDraft, result.Outcome);
        Assert.Equal(hide ? GitHubFailureCode.ResponseLost : (GitHubFailureCode?)null, result.Failure?.Code);
        using var restart = await Harness.Create(h.Remote, h.Authority, seed: false);
        for (var i = 0; i < 2; i++)
            Assert.Equal(result.Outcome, (await restart.Store.RecoverAsync(restart.State, restart.Head)).Outcome);
        Assert.Equal(1, h.Remote.Posts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Only_the_acknowledged_fence_writer_can_send_once(bool sameStore)
    {
        using var first = await Harness.Create();
        using var second = await Harness.Create(first.Remote, first.Authority, seed: false);
        var results = await Task.WhenAll(first.SendAsync(first.State, first.Head).AsTask(),
            (sameStore ? first.SendAsync(first.State, first.Head)
                : second.SendAsync(second.State, second.Head)).AsTask()).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(results, r => r.Outcome == GitHubProposalOutcome.OpenDraft);
        Assert.Single(results, r => r.Outcome == GitHubProposalOutcome.Conflict);
        Assert.Single(first.Remote.Prs);
        Assert.Equal(1, first.Remote.Posts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Base_mismatch_is_a_conservative_create_recovery_residual(bool loss)
    {
        using var h = await Harness.Create();
        h.Remote.LoseResponse = loss;
        h.Remote.BeforePost = () => h.Remote.Coordination.TargetHead = Oid('9');
        var created = await h.SendAsync(h.State, h.Head);
        Assert.Equal(GitHubProposalOutcome.StaleDraft, created.Outcome);
        Assert.Equal(h.State.HeadOid, h.Remote.Coordination.CoordinationHead);
        using var restart = await Harness.Create(h.Remote, h.Authority, seed: false);
        Assert.Equal(GitHubProposalOutcome.Conflict, (await restart.SendAsync(restart.State, restart.Head)).Outcome);
        Assert.Equal(GitHubProposalOutcome.StaleDraft, (await restart.Store.RecoverAsync(restart.State, restart.Head)).Outcome);
        Assert.Equal(1, h.Remote.Posts);
    }

    [Fact]
    public async Task Retained_success_distinguishes_later_drift_and_equal_unknown_histories_agree()
    {
        using var h = await Harness.Create();
        var success = await h.SendAsync(h.State, h.Head);
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
        await h.SendAsync(h.State, h.Head);
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
        var result = await h.Store.RecoverAsync(h.State, h.Head);
        Assert.Contains(result.Outcome, new[] { GitHubProposalOutcome.Conflict, GitHubProposalOutcome.Failed });
        if (kind is "title" or "body" or "marker" or "bot" or "user" or "head" or "head-repo" or "base-ref" or "generation" or "proposal")
            Assert.Equal(GitHubCoordinationFailureKind.HumanChange, result.Cause);
        Assert.Null(result.Observation);
        Assert.Equal(GitHubProposalOutcome.Conflict, (await h.SendAsync(h.State, h.Head)).Outcome);
        Assert.Equal(1, h.Remote.Posts);
    }

    [Fact]
    public async Task Genuine_claim_moved_claim_foreign_store_and_pre_dispatch_cancellation()
    {
        using var h = await Harness.Create();
        using var other = await Harness.Create();
        Assert.Equal(GitHubProposalOutcome.Conflict, (await h.SendAsync(other.State, h.Head)).Outcome);
        Assert.Throws<ArgumentException>(() => GitHubProposalPullRequestStore.Create(h.Client, other.Coordination, Publisher));
        Assert.Throws<ArgumentException>(() => GitHubProposalPullRequestStore.Create(h.Client, h.Coordination, null!));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.NotEqual(GitHubProposalOutcome.OpenDraft, (await h.SendAsync(h.State, h.Head, cancelled.Token)).Outcome);
        h.Remote.Coordination.ForceCoordinationHead(Oid('f'));
        Assert.NotEqual(GitHubProposalOutcome.OpenDraft, (await h.SendAsync(h.State, h.Head)).Outcome);
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
        var result = await h.SendAsync(h.State, h.Head);
        Assert.Equal((GitHubProposalOutcome)expectedValue, result.Outcome);
        Lifecycle(h.Remote.Prs[0], "draft");
        Assert.Equal(GitHubProposalOutcome.Conflict, (await h.Store.ObserveAsync(h.State, h.Head, result.Observation)).Outcome);
        Assert.Equal(1, h.Remote.Posts);
    }

    [Fact]
    public async Task Budget_hold_is_not_ready_history_and_real_ready_history_still_rejects_conversion()
    {
        using var h = await Harness.Create();
        var result = await h.SendAsync(h.State, h.Head);
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
    public async Task Repeated_replacements_preserve_the_sealed_PR_bytes_and_original_result_set()
    {
        using var initial = await Harness.Create();
        var created = await initial.SendAsync(initial.State, initial.Head);
        await initial.Publish(created);
        var body = initial.Remote.Prs[0].ToJsonString();
        var originalHead = initial.Remote.Coordination.CoordinationHead;
        var writes = initial.Remote.Coordination.ObjectMutationAttempts + initial.Remote.Coordination.RefMutationAttempts;
        for (var i = 0; i < 2; i++)
        {
            var replacement = ReplacementAuthority(initial.Authority, "replacement-" + i, (char)('6' + i));
            using var h = await Harness.Create(initial.Remote, replacement, seed: false);
            var read = await h.Coordination.ReadCurrentAsync();
            var claim = await h.Coordination.ClaimAsync(read.Read!);
            Assert.Equal(GitHubCoordinationOutcome.Conflict, claim.Outcome);
            Assert.Equal(body, initial.Remote.Prs[0].ToJsonString());
            Assert.Equal(originalHead, initial.Remote.Coordination.CoordinationHead);
            Assert.Equal(writes, initial.Remote.Coordination.ObjectMutationAttempts + initial.Remote.Coordination.RefMutationAttempts);
        }
        Assert.Equal(1, initial.Remote.Posts);
    }

    [Theory]
    [InlineData("first-proposal", "advance")]
    [InlineData("classification", "advance")]
    [InlineData("final-proposal", "advance")]
    [InlineData("first-proposal", "stable")]
    [InlineData("classification", "stable")]
    [InlineData("final-proposal", "stable")]
    [InlineData("first-proposal", "unavailable")]
    [InlineData("classification", "unavailable")]
    [InlineData("final-proposal", "unavailable")]
    [InlineData("first-proposal", "invalid")]
    [InlineData("classification", "invalid")]
    [InlineData("final-proposal", "invalid")]
    public async Task Human_change_requires_a_stable_authenticated_coordination_window(string window, string coordination)
    {
        using var initial = await Harness.Create();
        await initial.Publish(await initial.SendAsync(initial.State, initial.Head));
        var authority = initial.Authority;
        using var observer = await Harness.Create(initial.Remote, authority, seed: false);
        using var writer = await Harness.Create(initial.Remote, authority, seed: false);
        var advanced = await writer.Coordination.AdvanceAsync(writer.State, GitHubCoordinationStageUpdate.AwaitingReview());
        Assert.Null(advanced.Failure);
        initial.Remote.Coordination.ForceCoordinationHead(observer.State.HeadOid);
        var proposalReads = 0;
        initial.Remote.BeforeGet = path =>
        {
            if (path.Contains("/git/ref/heads/contract-scribe/proposals/", StringComparison.Ordinal)) proposalReads++;
            if (!(window == "classification" && path == "/repos/Owner/repo/pulls")
                && !(window == "first-proposal" && proposalReads == 1)
                && !(window == "final-proposal" && proposalReads == 2)) return;
            initial.Remote.BeforeGet = null;
            initial.Remote.Head = Oid('4');
            initial.Remote.Prs[0]["head"]!["sha"] = Oid('4');
            if (coordination == "advance") initial.Remote.Coordination.ForceCoordinationHead(advanced.State!.HeadOid);
            if (coordination == "invalid") initial.Remote.Coordination.SeedRaw(
                Encoding.UTF8.GetBytes("{}\n"), new string('a', 64), "claimed", Oid('1'));
            if (coordination == "unavailable") initial.Remote.RepositoryReadError = HttpStatusCode.Forbidden;
        };
        var writes = initial.Remote.Coordination.ObjectMutationAttempts + initial.Remote.Coordination.RefMutationAttempts;
        var requests = initial.Remote.Requests.Count;

        var result = await observer.Store.ObserveAsync(observer.State, observer.Head);

        Assert.Null(initial.Remote.BeforeGet);
        Assert.Null(result.Observation);
        Assert.Single(initial.Remote.Prs);
        Assert.Equal(1, initial.Remote.Posts);
        Assert.Equal(writes, initial.Remote.Coordination.ObjectMutationAttempts + initial.Remote.Coordination.RefMutationAttempts);
        Assert.All(initial.Remote.Requests.Skip(requests), request => Assert.True(request.StartsWith("GET ", StringComparison.Ordinal) || request == "POST /graphql"));
        if (coordination is "advance" or "stable")
        {
            Assert.Equal(GitHubDelivery.Read, result.Delivery);
            Assert.Equal(GitHubProposalOutcome.Conflict, result.Outcome);
            Assert.Null(result.Failure);
            if (coordination == "advance") Assert.Equal(advanced.State!.HeadOid,
                (await observer.Coordination.ReadCurrentAsync()).State!.HeadOid);
        }
        else
        {
            var failed = await observer.Coordination.ReadCurrentAsync();
            Assert.Null(failed.State);
            Assert.NotNull(failed.Failure);
            Assert.Equal(failed.Failure.Delivery, result.Delivery);
            Assert.Equal(failed.Failure.Kind == GitHubCoordinationFailureKind.Transport ? GitHubProposalOutcome.Failed : GitHubProposalOutcome.Conflict, result.Outcome);
            Assert.Equal(failed.Failure.Kind, result.Cause);
            Assert.Equal(failed.Failure.ReadbackFailure ?? failed.Failure.TransportFailure, result.Failure);
            if (coordination == "unavailable") Assert.Equal(403, result.Failure!.HttpStatus);
            if (coordination == "invalid") Assert.Equal(GitHubCoordinationFailureKind.ObjectMismatch, result.Cause);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Pagination_exact_duplicates_and_late_failure(bool duplicate, bool failure)
    {
        using var h = await Harness.Create();
        await h.SendAsync(h.State, h.Head);
        var unrelated = (JsonObject)h.Remote.Prs[0].DeepClone();
        unrelated["id"] = 10; unrelated["node_id"] = "PR_10"; unrelated["number"] = 10;
        unrelated["head"]!["ref"] = "unrelated"; unrelated["body"] = "unrelated"; unrelated["user"] = null;
        h.Remote.Prs.Insert(0, unrelated);
        h.Remote.Paginate = true; h.Remote.Duplicate = duplicate; h.Remote.FailLastPage = failure;
        var result = await h.Store.ObserveAsync(h.State, h.Head);
        Assert.Equal(failure ? GitHubProposalOutcome.Failed : GitHubProposalOutcome.OpenDraft, result.Outcome);
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
        private IGitHubPullRequestEntitlement? sendRight;
        internal GitHubProposalHead Head => new(GitHubPublicationFactory.CreateProposalRef(Authority), State.ProposalCommitOid!, State.ProposalTreeOid!);
        internal static async Task<Harness> Create(Remote? remote = null, ValidatedGitHubPublicationAuthority? authority = null, bool seed = true)
        {
            authority ??= InitialAuthority("operation-initial", '5');
            remote ??= new();
            if (seed) remote.Coordination.SeedChain(PublishedChain(authority)[..4]);
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
                State = current.State!,
                // Isolated R5 wire fixtures stand for an acknowledged fence winner.
                // Production and CLI tests obtain this right only from the real CAS.
                sendRight = seed ? (IGitHubPullRequestEntitlement)Assert.Single(typeof(GitHubCoordinationStore)
                    .GetNestedType("PullRequestEntitlement", BindingFlags.NonPublic)!
                    .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                    .Invoke([coordination, current.State]) : null,
            };
        }
        internal ValueTask<GitHubProposalResult> SendAsync(IGitHubCoordinationStateCapability state,
            GitHubProposalHead head, CancellationToken token = default) =>
            Store.CreateAuthorizedAsync(state, head, sendRight!, token);

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
        internal HttpStatusCode? PostError, RecoveryReadError;
        internal HttpStatusCode? RepositoryReadError;
        internal bool CreateBeforePostError;
        internal Action? BeforePost, AfterPost, AfterDetail;
        internal Action<string>? BeforeGet;
        private readonly object gate = new();
        internal async Task<HttpResponseMessage> Reply(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (DelayRecovery && Posts > 0) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (Posts > 0 && request.Method == HttpMethod.Get && RecoveryReadError is { } readError)
                return Json(readError, new { message = "synthetic recovery error" });
            var path = request.RequestUri!.AbsolutePath;
            lock (gate) Requests.Add(request.Method + " " + path);
            if (request.Method == HttpMethod.Get) BeforeGet?.Invoke(path);
            if (path == "/repos/Owner/repo" && RepositoryReadError is { } repositoryError)
                return Json(repositoryError, new { message = "synthetic repository error" });
            if (path == "/repos/Owner/repo/pulls" && request.Method == HttpMethod.Post)
            {
                Interlocked.Increment(ref Posts);
                var payload = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
                Assert.True(payload["draft"]!.GetValue<bool>()); Assert.False(payload["maintainer_can_modify"]!.GetValue<bool>());
                lock (gate)
                {
                    BeforePost?.Invoke(); BeforePost = null;
                    if (PostError is { } rejected && !CreateBeforePostError)
                        return Json(rejected, new { message = "synthetic creation error" });
                    if (Prs.Any(p => p["state"]!.GetValue<string>() == "open")) return Json(HttpStatusCode.UnprocessableEntity, new { message = "duplicate" });
                    var pr = MakePr(payload);
                    if (!HideCreated) Prs.Add(pr);
                    AfterPost?.Invoke(); AfterPost = null;
                    cancellationToken.ThrowIfCancellationRequested();
                    if (PostError is { } postError) return Json(postError, new { message = "synthetic creation error" });
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
