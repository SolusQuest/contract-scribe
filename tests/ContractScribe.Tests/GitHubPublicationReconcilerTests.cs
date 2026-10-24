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
public sealed partial class GitHubPublicationReconcilerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Original_send_fence_rechecks_the_proposal_ref_after_immutable_preparation(bool deleted)
    {
        var remote = new Remote();
        var authority = Branch.Authority(remote.Git);
        using var session = new Session(remote, authority);
        Assert.Equal(GitHubPublicationResultKind.RecoveredRefPartial, (await session.Publish()).Kind);
        var before = remote.State(authority);
        var head = remote.Git.Refs[GitHubPublicationFactory.CreateCoordinationRef(authority)];
        var reference = GitHubPublicationFactory.CreateProposalRef(authority);
        var injected = false;
        remote.AfterMutation = (_, body) =>
        {
            var request = JsonNode.Parse(body ?? "null");
            if (request?["message"]?.GetValue<string>() != "ContractScribe campaign coordination v1\n") return;
            var root = remote.Git.Trees[request["tree"]!.GetValue<string>()];
            var leaf = remote.Git.Trees[Assert.Single(root).Oid];
            var publication = leaf.SingleOrDefault(entry => entry.Path == GitHubCoordinationObjects.EventPath);
            if (publication is null || GitHubPublicationEvent.Decode(remote.Git.Blobs[publication.Oid]).State.Stage
                != GitHubCoordinationStage.PullRequestCreateStarted) return;
            if (deleted) remote.Git.Refs.Remove(reference);
            else remote.Git.Refs[reference] = authority.ExpectedBaseCommitOid;
            injected = true;
            remote.AfterMutation = null;
        };
        Assert.Equal(GitHubPublicationResultKind.Conflict, (await session.Publish()).Kind);
        Assert.True(injected);
        Assert.Equal(head, remote.Git.Refs[GitHubPublicationFactory.CreateCoordinationRef(authority)]);
        Assert.Equal(before.ResultSetCommitmentSha256, remote.State(authority).ResultSetCommitmentSha256);
        Assert.Equal(GitHubCoordinationStage.ProposalRefAdvanced, remote.State(authority).Stage);
        Assert.Equal(0, remote.PrAttempts);
        var writes = remote.Writes;
        using var restart = new Session(remote, authority);
        Assert.Contains((await restart.Publish()).Kind,
            new[] { GitHubPublicationResultKind.Conflict, GitHubPublicationResultKind.HumanChange });
        Assert.Equal(writes, remote.Writes);
        Assert.Equal(0, remote.PrAttempts);
    }

    private static readonly GitHubActor Publisher = new(99, "BOT_99", "github-actions[bot]", GitHubActorKind.Bot);

    [Theory]
    [InlineData("required-read")]
    [InlineData("applied-claim")]
    [InlineData("acknowledged-tree")]
    public async Task Observed_failures_preserve_owner_boundary_and_the_ordinary_request_trace(string fault)
    {
        async Task<(GitHubPublicationAttempt Attempt, Remote Remote)> Run(bool observed)
        {
            var remote = new Remote();
            var authority = Branch.Authority(remote.Git);
            using var session = new Session(remote, authority);
            if (fault == "acknowledged-tree")
            {
                var owner = GitHubCoordinationStore.Create(session.Client);
                var read = await owner.ReadCurrentAsync();
                Assert.NotNull((await owner.ClaimAsync(read.Read!)).State);
            }
            var applied = false;
            remote.AfterMutation = (_, body) =>
            {
                var payload = JsonNode.Parse(body!);
                if (fault == "applied-claim" && payload?["variables"] is not null
                    || fault == "acknowledged-tree" && payload?["tree"] is JsonArray) applied = true;
            };
            remote.RejectRead = path => fault == "required-read" && path == "/repos/Owner/repo/git/ref/heads/main"
                ? HttpStatusCode.Conflict
                : applied && (fault == "applied-claim" && path.Contains("/git/ref/heads/contract-scribe/coordination/", StringComparison.Ordinal)
                    || fault == "acknowledged-tree" && path.Contains("/git/trees/", StringComparison.Ordinal))
                    ? HttpStatusCode.NotFound : null;
            var attempt = observed ? await session.Observe() : new GitHubPublicationAttempt(await session.Publish());
            Assert.Equal(GitHubPublicationResultKind.Conflict, attempt.Result.Kind);
            Assert.Equal(0, remote.PrAttempts);
            Assert.Equal(0, remote.Git.ProposalWrites);
            if (fault != "required-read") Assert.Equal(GitHubCoordinationStage.Claimed, remote.State(authority).Stage);
            else Assert.Equal(0, remote.Writes);
            return (attempt, remote);
        }

        var observed = await Run(true);
        var ordinary = await Run(false);
        Assert.Equal(ordinary.Remote.Attempts, observed.Remote.Attempts);
        Assert.Equal(ordinary.Remote.Mutations, observed.Remote.Mutations);
        Assert.Equal(ordinary.Remote.Writes, observed.Remote.Writes);
        var diagnostic = Assert.IsType<GitHubPublicationDiagnostic>(observed.Attempt.Diagnostic);
        Assert.True(diagnostic.IsValid);
        Assert.Equal(fault switch
        {
            "required-read" => GitHubPublicationBoundary.CoordinationRead,
            "applied-claim" => GitHubPublicationBoundary.CoordinationClaim,
            _ => GitHubPublicationBoundary.GitCreateContent,
        }, diagnostic.Boundary);
        if (fault == "required-read")
        {
            Assert.Equal(GitHubPublicationOwner.Coordination, diagnostic.Owner);
            Assert.Equal(GitHubFailureCode.Conflict, diagnostic.TransportCode);
            Assert.Equal(409, diagnostic.TransportHttpStatus);
            Assert.Null(diagnostic.RecoveryCode);
        }
        else
        {
            Assert.Null(diagnostic.TransportCode);
            Assert.Equal(GitHubFailureCode.NotFound, diagnostic.RecoveryCode);
            Assert.Equal(404, diagnostic.RecoveryHttpStatus);
            Assert.Equal(GitHubDelivery.NeedsReadback, diagnostic.Delivery);
            if (fault == "acknowledged-tree")
            {
                Assert.Equal(GitHubPublicationOwner.GitData, diagnostic.Owner);
                Assert.Equal(GitHubProposalFailureKind.Unresolved, diagnostic.ProposalFailure);
                Assert.Equal(GitHubObjectKind.Tree, diagnostic.ObjectKind);
            }
        }
    }

    [Fact]
    public async Task Invalid_correlation_is_call_local_and_never_dispatches()
    {
        var remote = new Remote();
        var authority = Branch.Authority(remote.Git);
        using var session = new Session(remote, authority);
        var other = Branch.Authority(remote.Git, "other-operation");
        var invalid = await session.Reconciler.PublishObservedAsync(other, session.Payload);
        Assert.Equal(GitHubPublicationResultKind.LocalInvalid, invalid.Result.Kind);
        Assert.Equal(GitHubPublicationPredicate.InvalidCorrelation, invalid.Diagnostic!.Predicate);
        Assert.Empty(remote.Attempts);
        Assert.Null((await session.Observe()).Diagnostic);
        Assert.Equal(GitHubPublicationPredicate.InvalidCorrelation, invalid.Diagnostic.Predicate);
    }

    [Fact]
    public async Task Cancelled_queued_call_cannot_contaminate_the_active_call_or_later_publication()
    {
        var remote = new Remote();
        using var session = new Session(remote, Branch.Authority(remote.Git));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        remote.BeforeCas = async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        var active = session.Observe().AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var cancel = new CancellationTokenSource();
        var queued = session.Observe(cancel.Token).AsTask();
        cancel.Cancel();
        var failed = await queued;
        Assert.Equal(GitHubPublicationResultKind.Cancelled, failed.Result.Kind);
        Assert.Equal(GitHubPublicationPredicate.Cancelled, failed.Diagnostic!.Predicate);
        remote.BeforeCas = null;
        release.SetResult();
        var partial = await active;
        Assert.Equal(GitHubPublicationResultKind.RecoveredRefPartial, partial.Result.Kind);
        Assert.Null(partial.Diagnostic);
        var published = await session.Observe();
        Assert.Equal(GitHubPublicationResultKind.Published, published.Result.Kind);
        Assert.Null(published.Diagnostic);
        Assert.Equal(GitHubPublicationPredicate.Cancelled, failed.Diagnostic.Predicate);
        Assert.Equal(1, remote.Posts);
        Assert.Equal(1, remote.Git.ProposalWrites);
    }

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
    public async Task Fresh_original_ref_recovery_revalidates_and_sends_once_before_the_durable_fence()
    {
        var remote = new Remote();
        var authority = Branch.Authority(remote.Git);
        using (var first = new Session(remote, authority))
            Assert.Equal(GitHubPublicationResultKind.RecoveredRefPartial, (await first.Publish()).Kind);
        using var restart = new Session(remote, authority);
        Assert.Equal(GitHubPublicationResultKind.Published, (await restart.Publish()).Kind);
        var writes = remote.Writes;
        Assert.Equal(GitHubPublicationResultKind.ReplayNoOp, (await restart.Publish()).Kind);
        Assert.Equal(writes, remote.Writes);
        Assert.Equal(1, remote.Posts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sealed_publication_refuses_changed_result_and_new_path_without_mutation(bool includeNew)
    {
        var remote = new Remote();
        var first = Branch.Authority(remote.Git);
        using (var session = new Session(remote, first)) { await session.Publish(); await session.Publish(); }
        var original = remote.Prs[0].ToJsonString();
        var head = remote.Git.ProposalHead(first);
        var writes = remote.Writes;
        byte[] bytes = [4, 5, 6];
        var next = Branch.Authority(remote.Git, "replacement", first, bytes, includeNew: includeNew);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var replacement = new Session(remote, next, Branch.Payload(next, bytes, includeNew));
            Assert.Equal(GitHubPublicationResultKind.Conflict, (await replacement.Publish()).Kind);
            Assert.Equal(writes, remote.Writes);
            Assert.Equal(head, remote.Git.ProposalHead(first));
            Assert.Equal(original, remote.Prs[0].ToJsonString());
        }
        Assert.Equal(1, remote.Posts);
        Assert.Equal(1, remote.Git.ProposalWrites);
    }

    [Fact]
    public async Task A_new_operation_with_identical_bytes_still_cannot_reopen_a_sealed_result_set()
    {
        var remote = new Remote();
        var first = Branch.Authority(remote.Git);
        using var initial = new Session(remote, first);
        await initial.Publish(); await initial.Publish();
        var writes = remote.Writes;
        var next = Branch.Authority(remote.Git, "new-operation");
        using var replacement = new Session(remote, next);
        Assert.Equal(GitHubPublicationResultKind.Conflict, (await replacement.Publish()).Kind);
        Assert.Equal(writes, remote.Writes);
        Assert.Single(remote.Prs);
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
    public async Task Original_operation_recovers_atomically_recorded_ref_after_failed_readback()
    {
        var remote = new Remote();
        var first = Branch.Authority(remote.Git);
        byte[] bytes = [9, 8, 7];
        var next = Branch.Authority(remote.Git, "original-partial", candidate: bytes);
        // Stop the first read after the paired CAS; both remote effects must remain recoverable.
        var failReads = false;
        remote.AfterMutation = (_, body) =>
        {
            if (body?.Contains("/proposals/", StringComparison.Ordinal) == true) failReads = true;
        };
        remote.RejectRead = _ => failReads ? HttpStatusCode.Forbidden : null;
        using (var original = new Session(remote, next, Branch.Payload(next, bytes)))
            Assert.Equal(GitHubPublicationResultKind.Permission, (await original.Publish()).Kind);
        Assert.Equal(GitHubCoordinationStage.ProposalRefAdvanced, remote.State(next).Stage);
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
        else remote.Git.AdvanceUnchangedBase();
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
        };
        var staleInput = input with
        {
            SnapshotCommitmentSha256 = first.SnapshotCommitmentSha256,
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
    public async Task Sealed_publication_refuses_a_new_result_even_when_the_creation_quota_changes()
    {
        var remote = new Remote();
        var first = Branch.Authority(remote.Git);
        using (var initial = new Session(remote, first))
        { await initial.Publish(); Assert.Equal(GitHubPublicationResultKind.Published, (await initial.Publish()).Kind); }
        var writes = remote.Writes;
        byte[] bytes = [8, 7];
        foreach (var quota in new[] { 100, 101 })
        {
            var input = Input(first);
            var next = GitHubPublicationFactory.CreateAuthority(input with
            {
                OperationId = "replacement",
                CandidateCommitmentSha256 = Hash(bytes),
                Batch = input.Batch with { CreationQuota = quota },
                ChangedFiles = [first.ChangedFiles[0] with { CandidateFileSha256 = Hash(bytes) }],
            });
            using var replacement = new Session(remote, next, Branch.Payload(next, bytes));
            Assert.Equal(GitHubPublicationResultKind.Conflict, (await replacement.Publish()).Kind);
            Assert.Equal(writes, remote.Writes);
            Assert.Single(remote.Prs);
            Assert.Equal(1, remote.PrAttempts);
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
        byte[] bytes = [6, 7];
        var next = Branch.Authority(remote.Git, "rewind", candidate: bytes);
        string? claimOid = null;
        var injected = false;
        remote.BeforeMutation = (_, body) =>
        {
            var updates = JsonNode.Parse(body!)?["variables"]?["input"]?["refUpdates"]?.AsArray();
            if (updates is null) return;
            foreach (var update in updates)
            {
                var reference = update!["name"]!.GetValue<string>();
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
            }
        };
        using (var original = new Session(remote, next, Branch.Payload(next, bytes)))
            Assert.Contains((await original.Publish()).Kind, new[] { GitHubPublicationResultKind.Conflict, GitHubPublicationResultKind.HumanChange });
        Assert.True(injected);
        remote.BeforeMutation = null;
        var writes = remote.Writes;
        using var restart = new Session(remote, next, Branch.Payload(next, bytes));
        Assert.Contains((await restart.Publish()).Kind, new[] { GitHubPublicationResultKind.Conflict, GitHubPublicationResultKind.HumanChange });
        Assert.Equal(writes, remote.Writes);
        Assert.Equal(0, remote.Posts);
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
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Target_move_after_unrecorded_content_or_ref_preserves_exact_residual_before_stale(bool afterRef, bool denyObservation)
    {
        var remote = new Remote();
        var authority = Branch.Authority(remote.Git);
        remote.AfterMutation = (_, body) =>
        {
            var node = JsonNode.Parse(body!);
            var hit = afterRef ? node?["variables"]?["input"]?["refUpdates"]?.AsArray()
                .Any(update => update?["name"]?.GetValue<string>().Contains("/proposals/", StringComparison.Ordinal) == true) == true
                : node?["message"]?.GetValue<string>().StartsWith("ContractScribe proposal", StringComparison.Ordinal) == true;
            if (hit) remote.Git.Refs["refs/heads/main"] = new string('9', 40);
        };
        using var session = new Session(remote, authority);
        Assert.Equal(GitHubPublicationResultKind.Stale, (await session.Publish()).Kind);
        remote.AfterMutation = null;
        using var restart = new Session(remote, authority);
        if (denyObservation)
        {
            var reference = GitHubPublicationFactory.CreateCoordinationRef(authority);
            var predecessor = remote.Git.Refs[reference];
            remote.RejectMutation = path => path == "/graphql" ? HttpStatusCode.Forbidden : null;
            var denied = await restart.Observe();
            Assert.Equal(GitHubPublicationResultKind.Permission, denied.Result.Kind);
            Assert.Equal(afterRef ? GitHubPublicationBoundary.CoordinationAdvanceStale : GitHubPublicationBoundary.CoordinationAdvanceContent,
                denied.Diagnostic!.Boundary);
            Assert.Equal(predecessor, remote.Git.Refs[reference]);
            remote.RejectMutation = null;
            var retainedWrites = remote.Writes;
            Assert.Equal(GitHubPublicationResultKind.Conflict, (await restart.Publish()).Kind);
            Assert.Equal(retainedWrites, remote.Writes);
            Assert.Equal(0, remote.Posts);
            return;
        }
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
        var owner = GitHubCoordinationStore.Create(session.Client);
        using var foreign = new Session(remote, authority);
        Assert.Throws<ArgumentException>(() => GitHubProposalStore.Create(session.Client, GitHubCoordinationStore.Create(foreign.Client)));
        var state = (await owner.ReadCurrentAsync()).State!;
        var store = GitHubProposalPullRequestStore.Create(session.Client, owner, Publisher);
        var head = new GitHubProposalHead(GitHubPublicationFactory.CreateProposalRef(authority), state.ProposalCommitOid!, state.ProposalTreeOid!);
        var writes = remote.Writes;
        Assert.Equal(ContractScribe.GitHub.PullRequests.GitHubProposalOutcome.Conflict,
            (await store.CreateAuthorizedAsync(state, head, new CounterfeitRight())).Outcome);
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
        Assert.NotEqual(previousHead, remote.Git.Refs[GitHubPublicationFactory.CreateCoordinationRef(authority)]);
        Assert.Null(remote.State(authority).PullRequestNumber);
        Assert.Equal(GitHubCoordinationStage.PullRequestCreateStarted, remote.State(authority).Stage);
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
        var actual = await session.Observe();
        Assert.Equal(expected, actual.Result.Kind);
        Assert.Equal(proposal ? GitHubPublicationBoundary.GitCreateContent : GitHubPublicationBoundary.CoordinationClaim,
            actual.Diagnostic!.Boundary);
        Assert.Equal(status switch
        {
            401 => GitHubFailureCode.Authentication,
            429 => GitHubFailureCode.RateLimit,
            _ => GitHubFailureCode.Permission,
        }, actual.Diagnostic.TransportCode);
        Assert.Equal(status, actual.Diagnostic.TransportHttpStatus);
        Assert.Equal(failedRecovery ? GitHubFailureCode.HostFailure : GitHubFailureCode.NotFound, actual.Diagnostic.RecoveryCode);
        Assert.Equal(failedRecovery ? 503 : 404, actual.Diagnostic.RecoveryHttpStatus);
        Assert.Equal(GitHubObjectKind.Blob, actual.Diagnostic.ObjectKind);
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
    [InlineData("exact", (int)GitHubPublicationResultKind.RecoveredRefPartial)]
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
            if (remote.Git.CoordinationState(update["afterOid"]!.GetValue<string>()).Stage != GitHubCoordinationStage.ContentCreated) return;
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
            {
                remote.AfterMutation = null;
                throw new IOException("synthetic acknowledged-state response loss");
            }
        };
        var actual = await session.Observe(cancel.Token);
        Assert.Equal((GitHubPublicationResultKind)expected, actual.Result.Kind);
        if (scenario == "exact") Assert.Null(actual.Diagnostic);
        else
        {
            Assert.Equal(GitHubPublicationBoundary.CoordinationAdvanceContent, actual.Diagnostic!.Boundary);
            Assert.Equal(GitHubPublicationOwner.Coordination, actual.Diagnostic.Owner);
            if (scenario == "failed-recovery")
            {
                Assert.Equal(GitHubFailureCode.Permission, actual.Diagnostic.TransportCode);
                Assert.Equal(403, actual.Diagnostic.TransportHttpStatus);
                Assert.Equal(GitHubFailureCode.HostFailure, actual.Diagnostic.RecoveryCode);
                Assert.Equal(503, actual.Diagnostic.RecoveryHttpStatus);
            }
        }
        Assert.Equal(1, attempts);
        Assert.Equal(scenario == "exact" ? 1 : 0, remote.Git.ProposalWrites);
        Assert.Equal(0, remote.PrAttempts);
        if (scenario == "exact") Assert.Equal(GitHubCoordinationStage.ProposalRefAdvanced, remote.State(authority).Stage);
        else Assert.Equal(scenario == "divergent" ? authority.ExpectedBaseCommitOid : claim.HeadOid, remote.Git.Refs[reference]);
        remote.BeforeMutation = null; remote.BeforeCas = null; remote.AfterMutation = null;
        remote.RejectMutation = null; remote.RejectRead = null;
        var writes = remote.Writes;
        using var restart = new Session(remote, authority);
        var resumed = await restart.Publish();
        if (scenario == "exact")
        {
            Assert.Equal(GitHubPublicationResultKind.Published, resumed.Kind);
            Assert.Equal(1, remote.PrAttempts);
        }
        else
        {
            Assert.Equal(writes, remote.Writes);
            Assert.Equal(0, remote.PrAttempts);
        }
        Assert.Equal(scenario == "exact" ? 1 : 0, remote.Git.ProposalWrites);
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

    [Theory]
    [InlineData("first-proposal", "forward")]
    [InlineData("classification", "forward")]
    [InlineData("final-proposal", "forward")]
    [InlineData("first-proposal", "foreign")]
    [InlineData("classification", "foreign")]
    [InlineData("final-proposal", "foreign")]
    [InlineData("first-proposal", "rewind")]
    [InlineData("classification", "rewind")]
    [InlineData("final-proposal", "rewind")]
    [InlineData("classification", "title")]
    [InlineData("classification", "body")]
    [InlineData("classification", "actor")]
    [InlineData("classification", "base-ref")]
    [InlineData("classification", "head-repository")]
    [InlineData("first-proposal", "permission")]
    public async Task Sealed_result_observation_preserves_only_the_genuine_original_content_proof(string window, string change)
    {
        var remote = new Remote();
        var first = Branch.Authority(remote.Git);
        using var initial = new Session(remote, first);
        await initial.Publish(); await initial.Publish();
        var next = first;
        using var writer = new Session(remote, next);
        var coordination = GitHubCoordinationStore.Create(writer.Client);
        var currentWriter = (await coordination.ReadCurrentAsync()).State!;
        var reference = GitHubPublicationFactory.CreateProposalRef(next);
        var before = next.ExpectedBaseCommitOid;
        var advancedHead = currentWriter.HeadOid;
        var advanceWrites = 0;
        async Task Advance()
        {
            var writesBefore = remote.Writes;
            var advanced = await coordination.AdvanceAsync(currentWriter, GitHubCoordinationStageUpdate.AwaitingReview());
            Assert.NotNull(advanced.State);
            advancedHead = advanced.State!.HeadOid;
            advanceWrites = remote.Writes - writesBefore;
        }
        using var observer = new Session(remote, next, writer.Payload);
        var owner = GitHubCoordinationStore.Create(observer.Client);
        var current = await owner.ReadCurrentAsync();
        var inspection = (await GitHubProposalStore.Create(observer.Client, owner).InspectAsync(current.Read!, observer.Payload)).Inspection!;
        Assert.NotNull(inspection);
        Assert.True(inspection.ContentExists);
        Assert.Equal(currentWriter.ProposalCommitOid, inspection.CommitOid);
        Assert.Equal(GitHubPublicationContract.MissingGitObjectId, inspection.BeforeOid);
        Assert.Equal(currentWriter.ProposalCommitOid, inspection.ObservedRefOid);
        var store = GitHubProposalPullRequestStore.Create(observer.Client, owner, Publisher);
        var requests = remote.Attempts.Count;
        Assert.Null((await store.ObserveVerifiedAsync(current.State!, new CounterfeitInspection(inspection))).Observation);
        Assert.Equal(requests, remote.Attempts.Count);
        var proposalReads = 0;
        remote.BeforeReadAsync = async path =>
        {
            if (path.Contains("/git/ref/heads/contract-scribe/proposals/", StringComparison.Ordinal)) proposalReads++;
            if (!(window == "classification" && path == "/repos/Owner/repo/pulls")
                && !(window == "first-proposal" && proposalReads == 1)
                && !(window == "final-proposal" && proposalReads == 2)) return;
            remote.BeforeReadAsync = null;
            if (change != "rewind") await Advance();
            switch (change)
            {
                case "foreign": remote.Git.Refs[reference] = first.ExpectedBaseCommitOid; break;
                case "rewind": remote.Git.Refs[reference] = before; break;
                case "title": remote.Prs[0]["title"] = "human title"; break;
                case "body": remote.Prs[0]["body"] = "human body"; break;
                case "actor": remote.Prs[0]["user"]!["id"] = 100; break;
                case "base-ref": remote.Prs[0]["base"]!["ref"] = "other"; break;
                case "head-repository": remote.Prs[0]["head"]!["repo"]!["id"] = 100; break;
                case "permission": remote.RejectRead = p => p == "/repos/Owner/repo" ? HttpStatusCode.Forbidden : null; break;
            }
        };
        var writes = remote.Writes;

        var result = await store.ObserveVerifiedAsync(current.State!, inspection);

        Assert.Null(remote.BeforeReadAsync);
        if (change == "forward" && window == "final-proposal")
            Assert.NotNull(result.Observation); // A verified same-result hold is safe to observe.
        else Assert.Null(result.Observation);
        Assert.Equal(advancedHead, remote.Git.Refs[GitHubPublicationFactory.CreateCoordinationRef(next)]);
        Assert.Equal(writes + (change == "rewind" ? 0 : advanceWrites), remote.Writes);
        Assert.Equal(1, remote.PrAttempts);
        Assert.Single(remote.Prs);
        if (change == "forward" && window == "final-proposal")
            Assert.Equal(ContractScribe.GitHub.PullRequests.GitHubProposalOutcome.OpenDraft, result.Outcome);
        else Assert.Equal(change == "permission" ? ContractScribe.GitHub.PullRequests.GitHubProposalOutcome.Failed
            : ContractScribe.GitHub.PullRequests.GitHubProposalOutcome.Conflict, result.Outcome);
        if (change == "permission") Assert.Equal(403, result.Failure!.HttpStatus);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static GitHubPublicationAuthorityInput Input(ValidatedGitHubPublicationAuthority a) => GitHubPublicationTestBatch.Copy(a);

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
        internal ValueTask<GitHubPublicationAttempt> Observe(CancellationToken token = default) => Reconciler.PublishObservedAsync(Authority, Payload, token);
        public void Dispose() => client.Dispose();
    }

    private sealed class Handler(Remote remote) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (remote.BeforeRequest is { } beforeRequest) await beforeRequest(request, cancellationToken);
            var response = await remote.Reply(request, cancellationToken);
            if (remote.ServerNow is { } now)
            {
                response.Headers.Date = now;
                remote.ServerNow = now.AddSeconds(remote.SecondsPerRequest);
            }
            return response;
        }
    }

    // Reuse the real Git object/CAS fake; this extension owns only PR wire behavior.
    private sealed class Remote
    {
        internal readonly Branch.Remote Git = new();
        internal GitHubActor ExpectedPublisher = Publisher;
        internal Func<HttpRequestMessage, CancellationToken, Task>? BeforeRequest;
        internal DateTimeOffset? ServerNow;
        internal int SecondsPerRequest;
        internal readonly List<JsonObject> Prs = [];
        internal readonly List<(string Method, string Path)> Attempts = [];
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
        internal Func<string, Task>? BeforeReadAsync;
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
            lock (Git.SyncRoot) Attempts.Add((request.Method.Method, request.RequestUri!.AbsolutePath));
            if (request.Method == HttpMethod.Get) BeforeRead?.Invoke(request.RequestUri!.AbsolutePath);
            if (request.Method == HttpMethod.Get && BeforeReadAsync is { } beforeRead)
                await beforeRead(request.RequestUri!.AbsolutePath);
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
                        user = new { id = ExpectedPublisher.Id, node_id = ExpectedPublisher.NodeId, login = ExpectedPublisher.Login, type = "Bot" },
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
