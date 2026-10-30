using System.Collections.Immutable;
using ContractScribe.Core;
using ContractScribe.Roslyn;

namespace ContractScribe.Cli;

internal static partial class DocumentationScribeComposition
{
    private sealed class CampaignScribeSubmissionHost(CampaignProviderInvocationAuthority invocation,
        CampaignScribeExecutionCoordinator coordinator, DocumentationScribeManifestSelection selection,
        DocumentationScribeLoadedManifestContext loaded, string? configuredAgentEntrypoint, CancellationToken callerCancellation)
        : IDocumentationScribeSubmissionHost
    {
        internal DocumentationScribeRunResult? RestoreProgress(CancellationToken cancellationToken)
        {
            var state = invocation.AcceptedCheckpoint.Artifact.State;
            if (state.ScribeProgress is not { } progress) return null;
            var sources = progress.Members.Where(member => member.ContentSha256 is not null
                    && invocation.Request.ExecutableTargetIds.Contains(member.TargetId, StringComparer.Ordinal))
                .Select(member => (member.WorkItemKey, state.WorkItems.Single(work => work.WorkItemKey == member.WorkItemKey).TrustedProposal?.Evidence
                    ?? member.Skip!.Evidence)).ToImmutableArray();
            if (!loaded.VerifyFreshness(cancellationToken)) throw new ArgumentException("campaign.scribe.progress-stale");
            // Historical bootstrap evidence must be recaptured for the Host. Its
            // original inclusion can differ from this request's one unresolved seed.
            var currentEvidence = sources.SelectMany(source => CumulativeDocumentationPatchComposer.CaptureCurrentTargetEvidence(
                selection.PrimaryAudit.Session, invocation.PlanningInput,
                invocation.AcceptedPlan.WorkItems.Single(work => work.WorkItemKey == source.WorkItemKey).Targets.Single(),
                cancellationToken)).ToImmutableArray();
            var fresh = CumulativeDocumentationPatchComposer.RebuildEvidence(selection.PrimaryAudit.Session, invocation.PlanningInput,
                invocation.AcceptedPlan, sources, currentEvidence, cancellationToken);
            return CampaignStateFactory.RestoreScribeProgress(invocation, fresh);
        }

        public async ValueTask<DocumentationScribeSubmissionAcknowledgement?> AcknowledgeAsync(
            DocumentationScribeValidatedSubmission submission, DocumentationScribeProgressNotification progress,
            CancellationToken cancellationToken)
        {
            var member = invocation.Request.Manifest.Single(item => item.TargetId == submission.TargetId);
            var audit = selection.Members.Single(item => item.Audit.Target.SymbolRef == member.Target.SymbolRef).Audit;
            var view = invocation.Request with { ValidationTargetId = submission.TargetId };
            var deadlineOwner = coordinator.Scope.CurrentHostDeadlineOwner ?? DocumentationScribeDeadlineOwner.HostOperation;
            // Reuse the current postflight under the original TerminalSubmission permit.
            // This prepares no model context and obtains no nested Host/send authority.
            PreflightResult postflight;
            try
            {
                CampaignProcessBoundaryHooks.Reach(CampaignProcessBoundaryHooks.ScribeMemberBeforePostflight);
                cancellationToken.ThrowIfCancellationRequested();
                if (coordinator.CurrentRemaining <= 0 || coordinator.Scope.Allowance.RemainingMilliseconds <= 0)
                {
                    coordinator.Scope.ObserveDeadline(deadlineOwner);
                    return null;
                }
                CampaignProcessBoundaryHooks.Reach(CampaignProcessBoundaryHooks.ScribeMemberPostflightAdmitted);
                postflight = await PreflightAsync(audit, view, invocation.AttemptId, configuredAgentEntrypoint,
                    cancellationToken, coordinator.Scope, selection, prepareModelContext: false).ConfigureAwait(false);
                if (postflight.Failure is { } failure)
                {
                    if (failure.Kind == PreflightFailureKind.Cancelled) return Cancelled();
                    return Rejected(failure.Kind switch
                    {
                        PreflightFailureKind.Rejected => CampaignProviderCompletionKind.ProposalInvalid,
                        PreflightFailureKind.BudgetExhausted => CampaignProviderCompletionKind.BudgetExhausted,
                        PreflightFailureKind.TimedOut => CampaignProviderCompletionKind.Timeout,
                        _ => CampaignProviderCompletionKind.HostFailure,
                    });
                }
                if (postflight.ManifestContext is null || !loaded.VerifyFreshness(cancellationToken))
                    return Rejected(CampaignProviderCompletionKind.ProposalInvalid);
                if (coordinator.CurrentRemaining <= 0 || coordinator.Scope.Allowance.RemainingMilliseconds <= 0)
                {
                    coordinator.Scope.ObserveDeadline(deadlineOwner);
                    return null;
                }
            }
            catch (OperationCanceledException) { return Cancelled(); }
            catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
            { return Rejected(CampaignProviderCompletionKind.HostFailure); }
            var transition = CampaignStateReducer.AcceptScribeSubmission(invocation, submission);
            if (transition.Kind == CampaignTransitionKind.Rejected)
            {
                await coordinator.AcceptHostProgressAsync(transition, cancellationToken).ConfigureAwait(false);
                return null;
            }
            var workKey = invocation.AcceptedPlan.WorkItems.Single(work => work.Targets.Length == 1
                && work.Targets[0].SymbolRef == member.Target.SymbolRef).WorkItemKey;
            var work = transition.Artifact.State.WorkItems.Single(item => item.WorkItemKey == workKey);
            if (work.ClosedOutcome?.Code == CampaignWorkOutcomeCode.CompletedOverBound)
            {
                using var closedReplacement = CampaignProcessBoundaryHooks.EnterReplacementScope(
                    CampaignProcessBoundaryHooks.ScribeMemberReplacementScope);
                if (await coordinator.AcceptHostProgressAsync(transition, cancellationToken).ConfigureAwait(false))
                {
                    coordinator.ObserveProjectionCapacity();
                    CampaignProcessBoundaryHooks.Reach(CampaignProcessBoundaryHooks.ScribeMemberAfterReadback);
                }
                return null;
            }
            var evidence = work.TrustedProposal?.Evidence
                ?? transition.Artifact.State.ScribeProgress!.Members.Single(item => item.WorkItemKey == workKey).Skip!.Evidence;
            try
            {
                var fresh = CumulativeDocumentationPatchComposer.RebuildEvidence(audit.Session, invocation.PlanningInput,
                    invocation.AcceptedPlan, [(workKey, evidence)],
                    postflight.ManifestContext.Contexts.SelectMany(context => context.Facts.Evidence).ToImmutableArray(), cancellationToken);
                if (work.TrustedProposal is not null)
                {
                    var restored = CampaignStateFactory.RestoreTrustedSubmission(transition.Artifact.State, invocation.ExecutionCapability,
                        invocation.StyleConfigurationId, invocation.ValidatedStyleConfigurationProjection, invocation.PlanningInput,
                        invocation.AcceptedPlan, invocation.Request, submission.TargetId, fresh);
                    if (restored.ContentSha256 != submission.ContentSha256) return Rejected(CampaignProviderCompletionKind.ProposalInvalid);
                }
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) { return Cancelled(); }
            catch (ArgumentException) { return Rejected(CampaignProviderCompletionKind.ProposalInvalid); }
            transition = CampaignStateReducer.AcceptScribeSubmissionWithProgress(invocation, submission, progress);
            using var replacement = CampaignProcessBoundaryHooks.EnterReplacementScope(
                CampaignProcessBoundaryHooks.ScribeMemberProgressReplacementScope);
            if (!await coordinator.AcceptHostProgressAsync(transition, cancellationToken).ConfigureAwait(false)) return null;
            CampaignProcessBoundaryHooks.Reach(CampaignProcessBoundaryHooks.ScribeMemberAfterReadback);
            CampaignProcessBoundaryHooks.Reach(CampaignProcessBoundaryHooks.ScribeProgressAfterReadback);
            return new(submission.ScribeRequestSha256, submission.TargetId, submission.ContentSha256,
                DocumentationScribeAcknowledgementStage.DurablyRecorded)
            { ProgressSha256 = progress.ProgressSha256 };

            DocumentationScribeSubmissionAcknowledgement? Rejected(CampaignProviderCompletionKind kind)
            {
                _ = invocation.TryRecordHostRejection(submission, kind);
                return null;
            }
            DocumentationScribeSubmissionAcknowledgement? Cancelled()
            {
                if (callerCancellation.IsCancellationRequested) return Rejected(CampaignProviderCompletionKind.CallerCancelled);
                if (deadlineOwner == DocumentationScribeDeadlineOwner.HostOperation) return Rejected(CampaignProviderCompletionKind.Timeout);
                coordinator.Scope.ObserveDeadline(deadlineOwner);
                return null;
            }
        }

        public async ValueTask<DocumentationScribeProgressAcknowledgement?> ObserveProgressAsync(
            DocumentationScribeProgressNotification progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (coordinator.ProjectionCapacityStop) return null;
            var transition = CampaignStateReducer.AcceptScribeProgress(invocation, progress);
            using var replacement = CampaignProcessBoundaryHooks.EnterReplacementScope(
                CampaignProcessBoundaryHooks.ScribeProgressReplacementScope);
            if (!await coordinator.AcceptHostProgressAsync(transition, cancellationToken).ConfigureAwait(false)) return null;
            CampaignProcessBoundaryHooks.Reach(CampaignProcessBoundaryHooks.ScribeProgressAfterReadback);
            return new(progress.ProgressSha256);
        }
    }
}
