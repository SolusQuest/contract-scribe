using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Core;
using ContractScribe.GitHub.Coordination;
using ContractScribe.GitHub.GitData;
using ContractScribe.GitHub.PullRequests;
using ContractScribe.GitHub.Publication;
using ContractScribe.GitHub.Transport;
using Branch = ContractScribe.Tests.GitHubProposalBranchTests;

namespace ContractScribe.Tests;

[Collection("GitHub transport hook")]
public sealed class GitHubPublicationReconcilerTests
{
    private static readonly GitHubActor Publisher = new(99, "BOT_99", "github-actions[bot]", GitHubActorKind.Bot);

    [Fact]
    public async Task First_publication_uses_two_bounded_calls_then_cold_replay_reads_every_resource()
    {
        var remote = new Remote();
        var authority = Branch.Authority(remote.Git);
        using var session = new Session(remote, authority);
        var first = await session.Publish();
        Assert.Equal(GitHubPublicationResultKind.RecoveredRefPartial, first.Kind);
        Assert.Empty(remote.Prs);
        Assert.Equal(1, remote.Git.ProposalWrites);
        Assert.Equal(GitHubCoordinationStage.ProposalRefAdvanced, remote.State(authority).Stage);
        var second = await session.Publish();
        Assert.Equal(GitHubPublicationResultKind.Published, second.Kind);
        Assert.Equal(1, remote.Posts);
        Assert.Single(remote.Prs);
        Assert.Equal(GitHubCoordinationStage.Published, remote.State(authority).Stage);
        Assert.Equal(authority.OperationCommitmentSha256, second.PullRequest!.OperationCommitmentSha256);
        var writes = remote.Writes;
        var start = remote.Git.Requests.Count;
        using var restart = new Session(remote, authority);
        Assert.Equal(GitHubPublicationResultKind.ReplayNoOp, (await restart.Publish()).Kind);
        Assert.Equal(writes, remote.Writes);
        Assert.Contains(remote.Git.Requests.Skip(start), r => r.Path.Contains("/git/blobs/", StringComparison.Ordinal));
        Assert.DoesNotContain(remote.Git.Requests, r => r.Path == "/user" || r.Method == "PATCH");
    }

    [Fact]
    public async Task Lost_live_creation_entitlement_never_becomes_a_blind_post()
    {
        var remote = new Remote();
        var authority = Branch.Authority(remote.Git);
        using (var first = new Session(remote, authority))
            Assert.Equal(GitHubPublicationResultKind.RecoveredRefPartial, (await first.Publish()).Kind);
        var writes = remote.Writes;
        using var restart = new Session(remote, authority);
        Assert.Equal(GitHubPublicationResultKind.Conflict, (await restart.Publish()).Kind);
        Assert.Equal(GitHubPublicationResultKind.Conflict, (await restart.Publish()).Kind);
        Assert.Equal(writes, remote.Writes);
        Assert.Equal(0, remote.Posts);
    }

    [Fact]
    public async Task Append_runs_real_git_authority_and_retains_original_pr_metadata()
    {
        var remote = new Remote();
        var first = Branch.Authority(remote.Git);
        using (var session = new Session(remote, first))
        {
            await session.Publish();
            Assert.Equal(GitHubPublicationResultKind.Published, (await session.Publish()).Kind);
        }
        var metadata = remote.Prs[0]["body"]!.GetValue<string>();
        var title = remote.Prs[0]["title"]!.GetValue<string>();
        var previous = first;
        for (var i = 0; i < 3; i++)
        {
            byte[] bytes = [3, 4, (byte)i];
            var next = Branch.Authority(remote.Git, "append-" + i, previous, bytes);
            using var append = new Session(remote, next, Branch.Payload(next, bytes));
            Assert.Equal(GitHubPublicationResultKind.Published, (await append.Publish()).Kind);
            Assert.Equal(metadata, remote.Prs[0]["body"]!.GetValue<string>());
            Assert.Equal(title, remote.Prs[0]["title"]!.GetValue<string>());
            Assert.Equal(next.OperationCommitmentSha256, remote.State(next).OperationCommitmentSha256);
            Assert.Equal(next.CandidateCommitmentSha256, remote.State(next).CurrentCandidateCommitmentSha256);
            using var cold = new Session(remote, next, Branch.Payload(next, bytes));
            Assert.Equal(GitHubPublicationResultKind.ReplayNoOp, (await cold.Publish()).Kind);
            previous = next;
        }
        Assert.Equal(1, remote.Posts);
        Assert.Equal(4, remote.Git.ProposalWrites);
    }

    [Fact]
    public async Task Append_adds_a_path_to_the_cumulative_candidate_and_cold_replays_without_pr_prose_updates()
    {
        var remote = new Remote();
        var first = Branch.Authority(remote.Git);
        using (var initial = new Session(remote, first))
        {
            await initial.Publish();
            Assert.Equal(GitHubPublicationResultKind.Published, (await initial.Publish()).Kind);
        }
        var body = remote.Prs[0]["body"]!.GetValue<string>();
        byte[] bytes = [4, 5, 6];
        var next = Branch.Authority(remote.Git, "append-new-path", first, bytes, includeNew: true);
        var payload = Branch.Payload(next, bytes, includeNew: true);
        using (var append = new Session(remote, next, payload))
            Assert.Equal(GitHubPublicationResultKind.Published, (await append.Publish()).Kind);
        Assert.Equal(2, remote.State(next).CumulativeChangedFiles.Length);
        Assert.Equal(body, remote.Prs[0]["body"]!.GetValue<string>());
        var writes = remote.Writes;
        using var cold = new Session(remote, next, payload);
        Assert.Equal(GitHubPublicationResultKind.ReplayNoOp, (await cold.Publish()).Kind);
        Assert.Equal(writes, remote.Writes);
        Assert.Equal(1, remote.Posts);
    }

    [Theory]
    [InlineData("ready", (int)GitHubPublicationResultKind.AwaitingReview)]
    [InlineData("merged", (int)GitHubPublicationResultKind.Merged)]
    [InlineData("closed", (int)GitHubPublicationResultKind.ClosedUnmerged)]
    public async Task Human_lifecycle_is_observed_without_proposal_writes(string lifecycle, int expected)
    {
        var remote = new Remote();
        var authority = Branch.Authority(remote.Git);
        using var session = new Session(remote, authority);
        await session.Publish();
        await session.Publish();
        remote.Lifecycle(lifecycle);
        if (lifecycle == "merged") remote.Git.Refs["refs/heads/main"] = remote.Prs[0]["head"]!["sha"]!.GetValue<string>();
        var proposalWrites = remote.Git.ProposalWrites;
        Assert.Equal((GitHubPublicationResultKind)expected, (await session.Publish()).Kind);
        var writes = remote.Writes;
        Assert.Equal((GitHubPublicationResultKind)expected, (await session.Publish()).Kind);
        Assert.Equal(writes, remote.Writes);
        Assert.Equal(proposalWrites, remote.Git.ProposalWrites);
        Assert.Equal(1, remote.Posts);
    }

