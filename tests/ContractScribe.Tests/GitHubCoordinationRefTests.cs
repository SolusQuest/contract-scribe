using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;
using ContractScribe.Core;
using ContractScribe.Cli;
using ContractScribe.GitHub.PullRequests;
using ContractScribe.GitHub.Coordination;
using ContractScribe.GitHub.Transport;
using ContractScribe.GitHub.Publication;

namespace ContractScribe.Tests;

public sealed partial class GitHubCoordinationRefTests
{
    internal static bool IsCheckpointQuery(HttpRequestMessage request, bool currentOnly = false)
    {
        if (request.Method != HttpMethod.Post || request.RequestUri!.AbsolutePath != "/graphql") return false;
        using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        return body.RootElement.GetProperty("query").GetString()!.TrimStart().StartsWith("query", StringComparison.Ordinal)
            && (!currentOnly || !body.RootElement.GetProperty("variables").TryGetProperty("anchor", out _));
    }

    internal static bool IsRefMutation(HttpRequestMessage request) => request.Method == HttpMethod.Post
        && request.RequestUri!.AbsolutePath == "/graphql" && !IsCheckpointQuery(request);

    internal static bool ReadsCanonicalDocument(HttpRequestMessage request, string oid)
    {
        if (!IsCheckpointQuery(request)) return false;
        using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        return body.RootElement.GetProperty("variables").TryGetProperty("anchor", out var anchor) && anchor.GetString() == oid;
    }

    [Fact]
    public async Task G1_first_run_observes_actual_HEAD_without_candidate_or_expected_base()
    {
        var remote = new G1Remote();
        remote.Coordination.TargetHead = Oid('9');
        using var observer = remote.Observer();
        var result = await observer.ObserveAsync();
        Assert.Equal(GitHubCampaignKind.FirstRun, result.Kind);
        Assert.Equal(GitHubCampaignLedgerKind.Missing, result.LedgerKind);
        Assert.Equal(Oid('9'), result.TargetOid);
        Assert.Equal(42, result.Repository!.Id);
        Assert.Null(result.CoordinationOid);
        Assert.True(result.CollectionExhausted);
        Assert.Empty(result.PullRequests);
        remote.AssertReadOnly();
    }

    [Fact]
    public async Task G1_current_unpublished_ledger_is_authenticated_without_matching_a_candidate()
    {
        var remote = new G1Remote();
        var claim = GitHubCoordinationCodec.CreateClaim(Authority(), Oid('0'));
        remote.Coordination.Seed(claim);
        using var observer = remote.Observer();
        var result = await observer.ObserveAsync();
        Assert.Equal(GitHubCampaignKind.Unpublished, result.Kind);
        Assert.Equal(GitHubCampaignLedgerKind.Current, result.LedgerKind);
        Assert.Equal(claim.CurrentCandidateCommitmentSha256, result.Ledger!.CandidateCommitmentSha256);
        Assert.Equal(claim.OperationId, result.Ledger.OperationId);
        Assert.Equal(remote.Coordination.CoordinationHead, result.CoordinationOid);
        remote.AssertReadOnly();
    }

