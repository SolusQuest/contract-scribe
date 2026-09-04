using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Reflection;
using ContractScribe.Core;
using ContractScribe.GitHub.Coordination;
using ContractScribe.GitHub.Transport;

namespace ContractScribe.Tests;

public sealed partial class GitHubCoordinationRefTests
{
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
                var prepared = GitHubCoordinationObjects.Prepare(state);

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

        Assert.Equal(r1Commits["ref-partial"],
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
        var proposal = Decode(r1Vectors["ref-partial"]);
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
            .Replace("{\"version\":1,\"stage\":\"claimed\"", "{\"stage\":\"claimed\",\"version\":1", StringComparison.Ordinal));
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
            (GitHubCoordinationStage.ProposalRefAdvanced, GitHubCoordinationStage.PullRequestCreated),
            (GitHubCoordinationStage.ProposalRefAdvanced, GitHubCoordinationStage.Published),
            (GitHubCoordinationStage.ProposalRefAdvanced, GitHubCoordinationStage.StaleDraft),
            (GitHubCoordinationStage.ProposalRefAdvanced, GitHubCoordinationStage.Stale),
            (GitHubCoordinationStage.PullRequestCreated, GitHubCoordinationStage.Published),
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
            CampaignStateContract.MaximumPatchBytes, claim.CumulativeChangedFiles);
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
            source.PolicyCommitmentSha256, source.OperationId,
            source.OperationCommitmentSha256, source.CurrentCandidateCommitmentSha256,
            source.PrecedingOperationId, source.PrecedingAuthorityCommitmentSha256,
            source.PrecedingCandidateCommitmentSha256, source.GenerationId,
            source.Transition, source.CoordinationPredecessorOid, source.ContentCommitOid,
            source.ProposalRefOid, source.ProposalCommitOid, source.ProposalTreeOid,
            source.PullRequestCreationOperationCommitmentSha256, source.PullRequestNumber,
            source.ExpectedBaseOid, source.ObservedBaseOid, source.OwnershipMarkerSha256,
            blocks, bytes, files);

    private static GitHubCoordinationState WithStageValue(
        GitHubCoordinationState source,
        GitHubCoordinationStage stage) => new(
            stage, source.RepositoryId, source.TargetRef, source.TargetCommitOid,
            source.SnapshotCommitmentSha256, source.AuthorityCommitmentSha256,
            source.PolicyCommitmentSha256, source.OperationId,
            source.OperationCommitmentSha256, source.CurrentCandidateCommitmentSha256,
            source.PrecedingOperationId, source.PrecedingAuthorityCommitmentSha256,
            source.PrecedingCandidateCommitmentSha256, source.GenerationId,
            source.Transition, source.CoordinationPredecessorOid, source.ContentCommitOid,
            source.ProposalRefOid, source.ProposalCommitOid, source.ProposalTreeOid,
            source.PullRequestCreationOperationCommitmentSha256, source.PullRequestNumber,
            source.ExpectedBaseOid, source.ObservedBaseOid, source.OwnershipMarkerSha256,
            source.CumulativeDocumentationBlocks, source.CumulativePatchBytes,
            source.CumulativeChangedFiles);

    private static ValidatedGitHubPublicationAuthority Authority() =>
        GitHubPublicationFactory.CreateAuthority(new(
            "Owner", "repo", "refs/heads/main", new string('1', 40), "campaign-1",
            new string('1', 64), new string('2', 64), new string('3', 64), 7,
            new string('4', 64), new string('5', 64), new string('6', 64),
            new string('7', 64), new string('8', 64), "operation-1", "generation-1",
            null, null, null, null, null, null, null, GitHubPublicationTransitionKind.Initial,
            new(10, 10, 1000), new(10, 10, 1000),
            [new("docs/readme.md", new string('a', 64), new string('5', 64), 1, 10, 12, 1, 1)], []));

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
        Assert.Equal(4, remote.ObjectMutationAttempts);
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
    public async Task Competing_append_claims_use_the_same_nonzero_predecessor_and_only_one_wins()
    {
        var remote = new CoordinationRemote();
        var predecessorAuthority = InitialAuthority("operation-1", '5');
        var predecessor = Published(predecessorAuthority);
        remote.SeedChain(PublishedChain(predecessorAuthority));
        var firstAuthority = AppendAuthority(predecessorAuthority, "operation-2", '6');
        var secondAuthority = AppendAuthority(predecessorAuthority, "operation-3", '7');
        using var firstClient = Client(firstAuthority, remote);
        using var secondClient = Client(secondAuthority, remote);
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
        Assert.All(remote.RefUpdates, update => Assert.Equal(
            GitHubCoordinationObjects.Prepare(predecessor).CommitOid, update.Before));
        Assert.DoesNotContain(remote.RefUpdates, update => update.After == Oid('0'));
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
        Assert.Equal(4, remote.ObjectMutationAttempts);
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
        var expected = GitHubCoordinationObjects.Prepare(
            GitHubCoordinationCodec.CreateClaim(authority, Oid('0')));
        remote.SeedObjects(expected);

        var result = await store.ClaimAsync(absence.Read!);

        Assert.Equal(GitHubCoordinationOutcome.Failed, result.Outcome);
        Assert.Equal(GitHubCoordinationFailureKind.Unresolved, result.Failure!.Kind);
        Assert.Equal(0, remote.RefMutationAttempts);
        Assert.Equal(0, remote.ObjectMutationAttempts);
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
        var proposal = chain[2];
        var staleDraft = GitHubCoordinationCodec.WithStage(proposal,
            GitHubCoordinationStage.StaleDraft,
            GitHubCoordinationObjects.Prepare(proposal).CommitOid, proposal.ContentCommitOid,
            proposal.ProposalRefOid, proposal.ProposalCommitOid, proposal.ProposalTreeOid,
            Published(predecessor).PullRequestCreationOperationCommitmentSha256, 17,
            Oid('1'), Oid('9'), Published(predecessor).OwnershipMarkerSha256);
        var remote = new CoordinationRemote { TargetHead = Oid('9') };
        remote.SeedChain([chain[0], chain[1], proposal, staleDraft]);
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
        Assert.Equal(authority.PolicyCommitmentSha256, snapshot.PolicyCommitmentSha256);
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
    public async Task Missing_append_predecessor_and_cancelled_reads_fail_before_mutation()
    {
        var predecessor = InitialAuthority("operation-predecessor", '5');
        var remote = new CoordinationRemote();
        using var appendClient = Client(AppendAuthority(predecessor, "operation-append", '6'), remote);
        var missing = await GitHubCoordinationStore.Create(appendClient).ReadCurrentAsync();
        Assert.Equal(GitHubCoordinationFailureKind.MissingPredecessor, missing.Failure!.Kind);

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
        using var client = Client(AppendAuthority(predecessorAuthority, "operation-rewind-new", '6'), remote);
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
    public async Task Proposal_state_accepts_both_normative_direct_PR_results(
        int resultStageValue,
        char observedBase)
    {
        var resultStage = (GitHubCoordinationStage)resultStageValue;
        var remote = new CoordinationRemote();
        var authority = InitialAuthority("operation-pr-result", '5');
        using var client = Client(authority, remote);
        var store = GitHubCoordinationStore.Create(client);
        var absence = await store.ReadCurrentAsync();
        var claim = await store.ClaimAsync(absence.Read!);
        var content = await store.AdvanceAsync(claim.State!,
            GitHubCoordinationStageUpdate.ContentCreated(Oid('2')));
        var proposal = await store.AdvanceAsync(content.State!,
            GitHubCoordinationStageUpdate.ProposalRefAdvanced(Oid('2'), Oid('3')));
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
        await AssertAdvanceRejected(authority, chain[..3],
            GitHubCoordinationStageUpdate.PullRequestResult(
                GitHubCoordinationStage.Published, Oid('2'), Oid('4'),
                replacementCreation, 17, Oid('1'), Oid('1'),
                OwnershipMarker(replacementCreation)));

        var replacementProposal = PullRequestCreationCommitment(chain[0],
            GitHubPublicationFactory.CreateProposalRef(authority), Oid('4'), Oid('3'));
        await AssertAdvanceRejected(authority, chain[..3],
            GitHubCoordinationStageUpdate.PullRequestResult(
                GitHubCoordinationStage.Published, Oid('4'), Oid('3'),
                replacementProposal, 17, Oid('1'), Oid('1'),
                OwnershipMarker(replacementProposal)));

        var created = GitHubCoordinationCodec.WithStage(chain[2],
            GitHubCoordinationStage.PullRequestCreated,
            GitHubCoordinationObjects.Prepare(chain[2]).CommitOid,
            chain[3].ContentCommitOid, chain[3].ProposalRefOid,
            chain[3].ProposalCommitOid, chain[3].ProposalTreeOid,
            chain[3].PullRequestCreationOperationCommitmentSha256, 17,
            Oid('1'), Oid('1'), chain[3].OwnershipMarkerSha256);
        await AssertAdvanceRejected(authority, [.. chain[..3], created],
            GitHubCoordinationStageUpdate.PullRequestResult(
                GitHubCoordinationStage.Published, Oid('2'), Oid('3'),
                chain[3].PullRequestCreationOperationCommitmentSha256!, 18,
                Oid('1'), Oid('1'), chain[3].OwnershipMarkerSha256!));
        var replacementPr = Hash('c');
        await AssertAdvanceRejected(authority, [.. chain[..3], created],
            GitHubCoordinationStageUpdate.PullRequestResult(
                GitHubCoordinationStage.Published, Oid('2'), Oid('3'),
                replacementPr, 17, Oid('1'), Oid('1'),
                OwnershipMarker(replacementPr)));
        await AssertAdvanceRejected(authority, [.. chain[..3], created],
            GitHubCoordinationStageUpdate.PullRequestResult(
                GitHubCoordinationStage.Published, Oid('2'), Oid('3'),
                chain[3].PullRequestCreationOperationCommitmentSha256!, 17,
                Oid('1'), Oid('1'), Hash('d')));
        await AssertAdvanceRejected(authority, [.. chain[..3], created],
            GitHubCoordinationStageUpdate.PullRequestResult(
                GitHubCoordinationStage.Published, Oid('2'), Oid('3'),
                chain[3].PullRequestCreationOperationCommitmentSha256!, 17,
                Oid('9'), Oid('9'), chain[3].OwnershipMarkerSha256!));
    }

    [Fact]
    public async Task PR_commitment_marker_and_expected_base_are_authenticated_before_any_write()
    {
        var authority = InitialAuthority("operation-pr-auth", '5');
        var chain = PublishedChain(authority);
        var creation = chain[3].PullRequestCreationOperationCommitmentSha256!;

        await AssertAdvanceRejected(authority, chain[..3],
            GitHubCoordinationStageUpdate.PullRequestResult(
                GitHubCoordinationStage.Published, Oid('2'), Oid('3'),
                Hash('a'), 17, Oid('1'), Oid('1'), OwnershipMarker(Hash('a'))));
        await AssertAdvanceRejected(authority, chain[..3],
            GitHubCoordinationStageUpdate.PullRequestResult(
                GitHubCoordinationStage.Published, Oid('2'), Oid('3'),
                creation, 17, Oid('1'), Oid('1'), Hash('b')));
        await AssertAdvanceRejected(authority, chain[..3],
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
        var skipped = GitHubCoordinationCodec.WithStage(chain[2],
            GitHubCoordinationStage.AwaitingReview,
            GitHubCoordinationObjects.Prepare(chain[2]).CommitOid, published.ContentCommitOid,
            published.ProposalRefOid, published.ProposalCommitOid, published.ProposalTreeOid,
            published.PullRequestCreationOperationCommitmentSha256, published.PullRequestNumber,
            published.ExpectedBaseOid, published.ObservedBaseOid, published.OwnershipMarkerSha256);

        var initialRemote = new CoordinationRemote();
        initialRemote.Seed(impossibleInitial);
        using var initialClient = Client(authority, initialRemote);
        var initialRead = await GitHubCoordinationStore.Create(initialClient).ReadCurrentAsync();
        Assert.Equal(GitHubCoordinationFailureKind.ObjectMismatch, initialRead.Failure!.Kind);

        var skippedRemote = new CoordinationRemote();
        skippedRemote.SeedChain([.. chain[..3], skipped]);
        using var skippedClient = Client(authority, skippedRemote);
        var skippedRead = await GitHubCoordinationStore.Create(skippedClient).ReadCurrentAsync();
        Assert.Equal(GitHubCoordinationFailureKind.ObjectMismatch, skippedRead.Failure!.Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Later_partial_cannot_hide_an_append_claim_with_a_zero_predecessor(
        bool includeProposal)
    {
        var predecessorAuthority = InitialAuthority("operation-hidden-root-a", '5');
        var authority = AppendAuthority(predecessorAuthority, "operation-hidden-root-b", '6');
        var invalidRoot = GitHubCoordinationCodec.CreateClaim(authority, Oid('0'));
        var content = GitHubCoordinationCodec.WithStage(invalidRoot,
            GitHubCoordinationStage.ContentCreated,
            GitHubCoordinationObjects.Prepare(invalidRoot).CommitOid, Oid('2'));
        var proposal = GitHubCoordinationCodec.WithStage(content,
            GitHubCoordinationStage.ProposalRefAdvanced,
            GitHubCoordinationObjects.Prepare(content).CommitOid,
            Oid('2'), Oid('2'), Oid('2'), Oid('3'));
        var remote = new CoordinationRemote();
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
        Assert.Equal(6, maximum.GetRawConstantValue());
    }

    [Fact]
    public async Task Restarted_append_replay_authenticates_the_complete_preceding_path_map()
    {
        var predecessorAuthority = InitialAuthority("operation-map-a", '5');
        var predecessor = PublishedChain(predecessorAuthority);
        var authority = AppendAuthority(predecessorAuthority, "operation-map-b", '6',
            precedingFileCandidate: '9');
        var root = GitHubCoordinationCodec.CreateClaim(authority,
            GitHubCoordinationObjects.Prepare(predecessor[^1]).CommitOid);
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
            GitHubCoordinationObjects.Prepare(published).CommitOid,
            published.ContentCommitOid, published.ProposalRefOid,
            published.ProposalCommitOid, published.ProposalTreeOid,
            published.PullRequestCreationOperationCommitmentSha256,
            published.PullRequestNumber, published.ExpectedBaseOid,
            published.ObservedBaseOid, published.OwnershipMarkerSha256);
        var authority = SuccessorAuthority(predecessorAuthority, terminal, terminalStage);
        var substitutedParent = GitHubCoordinationCodec.WithStage(published, terminalStage,
            GitHubCoordinationObjects.Prepare(published).CommitOid,
            published.ContentCommitOid, published.ProposalRefOid,
            published.ProposalCommitOid, published.ProposalTreeOid,
            published.PullRequestCreationOperationCommitmentSha256,
            published.PullRequestNumber!.Value + 1, published.ExpectedBaseOid,
            published.ObservedBaseOid, published.OwnershipMarkerSha256);
        var root = GitHubCoordinationCodec.CreateClaim(authority,
            GitHubCoordinationObjects.Prepare(substitutedParent).CommitOid);
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
        var claimHead = GitHubCoordinationObjects.Prepare(claim).CommitOid;
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
            GitHubCoordinationObjects.Prepare(claim).CommitOid,
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
        prRemote.SeedRaw(wrongBase, published[3].OperationCommitmentSha256,
            "published", GitHubCoordinationObjects.Prepare(proposal).CommitOid);
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
            AppendAuthority(predecessorAuthority, "operation-base-b", '6', '9'), remote);
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
        var predecessorAuthority = InitialAuthority("operation-case-a", '5');
        var remote = new CoordinationRemote();
        remote.SeedChain(PublishedChain(predecessorAuthority));
        using var client = Client(AppendAuthority(
            predecessorAuthority, "operation-case-b", '6', alternateRepositoryCase: true), remote);
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

    private static GitHubCoordinationState Published(ValidatedGitHubPublicationAuthority authority)
        => PublishedChain(authority)[^1];

    private static ImmutableArray<GitHubCoordinationState> PublishedChain(
        ValidatedGitHubPublicationAuthority authority)
    {
        var claim = GitHubCoordinationCodec.CreateClaim(authority, Oid('0'));
        var proposalOid = Oid('2');
        var treeOid = Oid('3');
        var content = GitHubCoordinationCodec.WithStage(claim,
            GitHubCoordinationStage.ContentCreated,
            GitHubCoordinationObjects.Prepare(claim).CommitOid, proposalOid);
        var proposal = GitHubCoordinationCodec.WithStage(content,
            GitHubCoordinationStage.ProposalRefAdvanced,
            GitHubCoordinationObjects.Prepare(content).CommitOid,
            proposalOid, proposalOid, proposalOid, treeOid);
        var creation = PullRequestCreationCommitment(claim,
            GitHubPublicationFactory.CreateProposalRef(authority), proposalOid, treeOid);
        var marker = OwnershipMarker(creation);
        var published = GitHubCoordinationCodec.WithStage(proposal,
            GitHubCoordinationStage.Published,
            GitHubCoordinationObjects.Prepare(proposal).CommitOid,
            proposalOid, proposalOid, proposalOid, treeOid, creation, 17,
            Oid('1'), Oid('1'), marker);
        return [claim, content, proposal, published];
    }

    private static ImmutableArray<GitHubCoordinationState> StaleDraftChain(
        ValidatedGitHubPublicationAuthority authority)
    {
        var published = PublishedChain(authority);
        var proposal = published[2];
        var staleDraft = GitHubCoordinationCodec.WithStage(proposal,
            GitHubCoordinationStage.StaleDraft,
            GitHubCoordinationObjects.Prepare(proposal).CommitOid,
            published[3].ContentCommitOid, published[3].ProposalRefOid,
            published[3].ProposalCommitOid, published[3].ProposalTreeOid,
            published[3].PullRequestCreationOperationCommitmentSha256,
            published[3].PullRequestNumber, Oid('1'), Oid('9'),
            published[3].OwnershipMarkerSha256);
        return [.. published[..3], staleDraft];
    }

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

    private static ValidatedGitHubPublicationAuthority InitialAuthority(
        string operation,
        char candidate,
        string generation = "generation-1",
        char target = '1') =>
        GitHubPublicationFactory.CreateAuthority(new(
            "Owner", "repo", "refs/heads/main", Oid(target), "campaign-1", Hash('1'), Hash('2'), Hash('3'), 7,
            Hash('4'), Hash(candidate), Hash('6'), Hash('7'), Hash('8'), operation, generation,
            null, null, null, null, null, null, null, GitHubPublicationTransitionKind.Initial,
            new(10, 10, 1000), new(10, 10, 1000),
            [new("docs/readme.md", Hash('a'), Hash(candidate), 1, 10, 12, 1, 1)], []));

    private static ValidatedGitHubPublicationAuthority AppendAuthority(
        ValidatedGitHubPublicationAuthority predecessor,
        string operation,
        char candidate,
        char? target = null,
        bool alternateRepositoryCase = false,
        char precedingFileCandidate = '5') => GitHubPublicationFactory.CreateAuthority(new(
            alternateRepositoryCase ? predecessor.RepositoryOwner.ToLowerInvariant() : predecessor.RepositoryOwner,
            alternateRepositoryCase ? predecessor.RepositoryName.ToUpperInvariant() : predecessor.RepositoryName,
            predecessor.TargetRef,
            target is { } targetValue ? Oid(targetValue) : predecessor.ExpectedBaseCommitOid,
            predecessor.CampaignLineage,
            predecessor.SnapshotCommitmentSha256, Hash('2'), Hash('3'), 8, Hash('4'),
            Hash(candidate), Hash('6'), Hash('7'), Hash('8'), operation, predecessor.GenerationId,
            predecessor.OperationId, predecessor.AuthorityCommitmentSha256,
            predecessor.CandidateCommitmentSha256, predecessor.GenerationId,
            predecessor.SnapshotCommitmentSha256, predecessor.PolicyCommitmentSha256, null,
            GitHubPublicationTransitionKind.SameSnapshotAppend,
            predecessor.AcceptedM4Ceilings, predecessor.Policy,
            [new("docs/readme.md", Hash('5'), Hash(candidate), 1, 12, 14, 1, 1)],
            [new("docs/readme.md", Hash(precedingFileCandidate))]));

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
        GitHubCoordinationStage stage)
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
        var closedAuthorization = stage == GitHubCoordinationStage.ClosedUnmerged
            ? new GitHubClosedUnmergedSuccessorAuthorization(
                "authorization-1", terminalPredecessor.LogicalPredecessorId,
                terminalPredecessor.PullRequestNumber, terminalPredecessor.GenerationId,
                terminalPredecessor.HeadOid, Hash('a'), Hash('b'), Hash('c'),
                "generation-2", operation)
            : null;
        return GitHubPublicationFactory.CreateAuthority(new(
            predecessor.RepositoryOwner, predecessor.RepositoryName, predecessor.TargetRef,
            Oid('9'), predecessor.CampaignLineage, Hash('a'), Hash('d'), Hash('b'), 9,
            Hash('e'), Hash('c'), Hash('f'), Hash('7'), Hash('8'), operation, "generation-2",
            null, null, null, null, null, null, terminalPredecessor, transition,
            predecessor.AcceptedM4Ceilings, predecessor.Policy,
            [new("docs/readme.md", Hash('5'), Hash('c'), 1, 12, 14, 1, 1)], [],
            closedAuthorization));
    }

    private static GitHubApiClient Client(
        ValidatedGitHubPublicationAuthority authority,
        CoordinationRemote remote)
    {
        using var registration = (IDisposable)typeof(GitHubTransportTestHook)
            .GetMethod("Register", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [Origin, new RemoteHandler(remote), 30_000])!;
        return GitHubApiClient.Create(authority, GitHubTransportTestHook.Placeholder);
    }

    public enum LostMutation { None, Blob, LeafTree, RootTree, Commit, Ref }
    public enum LostReadback { Exact, Missing, Mismatch }
    public enum ObjectTamper { Blob, LeafTree, RootTree, Parent, Tree, Actor, Time, Message }

    private sealed record StoredTree(string Oid, ImmutableArray<GitHubTreeEntry> Entries);
    private sealed record StoredCommit(
        string Oid,
        string Tree,
        string Parent,
        string Message,
        GitHubCommitActor Author,
        GitHubCommitActor Committer);

    private sealed class SyntheticGuard(IGitHubCoordinationStateCapability state)
        : IGitHubCoordinationGuardCapability
    {
        public IGitHubCoordinationStateCapability State { get; } = state;
    }

    private sealed class CoordinationRemote
    {
        private readonly object gate = new();
        private readonly Dictionary<string, byte[]> blobs = new(StringComparer.Ordinal);
        private readonly Dictionary<string, StoredTree> trees = new(StringComparer.Ordinal);
        private readonly Dictionary<string, StoredCommit> commits = new(StringComparer.Ordinal);
        private readonly Dictionary<string, GitHubPreparedCoordination> prepared = new(StringComparer.Ordinal);
        private TaskCompletionSource? refBarrier;
        private int requiredRefCalls;
        private int arrivedRefCalls;
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
            var value = GitHubCoordinationObjects.Prepare(state);
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
                blobs[value.BlobOid] = value.StateBytes.ToArray();
                trees[value.LeafTreeOid] = new(value.LeafTreeOid,
                    GitHubCoordinationObjects.LeafEntries(value));
                trees[value.RootTreeOid] = new(value.RootTreeOid,
                    GitHubCoordinationObjects.RootEntries(value));
                var request = GitHubCoordinationObjects.CommitRequest(value);
                commits[value.CommitOid] = new(value.CommitOid, value.RootTreeOid,
                    value.ParentOid, value.Message, request.Author, request.Committer);
            }
        }

        internal void SeedRaw(
            byte[] stateBytes,
            string operationCommitment,
            string stage,
            string parent)
        {
            var blobOid = GitObjectOid("blob", stateBytes);
            var leafOid = GitObjectOid("tree", TreeEntryBytes("100644",
                GitHubCoordinationObjects.StatePath, blobOid));
            var rootOid = GitObjectOid("tree", TreeEntryBytes("40000",
                GitHubCoordinationObjects.RootPath, leafOid));
            var message = "ContractScribe coordination v1\n"
                + "operation=" + operationCommitment + "\n"
                + "stage=" + stage + "\n";
            var commitText = "tree " + rootOid + "\nparent " + parent
                + "\nauthor ContractScribe <contract-scribe@users.noreply.github.com> 946684800 +0000"
                + "\ncommitter ContractScribe <contract-scribe@users.noreply.github.com> 946684800 +0000\n\n"
                + message;
            var commitOid = GitObjectOid("commit", Encoding.UTF8.GetBytes(commitText));
            lock (gate)
            {
                blobs[blobOid] = stateBytes;
                trees[leafOid] = new(leafOid,
                    [new(GitHubCoordinationObjects.StatePath, GitHubTreeMode.File, blobOid, null)]);
                trees[rootOid] = new(rootOid,
                    [new(GitHubCoordinationObjects.RootPath, GitHubTreeMode.Directory, leafOid, null)]);
                var actor = new GitHubCommitActor(GitHubCoordinationObjects.ActorName,
                    GitHubCoordinationObjects.ActorEmail, GitHubCoordinationObjects.ActorDate);
                commits[commitOid] = new(commitOid, rootOid, parent, message, actor, actor);
                CoordinationHead = commitOid;
            }
        }

        internal GitHubCoordinationState State(string head)
        {
            lock (gate)
            {
                var commit = commits[head];
                var root = trees[commit.Tree];
                var leaf = trees[root.Entries.Single().Oid];
                return GitHubCoordinationCodec.Decode(blobs[leaf.Entries.Single().Oid]);
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
                        blobs[leaf.Entries.Single().Oid] = [.. blobs[leaf.Entries.Single().Oid], (byte)' '];
                        break;
                    case ObjectTamper.LeafTree:
                        trees[leaf.Oid] = leaf with
                        {
                            Entries = [.. leaf.Entries, new("unexpected.json", GitHubTreeMode.File,
                                leaf.Entries.Single().Oid, null)],
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
            if (path == "/graphql") return await MutateRef(document.RootElement);
            return Json(HttpStatusCode.NotFound, new { message = "Not Found" });
        }

        private HttpResponseMessage Get(string path)
        {
            lock (gate)
            {
                if (path.Equals("/repos/Owner/repo", StringComparison.OrdinalIgnoreCase))
                    return Json(HttpStatusCode.OK, Repository());
                if (path.EndsWith("/git/ref/heads/main", StringComparison.Ordinal))
                    return Ref("refs/heads/main", TargetHead);
                if (path.Contains("/git/ref/heads/contract-scribe/coordination/", StringComparison.Ordinal))
                    return CoordinationHead is null
                        ? Json(HttpStatusCode.NotFound, new { message = "Not Found" })
                        : Ref(RefName(path), CoordinationHead);
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
                if (path.Contains("/git/commits/", StringComparison.Ordinal) && commits.TryGetValue(oid, out var commit))
                    return Commit(commit);
                return Json(HttpStatusCode.NotFound, new { message = "Not Found" });
            }
        }

        private HttpResponseMessage MutateBlob(JsonElement body)
        {
            var bytes = Convert.FromBase64String(body.GetProperty("content").GetString()!);
            var value = GitHubCoordinationObjects.Prepare(GitHubCoordinationCodec.Decode(bytes));
            lock (gate)
            {
                ObjectMutationAttempts++;
                prepared[value.CommitOid] = value;
                blobs[value.BlobOid] = bytes;
                if (LoseResponse == LostMutation.Blob)
                {
                    if (LostBlobReadback == LostReadback.Missing)
                        blobs.Remove(value.BlobOid);
                    else if (LostBlobReadback == LostReadback.Mismatch)
                        blobs[value.BlobOid] = [.. bytes, (byte)' '];
                }
            }
            return MutationResponse(LostMutation.Blob, new { sha = value.BlobOid }, HttpStatusCode.Created);
        }

        private HttpResponseMessage MutateTree(JsonElement body)
        {
            var entries = body.GetProperty("tree").EnumerateArray().Select(entry => new GitHubTreeEntry(
                entry.GetProperty("path").GetString()!, Mode(entry.GetProperty("mode").GetString()!),
                entry.GetProperty("sha").GetString()!, null)).ToImmutableArray();
            var leaf = entries.Single().Path == GitHubCoordinationObjects.StatePath;
            GitHubPreparedCoordination value;
            lock (gate)
            {
                value = prepared.Values.Single(item => leaf
                    ? item.BlobOid == entries.Single().Oid
                    : item.LeafTreeOid == entries.Single().Oid);
                ObjectMutationAttempts++;
                var oid = leaf ? value.LeafTreeOid : value.RootTreeOid;
                trees[oid] = new(oid, entries);
            }
            var stored = new StoredTree(leaf ? value.LeafTreeOid : value.RootTreeOid, entries);
            return MutationResponse(leaf ? LostMutation.LeafTree : LostMutation.RootTree,
                TreeValue(stored), HttpStatusCode.Created);
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
            message = commit.Message,
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
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => remote.Reply(request);
    }
}