    [Fact]
    public async Task Stale_creation_is_durably_incomplete_and_cannot_regain_eligibility()
    {
        var remote = new Remote();
        var authority = Branch.Authority(remote.Git);
        using var session = new Session(remote, authority);
        await session.Publish();
        remote.BeforePost = () => remote.Git.Refs["refs/heads/main"] = new string('9', 40);
        var result = await session.Publish();
        Assert.Equal(GitHubPublicationResultKind.StaleBaseAfterCreate, result.Kind);
        Assert.Equal(new string('9', 40), result.StaleDraft!.ObservedBaseOid);
        Assert.Equal(GitHubCoordinationStage.StaleDraft, remote.State(authority).Stage);
        var writes = remote.Writes;
        using var restart = new Session(remote, authority);
        Assert.Equal(GitHubPublicationResultKind.StaleBaseAfterCreate, (await restart.Publish()).Kind);
        Assert.Equal(writes, remote.Writes);
        remote.Git.Refs["refs/heads/main"] = authority.ExpectedBaseCommitOid;
        Assert.Equal(GitHubPublicationResultKind.Conflict, (await restart.Publish()).Kind);
        Assert.Equal(writes, remote.Writes);
        Assert.Equal(1, remote.Posts);
    }

    [Theory]
    [InlineData(401, (int)GitHubPublicationResultKind.Permission)]
    [InlineData(403, (int)GitHubPublicationResultKind.Permission)]
    [InlineData(429, (int)GitHubPublicationResultKind.RateLimit)]
    [InlineData(503, (int)GitHubPublicationResultKind.HostFailure)]
    public async Task Failed_creation_retains_failure_and_cannot_be_retried(int status, int expected)
    {
        var remote = new Remote();
        using var session = new Session(remote, Branch.Authority(remote.Git));
        await session.Publish();
        remote.PostError = (HttpStatusCode)status;
        Assert.Equal((GitHubPublicationResultKind)expected, (await session.Publish()).Kind);
        Assert.Equal(1, remote.Posts);
        remote.PostError = null;
        Assert.Equal(GitHubPublicationResultKind.Conflict, (await session.Publish()).Kind);
        Assert.Equal(1, remote.Posts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Every_actual_mutation_boundary_recovers_or_stops_without_a_second_operation(bool after)
    {
        var baseline = new Remote();
        using (var normal = new Session(baseline, Branch.Authority(baseline.Git)))
        { await normal.Publish(); await normal.Publish(); }
        Assert.InRange(baseline.Mutations, 20, 60);
        for (var index = 1; index <= baseline.Mutations; index++)
        {
            var remote = new Remote { LoseAt = index, LoseAfter = after };
            var authority = Branch.Authority(remote.Git);
            using (var original = new Session(remote, authority))
            { await original.Publish(); await original.Publish(); }
            using var restart = new Session(remote, authority);
            var first = await restart.Publish();
            var writes = remote.Writes;
            var second = await restart.Publish();
            Assert.True(remote.Posts <= 1, $"fault {index}, after={after}, duplicate POST");
            Assert.True(remote.PrAttempts <= 1, $"fault {index}, after={after}, duplicate dispatched attempt");
            Assert.True(remote.Git.ProposalWrites <= 1, $"fault {index}, after={after}, duplicate ref transition");
            Assert.True(remote.Prs.Count <= 1, $"fault {index}, after={after}, duplicate PR");
            Assert.Equal(writes, remote.Writes);
            if (remote.Git.Refs.ContainsKey(GitHubPublicationFactory.CreateCoordinationRef(authority)))
                Assert.Equal(authority.OperationCommitmentSha256, remote.State(authority).OperationCommitmentSha256);
            if (second.Kind == GitHubPublicationResultKind.ReplayNoOp)
            {
                Assert.Single(remote.Prs);
                Assert.Equal(GitHubCoordinationStage.Published, remote.State(authority).Stage);
            }
            Assert.DoesNotContain("synthetic", first.ToString());
        }
    }

    [Fact]
    public async Task Independent_contenders_and_same_instance_calls_share_no_reconstructed_right()
    {
        var remote = new Remote();
        var authority = Branch.Authority(remote.Git);
        using var first = new Session(remote, authority);
        using var second = new Session(remote, authority);
        var claim = GitHubCoordinationObjects.Prepare(GitHubCoordinationCodec.CreateClaim(authority, new string('0', 40)));
        remote.Git.Barrier("GET", "/git/commits/" + claim.CommitOid, 2, onlyMissing: true);
        var calls = await Task.WhenAll(first.Publish().AsTask(), second.Publish().AsTask());
        Assert.Contains(calls, r => r.Kind == GitHubPublicationResultKind.RecoveredRefPartial);
        await Task.WhenAll(first.Publish().AsTask(), second.Publish().AsTask());
        Assert.Equal(1, remote.Posts);
        Assert.Single(remote.Prs);
        Assert.Equal(1, remote.Git.ProposalWrites);
        var writes = remote.Writes;
        var replays = await Task.WhenAll(first.Publish().AsTask(), first.Publish().AsTask());
        Assert.All(replays, result => Assert.Equal(GitHubPublicationResultKind.ReplayNoOp, result.Kind));
        Assert.Equal(writes, remote.Writes);
        var different = Branch.Authority(remote.Git, "different");
        using var contender = new Session(remote, different);
        Assert.Equal(GitHubPublicationResultKind.Conflict, (await contender.Publish()).Kind);
        Assert.Equal(writes, remote.Writes);
    }

    [Fact]
    public async Task Append_recovers_an_exact_unrecorded_ref_successor_after_restart()
    {
        var remote = new Remote();
        var first = Branch.Authority(remote.Git);
        using (var initial = new Session(remote, first)) { await initial.Publish(); await initial.Publish(); }
        byte[] bytes = [9, 8, 7];
        var next = Branch.Authority(remote.Git, "append-partial", first, bytes);
        // Stop the first read after the real ref CAS, before the coordination result is prepared.
        var failReads = false;
        remote.AfterMutation = (_, body) =>
        {
            if (body?.Contains("/proposals/", StringComparison.Ordinal) == true) failReads = true;
        };
        remote.RejectRead = _ => failReads ? HttpStatusCode.Forbidden : null;
        using (var append = new Session(remote, next, Branch.Payload(next, bytes)))
            Assert.Equal(GitHubPublicationResultKind.Permission, (await append.Publish()).Kind);
        Assert.Equal(GitHubCoordinationStage.ContentCreated, remote.State(next).Stage);
        remote.RejectRead = null; remote.AfterMutation = null;
        var refWrites = remote.Git.ProposalWrites;
        using var restart = new Session(remote, next, Branch.Payload(next, bytes));
        Assert.Equal(GitHubPublicationResultKind.Published, (await restart.Publish()).Kind);
        Assert.Equal(refWrites, remote.Git.ProposalWrites);
        Assert.Equal(1, remote.Posts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_fresh_successor_records_the_old_terminal_without_old_payload(bool closed)
    {
        var remote = new Remote();
        var first = Branch.Authority(remote.Git);
        using (var initial = new Session(remote, first)) { await initial.Publish(); await initial.Publish(); }
        remote.Lifecycle(closed ? "closed" : "merged");
        if (!closed) remote.Git.Refs["refs/heads/main"] = remote.Git.ProposalHead(first)!;
        byte[] bytes = [9, 8, 7];
        var prior = new GitHubPublicationPredecessorAuthority("prior", 1, first.GenerationId, remote.Git.ProposalHead(first)!,
            closed ? GitHubPublicationPredecessorDisposition.ClosedUnmerged : GitHubPublicationPredecessorDisposition.Merged);
        var input = Input(first) with
        {
            ExpectedBaseCommitOid = remote.Git.Refs["refs/heads/main"],
            SnapshotCommitmentSha256 = new string('a', 64),
            WorkPlanCommitmentSha256 = new string('b', 64),
            CandidateCommitmentSha256 = Hash(bytes),
            OperationId = "successor",
            GenerationId = "generation-next",
            TerminalPredecessor = prior,
            Transition = closed ? GitHubPublicationTransitionKind.SuccessorAfterClosedUnmerged : GitHubPublicationTransitionKind.SuccessorAfterMerge,
            ChangedFiles = [first.ChangedFiles[0] with { OriginalFileSha256 = Hash(closed ? Branch.Original : Branch.Candidate), CandidateFileSha256 = Hash(bytes) }],
            ClosedUnmergedSuccessorAuthorization = closed ? new("authorization", "prior", 1, first.GenerationId, prior.HeadOid,
                new string('a', 64), new string('b', 64), Hash(bytes), "generation-next", "successor") : null,
        };
        var staleInput = input with
        {
            SnapshotCommitmentSha256 = first.SnapshotCommitmentSha256,
            ClosedUnmergedSuccessorAuthorization = input.ClosedUnmergedSuccessorAuthorization is { } authorization
                ? authorization with { FreshSnapshotCommitmentSha256 = first.SnapshotCommitmentSha256 } : null,
        };
        var staleAuthority = GitHubPublicationFactory.CreateAuthority(staleInput);
        var oldWrites = remote.Writes;
        using (var stale = new Session(remote, staleAuthority, Branch.Payload(staleAuthority, bytes)))
            Assert.Equal(closed ? GitHubPublicationResultKind.ClosedUnmerged : GitHubPublicationResultKind.Merged, (await stale.Publish()).Kind);
        Assert.Equal(oldWrites, remote.Writes);
        var next = GitHubPublicationFactory.CreateAuthority(input);
        using var successor = new Session(remote, next, Branch.Payload(next, bytes));
        Assert.Equal(GitHubPublicationResultKind.RecoveredRefPartial, (await successor.Publish()).Kind);
        Assert.Equal(GitHubPublicationResultKind.Published, (await successor.Publish()).Kind);
        Assert.Equal(2, remote.Prs.Count);
        Assert.Single(remote.Prs, p => p["state"]!.GetValue<string>() == "open");
        Assert.Equal(next.GenerationId, remote.State(next).GenerationId);
    }

    [Fact]
    public async Task Equal_ceiling_publishes_but_next_append_and_policy_change_hold_without_writes()
    {
        foreach (var exhausted in new[] { false, true })
        {
            var remote = new Remote();
            var firstInput = Input(Branch.Authority(remote.Git));
            if (exhausted) firstInput = firstInput with { Policy = new(1, 1, 2) };
            var first = GitHubPublicationFactory.CreateAuthority(firstInput);
            using (var initial = new Session(remote, first))
            { await initial.Publish(); Assert.Equal(GitHubPublicationResultKind.Published, (await initial.Publish()).Kind); }
            byte[] bytes = [8, 7];
            var policy = exhausted ? first.Policy : new GitHubPublicationPolicy(9, 9, 999);
            var policyCommitment = GitHubPublicationFactory.CreateAuthority(firstInput with { Policy = policy }).PolicyCommitmentSha256;
            var next = GitHubPublicationFactory.CreateAuthority(firstInput with
            {
                OperationId = "append-hold",
                CandidateCommitmentSha256 = Hash(bytes),
                Policy = policy,
                Transition = GitHubPublicationTransitionKind.SameSnapshotAppend,
                PrecedingOperationId = first.OperationId,
                PrecedingAuthorityCommitmentSha256 = first.AuthorityCommitmentSha256,
                PrecedingCandidateCommitmentSha256 = first.CandidateCommitmentSha256,
                PrecedingGenerationId = first.GenerationId,
                PrecedingSnapshotCommitmentSha256 = first.SnapshotCommitmentSha256,
                PrecedingPolicyCommitmentSha256 = policyCommitment,
                PrecedingChangedFiles = [new("file.bin", first.ChangedFiles[0].CandidateFileSha256)],
                ChangedFiles = [first.ChangedFiles[0] with { CandidateFileSha256 = Hash(bytes) }],
            });
            var writes = remote.Writes;
            using var append = new Session(remote, next, Branch.Payload(next, bytes));
            var held = await append.Publish();
            Assert.Equal(GitHubPublicationResultKind.AwaitingReview, held.Kind);
            Assert.Equal(first.OperationCommitmentSha256, held.PullRequest!.OperationCommitmentSha256);
            Assert.Equal(writes, remote.Writes);
        }
    }

    [Theory]
    [InlineData("title")]
    [InlineData("body")]
    [InlineData("author")]
    [InlineData("head")]
    [InlineData("blob")]
    public async Task Changed_owned_resources_are_human_change_without_repair(string kind)
    {
        var remote = new Remote();
        var authority = Branch.Authority(remote.Git);
        using var session = new Session(remote, authority);
        await session.Publish(); await session.Publish();
        if (kind is "title" or "body") remote.Prs[0][kind] = "human change";
        if (kind == "author") remote.Prs[0]["user"]!["login"] = "another-bot";
        if (kind == "head") remote.Git.Refs[GitHubPublicationFactory.CreateProposalRef(authority)] = remote.Git.BaseOid;
        if (kind == "blob") remote.Git.Blobs[remote.Git.FileOid] = [7, 8, 9];
        var writes = remote.Writes;
        Assert.Equal(GitHubPublicationResultKind.HumanChange, (await session.Publish()).Kind);
        Assert.Equal(writes, remote.Writes);
    }

    [Fact]
    public async Task Active_campaign_is_not_hidden_by_missing_coordination_and_conflict_is_order_independent()
    {
        var remote = new Remote();
        var authority = Branch.Authority(remote.Git);
        using var session = new Session(remote, authority);
        await session.Publish(); await session.Publish();
        var head = remote.Git.Refs[GitHubPublicationFactory.CreateCoordinationRef(authority)];
        remote.Git.Refs.Remove(GitHubPublicationFactory.CreateCoordinationRef(authority));
        var writes = remote.Writes;
        using (var restart = new Session(remote, authority))
            Assert.Equal(GitHubPublicationResultKind.Conflict, (await restart.Publish()).Kind);
        Assert.Equal(writes, remote.Writes);
        remote.Git.Refs[GitHubPublicationFactory.CreateCoordinationRef(authority)] = head;
        var competing = (JsonObject)remote.Prs[0].DeepClone();
        competing["id"] = 2; competing["number"] = 2; competing["node_id"] = "PR_2";
        competing["head"]!["ref"] = competing["head"]!["ref"]!.GetValue<string>() + "-other";
        remote.Prs.Add(competing);
        Assert.Equal(GitHubPublicationResultKind.Conflict, (await session.Publish()).Kind);
        remote.Prs.Reverse();
        Assert.Equal(GitHubPublicationResultKind.Conflict, (await session.Publish()).Kind);
        Assert.Equal(writes, remote.Writes);
    }

    [Fact]
    public async Task Invalid_local_payload_and_preflight_permission_failure_make_no_writes()
    {
        var remote = new Remote();
        var authority = Branch.Authority(remote.Git);
        using var session = new Session(remote, authority);
        var other = Branch.Authority(remote.Git, "other");
        Assert.Equal(GitHubPublicationResultKind.LocalInvalid,
            (await session.Reconciler.PublishAsync(authority, Branch.Payload(other))).Kind);
        Assert.Empty(remote.Git.Requests);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Equal(GitHubPublicationResultKind.Cancelled, (await session.Publish(cancelled.Token)).Kind);
        Assert.Empty(remote.Git.Requests);
        remote.RejectRead = path => path.EndsWith("/pulls", StringComparison.Ordinal) ? HttpStatusCode.Forbidden : null;
        Assert.Equal(GitHubPublicationResultKind.Permission, (await session.Publish()).Kind);
        Assert.Equal(0, remote.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ancestor_rewind_at_cas_is_never_overwritten(bool coordination)
    {
        var remote = new Remote();
        var first = Branch.Authority(remote.Git);
        using (var initial = new Session(remote, first)) { await initial.Publish(); await initial.Publish(); }
        byte[] bytes = [6, 7];
        var next = Branch.Authority(remote.Git, "rewind", first, bytes);
        string? claimOid = null;
        var injected = false;
        remote.BeforeMutation = (_, body) =>
        {
            var update = JsonNode.Parse(body!)?["variables"]?["input"]?["refUpdates"]?[0];
            if (update is null) return;
            var reference = update["name"]!.GetValue<string>();
            var after = update["afterOid"]!.GetValue<string>();
            if (reference.Contains("/coordination/", StringComparison.Ordinal))
            {
                var state = remote.Git.CoordinationState(after);
                if (state.Stage == GitHubCoordinationStage.Claimed) claimOid = after;
                if (coordination && state.Stage == GitHubCoordinationStage.ProposalRefAdvanced)
                { remote.Git.Refs[reference] = claimOid!; injected = true; }
            }
            else if (!coordination && reference.Contains("/proposals/", StringComparison.Ordinal))
            { remote.Git.Refs[reference] = remote.Git.BaseOid; injected = true; }
        };
        using (var append = new Session(remote, next, Branch.Payload(next, bytes)))
            Assert.Contains((await append.Publish()).Kind, new[] { GitHubPublicationResultKind.Conflict, GitHubPublicationResultKind.HumanChange });
        Assert.True(injected);
        remote.BeforeMutation = null;
        var writes = remote.Writes;
        using var restart = new Session(remote, next, Branch.Payload(next, bytes));
        Assert.Contains((await restart.Publish()).Kind, new[] { GitHubPublicationResultKind.Conflict, GitHubPublicationResultKind.HumanChange });
        Assert.Equal(writes, remote.Writes);
        Assert.Equal(1, remote.Posts);
    }

    [Theory]
    [InlineData("ready", (int)GitHubPublicationResultKind.AwaitingReview, (int)GitHubCoordinationStage.AwaitingReview)]
    [InlineData("merged", (int)GitHubPublicationResultKind.Merged, (int)GitHubCoordinationStage.Merged)]
    [InlineData("closed", (int)GitHubPublicationResultKind.ClosedUnmerged, (int)GitHubCoordinationStage.ClosedUnmerged)]
    public async Task Creation_recovery_records_the_observed_lifecycle_without_inventing_a_published_draft(string lifecycle, int outcome, int stage)
    {
        var remote = new Remote();
        var authority = Branch.Authority(remote.Git);
        using var session = new Session(remote, authority);
        await session.Publish();
        remote.AfterMutation = (_, body) =>
        {
            if (JsonNode.Parse(body!)?["draft"] is null) return;
            remote.Lifecycle(lifecycle);
            if (lifecycle == "merged") remote.Git.Refs["refs/heads/main"] = remote.Git.ProposalHead(authority)!;
        };
        Assert.Equal((GitHubPublicationResultKind)outcome, (await session.Publish()).Kind);
        Assert.Equal((GitHubCoordinationStage)stage, remote.State(authority).Stage);
        var writes = remote.Writes;
        Assert.Equal((GitHubPublicationResultKind)outcome, (await session.Publish()).Kind);
        Assert.Equal(writes, remote.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Target_move_after_unrecorded_content_or_ref_preserves_exact_residual_before_stale(bool afterRef)
    {
        var remote = new Remote();
        var authority = Branch.Authority(remote.Git);
        remote.AfterMutation = (_, body) =>
        {
            var node = JsonNode.Parse(body!);
            var hit = afterRef ? node?["variables"]?["input"]?["refUpdates"]?[0]?["name"]?.GetValue<string>()
                .Contains("/proposals/", StringComparison.Ordinal) == true
                : node?["message"]?.GetValue<string>().StartsWith("ContractScribe proposal", StringComparison.Ordinal) == true;
            if (hit) remote.Git.Refs["refs/heads/main"] = new string('9', 40);
        };
        using var session = new Session(remote, authority);
        Assert.Equal(GitHubPublicationResultKind.Stale, (await session.Publish()).Kind);
        remote.AfterMutation = null;
        using var restart = new Session(remote, authority);
        Assert.Equal(GitHubPublicationResultKind.Stale, (await restart.Publish()).Kind);
        var state = remote.State(authority);
        Assert.Equal(GitHubCoordinationStage.Stale, state.Stage);
        Assert.NotNull(state.ContentCommitOid);
        if (afterRef) Assert.Equal(remote.Git.ProposalHead(authority), state.ProposalCommitOid);
        var writes = remote.Writes;
        Assert.Equal(GitHubPublicationResultKind.Stale, (await restart.Publish()).Kind);
        Assert.Equal(writes, remote.Writes);
        Assert.Equal(0, remote.Posts);
    }

    [Fact]
    public async Task Private_receipts_and_resource_proofs_reject_counterfeit_or_foreign_consumers()
    {
        var remote = new Remote();
        var authority = Branch.Authority(remote.Git);
        using var session = new Session(remote, authority);
        await session.Publish();
        var right = (IGitHubPullRequestEntitlement)typeof(GitHubPublicationReconciler)
            .GetField("createRight", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session.Reconciler)!;
        var owner = GitHubCoordinationStore.Create(session.Client);
        using var foreign = new Session(remote, authority);
        Assert.Throws<ArgumentException>(() => GitHubProposalStore.Create(session.Client, GitHubCoordinationStore.Create(foreign.Client)));
        var state = (await owner.ReadCurrentAsync()).State!;
        var store = GitHubProposalPullRequestStore.Create(session.Client, owner, Publisher);
        var head = new GitHubProposalHead(GitHubPublicationFactory.CreateProposalRef(authority), state.ProposalCommitOid!, state.ProposalTreeOid!);
        var writes = remote.Writes;
        Assert.Equal(ContractScribe.GitHub.PullRequests.GitHubProposalOutcome.Conflict,
            (await store.CreateAuthorizedAsync(state, head, new CounterfeitRight())).Outcome);
        Assert.Equal(ContractScribe.GitHub.PullRequests.GitHubProposalOutcome.Conflict,
            (await store.CreateAuthorizedAsync(state, head, right)).Outcome);
        Assert.Equal(writes, remote.Writes);
        Assert.Equal(GitHubPublicationResultKind.Published, (await session.Publish()).Kind);
        var read = await owner.ReadCurrentAsync();
        var resources = await GitHubProposalStore.Create(session.Client, owner).InspectAsync(read.Read!, session.Payload);
        var proof = (await store.ObserveVerifiedAsync(read.State!, resources.Inspection!)).Observation!;
        var requests = remote.Git.Requests.Count;
        Assert.Equal(GitHubCoordinationFailureKind.InvalidInput,
            (await owner.RecordObservationAsync(new CounterfeitObservation(proof))).Failure!.Kind);
        Assert.Equal(ContractScribe.GitHub.PullRequests.GitHubProposalOutcome.Conflict,
            (await store.ObserveVerifiedAsync(read.State!, new CounterfeitInspection(resources.Inspection!))).Outcome);
        Assert.Equal(requests, remote.Git.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Complete_pagination_precedes_ready_selection(bool failedLastPage)
    {
        var remote = new Remote();
        using var session = new Session(remote, Branch.Authority(remote.Git));
        await session.Publish(); await session.Publish();
        remote.Lifecycle("ready");
        var other = (JsonObject)remote.Prs[0].DeepClone();
        other["id"] = 2; other["number"] = 2; other["node_id"] = "PR_2";
        other["head"]!["ref"] = other["head"]!["ref"]!.GetValue<string>() + "-old";
        other["state"] = "closed"; other["closed_at"] = "2026-01-02T00:00:00Z"; other["body"] = "historical";
        remote.Prs.Add(other); remote.Paginate = true; remote.FailLastPage = failedLastPage;
        var writes = remote.Writes;
        Assert.Equal(failedLastPage ? GitHubPublicationResultKind.HostFailure : GitHubPublicationResultKind.AwaitingReview,
            (await session.Publish()).Kind);
        if (failedLastPage) Assert.Equal(writes, remote.Writes);
        Assert.Equal(1, remote.Git.ProposalWrites);
    }

    [Fact]
    public async Task Cancellation_after_post_recovers_without_resending_and_owner_deadline_is_timeout()
    {
        foreach (var timeout in new[] { false, true })
        {
            var remote = new Remote();
            var authority = Branch.Authority(remote.Git);
            using var session = new Session(remote, authority);
            await session.Publish();
            using var cancel = new CancellationTokenSource();
            if (timeout)
            {
                var store = typeof(GitHubPublicationReconciler).GetField("prs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session.Reconciler)!;
                typeof(GitHubProposalPullRequestStore).GetField("recoveryTimeout", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(store, TimeSpan.FromMilliseconds(25));
                remote.DelayPrReads = true;
            }
            else remote.AfterMutation = (_, body) => { if (JsonNode.Parse(body!)?["draft"] is not null) cancel.Cancel(); };
            Assert.Equal(timeout ? GitHubPublicationResultKind.Timeout : GitHubPublicationResultKind.Cancelled,
                (await session.Publish(cancel.Token)).Kind);
            remote.AfterMutation = null; remote.DelayPrReads = false;
            using var restart = new Session(remote, authority);
            Assert.Equal(GitHubPublicationResultKind.Published, (await restart.Publish()).Kind);
            Assert.Equal(1, remote.PrAttempts);
        }
    }

    [Fact]
    public async Task A_closed_pr_hidden_behind_a_rewound_stale_record_cannot_authorize_a_new_initial_generation()
    {
        var remote = new Remote();
        var first = Branch.Authority(remote.Git);
        using var initial = new Session(remote, first);
        await initial.Publish(); await initial.Publish();
        remote.Lifecycle("closed");
        var reference = GitHubPublicationFactory.CreateCoordinationRef(first);
        var head = remote.Git.Refs[reference];
        while (remote.Git.CoordinationState(head).Stage != GitHubCoordinationStage.ContentCreated)
            head = Assert.Single(remote.Git.Commits[head].Parents);
        remote.Git.Refs[reference] = head;
        remote.Git.MergedBase();
        var owner = GitHubCoordinationStore.Create(initial.Client);
        var current = (await owner.ReadCurrentAsync()).State!;
        var stale = await owner.AdvanceAsync(current, GitHubCoordinationStageUpdate.Stale(first.ExpectedBaseCommitOid, remote.Git.BaseOid));
        Assert.Equal(GitHubCoordinationStage.Stale, stale.State!.Stage);
        Assert.Null(stale.State.ProposalCommitOid);
        byte[] bytes = [9, 8];
        var next = GitHubPublicationFactory.CreateAuthority(Input(first) with
        {
            ExpectedBaseCommitOid = remote.Git.BaseOid,
            SnapshotCommitmentSha256 = new string('a', 64),
            OperationId = "unauthorized-replacement",
            GenerationId = "new-generation",
            CandidateCommitmentSha256 = Hash(bytes),
            ChangedFiles = [first.ChangedFiles[0] with { OriginalFileSha256 = Hash(Branch.Candidate), CandidateFileSha256 = Hash(bytes) }],
        });
        var writes = remote.Writes;
        using var replacement = new Session(remote, next, Branch.Payload(next, bytes));
        Assert.Equal(GitHubPublicationResultKind.Conflict, (await replacement.Publish()).Kind);
        Assert.Equal(writes, remote.Writes);
        Assert.Equal(1, remote.Posts);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("exact")]
    [InlineData("foreign")]
    public async Task Fresh_initial_requires_the_complete_stale_content_and_ref_residual(string residual)
    {
        var remote = new Remote();
        var first = Branch.Authority(remote.Git);
        using var original = new Session(remote, first);
        var owner = GitHubCoordinationStore.Create(original.Client);
        var read = await owner.ReadCurrentAsync();
        var claim = (await owner.ClaimAsync(read.Read!)).State!;
        var git = GitHubProposalStore.Create(original.Client, owner);
        var prepared = await git.PrepareAsync(claim, Branch.Payload(first));
        var content = (await git.CreateContentAsync(prepared.Prepared!, claim)).Content!;
        remote.Git.MergedBase();
        var stale = await owner.AdvanceAsync(claim, GitHubCoordinationStageUpdate.ContentCreated(content.CommitOid));
        Assert.Equal(GitHubCoordinationStage.Stale, stale.State!.Stage);
        Assert.Null(stale.State.ProposalCommitOid);
        if (residual != "absent") remote.Git.Refs[GitHubPublicationFactory.CreateProposalRef(first)] =
            residual == "exact" ? content.CommitOid : first.ExpectedBaseCommitOid;
        byte[] bytes = [8, 9];
        var next = GitHubPublicationFactory.CreateAuthority(Input(first) with
        {
            ExpectedBaseCommitOid = remote.Git.BaseOid,
            SnapshotCommitmentSha256 = new string('a', 64),
            OperationId = "fresh-replacement",
            GenerationId = "fresh-generation",
            CandidateCommitmentSha256 = Hash(bytes),
            ChangedFiles = [first.ChangedFiles[0] with { OriginalFileSha256 = Hash(Branch.Candidate), CandidateFileSha256 = Hash(bytes) }],
        });
        var writes = remote.Writes;
        using var replacement = new Session(remote, next, Branch.Payload(next, bytes));
        var result = await replacement.Publish();
        if (residual == "foreign")
        {
            Assert.Equal(GitHubPublicationResultKind.HumanChange, result.Kind);
            Assert.Equal(writes, remote.Writes);
            Assert.Equal(first.OperationCommitmentSha256, remote.State(first).OperationCommitmentSha256);
        }
        else
        {
            Assert.Equal(GitHubPublicationResultKind.RecoveredRefPartial, result.Kind);
            Assert.Equal(GitHubPublicationResultKind.Published, (await replacement.Publish()).Kind);
        }
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task Nonterminal_result_recording_rejects_drift_at_both_target_gates(int driftRead, bool ready)
    {
        var remote = new Remote();
        var authority = Branch.Authority(remote.Git);
        using var session = new Session(remote, authority);
        await session.Publish();
        var previousHead = remote.Git.Refs[GitHubPublicationFactory.CreateCoordinationRef(authority)];
        var details = 0;
        var recording = false;
        var targets = 0;
        remote.AfterMutation = (_, body) =>
        {
            if (ready && JsonNode.Parse(body!)?["draft"] is not null) remote.Lifecycle("ready");
        };
        remote.AfterRead = path =>
        {
            if (path == "/repos/Owner/repo/pulls/1" && ++details == 2) recording = true;
        };
        remote.BeforeRead = path =>
        {
            if (recording && path.EndsWith("/git/ref/heads/main", StringComparison.Ordinal) && ++targets == driftRead)
                remote.Git.MergedBase();
        };
        Assert.Equal(GitHubPublicationResultKind.Stale, (await session.Publish()).Kind);
        Assert.True(recording);
        Assert.True(targets >= driftRead);
        Assert.Equal(previousHead, remote.Git.Refs[GitHubPublicationFactory.CreateCoordinationRef(authority)]);
        Assert.Equal(GitHubCoordinationStage.ProposalRefAdvanced, remote.State(authority).Stage);
        remote.BeforeRead = null; remote.AfterRead = null; remote.AfterMutation = null;
        var writes = remote.Writes;
        await session.Publish();
        Assert.Equal(writes, remote.Writes);
    }

    [Theory]
    [InlineData(false, 401, false)]
    [InlineData(false, 403, false)]
    [InlineData(false, 429, false)]
    [InlineData(true, 401, false)]
    [InlineData(true, 403, false)]
    [InlineData(true, 429, false)]
    [InlineData(false, 403, true)]
    [InlineData(true, 403, true)]
    public async Task Object_recovery_distinguishes_empty_lookup_from_failed_read(bool proposal, int status, bool failedRecovery)
    {
        var remote = new Remote();
        var authority = Branch.Authority(remote.Git);
        using var session = new Session(remote, authority);
        if (proposal)
        {
            var owner = GitHubCoordinationStore.Create(session.Client);
            var read = await owner.ReadCurrentAsync();
            Assert.NotNull((await owner.ClaimAsync(read.Read!)).State);
        }
        var rejected = false;
        remote.RejectMutation = path =>
        {
            if (!path.EndsWith("/git/blobs", StringComparison.Ordinal)) return null;
            rejected = true;
            return (HttpStatusCode)status;
        };
        remote.RejectRead = path => rejected && failedRecovery && path.Contains("/git/blobs/", StringComparison.Ordinal)
            ? HttpStatusCode.ServiceUnavailable : null;
        var writes = remote.Writes;
        var expected = failedRecovery ? GitHubPublicationResultKind.HostFailure
            : status == 429 ? GitHubPublicationResultKind.RateLimit : GitHubPublicationResultKind.Permission;
        Assert.Equal(expected, (await session.Publish()).Kind);
        Assert.True(rejected);
        Assert.Equal(writes, remote.Writes);
        Assert.Equal(0, remote.PrAttempts);
    }

    [Fact]
    public async Task Authenticated_content_recovery_preserves_late_target_drift_over_response_loss()
    {
        var remote = new Remote();
        var authority = Branch.Authority(remote.Git);
        remote.AfterMutation = (_, body) =>
        {
            if (JsonNode.Parse(body!)?["message"]?.GetValue<string>().StartsWith("ContractScribe proposal", StringComparison.Ordinal) != true) return;
            remote.Git.MergedBase();
            throw new IOException("synthetic response loss after committed content");
        };
        using var session = new Session(remote, authority);
        Assert.Equal(GitHubPublicationResultKind.Stale, (await session.Publish()).Kind);
        Assert.Equal(0, remote.Git.ProposalWrites);
        Assert.Equal(0, remote.PrAttempts);
        remote.AfterMutation = null;
        Assert.Equal(GitHubPublicationResultKind.Stale, (await session.Publish()).Kind);
        var writes = remote.Writes;
        Assert.Equal(GitHubPublicationResultKind.Stale, (await session.Publish()).Kind);
        Assert.Equal(writes, remote.Writes);
    }

    [Theory]
    [InlineData("401", (int)GitHubPublicationResultKind.Permission)]
    [InlineData("403", (int)GitHubPublicationResultKind.Permission)]
    [InlineData("429", (int)GitHubPublicationResultKind.RateLimit)]
    [InlineData("cancel", (int)GitHubPublicationResultKind.Cancelled)]
    [InlineData("timeout", (int)GitHubPublicationResultKind.Timeout)]
    [InlineData("failed-recovery", (int)GitHubPublicationResultKind.HostFailure)]
    [InlineData("divergent", (int)GitHubPublicationResultKind.Conflict)]
    [InlineData("exact", (int)GitHubPublicationResultKind.RecoveredContentPartial)]
    public async Task Coordination_cas_recovery_distinguishes_unchanged_divergent_and_exact_heads(string scenario, int expected)
    {
        var remote = new Remote();
        var authority = Branch.Authority(remote.Git);
        using var session = new Session(remote, authority, timeoutMs: scenario == "timeout" ? 1000 : 30_000);
        var owner = GitHubCoordinationStore.Create(session.Client);
        var read = await owner.ReadCurrentAsync();
        var claim = (await owner.ClaimAsync(read.Read!)).State!;
        var reference = GitHubPublicationFactory.CreateCoordinationRef(authority);
        var refPath = "/repos/Owner/repo/git/ref/" + reference[5..];
        using var cancel = new CancellationTokenSource();
        var attempts = 0;
        remote.BeforeMutation = (_, body) =>
        {
            var update = JsonNode.Parse(body!)?["variables"]?["input"]?["refUpdates"]?[0];
            if (update is null) return;
            Assert.Equal(reference, update["name"]!.GetValue<string>());
            Assert.Equal(claim.HeadOid, update["beforeOid"]!.GetValue<string>());
            attempts++;
            if (scenario == "divergent") remote.Git.Refs[reference] = authority.ExpectedBaseCommitOid;
        };
        remote.BeforeCas = async token =>
        {
            if (scenario == "cancel") cancel.Cancel();
            if (scenario is "cancel" or "timeout") await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        remote.RejectMutation = path => path != "/graphql" || scenario is "cancel" or "timeout" or "exact" ? null
            : (HttpStatusCode)(int.TryParse(scenario, out var status) ? status : 403);
        remote.RejectRead = path => scenario == "failed-recovery" && attempts > 0 && path == refPath
            ? HttpStatusCode.ServiceUnavailable : null;
        remote.AfterMutation = (_, body) =>
        {
            if (scenario == "exact" && JsonNode.Parse(body!)?["variables"] is not null)
                throw new IOException("synthetic acknowledged-state response loss");
        };
        Assert.Equal((GitHubPublicationResultKind)expected, (await session.Publish(cancel.Token)).Kind);
        Assert.Equal(1, attempts);
        Assert.Equal(0, remote.Git.ProposalWrites);
        Assert.Equal(0, remote.PrAttempts);
        if (scenario == "exact") Assert.Equal(GitHubCoordinationStage.ContentCreated, remote.State(authority).Stage);
        else Assert.Equal(scenario == "divergent" ? authority.ExpectedBaseCommitOid : claim.HeadOid, remote.Git.Refs[reference]);
        remote.BeforeMutation = null; remote.BeforeCas = null; remote.AfterMutation = null;
        remote.RejectMutation = null; remote.RejectRead = null;
        var writes = remote.Writes;
        using var restart = new Session(remote, authority);
        await restart.Publish();
        Assert.Equal(writes, remote.Writes);
        Assert.Equal(0, remote.Git.ProposalWrites);
        Assert.Equal(0, remote.PrAttempts);
    }

    private sealed class CounterfeitRight : IGitHubPullRequestEntitlement;
    private sealed class CounterfeitObservation(IGitHubProposalObservation source) : IGitHubProposalObservation
    {
        public ContractScribe.GitHub.PullRequests.GitHubProposalOutcome Outcome => source.Outcome;
        public GitHubPullRequest PullRequest => source.PullRequest;
        public GitHubProposalMetadata Metadata => source.Metadata;
        public IGitHubCoordinationStateCapability Coordination => source.Coordination;
    }
    private sealed class CounterfeitInspection(IGitHubInspectedProposal source) : IGitHubInspectedProposal
    {
        public string Ref => source.Ref;
        public string CommitOid => source.CommitOid;
        public string TreeOid => source.TreeOid;
        public string BeforeOid => source.BeforeOid;
        public string ObservedRefOid => source.ObservedRefOid;
        public bool ContentExists => source.ContentExists;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static GitHubPublicationAuthorityInput Input(ValidatedGitHubPublicationAuthority a) => new(
        a.RepositoryOwner, a.RepositoryName, a.TargetRef, a.ExpectedBaseCommitOid, a.CampaignLineage,
        a.SnapshotCommitmentSha256, a.ExecutionCommitmentSha256, a.WorkPlanCommitmentSha256, a.CheckpointRevision,
        a.CheckpointSha256, a.CandidateCommitmentSha256, a.PatchRequestSha256, a.PatchResultCommitmentSha256,
        a.AcceptedProjectionCommitmentSha256, a.OperationId, a.GenerationId, a.PrecedingOperationId,
        a.PrecedingAuthorityCommitmentSha256, a.PrecedingCandidateCommitmentSha256, a.PrecedingGenerationId,
        a.PrecedingSnapshotCommitmentSha256, a.PrecedingPolicyCommitmentSha256, a.TerminalPredecessor, a.Transition,
        a.AcceptedM4Ceilings, a.Policy, a.ChangedFiles, a.PrecedingChangedFiles, a.ClosedUnmergedSuccessorAuthorization);

    private sealed class Session : IDisposable
    {
        private readonly GitHubApiClient client;
        internal GitHubApiClient Client => client;
        internal readonly GitHubPublicationReconciler Reconciler;
        internal readonly ValidatedGitHubPublicationAuthority Authority;
        internal readonly ValidatedGitHubChangedFilePayload Payload;
        internal Session(Remote remote, ValidatedGitHubPublicationAuthority authority, ValidatedGitHubChangedFilePayload? payload = null,
            int timeoutMs = 30_000)
        {
            Authority = authority;
            Payload = payload ?? Branch.Payload(authority);
            using var hook = (IDisposable)typeof(GitHubTransportTestHook).GetMethod("Register", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [new Uri("http://127.0.0.1:43219/"), new Handler(remote), timeoutMs])!;
            client = GitHubApiClient.Create(authority, GitHubTransportTestHook.Placeholder);
            Reconciler = GitHubPublicationReconciler.Create(client, Publisher);
        }
        internal ValueTask<GitHubPublicationResult> Publish(CancellationToken token = default) => Reconciler.PublishAsync(Authority, Payload, token);
        public void Dispose() => client.Dispose();
    }

    private sealed class Handler(Remote remote) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => remote.Reply(request, cancellationToken);
    }

    // Reuse the real Git object/CAS fake; this extension owns only PR wire behavior.
    private sealed class Remote
    {
        internal readonly Branch.Remote Git = new();
        internal readonly List<JsonObject> Prs = [];
        internal int Posts;
        internal int Writes => Git.Writes + Posts;
        internal Action? BeforePost;
        internal HttpStatusCode? PostError;
        internal int Mutations;
        internal int PrAttempts;
        internal int? LoseAt;
        internal bool LoseAfter;
        internal Action<int, string?>? AfterMutation;
        internal Action<int, string?>? BeforeMutation;
        internal Func<string, HttpStatusCode?>? RejectRead;
        internal Func<string, HttpStatusCode?>? RejectMutation;
        internal Func<CancellationToken, Task>? BeforeCas;
        internal Action<string>? BeforeRead, AfterRead;
        internal bool Paginate, FailLastPage, DelayPrReads;
        internal GitHubCoordinationState State(ValidatedGitHubPublicationAuthority authority) =>
            Git.CoordinationState(Git.Refs[GitHubPublicationFactory.CreateCoordinationRef(authority)]);
        internal void Lifecycle(string kind)
        {
            var pr = Prs[0];
            var closed = kind is "closed" or "merged";
            pr["state"] = closed ? "closed" : "open";
            pr["draft"] = kind == "draft";
            pr["merged"] = kind == "merged";
            pr["closed_at"] = closed ? "2026-01-02T00:00:00Z" : null;
            pr["merged_at"] = kind == "merged" ? "2026-01-02T00:00:00Z" : null;
        }
        internal async Task<HttpResponseMessage> Reply(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Get) BeforeRead?.Invoke(request.RequestUri!.AbsolutePath);
            if (DelayPrReads && Posts > 0 && request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.Contains("/pulls", StringComparison.Ordinal))
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            if (request.Method == HttpMethod.Get && RejectRead?.Invoke(request.RequestUri!.AbsolutePath) is { } rejected)
                return Json(rejected, new { message = "synthetic read rejection" });
            var mutation = request.Method == HttpMethod.Post;
            var index = mutation ? Interlocked.Increment(ref Mutations) : 0;
            var body = mutation ? await request.Content!.ReadAsStringAsync(token) : null;
            if (mutation && request.RequestUri!.AbsolutePath == "/repos/Owner/repo/pulls") Interlocked.Increment(ref PrAttempts);
            if (mutation) BeforeMutation?.Invoke(index, body);
            if (mutation && request.RequestUri!.AbsolutePath == "/graphql" && BeforeCas is not null) await BeforeCas(token);
            if (mutation && RejectMutation?.Invoke(request.RequestUri!.AbsolutePath) is { } rejection)
                return Json(rejection, new { message = "synthetic mutation rejection" });
            if (index == LoseAt && !LoseAfter) throw new IOException("synthetic pre-application loss");
            var response = await ReplyCore(request, token);
            if (!mutation) AfterRead?.Invoke(request.RequestUri!.AbsolutePath);
            if (mutation) AfterMutation?.Invoke(index, body);
            if (index == LoseAt && LoseAfter) throw new IOException("synthetic response loss");
            return response;
        }
        private async Task<HttpResponseMessage> ReplyCore(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (!path.StartsWith("/repos/Owner/repo/pulls", StringComparison.Ordinal)) return await Git.Reply(request, token);
            var text = request.Method == HttpMethod.Post ? await request.Content!.ReadAsStringAsync(token) : null;
            lock (Git.SyncRoot)
            {
                Git.Requests.Add((request.Method.Method, path));
                foreach (var pr in Prs)
                {
                    if (Git.Refs.TryGetValue("refs/heads/" + pr["head"]!["ref"]!.GetValue<string>(), out var head)) pr["head"]!["sha"] = head;
                    if (pr["state"]!.GetValue<string>() == "open") pr["base"]!["sha"] = Git.Refs["refs/heads/main"];
                }
                if (request.Method == HttpMethod.Post)
                {
                    Posts++;
                    BeforePost?.Invoke(); BeforePost = null;
                    if (PostError is { } error) return Json(error, new { message = "synthetic publication rejection" });
                    var payload = JsonNode.Parse(text!)!;
                    Assert.True(payload["draft"]!.GetValue<bool>());
                    Assert.False(payload["maintainer_can_modify"]!.GetValue<bool>());
                    if (Prs.Any(p => p["state"]!.GetValue<string>() == "open")) return Json(HttpStatusCode.UnprocessableEntity, new { message = "duplicate" });
                    var number = Prs.Count + 1;
                    var pr = JsonSerializer.SerializeToNode(new
                    {
                        id = number,
                        node_id = "PR_" + number,
                        number,
                        state = "open",
                        draft = true,
                        merged = false,
                        merged_at = (string?)null,
                        closed_at = (string?)null,
                        created_at = "2026-01-01T00:00:00Z",
                        title = payload["title"]!.GetValue<string>(),
                        body = payload["body"]!.GetValue<string>(),
                        user = new { id = Publisher.Id, node_id = Publisher.NodeId, login = Publisher.Login, type = "Bot" },
                        head = new { @ref = payload["head"]!.GetValue<string>(), sha = Git.Refs["refs/heads/" + payload["head"]!.GetValue<string>()], repo = Repo() },
                        @base = new { @ref = payload["base"]!.GetValue<string>(), sha = Git.Refs["refs/heads/main"], repo = Repo() },
                        maintainer_can_modify = false,
                    })!.AsObject();
                    Prs.Add(pr);
                    return Json(HttpStatusCode.Created, pr);
                }
                Assert.Equal(HttpMethod.Get, request.Method);
                if (path == "/repos/Owner/repo/pulls")
                {
                    var page2 = request.RequestUri.Query.Contains("page=2", StringComparison.Ordinal);
                    if (page2 && FailLastPage) return Json(HttpStatusCode.ServiceUnavailable, new { message = "synthetic final page failure" });
                    var response = Json(HttpStatusCode.OK, Paginate ? (page2 ? Prs.Skip(1) : Prs.Take(1)).ToArray() : Prs.ToArray());
                    if (Paginate && !page2) response.Headers.TryAddWithoutValidation("Link",
                        "<http://127.0.0.1:43219/repos/Owner/repo/pulls?state=all&sort=created&direction=asc&per_page=100&page=2>; rel=\"next\"");
                    return response;
                }
                var found = Prs.SingleOrDefault(p => p["number"]!.GetValue<int>().ToString() == path.Split('/')[^1]);
                return found is null ? Json(HttpStatusCode.NotFound, new { message = "missing" }) : Json(HttpStatusCode.OK, found);
            }
        }
        private object Repo() => new { id = Git.RepositoryId, node_id = "REPO_node", name = "repo", full_name = "Owner/repo", owner = new { login = "Owner", id = 1, node_id = "OWNER_node", type = "Organization" } };
        private static HttpResponseMessage Json(HttpStatusCode status, object value) => new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