    [Theory]
    [InlineData("draft", "OpenDraft", "Active")]
    [InlineData("ready", "OpenReady", "Active")]
    [InlineData("closed", "ClosedUnmerged", "Terminal")]
    [InlineData("merged", "Merged", "Terminal")]
    public async Task G1_observes_live_owned_PR_facts_without_candidate_or_user_endpoint(
        string lifecycle, string expectedPr, string expectedCampaign)
    {
        var remote = G1Remote.Published(lifecycle);
        if (lifecycle == "merged") remote.Coordination.TargetHead = Oid('9');
        using var observer = remote.Observer();
        var result = await observer.ObserveAsync();
        Assert.Null(result.Failure);
        Assert.Equal(expectedCampaign, result.Kind.ToString());
        var pr = Assert.Single(result.PullRequests);
        Assert.Equal(expectedPr, pr.Kind.ToString());
        Assert.Equal(17, pr.Number);
        Assert.Equal(1017, pr.Id);
        Assert.Equal("PR_17", pr.NodeId);
        Assert.Equal(Oid('1'), pr.BaseOid);
        Assert.True(pr.OwnershipVerified);
        Assert.Equal(GitHubPublicationPrincipal.ActionsBot, pr.Author);
        remote.AssertReadOnly();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task G1_pre_PR_states_distinguish_expected_name_from_actual_proposal_ref(int stage)
    {
        var authority = Authority();
        var chain = PublishedChain(authority);
        var remote = new G1Remote();
        remote.Coordination.SeedChain(chain.Take(stage + 1));
        var reference = GitHubPublicationFactory.CreateProposalRef(authority);
        if (stage == 2) remote.Refs[reference] = chain[stage].ProposalRefOid!;
        using var observer = remote.Observer();
        var result = await observer.ObserveAsync();
        Assert.Null(result.Failure);
        Assert.Equal(GitHubCampaignKind.Unpublished, result.Kind);
        Assert.Equal(reference, result.Ledger!.ExpectedProposalRef);
        if (stage == 2)
        {
            Assert.Equal(reference, result.ProposalRef!.Name);
            Assert.Equal(chain[stage].ProposalRefOid, result.ProposalRef.Oid);
            Assert.Equal("REF_" + reference.Split('/')[^1], result.ProposalRef.NodeId);
        }
        else Assert.Null(result.ProposalRef);
        Assert.Equal(2, remote.Requests.Count(path => path.EndsWith(reference[5..], StringComparison.Ordinal)));
        remote.AssertReadOnly();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task G1_unrecorded_proposal_ref_presence_or_late_appearance_cannot_be_absence(bool late)
    {
        var authority = Authority();
        var remote = new G1Remote();
        remote.Coordination.Seed(GitHubCoordinationCodec.CreateClaim(authority, Oid('0')));
        var reference = GitHubPublicationFactory.CreateProposalRef(authority);
        var reads = 0;
        if (!late) remote.Refs[reference] = Oid('2');
        remote.BeforeRead = path =>
        {
            if (path.EndsWith(reference[5..], StringComparison.Ordinal) && ++reads == 2)
                remote.Refs[reference] = Oid('2');
        };
        using var observer = remote.Observer();
        var result = await observer.ObserveAsync();
        Assert.Equal(GitHubCampaignKind.Unverifiable, result.Kind);
        Assert.Equal(GitHubCampaignFailureKind.RefChanged, result.Failure!.Kind);
        Assert.Equal(reference, result.Failure.Ref);
        Assert.Equal(Oid('2'), result.ProposalRef!.Oid);
        remote.AssertReadOnly();
    }

    [Theory]
    [InlineData("merged", false)]
    [InlineData("merged", true)]
    [InlineData("closed", false)]
    [InlineData("closed", true)]
    public async Task G1_terminal_successor_history_requires_a_different_generation(string lifecycle, bool sameGeneration)
    {
        var first = Authority();
        var chain = PublishedChain(first);
        var terminalStage = lifecycle == "merged" ? GitHubCoordinationStage.Merged : GitHubCoordinationStage.ClosedUnmerged;
        var terminal = G1Terminal(chain[^1], terminalStage);
        var next = SuccessorAuthority(first, terminal, terminalStage);
        var claim = GitHubCoordinationCodec.CreateClaim(next, PrepareFixture(terminal).CommitOid);
        if (sameGeneration) claim = GitHubCoordinationCodec.Decode(Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(GitHubCoordinationCodec.Encode(claim)).Replace(
                "\"generationId\":\"generation-2\"", "\"generationId\":\"generation-1\"", StringComparison.Ordinal)));
        var remote = new G1Remote();
        remote.Coordination.TargetHead = Oid('9');
        remote.Coordination.SeedChain([.. chain, terminal, claim]);
        remote.AddPr(first, chain[^1], lifecycle);
        using var observer = remote.Observer();
        var result = await observer.ObserveAsync();
        if (sameGeneration)
        {
            Assert.Equal(GitHubCampaignLedgerKind.Corrupt, result.LedgerKind);
            Assert.Equal(GitHubCampaignKind.Unverifiable, result.Kind);
            Assert.Equal(GitHubCampaignFailureKind.Corrupt, result.Failure!.Kind);
            Assert.Null(result.Ledger);
        }
        else
        {
            Assert.Null(result.Failure);
            Assert.Equal(GitHubCampaignKind.Unpublished, result.Kind);
            Assert.Equal("generation-2", result.Ledger!.GenerationId);
        }
        remote.AssertReadOnly();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task G1_sealed_generation_rejects_a_replacement_hidden_behind_any_partial_stage(int stage)
    {
        var original = InitialAuthority("creation-operation", '5');
        var history = PublishedChain(original).ToList();
        var replacement = ReplacementAuthority(original, "replacement", '6');
        var parent = PrepareFixture(history[^1]).CommitOid;
        foreach (var value in PublishedChain(replacement).Take(stage + 1))
        {
            var forged = Reanchor(value, parent);
            history.Add(forged);
            parent = PrepareFixture(forged).CommitOid;
        }
        var remote = new G1Remote();
        remote.Coordination.SeedChain(history);
        remote.AddPr(original, history[4], "draft");
        using var observer = remote.Observer();
        var result = await observer.ObserveAsync();
        Assert.Equal(GitHubCampaignLedgerKind.Corrupt, result.LedgerKind);
        Assert.Null(result.Ledger);
        Assert.Equal(GitHubCampaignKind.Unverifiable, result.Kind);
        remote.AssertReadOnly();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task G1_incompatible_current_or_ancestor_keeps_existing_publication_diagnostic(bool ancestor)
    {
        var authority = GitHubProposalBranchTests.Authority(new GitHubProposalBranchTests.Remote());
        var state = GitHubCoordinationCodec.CreateClaim(authority, Oid('0'));
        var remote = new G1Remote();
        remote.Coordination.TargetHead = authority.ExpectedBaseCommitOid;
        var text = Encoding.UTF8.GetString(GitHubCoordinationCodec.Encode(state));
        remote.Coordination.SeedRaw(Encoding.UTF8.GetBytes(text.Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal)),
            state.OperationCommitmentSha256, "claimed", state.TargetCommitOid);
        if (ancestor) remote.Coordination.Seed(GitHubCoordinationCodec.WithStage(state,
            GitHubCoordinationStage.ContentCreated, remote.Coordination.CoordinationHead!, Oid('2')));
        using (var observer = remote.Observer(GitHubPublicationFactory.CreateCampaignReadAuthority(authority)))
        {
            var result = await observer.ObserveAsync();
            Assert.Equal(GitHubCampaignLedgerKind.Incompatible, result.LedgerKind);
        }
        using var client = Client(authority, remote.Coordination);
        var reconciler = GitHubPublicationReconciler.Create(client, GitHubPublicationPrincipal.ActionsBot);
        var attempt = await reconciler.PublishObservedAsync(authority, GitHubProposalBranchTests.Payload(authority), default);
        Assert.Equal(GitHubPublicationResultKind.HumanChange, attempt.Result.Kind);
        Assert.Equal(GitHubCoordinationFailureKind.ObjectMismatch, attempt.Diagnostic!.CoordinationFailure);
        Assert.True(attempt.Diagnostic.IsValid);
        remote.AssertReadOnly();
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("merged")]
    public async Task G1_rereads_reopened_terminal_PR_and_preserves_actual_active_identity(string terminal)
    {
        var remote = G1Remote.Published(terminal);
        G1Remote.Lifecycle(remote.Prs[0], "draft");
        using var observer = remote.Observer();
        var result = await observer.ObserveAsync();
        Assert.Equal(GitHubCampaignKind.Unverifiable, result.Kind);
        Assert.Equal(GitHubCampaignFailureKind.StaleLedger, result.Failure!.Kind);
        Assert.Equal(GitHubCampaignPullRequestKind.OpenDraft, Assert.Single(result.PullRequests).Kind);
        Assert.Equal(17, result.Failure.PullRequestNumber);
        remote.AssertReadOnly();
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("merged")]
    public async Task G3_verified_terminal_PR_retains_immutable_base_when_its_proposal_branch_is_deleted(string terminal)
    {
        var remote = G1Remote.Published(terminal);
        remote.Refs.Clear();
        using var observer = remote.Observer();
        var result = await observer.ObserveAsync();
        Assert.Null(result.Failure);
        Assert.Equal(GitHubCampaignKind.Terminal, result.Kind);
        Assert.True(Assert.Single(result.PullRequests).OwnershipVerified);
        Assert.Equal(Oid('1'), result.PullRequests[0].OriginalBaseOid);
        Assert.Null(result.ProposalRef);
        remote.AssertReadOnly();
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("merged")]
    public async Task G3_terminal_snapshot_seed_survives_an_incomplete_successor_and_mutable_PR_base(string terminal)
    {
        var remote = G1Remote.Published(terminal);
        using var observer = remote.Observer();
        var observed = await observer.ObserveAsync();
        Assert.Null(observed.Failure);
        var seed = GitHubCampaignPreflight.TerminalSeed(observed);
        var successor = observed with
        {
            Ledger = observed.Ledger! with
            {
                Stage = GitHubCoordinationStage.Claimed,
                OperationId = "operation.incomplete-successor",
                OperationCommitmentSha256 = new string('9', 64),
                GenerationId = "generation.incomplete-successor",
            },
            PullRequests = observed.PullRequests.Select(pr => pr with { BaseOid = Oid('9') }).ToImmutableArray(),
        };
        Assert.Equal(seed, GitHubCampaignPreflight.TerminalSeed(successor));
        Assert.NotEqual(seed, GitHubCampaignPreflight.TerminalSeed(observed with
        {
            PullRequests = observed.PullRequests.Select(pr => pr with { GenerationId = "generation.other-terminal" }).ToImmutableArray(),
        }));
        remote.AssertReadOnly();
    }

    [Fact]
    public async Task G3_closed_base_reason_links_the_matching_retained_generation()
    {
        var remote = G1Remote.Published("closed");
        using var observer = remote.Observer();
        var observed = await observer.ObserveAsync();
        var first = Assert.Single(observed.PullRequests);
        var decision = observed with
        {
            TargetOid = Oid('9'),
            PullRequests = [first, first with
            {
                Number = 18, OriginalBaseOid = Oid('9'), BaseOid = Oid('1'), GenerationId = "closed-at-current-base",
            }],
        };
        var result = GitHubCampaignPreflight.Select(CliBuildIdentity.Current, CampaignOperation.Resume, decision, 7);
        Assert.NotNull(result);
        Assert.Equal("github-proposal.closed-same-base", Assert.Single(result.Diagnostics).Code);
        using var json = JsonDocument.Parse(result.StandardOutput);
        Assert.EndsWith("/pull/18", json.RootElement.GetProperty("pullRequestUrl").GetString(), StringComparison.Ordinal);
        Assert.Equal("closed-at-current-base", json.RootElement.GetProperty("generationId").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("publicationOperationId").ValueKind);
        remote.AssertReadOnly();
    }

    [Fact]
    public async Task G1_missing_ledger_with_managed_PR_never_becomes_first_run()
    {
        var remote = G1Remote.Published("draft", seed: false);
        using var observer = remote.Observer();
        var result = await observer.ObserveAsync();
        Assert.Equal(GitHubCampaignLedgerKind.Missing, result.LedgerKind);
        Assert.Equal(GitHubCampaignKind.Unverifiable, result.Kind);
        Assert.True(result.CollectionExhausted);
        var pr = Assert.Single(result.PullRequests);
        Assert.Equal(GitHubCampaignPullRequestKind.OpenDraft, pr.Kind);
        Assert.False(pr.OwnershipVerified);
        remote.AssertReadOnly();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task G1_complete_pagination_sees_later_managed_PR_and_unknown_last_page_never_proves_absence(bool failure)
    {
        var remote = G1Remote.Published("draft");
        var other = (JsonObject)remote.Prs[0].DeepClone();
        other["id"] = 1002; other["node_id"] = "PR_2"; other["number"] = 2;
        other["head"]!["ref"] = "unrelated"; other["body"] = null;
        remote.Prs.Insert(0, other);
        remote.Paginate = true;
        remote.FailLastPage = failure;
        using var observer = remote.Observer();
        var result = await observer.ObserveAsync();
        Assert.Equal(failure ? GitHubCampaignKind.Unverifiable : GitHubCampaignKind.Active, result.Kind);
        Assert.Equal(!failure, result.CollectionExhausted);
        if (!failure) { Assert.Equal(2, result.CollectionPages); Assert.Equal(17, Assert.Single(result.PullRequests).Number); }
        Assert.Contains(remote.Requests, path => path.Contains("page=2", StringComparison.Ordinal));
        remote.AssertReadOnly();
    }

    [Fact]
    public async Task G1_multiple_active_or_same_generation_duplicates_are_ambiguous_without_ledger()
    {
        foreach (var lifecycle in new[] { "draft", "closed" })
        {
            var remote = G1Remote.Published(lifecycle, seed: false);
            var duplicate = (JsonObject)remote.Prs[0].DeepClone();
            duplicate["id"] = 1018; duplicate["node_id"] = "PR_18"; duplicate["number"] = 18;
            remote.Prs.Add(duplicate);
            using var observer = remote.Observer();
            var result = await observer.ObserveAsync();
            Assert.Equal(GitHubCampaignKind.Unverifiable, result.Kind);
            Assert.Equal(lifecycle == "draft" ? GitHubCampaignFailureKind.MultipleActive : GitHubCampaignFailureKind.Ambiguous, result.Failure!.Kind);
            Assert.Equal(2, result.PullRequests.Length);
            remote.AssertReadOnly();
        }
    }

    [Theory]
    [InlineData("User")]
    [InlineData("Bot")]
    [InlineData("Mannequin")]
    [InlineData("null")]
    [InlineData("unknown")]
    public async Task G1_untrusted_or_unavailable_author_never_acquires_managed_ownership(string actor)
    {
        var remote = G1Remote.Published("draft");
        remote.Prs[0]["user"] = actor == "null" ? null : JsonSerializer.SerializeToNode(new
        { id = 77, node_id = "U_77", login = "synthetic-other", type = actor == "unknown" ? "FutureActor" : actor });
        using var observer = remote.Observer();
        var result = await observer.ObserveAsync();
        Assert.Equal(GitHubCampaignKind.Unverifiable, result.Kind);
        Assert.All(result.PullRequests, pr => Assert.False(pr.OwnershipVerified));
        Assert.NotNull(result.Failure);
        remote.AssertReadOnly();
    }

    [Theory]
    [InlineData("body")]
    [InlineData("title")]
    [InlineData("fork")]
    [InlineData("deleted")]
    [InlineData("base")]
    public async Task G1_human_or_foreign_PR_changes_are_source_free_unverifiable_facts(string change)
    {
        var remote = G1Remote.Published("draft");
        if (change is "body" or "title") remote.Prs[0][change] = "SYNTHETIC_PRIVATE_BODY_DO_NOT_PROJECT";
        if (change == "fork") remote.Prs[0]["head"]!["repo"]!["id"] = 55;
        if (change == "deleted") remote.Prs[0]["head"]!["repo"] = null;
        if (change == "base") remote.Prs[0]["base"]!["sha"] = Oid('9');
        using var observer = remote.Observer();
        var result = await observer.ObserveAsync();
        Assert.Equal(GitHubCampaignKind.Unverifiable, result.Kind);
        Assert.Equal(GitHubCampaignFailureKind.Ownership, result.Failure!.Kind);
        Assert.Equal(17, Assert.Single(result.PullRequests).Number);
        Assert.DoesNotContain("SYNTHETIC_PRIVATE_BODY_DO_NOT_PROJECT", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.Equal(nameof(GitHubCampaignObservation), result.ToString());
        Assert.Equal(nameof(GitHubCampaignFailure), result.Failure.ToString());
        remote.AssertReadOnly();
    }

    [Theory]
    [InlineData("target")]
    [InlineData("proposal")]
    [InlineData("pr")]
    [InlineData("transport")]
    public async Task G1_missing_or_unknown_remote_facts_fail_closed_with_exact_identity(string missing)
    {
        var remote = G1Remote.Published("draft");
        if (missing == "target") remote.RejectPath = "/git/ref/heads/main";
        if (missing == "proposal") remote.Refs.Clear();
        if (missing == "pr") remote.Prs.Clear();
        if (missing == "transport") { remote.RejectPath = "/pulls"; remote.RejectStatus = HttpStatusCode.Forbidden; }
        using var observer = remote.Observer();
        var result = await observer.ObserveAsync();
        Assert.Equal(GitHubCampaignKind.Unverifiable, result.Kind);
        Assert.NotNull(result.Failure);
        Assert.Equal("refs/heads/main", result.TargetRef);
        Assert.Equal(42, result.Repository!.Id);
        if (missing is "target" or "proposal") Assert.Equal(GitHubCampaignFailureKind.MissingRef, result.Failure.Kind);
        if (missing == "pr") { Assert.Equal(GitHubCampaignFailureKind.MissingPullRequest, result.Failure.Kind); Assert.Equal(17, result.Failure.PullRequestNumber); }
        remote.AssertReadOnly();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task G1_target_movement_and_late_read_window_drift_preserve_the_observed_PR(bool late)
    {
        var remote = G1Remote.Published("draft");
        if (!late) remote.Coordination.TargetHead = Oid('9');
        else
        {
            var reads = 0;
            remote.BeforeRead = path =>
            { if (path.EndsWith("/git/ref/heads/main", StringComparison.Ordinal) && ++reads == 2) remote.Coordination.TargetHead = Oid('9'); };
        }
        using var observer = remote.Observer();
        var result = await observer.ObserveAsync();
        Assert.Equal(GitHubCampaignKind.Unverifiable, result.Kind);
        Assert.Equal(late ? GitHubCampaignFailureKind.RefChanged : GitHubCampaignFailureKind.TargetMoved, result.Failure!.Kind);
        Assert.Equal(17, Assert.Single(result.PullRequests).Number);
        remote.AssertReadOnly();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task G1_unsupported_contract_and_corrupt_canonical_bytes_are_distinct_closed_observations(bool incompatible)
    {
        var remote = new G1Remote();
        var state = GitHubCoordinationCodec.CreateClaim(Authority(), Oid('0'));
        var text = Encoding.UTF8.GetString(GitHubCoordinationCodec.Encode(state));
        var bytes = Encoding.UTF8.GetBytes(incompatible ? text.Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal) : text + " ");
        remote.Coordination.SeedRaw(bytes, state.OperationCommitmentSha256, "claimed", state.TargetCommitOid);
        using var observer = remote.Observer();
        var result = await observer.ObserveAsync();
        Assert.Equal(GitHubCampaignKind.Unverifiable, result.Kind);
        Assert.Equal(incompatible ? GitHubCampaignLedgerKind.Incompatible : GitHubCampaignLedgerKind.Corrupt, result.LedgerKind);
        Assert.NotNull(result.CoordinationOid);
        Assert.Null(result.Ledger);
        Assert.False(result.CollectionExhausted);
        remote.AssertReadOnly();
    }

    [Theory]
    [InlineData(ObjectTamper.Blob)]
    [InlineData(ObjectTamper.LeafTree)]
    [InlineData(ObjectTamper.RootTree)]
    [InlineData(ObjectTamper.Parent)]
    [InlineData(ObjectTamper.Tree)]
    [InlineData(ObjectTamper.Actor)]
    [InlineData(ObjectTamper.Time)]
    [InlineData(ObjectTamper.Message)]
    public async Task G1_candidate_free_reader_retains_complete_Git_object_authentication(ObjectTamper tamper)
    {
        var remote = new G1Remote();
        remote.Coordination.Seed(GitHubCoordinationCodec.CreateClaim(Authority(), Oid('0')));
        remote.Coordination.Tamper(tamper);
        using var observer = remote.Observer();
        var result = await observer.ObserveAsync();
        Assert.Equal(GitHubCampaignKind.Unverifiable, result.Kind);
        Assert.Null(result.Ledger);
        Assert.NotNull(result.Failure);
        remote.AssertReadOnly();
    }

    [Fact]
    public async Task G1_terminal_detail_reopening_during_read_window_is_not_cached_completion()
    {
        var remote = G1Remote.Published("closed");
        remote.AfterDetail = () => G1Remote.Lifecycle(remote.Prs[0], "draft");
        using var observer = remote.Observer();
        var result = await observer.ObserveAsync();
        Assert.Equal(GitHubCampaignKind.Unverifiable, result.Kind);
        Assert.Equal(GitHubCampaignFailureKind.StaleLedger, result.Failure!.Kind);
        Assert.Equal(17, result.Failure.PullRequestNumber);
        remote.AssertReadOnly();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1025)]
    public async Task G1_observes_old_terminal_and_current_active_generations_from_complete_history(int checkpointCommits)
    {
        var first = Authority();
        var chain = PublishedChain(first);
        var terminal = G1Terminal(chain[^1], GitHubCoordinationStage.Merged);
        var next = SuccessorAuthority(first, terminal, GitHubCoordinationStage.Merged);
        var claim = GitHubCoordinationCodec.CreateClaim(next, PrepareFixture(terminal).CommitOid);
        var content = GitHubCoordinationCodec.WithStage(claim, GitHubCoordinationStage.ContentCreated,
            PrepareFixture(claim).CommitOid, Oid('2'));
        var proposal = GitHubCoordinationCodec.WithStage(content, GitHubCoordinationStage.ProposalRefAdvanced,
            PrepareFixture(content).CommitOid, Oid('2'), Oid('2'), Oid('2'), Oid('3'));
        var creation = PullRequestCreationCommitment(claim, GitHubPublicationFactory.CreateProposalRef(next), Oid('2'), Oid('3'));
        var fence = GitHubCoordinationCodec.WithStage(proposal, GitHubCoordinationStage.PullRequestCreateStarted,
            PrepareFixture(proposal).CommitOid, Oid('2'), Oid('2'), Oid('2'), Oid('3'), creation, null,
            Oid('9'), Oid('9'), OwnershipMarker(creation));
        var published = GitHubCoordinationCodec.WithStage(fence, GitHubCoordinationStage.Published,
            PrepareFixture(fence).CommitOid, Oid('2'), Oid('2'), Oid('2'), Oid('3'), creation, 18,
            Oid('9'), Oid('9'), OwnershipMarker(creation));
        var remote = new G1Remote();
        remote.Coordination.TargetHead = Oid('9');
        remote.Coordination.SeedChain([.. chain, terminal, claim, content, proposal, fence, published]);
        remote.AddPr(first, chain[^1], "merged");
        remote.AddPr(next, published, "draft");
        for (var index = 0; index < checkpointCommits; index++)
        {
            var head = remote.Coordination.CoordinationHead!;
            var document = FixtureDocuments[head].RetainAt(head) with { PredecessorOid = head };
            var prepared = GitHubCoordinationObjects.Prepare(document);
            remote.Coordination.SeedObjects(prepared);
            remote.Coordination.ForceCoordinationHead(prepared.CommitOid);
        }
        var reads = remote.Coordination.BlobReadAttempts;
        using var observer = remote.Observer();
        var result = await observer.ObserveAsync();
        Assert.Null(result.Failure);
        Assert.Equal(GitHubCampaignKind.Active, result.Kind);
        Assert.Equal(2, result.PullRequests.Length);
        Assert.Equal(GitHubCampaignPullRequestKind.Merged, result.PullRequests[0].Kind);
        Assert.Equal(GitHubCampaignPullRequestKind.OpenDraft, result.PullRequests[1].Kind);
        Assert.All(result.PullRequests, pr => Assert.True(pr.OwnershipVerified));
        Assert.Equal("generation-2", result.Ledger!.GenerationId);
        Assert.InRange(remote.Coordination.BlobReadAttempts - reads, 1, 100);
        remote.AssertReadOnly();
    }

    private static GitHubCoordinationState G1Terminal(GitHubCoordinationState state, GitHubCoordinationStage stage) =>
        GitHubCoordinationCodec.WithStage(state, stage, PrepareFixture(state).CommitOid,
            state.ContentCommitOid, state.ProposalRefOid, state.ProposalCommitOid, state.ProposalTreeOid,
            state.PullRequestCreationOperationCommitmentSha256, state.PullRequestNumber,
            state.ExpectedBaseOid, state.ObservedBaseOid, state.OwnershipMarkerSha256);

    [Theory]
    [InlineData("prefix")]
    [InlineData("marker")]
    [InlineData("number")]
    public async Task G1_relevance_never_discards_a_potentially_managed_PR(string relevance)
    {
        var remote = G1Remote.Published("draft", seed: relevance == "number");
        if (relevance != "prefix") remote.Prs[0]["head"]!["ref"] = "foreign-valid-head";
        if (relevance != "marker") remote.Prs[0]["body"] = null;
        using var observer = remote.Observer();
        var result = await observer.ObserveAsync();
        Assert.Equal(GitHubCampaignKind.Unverifiable, result.Kind);
        Assert.Equal(17, Assert.Single(result.PullRequests).Number);
        Assert.True(result.CollectionExhausted);
        remote.AssertReadOnly();
    }

    [Theory]
    [InlineData("coordination")]
    [InlineData("proposal")]
    [InlineData("history")]
    public async Task G1_ref_and_history_drift_cannot_become_absence_or_ownership(string drift)
    {
        var remote = drift == "coordination" ? new G1Remote() : G1Remote.Published("draft");
        if (drift == "history") remote.Coordination.RemoveCommit(PublishedChain(Authority())[0]);
        else
        {
            var reads = 0;
            remote.BeforeRead = path =>
            {
                if (path.Contains("/git/ref/heads/contract-scribe/" + drift + "/", StringComparison.Ordinal) && ++reads == 2)
                {
                    if (drift == "coordination") remote.Coordination.Seed(GitHubCoordinationCodec.CreateClaim(Authority(), Oid('0')));
                    else remote.Refs[GitHubPublicationFactory.CreateProposalRef(Authority())] = Oid('9');
                }
            };
            // The wire namespace is plural for proposals.
            if (drift == "proposal") remote.BeforeRead = path =>
            {
                if (path.Contains("/git/ref/heads/contract-scribe/proposals/", StringComparison.Ordinal) && ++reads == 2)
                    remote.Refs[GitHubPublicationFactory.CreateProposalRef(Authority())] = Oid('9');
            };
        }
        using var observer = remote.Observer();
        var result = await observer.ObserveAsync();
        Assert.Equal(GitHubCampaignKind.Unverifiable, result.Kind);
        Assert.NotNull(result.Failure);
        Assert.Equal(drift == "history" ? GitHubCampaignFailureKind.Transport : GitHubCampaignFailureKind.RefChanged, result.Failure.Kind);
        remote.AssertReadOnly();
    }

    [Fact]
    public async Task G1_old_terminal_PR_does_not_turn_a_new_unpublished_generation_into_terminal_work()
    {
        var first = Authority();
        var chain = PublishedChain(first);
        var terminal = G1Terminal(chain[^1], GitHubCoordinationStage.Merged);
        var next = SuccessorAuthority(first, terminal, GitHubCoordinationStage.Merged);
        var claim = GitHubCoordinationCodec.CreateClaim(next, PrepareFixture(terminal).CommitOid);
        var remote = new G1Remote();
        remote.Coordination.TargetHead = Oid('9');
        remote.Coordination.SeedChain([.. chain, terminal, claim]);
        remote.AddPr(first, chain[^1], "merged");
        using var observer = remote.Observer();
        var result = await observer.ObserveAsync();
        Assert.Null(result.Failure);
        Assert.Equal(GitHubCampaignKind.Unpublished, result.Kind);
        Assert.Equal("generation-2", result.Ledger!.GenerationId);
        Assert.Equal(GitHubCampaignPullRequestKind.Merged, Assert.Single(result.PullRequests).Kind);
        remote.AssertReadOnly();
    }

    private sealed class G1Remote : HttpMessageHandler
    {
        internal CoordinationRemote Coordination { get; } = new();
        internal List<JsonObject> Prs { get; } = [];
        internal Dictionary<string, string> Refs { get; } = new(StringComparer.Ordinal);
        internal List<string> Requests { get; } = [];
        internal bool Paginate, FailLastPage;
        internal string? RejectPath;
        internal HttpStatusCode RejectStatus = HttpStatusCode.NotFound;
        internal Action<string>? BeforeRead;
        internal Action? AfterDetail;

        internal static G1Remote Published(string lifecycle, bool seed = true)
        {
            var remote = new G1Remote();
            var authority = Authority();
            var chain = PublishedChain(authority);
            if (seed)
            {
                remote.Coordination.SeedChain(chain);
                if (lifecycle is "merged" or "closed") remote.Coordination.Seed(G1Terminal(chain[^1],
                    lifecycle == "merged" ? GitHubCoordinationStage.Merged : GitHubCoordinationStage.ClosedUnmerged));
            }
            remote.AddPr(authority, chain[^1], lifecycle);
            return remote;
        }

        internal void AddPr(ValidatedGitHubPublicationAuthority authority, GitHubCoordinationState state, string lifecycle)
        {
            var read = GitHubPublicationFactory.CreateCampaignReadAuthority(authority);
            var reference = GitHubPublicationFactory.CreateProposalRef(authority);
            var metadata = GitHubProposalPullRequestStore.CreateMetadata(read, reference, state.SnapshotCommitmentSha256,
                state.BatchIdentity, state.TargetRef, state.OperationCommitmentSha256,
                state.PullRequestCreationOperationCommitmentSha256!);
            var publisher = GitHubPublicationPrincipal.ActionsBot;
            var repository = JsonSerializer.SerializeToNode(new
            {
                id = 42,
                node_id = "R_42",
                name = "repo",
                full_name = "Owner/repo",
                @private = true,
                archived = false,
                disabled = false,
                owner = new { id = 7, node_id = "U_7", login = "Owner", type = "User" }
            });
            var number = state.PullRequestNumber!.Value;
            var pr = (JsonObject)JsonSerializer.SerializeToNode(new
            {
                id = 1000 + number,
                node_id = "PR_" + number,
                number,
                state = "open",
                draft = true,
                merged = false,
                merged_at = (string?)null,
                closed_at = (string?)null,
                created_at = "2026-01-01T00:00:00Z",
                title = metadata.Title,
                body = metadata.Body,
                maintainer_can_modify = false,
                user = new { id = publisher.Id, node_id = publisher.NodeId, login = publisher.Login, type = "Bot" },
                head = new { repo = repository, @ref = reference[11..], sha = state.ProposalCommitOid },
                @base = new { repo = repository, @ref = "main", sha = state.ObservedBaseOid },
            })!;
            Lifecycle(pr, lifecycle);
            Prs.Add(pr);
            Refs[reference] = state.ProposalCommitOid!;
        }

        internal static void Lifecycle(JsonObject pr, string lifecycle)
        {
            var closed = lifecycle is "closed" or "merged";
            pr["state"] = closed ? "closed" : "open";
            pr["draft"] = lifecycle == "draft";
            pr["merged"] = lifecycle == "merged";
            pr["closed_at"] = closed ? "2026-01-02T00:00:00Z" : null;
            pr["merged_at"] = lifecycle == "merged" ? "2026-01-02T00:00:00Z" : null;
        }

        internal GitHubCampaignObserver Observer(ValidatedGitHubCampaignReadAuthority? authority = null)
        {
            using var registration = (IDisposable)typeof(GitHubTransportTestHook).GetMethod("Register", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [Origin, this, 30_000])!;
            return GitHubCampaignObserver.Create(authority ?? GitHubPublicationFactory.CreateCampaignReadAuthority(
                "Owner", "repo", "refs/heads/main", "campaign-1"), GitHubTransportTestHook.Placeholder);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Post)
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Assert.Equal("/graphql", request.RequestUri!.AbsolutePath);
                Assert.StartsWith("query", body.RootElement.GetProperty("query").GetString()!.TrimStart());
                Requests.Add(request.RequestUri.PathAndQuery);
                token.ThrowIfCancellationRequested();
                return await Coordination.Reply(request);
            }
            Assert.Equal(HttpMethod.Get, request.Method);
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(request.RequestUri.PathAndQuery);
            BeforeRead?.Invoke(path);
            if (path == "/user" || RejectPath is { } rejected && path.EndsWith(rejected, StringComparison.Ordinal))
                return Reply(RejectStatus, new { message = "synthetic unavailable route" });
            if (path.EndsWith("/pulls", StringComparison.Ordinal))
            {
                var page2 = request.RequestUri.Query.Contains("page=2", StringComparison.Ordinal);
                if (page2 && FailLastPage) return Reply(HttpStatusCode.ServiceUnavailable, new { message = "synthetic" });
                var response = Reply(HttpStatusCode.OK, Paginate && Prs.Count > 1 ? (page2 ? Prs.Skip(1) : Prs.Take(1)).ToArray() : Prs.ToArray());
                if (Paginate && Prs.Count > 1 && !page2)
                    response.Headers.TryAddWithoutValidation("Link", "<" + Origin + "repos/Owner/repo/pulls?state=all&sort=created&direction=asc&per_page=100&page=2>; rel=\"next\"");
                return response;
            }
            if (path.Contains("/pulls/", StringComparison.Ordinal))
            {
                var number = int.Parse(path.Split('/')[^1], System.Globalization.CultureInfo.InvariantCulture);
                var pr = Prs.SingleOrDefault(pr => pr["number"]!.GetValue<int>() == number);
                var response = pr is null ? Reply(HttpStatusCode.NotFound, new { message = "missing" }) : Reply(HttpStatusCode.OK, pr);
                AfterDetail?.Invoke(); AfterDetail = null;
                return response;
            }
            if (path.Contains("/git/ref/heads/contract-scribe/proposals/", StringComparison.Ordinal))
            {
                var reference = "refs/" + Uri.UnescapeDataString(path[(path.IndexOf("/git/ref/", StringComparison.Ordinal) + 9)..]);
                return Refs.TryGetValue(reference, out var oid) ? Reply(HttpStatusCode.OK,
                    new { @ref = reference, node_id = "REF_" + reference.Split('/')[^1], @object = new { type = "commit", sha = oid } })
                    : Reply(HttpStatusCode.NotFound, new { message = "missing" });
            }
            token.ThrowIfCancellationRequested();
            return await Coordination.Reply(request);
        }

        private static HttpResponseMessage Reply(HttpStatusCode status, object value) => new(status)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

        internal void AssertReadOnly()
        {
            Assert.DoesNotContain("/user", Requests);
            Assert.Equal(0, Coordination.ObjectMutationAttempts);
            Assert.Equal(0, Coordination.RefMutationAttempts);
        }
    }

    [Fact]
    public void R1_and_R3_coordination_known_answers_round_trip_through_the_production_codec()
    {
        foreach (var path in new[] { Fixture(), StageFixture() })
        {
            using var fixture = JsonDocument.Parse(File.ReadAllBytes(path));
            foreach (var vector in fixture.RootElement.GetProperty("vectors").EnumerateArray())
            {
                var bytes = Convert.FromBase64String(
                    vector.GetProperty("canonicalStateUtf8Base64").GetString()!);
                var state = GitHubCoordinationCodec.Decode(bytes);
                var prepared = PrepareVector(vector);

                Assert.Equal(bytes, GitHubCoordinationCodec.Encode(state));
                Assert.Equal(vector.GetProperty("blobOid").GetString(), prepared.BlobOid);
                Assert.Equal(vector.GetProperty("leafTreeOid").GetString(), prepared.LeafTreeOid);
                Assert.Equal(vector.GetProperty("rootTreeOid").GetString(), prepared.RootTreeOid);
                Assert.Equal(vector.GetProperty("commitParentOid").GetString(), prepared.ParentOid);
                Assert.Equal(vector.GetProperty("commitOid").GetString(), prepared.CommitOid);
            }
        }
    }

    [Fact]
    public void Literal_R3_stage_vectors_form_only_legal_immediate_edges()
    {
        using var r1 = JsonDocument.Parse(File.ReadAllBytes(Fixture()));
        using var r3 = JsonDocument.Parse(File.ReadAllBytes(StageFixture()));
        var r1Commits = r1.RootElement.GetProperty("vectors").EnumerateArray()
            .ToDictionary(vector => vector.GetProperty("name").GetString()!,
                vector => vector.GetProperty("commitOid").GetString()!, StringComparer.Ordinal);
        var r3Vectors = r3.RootElement.GetProperty("vectors").EnumerateArray()
            .ToDictionary(vector => vector.GetProperty("name").GetString()!,
                vector => vector, StringComparer.Ordinal);

        Assert.Equal(r1Commits["pr-send-fence"],
            r3Vectors["pr-created"].GetProperty("commitParentOid").GetString());
        Assert.Equal(r1Commits["completed-publication"],
            r3Vectors["awaiting-review"].GetProperty("commitParentOid").GetString());
        var awaiting = r3Vectors["awaiting-review"].GetProperty("commitOid").GetString();
        Assert.Equal(awaiting,
            r3Vectors["merged"].GetProperty("commitParentOid").GetString());
        Assert.Equal(awaiting,
            r3Vectors["closed-unmerged"].GetProperty("commitParentOid").GetString());

        var validEdge = typeof(GitHubCoordinationStore).GetMethod(
            "ValidEdge", BindingFlags.Static | BindingFlags.NonPublic)!;
        GitHubCoordinationState Decode(JsonElement vector) => GitHubCoordinationCodec.Decode(
            Convert.FromBase64String(vector.GetProperty("canonicalStateUtf8Base64").GetString()!));
        var r1Vectors = r1.RootElement.GetProperty("vectors").EnumerateArray()
            .ToDictionary(vector => vector.GetProperty("name").GetString()!,
                vector => vector, StringComparer.Ordinal);
        var proposal = Decode(r1Vectors["pr-send-fence"]);
        var published = Decode(r1Vectors["completed-publication"]);
        var prCreated = Decode(r3Vectors["pr-created"]);
        var awaitingReview = Decode(r3Vectors["awaiting-review"]);
        Assert.True((bool)validEdge.Invoke(null, [proposal, prCreated])!);
        Assert.True((bool)validEdge.Invoke(null, [published, awaitingReview])!);
        Assert.True((bool)validEdge.Invoke(null, [awaitingReview, Decode(r3Vectors["merged"])])!);
        Assert.True((bool)validEdge.Invoke(null, [awaitingReview, Decode(r3Vectors["closed-unmerged"])])!);
    }

    [Fact]
    public void Codec_rejects_noncanonical_bytes_before_they_can_become_authority()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Fixture()));
        var canonical = Convert.FromBase64String(fixture.RootElement.GetProperty("vectors")[0]
            .GetProperty("canonicalStateUtf8Base64").GetString()!);
        var text = Encoding.UTF8.GetString(canonical);
        var reordered = Encoding.UTF8.GetBytes(text
            .Replace("{\"version\":1,\"campaignLineage\":\"campaign-1\"", "{\"campaignLineage\":\"campaign-1\",\"version\":1", StringComparison.Ordinal));
        var spaced = Encoding.UTF8.GetBytes(text[..^1] + " \n");
        var noLf = canonical[..^1];
        var bom = Encoding.UTF8.GetPreamble().Concat(canonical).ToArray();

