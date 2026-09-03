using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContractScribe.Cli;
using ContractScribe.Core;
using ContractScribe.Patching;
using ContractScribe.Roslyn;

namespace ContractScribe.Tests;

public sealed class GitHubPublicationRequestFactoryTests
{
    [Fact]
    public Task Accepted_candidate_is_bound_exactly_and_only_changed_bytes_are_copied() =>
        DocumentationScribeCompositionTests.PublicationAcceptanceAsync();

    [Fact]
    public Task Real_M4_acceptance_and_reconstruction_use_the_current_checkpoint() =>
        DocumentationScribeCompositionTests.PublicationRealReconstructionAsync();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Terminal_admission_is_closed_for_both_candidate_bearing_outcomes(bool reconstructed) =>
        DocumentationScribeCompositionTests.PublicationTerminalsAsync(reconstructed);

    [Theory]
    [MemberData(nameof(CorrelationCases))]
    public Task Current_authority_and_candidate_substitutions_fail_closed(string mutation) =>
        DocumentationScribeCompositionTests.PublicationCorrelationAsync(mutation);

    public static IEnumerable<object[]> CorrelationCases() => new[]
    {
        "outcomes", "missing-artifact", "missing-candidate", "checkpoint-failure",
        "checkpoint-digest", "checkpoint-bytes", "checkpoint-state", "newer-readback",
        "disposed-session", "different-session", "observations", "source-drift", "style",
        "execution", "plan", "audit", "cancelled", "candidate-request", "candidate-context",
        "candidate-rejected", "result-missing", "result-extra", "result-duplicate", "result-case",
        "original-hash", "candidate-hash", "block-count", "original-bytes", "candidate-bytes",
        "original-lines", "candidate-lines", "candidate-missing", "candidate-extra",
        "candidate-duplicate", "candidate-case", "advertised-hash", "payload-bytes",
        "unchanged-bytes", "missing-observation", "cumulative", "projection", "accepted-keys",
    }.Select(value => new object[] { value });

    [Theory]
    [MemberData(nameof(ConfigurationCases))]
    public Task Configuration_policy_and_predecessor_mismatches_fail_locally(string mutation) =>
        DocumentationScribeCompositionTests.PublicationConfigurationAsync(mutation);

    public static IEnumerable<object[]> ConfigurationCases() => new[]
    {
        "owner", "repository", "target", "tag", "base", "operation", "generation", "unknown-transition",
        "blocks-zero", "files-zero", "bytes-zero", "blocks-over-m4", "files-over-m4", "bytes-over-m4",
        "bytes-under-candidate", "initial-append", "initial-terminal", "initial-authorization",
        "append-missing", "append-operation", "append-authority", "append-candidate",
        "append-generation", "append-snapshot", "append-policy", "append-paths", "append-extra-path",
        "append-duplicate-path", "append-case-path", "append-terminal", "append-authorization",
        "merged-missing", "merged-disposition", "merged-generation", "merged-authorization",
        "closed-missing", "closed-id", "closed-logical", "closed-pr", "closed-generation",
        "closed-head", "closed-snapshot", "closed-work", "closed-candidate", "closed-new-generation",
        "closed-operation",
    }.Select(value => new object[] { value });

    [Fact]
    public Task Initial_append_and_explicit_successors_use_the_R1_transition_matrix() =>
        DocumentationScribeCompositionTests.PublicationTransitionsAsync();

    [Fact]
    public Task Whole_file_payload_bound_is_distinct_from_documentation_region_budget() =>
        DocumentationScribeCompositionTests.PublicationPayloadBoundAsync();

    [Fact]
    public void Snapshot_projection_is_framed_complete_and_stable()
    {
        var snapshot = new CampaignStateSnapshotAuthority("snapshot.fixture", Hex('a'), Hex('b'),
            Hex('c'), Hex('d'), TargetProfile.ExternalApi, Hex('e'));
        var commitment = GitHubPublicationRequestFactory.CreateSnapshotCommitment(snapshot);
        Assert.Equal("fd99ce4482b6dd861767867746accea83f432a8a0c63812548db1fb16ce1d8df", commitment);
        var mutations = new[]
        {
            snapshot with { OpaqueSnapshotBinding = "snapshot.other" },
            snapshot with { RepositoryCommitmentSha256 = Hex('f') },
            snapshot with { InputCommitmentSha256 = Hex('f') },
            snapshot with { InputIdentityCommitmentSha256 = Hex('f') },
            snapshot with { PolicyAuthorityCommitmentSha256 = Hex('f') },
            snapshot with { ExecutionCommitmentSha256 = Hex('f') },
            snapshot with { TargetProfile = Enum.GetValues<TargetProfile>().First(value => value != snapshot.TargetProfile) },
        };
        Assert.All(mutations, value => Assert.NotEqual(commitment,
            GitHubPublicationRequestFactory.CreateSnapshotCommitment(value)));
        Assert.Equal(commitment, GitHubPublicationRequestFactory.CreateSnapshotCommitment(snapshot with { }));
    }

