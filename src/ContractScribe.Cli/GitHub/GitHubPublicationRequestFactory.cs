using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ContractScribe.Core;
using ContractScribe.Patching;
using ContractScribe.Roslyn;

namespace ContractScribe.Cli;

internal sealed class GitHubPublicationRequest
{
    private GitHubPublicationRequest(
        ValidatedGitHubPublicationAuthority? authority,
        ValidatedGitHubChangedFilePayload? payload,
        GitHubPublicationLocalFailure? failure)
    {
        Authority = authority;
        Payload = payload;
        Failure = failure;
    }

    // Internal getters deliberately keep ordinary JSON serialization empty.
    internal ValidatedGitHubPublicationAuthority? Authority { get; }
    internal ValidatedGitHubChangedFilePayload? Payload { get; }
    internal GitHubPublicationLocalFailure? Failure { get; }
    internal bool IsValid => Authority is not null && Payload is not null;

    internal static GitHubPublicationRequest Accepted(
        ValidatedGitHubPublicationAuthority authority,
        ValidatedGitHubChangedFilePayload payload) => new(authority, payload, null);

    internal static GitHubPublicationRequest Invalid(
        GitHubPublicationValidationCode code,
        GitHubPublicationFieldId field) => new(null, null, new(code, field));

    public override string ToString() => nameof(GitHubPublicationRequest);
}