        Assert.Throws<GitHubCoordinationException>(() => GitHubCoordinationCodec.Decode(reordered));
        Assert.Throws<GitHubCoordinationException>(() => GitHubCoordinationCodec.Decode(spaced));
        Assert.Throws<GitHubCoordinationException>(() => GitHubCoordinationCodec.Decode(noLf));
        Assert.Throws<GitHubCoordinationException>(() => GitHubCoordinationCodec.Decode(bom));
    }

    [Fact]
    public void Stage_graph_is_closed_and_keeps_stale_draft_active_until_a_terminal_observation()
    {
        var method = typeof(GitHubCoordinationStore).GetMethod(
            "AllowsStage", BindingFlags.Static | BindingFlags.NonPublic)!;
        var allowed = new HashSet<(GitHubCoordinationStage Current, GitHubCoordinationStage Next)>
        {
            (GitHubCoordinationStage.Claimed, GitHubCoordinationStage.ContentCreated),
            (GitHubCoordinationStage.Claimed, GitHubCoordinationStage.Stale),
            (GitHubCoordinationStage.ContentCreated, GitHubCoordinationStage.ProposalRefAdvanced),
            (GitHubCoordinationStage.ContentCreated, GitHubCoordinationStage.Stale),
            (GitHubCoordinationStage.ProposalRefAdvanced, GitHubCoordinationStage.PullRequestCreateStarted),
            (GitHubCoordinationStage.PullRequestCreateStarted, GitHubCoordinationStage.PullRequestCreated),
            (GitHubCoordinationStage.PullRequestCreateStarted, GitHubCoordinationStage.Published),
            (GitHubCoordinationStage.PullRequestCreateStarted, GitHubCoordinationStage.StaleDraft),
            (GitHubCoordinationStage.ProposalRefAdvanced, GitHubCoordinationStage.Stale),
            (GitHubCoordinationStage.PullRequestCreateStarted, GitHubCoordinationStage.AwaitingReview),
            (GitHubCoordinationStage.PullRequestCreateStarted, GitHubCoordinationStage.Merged),
            (GitHubCoordinationStage.PullRequestCreateStarted, GitHubCoordinationStage.ClosedUnmerged),
            (GitHubCoordinationStage.PullRequestCreated, GitHubCoordinationStage.Published),
            (GitHubCoordinationStage.PullRequestCreated, GitHubCoordinationStage.AwaitingReview),
            (GitHubCoordinationStage.PullRequestCreated, GitHubCoordinationStage.Merged),
            (GitHubCoordinationStage.PullRequestCreated, GitHubCoordinationStage.ClosedUnmerged),
            (GitHubCoordinationStage.Published, GitHubCoordinationStage.AwaitingReview),
            (GitHubCoordinationStage.Published, GitHubCoordinationStage.Merged),
            (GitHubCoordinationStage.Published, GitHubCoordinationStage.ClosedUnmerged),
            (GitHubCoordinationStage.AwaitingReview, GitHubCoordinationStage.Merged),
            (GitHubCoordinationStage.AwaitingReview, GitHubCoordinationStage.ClosedUnmerged),
            (GitHubCoordinationStage.StaleDraft, GitHubCoordinationStage.Merged),
            (GitHubCoordinationStage.StaleDraft, GitHubCoordinationStage.ClosedUnmerged),
        };
        var state = GitHubCoordinationCodec.CreateClaim(Authority(), new string('0', 40));

        foreach (var current in Enum.GetValues<GitHubCoordinationStage>())
            foreach (var next in Enum.GetValues<GitHubCoordinationStage>())
            {
                var currentState = WithStageValue(state, current);
                Assert.Equal(allowed.Contains((current, next)),
                    Assert.IsType<bool>(method.Invoke(null, [currentState, next])));
            }
    }

    [Fact]
    public void Codec_normalizes_all_malformed_numeric_and_stage_shapes_to_the_closed_failure()
    {
        var state = GitHubCoordinationCodec.CreateClaim(Authority(), new string('0', 40));
        var canonical = Encoding.UTF8.GetString(GitHubCoordinationCodec.Encode(state));
        var fractional = Encoding.UTF8.GetBytes(canonical.Replace(
            "\"cumulativePatchBytes\":12", "\"cumulativePatchBytes\":1.2", StringComparison.Ordinal));
        var excessive = Encoding.UTF8.GetBytes(canonical.Replace(
            "\"cumulativePatchBytes\":12", "\"cumulativePatchBytes\":1000000000000001", StringComparison.Ordinal));
        var incompleteProposal = Encoding.UTF8.GetBytes(canonical
            .Replace("\"stage\":\"claimed\"", "\"stage\":\"proposal-ref-advanced\"", StringComparison.Ordinal)
            .Replace("\"contentCommitOid\":null", "\"contentCommitOid\":\"" + new string('2', 40) + "\"", StringComparison.Ordinal)
            .Replace("\"proposalRefOid\":null", "\"proposalRefOid\":\"" + new string('2', 40) + "\"", StringComparison.Ordinal)
            .Replace("\"proposalCommitOid\":null", "\"proposalCommitOid\":\"" + new string('2', 40) + "\"", StringComparison.Ordinal));

        Assert.Throws<GitHubCoordinationException>(() => GitHubCoordinationCodec.Decode(fractional));
        Assert.Throws<GitHubCoordinationException>(() => GitHubCoordinationCodec.Decode(excessive));
        Assert.Throws<GitHubCoordinationException>(() => GitHubCoordinationCodec.Decode(incompleteProposal));
    }

    [Fact]
    public void Codec_accepts_exact_Core_maxima_and_rejects_every_impossible_cumulative_domain()
    {
        var claim = GitHubCoordinationCodec.CreateClaim(Authority(), new string('0', 40));
        var exact = WithCumulative(claim,
            CampaignStateContract.MaximumActivePatchBlocks,
            (long)claim.CumulativeChangedFiles.Length * int.MaxValue, claim.CumulativeChangedFiles);
        Assert.Equal(exact.CumulativePatchBytes,
            GitHubCoordinationCodec.Decode(GitHubCoordinationCodec.Encode(exact)).CumulativePatchBytes);

        var canonical = Encoding.UTF8.GetString(GitHubCoordinationCodec.Encode(claim));
        var tooManyBlocks = Encoding.UTF8.GetBytes(canonical.Replace(
            "\"cumulativeDocumentationBlocks\":1",
            "\"cumulativeDocumentationBlocks\":513", StringComparison.Ordinal));
        var tooManyBytes = Encoding.UTF8.GetBytes(canonical.Replace(
            "\"cumulativePatchBytes\":12",
            "\"cumulativePatchBytes\":1099511627777", StringComparison.Ordinal));

        var twoFiles = WithCumulative(claim, 2, 2,
            [new("docs/a.md", new string('a', 64)),
                new("docs/b.md", new string('b', 64))]);
        var twoFilesCanonical = Encoding.UTF8.GetString(GitHubCoordinationCodec.Encode(twoFiles));
        var fewerBlocksThanFiles = Encoding.UTF8.GetBytes(twoFilesCanonical.Replace(
            "\"cumulativeDocumentationBlocks\":2",
            "\"cumulativeDocumentationBlocks\":1", StringComparison.Ordinal));
        var fewerBytesThanFiles = Encoding.UTF8.GetBytes(twoFilesCanonical.Replace(
            "\"cumulativePatchBytes\":2",
            "\"cumulativePatchBytes\":1", StringComparison.Ordinal));

        Assert.Throws<GitHubCoordinationException>(() => GitHubCoordinationCodec.Decode(tooManyBlocks));
        Assert.Throws<GitHubCoordinationException>(() => GitHubCoordinationCodec.Decode(tooManyBytes));
        Assert.Throws<GitHubCoordinationException>(() => GitHubCoordinationCodec.Decode(fewerBlocksThanFiles));
        Assert.Throws<GitHubCoordinationException>(() => GitHubCoordinationCodec.Decode(fewerBytesThanFiles));
    }

    private static GitHubCoordinationState WithCumulative(
        GitHubCoordinationState source,
        int blocks,
        long bytes,
        ImmutableArray<GitHubCoordinationChangedFile> files) => new(
            source.Stage, source.RepositoryId, source.TargetRef, source.TargetCommitOid,
            source.SnapshotCommitmentSha256, source.AuthorityCommitmentSha256,
            source.BatchIdentity, source.ResultSetCommitmentSha256, source.OperationId,
            source.OperationCommitmentSha256, source.CurrentCandidateCommitmentSha256,
            source.GenerationId,
            source.Transition, source.CoordinationPredecessorOid, source.ContentCommitOid,
            source.ProposalRefOid, source.ProposalCommitOid, source.ProposalTreeOid,
            source.PullRequestCreationOperationCommitmentSha256, source.PullRequestNumber,
            source.ExpectedBaseOid, source.ObservedBaseOid, source.OwnershipMarkerSha256,
            blocks, bytes, files, source.CampaignLineage);

    private static GitHubCoordinationState WithStageValue(
        GitHubCoordinationState source,
        GitHubCoordinationStage stage) => new(
            stage, source.RepositoryId, source.TargetRef, source.TargetCommitOid,
            source.SnapshotCommitmentSha256, source.AuthorityCommitmentSha256,
            source.BatchIdentity, source.ResultSetCommitmentSha256, source.OperationId,
            source.OperationCommitmentSha256, source.CurrentCandidateCommitmentSha256,
            source.GenerationId,
            source.Transition, source.CoordinationPredecessorOid, source.ContentCommitOid,
            source.ProposalRefOid, source.ProposalCommitOid, source.ProposalTreeOid,
            source.PullRequestCreationOperationCommitmentSha256, source.PullRequestNumber,
            source.ExpectedBaseOid, source.ObservedBaseOid, source.OwnershipMarkerSha256,
            source.CumulativeDocumentationBlocks, source.CumulativePatchBytes,
            source.CumulativeChangedFiles, source.CampaignLineage);

    private static ValidatedGitHubPublicationAuthority Authority() =>
        GitHubPublicationFactory.CreateAuthority(new("Owner",
            "repo",
            "refs/heads/main",
            new string('1', 40),
            "campaign-1",
            new string('1', 64),
            new string('2', 64),
            new string('3', 64),
            7,
            new string('4', 64),
            new string('5', 64),
            new string('6', 64),
            new string('7', 64),
            new string('8', 64),
            "operation-1",
            "generation-1",
            GitHubPublicationTransitionKind.Initial,
            Batch: GitHubPublicationTestBatch.ForFiles([new("docs/readme.md", new string('a', 64), new string('5', 64), 1, 10, 12, 1, 1)]),
            [new("docs/readme.md", new string('a', 64), new string('5', 64), 1, 10, 12, 1, 1)],
            null));

    private static GitHubPreparedCoordination PrepareVector(JsonElement vector) =>
        GitHubCoordinationObjects.Prepare(
            GitHubCoordinationDocument.Decode(Convert.FromBase64String(vector.GetProperty("canonicalDocumentUtf8Base64").GetString()!)),
            GitHubPublicationEvent.Decode(Convert.FromBase64String(vector.GetProperty("publicationEventUtf8Base64").GetString()!)));

    private static string Fixture() => Path.Join(Root(), "tests", "fixtures", "github",
        "publication-contract", "coordination-representation-v1.json");

    private static string StageFixture() => Path.Join(Root(), "tests", "fixtures", "github",
        "coordination", "stage-representation-v1.json");

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "ContractScribe.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}

[Collection("GitHub transport hook")]
public sealed partial class GitHubCoordinationRefTests
{
    private static readonly Uri Origin = new("http://127.0.0.1:18766/");
    private static string Oid(char value) => new(value, 40);
    private static string Hash(char value) => new(value, 64);

