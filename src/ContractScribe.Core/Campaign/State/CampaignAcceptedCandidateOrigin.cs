using System.Collections.Immutable;
using System.Text.Json;

namespace ContractScribe.Core;

public static partial class CampaignStateFactory
{
    internal static CampaignAcceptedCandidateOrigin? CreateSelfOrigin(CampaignCheckpointState state) =>
        state.CandidateObservation is not { } candidate ? null : new(
            state.CheckpointRevision, null, state.ProductRevision, state.CampaignLineage,
            state.Snapshot, state.ConfiguredCeilings.CampaignConfigurationCommitmentSha256, candidate);

    // Only the exact, externally accepted predecessor supplies the retained digest. Temporary
    // reservation construction states never become origins, even when they share a revision.
    internal static CampaignCheckpointState PreserveOrigin(
        CampaignCheckpointArtifact predecessor, CampaignCheckpointState successor)
    {
        var prior = predecessor.State.AcceptedCandidateOrigin;
        var candidate = successor.CandidateObservation;
        if (prior is not null && candidate is not null
            && successor.ProductRevision == predecessor.State.ProductRevision
            && successor.CampaignLineage == predecessor.State.CampaignLineage
            && successor.Snapshot == predecessor.State.Snapshot
            && successor.ConfiguredCeilings.CampaignConfigurationCommitmentSha256
                == prior.CampaignConfigurationCommitmentSha256
            && predecessor.State.CandidateObservation!.AcceptedWorkItemKeys.SequenceEqual(
                candidate.AcceptedWorkItemKeys, StringComparer.Ordinal))
        {
            successor = successor with
            {
                AcceptedCandidateOrigin = prior with
                {
                    CheckpointSha256 = prior.CheckpointSha256 ?? predecessor.Sha256,
                },
            };
        }
        Validate(successor);
        return successor;
    }

    private static void ValidateOrigin(CampaignCheckpointState state)
    {
        var origin = state.AcceptedCandidateOrigin;
        Require((origin is null) == (state.CandidateObservation is null),
            CampaignStateValidationCode.InvalidCorrelation);
        if (origin is null)
        {
            return;
        }
        Require(origin.ProductRevision == state.ProductRevision
            && origin.CampaignLineage == state.CampaignLineage
            && origin.Snapshot == state.Snapshot
            && origin.CampaignConfigurationCommitmentSha256
                == state.ConfiguredCeilings.CampaignConfigurationCommitmentSha256
            && origin.CheckpointRevision >= 0
            && (origin.CheckpointSha256 is null
                ? origin.CheckpointRevision == state.CheckpointRevision
                : IsSha256(origin.CheckpointSha256) && origin.CheckpointRevision < state.CheckpointRevision),
            CampaignStateValidationCode.InvalidCorrelation);
        // Historical facts must be structurally valid, but may disagree with a newly settled
        // reconstruction. Admission rejects that disagreement without erasing its charges.
        var historical = new CampaignCheckpointState(
            state.ProductRevision, state.CampaignLineage, state.Snapshot, state.CheckpointRevision,
            state.ConfiguredCeilings, state.LineageCharges, state.WorkItems, state.ActiveReservation,
            origin.CandidateObservation, state.CumulativeOutcome, state.KnownCompletedOperations,
            state.TerminalOutcome, state.Predecessor);
        ValidateCandidate(historical);
        if (origin.CheckpointSha256 is null)
        {
            Require(CandidateFactsEqual(origin.CandidateObservation, state.CandidateObservation!)
                && origin.CandidateObservation.PatchRequestSha256 == state.CandidateObservation!.PatchRequestSha256
                && origin.CandidateObservation.PatchResultCommitmentSha256
                    == state.CandidateObservation.PatchResultCommitmentSha256,
                CampaignStateValidationCode.InvalidCorrelation);
        }
    }

    private static bool CandidateFactsEqual(CampaignCandidateObservation left, CampaignCandidateObservation right) =>
        left.AcceptedProjectionCommitmentSha256 == right.AcceptedProjectionCommitmentSha256
        && left.AcceptedWorkItemKeys.SequenceEqual(right.AcceptedWorkItemKeys, StringComparer.Ordinal)
        && left.ChangedFiles.SequenceEqual(right.ChangedFiles);