    [Fact]
    public void Handoff_and_context_have_no_public_serialization_or_side_effect_capability()
    {
        foreach (var type in new[] { typeof(GitHubPublicationRequest), typeof(GitHubPublicationContext) })
        {
            Assert.False(type.IsVisible);
            Assert.Empty(type.GetProperties(BindingFlags.Public | BindingFlags.Instance));
            Assert.Empty(type.GetFields(BindingFlags.Public | BindingFlags.Instance));
        }
        var parameters = typeof(GitHubPublicationRequestFactory)
            .GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static)!.GetParameters();
        Assert.DoesNotContain(parameters, value => value.ParameterType == typeof(IGitHubPublicationPort)
            || value.ParameterType == typeof(ICampaignCheckpointStore));
        Assert.DoesNotContain(typeof(GitHubPublicationContext)
            .GetProperties(BindingFlags.NonPublic | BindingFlags.Instance),
            value => value.PropertyType == typeof(ICampaignCheckpointStore)
                || value.PropertyType == typeof(IGitHubPublicationPort));
    }

    private static string Hex(char value) => new(value, 64);
}

public sealed partial class DocumentationScribeCompositionTests
{
    internal static async Task PublicationAcceptanceAsync()
    {
        await using var fixture = await PublicationFixture.CreateAsync();
        var result = Publish(fixture);
        AssertPublicationAccepted(result);
        var authority = result.Authority!;
        var changed = Assert.Single(authority.ChangedFiles);
        var payload = Assert.Single(result.Payload!.Files);
        Assert.Equal("Fixture.cs", changed.Path);
        Assert.Equal(changed.Path, payload.Path);
        Assert.Equal(fixture.Outcome.Artifact!.Sha256, authority.CheckpointSha256);
        Assert.Equal(fixture.Outcome.Artifact.CheckpointRevision, authority.CheckpointRevision);
        Assert.Equal(fixture.Campaign.Plan.ExecutionCommitment, authority.WorkPlanCommitmentSha256);
        Assert.Equal(authority.ExecutionCommitmentSha256, authority.WorkPlanCommitmentSha256);
        Assert.Equal(fixture.Outcome.Artifact.State.CandidateObservation!.PatchResultCommitmentSha256,
            authority.CandidateCommitmentSha256);
        Assert.Equal(authority.CandidateCommitmentSha256, authority.PatchResultCommitmentSha256);
        Assert.Equal(32, authority.AcceptedM4Ceilings.MaximumDocumentationBlocks);
        Assert.Equal(8, authority.AcceptedM4Ceilings.MaximumDistinctChangedFiles);
        Assert.Equal(1_000_000, authority.AcceptedM4Ceilings.MaximumCumulativePatchBytes);
        Assert.NotEqual(payload.CandidateBytes.Length, changed.CandidateDocumentationByteCount);
        Assert.True(fixture.Outcome.AcceptedCandidate!.Files.Length > result.Payload.Files.Length);
        Assert.Equal(authority.AuthorityCommitmentSha256, result.Payload.AuthorityCommitmentSha256);

        var reversed = CandidateWith(fixture, files: fixture.Outcome.AcceptedCandidate.Files.Reverse().ToImmutableArray());
        var replay = GitHubPublicationRequestFactory.Create(fixture.Context,
            new(DocumentationCampaignOutcomeKind.Reconstructed, "ignored", fixture.Outcome.Artifact, reversed),
            PublicationConfiguration());
        AssertPublicationAccepted(replay);
        Assert.Equal(authority.AuthorityCommitmentSha256, replay.Authority!.AuthorityCommitmentSha256);
        Assert.Equal(authority.OperationCommitmentSha256, replay.Authority.OperationCommitmentSha256);
        Assert.Equal(payload.CandidateBytes.ToArray(), Assert.Single(replay.Payload!.Files).CandidateBytes.ToArray());
        Assert.Equal("{}", JsonSerializer.Serialize(result));
        Assert.Equal(nameof(GitHubPublicationRequest), result.ToString());
        Assert.Equal("{}", JsonSerializer.Serialize(fixture.Context));

        // Deliberately violate the test candidate's immutable wrapper to prove
        // the final Core payload owns a different buffer.
        var candidateFile = fixture.Outcome.AcceptedCandidate.Files.Single(file => file.RepositoryPath == payload.Path);
        var saved = payload.CandidateBytes.ToArray();
        ImmutableCollectionsMarshal.AsArray(candidateFile.Bytes)![0] ^= 1;
        Assert.Equal(saved, payload.CandidateBytes.ToArray());
    }