    private static string GitObjectOid(string type, ReadOnlySpan<byte> bytes)
    {
        var header = Encoding.ASCII.GetBytes(type + " "
            + bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\0");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(header);
        hash.AppendData(bytes);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static byte[] TreeEntryBytes(string mode, string name, string oid)
    {
        using var stream = new MemoryStream();
        stream.Write(Encoding.UTF8.GetBytes(mode + " " + name));
        stream.WriteByte(0);
        stream.Write(Convert.FromHexString(oid));
        return stream.ToArray();
    }

    [Fact]
    public async Task First_claim_is_atomic_and_an_exact_retry_is_a_zero_write_replay()
    {
        var remote = new CoordinationRemote();
        var authority = InitialAuthority("operation-1", '5');
        using var client = Client(authority, remote);
        var store = GitHubCoordinationStore.Create(client);

        var absence = await store.ReadCurrentAsync();
        Assert.Equal(GitHubCoordinationOutcome.ExpectedAbsence, absence.Outcome);
        var admitted = await store.ClaimAsync(absence.Read!);
        Assert.Equal(GitHubCoordinationOutcome.Admitted, admitted.Outcome);
        Assert.Equal(GitHubCoordinationStage.Claimed, admitted.State!.Stage);
        Assert.Equal(Oid('0'), admitted.State.CoordinationPredecessorOid);
        Assert.Equal(1, remote.SuccessfulRefMutations);

        var current = await store.ReadCurrentAsync();
        var replay = await store.ClaimAsync(current.Read!);
        Assert.Equal(GitHubCoordinationOutcome.Replayed, replay.Outcome);
        Assert.Equal(admitted.State.HeadOid, replay.State!.HeadOid);
        Assert.Equal(1, remote.RefMutationAttempts);
        Assert.Equal(5, remote.ObjectMutationAttempts);
    }

    [Theory]
    [InlineData(LostMutation.Blob)]
    [InlineData(LostMutation.LeafTree)]
    [InlineData(LostMutation.RootTree)]
    [InlineData(LostMutation.Commit)]
    [InlineData(LostMutation.Ref)]
    public async Task Every_mutation_response_loss_recovers_only_the_exact_owned_claim(
        LostMutation lost)
    {
        var remote = new CoordinationRemote { LoseResponse = lost };
        using var client = Client(InitialAuthority("operation-loss", '5'), remote);
        var store = GitHubCoordinationStore.Create(client);

        var absence = await store.ReadCurrentAsync();
        var result = await store.ClaimAsync(absence.Read!);

        Assert.Equal(GitHubCoordinationOutcome.Admitted, result.Outcome);
        Assert.NotNull(result.State);
        Assert.Equal(result.State!.HeadOid, remote.CoordinationHead);
        Assert.Equal(1, remote.SuccessfulRefMutations);
        Assert.Equal(LostMutation.None, remote.LoseResponse);
    }

    [Theory]
    [InlineData((int)LostReadback.Missing, (int)GitHubCoordinationFailureKind.Unresolved)]
    [InlineData((int)LostReadback.Mismatch, (int)GitHubCoordinationFailureKind.ObjectMismatch)]
    public async Task Ambiguous_object_create_never_repeats_when_exact_readback_is_absent_or_different(
        int readbackValue,
        int expectedValue)
    {
        var readback = (LostReadback)readbackValue;
        var expected = (GitHubCoordinationFailureKind)expectedValue;
        var remote = new CoordinationRemote
        {
            LoseResponse = LostMutation.Blob,
            LostBlobReadback = readback,
        };
        using var client = Client(InitialAuthority("operation-loss-blocked", '5'), remote);
        var store = GitHubCoordinationStore.Create(client);
        var absence = await store.ReadCurrentAsync();

        var result = await store.ClaimAsync(absence.Read!);

        Assert.Equal(GitHubCoordinationOutcome.Failed, result.Outcome);
        Assert.Equal(expected, result.Failure!.Kind);
        Assert.Equal(GitHubFailureCode.ResponseLost, result.Failure.TransportFailure!.Code);
        Assert.Equal(GitHubDelivery.Ambiguous, result.Failure.Delivery);
        Assert.IsType<GitHubObjectContext>(result.Failure.Context);
        if (readback == LostReadback.Missing)
            Assert.Equal(GitHubFailureCode.NotFound, result.Failure.ReadbackFailure!.Code);
        Assert.Equal(1, remote.ObjectMutationAttempts);
        Assert.Equal(0, remote.RefMutationAttempts);
    }

    [Fact]
    public async Task Competing_first_claims_use_zero_predecessors_and_only_one_wins()
    {
        var remote = new CoordinationRemote();
        using var firstClient = Client(InitialAuthority("operation-a", '5'), remote);
        using var secondClient = Client(InitialAuthority("operation-b", '6'), remote);
        var first = GitHubCoordinationStore.Create(firstClient);
        var second = GitHubCoordinationStore.Create(secondClient);
        var firstRead = await first.ReadCurrentAsync();
        var secondRead = await second.ReadCurrentAsync();
        remote.RequireConcurrentRefMutations(2);

        var results = await Task.WhenAll(
            first.ClaimAsync(firstRead.Read!).AsTask(),
            second.ClaimAsync(secondRead.Read!).AsTask());

        Assert.Single(results, result => result.Outcome == GitHubCoordinationOutcome.Admitted);
        Assert.Single(results, result => result.Outcome == GitHubCoordinationOutcome.Conflict);
        Assert.Equal(2, remote.RefMutationAttempts);
        Assert.Equal(1, remote.SuccessfulRefMutations);
        Assert.All(remote.RefUpdates, update => Assert.Equal(Oid('0'), update.Before));
    }

    [Fact]
    public async Task A_stale_absence_read_recovers_an_identical_concurrent_claim_without_another_write()
    {
        var remote = new CoordinationRemote();
        var authority = InitialAuthority("operation-same", '5');
        using var firstClient = Client(authority, remote);
        using var secondClient = Client(authority, remote);
        var first = GitHubCoordinationStore.Create(firstClient);
        var second = GitHubCoordinationStore.Create(secondClient);
        var firstRead = await first.ReadCurrentAsync();
        var secondRead = await second.ReadCurrentAsync();

        var winner = await first.ClaimAsync(firstRead.Read!);
        var replay = await second.ClaimAsync(secondRead.Read!);

        Assert.Equal(GitHubCoordinationOutcome.Admitted, winner.Outcome);
        Assert.Equal(GitHubCoordinationOutcome.Replayed, replay.Outcome);
        Assert.Equal(winner.State!.HeadOid, replay.State!.HeadOid);
        Assert.Equal(1, remote.RefMutationAttempts);
        Assert.Equal(1, remote.SuccessfulRefMutations);
    }

    [Fact]
    public async Task Initial_target_drift_with_an_absent_coordination_ref_is_not_a_missing_predecessor()
    {
        var remote = new CoordinationRemote { TargetHead = Oid('9') };
        using var client = Client(InitialAuthority("operation-initial-drift", '5'), remote);

        var result = await GitHubCoordinationStore.Create(client).ReadCurrentAsync();

        Assert.Equal(GitHubCoordinationOutcome.Failed, result.Outcome);
        Assert.Equal(GitHubCoordinationFailureKind.TargetMoved, result.Failure!.Kind);
        Assert.Null(result.Read);
        Assert.Null(result.State);
        Assert.Equal(0, remote.ObjectMutationAttempts);
        Assert.Equal(0, remote.RefMutationAttempts);
    }

    [Fact]
    public async Task Initial_claim_recovers_an_identical_successor_that_wins_before_the_orphan_gate()
    {
        var remote = new CoordinationRemote();
        var authority = InitialAuthority("operation-orphan-replay", '5');
        using var client = Client(authority, remote);
        var store = GitHubCoordinationStore.Create(client);
        var absence = await store.ReadCurrentAsync();
        var prepared = PrepareFixture(
            GitHubCoordinationCodec.CreateClaim(authority, Oid('0')));
        remote.SeedObjects(prepared);
        remote.MoveCoordinationHeadBeforeCommitRead = prepared.CommitOid;

        var result = await store.ClaimAsync(absence.Read!);

        Assert.Equal(GitHubCoordinationOutcome.Replayed, result.Outcome);
        Assert.Equal(prepared.CommitOid, result.State!.HeadOid);
        Assert.Equal(0, remote.RefMutationAttempts);
        Assert.Equal(0, remote.ObjectMutationAttempts);
    }

    [Fact]
    public async Task Terminal_successor_recovers_an_identical_winner_before_the_orphan_gate()
    {
        var predecessorAuthority = InitialAuthority("predecessor", '5');
        var chain = PublishedChain(predecessorAuthority);
        var terminal = G1Terminal(chain[^1], GitHubCoordinationStage.Merged);
        var remote = new CoordinationRemote { TargetHead = Oid('9') };
        remote.SeedChain([.. chain, terminal]);
        var authority = SuccessorAuthority(predecessorAuthority, terminal, GitHubCoordinationStage.Merged);
        using var client = Client(authority, remote);
        var store = GitHubCoordinationStore.Create(client);
        var current = await store.ReadCurrentAsync();
        var prepared = PrepareFixture(GitHubCoordinationCodec.CreateClaim(authority, current.State!.HeadOid));
        remote.SeedObjects(prepared);
        remote.MoveCoordinationHeadBeforeCommitRead = prepared.CommitOid;
        var result = await store.ClaimAsync(current.Read!);
        Assert.Equal(GitHubCoordinationOutcome.Replayed, result.Outcome);
        Assert.Equal(prepared.CommitOid, result.State!.HeadOid);
        Assert.Equal(0, remote.RefMutationAttempts);
        Assert.Equal(0, remote.ObjectMutationAttempts);
    }

    [Fact]
    public async Task Initial_claim_recovers_an_identical_successor_that_wins_before_the_final_gate()
    {
        var remote = new CoordinationRemote();
        var authority = InitialAuthority("operation-final-replay", '5');
        using var client = Client(authority, remote);
        var store = GitHubCoordinationStore.Create(client);
        var absence = await store.ReadCurrentAsync();
        var prepared = PrepareFixture(
            GitHubCoordinationCodec.CreateClaim(authority, Oid('0')));
        remote.MoveCoordinationHeadBeforeRefRead = prepared.CommitOid;
        remote.MoveCoordinationHeadAfterObjectMutationCount = 4;

        var result = await store.ClaimAsync(absence.Read!);

        Assert.Equal(GitHubCoordinationOutcome.Replayed, result.Outcome);
        Assert.Equal(prepared.CommitOid, result.State!.HeadOid);
        Assert.Equal(0, remote.RefMutationAttempts);
        Assert.Equal(5, remote.ObjectMutationAttempts);
    }

    [Fact]
    public async Task Recovered_claim_uses_ordinary_stale_terminalization_when_the_target_has_moved()
    {
        var remote = new CoordinationRemote();
        var authority = InitialAuthority("operation-recovered-stale", '5');
        using var client = Client(authority, remote);
        var store = GitHubCoordinationStore.Create(client);
        var absence = await store.ReadCurrentAsync();
        var prepared = PrepareFixture(
            GitHubCoordinationCodec.CreateClaim(authority, Oid('0')));
        remote.SeedObjects(prepared);
        remote.MoveCoordinationHeadBeforeCommitRead = prepared.CommitOid;
        remote.TargetHead = Oid('9');

        var result = await store.ClaimAsync(absence.Read!);

        Assert.Equal(GitHubCoordinationOutcome.Stale, result.Outcome);
        Assert.Equal(GitHubCoordinationStage.Stale, result.State!.Stage);
        Assert.Equal(Oid('1'), result.State.ExpectedBaseOid);
        Assert.Equal(Oid('9'), result.State.ObservedBaseOid);
        Assert.Equal(1, remote.SuccessfulRefMutations);
    }

    [Fact]
    public async Task Final_gate_claim_recovery_precedes_ordinary_target_drift_terminalization()
    {
        var remote = new CoordinationRemote { TargetAfterCommitMutation = Oid('9') };
        var authority = InitialAuthority("operation-final-recovered-stale", '5');
        using var client = Client(authority, remote);
        var store = GitHubCoordinationStore.Create(client);
        var absence = await store.ReadCurrentAsync();
        var prepared = PrepareFixture(
            GitHubCoordinationCodec.CreateClaim(authority, Oid('0')));
        remote.MoveCoordinationHeadBeforeRefRead = prepared.CommitOid;
        remote.MoveCoordinationHeadAfterObjectMutationCount = 4;

        var result = await store.ClaimAsync(absence.Read!);

        Assert.Equal(GitHubCoordinationOutcome.Stale, result.Outcome);
        Assert.Equal(Oid('1'), result.State!.ExpectedBaseOid);
        Assert.Equal(Oid('9'), result.State.ObservedBaseOid);
        Assert.Equal(1, remote.SuccessfulRefMutations);
    }

    [Fact]
    public async Task Competing_replacements_of_a_sealed_generation_both_stop_before_mutation()
    {
        var remote = new CoordinationRemote();
        var predecessor = InitialAuthority("original", '5');
        remote.SeedChain(PublishedChain(predecessor));
        var head = remote.CoordinationHead;
        using var firstClient = Client(ReplacementAuthority(predecessor, "replacement-a", '6'), remote);
        using var secondClient = Client(ReplacementAuthority(predecessor, "replacement-b", '7'), remote);
        var first = GitHubCoordinationStore.Create(firstClient);
        var second = GitHubCoordinationStore.Create(secondClient);
        var a = await first.ReadCurrentAsync();
        var b = await second.ReadCurrentAsync();
        var results = await Task.WhenAll(first.ClaimAsync(a.Read!).AsTask(), second.ClaimAsync(b.Read!).AsTask());
        Assert.All(results, result => Assert.Equal(GitHubCoordinationOutcome.Conflict, result.Outcome));
        Assert.Equal(head, remote.CoordinationHead);
        Assert.Equal(0, remote.ObjectMutationAttempts);
        Assert.Equal(0, remote.RefMutationAttempts);
    }

    [Fact]
    public async Task Target_movement_after_content_creation_persists_the_exact_content_residual()
    {
        var remote = new CoordinationRemote();
        using var client = Client(InitialAuthority("operation-stale", '5'), remote);
        var store = GitHubCoordinationStore.Create(client);
        var absence = await store.ReadCurrentAsync();
        var admitted = await store.ClaimAsync(absence.Read!);
        remote.TargetHead = Oid('9');

        var stale = await store.AdvanceAsync(admitted.State!,
            GitHubCoordinationStageUpdate.ContentCreated(Oid('2')));

        Assert.Equal(GitHubCoordinationOutcome.Stale, stale.Outcome);
        Assert.Equal(GitHubCoordinationStage.Stale, stale.State!.Stage);
        var persisted = remote.State(stale.State.HeadOid);
        Assert.Equal(Oid('2'), persisted.ContentCommitOid);
        Assert.Equal(Oid('1'), persisted.ExpectedBaseOid);
        Assert.Equal(Oid('9'), persisted.ObservedBaseOid);
        Assert.Null(persisted.ProposalRefOid);
    }

    [Fact]
    public async Task Target_movement_after_proposal_ref_creation_persists_the_complete_proposal_residual()
    {
        var remote = new CoordinationRemote();
        using var client = Client(InitialAuthority("operation-proposal-stale", '5'), remote);
        var store = GitHubCoordinationStore.Create(client);
        var absence = await store.ReadCurrentAsync();
        var claim = await store.ClaimAsync(absence.Read!);
        var content = await store.AdvanceAsync(claim.State!,
            GitHubCoordinationStageUpdate.ContentCreated(Oid('2')));
        remote.TargetHead = Oid('9');

        var stale = await store.AdvanceAsync(content.State!,
            GitHubCoordinationStageUpdate.ProposalRefAdvanced(Oid('2'), Oid('3')));

        Assert.Equal(GitHubCoordinationOutcome.Stale, stale.Outcome);
        var persisted = remote.State(stale.State!.HeadOid);
        Assert.Equal(Oid('2'), persisted.ContentCommitOid);
        Assert.Equal(Oid('2'), persisted.ProposalRefOid);
        Assert.Equal(Oid('2'), persisted.ProposalCommitOid);
        Assert.Equal(Oid('3'), persisted.ProposalTreeOid);
        Assert.Equal(Oid('9'), persisted.ObservedBaseOid);
    }

    [Fact]
    public async Task Target_movement_before_the_claim_CAS_leaves_only_unreachable_objects()
    {
        var remote = new CoordinationRemote { TargetAfterCommitMutation = Oid('9') };
        using var client = Client(InitialAuthority("operation-pre-cas", '5'), remote);
        var store = GitHubCoordinationStore.Create(client);
        var absence = await store.ReadCurrentAsync();

        var result = await store.ClaimAsync(absence.Read!);

        Assert.Equal(GitHubCoordinationOutcome.Failed, result.Outcome);
        Assert.Equal(GitHubCoordinationFailureKind.TargetMoved, result.Failure!.Kind);
        Assert.Equal(5, remote.ObjectMutationAttempts);
        Assert.Equal(0, remote.RefMutationAttempts);
        Assert.Null(remote.CoordinationHead);
    }

    [Fact]
    public async Task An_existing_prepared_successor_without_the_expected_ref_is_not_resubmitted()
    {
        var remote = new CoordinationRemote();
        var authority = InitialAuthority("operation-fence", '5');
        using var client = Client(authority, remote);
        var store = GitHubCoordinationStore.Create(client);
        var absence = await store.ReadCurrentAsync();
        var expected = PrepareFixture(
            GitHubCoordinationCodec.CreateClaim(authority, Oid('0')));
        remote.SeedObjects(expected);

        var result = await store.ClaimAsync(absence.Read!);

        Assert.Equal(GitHubCoordinationOutcome.Failed, result.Outcome);
        Assert.Equal(GitHubCoordinationFailureKind.Unresolved, result.Failure!.Kind);
        Assert.Equal(0, remote.RefMutationAttempts);
        Assert.Equal(0, remote.ObjectMutationAttempts);
    }

    [Fact]
    public async Task Identical_stage_advance_already_current_at_the_initial_gate_is_a_zero_write_replay()
    {
        var remote = new CoordinationRemote();
        var authority = InitialAuthority("operation-stage-initial-replay", '5');
        using var firstClient = Client(authority, remote);
        using var secondClient = Client(authority, remote);
        var first = GitHubCoordinationStore.Create(firstClient);
        var second = GitHubCoordinationStore.Create(secondClient);
        var absence = await first.ReadCurrentAsync();
        await first.ClaimAsync(absence.Read!);
        var firstCurrent = await first.ReadCurrentAsync();
        var secondCurrent = await second.ReadCurrentAsync();
        var winner = await first.AdvanceAsync(firstCurrent.State!,
            GitHubCoordinationStageUpdate.ContentCreated(Oid('2')));
        var refMutations = remote.RefMutationAttempts;
        var objectMutations = remote.ObjectMutationAttempts;

        var replay = await second.AdvanceAsync(secondCurrent.State!,
            GitHubCoordinationStageUpdate.ContentCreated(Oid('2')));

        Assert.Equal(GitHubCoordinationOutcome.Advanced, winner.Outcome);
        Assert.Equal(GitHubCoordinationOutcome.Advanced, replay.Outcome);
        Assert.Equal(winner.State!.HeadOid, replay.State!.HeadOid);
        Assert.Equal(refMutations, remote.RefMutationAttempts);
        Assert.Equal(objectMutations, remote.ObjectMutationAttempts);
    }

    [Fact]
    public async Task Identical_stage_advance_that_wins_before_the_orphan_gate_is_a_zero_write_replay()
    {
        var remote = new CoordinationRemote();
        var authority = InitialAuthority("operation-stage-orphan-replay", '5');
        using var client = Client(authority, remote);
        var store = GitHubCoordinationStore.Create(client);
        var absence = await store.ReadCurrentAsync();
        var claim = await store.ClaimAsync(absence.Read!);
        var prepared = await PrepareContentSuccessorAsync(authority, remote.State(claim.State!.HeadOid));
        remote.SeedObjects(prepared);
        remote.MoveCoordinationHeadBeforeCommitRead = prepared.CommitOid;
        var refMutations = remote.RefMutationAttempts;
        var objectMutations = remote.ObjectMutationAttempts;

        var replay = await store.AdvanceAsync(claim.State,
            GitHubCoordinationStageUpdate.ContentCreated(Oid('2')));

        Assert.Equal(GitHubCoordinationOutcome.Advanced, replay.Outcome);
        Assert.Equal(prepared.CommitOid, replay.State!.HeadOid);
        Assert.Equal(refMutations, remote.RefMutationAttempts);
        Assert.Equal(objectMutations, remote.ObjectMutationAttempts);
    }

    [Fact]
    public async Task Identical_stage_advance_that_wins_before_the_final_gate_is_a_zero_write_replay()
    {
        var remote = new CoordinationRemote();
        var authority = InitialAuthority("operation-stage-final-replay", '5');
        using var client = Client(authority, remote);
        var store = GitHubCoordinationStore.Create(client);
        var absence = await store.ReadCurrentAsync();
        var claim = await store.ClaimAsync(absence.Read!);
        var prepared = await PrepareContentSuccessorAsync(authority, remote.State(claim.State!.HeadOid));
        remote.MoveCoordinationHeadBeforeRefRead = prepared.CommitOid;
        remote.MoveCoordinationHeadAfterObjectMutationCount = remote.ObjectMutationAttempts + 4;
        var refMutations = remote.RefMutationAttempts;

        var replay = await store.AdvanceAsync(claim.State,
            GitHubCoordinationStageUpdate.ContentCreated(Oid('2')));

        Assert.Equal(GitHubCoordinationOutcome.Advanced, replay.Outcome);
        Assert.Equal(prepared.CommitOid, replay.State!.HeadOid);
        Assert.Equal(refMutations, remote.RefMutationAttempts);
    }

    [Fact]
    public async Task A_genuinely_different_stage_successor_remains_a_conflict()
    {
        var remote = new CoordinationRemote();
        var authority = InitialAuthority("operation-stage-conflict", '5');
        using var firstClient = Client(authority, remote);
        using var secondClient = Client(authority, remote);
        var first = GitHubCoordinationStore.Create(firstClient);
        var second = GitHubCoordinationStore.Create(secondClient);
        var absence = await first.ReadCurrentAsync();
        await first.ClaimAsync(absence.Read!);
        var firstCurrent = await first.ReadCurrentAsync();
        var secondCurrent = await second.ReadCurrentAsync();
        await first.AdvanceAsync(firstCurrent.State!,
            GitHubCoordinationStageUpdate.ContentCreated(Oid('2')));

        var conflict = await second.AdvanceAsync(secondCurrent.State!,
            GitHubCoordinationStageUpdate.ContentCreated(Oid('3')));

        Assert.Equal(GitHubCoordinationOutcome.Conflict, conflict.Outcome);
        Assert.Equal(GitHubCoordinationFailureKind.Conflict, conflict.Failure!.Kind);
    }

    [Fact]
    public async Task Recovered_stage_successor_uses_ordinary_stale_terminalization_after_target_drift()
    {
        var remote = new CoordinationRemote();
        var authority = InitialAuthority("operation-stage-recovered-stale", '5');
        using var firstClient = Client(authority, remote);
        using var secondClient = Client(authority, remote);
        var first = GitHubCoordinationStore.Create(firstClient);
        var second = GitHubCoordinationStore.Create(secondClient);
        var absence = await first.ReadCurrentAsync();
        await first.ClaimAsync(absence.Read!);
        var firstCurrent = await first.ReadCurrentAsync();
        var secondCurrent = await second.ReadCurrentAsync();
        await first.AdvanceAsync(firstCurrent.State!,
            GitHubCoordinationStageUpdate.ContentCreated(Oid('2')));
        remote.TargetHead = Oid('9');

        var result = await second.AdvanceAsync(secondCurrent.State!,
            GitHubCoordinationStageUpdate.ContentCreated(Oid('2')));

        Assert.Equal(GitHubCoordinationOutcome.Stale, result.Outcome);
        Assert.Equal(Oid('1'), result.State!.ExpectedBaseOid);
        Assert.Equal(Oid('9'), result.State.ObservedBaseOid);
        Assert.Equal(3, remote.SuccessfulRefMutations);
    }

    [Theory]
    [InlineData(false, SuccessorObservationWindow.Orphan)]
    [InlineData(false, SuccessorObservationWindow.FinalRef)]
    [InlineData(false, SuccessorObservationWindow.CasReadback)]
    [InlineData(true, SuccessorObservationWindow.Orphan)]
    [InlineData(true, SuccessorObservationWindow.FinalRef)]
    [InlineData(true, SuccessorObservationWindow.CasReadback)]
    public async Task Target_drift_recovers_the_exact_intended_successor_at_every_late_gate(
        bool proposal,
        SuccessorObservationWindow window)
    {
        var remote = new CoordinationRemote();
        var authority = InitialAuthority(
            $"operation-cross-product-{proposal}-{window}", '5');
        using var client = Client(authority, remote);
        var store = GitHubCoordinationStore.Create(client);
        var absence = await store.ReadCurrentAsync();
        var claim = await store.ClaimAsync(absence.Read!);
        var predecessor = claim.State!;
        var operationStates = new List<GitHubCoordinationState>
        {
            remote.State(predecessor.HeadOid),
        };
        if (proposal)
        {
            var content = await store.AdvanceAsync(predecessor,
                GitHubCoordinationStageUpdate.ContentCreated(Oid('2')));
            predecessor = content.State!;
            operationStates.Add(remote.State(predecessor.HeadOid));
        }
        var update = proposal
            ? GitHubCoordinationStageUpdate.ProposalRefAdvanced(Oid('2'), Oid('3'))
            : GitHubCoordinationStageUpdate.ContentCreated(Oid('2'));
        var intended = await PrepareSuccessorAsync(authority, operationStates, update, '1');
        var stale = await PrepareSuccessorAsync(authority, operationStates, update, '9');
        switch (window)
        {
            case SuccessorObservationWindow.Orphan:
                remote.TargetHead = Oid('9');
                remote.SeedObjects(intended);
                remote.SeedObjects(stale);
                remote.MoveCoordinationHeadBeforeCommitRead = stale.CommitOid;
                remote.MoveCoordinationHeadBeforeCommitReadTo = intended.CommitOid;
                break;
            case SuccessorObservationWindow.FinalRef:
                remote.TargetAfterCommitMutation = Oid('9');
                remote.MoveCoordinationHeadBeforeRefRead = intended.CommitOid;
                remote.MoveCoordinationHeadAfterObjectMutationCount =
                    remote.ObjectMutationAttempts + 4;
                break;
            case SuccessorObservationWindow.CasReadback:
                remote.TargetAfterCommitMutation = Oid('9');
                remote.MoveCoordinationHeadBeforeRefMutation = intended.CommitOid;
                break;
        }

        var result = await store.AdvanceAsync(predecessor, update);

        Assert.Equal(GitHubCoordinationOutcome.Stale, result.Outcome);
        Assert.Equal(GitHubCoordinationStage.Stale, result.State!.Stage);
        Assert.Equal(Oid('1'), result.State.ExpectedBaseOid);
        Assert.Equal(Oid('9'), result.State.ObservedBaseOid);
        Assert.Equal(Oid('2'), result.State.ContentCommitOid);
        Assert.Equal(proposal ? Oid('2') : null, result.State.ProposalRefOid);
        Assert.Equal(proposal ? Oid('2') : null, result.State.ProposalCommitOid);
        Assert.Equal(proposal ? Oid('3') : null, result.State.ProposalTreeOid);
        Assert.Equal(result.State.HeadOid, remote.CoordinationHead);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task Target_drift_with_any_preexisting_exact_candidate_fails_closed_at_the_predecessor_ref(
        bool proposal,
        bool seedIntended,
        bool seedStale)
    {
        var remote = new CoordinationRemote();
        var authority = InitialAuthority($"operation-alternate-orphan-{proposal}", '5');
        using var client = Client(authority, remote);
        var store = GitHubCoordinationStore.Create(client);
        var absence = await store.ReadCurrentAsync();
        var claim = await store.ClaimAsync(absence.Read!);
        var predecessor = claim.State!;
        var operationStates = new List<GitHubCoordinationState>
        {
            remote.State(predecessor.HeadOid),
        };
        if (proposal)
        {
            var content = await store.AdvanceAsync(predecessor,
                GitHubCoordinationStageUpdate.ContentCreated(Oid('2')));
            predecessor = content.State!;
            operationStates.Add(remote.State(predecessor.HeadOid));
        }
        var update = proposal
            ? GitHubCoordinationStageUpdate.ProposalRefAdvanced(Oid('2'), Oid('3'))
            : GitHubCoordinationStageUpdate.ContentCreated(Oid('2'));
        var intended = await PrepareSuccessorAsync(authority, operationStates, update, '1');
        var stale = await PrepareSuccessorAsync(authority, operationStates, update, '9');
        if (seedIntended) remote.SeedObjects(intended);
        if (seedStale) remote.SeedObjects(stale);
        remote.TargetHead = Oid('9');
        var objectMutations = remote.ObjectMutationAttempts;
        var refMutations = remote.RefMutationAttempts;

        var result = await store.AdvanceAsync(predecessor, update);

        Assert.Equal(GitHubCoordinationOutcome.Failed, result.Outcome);
        Assert.Equal(GitHubCoordinationFailureKind.Unresolved, result.Failure!.Kind);
        Assert.Null(result.State);
        Assert.Null(result.Guard);
        Assert.Equal(objectMutations, remote.ObjectMutationAttempts);
        Assert.Equal(refMutations, remote.RefMutationAttempts);
        Assert.Equal(predecessor.HeadOid, remote.CoordinationHead);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Target_drift_replays_the_exact_stale_successor_at_the_initial_gate_without_writes(
        bool proposal)
    {
        var remote = new CoordinationRemote();
        var authority = InitialAuthority($"operation-stale-replay-{proposal}", '5');
        using var client = Client(authority, remote);
        var store = GitHubCoordinationStore.Create(client);
        var absence = await store.ReadCurrentAsync();
        var claim = await store.ClaimAsync(absence.Read!);
        var predecessor = claim.State!;
        var operationStates = new List<GitHubCoordinationState>
        {
            remote.State(predecessor.HeadOid),
        };
        if (proposal)
        {
            var content = await store.AdvanceAsync(predecessor,
                GitHubCoordinationStageUpdate.ContentCreated(Oid('2')));
            predecessor = content.State!;
            operationStates.Add(remote.State(predecessor.HeadOid));
        }
        var update = proposal
            ? GitHubCoordinationStageUpdate.ProposalRefAdvanced(Oid('2'), Oid('3'))
            : GitHubCoordinationStageUpdate.ContentCreated(Oid('2'));
        var stale = await PrepareSuccessorAsync(authority, operationStates, update, '9');
        remote.SeedObjects(stale);
        remote.TargetHead = Oid('9');
        remote.ForceCoordinationHead(stale.CommitOid);
        var objectMutations = remote.ObjectMutationAttempts;
        var refMutations = remote.RefMutationAttempts;

        var result = await store.AdvanceAsync(predecessor, update);

        Assert.Equal(GitHubCoordinationOutcome.Stale, result.Outcome);
        Assert.Equal(stale.CommitOid, result.State!.HeadOid);
        Assert.Equal(objectMutations, remote.ObjectMutationAttempts);
        Assert.Equal(refMutations, remote.RefMutationAttempts);
        Assert.Equal(stale.CommitOid, remote.CoordinationHead);
    }

    [Fact]
    public async Task Target_movement_immediately_after_the_claim_CAS_is_terminalized_by_a_second_exact_CAS()
    {
        var remote = new CoordinationRemote { TargetAfterSuccessfulRef = Oid('9') };
        using var client = Client(InitialAuthority("operation-post-cas", '5'), remote);
        var store = GitHubCoordinationStore.Create(client);
        var absence = await store.ReadCurrentAsync();

        var result = await store.ClaimAsync(absence.Read!);

        Assert.Equal(GitHubCoordinationOutcome.Stale, result.Outcome);
        Assert.Equal(GitHubCoordinationStage.Stale, result.State!.Stage);
        var state = remote.State(result.State.HeadOid);
        Assert.Null(state.ContentCommitOid);
        Assert.Equal(Oid('1'), state.ExpectedBaseOid);
        Assert.Equal(Oid('9'), state.ObservedBaseOid);
        Assert.Equal(2, remote.SuccessfulRefMutations);
        Assert.Equal(Oid('0'), remote.RefUpdates[0].Before);
        Assert.Equal(remote.RefUpdates[0].After, remote.RefUpdates[1].Before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Target_movement_immediately_after_a_stage_CAS_preserves_the_residual_and_terminalizes(
        bool proposal)
    {
        var remote = new CoordinationRemote();
        var authority = InitialAuthority($"operation-stage-post-cas-{proposal}", '5');
        using var client = Client(authority, remote);
        var store = GitHubCoordinationStore.Create(client);
        var absence = await store.ReadCurrentAsync();
        var claim = await store.ClaimAsync(absence.Read!);
        var predecessor = claim.State!;
        if (proposal)
        {
            var content = await store.AdvanceAsync(predecessor,
                GitHubCoordinationStageUpdate.ContentCreated(Oid('2')));
            predecessor = content.State!;
        }
        var update = proposal
            ? GitHubCoordinationStageUpdate.ProposalRefAdvanced(Oid('2'), Oid('3'))
            : GitHubCoordinationStageUpdate.ContentCreated(Oid('2'));
        var successfulRefMutations = remote.SuccessfulRefMutations;
        remote.TargetAfterSuccessfulRef = Oid('9');

        var result = await store.AdvanceAsync(predecessor, update);

        Assert.Equal(GitHubCoordinationOutcome.Stale, result.Outcome);
        Assert.Equal(GitHubCoordinationStage.Stale, result.State!.Stage);
        Assert.Equal(Oid('1'), result.State.ExpectedBaseOid);
        Assert.Equal(Oid('9'), result.State.ObservedBaseOid);
        Assert.Equal(Oid('2'), result.State.ContentCommitOid);
        Assert.Equal(proposal ? Oid('2') : null, result.State.ProposalRefOid);
        Assert.Equal(proposal ? Oid('2') : null, result.State.ProposalCommitOid);
        Assert.Equal(proposal ? Oid('3') : null, result.State.ProposalTreeOid);
        Assert.Equal(successfulRefMutations + 2, remote.SuccessfulRefMutations);
        Assert.Equal(remote.RefUpdates[^2].After, remote.RefUpdates[^1].Before);
        Assert.Equal(result.State.HeadOid, remote.CoordinationHead);
    }

    [Fact]
    public async Task Exact_stale_state_can_be_replaced_only_by_a_fresh_initial_generation()
    {
        var remote = new CoordinationRemote();
        using (var firstClient = Client(InitialAuthority("operation-stale-old", '5'), remote))
        {
            var first = GitHubCoordinationStore.Create(firstClient);
            var absence = await first.ReadCurrentAsync();
            var claim = await first.ClaimAsync(absence.Read!);
            remote.TargetHead = Oid('9');
            var stale = await first.AdvanceAsync(claim.State!,
                GitHubCoordinationStageUpdate.ContentCreated(Oid('2')));
            Assert.Equal(GitHubCoordinationOutcome.Stale, stale.Outcome);
        }
        var staleHead = remote.CoordinationHead;
        remote.TargetHead = Oid('8');
        var freshAuthority = InitialAuthority(
            "operation-stale-fresh", '6', "generation-2", '8');
        string freshHead;
        using (var freshClient = Client(freshAuthority, remote))
        {
            var fresh = GitHubCoordinationStore.Create(freshClient);
            var current = await fresh.ReadCurrentAsync();

            var result = await fresh.ClaimAsync(current.Read!);

            Assert.Equal(GitHubCoordinationOutcome.Admitted, result.Outcome);
            Assert.Equal(staleHead, result.State!.CoordinationPredecessorOid);
            Assert.Equal("operation-stale-fresh", result.State.OperationId);
            Assert.Equal(Oid('8'), result.State.TargetCommitOid);
            var persisted = remote.State(result.State.HeadOid);
            Assert.Null(persisted.ContentCommitOid);
            Assert.Null(persisted.ProposalRefOid);
            freshHead = result.State.HeadOid;
        }
        using var restartClient = Client(freshAuthority, remote);

        var restarted = await GitHubCoordinationStore.Create(restartClient).ReadCurrentAsync();

        Assert.Equal(GitHubCoordinationOutcome.Current, restarted.Outcome);
        Assert.Equal(freshHead, restarted.State!.HeadOid);
    }

    [Fact]
    public async Task Renewed_initial_claim_uses_ordinary_stale_terminalization_if_target_moves_again()
    {
        var remote = new CoordinationRemote();
        using (var firstClient = Client(InitialAuthority("operation-stale-twice-old", '5'), remote))
        {
            var first = GitHubCoordinationStore.Create(firstClient);
            var absence = await first.ReadCurrentAsync();
            var claim = await first.ClaimAsync(absence.Read!);
            remote.TargetHead = Oid('9');
            var stale = await first.AdvanceAsync(claim.State!,
                GitHubCoordinationStageUpdate.ContentCreated(Oid('2')));
            Assert.Equal(GitHubCoordinationOutcome.Stale, stale.Outcome);
        }
        remote.TargetHead = Oid('8');
        var freshAuthority = InitialAuthority(
            "operation-stale-twice-fresh", '6', "generation-2", '8');
        string terminalHead;
        using (var freshClient = Client(freshAuthority, remote))
        {
            var fresh = GitHubCoordinationStore.Create(freshClient);
            var current = await fresh.ReadCurrentAsync();
            remote.TargetAfterSuccessfulRef = Oid('7');

            var result = await fresh.ClaimAsync(current.Read!);

            Assert.Equal(GitHubCoordinationOutcome.Stale, result.Outcome);
            Assert.Equal(Oid('8'), result.State!.ExpectedBaseOid);
            Assert.Equal(Oid('7'), result.State.ObservedBaseOid);
            var persisted = remote.State(result.State.HeadOid);
            Assert.Null(persisted.ContentCommitOid);
            Assert.Null(persisted.ProposalRefOid);
            terminalHead = result.State.HeadOid;
        }
        using var restartClient = Client(freshAuthority, remote);

        var restarted = await GitHubCoordinationStore.Create(restartClient).ReadCurrentAsync();

        Assert.Equal(GitHubCoordinationOutcome.Current, restarted.Outcome);
        Assert.Equal(terminalHead, restarted.State!.HeadOid);
    }

    [Fact]
    public async Task Stale_draft_is_blocking_and_cannot_use_the_fresh_initial_continuation()
    {
        var predecessor = InitialAuthority("operation-stale-draft", '5');
        var chain = PublishedChain(predecessor);
        var proposal = chain[3];
        var staleDraft = GitHubCoordinationCodec.WithStage(proposal,
            GitHubCoordinationStage.StaleDraft,
            PrepareFixture(proposal).CommitOid, proposal.ContentCommitOid,
            proposal.ProposalRefOid, proposal.ProposalCommitOid, proposal.ProposalTreeOid,
            Published(predecessor).PullRequestCreationOperationCommitmentSha256, 17,
            Oid('1'), Oid('9'), Published(predecessor).OwnershipMarkerSha256);
        var remote = new CoordinationRemote { TargetHead = Oid('9') };
        remote.SeedChain([.. chain[..4], staleDraft]);
        using var client = Client(
            InitialAuthority("operation-after-stale-draft", '6', "generation-2", '9'), remote);
        var store = GitHubCoordinationStore.Create(client);
        var current = await store.ReadCurrentAsync();

        var result = await store.ClaimAsync(current.Read!);

        Assert.Equal(GitHubCoordinationOutcome.Conflict, result.Outcome);
        Assert.Equal(GitHubCoordinationFailureKind.StageConflict, result.Failure!.Kind);
        Assert.Equal(0, remote.ObjectMutationAttempts);
        Assert.Equal(0, remote.RefMutationAttempts);
    }

    [Fact]
    public async Task Capabilities_are_store_owned_and_published_state_cannot_authorize_a_later_create()
    {
        var remote = new CoordinationRemote();
        var authority = InitialAuthority("operation-capability", '5');
        using var firstClient = Client(authority, remote);
        using var secondClient = Client(authority, remote);
        var first = GitHubCoordinationStore.Create(firstClient);
        var second = GitHubCoordinationStore.Create(secondClient);
        var absence = await first.ReadCurrentAsync();

        var foreign = await second.ClaimAsync(absence.Read!);
        Assert.Equal(GitHubCoordinationFailureKind.InvalidInput, foreign.Failure!.Kind);
        Assert.Equal(0, remote.ObjectMutationAttempts);
        Assert.Equal(0, remote.RefMutationAttempts);

        remote.SeedChain(PublishedChain(authority));
        var current = await first.ReadCurrentAsync();
        var forbidden = await first.ReadClaimAsync(current.State!);
        Assert.Equal(GitHubCoordinationFailureKind.InvalidInput, forbidden.Failure!.Kind);
    }

    [Fact]
    public async Task State_snapshot_is_complete_and_only_the_issuing_store_can_validate_a_guard()
    {
        var authority = InitialAuthority("operation-snapshot", '5');
        var publishedRemote = new CoordinationRemote();
        publishedRemote.SeedChain(PublishedChain(authority));
        using var publishedClient = Client(authority, publishedRemote);
        var snapshot = (await GitHubCoordinationStore.Create(publishedClient)
            .ReadCurrentAsync()).State!;

        Assert.Equal("Owner", snapshot.Repository.Owner);
        Assert.Equal("repo", snapshot.Repository.Name);
        Assert.Equal(authority.TargetRef, snapshot.TargetRef);
        Assert.Equal(authority.ExpectedBaseCommitOid, snapshot.TargetCommitOid);
        Assert.Equal(authority.SnapshotCommitmentSha256, snapshot.SnapshotCommitmentSha256);
        Assert.Equal(authority.AuthorityCommitmentSha256, snapshot.AuthorityCommitmentSha256);
        Assert.Equal(authority.Batch.Identity, snapshot.BatchIdentity);
        Assert.Equal(authority.CandidateCommitmentSha256, snapshot.CurrentCandidateCommitmentSha256);
        Assert.Equal(authority.GenerationId, snapshot.GenerationId);
        Assert.Equal("initial", snapshot.Transition);
        Assert.Equal(Oid('2'), snapshot.ContentCommitOid);
        Assert.Equal(Oid('2'), snapshot.ProposalRefOid);
        Assert.Equal(Oid('2'), snapshot.ProposalCommitOid);
        Assert.Equal(Oid('3'), snapshot.ProposalTreeOid);
        Assert.Equal(17, snapshot.PullRequestNumber);
        Assert.Equal(Oid('1'), snapshot.ExpectedBaseOid);
        Assert.Equal(Oid('1'), snapshot.ObservedBaseOid);
        Assert.NotNull(snapshot.PullRequestCreationOperationCommitmentSha256);
        Assert.NotNull(snapshot.OwnershipMarkerSha256);
        Assert.Equal(authority.CumulativeDocumentationBlocks,
            snapshot.CumulativeDocumentationBlocks);
        Assert.Equal(authority.CumulativePatchBytes, snapshot.CumulativePatchBytes);
        Assert.Equal(authority.ChangedFiles.Length, snapshot.CumulativeChangedFiles.Length);

        var claimRemote = new CoordinationRemote();
        using var claimClient = Client(authority, claimRemote);
        var claimStore = GitHubCoordinationStore.Create(claimClient);
        var absence = await claimStore.ReadCurrentAsync();
        var claim = await claimStore.ClaimAsync(absence.Read!);
        var guarded = await claimStore.ReadClaimAsync(claim.State!);
        Assert.Same(guarded.State, claimStore.ValidateGuard(guarded.Guard!));
        Assert.Null(claimStore.ValidateGuard(new SyntheticGuard(guarded.State!)));
    }

    [Theory]
    [InlineData((int)GitHubCoordinationStage.Merged)]
    [InlineData((int)GitHubCoordinationStage.ClosedUnmerged)]
    public async Task Stale_draft_can_persist_an_authenticated_human_terminal_observation(
        int terminalValue)
    {
        var terminal = (GitHubCoordinationStage)terminalValue;
        var authority = InitialAuthority("operation-stale-terminal", '5');
        var chain = StaleDraftChain(authority);
        var remote = new CoordinationRemote { TargetHead = Oid('9') };
        remote.SeedChain(chain);
        using var client = Client(authority, remote);
        var store = GitHubCoordinationStore.Create(client);
        var current = await store.ReadCurrentAsync();

        var result = await store.AdvanceAsync(current.State!,
            GitHubCoordinationStageUpdate.Terminal(terminal));

        Assert.Equal(GitHubCoordinationOutcome.Advanced, result.Outcome);
        Assert.Equal(terminal, result.State!.Stage);
        Assert.Equal(chain[^1].PullRequestNumber, result.State.PullRequestNumber);
        Assert.Equal(chain[^1].ObservedBaseOid, result.State.ObservedBaseOid);

        var successorAuthority = SuccessorAuthority(authority, result.State, terminal);
        using var successorClient = Client(successorAuthority, remote);
        var successor = GitHubCoordinationStore.Create(successorClient);
        var terminalRead = await successor.ReadCurrentAsync();
        var admitted = await successor.ClaimAsync(terminalRead.Read!);
        Assert.Equal(GitHubCoordinationOutcome.Admitted, admitted.Outcome);
        Assert.Equal("generation-2", admitted.State!.GenerationId);
    }

    [Fact]
    public async Task Possible_delivery_cancellation_uses_independent_bounded_readback()
    {
        using var objectCancellation = new CancellationTokenSource();
        var objectRemote = new CoordinationRemote
        {
            CancelAfterMutation = LostMutation.Blob,
            CancelSource = objectCancellation,
        };
        var objectAuthority = InitialAuthority("operation-object-cancel", '5');
        using var objectClient = Client(objectAuthority, objectRemote);
        var objectStore = GitHubCoordinationStore.Create(objectClient);
        var absence = await objectStore.ReadCurrentAsync();

        var objectResult = await objectStore.ClaimAsync(absence.Read!, objectCancellation.Token);

        Assert.True(objectCancellation.IsCancellationRequested);
        Assert.Equal(GitHubFailureCode.Cancelled, objectResult.Failure!.TransportFailure!.Code);
        Assert.Equal(GitHubDelivery.NotDispatched, objectResult.Failure.Delivery);
        Assert.Equal(1, objectRemote.ObjectMutationAttempts);
        Assert.Equal(1, objectRemote.BlobReadAttempts);
        Assert.Equal(0, objectRemote.RefMutationAttempts);

        using var refCancellation = new CancellationTokenSource();
        var refAuthority = InitialAuthority("operation-ref-cancel", '5');
        var refRemote = new CoordinationRemote();
        using var refClient = Client(refAuthority, refRemote);
        var refStore = GitHubCoordinationStore.Create(refClient);
        var refAbsence = await refStore.ReadCurrentAsync();
        refRemote.CancelAfterMutation = LostMutation.Ref;
        refRemote.CancelSource = refCancellation;

        var refResult = await refStore.ClaimAsync(refAbsence.Read!, refCancellation.Token);

        Assert.True(refCancellation.IsCancellationRequested);
        Assert.Equal(GitHubCoordinationOutcome.Admitted, refResult.Outcome);
        Assert.Equal(GitHubCoordinationStage.Claimed, refResult.State!.Stage);
        Assert.Equal(refResult.State.HeadOid, refRemote.CoordinationHead);
        Assert.Equal(1, refRemote.SuccessfulRefMutations);

        var recoveryFailure = typeof(GitHubCoordinationStore).GetMethod(
            "RecoveryFailure", BindingFlags.Static | BindingFlags.NonPublic)!;
        using var expiredRecovery = new CancellationTokenSource();
        expiredRecovery.Cancel();
        var timeout = Assert.IsType<GitHubFailure>(recoveryFailure.Invoke(null,
            [new GitHubFailure(GitHubFailureCode.Cancelled), expiredRecovery]));
        Assert.Equal(GitHubFailureCode.Timeout, timeout.Code);
        using var activeRecovery = new CancellationTokenSource();
        var callerCancellation = Assert.IsType<GitHubFailure>(recoveryFailure.Invoke(null,
            [new GitHubFailure(GitHubFailureCode.Cancelled), activeRecovery]));
        Assert.Equal(GitHubFailureCode.Cancelled, callerCancellation.Code);
    }

    [Fact]
    public async Task Claim_guard_succeeds_only_while_the_exact_claim_head_and_target_remain_current()
    {
        var remote = new CoordinationRemote();
        using var client = Client(InitialAuthority("operation-guard", '5'), remote);
        var store = GitHubCoordinationStore.Create(client);
        var absence = await store.ReadCurrentAsync();
        var claim = await store.ClaimAsync(absence.Read!);
        var claimState = Assert.IsAssignableFrom<IGitHubCoordinationStateCapability>(claim.State);

        var guarded = await store.ReadClaimAsync(claimState);
        Assert.Equal(GitHubCoordinationOutcome.Guarded, guarded.Outcome);
        Assert.Equal(claimState.HeadOid, guarded.Guard!.State.HeadOid);

        remote.ForceCoordinationHead(Oid('1'));
        var moved = await store.ReadClaimAsync(claimState);
        Assert.Equal(GitHubCoordinationOutcome.Conflict, moved.Outcome);
        Assert.Equal(GitHubCoordinationFailureKind.Conflict, moved.Failure!.Kind);
    }

    [Fact]
    public async Task Missing_initial_ledger_and_cancelled_reads_do_not_mutate()
    {
        var predecessor = InitialAuthority("operation-predecessor", '5');
        var remote = new CoordinationRemote();
        using var replacementClient = Client(ReplacementAuthority(predecessor, "operation-replacement", '6'), remote);
        var missing = await GitHubCoordinationStore.Create(replacementClient).ReadCurrentAsync();
        Assert.Equal(GitHubCoordinationOutcome.ExpectedAbsence, missing.Outcome);

        using var cancelledClient = Client(InitialAuthority("operation-cancelled", '7'), remote);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = await GitHubCoordinationStore.Create(cancelledClient)
            .ReadCurrentAsync(cancellation.Token);
        Assert.Equal(GitHubFailureCode.Cancelled, cancelled.Failure!.TransportFailure!.Code);
        Assert.Equal(GitHubDelivery.NotDispatched, cancelled.Failure.Delivery);
        Assert.Equal(0, remote.ObjectMutationAttempts);
        Assert.Equal(0, remote.RefMutationAttempts);
    }

    [Fact]
    public async Task Rewind_after_read_rejects_the_stale_nonzero_predecessor_before_object_creation()
    {
        var predecessorAuthority = InitialAuthority("operation-rewind-old", '5');
        var remote = new CoordinationRemote();
        remote.SeedChain(PublishedChain(predecessorAuthority));
        using var client = Client(ReplacementAuthority(predecessorAuthority, "operation-rewind-new", '6'), remote);
        var store = GitHubCoordinationStore.Create(client);
        var current = await store.ReadCurrentAsync();
        remote.ForceCoordinationHead(Oid('1'));

        var result = await store.ClaimAsync(current.Read!);

        Assert.Equal(GitHubCoordinationOutcome.Conflict, result.Outcome);
        Assert.Equal(0, remote.ObjectMutationAttempts);
        Assert.Equal(0, remote.RefMutationAttempts);
    }

    [Fact]
    public async Task An_authenticated_observation_for_another_operation_cannot_authorize_a_guard_or_advance()
    {
        var remote = new CoordinationRemote();
        var observedAuthority = InitialAuthority("operation-observed", '5');
        remote.Seed(GitHubCoordinationCodec.CreateClaim(observedAuthority, Oid('0')));
        using var client = Client(InitialAuthority("operation-requested", '6'), remote);
        var store = GitHubCoordinationStore.Create(client);
        var observed = await store.ReadCurrentAsync();

        var guard = await store.ReadClaimAsync(observed.State!);
        var advance = await store.AdvanceAsync(observed.State!,
            GitHubCoordinationStageUpdate.ContentCreated(Oid('2')));

        Assert.Equal(GitHubCoordinationFailureKind.DifferentOperation, guard.Failure!.Kind);
        Assert.Equal(GitHubCoordinationFailureKind.DifferentOperation, advance.Failure!.Kind);
        Assert.Equal(0, remote.ObjectMutationAttempts);
        Assert.Equal(0, remote.RefMutationAttempts);
    }

    [Theory]
    [InlineData((int)GitHubCoordinationStage.Published, '1')]
    [InlineData((int)GitHubCoordinationStage.StaleDraft, '9')]
    public async Task Fenced_state_accepts_both_normative_PR_results(
        int resultStageValue,
        char observedBase)
    {
        var resultStage = (GitHubCoordinationStage)resultStageValue;
        var remote = new CoordinationRemote();
        var authority = InitialAuthority("operation-pr-result", '5');
        using var client = Client(authority, remote);
        var store = GitHubCoordinationStore.Create(client);
        remote.SeedChain(PublishedChain(authority)[..4]);
        var proposal = await store.ReadCurrentAsync();
        var initial = GitHubCoordinationCodec.CreateClaim(authority, Oid('0'));
        var creation = PullRequestCreationCommitment(initial,
            GitHubPublicationFactory.CreateProposalRef(authority), Oid('2'), Oid('3'));
        var marker = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            "<!-- contract-scribe-publication-v1 ownership=sha256:" + creation + " -->\n")));
        remote.TargetHead = Oid(observedBase);

        var result = await store.AdvanceAsync(proposal.State!,
            GitHubCoordinationStageUpdate.PullRequestResult(resultStage, Oid('2'), Oid('3'),
                creation, 17, Oid('1'), Oid(observedBase), marker));

        Assert.Equal(GitHubCoordinationOutcome.Advanced, result.Outcome);
        Assert.Equal(resultStage, result.State!.Stage);
        Assert.Equal(Oid(observedBase), remote.State(result.State.HeadOid).ObservedBaseOid);
    }

    [Fact]
    public async Task Every_stage_edge_retains_the_authenticated_resource_identity_before_any_write()
    {
        var authority = InitialAuthority("operation-retained", '5');
        var chain = PublishedChain(authority);

        await AssertAdvanceRejected(authority, chain[..2],
            GitHubCoordinationStageUpdate.ProposalRefAdvanced(Oid('4'), Oid('3')));

        var replacementCreation = PullRequestCreationCommitment(chain[0],
            GitHubPublicationFactory.CreateProposalRef(authority), Oid('2'), Oid('4'));
        await AssertAdvanceRejected(authority, chain[..4],
            GitHubCoordinationStageUpdate.PullRequestResult(
                GitHubCoordinationStage.Published, Oid('2'), Oid('4'),
                replacementCreation, 17, Oid('1'), Oid('1'),
                OwnershipMarker(replacementCreation)));

        var replacementProposal = PullRequestCreationCommitment(chain[0],
            GitHubPublicationFactory.CreateProposalRef(authority), Oid('4'), Oid('3'));
        await AssertAdvanceRejected(authority, chain[..4],
            GitHubCoordinationStageUpdate.PullRequestResult(
                GitHubCoordinationStage.Published, Oid('4'), Oid('3'),
                replacementProposal, 17, Oid('1'), Oid('1'),
                OwnershipMarker(replacementProposal)));

        var created = GitHubCoordinationCodec.WithStage(chain[3],
            GitHubCoordinationStage.PullRequestCreated,
            PrepareFixture(chain[3]).CommitOid,
            chain[^1].ContentCommitOid, chain[^1].ProposalRefOid,
            chain[^1].ProposalCommitOid, chain[^1].ProposalTreeOid,
            chain[^1].PullRequestCreationOperationCommitmentSha256, 17,
            Oid('1'), Oid('1'), chain[^1].OwnershipMarkerSha256);
        await AssertAdvanceRejected(authority, [.. chain[..4], created],
            GitHubCoordinationStageUpdate.PullRequestResult(
                GitHubCoordinationStage.Published, Oid('2'), Oid('3'),
                chain[^1].PullRequestCreationOperationCommitmentSha256!, 18,
                Oid('1'), Oid('1'), chain[^1].OwnershipMarkerSha256!));
        var replacementPr = Hash('c');
        await AssertAdvanceRejected(authority, [.. chain[..4], created],
            GitHubCoordinationStageUpdate.PullRequestResult(
                GitHubCoordinationStage.Published, Oid('2'), Oid('3'),
                replacementPr, 17, Oid('1'), Oid('1'),
                OwnershipMarker(replacementPr)));
        await AssertAdvanceRejected(authority, [.. chain[..4], created],
            GitHubCoordinationStageUpdate.PullRequestResult(
                GitHubCoordinationStage.Published, Oid('2'), Oid('3'),
                chain[^1].PullRequestCreationOperationCommitmentSha256!, 17,
                Oid('1'), Oid('1'), Hash('d')));
        await AssertAdvanceRejected(authority, [.. chain[..4], created],
            GitHubCoordinationStageUpdate.PullRequestResult(
                GitHubCoordinationStage.Published, Oid('2'), Oid('3'),
                chain[^1].PullRequestCreationOperationCommitmentSha256!, 17,
                Oid('9'), Oid('9'), chain[^1].OwnershipMarkerSha256!));
    }

    [Fact]
    public async Task PR_commitment_marker_and_expected_base_are_authenticated_before_any_write()
    {
        var authority = InitialAuthority("operation-pr-auth", '5');
        var chain = PublishedChain(authority);
        var creation = chain[^1].PullRequestCreationOperationCommitmentSha256!;

        await AssertAdvanceRejected(authority, chain[..4],
            GitHubCoordinationStageUpdate.PullRequestResult(
                GitHubCoordinationStage.Published, Oid('2'), Oid('3'),
                Hash('a'), 17, Oid('1'), Oid('1'), OwnershipMarker(Hash('a'))));
        await AssertAdvanceRejected(authority, chain[..4],
            GitHubCoordinationStageUpdate.PullRequestResult(
                GitHubCoordinationStage.Published, Oid('2'), Oid('3'),
                creation, 17, Oid('1'), Oid('1'), Hash('b')));
        await AssertAdvanceRejected(authority, chain[..4],
            GitHubCoordinationStageUpdate.PullRequestResult(
                GitHubCoordinationStage.Published, Oid('2'), Oid('3'),
                creation, 17, Oid('9'), Oid('9'), OwnershipMarker(creation)));
    }

    [Fact]
    public async Task Caller_cannot_fabricate_target_drift_while_the_target_is_current()
    {
        var authority = InitialAuthority("operation-false-stale", '5');
        var chain = PublishedChain(authority);

        await AssertAdvanceRejected(authority, chain[..3],
            GitHubCoordinationStageUpdate.Stale(Oid('1'), Oid('9')));
    }

    [Fact]
    public async Task Current_state_requires_one_legal_authenticated_immediate_edge()
    {
        var authority = InitialAuthority("operation-edge", '5');
        var chain = PublishedChain(authority);
        var published = chain[^1];
        var impossibleInitial = GitHubCoordinationCodec.WithStage(chain[0],
            GitHubCoordinationStage.Published, Oid('0'), published.ContentCommitOid,
            published.ProposalRefOid, published.ProposalCommitOid, published.ProposalTreeOid,
            published.PullRequestCreationOperationCommitmentSha256, published.PullRequestNumber,
            published.ExpectedBaseOid, published.ObservedBaseOid, published.OwnershipMarkerSha256);
        var skipped = GitHubCoordinationCodec.WithStage(chain[1],
            GitHubCoordinationStage.AwaitingReview,
            PrepareFixture(chain[1]).CommitOid, published.ContentCommitOid,
            published.ProposalRefOid, published.ProposalCommitOid, published.ProposalTreeOid,
            published.PullRequestCreationOperationCommitmentSha256, published.PullRequestNumber,
            published.ExpectedBaseOid, published.ObservedBaseOid, published.OwnershipMarkerSha256);

        var initialRemote = new CoordinationRemote();
        initialRemote.Seed(impossibleInitial);
        using var initialClient = Client(authority, initialRemote);
        var initialRead = await GitHubCoordinationStore.Create(initialClient).ReadCurrentAsync();
        Assert.Equal(GitHubCoordinationFailureKind.ObjectMismatch, initialRead.Failure!.Kind);

        var skippedRemote = new CoordinationRemote();
        skippedRemote.SeedChain([.. chain[..2], skipped]);
        using var skippedClient = Client(authority, skippedRemote);
        var skippedRead = await GitHubCoordinationStore.Create(skippedClient).ReadCurrentAsync();
        Assert.Equal(GitHubCoordinationFailureKind.ObjectMismatch, skippedRead.Failure!.Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Later_partial_cannot_hide_a_terminal_successor_with_a_zero_predecessor(
        bool includeProposal)
    {
        var predecessorAuthority = InitialAuthority("operation-hidden-root-a", '5');
        var authority = SuccessorAuthority(predecessorAuthority, Published(predecessorAuthority), GitHubCoordinationStage.Merged);
        var invalidRoot = GitHubCoordinationCodec.CreateClaim(authority, Oid('0'));
        var content = GitHubCoordinationCodec.WithStage(invalidRoot,
            GitHubCoordinationStage.ContentCreated,
            PrepareFixture(invalidRoot).CommitOid, Oid('2'));
        var proposal = GitHubCoordinationCodec.WithStage(content,
            GitHubCoordinationStage.ProposalRefAdvanced,
            PrepareFixture(content).CommitOid,
            Oid('2'), Oid('2'), Oid('2'), Oid('3'));
        var remote = new CoordinationRemote { TargetHead = Oid('9') };
        remote.SeedChain(includeProposal
            ? [invalidRoot, content, proposal]
            : [invalidRoot, content]);
        using var client = Client(authority, remote);

        var result = await GitHubCoordinationStore.Create(client).ReadCurrentAsync();

        Assert.Null(result.State);
        Assert.Null(result.Read);
        Assert.Equal(GitHubCoordinationFailureKind.ObjectMismatch, result.Failure!.Kind);
        Assert.Equal(0, remote.ObjectMutationAttempts);
        Assert.Equal(0, remote.RefMutationAttempts);
        var maximum = typeof(GitHubCoordinationStore).GetField(
            "MaximumSameOperationTransitions", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Equal(7, maximum.GetRawConstantValue());
    }

    [Fact]
    public async Task Restarted_replacement_cannot_rebind_the_sealed_path_map()
    {
        var predecessorAuthority = InitialAuthority("operation-map-a", '5');
        var predecessor = PublishedChain(predecessorAuthority);
        var authority = ReplacementAuthority(predecessorAuthority, "operation-map-b", '6',
            precedingFileCandidate: '9');
        var root = GitHubCoordinationCodec.CreateClaim(authority,
            PrepareFixture(predecessor[^1]).CommitOid);
        var remote = new CoordinationRemote();
        remote.SeedChain([.. predecessor, root]);
        using var client = Client(authority, remote);

        var result = await GitHubCoordinationStore.Create(client).ReadCurrentAsync();

        Assert.Null(result.State);
        Assert.Null(result.Read);
        Assert.Equal(GitHubCoordinationFailureKind.ObjectMismatch, result.Failure!.Kind);
        Assert.Equal(0, remote.ObjectMutationAttempts);
        Assert.Equal(0, remote.RefMutationAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restarted_successor_replay_authenticates_the_terminal_parent(
        bool closedUnmerged)
    {
        var terminalStage = closedUnmerged
            ? GitHubCoordinationStage.ClosedUnmerged
            : GitHubCoordinationStage.Merged;
        var predecessorAuthority = InitialAuthority("operation-terminal-a", '5');
        var chain = PublishedChain(predecessorAuthority);
        var published = chain[^1];
        var terminal = GitHubCoordinationCodec.WithStage(published, terminalStage,
            PrepareFixture(published).CommitOid,
            published.ContentCommitOid, published.ProposalRefOid,
            published.ProposalCommitOid, published.ProposalTreeOid,
            published.PullRequestCreationOperationCommitmentSha256,
            published.PullRequestNumber, published.ExpectedBaseOid,
            published.ObservedBaseOid, published.OwnershipMarkerSha256);
        var authority = SuccessorAuthority(predecessorAuthority, terminal, terminalStage);
        var substitutedParent = GitHubCoordinationCodec.WithStage(published, terminalStage,
            PrepareFixture(published).CommitOid,
            published.ContentCommitOid, published.ProposalRefOid,
            published.ProposalCommitOid, published.ProposalTreeOid,
            published.PullRequestCreationOperationCommitmentSha256,
            published.PullRequestNumber!.Value + 1, published.ExpectedBaseOid,
            published.ObservedBaseOid, published.OwnershipMarkerSha256);
        var root = GitHubCoordinationCodec.CreateClaim(authority,
            PrepareFixture(substitutedParent).CommitOid);
        var remote = new CoordinationRemote { TargetHead = Oid('9') };
        remote.SeedChain([.. chain, substitutedParent, root]);
        using var client = Client(authority, remote);

        var result = await GitHubCoordinationStore.Create(client).ReadCurrentAsync();

        Assert.Null(result.State);
        Assert.Null(result.Read);
        Assert.Equal(GitHubCoordinationFailureKind.ObjectMismatch, result.Failure!.Kind);
        Assert.Equal(0, remote.ObjectMutationAttempts);
        Assert.Equal(0, remote.RefMutationAttempts);
    }

    [Fact]
    public async Task Stale_state_binds_expected_base_to_operation_target()
    {
        var authority = InitialAuthority("operation-stale-base", '5');
        var claim = GitHubCoordinationCodec.CreateClaim(authority, Oid('0'));
        var claimHead = PrepareFixture(claim).CommitOid;
        var stale = GitHubCoordinationCodec.WithStage(claim,
            GitHubCoordinationStage.Stale, claimHead,
            expectedBaseOid: Oid('1'), observedBaseOid: Oid('9'));
        var canonical = Encoding.UTF8.GetString(GitHubCoordinationCodec.Encode(stale));
        var tampered = Encoding.UTF8.GetBytes(canonical.Replace(
            $"\"expectedBaseOid\":\"{Oid('1')}\"",
            $"\"expectedBaseOid\":\"{Oid('2')}\"", StringComparison.Ordinal));
        Assert.Throws<GitHubCoordinationException>(() => GitHubCoordinationCodec.Decode(tampered));
        var remote = new CoordinationRemote { TargetHead = Oid('9') };
        remote.Seed(claim);
        remote.SeedRaw(tampered, stale.OperationCommitmentSha256, "stale", claimHead);
        using var client = Client(authority, remote);

        var result = await GitHubCoordinationStore.Create(client).ReadCurrentAsync();

        Assert.Null(result.State);
        Assert.Null(result.Read);
        Assert.Equal(GitHubCoordinationFailureKind.ObjectMismatch, result.Failure!.Kind);
        Assert.Equal(0, remote.ObjectMutationAttempts);
        Assert.Equal(0, remote.RefMutationAttempts);
    }

    [Fact]
    public async Task Same_operation_edges_authenticate_stale_source_shape_and_PR_base()
    {
        var staleAuthority = InitialAuthority("operation-stale-edge", '5');
        var claim = GitHubCoordinationCodec.CreateClaim(staleAuthority, Oid('0'));
        var proposalShapedStale = GitHubCoordinationCodec.WithStage(claim,
            GitHubCoordinationStage.Stale,
            PrepareFixture(claim).CommitOid,
            Oid('2'), Oid('2'), Oid('2'), Oid('3'),
            expectedBaseOid: Oid('1'), observedBaseOid: Oid('9'));
        var staleRemote = new CoordinationRemote { TargetHead = Oid('9') };
        staleRemote.SeedChain([claim, proposalShapedStale]);
        using var staleClient = Client(staleAuthority, staleRemote);

        var staleResult = await GitHubCoordinationStore.Create(staleClient).ReadCurrentAsync();

        Assert.Null(staleResult.State);
        Assert.Equal(GitHubCoordinationFailureKind.ObjectMismatch, staleResult.Failure!.Kind);

        var prAuthority = InitialAuthority("operation-pr-base-edge", '5');
        var published = PublishedChain(prAuthority);
        var proposal = published[2];
        var canonicalPublished = Encoding.UTF8.GetString(
            GitHubCoordinationCodec.Encode(published[3]));
        var wrongBase = Encoding.UTF8.GetBytes(canonicalPublished
            .Replace($"\"expectedBaseOid\":\"{Oid('1')}\"",
                $"\"expectedBaseOid\":\"{Oid('9')}\"", StringComparison.Ordinal)
            .Replace($"\"observedBaseOid\":\"{Oid('1')}\"",
                $"\"observedBaseOid\":\"{Oid('9')}\"", StringComparison.Ordinal));
        var prRemote = new CoordinationRemote();
        prRemote.SeedChain(published[..3]);
        prRemote.SeedRaw(wrongBase, published[^1].OperationCommitmentSha256,
            "published", PrepareFixture(proposal).CommitOid);
        using var prClient = Client(prAuthority, prRemote);

        var prResult = await GitHubCoordinationStore.Create(prClient).ReadCurrentAsync();

        Assert.Null(prResult.State);
        Assert.Equal(GitHubCoordinationFailureKind.ObjectMismatch, prResult.Failure!.Kind);
        Assert.Equal(0, staleRemote.ObjectMutationAttempts + prRemote.ObjectMutationAttempts);
        Assert.Equal(0, staleRemote.RefMutationAttempts + prRemote.RefMutationAttempts);
    }

    [Theory]
    [InlineData("\"cumulativeDocumentationBlocks\":1", "\"cumulativeDocumentationBlocks\":513")]
    [InlineData("\"cumulativePatchBytes\":12", "\"cumulativePatchBytes\":1099511627777")]
    public async Task Impossible_canonical_remote_bounds_never_produce_a_state_capability(
        string original,
        string replacement)
    {
        var authority = InitialAuthority("operation-remote-bounds", '5');
        var claim = GitHubCoordinationCodec.CreateClaim(authority, Oid('0'));
        var canonical = Encoding.UTF8.GetString(GitHubCoordinationCodec.Encode(claim));
        var remote = new CoordinationRemote();
        remote.SeedRaw(Encoding.UTF8.GetBytes(canonical.Replace(
            original, replacement, StringComparison.Ordinal)), claim.OperationCommitmentSha256,
            "claimed", claim.TargetCommitOid);
        using var client = Client(authority, remote);

        var result = await GitHubCoordinationStore.Create(client).ReadCurrentAsync();

        Assert.Null(result.State);
        Assert.Equal(GitHubCoordinationFailureKind.ObjectMismatch, result.Failure!.Kind);
    }

    [Fact]
    public async Task Same_snapshot_append_cannot_cross_a_changed_target_base()
    {
        var predecessorAuthority = InitialAuthority("operation-base-a", '5');
        var remote = new CoordinationRemote { TargetHead = Oid('9') };
        remote.SeedChain(PublishedChain(predecessorAuthority));
        using var client = Client(
            ReplacementAuthority(predecessorAuthority, "operation-base-b", '6', '9'), remote);
        var store = GitHubCoordinationStore.Create(client);
        var current = await store.ReadCurrentAsync();

        var result = await store.ClaimAsync(current.Read!);

        Assert.Equal(GitHubCoordinationFailureKind.StageConflict, result.Failure!.Kind);
        Assert.Equal(0, remote.ObjectMutationAttempts);
        Assert.Equal(0, remote.RefMutationAttempts);
    }

    [Fact]
    public async Task Immediate_edge_preserves_repository_ASCII_case_equivalence()
    {
        var predecessor = InitialAuthority("operation-case-a", '5');
        var history = PublishedChain(predecessor);
        var terminal = G1Terminal(history[^1], GitHubCoordinationStage.Merged);
        var remote = new CoordinationRemote { TargetHead = Oid('9') };
        remote.SeedChain([.. history, terminal]);
        var next = GitHubPublicationFactory.CreateAuthority(GitHubPublicationTestBatch.Copy(
            SuccessorAuthority(predecessor, terminal, GitHubCoordinationStage.Merged)) with
        { RepositoryOwner = "owner", RepositoryName = "REPO" });
        using var client = Client(next, remote);
        var store = GitHubCoordinationStore.Create(client);
        var current = await store.ReadCurrentAsync();
        var result = await store.ClaimAsync(current.Read!);
        Assert.Equal(GitHubCoordinationOutcome.Admitted, result.Outcome);
        Assert.Equal("owner/REPO", remote.State(result.State!.HeadOid).RepositoryId);
    }

    private static async Task AssertAdvanceRejected(
        ValidatedGitHubPublicationAuthority authority,
        IEnumerable<GitHubCoordinationState> chain,
        GitHubCoordinationStageUpdate update)
    {
        var remote = new CoordinationRemote();
        remote.SeedChain(chain);
        using var client = Client(authority, remote);
        var store = GitHubCoordinationStore.Create(client);
        var current = await store.ReadCurrentAsync();
        Assert.NotNull(current.State);

        var result = await store.AdvanceAsync(current.State!, update);

        Assert.Equal(GitHubCoordinationOutcome.Failed, result.Outcome);
        Assert.Equal(GitHubCoordinationFailureKind.InvalidInput, result.Failure!.Kind);
        Assert.Equal(0, remote.ObjectMutationAttempts);
        Assert.Equal(0, remote.RefMutationAttempts);
    }

    [Theory]
    [InlineData(ObjectTamper.Blob)]
    [InlineData(ObjectTamper.LeafTree)]
    [InlineData(ObjectTamper.RootTree)]
    [InlineData(ObjectTamper.Parent)]
    [InlineData(ObjectTamper.Tree)]
    [InlineData(ObjectTamper.Actor)]
    [InlineData(ObjectTamper.Time)]
    [InlineData(ObjectTamper.Message)]
    public async Task Every_authenticated_object_component_fails_closed_when_tampered(ObjectTamper tamper)
    {
        var authority = InitialAuthority("operation-tamper", '5');
        var remote = new CoordinationRemote();
        remote.Seed(GitHubCoordinationCodec.CreateClaim(authority, Oid('0')));
        remote.Tamper(tamper);
        using var client = Client(authority, remote);

        var result = await GitHubCoordinationStore.Create(client).ReadCurrentAsync();

        Assert.Equal(GitHubCoordinationOutcome.Failed, result.Outcome);
        Assert.Equal(GitHubCoordinationFailureKind.ObjectMismatch, result.Failure!.Kind);
    }

    // Content-addressed fixture documents let independently constructed state sequences
    // retain the exact preceding event, including when tests deliberately forge an edge.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, GitHubCoordinationDocument>
        FixtureDocuments = new(StringComparer.Ordinal);

    private static GitHubPreparedCoordination PrepareFixture(GitHubCoordinationState state)
    {
        FixtureDocuments.TryGetValue(state.CoordinationPredecessorOid, out var previous);
        var prepared = GitHubCoordinationObjects.PreparePublication(state,
            previous?.RetainAt(state.CoordinationPredecessorOid));
        FixtureDocuments[prepared.CommitOid] = prepared.Document;
        return prepared;
    }

    private static GitHubCoordinationState Published(ValidatedGitHubPublicationAuthority authority)
        => PublishedChain(authority)[^1];

    internal static ImmutableArray<GitHubCoordinationState> PublishedChain(
        ValidatedGitHubPublicationAuthority authority)
    {
        var claim = GitHubCoordinationCodec.CreateClaim(authority, Oid('0'));
        var proposalOid = Oid('2');
        var treeOid = Oid('3');
        var content = GitHubCoordinationCodec.WithStage(claim,
            GitHubCoordinationStage.ContentCreated,
            PrepareFixture(claim).CommitOid, proposalOid);
        var proposal = GitHubCoordinationCodec.WithStage(content,
            GitHubCoordinationStage.ProposalRefAdvanced,
            PrepareFixture(content).CommitOid,
            proposalOid, proposalOid, proposalOid, treeOid);
        var creation = PullRequestCreationCommitment(claim,
            GitHubPublicationFactory.CreateProposalRef(authority), proposalOid, treeOid);
        var marker = OwnershipMarker(creation);
        var fence = GitHubCoordinationCodec.WithStage(proposal,
            GitHubCoordinationStage.PullRequestCreateStarted,
            PrepareFixture(proposal).CommitOid,
            proposalOid, proposalOid, proposalOid, treeOid, creation, null,
            authority.ExpectedBaseCommitOid, authority.ExpectedBaseCommitOid, marker);
        var published = GitHubCoordinationCodec.WithStage(fence,
            GitHubCoordinationStage.Published,
            PrepareFixture(fence).CommitOid,
            proposalOid, proposalOid, proposalOid, treeOid, creation, 17,
            authority.ExpectedBaseCommitOid, authority.ExpectedBaseCommitOid, marker);
        return [claim, content, proposal, fence, published];
    }

    private static ImmutableArray<GitHubCoordinationState> StaleDraftChain(
        ValidatedGitHubPublicationAuthority authority)
    {
        var published = PublishedChain(authority);
        var proposal = published[3];
        var staleDraft = GitHubCoordinationCodec.WithStage(proposal,
            GitHubCoordinationStage.StaleDraft,
            PrepareFixture(proposal).CommitOid,
            published[^1].ContentCommitOid, published[^1].ProposalRefOid,
            published[^1].ProposalCommitOid, published[^1].ProposalTreeOid,
            published[^1].PullRequestCreationOperationCommitmentSha256,
            published[^1].PullRequestNumber, Oid('1'), Oid('9'),
            published[^1].OwnershipMarkerSha256);
        return [.. published[..4], staleDraft];
    }

    private static ImmutableArray<GitHubCoordinationState> ReanchoredGeneration(
        ValidatedGitHubPublicationAuthority authority, string parent, int number)
    {
        var states = ImmutableArray.CreateBuilder<GitHubCoordinationState>();
        foreach (var value in PublishedChain(authority))
        {
            var state = Reanchor(value, parent);
            if (value.PullRequestNumber is not null)
                state = GitHubCoordinationCodec.WithStage(state, state.Stage, parent,
                    state.ContentCommitOid, state.ProposalRefOid, state.ProposalCommitOid, state.ProposalTreeOid,
                    state.PullRequestCreationOperationCommitmentSha256, number,
                    state.ExpectedBaseOid, state.ObservedBaseOid, state.OwnershipMarkerSha256);
            states.Add(state);
            parent = PrepareFixture(state).CommitOid;
        }
        return states.ToImmutable();
    }

    private static GitHubCoordinationState Reanchor(GitHubCoordinationState state, string parent) =>
        GitHubCoordinationCodec.WithStage(state, state.Stage, parent, state.ContentCommitOid,
            state.ProposalRefOid, state.ProposalCommitOid, state.ProposalTreeOid,
            state.PullRequestCreationOperationCommitmentSha256, state.PullRequestNumber,
            state.ExpectedBaseOid, state.ObservedBaseOid, state.OwnershipMarkerSha256);

    private static string OwnershipMarker(string creation) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            "<!-- contract-scribe-publication-v1 ownership=sha256:" + creation + " -->\n")));

    private static string PullRequestCreationCommitment(
        GitHubCoordinationState state,
        string proposalRef,
        string proposalOid,
        string treeOid)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Add(hash, "domain", "contract-scribe/github-pull-request-creation/v1");
        Add(hash, "version", GitHubPublicationContract.Version);
        Add(hash, "repository-id", state.RepositoryId);
        Add(hash, "target-ref", state.TargetRef);
        Add(hash, "target-commit-oid", state.TargetCommitOid);
        Add(hash, "authority-commitment", state.AuthorityCommitmentSha256);
        Add(hash, "operation-commitment", state.OperationCommitmentSha256);
        Add(hash, "generation-id", state.GenerationId);
        Add(hash, "proposal-ref", proposalRef);
        Add(hash, "proposal-commit-oid", proposalOid);
        Add(hash, "proposal-tree-oid", treeOid);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void Add(IncrementalHash hash, string label, string value)
    {
        Append(hash, Encoding.UTF8.GetBytes(label));
        Append(hash, Encoding.UTF8.GetBytes(value));
    }

    private static void Add(IncrementalHash hash, string label, int value)
    {
        Append(hash, Encoding.UTF8.GetBytes(label));
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        Append(hash, bytes);
    }

    private static void Append(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        hash.AppendData(length);
        hash.AppendData(value);
    }

    internal static ValidatedGitHubPublicationAuthority InitialAuthority(
        string operation,
        char candidate,
        string generation = "generation-1",
        char target = '1') =>
        GitHubPublicationFactory.CreateAuthority(new("Owner",
            "repo",
            "refs/heads/main",
            Oid(target),
            "campaign-1",
            Hash('1'),
            Hash('2'),
            Hash('3'),
            7,
            Hash('4'),
            Hash(candidate),
            Hash('6'),
            Hash('7'),
            Hash('8'),
            operation,
            generation,
            GitHubPublicationTransitionKind.Initial,
            Batch: GitHubPublicationTestBatch.ForFiles([new("docs/readme.md", Hash('a'), Hash(candidate), 1, 10, 12, 1, 1)]),
            [new("docs/readme.md", Hash('a'), Hash(candidate), 1, 10, 12, 1, 1)],
            null));

    internal static ValidatedGitHubPublicationAuthority ReplacementAuthority(
        ValidatedGitHubPublicationAuthority predecessor,
        string operation,
        char candidate,
        char? target = null,
        bool alternateRepositoryCase = false,
        char precedingFileCandidate = '5') => GitHubPublicationFactory.CreateAuthority(new(alternateRepositoryCase ? predecessor.RepositoryOwner.ToLowerInvariant() : predecessor.RepositoryOwner,
            alternateRepositoryCase ? predecessor.RepositoryName.ToUpperInvariant() : predecessor.RepositoryName,
            predecessor.TargetRef,
            target is { } targetValue ? Oid(targetValue) : predecessor.ExpectedBaseCommitOid,
            predecessor.CampaignLineage,
            predecessor.SnapshotCommitmentSha256,
            Hash('2'),
            Hash('3'),
            8,
            Hash('4'),
            Hash(candidate),
            Hash('6'),
            Hash('7'),
            Hash('8'),
            operation,
            predecessor.GenerationId,
            GitHubPublicationTransitionKind.Initial,
            Batch: GitHubPublicationTestBatch.ForFiles([new("docs/readme.md", Hash(precedingFileCandidate), Hash(candidate), 1, 12, 14, 1, 1)]),
            [new("docs/readme.md", Hash(precedingFileCandidate), Hash(candidate), 1, 12, 14, 1, 1)],
            null));

    [Fact]
    public async Task Successive_generations_authenticate_original_creation_and_terminal_provenance()
    {
        var first = InitialAuthority("original-creation", '5');
        var original = PublishedChain(first);
        var terminal = G1Terminal(original[^1], GitHubCoordinationStage.Merged);
        var next = SuccessorAuthority(first, terminal, GitHubCoordinationStage.Merged);
        var second = ReanchoredGeneration(next, PrepareFixture(terminal).CommitOid, 18);
        var history = original.Add(terminal).AddRange(second);
        var remote = new CoordinationRemote { TargetHead = Oid('9') };
        remote.SeedChain(history);
        using var client = Client(next, remote);
        var store = GitHubCoordinationStore.Create(client);
        var recovered = await store.ReadCurrentAsync();
        Assert.NotNull(recovered.State);
        Assert.Equal(next.OperationId, store.CreationSource(recovered.State!)!.OperationId);
        Assert.Equal(first.OperationId, store.AdmissionSource(recovered.State!)!.OperationId);
        Assert.Equal(new[] { 17, 18 }, store.ObservedGenerations(recovered.State!).Select(g => g.Number!.Value).Order());
        var head = remote.CoordinationHead;
        var writes = remote.ObjectMutationAttempts;
        remote.RemoveCommit(original[1]);
        Assert.Null((await store.ReadCurrentAsync()).State);
        Assert.Equal(head, remote.CoordinationHead);
        Assert.Equal(writes, remote.ObjectMutationAttempts);
        foreach (var corruptMarker in new[] { false, true })
        {
            var forged = new CoordinationRemote { TargetHead = Oid('9') };
            var parent = Oid('0');
            foreach (var entry in history)
            {
                var changed = Reanchor(entry, parent);
                if (entry.OperationId == first.OperationId && entry.PullRequestCreationOperationCommitmentSha256 is not null)
                    changed = GitHubCoordinationCodec.WithStage(changed, changed.Stage, parent,
                        changed.ContentCommitOid, changed.ProposalRefOid, changed.ProposalCommitOid, changed.ProposalTreeOid,
                        corruptMarker ? Hash('f') : changed.PullRequestCreationOperationCommitmentSha256,
                        changed.PullRequestNumber is null ? null : 19, changed.ExpectedBaseOid, changed.ObservedBaseOid,
                        corruptMarker ? OwnershipMarker(Hash('f')) : changed.OwnershipMarkerSha256);
                forged.Seed(changed);
                parent = PrepareFixture(changed).CommitOid;
            }
            using var badClient = Client(next, forged);
            var rejected = await GitHubCoordinationStore.Create(badClient).ReadCurrentAsync();
            Assert.Null(rejected.State);
            Assert.Equal(GitHubCoordinationFailureKind.ObjectMismatch, rejected.Failure!.Kind);
            Assert.Equal(0, forged.ObjectMutationAttempts);
        }
    }

    [Fact]
    public async Task Creation_history_bound_returns_no_partial_authority()
    {
        var authority = InitialAuthority("history-origin", '5');
        var chain = PublishedChain(authority).ToList();
        for (var index = 0; chain.Count <= GitHubCoordinationStore.MaximumHistoryStates; index++)
        {
            var terminal = G1Terminal(chain[^1], GitHubCoordinationStage.Merged);
            chain.Add(terminal);
            authority = GitHubPublicationFactory.CreateAuthority(GitHubPublicationTestBatch.Copy(authority) with
            {
                SnapshotCommitmentSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("snapshot " + index))),
                OperationId = "history-" + index,
                GenerationId = "generation-" + index,
                Transition = GitHubPublicationTransitionKind.SuccessorAfterMerge,
                TerminalPredecessor = new("previous", terminal.PullRequestNumber!.Value,
                    terminal.GenerationId, terminal.ProposalCommitOid!, GitHubPublicationPredecessorDisposition.Merged),
            });
            chain.AddRange(ReanchoredGeneration(authority, PrepareFixture(terminal).CommitOid, index + 18));
        }
        var remote = new CoordinationRemote(); remote.SeedChain(chain);
        using var client = Client(authority, remote);
        var result = await GitHubCoordinationStore.Create(client).ReadCurrentAsync();
        Assert.Equal(GitHubCoordinationFailureKind.Bounds, result.Failure!.Kind);
        Assert.Null(result.State);
        Assert.Null(result.Read);
        Assert.Equal(0, remote.ObjectMutationAttempts);
    }

    private static ValidatedGitHubPublicationAuthority SuccessorAuthority(
        ValidatedGitHubPublicationAuthority predecessor,
        GitHubCoordinationState terminal,
        GitHubCoordinationStage stage) => SuccessorAuthority(
            predecessor, terminal.PullRequestNumber!.Value, terminal.GenerationId,
            terminal.ProposalCommitOid!, stage);

    private static ValidatedGitHubPublicationAuthority SuccessorAuthority(
        ValidatedGitHubPublicationAuthority predecessor,
        IGitHubCoordinationStateCapability terminal,
        GitHubCoordinationStage stage) => SuccessorAuthority(
            predecessor, terminal.PullRequestNumber!.Value, terminal.GenerationId,
            terminal.ProposalCommitOid!, stage);

    private static ValidatedGitHubPublicationAuthority SuccessorAuthority(
        ValidatedGitHubPublicationAuthority predecessor,
        long pullRequestNumber,
        string generationId,
        string headOid,
        GitHubCoordinationStage stage,
        string? expectedBaseOid = null)
    {
        var disposition = stage == GitHubCoordinationStage.Merged
            ? GitHubPublicationPredecessorDisposition.Merged
            : GitHubPublicationPredecessorDisposition.ClosedUnmerged;
        var transition = stage == GitHubCoordinationStage.Merged
            ? GitHubPublicationTransitionKind.SuccessorAfterMerge
            : GitHubPublicationTransitionKind.SuccessorAfterClosedUnmerged;
        var operation = "operation-successor-" + stage;
        var terminalPredecessor = new GitHubPublicationPredecessorAuthority(
            "logical-predecessor", pullRequestNumber, generationId, headOid, disposition);
        return GitHubPublicationFactory.CreateAuthority(new(
            predecessor.RepositoryOwner, predecessor.RepositoryName, predecessor.TargetRef,
            expectedBaseOid ?? Oid('9'), predecessor.CampaignLineage, Hash('a'), Hash('d'), Hash('b'), 9,
            Hash('e'), Hash('c'), Hash('f'), Hash('7'), Hash('8'), operation, "generation-2",
            transition, GitHubPublicationTestBatch.ForFiles(),
            [new("docs/readme.md", Hash('5'), Hash('c'), 1, 12, 14, 1, 1)], terminalPredecessor));
    }

    internal static GitHubApiClient Client(
        ValidatedGitHubPublicationAuthority authority,
        CoordinationRemote remote)
    {
        using var registration = (IDisposable)typeof(GitHubTransportTestHook)
            .GetMethod("Register", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [Origin, new RemoteHandler(remote), 30_000])!;
        return GitHubApiClient.Create(authority, GitHubTransportTestHook.Placeholder);
    }

    private static Task<GitHubPreparedCoordination> PrepareContentSuccessorAsync(
        ValidatedGitHubPublicationAuthority authority,
        GitHubCoordinationState claim) => PrepareSuccessorAsync(authority, claim,
            GitHubCoordinationStageUpdate.ContentCreated(Oid('2')), '1');

    private static Task<GitHubPreparedCoordination> PrepareSuccessorAsync(
        ValidatedGitHubPublicationAuthority authority,
        GitHubCoordinationState currentState,
        GitHubCoordinationStageUpdate update,
        char target) => PrepareSuccessorAsync(authority, [currentState], update, target);

    private static async Task<GitHubPreparedCoordination> PrepareSuccessorAsync(
        ValidatedGitHubPublicationAuthority authority,
        IEnumerable<GitHubCoordinationState> currentOperation,
        GitHubCoordinationStageUpdate update,
        char target)
    {
        var template = new CoordinationRemote { TargetHead = Oid(target) };
        template.SeedChain(currentOperation);
        using var client = Client(authority, template);
        var store = GitHubCoordinationStore.Create(client);
        var current = await store.ReadCurrentAsync();
        var advanced = await store.AdvanceAsync(current.State!, update);
        return PrepareFixture(template.State(advanced.State!.HeadOid));
    }

    public enum LostMutation { None, Blob, LeafTree, RootTree, Commit, Ref }
    public enum LostReadback { Exact, Missing, Mismatch }
    public enum ObjectTamper { Blob, LeafTree, RootTree, Parent, Tree, Actor, Time, Message }
    public enum SuccessorObservationWindow { Orphan, FinalRef, CasReadback }

    private sealed record StoredTree(string Oid, ImmutableArray<GitHubTreeEntry> Entries);
    private sealed record StoredCommit(
        string Oid,
        string Tree,
        string Parent,
        string Message,
        GitHubCommitActor Author,
        GitHubCommitActor Committer,
        JsonObject? Signature = null,
        JsonObject? Principal = null);

    private sealed class SyntheticGuard(IGitHubCoordinationStateCapability state)
        : IGitHubCoordinationGuardCapability
    {
        public IGitHubCoordinationStateCapability State { get; } = state;
    }

    internal sealed class CoordinationRemote
    {
        private readonly object gate = new();
        private readonly Dictionary<string, byte[]> blobs = new(StringComparer.Ordinal);
        private readonly Dictionary<string, StoredTree> trees = new(StringComparer.Ordinal);
        private readonly Dictionary<string, StoredCommit> commits = new(StringComparer.Ordinal);
        private readonly Dictionary<string, GitHubPreparedCoordination> prepared = new(StringComparer.Ordinal);
        private TaskCompletionSource? refBarrier;
        private int requiredRefCalls;
        private int arrivedRefCalls;
        private TimeSpan serverClockOffset;
        internal DateTimeOffset ServerNow
        {
            get => DateTimeOffset.UtcNow + serverClockOffset;
            set => serverClockOffset = value - DateTimeOffset.UtcNow;
        }
        internal bool IncludeServerDate { get; set; } = true;
        internal string TargetHead { get; set; } = Oid('1');
        internal string? CoordinationHead { get; private set; }
        internal LostMutation LoseResponse { get; set; }
        internal LostMutation CancelAfterMutation { get; set; }
        internal CancellationTokenSource? CancelSource { get; set; }
        internal LostReadback LostBlobReadback { get; set; }
        internal string? TargetAfterSuccessfulRef { get; set; }
        internal string? TargetAfterCommitMutation { get; set; }
        internal int RefMutationAttempts { get; private set; }
        internal int SuccessfulRefMutations { get; private set; }
        internal int ObjectMutationAttempts { get; private set; }
        internal int BlobReadAttempts { get; private set; }
        internal string? MoveCoordinationHeadBeforeCommitRead { get; set; }
        internal string? MoveCoordinationHeadBeforeCommitReadTo { get; set; }
        internal string? MoveCoordinationHeadBeforeRefRead { get; set; }
        internal string? MoveCoordinationHeadBeforeRefMutation { get; set; }
        internal int MoveCoordinationHeadAfterObjectMutationCount { get; set; } = int.MaxValue;
        internal List<(string Before, string After)> RefUpdates { get; } = [];

        internal void RequireConcurrentRefMutations(int count)
        {
            requiredRefCalls = count;
            refBarrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        internal void ForceCoordinationHead(string head)
        {
            lock (gate) CoordinationHead = head;
        }

        internal void Seed(GitHubCoordinationState state)
        {
            var value = PrepareFixture(state);
            SeedObjects(value);
            CoordinationHead = value.CommitOid;
        }

        internal void SeedChain(IEnumerable<GitHubCoordinationState> states)
        {
            foreach (var state in states) Seed(state);
        }

        internal void SeedObjects(GitHubPreparedCoordination value)
        {
            lock (gate)
            {
                prepared[value.CommitOid] = value;
                FixtureDocuments[value.CommitOid] = value.Document;
                blobs[value.BlobOid] = value.StateBytes.ToArray();
                if (value.Event is { } publication) blobs[value.Document.Publication!.Oid] = publication.Encode();
                trees[value.LeafTreeOid] = new(value.LeafTreeOid,
                    GitHubCoordinationObjects.LeafEntries(value));
                trees[value.RootTreeOid] = new(value.RootTreeOid,
                    GitHubCoordinationObjects.RootEntries(value));
                var request = GitHubCoordinationObjects.CommitRequest(value);
                commits[value.CommitOid] = new(value.CommitOid, value.RootTreeOid,
                    value.ParentOid, value.Message, request.Author, request.Committer);
            }
        }

        internal void AppendDocument(Func<GitHubCoordinationDocument, GitHubCoordinationDocument> change)
        {
            var head = CoordinationHead!;
            var document = change(FixtureDocuments[head].RetainAt(head) with { PredecessorOid = head });
            var value = GitHubCoordinationObjects.Prepare(document);
            SeedObjects(value);
            ForceCoordinationHead(value.CommitOid);
        }

        internal void CorruptBlob(string oid)
        {
            lock (gate) blobs[oid] = [.. blobs[oid], (byte)' '];
        }

        // Server-side content fixture only. Clients must still independently
        // fetch and authenticate all objects before any positive-pool admission.
        internal void SeedProposalContent(GitHubCreateCommit value, byte[] bytes, ImmutableArray<GitHubTreeEntry> entries)
        {
            lock (gate)
            {
                blobs[GitHubCoordinationObjects.ObjectOid("blob", bytes)] = bytes;
                trees[value.TreeOid] = new(value.TreeOid, entries);
                commits[value.ExpectedOid] = new(value.ExpectedOid, value.TreeOid, value.ParentOid,
                    value.Message, value.Author, value.Committer);
            }
        }

        internal string SeedBlob(byte[] bytes)
        {
            var oid = GitHubCoordinationObjects.ObjectOid("blob", bytes);
            lock (gate) blobs[oid] = bytes;
            return oid;
        }

        internal void SeedRaw(
            byte[] stateBytes,
            string operationCommitment,
            string stage,
            string parent)
        {
            using var state = JsonDocument.Parse(stateBytes);
            var root = state.RootElement;
            string Field(string name, string fallback) => root.TryGetProperty(name, out var value) ? value.GetString()! : fallback;
            var predecessor = Field("coordinationPredecessorOid", Oid('0'));
            FixtureDocuments.TryGetValue(predecessor, out var previous);
            var eventBytes = Encoding.UTF8.GetBytes("{\"format\":\"publication-event-v1\",\"state\":"
                + Encoding.UTF8.GetString(stateBytes).TrimEnd('\n') + ",\"previous\":"
                + JsonSerializer.Serialize(previous?.RetainAt(predecessor).Publication,
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) + "}");
            var eventOid = GitObjectOid("blob", eventBytes);
            var document = new GitHubCoordinationDocument(GitHubCoordinationDocument.CurrentFormat,
                Field("repositoryId", "Owner/repo"), Field("targetRef", "refs/heads/main"),
                Field("targetCommitOid", parent), Field("campaignLineage", "campaign-1"),
                predecessor, null, null, null, new(eventOid, GitHubCoordinationDocument.CurrentCommit), null);
            var value = GitHubCoordinationObjects.Prepare(document);
            SeedObjects(value);
            lock (gate)
            {
                blobs[eventOid] = eventBytes;
                CoordinationHead = value.CommitOid;
            }
        }

        internal GitHubCoordinationState State(string head)
        {
            lock (gate)
            {
                var commit = commits[head];
                var root = trees[commit.Tree];
                var leaf = trees[root.Entries.Single().Oid];
                return GitHubPublicationEvent.Decode(blobs[leaf.Entries.Single(entry => entry.Path == GitHubCoordinationObjects.EventPath).Oid]).State;
            }
        }

        internal void Tamper(ObjectTamper kind)
        {
            lock (gate)
            {
                var head = CoordinationHead!;
                var commit = commits[head];
                var root = trees[commit.Tree];
                var leaf = trees[root.Entries.Single().Oid];
                switch (kind)
                {
                    case ObjectTamper.Blob:
                        blobs[leaf.Entries.Single(entry => entry.Path == GitHubCoordinationObjects.StatePath).Oid] = [.. blobs[leaf.Entries.Single(entry => entry.Path == GitHubCoordinationObjects.StatePath).Oid], (byte)' '];
                        break;
                    case ObjectTamper.LeafTree:
                        trees[leaf.Oid] = leaf with
                        {
                            Entries = [.. leaf.Entries, new("unexpected.json", GitHubTreeMode.File,
                                leaf.Entries.Single(entry => entry.Path == GitHubCoordinationObjects.StatePath).Oid, null)],
                        };
                        break;
                    case ObjectTamper.RootTree:
                        trees[root.Oid] = root with
                        {
                            Entries = [.. root.Entries, new("unexpected", GitHubTreeMode.Directory,
                                root.Entries.Single().Oid, null)],
                        };
                        break;
                    case ObjectTamper.Parent:
                        commits[head] = commit with { Parent = Oid('9') };
                        break;
                    case ObjectTamper.Tree:
                        trees[Oid('e')] = root with { Oid = Oid('e') };
                        commits[head] = commit with { Tree = Oid('e') };
                        break;
                    case ObjectTamper.Actor:
                        commits[head] = commit with
                        {
                            Author = commit.Author with { Name = "SomebodyElse" },
                        };
                        break;
                    case ObjectTamper.Time:
                        commits[head] = commit with
                        {
                            Committer = commit.Committer with { Date = commit.Committer.Date.AddSeconds(1) },
                        };
                        break;
                    case ObjectTamper.Message:
                        commits[head] = commit with { Message = commit.Message + "changed\n" };
                        break;
                }
            }
        }

        internal void RemoveCommit(GitHubCoordinationState state)
        { lock (gate) commits.Remove(PrepareFixture(state).CommitOid); }

        internal void RemoveCommit(string actualOid)
        { lock (gate) commits.Remove(actualOid); }

        internal async Task<HttpResponseMessage> Reply(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get) return Get(path);
            using var document = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            if (path.EndsWith("/git/blobs", StringComparison.Ordinal))
                return MutateBlob(document.RootElement);
            if (path.EndsWith("/git/trees", StringComparison.Ordinal))
                return MutateTree(document.RootElement);
            if (path.EndsWith("/git/commits", StringComparison.Ordinal))
                return MutateCommit(document.RootElement);
            if (path == "/graphql")
            {
                if (document.RootElement.GetProperty("query").GetString()!.TrimStart().StartsWith("query", StringComparison.Ordinal))
                    return CheckpointSnapshot(document.RootElement.GetProperty("variables"));
                if (document.RootElement.GetProperty("query").GetString()!.Contains("createCommitOnBranch", StringComparison.Ordinal))
                    return MutateCheckpoint(document.RootElement.GetProperty("variables").GetProperty("input"));
                return await MutateRef(document.RootElement);
            }
            return Json(HttpStatusCode.NotFound, new { message = "Not Found" });
        }

        private HttpResponseMessage Get(string path)
        {
            lock (gate)
            {
                if (path.Equals("/repos/Owner/repo", StringComparison.OrdinalIgnoreCase))
                    return Json(HttpStatusCode.OK, Repository());
                if (path == "/repos/Owner/repo/pulls")
                    return Json(HttpStatusCode.OK, Array.Empty<object>());
                if (path.EndsWith("/git/ref/heads/main", StringComparison.Ordinal))
                    return Ref("refs/heads/main", TargetHead);
                if (path.Contains("/git/ref/heads/contract-scribe/coordination/", StringComparison.Ordinal))
                {
                    if (MoveCoordinationHeadBeforeRefRead is { } moved
                        && ObjectMutationAttempts >= MoveCoordinationHeadAfterObjectMutationCount)
                    {
                        CoordinationHead = moved;
                        MoveCoordinationHeadBeforeRefRead = null;
                    }
                    return CoordinationHead is null
                        ? Json(HttpStatusCode.NotFound, new { message = "Not Found" })
                        : Ref(RefName(path), CoordinationHead);
                }
                var oid = path[(path.LastIndexOf('/') + 1)..];
                if (path.Contains("/git/blobs/", StringComparison.Ordinal) && blobs.TryGetValue(oid, out var bytes))
                {
                    BlobReadAttempts++;
                    return Json(HttpStatusCode.OK, new
                    {
                        sha = oid,
                        size = bytes.Length,
                        encoding = "base64",
                        content = Convert.ToBase64String(bytes),
                    });
                }
                if (path.Contains("/git/trees/", StringComparison.Ordinal) && trees.TryGetValue(oid, out var tree))
                    return Tree(tree);
                if (path.Contains("/git/commits/", StringComparison.Ordinal))
                {
                    if (MoveCoordinationHeadBeforeCommitRead == oid)
                    {
                        CoordinationHead = MoveCoordinationHeadBeforeCommitReadTo ?? oid;
                        MoveCoordinationHeadBeforeCommitRead = null;
                        MoveCoordinationHeadBeforeCommitReadTo = null;
                    }
                    if (commits.TryGetValue(oid, out var commit)) return Commit(commit);
                }
                return Json(HttpStatusCode.NotFound, new { message = "Not Found" });
            }
        }

        private HttpResponseMessage CheckpointSnapshot(JsonElement variables)
        {
            lock (gate)
            {
                var historical = variables.TryGetProperty("anchor", out var anchor);
                var coordination = historical ? anchor.GetString()! : variables.GetProperty("coord").GetString()!;
                if (!historical && MoveCoordinationHeadBeforeRefRead is { } moved && ObjectMutationAttempts >= MoveCoordinationHeadAfterObjectMutationCount)
                {
                    CoordinationHead = moved;
                    MoveCoordinationHeadBeforeRefRead = null;
                }
                var head = historical ? coordination : CoordinationHead;
                var repository = new JsonObject
                {
                    ["id"] = "R_42",
                    ["fullDatabaseId"] = "42",
                    ["name"] = "repo",
                    ["owner"] = new JsonObject { ["login"] = "Owner" },
                    ["base"] = Reference("refs/heads/main", TargetHead, content: false),
                    ["coord"] = historical
                        ? CoordinationHead is null ? null : Reference(variables.GetProperty("coord").GetString()!, CoordinationHead, content: false)
                        : head is null ? null : Reference(coordination, head, content: true),
                };
                if (historical) repository["document"] = Reference(coordination, head!, content: true)["target"]!.DeepClone();
                foreach (var (alias, path) in new[] { ("state", "coordination-state-v1.json"), ("checkpoint", "checkpoint.json"),
                    ("plan", "plan.json"), ("archive", "archive-predecessor.json"), ("event", "publication-event.json") })
                {
                    if (historical && alias != "state") continue;
                    Assert.Equal(coordination + ":.contract-scribe/" + path, variables.GetProperty(alias).GetString());
                    repository[alias] = File(path);
                }
                return Json(HttpStatusCode.OK, new JsonObject { ["data"] = new JsonObject { ["repository"] = repository } });

                JsonObject Reference(string name, string oid, bool content)
                {
                    var target = new JsonObject { ["__typename"] = "Commit", ["oid"] = oid };
                    if (content && commits.TryGetValue(oid, out var commit))
                    {
                        if (MoveCoordinationHeadBeforeCommitRead == oid)
                        {
                            CoordinationHead = MoveCoordinationHeadBeforeCommitReadTo ?? oid;
                            MoveCoordinationHeadBeforeCommitRead = MoveCoordinationHeadBeforeCommitReadTo = null;
                        }
                        var author = JsonSerializer.SerializeToNode(Actor(commit.Author))!;
                        author["user"] = commit.Principal?.DeepClone();
                        target["message"] = commit.Message;
                        target["author"] = author;
                        target["committer"] = JsonSerializer.SerializeToNode(Actor(commit.Committer));
                        target["signature"] = commit.Signature?.DeepClone();
                        target["parents"] = new JsonObject
                        {
                            ["totalCount"] = 1,
                            ["pageInfo"] = new JsonObject { ["hasNextPage"] = false },
                            ["nodes"] = new JsonArray(new JsonObject { ["oid"] = commit.Parent }),
                        };
                        target["tree"] = Tree(commit.Tree, children: true);
                    }
                    return new JsonObject
                    {
                        ["id"] = "REF_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..12],
                        ["prefix"] = "refs/heads/",
                        ["name"] = name[11..],
                        ["target"] = target,
                    };
                }

                JsonObject Tree(string oid, bool children)
                {
                    var entries = new JsonArray();
                    foreach (var entry in trees[oid].Entries)
                    {
                        var value = new JsonObject { ["name"] = entry.Path, ["mode"] = entry.Mode == GitHubTreeMode.Directory ? 16384 : 33188, ["oid"] = entry.Oid };
                        if (children) value["object"] = entry.Mode == GitHubTreeMode.Directory
                            ? Tree(entry.Oid, children: false) : new JsonObject { ["__typename"] = "Blob" };
                        entries.Add(value);
                    }
                    return new JsonObject { ["__typename"] = "Tree", ["oid"] = oid, ["entries"] = entries };
                }

                JsonObject? File(string path)
                {
                    if (head is null || !commits.TryGetValue(head, out var commit) || !trees.TryGetValue(commit.Tree, out var root)) return null;
                    var directory = root.Entries.SingleOrDefault(entry => entry.Path == ".contract-scribe" && entry.Mode == GitHubTreeMode.Directory);
                    if (directory is null || !trees.TryGetValue(directory.Oid, out var leaf)) return null;
                    var entry = leaf.Entries.SingleOrDefault(item => item.Path == path);
                    if (entry is null || !blobs.TryGetValue(entry.Oid, out var bytes)) return null;
                    BlobReadAttempts++;
                    return new JsonObject
                    {
                        ["__typename"] = "Blob",
                        ["oid"] = entry.Oid,
                        ["byteSize"] = bytes.Length,
                        ["isBinary"] = false,
                        ["isTruncated"] = false,
                        ["text"] = Encoding.UTF8.GetString(bytes),
                    };
                }
            }
        }

        private HttpResponseMessage MutateCheckpoint(JsonElement input)
        {
            lock (gate)
            {
                RefMutationAttempts++;
                var before = input.GetProperty("expectedHeadOid").GetString()!;
                var reference = "refs/heads/contract-scribe/coordination/" + GitHubPublicationFactory.CreateCoordinationRef(
                    GitHubPublicationFactory.CreateCampaignReadAuthority("Owner", "repo", "refs/heads/main", FixtureDocuments[before].CampaignLineage)).Split('/')[^1];
                Assert.Equal("REF_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(reference)))[..12],
                    input.GetProperty("branch").GetProperty("id").GetString());
                if (MoveCoordinationHeadBeforeRefMutation is { } moved)
                { CoordinationHead = moved; MoveCoordinationHeadBeforeRefMutation = null; }
                if (CoordinationHead != before)
                    return Json(HttpStatusCode.OK, new { data = (object?)null, errors = new[] { new { type = "CONFLICT", message = "synthetic conflict" } } });
                var parent = commits[before];
                var priorRoot = trees[parent.Tree];
                var leaf = trees[priorRoot.Entries.Single().Oid].Entries.ToDictionary(entry => entry.Path, StringComparer.Ordinal);
                foreach (var file in input.GetProperty("fileChanges").GetProperty("additions").EnumerateArray())
                {
                    var path = file.GetProperty("path").GetString()!;
                    Assert.StartsWith(".contract-scribe/", path);
                    var bytes = Convert.FromBase64String(file.GetProperty("contents").GetString()!);
                    var blob = GitHubCoordinationObjects.ObjectOid("blob", bytes);
                    blobs[blob] = bytes;
                    leaf[path[17..]] = new(path[17..], GitHubTreeMode.File, blob, null);
                }
                var entries = leaf.Values.OrderBy(entry => entry.Path, StringComparer.Ordinal).ToImmutableArray();
                var leafOid = GitHubCoordinationObjects.TreeOid(entries);
                trees[leafOid] = new(leafOid, entries);
                ImmutableArray<GitHubTreeEntry> roots = [new(".contract-scribe", GitHubTreeMode.Directory, leafOid, null)];
                var rootOid = GitHubCoordinationObjects.TreeOid(roots);
                trees[rootOid] = new(rootOid, roots);
                var message = input.GetProperty("message");
                var text = message.GetProperty("headline").GetString() + "\n\n" + message.GetProperty("body").GetString();
                var seconds = ServerNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
                var payload = "tree " + rootOid + "\nparent " + before
                    + "\nauthor github-actions[bot] <41898282+github-actions[bot]@users.noreply.github.com> " + seconds
                    + " +0000\ncommitter GitHub <noreply@github.com> " + seconds + " +0000\n\n" + text;
                const string signature = "-----BEGIN PGP SIGNATURE-----\nsynthetic-unit-fixture\n-----END PGP SIGNATURE-----\n";
                var separator = payload.IndexOf("\n\n", StringComparison.Ordinal);
                var raw = payload[..(separator + 1)] + "gpgsig " + signature.TrimEnd('\n').Replace("\n", "\n ", StringComparison.Ordinal)
                    + "\n" + payload[(separator + 1)..];
                var oid = GitHubCoordinationObjects.ObjectOid("commit", Encoding.UTF8.GetBytes(raw));
                commits[oid] = new(oid, rootOid, before, text,
                    new("github-actions[bot]", "41898282+github-actions[bot]@users.noreply.github.com", ServerNow),
                    new("GitHub", "noreply@github.com", ServerNow),
                    new JsonObject
                    {
                        ["__typename"] = "GpgSignature",
                        ["isValid"] = true,
                        ["state"] = "VALID",
                        ["wasSignedByGitHub"] = true,
                        ["payload"] = payload,
                        ["signature"] = signature
                    },
                    new JsonObject { ["__typename"] = "User", ["id"] = "MDM6Qm90NDE4OTgyODI=", ["databaseId"] = 41898282, ["login"] = "github-actions[bot]" });
                FixtureDocuments[oid] = GitHubCoordinationDocument.Decode(blobs[leaf[GitHubCoordinationObjects.StatePath].Oid]);
                if (LoseResponse == LostMutation.Ref && LostBlobReadback != LostReadback.Exact)
                {
                    var checkpoint = leaf.GetValueOrDefault(GitHubCoordinationObjects.CheckpointPath);
                    if (checkpoint is not null)
                    {
                        if (LostBlobReadback == LostReadback.Missing) blobs.Remove(checkpoint.Oid);
                        else blobs[checkpoint.Oid] = [.. blobs[checkpoint.Oid], (byte)' '];
                    }
                }
                RefUpdates.Add((before, oid));
                CoordinationHead = oid;
                SuccessfulRefMutations++;
                if (TargetAfterSuccessfulRef is { } target) { TargetHead = target; TargetAfterSuccessfulRef = null; }
                return MutationResponse(LostMutation.Ref,
                    new { data = new { createCommitOnBranch = new { clientMutationId = input.GetProperty("clientMutationId").GetString() } } }, HttpStatusCode.OK);
            }
        }

        private HttpResponseMessage MutateBlob(JsonElement body)
        {
            var bytes = Convert.FromBase64String(body.GetProperty("content").GetString()!);
            var oid = GitHubCoordinationObjects.ObjectOid("blob", bytes);
            lock (gate)
            {
                ObjectMutationAttempts++;
                blobs[oid] = bytes;
                try
                {
                    var value = GitHubCoordinationObjects.Prepare(GitHubCoordinationDocument.Decode(bytes));
                    prepared[value.CommitOid] = value;
                    FixtureDocuments[value.CommitOid] = value.Document;
                }
                catch (GitHubCoordinationException) { }
                if (LoseResponse == LostMutation.Blob)
                {
                    if (LostBlobReadback == LostReadback.Missing) blobs.Remove(oid);
                    else if (LostBlobReadback == LostReadback.Mismatch) blobs[oid] = [.. bytes, (byte)' '];
                }
            }
            return MutationResponse(LostMutation.Blob, new { sha = oid }, HttpStatusCode.Created);
        }

        private HttpResponseMessage MutateTree(JsonElement body)
        {
            var entries = body.GetProperty("tree").EnumerateArray().Select(entry => new GitHubTreeEntry(
                entry.GetProperty("path").GetString()!, Mode(entry.GetProperty("mode").GetString()!),
                entry.GetProperty("sha").GetString()!, null)).ToImmutableArray();
            var leaf = entries.Any(entry => entry.Path == GitHubCoordinationObjects.StatePath);
            var oid = GitHubCoordinationObjects.TreeOid(entries);
            var stored = new StoredTree(oid, entries);
            lock (gate) { ObjectMutationAttempts++; trees[oid] = stored; }
            return MutationResponse(leaf ? LostMutation.LeafTree : LostMutation.RootTree, TreeValue(stored), HttpStatusCode.Created);
        }

        private HttpResponseMessage MutateCommit(JsonElement body)
        {
            var root = body.GetProperty("tree").GetString()!;
            GitHubPreparedCoordination value;
            StoredCommit commit;
            lock (gate)
            {
                value = prepared.Values.Single(item => item.RootTreeOid == root);
                var request = GitHubCoordinationObjects.CommitRequest(value);
                commit = new(value.CommitOid, root, value.ParentOid, value.Message,
                    request.Author, request.Committer);
                ObjectMutationAttempts++;
                commits[value.CommitOid] = commit;
                if (TargetAfterCommitMutation is { } moved)
                {
                    TargetHead = moved;
                    TargetAfterCommitMutation = null;
                }
            }
            return MutationResponse(LostMutation.Commit, CommitValue(commit), HttpStatusCode.Created);
        }

        private async Task<HttpResponseMessage> MutateRef(JsonElement body)
        {
            var input = body.GetProperty("variables").GetProperty("input");
            var mutation = input.GetProperty("refUpdates")[0];
            var before = mutation.GetProperty("beforeOid").GetString()!;
            var after = mutation.GetProperty("afterOid").GetString()!;
            var mutationId = input.GetProperty("clientMutationId").GetString()!;
            Task? wait = null;
            lock (gate)
            {
                RefMutationAttempts++;
                RefUpdates.Add((before, after));
                if (requiredRefCalls > 0 && ++arrivedRefCalls == requiredRefCalls)
                    refBarrier!.TrySetResult();
                if (requiredRefCalls > 0) wait = refBarrier!.Task;
            }
            if (wait is not null) await wait;
            lock (gate)
            {
                if (MoveCoordinationHeadBeforeRefMutation is { } concurrentHead)
                {
                    CoordinationHead = concurrentHead;
                    MoveCoordinationHeadBeforeRefMutation = null;
                }
                var matches = before == Oid('0') ? CoordinationHead is null : CoordinationHead == before;
                if (!matches)
                    return Json(HttpStatusCode.OK, new
                    {
                        data = (object?)null,
                        errors = new[] { new { message = "conflict", type = "CONFLICT" } },
                    });
                CoordinationHead = after;
                SuccessfulRefMutations++;
                if (TargetAfterSuccessfulRef is { } moved)
                {
                    TargetHead = moved;
                    TargetAfterSuccessfulRef = null;
                }
            }
            return MutationResponse(LostMutation.Ref,
                new { data = new { updateRefs = new { clientMutationId = mutationId } } },
                HttpStatusCode.OK);
        }

        private HttpResponseMessage MutationResponse(LostMutation kind, object value, HttpStatusCode status)
        {
            if (CancelAfterMutation == kind)
            {
                CancelAfterMutation = LostMutation.None;
                CancelSource!.Cancel();
                throw new OperationCanceledException(CancelSource.Token);
            }
            if (LoseResponse == kind)
            {
                LoseResponse = LostMutation.None;
                throw new IOException("synthetic response loss");
            }
            return Json(status, value);
        }

        private static object Repository() => new
        {
            id = 42,
            node_id = "R_42",
            name = "repo",
            full_name = "Owner/repo",
            @private = true,
            archived = false,
            disabled = false,
            owner = new { id = 7, node_id = "U_7", login = "Owner", type = "User" },
        };

        private static HttpResponseMessage Ref(string name, string oid) => Json(HttpStatusCode.OK, new
        {
            @ref = name,
            node_id = "REF_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..12],
            @object = new { type = "commit", sha = oid },
        });

        private static string RefName(string path) => "refs/" + Uri.UnescapeDataString(
            path[(path.IndexOf("/git/ref/", StringComparison.Ordinal) + 9)..]);

        private static HttpResponseMessage Tree(StoredTree tree) =>
            Json(HttpStatusCode.OK, TreeValue(tree));

        private static object TreeValue(StoredTree tree) => new
        {
            sha = tree.Oid,
            truncated = false,
            tree = tree.Entries.Select(entry => new
            {
                path = entry.Path,
                mode = entry.Mode == GitHubTreeMode.Directory ? "040000" : "100644",
                type = entry.Mode == GitHubTreeMode.Directory ? "tree" : "blob",
                sha = entry.Oid,
            }).ToArray(),
        };

        private static HttpResponseMessage Commit(StoredCommit commit) =>
            Json(HttpStatusCode.OK, CommitValue(commit));

        private static object CommitValue(StoredCommit commit) => new
        {
            sha = commit.Oid,
            tree = new { sha = commit.Tree },
            parents = new[] { new { sha = commit.Parent } },
            // GitHub's response omits the final LF; stored raw bytes still determine the OID.
            message = commit.Message.EndsWith('\n') ? commit.Message[..^1] : commit.Message,
            author = Actor(commit.Author),
            committer = Actor(commit.Committer),
        };

        private static object Actor(GitHubCommitActor actor) => new
        {
            name = actor.Name,
            email = actor.Email,
            date = actor.Date.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
        };

        private static GitHubTreeMode Mode(string mode) => mode switch
        {
            "100644" => GitHubTreeMode.File,
            "040000" => GitHubTreeMode.Directory,
            _ => throw new InvalidOperationException(),
        };

        private static HttpResponseMessage Json(HttpStatusCode status, object value) => new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
        };
    }

    private sealed class RemoteHandler(CoordinationRemote remote) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = await remote.Reply(request);
            if (remote.IncludeServerDate) response.Headers.Date = remote.ServerNow;
            return response;
        }
    }
}
