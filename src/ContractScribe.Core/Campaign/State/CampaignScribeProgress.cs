using System.Collections.Immutable;

namespace ContractScribe.Core;

public sealed record CampaignScribeSkipProjection(DocumentationScribeSkipReason Reason,
    ImmutableArray<CampaignEvidenceProjection> Evidence);

/// <summary>Factory-derived shared-session membership and immutable accepted content correlation.</summary>
public sealed record CampaignScribeMemberProgress(string WorkItemKey, string TargetId, int OuterAttemptOrdinal,
    string SessionAnchorWorkItemKey, int SessionAnchorOuterAttemptOrdinal, DocumentationScribeAttemptId AttemptId,
    string ScribeRequestSha256, string? ContentSha256, CampaignScribeSkipProjection? Skip);

public sealed record CampaignScribeSessionProgress(string AnchorWorkItemKey, int AnchorOuterAttemptOrdinal,
    DocumentationScribeAttemptId AttemptId, ImmutableArray<string> AdmittedWorkItemKeys, bool Ended);

/// <summary>Source-free cumulative batch progress; member receipts and Runtime notification receipts are distinct.</summary>
public sealed record CampaignScribeProgress(string BatchIdentity, int RepairCount, int NextSequence,
    string? LastProgressSha256, string? LastProgressRequestSha256, DocumentationScribeAttemptId? LastProgressAttemptId,
    bool LastProgressStopped, CampaignScribeSessionProgress? Session,
    ImmutableArray<CampaignScribeMemberProgress> Members)
{
    public ImmutableArray<DocumentationScribeTargetFailure> TargetFailures { get; init; } = [];
    public bool LastProgressFinished { get; init; }
}

