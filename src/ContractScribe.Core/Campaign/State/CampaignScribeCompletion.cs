using System.Collections.Immutable;

namespace ContractScribe.Core;

/// <summary>A current Host completion fact, private to its original invocation; it grants no receipt or write.</summary>
internal sealed record CampaignScribeHostRejection(string TargetId, string ContentSha256, CampaignProviderCompletionKind Kind);

public static partial class CampaignStateReducer
{
    private static CampaignTransitionResult CompleteRecordedScribeProgress(CampaignCheckpointArtifact predecessor,
        CampaignProviderCompletionAuthority completion, CampaignProviderReservation claim)
    {
        var state = predecessor.State;
        var invocation = completion.Invocation;
        var outcome = completion.Outcome!;
        var progress = state.ScribeProgress!;
        var hostRejection = invocation.HostRejection;
        if (progress.Session is not { Ended: false } session || session.AttemptId != invocation.AttemptId
            || !invocation.ValidateOutcome(outcome))
            return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
        foreach (var receipt in outcome.RunResult.Submissions)
        {
            var member = progress.Members.SingleOrDefault(item => item.TargetId == receipt.TargetId);
            // Received content is a real interrupted Host observation, not an
            // accepted proposal. Its failed completion must still settle once
            // without erasing earlier durable siblings or promoting this body.
            if (receipt.Stage == DocumentationScribeAcknowledgementStage.Received
                && outcome.RunResult.Stop is DocumentationScribeFailureTerminal or DocumentationScribeCancelledTerminal
                && member?.ContentSha256 is null)
                continue;
            if (receipt.Stage < DocumentationScribeAcknowledgementStage.DurablyRecorded || member?.ContentSha256 is null
                || !(member.ContentSha256 == receipt.ContentSha256 && member.ScribeRequestSha256 == outcome.Request.ArtifactSha256
                    || invocation.RestoredReceiptMatches(receipt.TargetId, receipt.ContentSha256)))
                return Reject(predecessor, CampaignTransitionFailure.InvalidCorrelation);
        }
        var settlement = CampaignBudgetAccounting.SettleProviderInvocation(state, outcome, completion.ActiveElapsedMilliseconds);
        if (settlement.Charges is null) return Reject(predecessor, CampaignTransitionFailure.InvalidCorrelation);
        var items = state.WorkItems;
        var stopped = outcome.RunResult.Stop;
        foreach (var receipt in outcome.RunResult.Submissions.Where(receipt => receipt.Stage == DocumentationScribeAcknowledgementStage.Received))
        {
            var target = invocation.Request.Manifest.Single(member => member.TargetId == receipt.TargetId);
            var key = state.Batch.CompleteTargets.Single(candidate => candidate.SymbolRef == target.Target.SymbolRef).WorkItemKey;
            var started = StartScribeMember(state with { ScribeProgress = progress }, invocation.Request, key, receipt.TargetId, progress, items);
            items = started.Items; progress = started.Progress;
        }
        if (hostRejection is not null)
        {
            var receipt = outcome.RunResult.Submissions.SingleOrDefault(receipt => receipt.TargetId == hostRejection.TargetId);
            if (receipt?.Stage != DocumentationScribeAcknowledgementStage.Received || receipt.ContentSha256 != hostRejection.ContentSha256)
                return Reject(predecessor, CampaignTransitionFailure.InvalidCorrelation);
            var member = progress.Members.Single(member => member.TargetId == hostRejection.TargetId);
            DocumentationScribeTerminal hostStop = hostRejection.Kind switch
            {
                CampaignProviderCompletionKind.ProposalInvalid => new DocumentationScribeFailureTerminal(DocumentationScribeFailureCode.Validation, null),
                CampaignProviderCompletionKind.BudgetExhausted => new DocumentationScribeFailureTerminal(DocumentationScribeFailureCode.Budget, null),
                CampaignProviderCompletionKind.Timeout => new DocumentationScribeFailureTerminal(DocumentationScribeFailureCode.Timeout, null),
                CampaignProviderCompletionKind.CallerCancelled => new DocumentationScribeCancelledTerminal(DocumentationScribeCancellationCode.Caller),
                CampaignProviderCompletionKind.ShutdownCancelled => new DocumentationScribeCancelledTerminal(DocumentationScribeCancellationCode.Shutdown),
                _ => new DocumentationScribeFailureTerminal(DocumentationScribeFailureCode.Internal, null),
            };
            items = ReplaceWork(items, member.WorkItemKey, CampaignWorkStatus.Closed, null,
                CreateClosedScribeOutcome(outcome, member.WorkItemKey, hostStop));
        }
        var failed = stopped is DocumentationScribeFailureTerminal;
        if (failed)
        {
            foreach (var binding in progress.Members.Where(member => member.AttemptId == session.AttemptId && member.ContentSha256 is null))
            {
                var work = items.Single(item => item.WorkItemKey == binding.WorkItemKey);
                if (work.Status != CampaignWorkStatus.Planned) continue;
                var closed = CreateClosedScribeOutcome(outcome, binding.WorkItemKey, stopped);
                items = ReplaceWork(items, binding.WorkItemKey, CampaignWorkStatus.Closed, null, closed);
                if (closed is { Code: CampaignWorkOutcomeCode.ProviderFailure, ProviderDisposition: CampaignProviderFinalDisposition.Retryable }
                    && work.OuterAttemptCount >= state.ConfiguredCeilings.CampaignBudget.MaximumAttemptsPerTarget)
                    items = MarkAttemptSuppressed(items, binding.WorkItemKey);
            }
        }
        if (stopped is DocumentationScribeFinishedTerminal)
        {
            foreach (var failure in outcome.RunResult.TargetFailures)
            {
                var binding = progress.Members.Single(member => member.TargetId == failure.TargetId);
                var work = items.Single(item => item.WorkItemKey == binding.WorkItemKey);
                if (work.Status != CampaignWorkStatus.Planned || binding.ContentSha256 is not null) continue;
                items = ReplaceWork(items, binding.WorkItemKey, CampaignWorkStatus.Closed, null,
                    CreateClosedScribeOutcome(outcome, binding.WorkItemKey,
                        new DocumentationScribeFailureTerminal(DocumentationScribeFailureCode.Validation, null)));
            }
        }
        var pending = session.AdmittedWorkItemKeys.Any(key => items.Single(item => item.WorkItemKey == key) is
        { AttemptDisposition: CampaignAttemptDisposition.Open, Status: CampaignWorkStatus.Planned });
        var continuing = hostRejection is null && !failed && pending;
        if (continuing || stopped is DocumentationScribeCancelledTerminal && hostRejection is null)
            items = PreservePausedAttempt(items, claim);
        CampaignTerminalOutcome? terminal = hostRejection?.Kind switch
        {
            CampaignProviderCompletionKind.BudgetExhausted => new(CampaignTerminalKind.Exhausted, CampaignTerminalReason.Budget),
            CampaignProviderCompletionKind.Timeout => new(CampaignTerminalKind.Timeout, CampaignTerminalReason.Deadline),
            CampaignProviderCompletionKind.HostFailure or CampaignProviderCompletionKind.ShutdownCancelled => new(CampaignTerminalKind.Failed, CampaignTerminalReason.Host),
            CampaignProviderCompletionKind.CallerCancelled => new(CampaignTerminalKind.Cancelled, CampaignTerminalReason.Caller),
            CampaignProviderCompletionKind.ProposalInvalid => CompleteWhenResolved(state.Batch, items),
            _ => stopped switch
            {
                DocumentationScribeCancelledTerminal { Code: DocumentationScribeCancellationCode.Caller } => new(CampaignTerminalKind.Cancelled, CampaignTerminalReason.Caller),
                DocumentationScribeCancelledTerminal => new(CampaignTerminalKind.Failed, CampaignTerminalReason.Host),
                DocumentationScribeFailureTerminal { Code: DocumentationScribeFailureCode.Timeout } => new(CampaignTerminalKind.Timeout, CampaignTerminalReason.Deadline),
                DocumentationScribeFailureTerminal { Code: DocumentationScribeFailureCode.Budget } => new(CampaignTerminalKind.Exhausted, CampaignTerminalReason.Budget),
                _ => CompleteWhenResolved(state.Batch, items),
            },
        };
        if (settlement.Kind == CampaignBudgetDecisionKind.Exhausted && terminal is null or { Kind: CampaignTerminalKind.Complete })
            terminal = new(CampaignTerminalKind.Exhausted, CampaignTerminalReason.LifetimeCap);
        progress = progress with { Session = session with { Ended = !continuing && (hostRejection is not null || stopped is not DocumentationScribeCancelledTerminal) } };
        var transition = Applied(predecessor, CreateState(state, NextRevision(state.CheckpointRevision), settlement.Charges,
            items, null, state.CandidateObservation, state.CumulativeOutcome, terminal, state.Predecessor, scribeProgress: progress));
        return completion.TryConsume() && invocation.TryCompleteLifecycle(invocation.DispatchStarted)
            ? transition : Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
    }
}