    internal static async Task PublicationTerminalsAsync(bool reconstructed)
    {
        await using var fixture = await PublicationFixture.CreateAsync();
        var terminals = new CampaignTerminalOutcome?[]
        {
            null,
            new(CampaignTerminalKind.Complete, CampaignTerminalReason.AllWorkClosed),
            new(CampaignTerminalKind.Complete, CampaignTerminalReason.NoWork),
            new(CampaignTerminalKind.Exhausted, CampaignTerminalReason.Budget),
            new(CampaignTerminalKind.Cancelled, CampaignTerminalReason.Caller),
            new(CampaignTerminalKind.Timeout, CampaignTerminalReason.Deadline),
            new(CampaignTerminalKind.Failed, CampaignTerminalReason.Host),
            new(CampaignTerminalKind.Superseded, CampaignTerminalReason.NewSnapshot),
            new(CampaignTerminalKind.Complete, CampaignTerminalReason.Host),
            new((CampaignTerminalKind)999, CampaignTerminalReason.AllWorkClosed),
            new(CampaignTerminalKind.Complete, (CampaignTerminalReason)999),
        };
        foreach (var terminal in terminals)
        {
            var state = PublicationState(fixture.Outcome.Artifact!.State, terminal: terminal);
            var artifact = PublicationArtifact(state, fixture.Outcome.Artifact);
            var outcome = new DocumentationCampaignOutcome(reconstructed
                    ? DocumentationCampaignOutcomeKind.Reconstructed : DocumentationCampaignOutcomeKind.Accepted,
                "test", artifact, fixture.Outcome.AcceptedCandidate);
            var result = GitHubPublicationRequestFactory.Create(
                fixture.Context with { CurrentCheckpoint = artifact }, outcome, PublicationConfiguration());
            if (terminal is null or { Kind: CampaignTerminalKind.Complete, Reason: CampaignTerminalReason.AllWorkClosed })
                AssertPublicationAccepted(result);
            else
                AssertPublicationInvalid(result);
        }
    }