public static partial class CampaignStateFactory
{
    private static void ValidateScribeProgress(CampaignCheckpointState state)
    {
        if (state.ScribeProgress is not { } progress) return;
        Require(progress.BatchIdentity == state.Batch.Identity
            && progress.RepairCount is >= 0 and <= DocumentationScribeValidation.MaximumInvalidSubmissions
            && progress.NextSequence >= 1 && !progress.Members.IsDefault
            && progress.Members.Length <= state.WorkItems.Length,
            CampaignStateValidationCode.InvalidBound);
        Require(progress.LastProgressSha256 is null
            ? progress.LastProgressRequestSha256 is null && progress.LastProgressAttemptId is null && !progress.LastProgressStopped
            : IsSha256(progress.LastProgressSha256) && IsSha256(progress.LastProgressRequestSha256)
                && progress.LastProgressAttemptId is { } observedAttempt
                && DocumentationScribeAttemptId.TryParse(observedAttempt.Value, out _),
            CampaignStateValidationCode.InvalidCorrelation);
        var selected = state.Batch.CompleteTargets.Where(target => state.Batch.SelectedTargetKeys.Contains(target.TargetKey, StringComparer.Ordinal)
            && target.Dispatchable).Select(target => target.WorkItemKey).ToHashSet(StringComparer.Ordinal);
        var workByKey = state.WorkItems.ToDictionary(work => work.WorkItemKey, StringComparer.Ordinal);
        var order = state.WorkItems.Select((work, index) => (work.WorkItemKey, index))
            .ToDictionary(pair => pair.WorkItemKey, pair => pair.index, StringComparer.Ordinal);
        var previous = -1;
        foreach (var member in progress.Members)
        {
            Require(member is not null && selected.Contains(member.WorkItemKey)
                && selected.Contains(member.SessionAnchorWorkItemKey)
                && order[member.WorkItemKey] > previous
                && IsOpaqueId(member.TargetId, DocumentationScribeContract.MaximumIdentifierScalars)
                && member.OuterAttemptOrdinal > 0 && member.OuterAttemptOrdinal == workByKey[member.WorkItemKey].OuterAttemptCount
                && member.SessionAnchorOuterAttemptOrdinal > 0
                && member.SessionAnchorOuterAttemptOrdinal <= workByKey[member.SessionAnchorWorkItemKey].OuterAttemptCount
                && CreateScribeAttemptId(state.Snapshot.ExecutionCommitmentSha256,
                    state.ConfiguredCeilings.ScribeExecutionAuthority, member.SessionAnchorWorkItemKey,
                    member.SessionAnchorOuterAttemptOrdinal) == member.AttemptId
                && IsSha256(member.ScribeRequestSha256), CampaignStateValidationCode.InvalidCorrelation);
            previous = order[member.WorkItemKey];
            var work = workByKey[member.WorkItemKey];
            Require(member.ContentSha256 is null ? member.Skip is null
                : IsSha256(member.ContentSha256) && (member.Skip is null
                    ? work.TrustedProposal is { } proposal && proposal.HistoricalAttemptId == member.AttemptId
                        && proposal.HistoricalScribeRequestSha256 == member.ScribeRequestSha256
                        || work.ClosedOutcome is { Stage: CampaignWorkOutcomeStage.Patch, Code: CampaignWorkOutcomeCode.PatchRejected }
                    : work.Status == CampaignWorkStatus.Closed && work.ClosedOutcome?.Stage == CampaignWorkOutcomeStage.Scribe
                        && work.ClosedOutcome.AttemptId == member.AttemptId
                        && work.ClosedOutcome.ScribeRequestSha256 == member.ScribeRequestSha256),
                CampaignStateValidationCode.InvalidCorrelation);
            if (member.Skip is { } skip)
            {
                Require(Enum.IsDefined(skip.Reason) && !skip.Evidence.IsDefault
                    && skip.Evidence.Length <= CampaignStateContract.MaximumEvidenceReferencesPerBlock,
                    CampaignStateValidationCode.InvalidBound);
                Require(member.ContentSha256 == DocumentationScribeValidation.CreateSkipContentSha256(skip.Reason,
                        skip.Evidence.Select(evidence => evidence.EvidenceReferenceId).ToImmutableArray())
                    && work.ClosedOutcome!.Code == (skip.Reason == DocumentationScribeSkipReason.InsufficientEvidence
                        ? CampaignWorkOutcomeCode.InsufficientEvidence : CampaignWorkOutcomeCode.UnsupportedDomain),
                    CampaignStateValidationCode.InvalidCorrelation);
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var evidence in skip.Evidence)
                    Require(ids.Add(evidence.EvidenceReferenceId) && DocumentationScribeValidation.IsStableEvidenceProjection(
                        evidence.EvidenceReferenceId, evidence.Subject, evidence.Kind, evidence.Relation, evidence.Authority,
                        evidence.Locator, evidence.ContentSha256, evidence.OriginalUtf8ByteCount, evidence.IncludedUtf8ByteCount,
                        evidence.IsTruncated, evidence.ClaimCategoryIds), CampaignStateValidationCode.InvalidReference);
            }
        }
        Require(!progress.TargetFailures.IsDefault && progress.TargetFailures.Length <= progress.Members.Length
            && progress.TargetFailures.Select(failure => failure.TargetId).Distinct(StringComparer.Ordinal).Count() == progress.TargetFailures.Length
            && (!progress.LastProgressFinished || progress.LastProgressStopped), CampaignStateValidationCode.InvalidBound);
        foreach (var failure in progress.TargetFailures)
            Require(progress.Members.Any(member => member.TargetId == failure.TargetId && member.ContentSha256 is null)
                && failure.ValidationCode.Length is > 0 and <= 128
                && failure.ValidationCode.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-'),
                CampaignStateValidationCode.InvalidCorrelation);
        if (progress.Session is { } session)
        {
            Require(selected.Contains(session.AnchorWorkItemKey) && session.AnchorOuterAttemptOrdinal > 0
                && session.AnchorOuterAttemptOrdinal <= workByKey[session.AnchorWorkItemKey].OuterAttemptCount
                && CreateScribeAttemptId(state.Snapshot.ExecutionCommitmentSha256, state.ConfiguredCeilings.ScribeExecutionAuthority,
                    session.AnchorWorkItemKey, session.AnchorOuterAttemptOrdinal) == session.AttemptId
                && !session.AdmittedWorkItemKeys.IsDefaultOrEmpty
                && session.AdmittedWorkItemKeys.Length <= selected.Count
                && session.AdmittedWorkItemKeys.Distinct(StringComparer.Ordinal).Count() == session.AdmittedWorkItemKeys.Length
                && session.AdmittedWorkItemKeys.All(selected.Contains), CampaignStateValidationCode.InvalidCorrelation);
            if (state.ActiveReservation is CampaignProviderReservation claim)
                Require(!session.Ended && session.AnchorWorkItemKey == claim.WorkItemKey && session.AttemptId == claim.AttemptId,
                    CampaignStateValidationCode.InvalidCorrelation);
        }
    }
}