    internal static CampaignAcceptedCandidateOrigin ProveAcceptedCandidateOrigin(
        CampaignCheckpointArtifact artifact, DocumentationPatchRequest request,
        DocumentationPatchValidationResult result)
    {
        Validate(artifact.State);
        var canonical = CampaignStateJson.CreateArtifact(artifact.State);
        Require(canonical.Sha256 == artifact.Sha256
            && canonical.ExactUtf8Json.AsSpan().SequenceEqual(artifact.ExactUtf8Json.AsSpan()),
            CampaignStateValidationCode.InvalidCorrelation);
        var current = artifact.State.CandidateObservation;
        var origin = artifact.State.AcceptedCandidateOrigin;
        Require(current is not null && origin is not null, CampaignStateValidationCode.InvalidCorrelation);
        // This validates the real fresh request/result first. Historical framing is private and
        // cannot be used to choose an arbitrary request hash or authorize an invocation.
        var freshCommitment = CreatePatchResultCommitment(request, result);
        Require(result.Outcome == DocumentationPatchOutcome.Accepted
            && request.ArtifactSha256 == current.PatchRequestSha256
            && freshCommitment == current.PatchResultCommitmentSha256
            && CandidateFactsEqual(origin.CandidateObservation, current)
            && FramePatchResult(origin.CandidateObservation.PatchRequestSha256, result)
                == origin.CandidateObservation.PatchResultCommitmentSha256,
            CampaignStateValidationCode.InvalidCorrelation);
        return origin with { CheckpointSha256 = origin.CheckpointSha256 ?? artifact.Sha256 };
    }

    internal static void ValidatePatchSuccessorCapacity(
        CampaignCheckpointArtifact predecessor, CampaignCheckpointState reservation,
        DocumentationPatchRequest request)
    {
        var intended = PreserveOrigin(predecessor, reservation);
        var currentBytes = CampaignStateJson.CreateArtifact(intended).ExactUtf8Json.Length;
        var growth = CampaignStateJson.MeasurePatchSuccessorGrowth(intended, request);
        Require((long)currentBytes + growth <= CampaignStateContract.MaximumArtifactUtf8Bytes,
            CampaignStateValidationCode.InvalidBound);
    }
}

public static partial class CampaignStateJson
{
    private static void WriteOrigin(Utf8JsonWriter writer, CampaignAcceptedCandidateOrigin? origin)
    {
        if (origin is null)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartObject();
        writer.WriteNumber("checkpointRevision", origin.CheckpointRevision);
        writer.WriteString("checkpointSha256", origin.CheckpointSha256);
        WriteProduct(writer, "productRevision", origin.ProductRevision);
        writer.WriteString("campaignLineage", origin.CampaignLineage);
        WriteSnapshot(writer, "snapshot", origin.Snapshot);
        writer.WriteString("campaignConfigurationCommitmentSha256", origin.CampaignConfigurationCommitmentSha256);
        writer.WritePropertyName("candidateObservation");
        WriteCandidate(writer, origin.CandidateObservation);
        writer.WriteEndObject();
    }

    private static CampaignAcceptedCandidateOrigin? ParseOrigin(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        ExpectObject(element, "checkpointRevision", "checkpointSha256", "productRevision",
            "campaignLineage", "snapshot", "campaignConfigurationCommitmentSha256", "candidateObservation");
        return new(ReadInt64(element, "checkpointRevision"), ReadNullableString(element, "checkpointSha256"),
            ParseProduct(element.GetProperty("productRevision")), ReadString(element, "campaignLineage"),
            ParseSnapshot(element.GetProperty("snapshot")), ReadString(element, "campaignConfigurationCommitmentSha256"),
            ParseCandidate(element.GetProperty("candidateObservation"))
                ?? throw CampaignStateFactory.Fail(CampaignStateValidationCode.InvalidShape, "Origin candidate is absent."));
    }

    internal static long MeasurePatchSuccessorGrowth(CampaignCheckpointState state, DocumentationPatchRequest request)
    {
        var hash = new string('f', 64);
        var files = request.Blocks.Select(block => block.Locator)
            .OfType<DocumentationPatchRepositoryLocator>().Select(locator => locator.Path)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(path => new CampaignChangedFileObservation(path, hash, hash,
                int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue)).ToImmutableArray();
        var candidate = new CampaignCandidateObservation(
            request.Blocks.Select(block => block.BlockId).ToImmutableArray(), hash, files, hash, hash);
        var origin = new CampaignAcceptedCandidateOrigin(CampaignStateContract.MaximumObservation, hash,
            state.ProductRevision, state.CampaignLineage, state.Snapshot,
            state.ConfiguredCeilings.CampaignConfigurationCommitmentSha256, candidate);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            WriteCandidate(writer, candidate);
            WriteOrigin(writer, origin);
            writer.WriteEndArray();
        }
        // Keep the existing representations in the bound, and add both future observations.
        // 4096 covers fixed cumulative/reservation/charge scalar growth; each selected status
        // and count has another 64 bytes. The origin above already includes a retained SHA.
        return checked(stream.Length + 4096L + 64L * request.Blocks.Length);
    }
}