    internal static async Task PublicationCorrelationAsync(string mutation)
    {
        await using var fixture = await PublicationFixture.CreateAsync();
        var context = fixture.Context;
        var artifact = fixture.Outcome.Artifact!;
        var candidate = fixture.Outcome.AcceptedCandidate!;
        var outcome = fixture.Outcome;
        var files = candidate.Files;
        var changedIndex = files.IndexOf(files.Single(file => file.RepositoryPath == "Fixture.cs"));
        var unchangedIndex = Enumerable.Range(0, files.Length).First(index => index != changedIndex);
        var changed = files[changedIndex];
        var result = candidate.Result;
        var resultFile = Assert.Single(result.ChangedFiles);
        CompositionFixture? other = null;
        try
        {
            switch (mutation)
            {
                case "outcomes":
                    foreach (var kind in Enum.GetValues<DocumentationCampaignOutcomeKind>())
                        if (kind is not (DocumentationCampaignOutcomeKind.Accepted or DocumentationCampaignOutcomeKind.Reconstructed))
                            AssertPublicationInvalid(GitHubPublicationRequestFactory.Create(context,
                                new(kind, "private-source-sentinel", artifact), PublicationConfiguration()));
                    AssertPublicationInvalid(GitHubPublicationRequestFactory.Create(context,
                        new((DocumentationCampaignOutcomeKind)999, "private-source-sentinel"), PublicationConfiguration()));
                    return;
                case "missing-artifact": outcome = new(outcome.Kind, "test", acceptedCandidate: candidate); break;
                case "missing-candidate": outcome = new(outcome.Kind, "test", artifact); break;
                case "checkpoint-failure": outcome = new(outcome.Kind, "test", artifact, candidate, CampaignCheckpointAcceptanceKind.Conflict); break;
                case "checkpoint-digest": artifact = new(artifact.State, artifact.ExactUtf8Json.ToArray(), PublicationHex('a')); break;
                case "checkpoint-bytes": artifact = new(artifact.State, [.. artifact.ExactUtf8Json, (byte)' '], artifact.Sha256); break;
                case "checkpoint-state": artifact = new(PublicationState(artifact.State, revision: artifact.CheckpointRevision + 1), artifact.ExactUtf8Json.ToArray(), artifact.Sha256); break;
                case "newer-readback": context = context with { CurrentCheckpoint = CampaignStateJson.CreateArtifact(PublicationState(artifact.State, revision: artifact.CheckpointRevision + 1)) }; break;
                case "disposed-session": await fixture.Fixture.Session.DisposeAsync(); break;
                case "different-session": other = await CompositionFixture.CreateProposalStageAsync(); context = context with { Session = other.Classified }; break;
                case "observations": other = await CompositionFixture.CreateProposalStageAsync(); context = context with { Observations = other.Observed }; break;
                case "source-drift": await File.AppendAllTextAsync(fixture.Fixture.SourcePath, "// changed after acceptance\n"); break;
                case "style": context = context with { StyleConfigurationProjection = JsonSerializer.SerializeToElement(new { style = "other" }) }; break;
                case "execution": context = context with { ExecutionCapability = null! }; break;
                case "plan": context = context with { AcceptedPlan = new(context.AcceptedPlan.CampaignLineage, "other-snapshot", context.AcceptedPlan.AuditDocumentSha256, context.AcceptedPlan.ExecutionCommitment, context.AcceptedPlan.TargetProfile, context.AcceptedPlan.WorkItems, context.AcceptedPlan.Summary) }; break;
                case "audit": context = context with { AcceptedAuditInputs = [] }; break;
                case "cancelled":
                    using (var cancellation = new CancellationTokenSource())
                    {
                        cancellation.Cancel();
                        Assert.Throws<OperationCanceledException>(() => GitHubPublicationRequestFactory.Create(
                            context with { CancellationToken = cancellation.Token }, outcome, PublicationConfiguration()));
                    }
                    return;
                case "candidate-request": result = PublicationResult(result, requestSha: PublicationHex('a')); break;
                case "candidate-context":
                    RepositoryContextRef.TryParse("repoctx-ffffffffffffffffffffffffffffffff", out var otherContext);
                    result = PublicationResult(result, context: new(otherContext, result.Context.InputIdentity, result.Context.TargetProfile)); break;
                case "candidate-rejected": result = PublicationResult(result, outcome: DocumentationPatchOutcome.Rejected); break;
                case "result-missing": result = PublicationResult(result, files: []); break;
                case "result-extra": result = PublicationResult(result, files: [resultFile, PublicationChanged(resultFile, path: "Extra.cs")]); break;
                case "result-duplicate": result = PublicationResult(result, files: [resultFile, resultFile]); break;
                case "result-case": result = PublicationResult(result, files: [resultFile, PublicationChanged(resultFile, path: "fixture.cs")]); break;
                case "original-hash": result = PublicationResult(result, files: [PublicationChanged(resultFile, original: PublicationHex('a'))]); break;
                case "candidate-hash": result = PublicationResult(result, files: [PublicationChanged(resultFile, hash: PublicationHex('a'))]); break;
                case "block-count": result = PublicationResult(result, files: [PublicationChanged(resultFile, blocks: resultFile.ChangedDocumentationBlockCount + 1)]); break;
                case "original-bytes": result = PublicationResult(result, files: [PublicationChanged(resultFile, originalBytes: resultFile.OriginalDocumentationByteCount + 1)]); break;
                case "candidate-bytes": result = PublicationResult(result, files: [PublicationChanged(resultFile, candidateBytes: resultFile.CandidateDocumentationByteCount + 1)]); break;
                case "original-lines": result = PublicationResult(result, files: [PublicationChanged(resultFile, originalLines: resultFile.OriginalDocumentationLineCount + 1)]); break;
                case "candidate-lines": result = PublicationResult(result, files: [PublicationChanged(resultFile, candidateLines: resultFile.CandidateDocumentationLineCount + 1)]); break;
                case "candidate-missing": files = files.RemoveAt(changedIndex); break;
                case "candidate-extra": files = files.SetItem(unchangedIndex, new("Extra.cs", changed.Bytes, changed.Sha256)); break;
                case "candidate-duplicate": files = files.SetItem(unchangedIndex, changed); break;
                case "candidate-case": files = files.SetItem(unchangedIndex, new("fixture.cs", changed.Bytes, changed.Sha256)); break;
                case "advertised-hash": files = files.SetItem(changedIndex, new(changed.RepositoryPath, changed.Bytes, PublicationHex('a'))); break;
                case "payload-bytes": files = files.SetItem(changedIndex, new(changed.RepositoryPath, [.. changed.Bytes, (byte)'x'], changed.Sha256)); break;
                case "unchanged-bytes":
                    var bytes = files[unchangedIndex].Bytes.Add((byte)'x');
                    files = files.SetItem(unchangedIndex, new(files[unchangedIndex].RepositoryPath, bytes, Sha256(bytes.ToArray()))); break;
                case "missing-observation": artifact = PublicationArtifact(PublicationState(artifact.State, removeObservation: true), artifact); break;
                case "cumulative": artifact = PublicationArtifact(PublicationState(artifact.State, cumulative: artifact.State.CumulativeOutcome! with { Kind = CampaignCumulativeOutcomeKind.Stale }), artifact); break;
                case "projection": artifact = PublicationArtifact(PublicationState(artifact.State, observation: artifact.State.CandidateObservation! with { AcceptedProjectionCommitmentSha256 = PublicationHex('a') }), artifact); break;
                case "accepted-keys": artifact = PublicationArtifact(PublicationState(artifact.State, observation: artifact.State.CandidateObservation! with { AcceptedWorkItemKeys = [] }), artifact); break;
                default: throw new InvalidOperationException(mutation);
            }
            if (outcome == fixture.Outcome)
                outcome = new(outcome.Kind, "private-source-sentinel", artifact, CandidateWith(fixture, result, files));
            AssertPublicationInvalid(GitHubPublicationRequestFactory.Create(context, outcome, PublicationConfiguration()));
        }
        finally
        {
            if (other is not null) await other.DisposeAsync();
        }
    }

