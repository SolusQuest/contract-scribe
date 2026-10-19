using System.Collections.Immutable;

namespace ContractScribe.Core;

public static partial class CampaignStateReducer
{
    internal static CampaignTransitionResult AcceptScribeSubmission(CampaignProviderInvocationAuthority invocation,
        DocumentationScribeValidatedSubmission submission)
    {
        var predecessor = invocation.AcceptedCheckpoint.Artifact;
        try
        {
            var claim = CurrentClaim(invocation);
            var state = predecessor.State;
            var progress = state.ScribeProgress;
            var session = progress?.Session;
            if (claim.OperationKind != CampaignProviderOperationKind.Host || claim.HostPhase != CampaignProviderHostPhase.TerminalSubmission
                || progress is null || session is not { Ended: false } || session.AttemptId != claim.AttemptId
                || submission.ScribeRequestSha256 != invocation.ScribeRequestSha256)
                return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
            var member = invocation.Request.Manifest.Single(item => item.TargetId == submission.TargetId);
            var workKey = state.Batch.CompleteTargets.Single(target => target.SymbolRef == member.Target.SymbolRef).WorkItemKey;
            if (!session.AdmittedWorkItemKeys.Contains(workKey, StringComparer.Ordinal)
                || !invocation.Request.ExecutableTargetIds.Contains(submission.TargetId, StringComparer.Ordinal))
                return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
            var existing = progress.Members.SingleOrDefault(item => item.WorkItemKey == workKey);
            if (existing?.ContentSha256 is not null)
                return existing.ContentSha256 == submission.ContentSha256 && existing.ScribeRequestSha256 == submission.ScribeRequestSha256
                    ? new(CampaignTransitionKind.Unchanged, predecessor, predecessor, CampaignTransitionFailure.None, claim.AttemptId)
                    : Reject(predecessor, CampaignTransitionFailure.ConflictingReplay);
            var started = StartScribeMember(state, invocation.Request, workKey, submission.TargetId, progress);
            var interim = CreateState(state, state.CheckpointRevision, state.LineageCharges, started.Items, claim,
                state.CandidateObservation, state.CumulativeOutcome, state.TerminalOutcome, state.Predecessor,
                scribeProgress: started.Progress);
            CampaignTrustedProposal? proposal = null;
            CampaignWorkClosedOutcome? closed = null;
            CampaignScribeSkipProjection? skipProjection = null;
            var status = CampaignWorkStatus.ProposalComplete;
            if (submission.Result is DocumentationScribeProposalTerminal)
            {
                proposal = CampaignStateFactory.CreateTrustedSubmissionProposal(interim, invocation.ExecutionCapability,
                    invocation.StyleConfigurationId, invocation.ValidatedStyleConfigurationProjection,
                    invocation.PlanningInput, invocation.AcceptedPlan, workKey, invocation.Request, invocation.AttemptId, submission);
                var admitted = CampaignStateFactory.EvaluateTrustedProposalAdmission(interim, workKey, proposal,
                    new(invocation.Request.Context.RepositoryContextRef, invocation.Request.Context.InputIdentity,
                        invocation.Request.Context.TargetProfile));
                if (admitted == CampaignTrustedProposalAdmissionKind.Invalid)
                    return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
                if (admitted == CampaignTrustedProposalAdmissionKind.OverBound)
                {
                    // The completed result is known, but its body cannot join the bounded
                    // cumulative M2 request. Keep only the closed commitment, preserving
                    // every previously accepted sibling for independent M2 validation.
                    return CloseOverboundScribeMember(predecessor, claim, started.Items, started.Progress, workKey,
                        proposal.ProposalCommitmentSha256);
                }
            }
            else if (submission.Result is DocumentationScribeSkipTerminal skip)
            {
                status = CampaignWorkStatus.Closed;
                var catalog = invocation.Request.EvidenceReferences.Concat(submission.DynamicEvidenceReferences).ToImmutableArray();
                var evidence = skip.EvidenceReferenceIds.Select(id => catalog.Single(reference => reference.EvidenceReferenceId == id)).ToImmutableArray();
                if (evidence.Length != skip.EvidenceReferenceIds.Length)
                    return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
                skipProjection = new(skip.Reason, evidence.Select(CampaignStateFactory.ProjectEvidence).ToImmutableArray());
                closed = new(CampaignWorkOutcomeStage.Scribe, skip.Reason == DocumentationScribeSkipReason.InsufficientEvidence
                    ? CampaignWorkOutcomeCode.InsufficientEvidence : CampaignWorkOutcomeCode.UnsupportedDomain,
                    null, invocation.ScribeRequestSha256, invocation.AttemptId, null, null, null, workKey);
            }
            else return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
            var binding = started.Progress.Members.Single(item => item.WorkItemKey == workKey) with
            { ContentSha256 = submission.ContentSha256, ScribeRequestSha256 = submission.ScribeRequestSha256, Skip = skipProjection };
            var nextProgress = started.Progress with
            {
                Members = started.Progress.Members.Select(item => item.WorkItemKey == workKey ? binding : item).ToImmutableArray(),
                TargetFailures = started.Progress.TargetFailures.Where(failure => failure.TargetId != submission.TargetId).ToImmutableArray(),
            };
            var items = ReplaceWork(started.Items, workKey, status, proposal, closed);
            try
            {
                var transition = Applied(predecessor, CreateState(state, NextRevision(state.CheckpointRevision), state.LineageCharges,
                    items, claim, state.CandidateObservation, state.CumulativeOutcome, state.TerminalOutcome, state.Predecessor,
                    scribeProgress: nextProgress), claim.AttemptId);
                ValidateProviderSettlementCapacity(transition.Artifact.State);
                return transition;
            }
            catch (CampaignStateValidationException exception) when (exception.Code == CampaignStateValidationCode.DocumentTooLarge)
            {
                return CloseOverboundScribeMember(predecessor, claim, started.Items, started.Progress, workKey,
                    proposal?.ProposalCommitmentSha256 ?? submission.ContentSha256);
            }
        }
        catch (CampaignStateValidationException exception) when (exception.Code == CampaignStateValidationCode.DocumentTooLarge)
        { return Reject(predecessor, CampaignTransitionFailure.CheckpointCapacity); }
        catch (Exception exception) when (IsBoundedContractFailure(exception) || exception is OverflowException)
        { return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority); }
    }

    private static CampaignTransitionResult CloseOverboundScribeMember(CampaignCheckpointArtifact predecessor,
        CampaignProviderReservation claim, ImmutableArray<CampaignWorkItemState> items, CampaignScribeProgress progress,
        string key, string commitment)
    {
        var state = predecessor.State;
        var closed = new CampaignWorkClosedOutcome(CampaignWorkOutcomeStage.Scribe, CampaignWorkOutcomeCode.CompletedOverBound,
            null, claim.ScribeRequestSha256, claim.AttemptId, null, null, commitment, key);
        var id = progress.Members.Single(member => member.WorkItemKey == key).TargetId;
        progress = progress with { TargetFailures = progress.TargetFailures.Where(failure => failure.TargetId != id).ToImmutableArray() };
        var transition = Applied(predecessor, CreateState(state, NextRevision(state.CheckpointRevision), state.LineageCharges,
            ReplaceWork(items, key, CampaignWorkStatus.Closed, null, closed), claim, state.CandidateObservation,
            state.CumulativeOutcome, state.TerminalOutcome, state.Predecessor, scribeProgress: progress), claim.AttemptId);
        ValidateProviderSettlementCapacity(transition.Artifact.State);
        return transition;
    }

    internal static CampaignTransitionResult AcceptScribeProgress(CampaignProviderInvocationAuthority invocation,
        DocumentationScribeProgressNotification notification)
    {
        var predecessor = invocation.AcceptedCheckpoint.Artifact;
        try
        {
            var state = predecessor.State;
            var claim = CurrentClaim(invocation);
            var progress = state.ScribeProgress;
            if (claim.OperationKind != CampaignProviderOperationKind.Host || claim.HostPhase != CampaignProviderHostPhase.TerminalSubmission
                || progress?.Session is not { Ended: false } session || session.AttemptId != invocation.AttemptId
                || notification.ScribeRequestSha256 != invocation.ScribeRequestSha256 || notification.AttemptId != invocation.AttemptId
                || notification.NextSequence < progress.NextSequence || notification.NextSequence > (long)progress.NextSequence + 1
                || notification.RepairCount < progress.RepairCount || notification.RepairCount > progress.RepairCount + 1)
                return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
            if (progress.LastProgressSha256 == notification.ProgressSha256)
                return new(CampaignTransitionKind.Unchanged, predecessor, predecessor, CampaignTransitionFailure.None, claim.AttemptId);
            var items = state.WorkItems;
            foreach (var id in notification.AttemptedTargetIds)
            {
                var target = invocation.Request.Manifest.Single(member => member.TargetId == id);
                var key = state.Batch.CompleteTargets.Single(candidate => candidate.SymbolRef == target.Target.SymbolRef).WorkItemKey;
                if (!session.AdmittedWorkItemKeys.Contains(key, StringComparer.Ordinal))
                    return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
                var started = StartScribeMember(state with { ScribeProgress = progress }, invocation.Request, key, id, progress, items);
                items = started.Items; progress = started.Progress;
            }
            foreach (var receipt in notification.Receipts)
            {
                var member = progress.Members.SingleOrDefault(item => item.TargetId == receipt.TargetId);
                // Stopped Received observations record an attempted but unconfirmed
                // Host result. They never create accepted content or a durable receipt.
                if (notification.Stopped && receipt.Stage == DocumentationScribeAcknowledgementStage.Received
                    && member is { ContentSha256: null }) continue;
                if (member?.ContentSha256 is null || receipt.Stage < DocumentationScribeAcknowledgementStage.DurablyRecorded
                    || !(member.ContentSha256 == receipt.ContentSha256 && member.ScribeRequestSha256 == notification.ScribeRequestSha256
                        || invocation.RestoredReceiptMatches(receipt.TargetId, receipt.ContentSha256)))
                    return Reject(predecessor, CampaignTransitionFailure.InvalidCorrelation);
            }
            progress = progress with
            {
                RepairCount = notification.RepairCount,
                NextSequence = notification.NextSequence,
                LastProgressSha256 = notification.ProgressSha256,
                LastProgressRequestSha256 = notification.ScribeRequestSha256,
                LastProgressAttemptId = notification.AttemptId,
                LastProgressStopped = notification.Stopped,
                LastProgressFinished = notification.Finished,
                TargetFailures = notification.Failures,
            };
            var transition = Applied(predecessor, CreateState(state, NextRevision(state.CheckpointRevision), state.LineageCharges,
                items, claim, state.CandidateObservation, state.CumulativeOutcome, state.TerminalOutcome, state.Predecessor,
                scribeProgress: progress), claim.AttemptId);
            ValidateProviderSettlementCapacity(transition.Artifact.State);
            return transition;
        }
        catch (CampaignStateValidationException exception) when (exception.Code == CampaignStateValidationCode.DocumentTooLarge)
        { return Reject(predecessor, CampaignTransitionFailure.CheckpointCapacity); }
        catch (Exception exception) when (IsBoundedContractFailure(exception) || exception is OverflowException)
        { return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority); }
    }

    private static (ImmutableArray<CampaignWorkItemState> Items, CampaignScribeProgress Progress) StartScribeMember(
        CampaignCheckpointState state, DocumentationScribeRequest request, string workKey, string targetId,
        CampaignScribeProgress progress, ImmutableArray<CampaignWorkItemState>? currentItems = null)
    {
        var items = currentItems ?? state.WorkItems;
        var work = items.Single(item => item.WorkItemKey == workKey);
        var session = progress.Session!;
        var existing = progress.Members.SingleOrDefault(item => item.WorkItemKey == workKey);
        if (existing is not null && existing.AttemptId == session.AttemptId)
        {
            if (existing.TargetId != targetId || existing.OuterAttemptOrdinal != work.OuterAttemptCount)
                throw CampaignStateFactory.Fail(CampaignStateValidationCode.InvalidCorrelation, "The member start belongs to another session.");
            return (items, progress);
        }
        if (existing?.ContentSha256 is not null || work.Status != CampaignWorkStatus.Planned
            || work.AttemptDisposition != CampaignAttemptDisposition.Open
            || work.OuterAttemptCount >= state.ConfiguredCeilings.CampaignBudget.MaximumAttemptsPerTarget)
            throw CampaignStateFactory.Fail(CampaignStateValidationCode.InvalidCorrelation, "The member cannot start under this session.");
        var ordinal = checked(work.OuterAttemptCount + 1);
        var binding = new CampaignScribeMemberProgress(workKey, targetId, ordinal, session.AnchorWorkItemKey,
            session.AnchorOuterAttemptOrdinal, session.AttemptId, request.ArtifactSha256, null, null);
        items = items.Select(item => item.WorkItemKey == workKey ? item with { OuterAttemptCount = ordinal } : item).ToImmutableArray();
        var members = items.Select(item => item.WorkItemKey == workKey ? binding
            : progress.Members.SingleOrDefault(member => member.WorkItemKey == item.WorkItemKey))
            .OfType<CampaignScribeMemberProgress>().ToImmutableArray();
        return (items, progress with { Members = members });
    }
}
