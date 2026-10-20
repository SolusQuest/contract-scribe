using System.Security.Cryptography;
using System.Text;

namespace ContractScribe.Core;

/// <summary>Retained charges whose exact rows live in an authenticated immutable predecessor witness.</summary>
public sealed record CampaignArchivedDispatchPrefix(string WitnessSha256, CampaignLineageCharges Charges);

/// <summary>Core-checked correlation with the remote adapter's authenticated predecessor witness.</summary>
public sealed class CampaignVerifiedArchive
{
    internal CampaignVerifiedArchive(string checkpointSha256, string witnessSha256)
    {
        CheckpointSha256 = checkpointSha256;
        WitnessSha256 = witnessSha256;
    }

    internal string CheckpointSha256 { get; }
    public string WitnessSha256 { get; }
    public override string ToString() => nameof(CampaignVerifiedArchive);
}

public static class CampaignDispatchArchive
{
    /// <summary>Archives complete attempts at a quiescent boundary; paused attempts keep their ordinal history.</summary>
    public static CampaignCheckpointArtifact? Prepare(CampaignAcceptedCheckpoint accepted)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        return PrepareArtifact(accepted.Artifact);
    }

    private static CampaignCheckpointArtifact? PrepareArtifact(CampaignCheckpointArtifact predecessor)
    {
        var state = predecessor.State;
        if (state.ActiveReservation is not null || state.DispatchObservations.IsEmpty
            || state.WorkItems.Any(work => work.PausedProviderAttempt is not null)) return null;
        var prefix = Prefix(predecessor);
        var next = CampaignStateFactory.CreateValidated(state.ProductRevision, state.CampaignLineage, state.Snapshot,
            checked(state.CheckpointRevision + 1), state.ConfiguredCeilings, state.LineageCharges, state.WorkItems,
            state.Batch, candidateObservation: state.CandidateObservation, cumulativeOutcome: state.CumulativeOutcome,
            knownCompletedOperations: state.KnownCompletedOperations, terminalOutcome: state.TerminalOutcome,
            predecessor: state.Predecessor, scribeProgress: state.ScribeProgress, archivedDispatchPrefix: prefix);
        return CampaignStateJson.CreateArtifact(CampaignStateFactory.PreserveOrigin(predecessor, next));
    }

    /// <summary>Checks the exact compacted successor before accepting a new archive tip.</summary>
    public static CampaignVerifiedArchive VerifyTransition(CampaignCheckpointArtifact predecessor,
        CampaignCheckpointArtifact successor)
    {
        ArgumentNullException.ThrowIfNull(predecessor);
        ArgumentNullException.ThrowIfNull(successor);
        if (PrepareArtifact(predecessor)?.Sha256 != successor.Sha256)
            throw new CampaignStateValidationException(CampaignStateValidationCode.InvalidCorrelation,
                "The archive transition must preserve the exact predecessor state apart from compaction.");
        return Verify(predecessor, successor);
    }

    /// <summary>
    /// Validates exact predecessor rows and retained accounting. The adapter must first authenticate this
    /// predecessor as the actual parent checkpoint of the immutable archive transition, including prior-tip linkage.
    /// </summary>
    public static CampaignVerifiedArchive Verify(CampaignCheckpointArtifact predecessor, CampaignCheckpointArtifact current)
    {
        ArgumentNullException.ThrowIfNull(predecessor);
        ArgumentNullException.ThrowIfNull(current);
        var prefix = Prefix(predecessor);
        if (current.State.CampaignLineage != predecessor.State.CampaignLineage
            || current.CheckpointRevision <= predecessor.CheckpointRevision
            || current.State.ArchivedDispatchPrefix != prefix)
            throw new CampaignStateValidationException(CampaignStateValidationCode.InvalidCorrelation,
                "The archive witness does not match the checkpoint.");
        return new(current.Sha256, prefix.WitnessSha256);
    }

    private static CampaignArchivedDispatchPrefix Prefix(CampaignCheckpointArtifact predecessor)
    {
        var state = predecessor.State;
        CampaignStateFactory.Validate(state);
        if (state.ActiveReservation is not null || state.DispatchObservations.IsEmpty
            || state.WorkItems.Any(work => work.PausedProviderAttempt is not null)
            || state.DispatchObservations.Any(row => row.Charges is null))
            throw new CampaignStateValidationException(CampaignStateValidationCode.InvalidCorrelation,
                "Only complete request histories can be archived.");
        var charges = state.ArchivedDispatchPrefix?.Charges ?? CampaignStateFactory.EmptyChargesForAcceptance();
        foreach (var row in state.DispatchObservations)
        {
            var delta = row.Charges!;
            charges = charges with
            {
                ProviderRequests = Add(charges.ProviderRequests, delta.ProviderRequests),
                InputTokens = Add(charges.InputTokens, delta.InputTokens),
                CachedInputTokens = Add(charges.CachedInputTokens, delta.CachedInputTokens),
                UncachedInputTokens = Add(charges.UncachedInputTokens, delta.UncachedInputTokens),
                OutputTokens = Add(charges.OutputTokens, delta.OutputTokens),
                ReasoningTokens = Add(charges.ReasoningTokens, delta.ReasoningTokens),
                CostMicrounits = Add(charges.CostMicrounits, delta.CostMicrounits),
                ActiveElapsedMilliseconds = Add(charges.ActiveElapsedMilliseconds, delta.ActiveElapsedMilliseconds),
                HasUnpricedCostHistory = state.LineageCharges.HasUnpricedCostHistory,
            };
        }
        return new(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            "contract-scribe/archive-witness/v1\n" + predecessor.Sha256))), charges);
    }

    private static CampaignChargeObservation Add(CampaignChargeObservation left, CampaignChargeObservation right) => new(
        left.Observed is null && right.Observed is null ? null : checked((left.Observed ?? 0) + (right.Observed ?? 0)),
        checked(left.ConservativeUnobserved + right.ConservativeUnobserved), checked(left.TotalCharged + right.TotalCharged));
}

public static partial class CampaignStateReducer
{
    public static CampaignTransitionResult ArchiveDispatchHistory(CampaignAcceptedCheckpoint accepted)
    {
        var next = CampaignDispatchArchive.Prepare(accepted);
        return next is null ? Reject(accepted.Artifact, CampaignTransitionFailure.InvalidAuthority)
            : Applied(accepted.Artifact, next.State);
    }
}