    internal static async Task PublicationConfigurationAsync(string mutation)
    {
        await using var fixture = await PublicationFixture.CreateAsync();
        var initial = Publish(fixture);
        AssertPublicationAccepted(initial);
        var configuration = PublicationConfiguration();
        var append = PublicationAppend(initial.Authority!);
        var terminal = new GitHubPublicationPredecessorAuthority("predecessor", 10, "generation.previous",
            new string('b', 40), GitHubPublicationPredecessorDisposition.ClosedUnmerged);
        var authorization = PublicationAuthorization(initial.Authority!, terminal);
        if (mutation.StartsWith("append-", StringComparison.Ordinal))
            configuration = configuration with { Transition = GitHubPublicationTransitionKind.SameSnapshotAppend, AppendPredecessor = append };
        if (mutation.StartsWith("merged-", StringComparison.Ordinal))
            configuration = configuration with { Transition = GitHubPublicationTransitionKind.SuccessorAfterMerge, TerminalPredecessor = terminal with { Disposition = GitHubPublicationPredecessorDisposition.Merged } };
        if (mutation.StartsWith("closed-", StringComparison.Ordinal))
            configuration = configuration with { Transition = GitHubPublicationTransitionKind.SuccessorAfterClosedUnmerged, TerminalPredecessor = terminal, ClosedUnmergedSuccessorAuthorization = authorization };
        configuration = mutation switch
        {
            "owner" => configuration with { RepositoryOwner = "bad/owner" },
            "repository" => configuration with { RepositoryName = "" },
            "target" => configuration with { TargetRef = "refs/heads/bad..ref" },
            "tag" => configuration with { TargetRef = "refs/tags/main" },
            "base" => configuration with { ExpectedBaseCommitOid = new('0', 40) },
            "operation" => configuration with { OperationId = "bad\noperation" },
            "generation" => configuration with { GenerationId = "" },
            "unknown-transition" => configuration with { Transition = (GitHubPublicationTransitionKind)99 },
            "blocks-zero" => configuration with { Policy = configuration.Policy with { MaximumDocumentationBlocks = 0 } },
            "files-zero" => configuration with { Policy = configuration.Policy with { MaximumDistinctChangedFiles = 0 } },
            "bytes-zero" => configuration with { Policy = configuration.Policy with { MaximumCumulativePatchBytes = 0 } },
            "blocks-over-m4" => configuration with { Policy = configuration.Policy with { MaximumDocumentationBlocks = 33 } },
            "files-over-m4" => configuration with { Policy = configuration.Policy with { MaximumDistinctChangedFiles = 9 } },
            "bytes-over-m4" => configuration with { Policy = configuration.Policy with { MaximumCumulativePatchBytes = 1_000_001 } },
            "bytes-under-candidate" => configuration with { Policy = configuration.Policy with { MaximumCumulativePatchBytes = initial.Authority!.CumulativePatchBytes - 1 } },
            "initial-append" => configuration with { AppendPredecessor = append },
            "initial-terminal" => configuration with { TerminalPredecessor = terminal },
            "initial-authorization" => configuration with { ClosedUnmergedSuccessorAuthorization = authorization },
            "append-missing" => configuration with { AppendPredecessor = null },
            "append-operation" => configuration with { AppendPredecessor = append with { OperationId = configuration.OperationId } },
            "append-authority" => configuration with { AppendPredecessor = append with { AuthorityCommitmentSha256 = null! } },
            "append-candidate" => configuration with { AppendPredecessor = append with { CandidateCommitmentSha256 = null! } },
            "append-generation" => configuration with { AppendPredecessor = append with { GenerationId = "different" } },
            "append-snapshot" => configuration with { AppendPredecessor = append with { SnapshotCommitmentSha256 = PublicationHex('f') } },
            "append-policy" => configuration with { Policy = configuration.Policy with { MaximumDocumentationBlocks = 31 } },
            "append-paths" => configuration with { AppendPredecessor = append with { ChangedFiles = [] } },
            "append-extra-path" => configuration with { AppendPredecessor = append with { ChangedFiles = [new("Extra.cs", PublicationHex('a'))] } },
            "append-duplicate-path" => configuration with { AppendPredecessor = append with { ChangedFiles = [append.ChangedFiles[0], append.ChangedFiles[0]] } },
            "append-case-path" => configuration with { AppendPredecessor = append with { ChangedFiles = [append.ChangedFiles[0], new("fixture.cs", PublicationHex('a'))] } },
            "append-terminal" => configuration with { TerminalPredecessor = terminal },
            "append-authorization" => configuration with { ClosedUnmergedSuccessorAuthorization = authorization },
            "merged-missing" => configuration with { TerminalPredecessor = null },
            "merged-disposition" => configuration with { TerminalPredecessor = terminal },
            "merged-generation" => configuration with { GenerationId = terminal.GenerationId },
            "merged-authorization" => configuration with { ClosedUnmergedSuccessorAuthorization = authorization },
            "closed-missing" => configuration with { ClosedUnmergedSuccessorAuthorization = null },
            "closed-id" => configuration with { ClosedUnmergedSuccessorAuthorization = authorization with { AuthorizationId = "" } },
            "closed-logical" => configuration with { ClosedUnmergedSuccessorAuthorization = authorization with { LogicalPredecessorId = "other" } },
            "closed-pr" => configuration with { ClosedUnmergedSuccessorAuthorization = authorization with { ClosedPullRequestNumber = 11 } },
            "closed-generation" => configuration with { ClosedUnmergedSuccessorAuthorization = authorization with { ClosedGenerationId = "other" } },
            "closed-head" => configuration with { ClosedUnmergedSuccessorAuthorization = authorization with { ClosedHeadOid = new('c', 40) } },
            "closed-snapshot" => configuration with { ClosedUnmergedSuccessorAuthorization = authorization with { FreshSnapshotCommitmentSha256 = PublicationHex('f') } },
            "closed-work" => configuration with { ClosedUnmergedSuccessorAuthorization = authorization with { FreshWorkPlanCommitmentSha256 = PublicationHex('f') } },
            "closed-candidate" => configuration with { ClosedUnmergedSuccessorAuthorization = authorization with { FreshCandidateCommitmentSha256 = PublicationHex('f') } },
            "closed-new-generation" => configuration with { ClosedUnmergedSuccessorAuthorization = authorization with { NewGenerationId = "other" } },
            "closed-operation" => configuration with { ClosedUnmergedSuccessorAuthorization = authorization with { OperationId = "other" } },
            _ => throw new InvalidOperationException(mutation),
        };
        AssertPublicationInvalid(GitHubPublicationRequestFactory.Create(fixture.Context, fixture.Outcome, configuration));
    }