public static partial class CampaignStateReducer
{
    internal static CampaignTransitionResult ResumeCancelledScribeProgress(CampaignAcceptedCheckpoint accepted,
        CampaignScribeExecutionCapability capability, string styleId, System.Text.Json.JsonElement style,
        string inputIdentity, CampaignPlanningInput planning, CampaignWorkPlan plan)
    {
        var predecessor = accepted.Artifact;
        try
        {
            var state = predecessor.State;
            CampaignStateFactory.ValidateCurrentContext(state, capability, styleId, style, inputIdentity, planning, plan);
            if (state.TerminalOutcome?.Kind != CampaignTerminalKind.Cancelled || state.ActiveReservation is not null
                || state.ScribeProgress is not { } progress || !progress.Members.Any(member => member.ContentSha256 is not null))
                return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
            return Applied(predecessor, CreateState(state, NextRevision(state.CheckpointRevision), state.LineageCharges,
                state.WorkItems, null, state.CandidateObservation, state.CumulativeOutcome, null, state.Predecessor));
        }
        catch (Exception exception) when (IsBoundedContractFailure(exception) || exception is OverflowException)
        { return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority); }
    }

    private static bool RequestTargetsAllowed(CampaignCheckpointState state, DocumentationScribeRequest request,
        CampaignInvocationTargetAllowance allowance) => request.ExecutableTargetIds.All(id =>
    {
        var symbol = request.Manifest.Single(member => member.TargetId == id).Target.SymbolRef;
        var key = state.Batch.CompleteTargets.Single(target => target.SymbolRef == symbol).WorkItemKey;
        var prior = state.WorkItems.Single(item => item.WorkItemKey == key);
        return CampaignStateFactory.AllowsTarget(state, key, allowance)
            || state.ScribeProgress?.Members.Any(member => member.WorkItemKey == key && member.ContentSha256 is not null) == true
                && (prior.Status is CampaignWorkStatus.ProposalComplete or CampaignWorkStatus.Accepted
                    || prior.ClosedOutcome?.Code is CampaignWorkOutcomeCode.InsufficientEvidence or CampaignWorkOutcomeCode.UnsupportedDomain);
    });

    private static CampaignScribeProgress StartScribeSession(CampaignCheckpointState state, string anchorKey,
        int ordinal, DocumentationScribeAttemptId attempt, string requestSha256, DocumentationScribeRequest? request)
    {
        var progress = state.ScribeProgress ?? new CampaignScribeProgress(state.Batch.Identity, 0, 1, null, null, null, false, null, []);
        var admitted = request is null ? [anchorKey] : request.ExecutableTargetIds.Select(id =>
        {
            var symbol = request.Manifest.Single(member => member.TargetId == id).Target.SymbolRef;
            return state.Batch.CompleteTargets.Single(target => target.SymbolRef == symbol).WorkItemKey;
        }).ToImmutableArray();
        var targetId = request is null ? new string('a', DocumentationScribeContract.MaximumIdentifierScalars)
            : request.Manifest.Single(member => state.Batch.CompleteTargets.Any(target => target.WorkItemKey == anchorKey
                && target.SymbolRef == member.Target.SymbolRef)).TargetId;
        var anchor = new CampaignScribeMemberProgress(anchorKey, targetId, ordinal, anchorKey, ordinal, attempt, requestSha256, null, null);
        var members = state.WorkItems.Select(work => work.WorkItemKey == anchorKey ? anchor
            : progress.Members.SingleOrDefault(member => member.WorkItemKey == work.WorkItemKey))
            .OfType<CampaignScribeMemberProgress>().ToImmutableArray();
        return progress with { Session = new(anchorKey, ordinal, attempt, admitted, false), Members = members };
    }
}
