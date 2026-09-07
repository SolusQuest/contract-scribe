using System.Collections.Immutable;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ContractScribe.Core;
using ContractScribe.GitHub.Coordination;
using ContractScribe.GitHub.GitData;
using ContractScribe.GitHub.Transport;
using static ContractScribe.GitHub.GitData.GitHubProposalObjects;

namespace ContractScribe.Tests;

[Collection("GitHub transport hook")]
public sealed class GitHubProposalBranchTests
{
    private static readonly byte[] Original = [0, 255, 1, 13, 10];
    private static readonly byte[] Candidate = [0, 254, 2, 13, 10];
    private static readonly byte[] Unchanged = Encoding.UTF8.GetBytes("untouched synthetic bytes\n");
    private static string Hash(char c) => new(c, 64);
    private static string Oid(char c) => new(c, 40);

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("truncated")]
    public async Task Inherited_tree_damage_after_preparation_is_not_repaired_or_reported_as_verified(string damage)
    {
        var remote = new Remote();
        using var session = new Session(Authority(remote), remote);
        var claim = await Claim(session);
        var prepared = await session.Proposal.PrepareAsync(claim, Payload(session.Authority));
        Assert.Equal(GitHubProposalOutcome.Prepared, prepared.Outcome);
        var inherited = remote.BaseRoot.Single(e => e.Path == "keep").Oid;
        if (damage == "missing") remote.Trees.Remove(inherited);
        if (damage == "corrupt") remote.Trees[inherited] = [];
        if (damage == "truncated") remote.TruncatedOid = inherited;
        var result = await session.Proposal.CreateContentAsync(prepared.Prepared!, claim);
        Assert.Equal(GitHubProposalOutcome.Failed, result.Outcome);
        Assert.Null(result.Content);
        Assert.Equal(GitHubDelivery.NeedsReadback, result.Failure!.Delivery);
        Assert.NotNull(result.Failure.Context);
        Assert.Equal(0, remote.ProposalAttempts);
        if (damage == "missing") Assert.False(remote.Trees.ContainsKey(inherited));
        if (damage == "corrupt") Assert.Empty(remote.Trees[inherited]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    public async Task Content_creation_only_rereads_unchanged_trees_in_final_verification(int depth)
    {
        var remote = new Remote();
        remote.DeepBase(depth);
        var baseTrees = remote.Trees.Keys.ToArray();
        using var session = new Session(Authority(remote), remote);
        var claim = await Claim(session);
        var prepared = await session.Proposal.PrepareAsync(claim, Payload(session.Authority));
        Assert.Equal(GitHubProposalOutcome.Prepared, prepared.Outcome);
        var unchanged = baseTrees.Where(oid => oid != remote.BaseTreeOid
            && remote.Requests.Any(r => r.Method == "GET" && r.Path.EndsWith("/git/trees/" + oid, StringComparison.Ordinal))).ToArray();
        var start = remote.Requests.Count;
        var created = await session.Proposal.CreateContentAsync(prepared.Prepared!, claim);
        Assert.Equal(GitHubProposalOutcome.ContentVerified, created.Outcome);
        Assert.NotEmpty(unchanged);
        foreach (var oid in unchanged)
            Assert.Single(remote.Requests.Skip(start), r => r.Method == "GET"
                && r.Path.EndsWith("/git/trees/" + oid, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_third_operation_appends_to_the_authenticated_second_proposal()
    {
        var remote = new Remote();
        var initial = Authority(remote);
        using var first = new Session(initial, remote);
        await RecordPublication(first, remote, Candidate, false);
        var secondAuthority = Authority(remote, "second", initial, [6]);
        using var second = new Session(secondAuthority, remote);
        var preceding = await RecordPublication(second, remote, [6], false);
        var thirdAuthority = Authority(remote, "third", secondAuthority, [7]);
        using var third = new Session(thirdAuthority, remote);
        var content = await RecordPublication(third, remote, [7], false);
        Assert.Equal(preceding.CommitOid, Assert.Single(remote.Commits[content.CommitOid].Parents));
    }

    [Fact]
    public async Task Final_recovery_budget_expiry_keeps_delivery_and_reports_timeout()
    {
        var remote = new Remote();
        using var session = new Session(Authority(remote), remote);
        var claim = await Claim(session);
        var prepared = await session.Proposal.PrepareAsync(claim, Payload(session.Authority));
        remote.LateFailure = "timeout";
        var result = await session.Proposal.CreateContentAsync(prepared.Prepared!, claim);
        Assert.Equal(GitHubProposalOutcome.Failed, result.Outcome);
        Assert.Equal(GitHubDelivery.NeedsReadback, result.Failure!.Delivery);
        Assert.NotNull(result.Failure.Context);
        Assert.Equal(GitHubFailureCode.Timeout, result.Failure.Readback!.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rehashed_predecessor_with_an_unauthorized_parent_is_rejected(bool precedingAppend)
    {
        var remote = new Remote();
        var initial = Authority(remote);
        using var first = new Session(initial, remote);
        await RecordPublication(first, remote, Candidate, badParent: !precedingAppend);
        var previous = initial;
        if (precedingAppend)
        {
            previous = Authority(remote, "previous-append", initial, [6]);
            using var append = new Session(previous, remote);
            await RecordPublication(append, remote, [6], badParent: true);
        }
        var current = Authority(remote, "current-append", previous, [7]);
        using var session = new Session(current, remote);
        var claim = await Claim(session);
        var writes = remote.Writes;
        var result = await session.Proposal.PrepareAsync(claim, Payload(current, [7]));
        Assert.Equal(GitHubProposalOutcome.Failed, result.Outcome);
        Assert.Equal(writes, remote.Writes);
    }

    [Theory]
    [InlineData("content")]
    [InlineData("content-target")]
    [InlineData("ref")]
    public async Task Late_verification_failure_retains_the_already_dispatched_mutation(string phase)
    {
        var remote = new Remote();
        using var session = new Session(Authority(remote), remote);
        var claim = await Claim(session);
        var prepared = await session.Proposal.PrepareAsync(claim, Payload(session.Authority));
        remote.LateFailure = phase;
        var result = await session.Proposal.CreateContentAsync(prepared.Prepared!, claim);
        if (phase == "ref")
        {
            var stage = await session.Coordination.AdvanceAsync(claim,
                GitHubCoordinationStageUpdate.ContentCreated(result.Content!.CommitOid));
            result = await session.Proposal.AdvanceRefAsync(result.Content, stage.State!, stage.ProposalRefEntitlement);
        }
        Assert.Equal(GitHubProposalOutcome.Failed, result.Outcome);
        Assert.Equal(GitHubDelivery.NeedsReadback, result.Failure!.Delivery);
        Assert.NotNull(result.Failure.Context);
        if (phase == "content-target")
        {
            Assert.NotNull(result.Content);
            Assert.Equal(GitHubCoordinationFailureKind.TargetMoved, result.Failure.CoordinationCause);
            Assert.Equal(result.Content!.CommitOid, Assert.IsType<GitHubObjectContext>(result.Failure.Context).ExpectedOid);
        }
    }

    [Theory]
    [InlineData("content")]
    [InlineData("proposal")]
    [InlineData("pr-created")]
    [InlineData("published")]
    [InlineData("awaiting")]
    [InlineData("merged")]
    [InlineData("closed")]
    [InlineData("stale-content")]
    [InlineData("stale-proposal")]
    [InlineData("stale-draft")]
    [InlineData("changed-ref")]
    [InlineData("published-current")]
    public async Task Recorded_resources_remain_readable_after_completion_or_target_movement_without_granting_writes(string stageName)
    {
        var remote = new Remote();
        var authority = Authority(remote);
        using var first = new Session(authority, remote);
        var (claim, content) = await CreateContent(first);
        var stage = await first.Coordination.AdvanceAsync(claim, GitHubCoordinationStageUpdate.ContentCreated(content.CommitOid));
        Assert.Equal(GitHubProposalOutcome.RefVerified,
            (await first.Proposal.AdvanceRefAsync(content, stage.State!, stage.ProposalRefEntitlement)).Outcome);
        if (stageName != "content" && stageName != "stale-content")
            stage = await first.Coordination.AdvanceAsync(stage.State!, GitHubCoordinationStageUpdate.ProposalRefAdvanced(content.CommitOid, content.TreeOid));
        if (stageName.StartsWith("stale-", StringComparison.Ordinal))
        {
            remote.Refs["refs/heads/main"] = Oid('9');
            if (stageName == "stale-draft") await Publish(first, remote, stage.State!, content, GitHubCoordinationStage.StaleDraft);
            else stage = await first.Coordination.AdvanceAsync(stage.State!, GitHubCoordinationStageUpdate.Stale(remote.BaseOid, Oid('9')));
        }
        else if (stageName is not ("content" or "proposal"))
        {
            await Publish(first, remote, stage.State!, content, stageName == "pr-created"
                ? GitHubCoordinationStage.PullRequestCreated : GitHubCoordinationStage.Published);
            stage = await first.Coordination.ReadCurrentAsync();
            if (stageName == "awaiting") await first.Coordination.AdvanceAsync(stage.State!, GitHubCoordinationStageUpdate.AwaitingReview());
            if (stageName is "merged" or "closed") await first.Coordination.AdvanceAsync(stage.State!, GitHubCoordinationStageUpdate.Terminal(
                stageName == "merged" ? GitHubCoordinationStage.Merged : GitHubCoordinationStage.ClosedUnmerged));
        }
        if (stageName != "published-current") remote.Refs["refs/heads/main"] = Oid('9');
        if (stageName == "changed-ref") remote.Refs[content.Ref] = Oid('8');
        using var restart = new Session(authority, remote);
        var state = (await restart.Coordination.ReadCurrentAsync()).State!;
        var writes = remote.Writes;
        var result = await restart.Proposal.VerifyRecordedAsync(state, Payload(authority));
        Assert.Equal(GitHubProposalOutcome.RecordedVerified, result.Outcome);
        Assert.Null(result.Content);
        Assert.Null(result.Prepared);
        Assert.Equal(remote.BaseOid, result.Observation!.ExpectedBaseOid);
        Assert.Equal(stageName == "published-current" ? remote.BaseOid : Oid('9'), result.Observation.ObservedBaseOid);
        Assert.Equal(stageName == "changed-ref" ? Oid('8') : content.CommitOid, result.Observation.ObservedRefOid);
        Assert.Equal(content.CommitOid, result.Observation.CommitOid);
        Assert.Null((await restart.Coordination.ReadResourceAsync(state)).ProposalRefEntitlement);
        Assert.Null((await restart.Coordination.ReadClaimAsync(state)).Guard);
        Assert.Equal(writes, remote.Writes);
    }

    [Fact]
    public async Task Malformed_coordination_keeps_its_domain_cause_in_the_R4_failure()
    {
        var remote = new Remote();
        using var session = new Session(Authority(remote), remote);
        var claim = await Claim(session);
        var root = remote.Trees[remote.Commits[claim.HeadOid].TreeOid];
        var leaf = remote.Trees[root[0].Oid];
        remote.Blobs[leaf[0].Oid] = [0];
        var result = await session.Proposal.PrepareAsync(claim, Payload(session.Authority));
        Assert.Equal(GitHubCoordinationFailureKind.ObjectMismatch, result.Failure!.CoordinationCause);
        Assert.Equal(0, remote.ProposalAttempts);
    }

    [Fact]
    public async Task A_misbound_R4_client_cannot_use_another_repositorys_R3_guard()
    {
        var left = new Remote();
        var right = new Remote { RepositoryId = 2 };
        var authority = Authority(left);
        using var a = new Session(authority, left);
        using var b = new Session(authority, right);
        Assert.NotNull((await a.Client.GetRepositoryAsync()).Value);
        var claim = await Claim(b);
        var misbound = GitHubProposalStore.Create(a.Client, b.Coordination);
        var leftWrites = left.Writes;
        var rightWrites = right.Writes;
        Assert.Equal(GitHubProposalOutcome.Failed, (await misbound.PrepareAsync(claim, Payload(authority))).Outcome);
        Assert.Equal(leftWrites, left.Writes);
        Assert.Equal(rightWrites, right.Writes);
    }

    private static async Task<IGitHubProposalContent> RecordPublication(Session session, Remote remote, byte[] bytes, bool badParent)
    {
        var claim = await Claim(session);
        var prepared = await session.Proposal.PrepareAsync(claim, Payload(session.Authority, bytes));
        Assert.Equal(GitHubProposalOutcome.Prepared, prepared.Outcome);
        var created = await session.Proposal.CreateContentAsync(prepared.Prepared!, claim);
        Assert.Equal(GitHubProposalOutcome.ContentVerified, created.Outcome);
        var content = created.Content!;
        if (badParent)
        {
            var forged = Commit(content.TreeOid, Oid('8'), session.Authority.OperationCommitmentSha256,
                content.Ref[(content.Ref.LastIndexOf('/') + 1)..]);
            remote.Commits[forged.ExpectedOid] = new(forged.ExpectedOid, forged.TreeOid, [forged.ParentOid], forged.Message, forged.Author, forged.Committer);
            content = new RecordedContent(forged.ExpectedOid, forged.TreeOid, content.Ref);
        }
        var stage = await session.Coordination.AdvanceAsync(claim, GitHubCoordinationStageUpdate.ContentCreated(content.CommitOid));
        Assert.NotNull(stage.State);
        if (badParent) remote.Refs[content.Ref] = content.CommitOid;
        else Assert.Equal(GitHubProposalOutcome.RefVerified,
            (await session.Proposal.AdvanceRefAsync(content, stage.State!, stage.ProposalRefEntitlement)).Outcome);
        var proposal = await session.Coordination.AdvanceAsync(stage.State!,
            GitHubCoordinationStageUpdate.ProposalRefAdvanced(content.CommitOid, content.TreeOid));
        Assert.NotNull(proposal.State);
        await Publish(session, remote, proposal.State!, content);
        return content;
    }

    private sealed record RecordedContent(string CommitOid, string TreeOid, string Ref) : IGitHubProposalContent;

    [Fact]
    public void Independent_Git_known_answers_pin_binary_Unicode_tree_order_and_all_commit_shapes()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "ContractScribe.slnx"))) directory = directory.Parent;
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Join(directory!.FullName,
            "tests/fixtures/github/git-data/known-answers.json")));
        foreach (var item in fixture.RootElement.GetProperty("objects").EnumerateArray())
        {
            var oid = item.GetProperty("oid").GetString();
            switch (item.GetProperty("type").GetString())
            {
                case "blob":
                    Assert.Equal(oid, ObjectOid("blob", Convert.FromHexString(item.GetProperty("hex").GetString()!)));
                    break;
                case "tree":
                    var entries = item.GetProperty("entries").EnumerateArray().Select(e => new GitHubTreeEntry(
                        e.GetProperty("name").GetString()!, e.GetProperty("mode").GetString() switch
                        { "40000" => GitHubTreeMode.Directory, "100755" => GitHubTreeMode.Executable, _ => GitHubTreeMode.File },
                        e.GetProperty("oid").GetString()!, null)).ToImmutableArray();
                    Assert.Equal(oid, TreeOid(entries));
                    Assert.Equal(item.GetProperty("preimageBase64").GetString(), Convert.ToBase64String(TreeBytes(entries)));
                    Assert.Equal(oid, TreeOid(entries.Reverse().ToImmutableArray()));
                    break;
                case "commit":
                    var commit = Commit(item.GetProperty("tree").GetString()!, item.GetProperty("parent").GetString()!,
                        item.GetProperty("operation").GetString()!, item.GetProperty("generation").GetString()!);
                    Assert.Equal(oid, commit.ExpectedOid);
                    Assert.Equal(item.GetProperty("message").GetString(), commit.Message);
                    Assert.Equal(item.GetProperty("preimageBase64").GetString(), Convert.ToBase64String(CommitBytes(commit)));
                    break;
            }
        }
    }

    [Fact]
    public async Task Initial_binary_publication_preserves_all_other_entries_and_exact_replay_is_read_only()
    {
        var remote = new Remote();
        using var session = new Session(Authority(remote), remote);
        var (claim, content) = await CreateContent(session);
        var stage = await session.Coordination.AdvanceAsync(claim, GitHubCoordinationStageUpdate.ContentCreated(content.CommitOid));
        Assert.NotNull(stage.ProposalRefEntitlement);
        var result = await session.Proposal.AdvanceRefAsync(content, stage.State!, stage.ProposalRefEntitlement);
        Assert.Equal(GitHubProposalOutcome.RefVerified, result.Outcome);
        Assert.Equal(content.CommitOid, remote.ProposalHead(session.Authority));
        Assert.Equal(1, remote.ProposalWrites);
        var committed = remote.Commits[content.CommitOid];
        Assert.Equal(remote.BaseOid, Assert.Single(committed.Parents));
        Assert.Equal(Actor, committed.Author);
        Assert.Equal(Actor, committed.Committer);
        Assert.DoesNotContain(Convert.ToBase64String(Candidate), committed.Message);
        var root = remote.Trees[content.TreeOid];
        Assert.Equal(remote.BaseRoot.Single(e => e.Path == "keep"), root.Single(e => e.Path == "keep"));
        Assert.Equal(remote.BaseRoot.Single(e => e.Path == "link"), root.Single(e => e.Path == "link"));
        Assert.Equal(remote.BaseRoot.Single(e => e.Path == "module"), root.Single(e => e.Path == "module"));
        Assert.Equal(GitHubTreeMode.Executable, root.Single(e => e.Path == "file.bin").Mode);
        Assert.Equal(Candidate, remote.Blobs[root.Single(e => e.Path == "file.bin").Oid]);
        var writes = remote.Writes;
        using var restart = new Session(session.Authority, remote);
        var state = (await restart.Coordination.ReadCurrentAsync()).State!;
        var prepared = await restart.Proposal.PrepareAsync(state, Payload(restart.Authority));
        var recovered = await restart.Proposal.CreateContentAsync(prepared.Prepared!, state);
        Assert.Equal(GitHubProposalOutcome.RefVerified, (await restart.Proposal.AdvanceRefAsync(recovered.Content!, state)).Outcome);
        Assert.Equal(writes, remote.Writes);
        Assert.All(remote.Requests.Where(r => r.Method != "GET"), r => Assert.True(
            r.Path == "/graphql" || r.Path.EndsWith("/git/blobs") || r.Path.EndsWith("/git/trees") || r.Path.EndsWith("/git/commits")));
    }

    [Theory]
    [InlineData("blob")]
    [InlineData("tree")]
    [InlineData("commit")]
    [InlineData("proposal-ref")]
    public async Task Applied_response_loss_recovers_exact_content_or_ref_without_repeating_the_transition(string kind)
    {
        var remote = new Remote();
        using var session = new Session(Authority(remote), remote);
        var claim = await Claim(session);
        remote.LoseAfter = kind;
        var prepared = await session.Proposal.PrepareAsync(claim, Payload(session.Authority));
        var created = await session.Proposal.CreateContentAsync(prepared.Prepared!, claim);
        Assert.Equal(GitHubProposalOutcome.ContentVerified, created.Outcome);
        var stage = await session.Coordination.AdvanceAsync(claim, GitHubCoordinationStageUpdate.ContentCreated(created.Content!.CommitOid));
        var result = await session.Proposal.AdvanceRefAsync(created.Content, stage.State!, stage.ProposalRefEntitlement);
        Assert.Equal(GitHubProposalOutcome.RefVerified, result.Outcome);
        Assert.Equal(1, remote.ProposalWrites);
        Assert.Equal(1, remote.ProposalAttempts);
    }

    [Theory]
    [InlineData("blob")]
    [InlineData("tree")]
    [InlineData("commit")]
    [InlineData("proposal-ref")]
    public async Task Loss_before_application_stops_at_missing_readback_and_exact_content_retry_is_stable(string kind)
    {
        var remote = new Remote();
        using var session = new Session(Authority(remote), remote);
        var claim = await Claim(session);
        var prepared = await session.Proposal.PrepareAsync(claim, Payload(session.Authority));
        remote.LoseBefore = kind;
        var content = await session.Proposal.CreateContentAsync(prepared.Prepared!, claim);
        if (kind != "proposal-ref")
        {
            Assert.Equal(GitHubProposalFailureKind.Unresolved, content.Failure!.Kind);
            Assert.Equal(GitHubDelivery.Ambiguous, content.Failure.Delivery);
            Assert.NotNull(content.Failure.Context);
            Assert.Equal(0, remote.ProposalAttempts);
            content = await session.Proposal.CreateContentAsync(prepared.Prepared!, claim);
            Assert.Equal(GitHubProposalOutcome.ContentVerified, content.Outcome);
        }
        var stage = await session.Coordination.AdvanceAsync(claim, GitHubCoordinationStageUpdate.ContentCreated(content.Content!.CommitOid));
        var result = await session.Proposal.AdvanceRefAsync(content.Content, stage.State!, stage.ProposalRefEntitlement);
        if (kind == "proposal-ref")
        {
            Assert.Equal(GitHubProposalFailureKind.Unresolved, result.Failure!.Kind);
            Assert.Equal(GitHubDelivery.Ambiguous, result.Failure.Delivery);
            Assert.Equal(GitHubProposalOutcome.Failed,
                (await session.Proposal.AdvanceRefAsync(content.Content, stage.State!, stage.ProposalRefEntitlement)).Outcome);
            Assert.Equal(0, remote.ProposalWrites);
        }
        else Assert.Equal(GitHubProposalOutcome.RefVerified, result.Outcome);
    }

    [Theory]
    [InlineData("blob")]
    [InlineData("tree")]
    [InlineData("commit")]
    [InlineData("proposal-ref")]
    public async Task Caller_cancellation_after_delivery_allows_readback_but_no_later_write(string kind)
    {
        var remote = new Remote();
        using var session = new Session(Authority(remote), remote);
        var claim = await Claim(session);
        var prepared = await session.Proposal.PrepareAsync(claim, Payload(session.Authority));
        using var cancellation = new CancellationTokenSource();
        remote.CancelAfter = kind; remote.CancelSource = cancellation;
        var created = await session.Proposal.CreateContentAsync(prepared.Prepared!, claim, cancellation.Token);
        if (kind is "blob" or "tree")
        {
            Assert.Equal(GitHubProposalOutcome.Failed, created.Outcome);
            Assert.Equal(0, remote.ProposalWrites);
            Assert.True(cancellation.IsCancellationRequested);
            return;
        }
        Assert.Equal(GitHubProposalOutcome.ContentVerified, created.Outcome);
        if (kind == "commit") { Assert.True(cancellation.IsCancellationRequested); Assert.Equal(0, remote.ProposalAttempts); return; }
        var stage = await session.Coordination.AdvanceAsync(claim, GitHubCoordinationStageUpdate.ContentCreated(created.Content!.CommitOid));
        var result = await session.Proposal.AdvanceRefAsync(created.Content!, stage.State!, stage.ProposalRefEntitlement, cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(GitHubProposalOutcome.RefVerified, result.Outcome);
        Assert.Equal(1, remote.ProposalAttempts);
    }

    [Theory]
    [InlineData("blob")]
    [InlineData("tree")]
    [InlineData("commit")]
    [InlineData("proposal-ref")]
    public async Task Mismatching_post_write_readback_preserves_mutation_context_and_stops(string kind)
    {
        var remote = new Remote();
        using var session = new Session(Authority(remote), remote);
        var claim = await Claim(session);
        var prepared = await session.Proposal.PrepareAsync(claim, Payload(session.Authority));
        remote.CorruptAfter = kind;
        var created = await session.Proposal.CreateContentAsync(prepared.Prepared!, claim);
        GitHubProposalResult result;
        if (kind == "proposal-ref")
        {
            var stage = await session.Coordination.AdvanceAsync(claim,
                GitHubCoordinationStageUpdate.ContentCreated(created.Content!.CommitOid));
            result = await session.Proposal.AdvanceRefAsync(created.Content, stage.State!, stage.ProposalRefEntitlement);
        }
        else result = created;
        Assert.Equal(GitHubProposalOutcome.Failed, result.Outcome);
        Assert.Equal(GitHubProposalFailureKind.Integrity, result.Failure!.Kind);
        Assert.Equal(GitHubDelivery.NeedsReadback, result.Failure.Delivery);
        Assert.NotNull(result.Failure.Context);
        Assert.Equal(kind == "proposal-ref" ? 1 : 0, remote.ProposalAttempts);
        Assert.DoesNotContain("synthetic", result.ToString());
    }

    [Theory]
    [InlineData("parent")]
    [InlineData("message")]
    [InlineData("actor")]
    [InlineData("time")]
    [InlineData("tree")]
    [InlineData("bytes")]
    [InlineData("extra-entry")]
    [InlineData("mode")]
    public async Task Damaged_content_is_never_advanced_even_with_an_unspent_receipt(string kind)
    {
        var remote = new Remote();
        using var session = new Session(Authority(remote), remote);
        var (claim, content) = await CreateContent(session);
        var stage = await session.Coordination.AdvanceAsync(claim, GitHubCoordinationStageUpdate.ContentCreated(content.CommitOid));
        var original = remote.Commits[content.CommitOid];
        remote.Commits[content.CommitOid] = kind switch
        {
            "parent" => original with { Parents = [Oid('8')] },
            "message" => original with { Message = original.Message + "human" },
            "actor" => original with { Author = original.Author with { Name = "Human" } },
            "time" => original with { Committer = original.Committer with { Date = Actor.Date.AddSeconds(1) } },
            "tree" => original with { TreeOid = remote.BaseTreeOid },
            _ => original,
        };
        var entries = remote.Trees[content.TreeOid];
        if (kind == "bytes") remote.Blobs[entries.Single(e => e.Path == "file.bin").Oid] = [9];
        if (kind == "extra-entry") remote.Trees[content.TreeOid] = entries.Add(new("extra", GitHubTreeMode.File, remote.FileOid, null));
        if (kind == "mode") remote.Trees[content.TreeOid] = entries.SetItem(0, entries[0] with { Mode = GitHubTreeMode.File });
        var result = await session.Proposal.AdvanceRefAsync(content, stage.State!, stage.ProposalRefEntitlement);
        Assert.Equal(GitHubProposalOutcome.Failed, result.Outcome);
        Assert.Equal(0, remote.ProposalAttempts);
    }

    [Theory]
    [InlineData("target")]
    [InlineData("claim")]
    [InlineData("proposal")]
    public async Task Drift_before_ref_dispatch_stops_forward_mutation(string kind)
    {
        var remote = new Remote();
        using var session = new Session(Authority(remote), remote);
        var (claim, content) = await CreateContent(session);
        var stage = await session.Coordination.AdvanceAsync(claim, GitHubCoordinationStageUpdate.ContentCreated(content.CommitOid));
        var name = kind == "target" ? "refs/heads/main" : kind == "claim"
            ? GitHubPublicationFactory.CreateCoordinationRef(session.Authority) : content.Ref;
        remote.Refs[name] = Oid('9');
        Assert.Equal(GitHubProposalOutcome.Failed,
            (await session.Proposal.AdvanceRefAsync(content, stage.State!, stage.ProposalRefEntitlement)).Outcome);
        Assert.Equal(0, remote.ProposalAttempts);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("bytes")]
    public async Task Payload_is_recorrelated_even_when_presented_as_a_validated_wrapper(string kind)
    {
        var remote = new Remote();
        using var session = new Session(Authority(remote), remote);
        var claim = await Claim(session);
        var good = Payload(session.Authority);
        var files = kind switch
        {
            "missing" => good.Files.RemoveAt(0),
            "duplicate" => good.Files.Add(good.Files[0]),
            _ => Payload(Authority(remote, candidate: [8]), [8]).Files,
        };
        var forged = (ValidatedGitHubChangedFilePayload)Activator.CreateInstance(typeof(ValidatedGitHubChangedFilePayload),
            BindingFlags.NonPublic | BindingFlags.Instance, null, [files, session.Authority.AuthorityCommitmentSha256], null)!;
        var count = remote.Requests.Count;
        Assert.Equal(GitHubProposalOutcome.Failed, (await session.Proposal.PrepareAsync(claim, forged)).Outcome);
        Assert.Equal(count, remote.Requests.Count);
    }

    [Theory]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public async Task Aggregate_tree_depth_has_a_bounded_pre_mutation_boundary(int depth, bool permitted)
    {
        var remote = new Remote();
        remote.DeepBase(depth);
        using var session = new Session(Authority(remote), remote);
        var claim = await Claim(session);
        var count = remote.Writes;
        var prepared = await session.Proposal.PrepareAsync(claim, Payload(session.Authority));
        Assert.Equal(permitted, prepared.Outcome == GitHubProposalOutcome.Prepared);
        Assert.Equal(count, remote.Writes);
    }

    [Fact]
    public void Tree_entry_and_request_bounds_fail_before_allocation_of_the_Git_preimage()
    {
        var entries = Enumerable.Range(0, 100_000).Select(i => new GitHubTreeEntry("f" + i,
            GitHubTreeMode.File, Oid('1'), null)).ToImmutableArray();
        Assert.NotEmpty(TreeBytes(entries));
        Assert.Throws<GitHubProposalException>(() => TreeBytes(entries.Add(new("overflow", GitHubTreeMode.File, Oid('1'), null))));
        var large = Enumerable.Range(0, 5000).Select(i => new GitHubTreeEntry(new string('a', 1000) + i,
            GitHubTreeMode.File, Oid('1'), null)).ToImmutableArray();
        Assert.Throws<GitHubProposalException>(() => TreeBytes(large));
        Assert.Throws<EncoderFallbackException>(() => TreeBytes([new("\ud800", GitHubTreeMode.File, Oid('1'), null)]));
    }

    [Fact]
    public async Task Content_stage_response_loss_authenticates_state_but_never_issues_forward_entitlement()
    {
        var remote = new Remote();
        using var session = new Session(Authority(remote), remote);
        var (claim, content) = await CreateContent(session);
        remote.LoseAfter = "coordination-ref";
        var stage = await session.Coordination.AdvanceAsync(claim, GitHubCoordinationStageUpdate.ContentCreated(content.CommitOid));
        Assert.Equal(GitHubCoordinationOutcome.Advanced, stage.Outcome);
        Assert.Null(stage.ProposalRefEntitlement);
        Assert.Equal(GitHubProposalFailureKind.Unresolved,
            (await session.Proposal.AdvanceRefAsync(content, stage.State!)).Failure!.Kind);
        Assert.Equal(0, remote.ProposalAttempts);
    }

    [Fact]
    public async Task One_receipt_is_consumed_atomically_and_cannot_survive_replay_or_a_human_rewind()
    {
        var remote = new Remote();
        using var session = new Session(Authority(remote), remote);
        var (claim, content) = await CreateContent(session);
        var stage = await session.Coordination.AdvanceAsync(claim, GitHubCoordinationStageUpdate.ContentCreated(content.CommitOid));
        var results = await Task.WhenAll(
            session.Proposal.AdvanceRefAsync(content, stage.State!, stage.ProposalRefEntitlement).AsTask(),
            session.Proposal.AdvanceRefAsync(content, stage.State!, stage.ProposalRefEntitlement).AsTask());
        Assert.Single(results, r => r.Outcome == GitHubProposalOutcome.RefVerified);
        Assert.Equal(1, remote.ProposalAttempts);
        remote.Refs.Remove(GitHubPublicationFactory.CreateProposalRef(session.Authority));
        Assert.Equal(GitHubProposalOutcome.Failed,
            (await session.Proposal.AdvanceRefAsync(content, stage.State!, stage.ProposalRefEntitlement)).Outcome);
        Assert.Equal(1, remote.ProposalAttempts);
    }

    [Fact]
    public async Task Identical_contenders_have_only_one_winning_stage_receipt_and_loser_cannot_reverse_a_rewind()
    {
        var remote = new Remote();
        var authority = Authority(remote);
        using var first = new Session(authority, remote);
        using var second = new Session(authority, remote);
        var claimA = await Claim(first);
        var claimB = (await second.Coordination.ReadCurrentAsync()).State!;
        var a = await first.Proposal.PrepareAsync(claimA, Payload(authority));
        var b = await second.Proposal.PrepareAsync(claimB, Payload(authority));
        // Both read absence before either can publish the content commit.
        remote.Barrier("GET", "/git/commits/", 2, onlyMissing: true);
        var content = await Task.WhenAll(first.Proposal.CreateContentAsync(a.Prepared!, claimA).AsTask(),
            second.Proposal.CreateContentAsync(b.Prepared!, claimB).AsTask());
        Assert.All(content, c => Assert.Equal(GitHubProposalOutcome.ContentVerified, c.Outcome));
        remote.Barrier("GET", "/git/commits/", 2, onlyMissing: true);
        var stages = await Task.WhenAll(first.Coordination.AdvanceAsync(claimA,
                GitHubCoordinationStageUpdate.ContentCreated(content[0].Content!.CommitOid)).AsTask(),
            second.Coordination.AdvanceAsync(claimB,
                GitHubCoordinationStageUpdate.ContentCreated(content[1].Content!.CommitOid)).AsTask());
        Assert.Single(stages, s => s.ProposalRefEntitlement is not null);
        var winner = stages[0].ProposalRefEntitlement is not null ? 0 : 1;
        var sessions = new[] { first, second };
        Assert.Equal(GitHubProposalOutcome.RefVerified, (await sessions[winner].Proposal.AdvanceRefAsync(
            content[winner].Content!, stages[winner].State!, stages[winner].ProposalRefEntitlement)).Outcome);
        remote.Refs.Remove(GitHubPublicationFactory.CreateProposalRef(authority));
        var loser = 1 - winner;
        var loserState = (await sessions[loser].Coordination.ReadCurrentAsync()).State!;
        Assert.Equal(GitHubProposalFailureKind.Unresolved,
            (await sessions[loser].Proposal.AdvanceRefAsync(content[loser].Content!, loserState)).Failure!.Kind);
        Assert.Equal(1, remote.ProposalAttempts);
    }

    [Theory]
    [InlineData("base-bytes")]
    [InlineData("missing-path")]
    [InlineData("symlink")]
    [InlineData("submodule")]
    [InlineData("case")]
    [InlineData("tree-sha")]
    [InlineData("truncated")]
    [InlineData("repository")]
    [InlineData("target")]
    public async Task Invalid_base_or_remote_identity_never_publishes_proposal_objects(string kind)
    {
        var remote = new Remote();
        if (kind is "missing-path" or "symlink" or "submodule" or "case") remote.ChangeBase(kind);
        using var session = new Session(Authority(remote), remote);
        var claim = await Claim(session);
        if (kind == "base-bytes") remote.Blobs[remote.FileOid] = [9];
        if (kind == "tree-sha") remote.Trees[remote.BaseTreeOid] = [];
        if (kind == "truncated") remote.TruncatedOid = remote.BaseTreeOid;
        if (kind == "repository") remote.RepositoryId = 99;
        if (kind == "target") remote.Refs["refs/heads/main"] = Oid('9');
        var writes = remote.Writes;
        var result = await session.Proposal.PrepareAsync(claim, Payload(session.Authority));
        Assert.Equal(GitHubProposalOutcome.Failed, result.Outcome);
        Assert.Equal(writes, remote.Writes);
        Assert.Equal(0, remote.ProposalAttempts);
    }

    [Fact]
    public async Task Wrong_payload_and_forged_capabilities_fail_before_network_and_never_leak_source()
    {
        var remote = new Remote();
        using var session = new Session(Authority(remote), remote);
        var claim = await Claim(session);
        var other = Authority(remote, operation: "other");
        var requests = remote.Requests.Count;
        var failed = await session.Proposal.PrepareAsync(claim, Payload(other));
        Assert.Equal(GitHubProposalOutcome.Failed, failed.Outcome);
        Assert.Equal(requests, remote.Requests.Count);
        Assert.DoesNotContain("other", failed.ToString());
        Assert.Equal(GitHubProposalOutcome.Failed,
            (await session.Proposal.CreateContentAsync(new ForgedPrepared(), claim)).Outcome);
        var (_, content) = await CreateContent(session, claim);
        var stage = await session.Coordination.AdvanceAsync(claim, GitHubCoordinationStageUpdate.ContentCreated(content.CommitOid));
        Assert.Equal(GitHubProposalOutcome.Failed,
            (await session.Proposal.AdvanceRefAsync(content, stage.State!, new ForgedEntitlement())).Outcome);
        Assert.Equal(0, remote.ProposalAttempts);
    }

    [Fact]
    public async Task Recorded_content_disappearance_after_restart_is_not_repaired()
    {
        var remote = new Remote();
        var authority = Authority(remote);
        using var session = new Session(authority, remote);
        var (claim, content) = await CreateContent(session);
        await session.Coordination.AdvanceAsync(claim, GitHubCoordinationStageUpdate.ContentCreated(content.CommitOid));
        remote.Commits.Remove(content.CommitOid);
        using var restart = new Session(authority, remote);
        var state = (await restart.Coordination.ReadCurrentAsync()).State!;
        var prepared = await restart.Proposal.PrepareAsync(state, Payload(authority));
        var writes = remote.Writes;
        var result = await restart.Proposal.CreateContentAsync(prepared.Prepared!, state);
        Assert.Equal(GitHubProposalOutcome.Failed, result.Outcome);
        Assert.Equal(writes, remote.Writes);
    }

    [Fact]
    public async Task Prepared_content_cannot_be_reused_against_a_different_recorded_content_stage()
    {
        var remote = new Remote();
        using var session = new Session(Authority(remote), remote);
        var claim = await Claim(session);
        var prepared = await session.Proposal.PrepareAsync(claim, Payload(session.Authority));
        var stage = await session.Coordination.AdvanceAsync(claim, GitHubCoordinationStageUpdate.ContentCreated(Oid('8')));
        var writes = remote.Writes;
        Assert.Equal(GitHubProposalOutcome.Failed, (await session.Proposal.CreateContentAsync(prepared.Prepared!, stage.State!)).Outcome);
        Assert.Equal(writes, remote.Writes);
    }

    [Fact]
    public async Task Foreign_store_state_and_entitlement_do_not_cross_the_owning_store_boundary()
    {
        var remote = new Remote();
        var authority = Authority(remote);
        using var first = new Session(authority, remote);
        var (claim, content) = await CreateContent(first);
        var stage = await first.Coordination.AdvanceAsync(claim, GitHubCoordinationStageUpdate.ContentCreated(content.CommitOid));
        using var second = new Session(authority, remote);
        Assert.Equal(GitHubProposalOutcome.Failed, (await second.Proposal.PrepareAsync(stage.State!, Payload(authority))).Outcome);
        var current = (await second.Coordination.ReadCurrentAsync()).State!;
        var prepared = await second.Proposal.PrepareAsync(current, Payload(authority));
        var recovered = await second.Proposal.CreateContentAsync(prepared.Prepared!, current);
        Assert.Equal(GitHubProposalOutcome.Failed,
            (await second.Proposal.AdvanceRefAsync(recovered.Content!, current, stage.ProposalRefEntitlement)).Outcome);
        Assert.Equal(0, remote.ProposalAttempts);
    }

    [Fact]
    public async Task Admitted_merged_successor_uses_its_fresh_base_and_generation_without_mutating_the_old_ref()
    {
        var remote = new Remote();
        using var first = new Session(Authority(remote), remote);
        var (claim, content) = await CreateContent(first);
        var stage = await first.Coordination.AdvanceAsync(claim, GitHubCoordinationStageUpdate.ContentCreated(content.CommitOid));
        Assert.Equal(GitHubProposalOutcome.RefVerified,
            (await first.Proposal.AdvanceRefAsync(content, stage.State!, stage.ProposalRefEntitlement)).Outcome);
        var proposal = await first.Coordination.AdvanceAsync(stage.State!,
            GitHubCoordinationStageUpdate.ProposalRefAdvanced(content.CommitOid, content.TreeOid));
        await Publish(first, remote, proposal.State!, content);
        var published = (await first.Coordination.ReadCurrentAsync()).State!;
        var terminal = await first.Coordination.AdvanceAsync(published,
            GitHubCoordinationStageUpdate.Terminal(GitHubCoordinationStage.Merged));
        Assert.NotNull(terminal.State);
        remote.MergedBase();
        var freshBytes = new byte[] { 5, 4, 3 };
        var authority = GitHubPublicationFactory.CreateAuthority(new(
            "Owner", "repo", "refs/heads/main", remote.BaseOid, "campaign", Hash('a'), Hash('b'), Hash('c'), 3,
            Hash('d'), Hash('e'), Hash('6'), Hash('7'), Hash('8'), "successor", "generation-2",
            null, null, null, null, null, null,
            new("predecessor", 1, "generation", content.CommitOid, GitHubPublicationPredecessorDisposition.Merged),
            GitHubPublicationTransitionKind.SuccessorAfterMerge, new(10, 10, 1000), new(10, 10, 1000),
            [new("file.bin", Sha256(Candidate), Sha256(freshBytes), 1, 1, 2, 1, 1)], []));
        using var successor = new Session(authority, remote);
        var nextClaim = await Claim(successor);
        var prepared = await successor.Proposal.PrepareAsync(nextClaim, Payload(authority, freshBytes));
        Assert.Equal(GitHubProposalOutcome.Prepared, prepared.Outcome);
        var created = await successor.Proposal.CreateContentAsync(prepared.Prepared!, nextClaim);
        Assert.Equal(GitHubProposalOutcome.ContentVerified, created.Outcome);
        var nextStage = await successor.Coordination.AdvanceAsync(nextClaim,
            GitHubCoordinationStageUpdate.ContentCreated(created.Content!.CommitOid));
        Assert.Equal(GitHubProposalOutcome.RefVerified,
            (await successor.Proposal.AdvanceRefAsync(created.Content, nextStage.State!, nextStage.ProposalRefEntitlement)).Outcome);
        Assert.Equal(content.CommitOid, remote.Refs[content.Ref]);
        Assert.NotEqual(content.Ref, created.Content.Ref);
        Assert.Equal(remote.BaseOid, Assert.Single(remote.Commits[created.Content.CommitOid].Parents));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cold_append_uses_authenticated_previous_head_and_complete_cumulative_tree(bool rewindAtCas)
    {
        var remote = new Remote();
        var initial = Authority(remote);
        using var first = new Session(initial, remote);
        var (claim, content) = await CreateContent(first);
        var stage = await first.Coordination.AdvanceAsync(claim, GitHubCoordinationStageUpdate.ContentCreated(content.CommitOid));
        Assert.Equal(GitHubProposalOutcome.RefVerified,
            (await first.Proposal.AdvanceRefAsync(content, stage.State!, stage.ProposalRefEntitlement)).Outcome);
        var proposal = await first.Coordination.AdvanceAsync(stage.State!,
            GitHubCoordinationStageUpdate.ProposalRefAdvanced(content.CommitOid, content.TreeOid));
        await Publish(first, remote, proposal.State!, content);
        var append = Authority(remote, "append", initial, [0, 253, 3, 13, 10], includeNew: true);
        using var admitted = new Session(append, remote);
        await Claim(admitted);
        using var restart = new Session(append, remote);
        var restartedClaim = (await restart.Coordination.ReadCurrentAsync()).State!;
        var source = restart.Coordination.AdmissionSource(restartedClaim);
        Assert.Equal(content.CommitOid, source!.ProposalCommitOid);
        var payload = Payload(append, [0, 253, 3, 13, 10], includeNew: true);
        var prepared = await restart.Proposal.PrepareAsync(restartedClaim, payload);
        Assert.Equal(GitHubProposalOutcome.Prepared, prepared.Outcome);
        var created = await restart.Proposal.CreateContentAsync(prepared.Prepared!, restartedClaim);
        Assert.Equal(GitHubProposalOutcome.ContentVerified, created.Outcome);
        Assert.Equal(content.CommitOid, Assert.Single(remote.Commits[created.Content!.CommitOid].Parents));
        var next = await restart.Coordination.AdvanceAsync(restartedClaim,
            GitHubCoordinationStageUpdate.ContentCreated(created.Content.CommitOid));
        Assert.NotNull(next.ProposalRefEntitlement);
        if (rewindAtCas) remote.ProposalBeforeCas = remote.BaseOid;
        Assert.Equal(rewindAtCas ? GitHubProposalOutcome.Failed : GitHubProposalOutcome.RefVerified,
            (await restart.Proposal.AdvanceRefAsync(created.Content, next.State!, next.ProposalRefEntitlement)).Outcome);
        Assert.Equal(rewindAtCas ? 1 : 2, remote.ProposalWrites);
        var newRoot = remote.Trees[created.Content.TreeOid];
        var nested = remote.Trees[newRoot.Single(e => e.Path == "keep").Oid];
        Assert.Equal(new byte[] { 7 }, remote.Blobs[nested.Single().Oid]);
    }

    private static async Task Publish(Session session, Remote remote, IGitHubCoordinationStateCapability state, IGitHubProposalContent content,
        GitHubCoordinationStage stage = GitHubCoordinationStage.Published)
    {
        var stored = remote.CoordinationState(state.HeadOid);
        var creation = (string)typeof(GitHubCoordinationCodec).GetMethod("PullRequestCreationCommitment",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [stored, content.Ref])!;
        var marker = Sha256(Encoding.UTF8.GetBytes("<!-- contract-scribe-publication-v1 ownership=sha256:" + creation + " -->\n"));
        var result = await session.Coordination.AdvanceAsync(state, GitHubCoordinationStageUpdate.PullRequestResult(
            stage, content.CommitOid, content.TreeOid, creation, 1, remote.BaseOid, remote.Refs["refs/heads/main"], marker));
        Assert.Equal(GitHubCoordinationOutcome.Advanced, result.Outcome);
    }

    private static async Task<IGitHubCoordinationStateCapability> Claim(Session session)
    {
        var read = await session.Coordination.ReadCurrentAsync();
        var result = await session.Coordination.ClaimAsync(read.Read!);
        Assert.NotNull(result.State);
        return result.State!;
    }
    private static async Task<(IGitHubCoordinationStateCapability Claim, IGitHubProposalContent Content)> CreateContent(
        Session session, IGitHubCoordinationStateCapability? current = null)
    {
        var claim = current ?? await Claim(session);
        var prepared = await session.Proposal.PrepareAsync(claim, Payload(session.Authority));
        Assert.Equal(GitHubProposalOutcome.Prepared, prepared.Outcome);
        var content = await session.Proposal.CreateContentAsync(prepared.Prepared!, claim);
        Assert.Equal(GitHubProposalOutcome.ContentVerified, content.Outcome);
        return (claim, content.Content!);
    }
    private static ValidatedGitHubPublicationAuthority Authority(Remote remote, string operation = "initial",
        ValidatedGitHubPublicationAuthority? previous = null, byte[]? candidate = null, bool includeNew = false) => GitHubPublicationFactory.CreateAuthority(new(
            "Owner", "repo", "refs/heads/main", remote.BaseOid, "campaign", Hash('1'), Hash('2'), Hash('3'), previous is null ? 1 : 2,
            Hash('4'), Sha256(candidate ?? Candidate), Hash('6'), Hash('7'), Hash('8'), operation, "generation",
            previous?.OperationId, previous?.AuthorityCommitmentSha256, previous?.CandidateCommitmentSha256,
            previous?.GenerationId, previous?.SnapshotCommitmentSha256, previous?.PolicyCommitmentSha256, null,
            previous is null ? GitHubPublicationTransitionKind.Initial : GitHubPublicationTransitionKind.SameSnapshotAppend,
            new(10, 10, 1000), new(10, 10, 1000),
            includeNew ? [new("file.bin", Sha256(Original), Sha256(candidate ?? Candidate), 1, 1, 2, 1, 1),
                new("keep/nested.txt", Sha256(Unchanged), Sha256([7]), 1, 1, 2, 1, 1)]
                : [new("file.bin", Sha256(Original), Sha256(candidate ?? Candidate), 1, 1, 2, 1, 1)],
            previous is null ? [] : [new("file.bin", previous.ChangedFiles[0].CandidateFileSha256)]));
    private static ValidatedGitHubChangedFilePayload Payload(ValidatedGitHubPublicationAuthority authority, byte[]? bytes = null, bool includeNew = false) =>
        GitHubPublicationFactory.CreatePayload(authority, includeNew
            ? [new("file.bin", bytes ?? Candidate), new("keep/nested.txt", new byte[] { 7 })]
            : [new("file.bin", bytes ?? Candidate)]);

    private sealed class ForgedPrepared : IGitHubPreparedProposal;
    private sealed class ForgedEntitlement : IGitHubProposalRefEntitlement;
    private sealed class Session : IDisposable
    {
        internal Session(ValidatedGitHubPublicationAuthority authority, Remote remote)
        {
            Authority = authority;
            using var hook = (IDisposable)typeof(GitHubTransportTestHook).GetMethod("Register", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [new Uri("http://127.0.0.1:43219/"), new Handler(remote), 30_000])!;
            Client = GitHubApiClient.Create(authority, GitHubTransportTestHook.Placeholder);
            Coordination = GitHubCoordinationStore.Create(Client);
            Proposal = GitHubProposalStore.Create(Client, Coordination);
        }
        internal ValidatedGitHubPublicationAuthority Authority { get; }
        internal GitHubApiClient Client { get; }
        internal GitHubCoordinationStore Coordination { get; }
        internal GitHubProposalStore Proposal { get; }
        public void Dispose() => Client.Dispose();
    }

    private sealed class Handler(Remote remote) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            remote.Reply(request, cancellationToken);
    }

    private sealed class Remote
    {
        internal readonly Dictionary<string, byte[]> Blobs = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, ImmutableArray<GitHubTreeEntry>> Trees = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, GitHubCommit> Commits = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, string> Refs = new(StringComparer.Ordinal);
        internal readonly List<(string Method, string Path)> Requests = [];
        private readonly object gate = new();
        internal string FileOid { get; }
        internal string BaseOid { get; private set; } = "";
        internal string BaseTreeOid { get; private set; } = "";
        internal ImmutableArray<GitHubTreeEntry> BaseRoot { get; private set; }
        internal string? TruncatedOid;
        internal long RepositoryId = 1;
        internal string? LoseAfter;
        internal string? LoseBefore;
        internal string? CancelAfter;
        internal string? CorruptAfter;
        internal string? LateFailure;
        private string? lateCommit;
        private int lateReads;
        internal string? ProposalBeforeCas;
        internal CancellationTokenSource? CancelSource;
        internal int Writes;
        internal int ProposalAttempts;
        internal int ProposalWrites;
        private string? barrierMethod;
        private string? barrierPath;
        private bool barrierMissing;
        private int barrierRemaining;
        private TaskCompletionSource? barrier;

        internal Remote()
        {
            FileOid = AddBlob(Original);
            var keep = AddBlob(Unchanged);
            var nested = AddTree([new("nested.txt", GitHubTreeMode.File, keep, null)]);
            BaseRoot = [new("file.bin", GitHubTreeMode.Executable, FileOid, null),
                new("keep", GitHubTreeMode.Directory, nested, null), new("link", GitHubTreeMode.SymbolicLink, keep, null),
                new("module", GitHubTreeMode.Submodule, Oid('9'), null)];
            SetBase();
        }
        internal void ChangeBase(string kind)
        {
            BaseRoot = kind switch
            {
                "missing-path" => BaseRoot.RemoveAt(0),
                "symlink" => BaseRoot.SetItem(0, BaseRoot[0] with { Mode = GitHubTreeMode.SymbolicLink }),
                "submodule" => BaseRoot.SetItem(0, BaseRoot[0] with { Mode = GitHubTreeMode.Submodule }),
                // An invalid case-colliding tree is supplied under a real-looking fixed OID.
                "case" => BaseRoot.Add(new("FILE.BIN", GitHubTreeMode.File, FileOid, null)),
                _ => throw new InvalidOperationException(),
            };
            if (kind == "case")
            {
                BaseTreeOid = Oid('8'); Trees[BaseTreeOid] = BaseRoot;
                var request = Commit(BaseTreeOid, Oid('1'), Hash('a'), Hash('b'));
                BaseOid = request.ExpectedOid; AddCommit(request); Refs["refs/heads/main"] = BaseOid;
            }
            else SetBase();
        }
        private void SetBase()
        {
            BaseTreeOid = AddTree(BaseRoot);
            var request = Commit(BaseTreeOid, Oid('1'), Hash('a'), Hash('b'));
            BaseOid = request.ExpectedOid; AddCommit(request); Refs["refs/heads/main"] = BaseOid;
        }
        internal void DeepBase(int depth)
        {
            var oid = AddTree([new("x", GitHubTreeMode.File, FileOid, null)]);
            for (var i = 1; i < depth; i++) oid = AddTree([new("d", GitHubTreeMode.Directory, oid, null)]);
            BaseRoot = BaseRoot.SetItem(1, new("keep", GitHubTreeMode.Directory, oid, null));
            SetBase();
        }
        internal void MergedBase()
        {
            BaseRoot = BaseRoot.SetItem(0, BaseRoot[0] with { Oid = AddBlob(Candidate) });
            SetBase();
        }
        private string AddBlob(byte[] bytes) { var oid = ObjectOid("blob", bytes); Blobs[oid] = bytes; return oid; }
        private string AddTree(ImmutableArray<GitHubTreeEntry> entries) { var oid = TreeOid(entries); Trees[oid] = entries; return oid; }
        private void AddCommit(GitHubCreateCommit value) => Commits[value.ExpectedOid] = new(value.ExpectedOid, value.TreeOid,
            [value.ParentOid], value.Message, value.Author, value.Committer);
        internal string? ProposalHead(ValidatedGitHubPublicationAuthority authority) =>
            Refs.GetValueOrDefault(GitHubPublicationFactory.CreateProposalRef(authority));
        internal GitHubCoordinationState CoordinationState(string head)
        {
            var root = Trees[Commits[head].TreeOid];
            var leaf = Trees[root[0].Oid];
            return GitHubCoordinationCodec.Decode(Blobs[leaf[0].Oid]);
        }
        internal void Barrier(string method, string path, int count, bool onlyMissing)
        { barrierMethod = method; barrierPath = path; barrierRemaining = count; barrierMissing = onlyMissing; barrier = new(TaskCreationOptions.RunContinuationsAsynchronously); }

        internal async Task<HttpResponseMessage> Reply(HttpRequestMessage request, CancellationToken token)
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            var method = request.Method.Method;
            var text = request.Content is null ? null : await request.Content.ReadAsStringAsync(token);
            Task? wait = null;
            HttpResponseMessage? early = null;
            lock (gate)
            {
                Requests.Add((method, path));
                if (barrierRemaining > 0 && method == barrierMethod && path.Contains(barrierPath!, StringComparison.Ordinal)
                    && (!barrierMissing || !Commits.ContainsKey(path[(path.LastIndexOf('/') + 1)..])))
                {
                    early = Get(path); // Freeze each missing observation before opening the barrier.
                    wait = barrier!.Task;
                    if (--barrierRemaining == 0) barrier.TrySetResult();
                }
            }
            if (wait is not null) { await wait.WaitAsync(TimeSpan.FromSeconds(10), token); return early!; }
            if (method == "GET" && LateFailure == "timeout" && lateReads == 1
                && path.EndsWith("/git/commits/" + lateCommit, StringComparison.Ordinal))
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            lock (gate)
            {
                if (method == "GET") return Get(path);
                Assert.Equal("POST", method);
                using var doc = JsonDocument.Parse(text!);
                var body = doc.RootElement;
                Writes++;
                string kind;
                object response;
                var status = HttpStatusCode.Created;
                var requestKind = path.EndsWith("/git/blobs", StringComparison.Ordinal) ? "blob"
                    : path.EndsWith("/git/trees", StringComparison.Ordinal) ? "tree"
                    : path.EndsWith("/git/commits", StringComparison.Ordinal) ? "commit"
                    : text!.Contains("/proposals/", StringComparison.Ordinal) ? "proposal-ref" : "coordination-ref";
                if (LoseBefore == requestKind) { LoseBefore = null; throw new IOException("synthetic pre-application loss"); }
                if (path.EndsWith("/git/blobs", StringComparison.Ordinal))
                {
                    Assert.Equal("base64", body.GetProperty("encoding").GetString());
                    var oid = AddBlob(Convert.FromBase64String(body.GetProperty("content").GetString()!));
                    response = new { sha = oid }; kind = "blob";
                }
                else if (path.EndsWith("/git/trees", StringComparison.Ordinal))
                {
                    var entries = body.GetProperty("tree").EnumerateArray().Select(e => new GitHubTreeEntry(
                        e.GetProperty("path").GetString()!, ParseMode(e.GetProperty("mode").GetString()!),
                        e.GetProperty("sha").GetString()!, null)).ToImmutableArray();
                    var oid = AddTree(entries); response = TreeResponse(oid); kind = "tree";
                }
                else if (path.EndsWith("/git/commits", StringComparison.Ordinal))
                {
                    var value = new GitHubCreateCommit("", body.GetProperty("tree").GetString()!,
                        Assert.Single(body.GetProperty("parents").EnumerateArray()).GetString()!,
                        body.GetProperty("message").GetString()!, ReadActor(body.GetProperty("author")), ReadActor(body.GetProperty("committer")));
                    value = value with { ExpectedOid = ObjectOid("commit", CommitBytes(value)) };
                    AddCommit(value); response = CommitResponse(Commits[value.ExpectedOid]); kind = "commit";
                    if (value.Message.StartsWith("ContractScribe proposal", StringComparison.Ordinal)
                        && LateFailure is "content" or "content-target" or "timeout") { lateCommit = value.ExpectedOid; lateReads = 2; }
                }
                else
                {
                    Assert.Equal("/graphql", path);
                    Assert.Equal(GitHubApiClient.UpdateRefsDocument, body.GetProperty("query").GetString());
                    var input = body.GetProperty("variables").GetProperty("input");
                    var update = Assert.Single(input.GetProperty("refUpdates").EnumerateArray());
                    var name = update.GetProperty("name").GetString()!;
                    Assert.StartsWith("refs/heads/contract-scribe/", name);
                    Assert.False(update.GetProperty("force").GetBoolean());
                    var before = update.GetProperty("beforeOid").GetString();
                    var after = update.GetProperty("afterOid").GetString()!;
                    Assert.NotEqual(Oid('0'), after);
                    kind = name.Contains("/proposals/", StringComparison.Ordinal) ? "proposal-ref" : "coordination-ref";
                    if (kind == "proposal-ref") ProposalAttempts++;
                    if (kind == "proposal-ref" && ProposalBeforeCas is not null)
                    { Refs[name] = ProposalBeforeCas; ProposalBeforeCas = null; }
                    if (Refs.GetValueOrDefault(name, Oid('0')) != before)
                        return Json(HttpStatusCode.OK, new { data = (object?)null, errors = new[] { new { message = "conflict", type = "CONFLICT" } } });
                    Refs[name] = after;
                    if (kind == "proposal-ref") ProposalWrites++;
                    if (kind == "proposal-ref" && LateFailure == "ref") { lateCommit = after; lateReads = 1; }
                    response = new { data = new { updateRefs = new { clientMutationId = input.GetProperty("clientMutationId").GetString() } } };
                    status = HttpStatusCode.OK;
                }
                if (LoseAfter == kind) { LoseAfter = null; throw new IOException("synthetic response loss"); }
                if (CorruptAfter == kind)
                {
                    CorruptAfter = null;
                    if (kind == "blob") Blobs[Blobs.Keys.Last()] = [99];
                    if (kind == "tree") Trees[Trees.Keys.Last()] = [];
                    if (kind == "commit")
                    {
                        var oid = Commits.Keys.Last();
                        Commits[oid] = Commits[oid] with { Message = "human modification" };
                    }
                    if (kind == "proposal-ref")
                    {
                        var name = Refs.Keys.Single(n => n.Contains("/proposals/", StringComparison.Ordinal));
                        Refs[name] = Oid('9');
                    }
                }
                if (CancelAfter == kind) { CancelAfter = null; CancelSource!.Cancel(); throw new OperationCanceledException(token); }
                return Json(status, response);
            }
        }
        private HttpResponseMessage Get(string path)
        {
            if (path == "/repos/Owner/repo") return Json(HttpStatusCode.OK, new
            {
                id = RepositoryId,
                node_id = "REPO_node",
                name = "repo",
                full_name = "Owner/repo",
                @private = false,
                archived = false,
                disabled = false,
                owner = new { id = 1, node_id = "OWNER_node", login = "Owner", type = "Organization" },
            });
            if (path.Contains("/git/ref/", StringComparison.Ordinal))
            {
                var name = "refs/" + path[(path.IndexOf("/git/ref/", StringComparison.Ordinal) + 9)..];
                return Refs.TryGetValue(name, out var head) ? Json(HttpStatusCode.OK,
                    new { @ref = name, node_id = "REF_node", @object = new { type = "commit", sha = head } }) : Missing();
            }
            var oid = path[(path.LastIndexOf('/') + 1)..];
            if (path.Contains("/git/blobs/", StringComparison.Ordinal) && Blobs.TryGetValue(oid, out var bytes))
                return Json(HttpStatusCode.OK, new { sha = oid, encoding = "base64", size = bytes.Length, content = Convert.ToBase64String(bytes) });
            if (path.Contains("/git/trees/", StringComparison.Ordinal) && Trees.ContainsKey(oid)) return Json(HttpStatusCode.OK, TreeResponse(oid));
            if (path.Contains("/git/commits/", StringComparison.Ordinal) && Commits.TryGetValue(oid, out var commit))
            {
                if (oid == lateCommit && --lateReads == 0)
                {
                    lateCommit = null;
                    if (LateFailure == "content-target") Refs["refs/heads/main"] = Oid('9');
                    else commit = commit with { Message = "late verification corruption" };
                }
                return Json(HttpStatusCode.OK, CommitResponse(commit));
            }
            return Missing();
        }
        private object TreeResponse(string oid) => new
        {
            sha = oid,
            truncated = oid == TruncatedOid,
            tree = Trees[oid].Select(e => new
            {
                path = e.Path,
                mode = e.Mode == GitHubTreeMode.Directory ? "040000" : Mode(e.Mode),
                type = e.Mode == GitHubTreeMode.Directory ? "tree" : e.Mode == GitHubTreeMode.Submodule ? "commit" : "blob",
                sha = e.Oid
            }).ToArray(),
        };
        private static object CommitResponse(GitHubCommit c) => new
        {
            sha = c.Oid,
            tree = new { sha = c.TreeOid },
            parents = c.Parents.Select(p => new { sha = p }).ToArray(),
            message = c.Message,
            author = ActorResponse(c.Author),
            committer = ActorResponse(c.Committer),
        };
        private static object ActorResponse(GitHubCommitActor a) => new { name = a.Name, email = a.Email, date = a.Date.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") };
        private static GitHubCommitActor ReadActor(JsonElement a) => new(a.GetProperty("name").GetString()!, a.GetProperty("email").GetString()!, a.GetProperty("date").GetDateTimeOffset());
        private static GitHubTreeMode ParseMode(string mode) => mode switch
        { "100644" => GitHubTreeMode.File, "100755" => GitHubTreeMode.Executable, "040000" => GitHubTreeMode.Directory, "120000" => GitHubTreeMode.SymbolicLink, "160000" => GitHubTreeMode.Submodule, _ => throw new InvalidOperationException() };
        private static HttpResponseMessage Missing() => Json(HttpStatusCode.NotFound, new { message = "Not Found" });
        private static HttpResponseMessage Json(HttpStatusCode status, object value) => new(status)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