    internal static async Task PublicationTransitionsAsync()
    {
        await using var fixture = await PublicationFixture.CreateAsync();
        var first = Publish(fixture);
        AssertPublicationAccepted(first);
        var configurations = new[]
        {
            PublicationConfiguration() with { Transition = GitHubPublicationTransitionKind.SameSnapshotAppend, AppendPredecessor = PublicationAppend(first.Authority!) },
            PublicationConfiguration() with { Transition = GitHubPublicationTransitionKind.SuccessorAfterMerge, TerminalPredecessor = new("predecessor", 10, "generation.previous", new('b', 40), GitHubPublicationPredecessorDisposition.Merged) },
            PublicationConfiguration() with { Policy = new(1, 1, first.Authority!.CumulativePatchBytes) },
        };
        foreach (var configuration in configurations)
        {
            var result = GitHubPublicationRequestFactory.Create(fixture.Context, fixture.Outcome, configuration);
            AssertPublicationAccepted(result);
            Assert.NotEqual(first.Authority!.OperationCommitmentSha256, result.Authority!.OperationCommitmentSha256);
        }
        var predecessor = new GitHubPublicationPredecessorAuthority("predecessor", 10, "generation.previous", new('b', 40), GitHubPublicationPredecessorDisposition.ClosedUnmerged);
        var closed = PublicationConfiguration() with
        {
            Transition = GitHubPublicationTransitionKind.SuccessorAfterClosedUnmerged,
            TerminalPredecessor = predecessor,
            ClosedUnmergedSuccessorAuthorization = PublicationAuthorization(first.Authority!, predecessor),
        };
        var successor = GitHubPublicationRequestFactory.Create(fixture.Context, fixture.Outcome, closed);
        AssertPublicationAccepted(successor);
        var replay = GitHubPublicationRequestFactory.Create(fixture.Context, fixture.Outcome, closed);
        Assert.Equal(successor.Authority!.OperationCommitmentSha256, replay.Authority!.OperationCommitmentSha256);
        var differentId = GitHubPublicationRequestFactory.Create(fixture.Context, fixture.Outcome,
            closed with { ClosedUnmergedSuccessorAuthorization = closed.ClosedUnmergedSuccessorAuthorization! with { AuthorizationId = "another-explicit-authorization" } });
        AssertPublicationAccepted(differentId);
        Assert.NotEqual(successor.Authority.OperationCommitmentSha256, differentId.Authority!.OperationCommitmentSha256);
    }

    internal static async Task PublicationPayloadBoundAsync()
    {
        var bytes = new byte[GitHubPublicationContract.MaximumPayloadBytesPerFile + 1];
        await using var fixture = await PublicationFixture.CreateAsync(bytes);
        var result = Publish(fixture);
        AssertPublicationInvalid(result);
        Assert.Equal(GitHubPublicationValidationCode.InvalidBound, result.Failure!.Code);
        Assert.Equal(GitHubPublicationFieldId.Payload, result.Failure.Field);
    }

    internal static async Task PublicationRealReconstructionAsync()
    {
        await using var fixture = await CompositionFixture.CreateProposalStageAsync();
        var campaign = fixture.CreateCampaign();
        var store = await ProposalReadyStore(fixture, campaign);
        var source = await File.ReadAllBytesAsync(fixture.SourcePath);
        var input = PatchInput(fixture, campaign, store);
        var accepted = await DocumentationCampaignPatchExecutor.ExecuteAsync(input);
        if (!OperatingSystem.IsLinux())
        {
            Assert.Equal(DocumentationCampaignOutcomeKind.HostFailure, accepted.Kind);
            AssertPublicationInvalid(GitHubPublicationRequestFactory.Create(
                PublicationContext(fixture, campaign, store.Current!), accepted, PublicationConfiguration()));
            return;
        }
        Assert.Equal(DocumentationCampaignOutcomeKind.Accepted, accepted.Kind);
        var first = GitHubPublicationRequestFactory.Create(PublicationContext(fixture, campaign, store.Current!), accepted, PublicationConfiguration());
        AssertPublicationAccepted(first);
        var reconstructed = await DocumentationCampaignPatchExecutor.ExecuteAsync(input);
        Assert.Equal(DocumentationCampaignOutcomeKind.Reconstructed, reconstructed.Kind);
        var current = PublicationContext(fixture, campaign, store.Current!);
        var second = GitHubPublicationRequestFactory.Create(current, reconstructed, PublicationConfiguration());
        AssertPublicationAccepted(second);
        Assert.True(second.Authority!.CheckpointRevision > first.Authority!.CheckpointRevision);
        Assert.NotEqual(first.Authority.CheckpointSha256, second.Authority.CheckpointSha256);
        Assert.NotEqual(first.Authority.AuthorityCommitmentSha256, second.Authority.AuthorityCommitmentSha256);
        Assert.Equal(first.Authority.SnapshotCommitmentSha256, second.Authority.SnapshotCommitmentSha256);
        Assert.Equal(first.Authority.CandidateCommitmentSha256, second.Authority.CandidateCommitmentSha256);
        Assert.Equal(Assert.Single(first.Payload!.Files).CandidateBytes.ToArray(), Assert.Single(second.Payload!.Files).CandidateBytes.ToArray());
        AssertPublicationInvalid(GitHubPublicationRequestFactory.Create(current, accepted, PublicationConfiguration()));
        Assert.Equal(source, await File.ReadAllBytesAsync(fixture.SourcePath));
    }