internal static class GitHubPublicationRequestFactory
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static GitHubPublicationRequest Create(
        GitHubPublicationContext context,
        DocumentationCampaignOutcome outcome,
        GitHubPublicationConfiguration configuration)
    {
        var field = GitHubPublicationFieldId.Campaign;
        try
        {
            ArgumentNullException.ThrowIfNull(context);
            context.CancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(outcome);
            ArgumentNullException.ThrowIfNull(configuration);
            Require(outcome.Kind is DocumentationCampaignOutcomeKind.Accepted
                or DocumentationCampaignOutcomeKind.Reconstructed);
            Require(outcome.CheckpointFailure is null);
            var artifact = outcome.Artifact;
            var candidate = outcome.AcceptedCandidate;
            Require(artifact is not null && candidate is not null);
            var state = artifact!.State;
            Require(state.ActiveReservation is null
                && state.CumulativeOutcome?.Kind == CampaignCumulativeOutcomeKind.Accepted
                && state.CandidateObservation is not null
                && state.WorkItems.Any(work => work.Status == CampaignWorkStatus.Accepted)
                && (state.TerminalOutcome is null
                    or { Kind: CampaignTerminalKind.Complete, Reason: CampaignTerminalReason.AllWorkClosed }));

            field = GitHubPublicationFieldId.Checkpoint;
            Require(ExactArtifact(artifact, CampaignStateJson.CreateArtifact(state)));
            Require(ExactArtifact(context.CurrentCheckpoint,
                CampaignStateJson.CreateArtifact(context.CurrentCheckpoint.State)));
            Require(ExactArtifact(artifact, context.CurrentCheckpoint));

            field = GitHubPublicationFieldId.WorkPlan;
            Require(!context.Session.RepositorySession.IsDisposed
                && ReferenceEquals(context.Session.Classification.ClassificationSet,
                    context.PlanningInput.Classifications)
                && ReferenceEquals(context.Observations.ObservationSet,
                    context.PlanningInput.Observations)
                && context.Observations.IsBoundToObservationSession(context.Session));
            CampaignStateFactory.ValidateCurrentContext(
                state, context.ExecutionCapability, context.StyleConfigurationId,
                context.StyleConfigurationProjection, context.Session.RepositorySession.InputIdentity,
                context.PlanningInput, context.AcceptedPlan);
            var audit = DocumentationScribeAuditAuthority.Create(
                context.Session, context.Observations, context.AcceptedPolicy,
                context.AcceptedAuditInputs, context.AcceptedAuditDocument);
            var request = CumulativeDocumentationPatchComposer.Compose(
                context.Session, context.PlanningInput, context.AcceptedPlan,
                audit, state, acceptedOnly: true, context.CancellationToken).Request;

            field = GitHubPublicationFieldId.Candidate;
            var observation = state.CandidateObservation!;
            var cumulative = state.CumulativeOutcome!;
            Require(candidate!.Result.Outcome == DocumentationPatchOutcome.Accepted);
            var resultCommitment = CampaignStateFactory.CreatePatchResultCommitment(request, candidate.Result);
            Require(request.ArtifactSha256 == observation.PatchRequestSha256
                && request.ArtifactSha256 == cumulative.PatchRequestSha256
                && resultCommitment == observation.PatchResultCommitmentSha256
                && resultCommitment == cumulative.PatchResultCommitmentSha256
                && observation.AcceptedProjectionCommitmentSha256 == cumulative.ProjectionCommitmentSha256
                && observation.AcceptedWorkItemKeys.Order(StringComparer.Ordinal).SequenceEqual(
                    request.Blocks.Select(block => block.BlockId).Order(StringComparer.Ordinal),
                    StringComparer.Ordinal));

            field = GitHubPublicationFieldId.ChangedFiles;
            var changed = CorrelateChangedFiles(observation, candidate.Result);

            field = GitHubPublicationFieldId.Policy;
            var budget = state.ConfiguredCeilings.CampaignBudget;
            var append = configuration.AppendPredecessor;
            Require((configuration.Transition == GitHubPublicationTransitionKind.SameSnapshotAppend)
                == (append is not null));
            if (append is not null)
            {
                Require(!append.ChangedFiles.IsDefault);
            }
            Require(configuration.TargetRef.StartsWith("refs/heads/", StringComparison.Ordinal));
            var authority = GitHubPublicationFactory.CreateAuthority(new(
                configuration.RepositoryOwner,
                configuration.RepositoryName,
                configuration.TargetRef,
                configuration.ExpectedBaseCommitOid,
                state.CampaignLineage,
                CreateSnapshotCommitment(state.Snapshot),
                state.Snapshot.ExecutionCommitmentSha256,
                context.AcceptedPlan.ExecutionCommitment,
                artifact.CheckpointRevision,
                artifact.Sha256,
                resultCommitment,
                request.ArtifactSha256,
                resultCommitment,
                observation.AcceptedProjectionCommitmentSha256,
                configuration.OperationId,
                configuration.GenerationId,
                append?.OperationId,
                append?.AuthorityCommitmentSha256,
                append?.CandidateCommitmentSha256,
                append?.GenerationId,
                append?.SnapshotCommitmentSha256,
                append?.PolicyCommitmentSha256,
                configuration.TerminalPredecessor,
                configuration.Transition,
                new(budget.MaximumBlocks, budget.MaximumChangedFiles, budget.MaximumPatchBytes),
                configuration.Policy,
                changed,
                append?.ChangedFiles ?? [],
                configuration.ClosedUnmergedSuccessorAuthorization));

            field = GitHubPublicationFieldId.Payload;
            var capture = context.Session.RepositorySession.CaptureDocumentationPatchRepositoryBaseline(
                context.CancellationToken);
            Require(capture.Status == DocumentationPatchRepositoryBaselineStatus.Captured
                && capture.Baseline is not null);
            var baseline = capture.Baseline!;
            var payloadInputs = SelectPayload(candidate, baseline, authority, context.CancellationToken);
            var payload = GitHubPublicationFactory.CreatePayload(authority, payloadInputs);
            context.CancellationToken.ThrowIfCancellationRequested();
            Require(baseline.Rebind(context.CancellationToken).Status
                    == DocumentationPatchRepositoryRebindStatus.Unchanged
                && !context.Session.RepositorySession.IsDisposed
                && context.Observations.IsBoundToObservationSession(context.Session));
            return GitHubPublicationRequest.Accepted(authority, payload);
        }
        catch (OperationCanceledException) when (context?.CancellationToken.IsCancellationRequested == true)
        {
            throw;
        }
        catch (GitHubPublicationValidationException exception)
        {
            return GitHubPublicationRequest.Invalid(exception.Code, field);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            return GitHubPublicationRequest.Invalid(GitHubPublicationValidationCode.InvalidCorrelation, field);
        }
    }

    private static ImmutableArray<GitHubChangedFileAuthority> CorrelateChangedFiles(
        CampaignCandidateObservation observation,
        DocumentationPatchValidationResult result)
    {
        Require(!observation.ChangedFiles.IsDefaultOrEmpty
            && !result.ChangedFiles.IsDefaultOrEmpty
            && observation.ChangedFiles.Length == result.ChangedFiles.Length);
        var byPath = result.ChangedFiles.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var resultPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var observedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in result.ChangedFiles)
        {
            Require(resultPaths.Add(file.Path));
        }
        var changed = ImmutableArray.CreateBuilder<GitHubChangedFileAuthority>(observation.ChangedFiles.Length);
        foreach (var file in observation.ChangedFiles)
        {
            Require(observedPaths.Add(file.Path) && byPath.TryGetValue(file.Path, out _));
            var actual = byPath[file.Path];
            Require(file.OriginalFileSha256 == actual.OriginalFileSha256
                && file.CandidateFileSha256 == actual.CandidateFileSha256
                && file.ChangedDocumentationBlockCount == actual.ChangedDocumentationBlockCount
                && file.OriginalDocumentationByteCount == actual.OriginalDocumentationByteCount
                && file.CandidateDocumentationByteCount == actual.CandidateDocumentationByteCount
                && file.OriginalDocumentationLineCount == actual.OriginalDocumentationLineCount
                && file.CandidateDocumentationLineCount == actual.CandidateDocumentationLineCount);
            changed.Add(new(file.Path, file.OriginalFileSha256, file.CandidateFileSha256,
                file.ChangedDocumentationBlockCount, file.OriginalDocumentationByteCount,
                file.CandidateDocumentationByteCount, file.OriginalDocumentationLineCount,
                file.CandidateDocumentationLineCount));
        }
        return changed.MoveToImmutable();
    }

    private static ImmutableArray<GitHubChangedFilePayloadInput> SelectPayload(
        DocumentationPatchAcceptedCandidate candidate,
        DocumentationPatchRepositoryBaseline baseline,
        ValidatedGitHubPublicationAuthority authority,
        CancellationToken cancellationToken)
    {
        Require(!candidate.Files.IsDefault && candidate.Files.Length == baseline.Entries.Length);
        var originals = baseline.Entries.ToDictionary(file => file.RepositoryPath, StringComparer.Ordinal);
        var changes = authority.ChangedFiles.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selected = ImmutableArray.CreateBuilder<GitHubChangedFilePayloadInput>(changes.Count);
        foreach (var file in candidate.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Require(file is not null && !file.Bytes.IsDefault
                && paths.Add(file.RepositoryPath) && originals.ContainsKey(file.RepositoryPath));
            var original = originals[file!.RepositoryPath];
            if (changes.TryGetValue(file.RepositoryPath, out var change))
            {
                Require(original.Sha256 == change.OriginalFileSha256
                    && file.Sha256 == change.CandidateFileSha256);
                // Core bounds lengths before allocation and hashes its owned copy.
                selected.Add(new(file.RepositoryPath, file.Bytes.AsMemory()));
            }
            else
            {
                Require(file.Sha256 == original.Sha256
                    && file.Bytes.AsSpan().SequenceEqual(original.Bytes.AsSpan()));
            }
        }
        Require(selected.Count == changes.Count);
        return selected.MoveToImmutable();
    }

    internal static string CreateSnapshotCommitment(CampaignStateSnapshotAuthority snapshot)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Add("contract-scribe/github-m4-snapshot/v1");
        Add("opaque-binding"); Add(snapshot.OpaqueSnapshotBinding);
        Add("repository"); Add(snapshot.RepositoryCommitmentSha256);
        Add("input"); Add(snapshot.InputCommitmentSha256);
        Add("input-identity"); Add(snapshot.InputIdentityCommitmentSha256);
        Add("policy-authority"); Add(snapshot.PolicyAuthorityCommitmentSha256);
        Add("target-profile"); Add(ClassificationVocabulary.GetId(snapshot.TargetProfile));
        Add("execution"); Add(snapshot.ExecutionCommitmentSha256);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

        void Add(string value)
        {
            var bytes = StrictUtf8.GetBytes(value);
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
    }

    private static bool ExactArtifact(CampaignCheckpointArtifact left, CampaignCheckpointArtifact right) =>
        left.CheckpointRevision == right.CheckpointRevision
        && left.Sha256 == right.Sha256
        && left.ExactUtf8Json.AsSpan().SequenceEqual(right.ExactUtf8Json.AsSpan());

    private static void Require(bool condition)
    {
        if (!condition)
        {
            throw new ArgumentException("github-publication.local-invalid");
        }
    }
}