    private static GitHubPublicationConfiguration PublicationConfiguration() => new(
        "synthetic-owner", "synthetic-repository", "refs/heads/main", new('a', 40),
        "operation.current", "generation.current", new(32, 8, 1_000_000), GitHubPublicationTransitionKind.Initial);

    private static GitHubPublicationAppendPredecessor PublicationAppend(ValidatedGitHubPublicationAuthority authority) => new(
        "operation.previous", PublicationHex('a'), PublicationHex('b'), authority.GenerationId,
        authority.SnapshotCommitmentSha256, authority.PolicyCommitmentSha256,
        authority.ChangedFiles.Select(file => new GitHubPrecedingChangedFileAuthority(file.Path, PublicationHex('c'))).ToImmutableArray());

    private static GitHubClosedUnmergedSuccessorAuthorization PublicationAuthorization(
        ValidatedGitHubPublicationAuthority authority, GitHubPublicationPredecessorAuthority predecessor) => new(
        "authorization.explicit", predecessor.LogicalPredecessorId, predecessor.PullRequestNumber,
        predecessor.GenerationId, predecessor.HeadOid, authority.SnapshotCommitmentSha256,
        authority.WorkPlanCommitmentSha256, authority.CandidateCommitmentSha256, authority.GenerationId, authority.OperationId);

    private static GitHubPublicationContext PublicationContext(
        CompositionFixture fixture, CampaignExecutionFixture campaign, CampaignCheckpointArtifact current) => new(
        fixture.Classified, fixture.Observed, fixture.Policy, fixture.AuditInputs, fixture.AuditDocument,
        campaign.PlanningInput, campaign.Plan, campaign.ExecutionCapability, "style.public-api.v1", campaign.StyleProjection, current);

    private static GitHubPublicationRequest Publish(PublicationFixture fixture) =>
        GitHubPublicationRequestFactory.Create(fixture.Context, fixture.Outcome, PublicationConfiguration());

    private static void AssertPublicationAccepted(GitHubPublicationRequest result)
    {
        Assert.True(result.IsValid, result.Failure?.ToString());
        Assert.NotNull(result.Authority);
        Assert.NotNull(result.Payload);
        Assert.Null(result.Failure);
    }

    private static void AssertPublicationInvalid(GitHubPublicationRequest result)
    {
        Assert.False(result.IsValid);
        Assert.Null(result.Authority);
        Assert.Null(result.Payload);
        Assert.NotNull(result.Failure);
        Assert.Equal("{}", JsonSerializer.Serialize(result));
        Assert.Equal(nameof(GitHubPublicationRequest), result.ToString());
    }

    private static string PublicationHex(char value) => new(value, 64);

    private static DocumentationPatchAcceptedCandidate CandidateWith(PublicationFixture fixture,
        DocumentationPatchValidationResult? result = null, ImmutableArray<DocumentationPatchAcceptedCandidateFile>? files = null) =>
        new(fixture.PatchRequest, fixture.Baseline, result ?? fixture.Outcome.AcceptedCandidate!.Result, null!,
            files ?? fixture.Outcome.AcceptedCandidate!.Files, []);

    private static DocumentationPatchValidationResult PublicationResult(DocumentationPatchValidationResult basis,
        string? requestSha = null, DocumentationPatchContext? context = null, DocumentationPatchOutcome? outcome = null,
        ImmutableArray<DocumentationPatchChangedFile>? files = null) => new(
        requestSha ?? basis.PatchRequestSha256, context ?? basis.Context, outcome ?? basis.Outcome,
        basis.Targets, files ?? basis.ChangedFiles, basis.ChangedDocumentationBlockCount, basis.Invariants, basis.Diagnostics);

    private static DocumentationPatchChangedFile PublicationChanged(DocumentationPatchChangedFile basis,
        string? path = null, string? original = null, string? hash = null, int? blocks = null,
        int? originalBytes = null, int? candidateBytes = null, int? originalLines = null, int? candidateLines = null) => new(
        path ?? basis.Path, original ?? basis.OriginalFileSha256, hash ?? basis.CandidateFileSha256,
        blocks ?? basis.ChangedDocumentationBlockCount, originalBytes ?? basis.OriginalDocumentationByteCount,
        candidateBytes ?? basis.CandidateDocumentationByteCount, originalLines ?? basis.OriginalDocumentationLineCount,
        candidateLines ?? basis.CandidateDocumentationLineCount);

    private static CampaignCheckpointState PublicationState(CampaignCheckpointState basis, long? revision = null,
        CampaignTerminalOutcome? terminal = null, CampaignCandidateObservation? observation = null,
        CampaignCumulativeOutcome? cumulative = null, bool removeObservation = false) => new(
        basis.ProductRevision, basis.CampaignLineage, basis.Snapshot, revision ?? basis.CheckpointRevision,
        basis.ConfiguredCeilings, basis.LineageCharges, basis.WorkItems, basis.ActiveReservation,
        removeObservation ? null : observation ?? basis.CandidateObservation, cumulative ?? basis.CumulativeOutcome,
        basis.KnownCompletedOperations, terminal, basis.Predecessor);

    private static CampaignCheckpointArtifact PublicationArtifact(CampaignCheckpointState state, CampaignCheckpointArtifact basis)
    {
        try { return CampaignStateJson.CreateArtifact(state); }
        catch (CampaignStateValidationException) { return new(state, basis.ExactUtf8Json.ToArray(), basis.Sha256); }
    }

    private sealed record PublicationFixture(CompositionFixture Fixture, CampaignExecutionFixture Campaign,
        GitHubPublicationContext Context, DocumentationCampaignOutcome Outcome,
        DocumentationPatchRequest PatchRequest, DocumentationPatchRepositoryBaseline Baseline) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Fixture.DisposeAsync();

        internal static async Task<PublicationFixture> CreateAsync(byte[]? changedBytes = null)
        {
            var fixture = await CompositionFixture.CreateProposalStageAsync();
            try
            {
                var campaign = fixture.CreateCampaign();
                var store = await ProposalReadyStore(fixture, campaign);
                var request = CumulativeDocumentationPatchComposer.Compose(fixture.Classified, campaign.PlanningInput,
                    campaign.Plan, DocumentationScribeAuditAuthority.Create(fixture.Classified, fixture.Observed,
                        fixture.Policy, fixture.AuditInputs, fixture.AuditDocument), store.Current!.State,
                    acceptedOnly: false, CancellationToken.None).Request;
                var baseline = fixture.Session.CaptureDocumentationPatchRepositoryBaseline().Baseline!;
                Assert.NotNull(baseline);
                var path = Assert.IsType<DocumentationPatchRepositoryLocator>(Assert.Single(request.Blocks).Locator).Path;
                var original = baseline.Entries.Single(file => file.RepositoryPath == path);
                // Synthetic accepted capability: the fixture exercises H1's
                // correlation, while the real M2 test proves semantic provenance.
                var bytes = changedBytes ?? [.. original.Bytes, .. Encoding.UTF8.GetBytes("\n/// Synthetic accepted documentation.\n")];
                var candidateHash = Sha256(bytes);
                var result = DocumentationPatchValidator.CreateResult(request, DocumentationPatchOutcome.Accepted,
                    request.Blocks.Select(_ => DocumentationPatchTargetStatus.Valid),
                    [new(path, original.Sha256, candidateHash, 1, 0, 48, 0, 1)],
                    DocumentationPatchValidator.InvariantIds.Select(id => new DocumentationPatchInvariantResult(id, DocumentationPatchInvariantStatus.Passed)), []);
                var reserved = CampaignStateReducer.ReservePatchInvocation(store.Current!, request, 1_000);
                Assert.Equal(CampaignTransitionKind.Applied, reserved.Kind);
                var acceptedReservation = await CampaignCheckpointAcceptance.AcceptAsync(store, reserved);
                var invocation = CampaignStateReducer.CreatePatchInvocationAuthority(acceptedReservation.AcceptedCheckpoint!, request);
                Assert.True(invocation.TryBeginDispatch());
                var completion = CampaignStateReducer.CompletePatchInvocation(store.Current!, invocation, request, result, 1);
                Assert.Equal(CampaignTransitionKind.Applied, completion.Kind);
                var accepted = await CampaignCheckpointAcceptance.AcceptAsync(store, completion);
                Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted, accepted.Kind);
                var files = baseline.Entries.Select(file => file.RepositoryPath == path
                    ? new DocumentationPatchAcceptedCandidateFile(path, ImmutableArray.CreateRange(bytes), candidateHash)
                    : new DocumentationPatchAcceptedCandidateFile(file.RepositoryPath, file.Bytes, file.Sha256)).ToImmutableArray();
                var candidate = new DocumentationPatchAcceptedCandidate(request, baseline, result, null!, files, []);
                var outcome = new DocumentationCampaignOutcome(DocumentationCampaignOutcomeKind.Accepted, "campaign.patch.accepted", accepted.Artifact, candidate);
                return new(fixture, campaign, PublicationContext(fixture, campaign, accepted.Artifact!), outcome, request, baseline);
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }
    }
}
