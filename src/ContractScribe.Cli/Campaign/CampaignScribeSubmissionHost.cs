using System.Collections.Immutable;
using ContractScribe.Core;
using ContractScribe.Roslyn;

namespace ContractScribe.Cli;

internal static partial class DocumentationScribeComposition
{
    private sealed class CampaignScribeSubmissionHost(CampaignProviderInvocationAuthority invocation,
        CampaignScribeExecutionCoordinator coordinator, DocumentationScribeManifestSelection selection,
        DocumentationScribeLoadedManifestContext loaded, string? configuredAgentEntrypoint)
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
            var fresh = CumulativeDocumentationPatchComposer.RebuildEvidence(selection.PrimaryAudit.Session, invocation.PlanningInput,
                invocation.AcceptedPlan, sources, loaded.Contexts.SelectMany(context => context.Facts.Evidence).ToImmutableArray(), cancellationToken);
            return CampaignStateFactory.RestoreScribeProgress(invocation, fresh);
        }

        public async ValueTask<DocumentationScribeSubmissionAcknowledgement?> AcknowledgeAsync(
            DocumentationScribeValidatedSubmission submission, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var member = invocation.Request.Manifest.Single(item => item.TargetId == submission.TargetId);
            var audit = selection.Members.Single(item => item.Audit.Target.SymbolRef == member.Target.SymbolRef).Audit;
            var view = invocation.Request with { ValidationTargetId = submission.TargetId };
            // Reuse the current postflight under the original TerminalSubmission permit.
            // This prepares no model context and obtains no nested Host/send authority.
            var postflight = await PreflightAsync(audit, view, invocation.AttemptId, configuredAgentEntrypoint,
                cancellationToken, coordinator.Scope, selection, prepareModelContext: false).ConfigureAwait(false);
            if (postflight.Failure is not null || postflight.ManifestContext is null || !loaded.VerifyFreshness(cancellationToken)
                || coordinator.CurrentRemaining <= 0) return null;
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
            var fresh = CumulativeDocumentationPatchComposer.RebuildEvidence(audit.Session, invocation.PlanningInput,
                invocation.AcceptedPlan, [(workKey, evidence)],
                postflight.ManifestContext.Contexts.SelectMany(context => context.Facts.Evidence).ToImmutableArray(), cancellationToken);
            if (work.TrustedProposal is not null)
            {
                var restored = CampaignStateFactory.RestoreTrustedSubmission(transition.Artifact.State, invocation.ExecutionCapability,
                    invocation.StyleConfigurationId, invocation.ValidatedStyleConfigurationProjection, invocation.PlanningInput,
                    invocation.AcceptedPlan, invocation.Request, submission.TargetId, fresh);
                if (restored.ContentSha256 != submission.ContentSha256) return null;
            }
            cancellationToken.ThrowIfCancellationRequested();
            using var replacement = CampaignProcessBoundaryHooks.EnterReplacementScope(
                CampaignProcessBoundaryHooks.ScribeMemberReplacementScope);
            if (!await coordinator.AcceptHostProgressAsync(transition, cancellationToken).ConfigureAwait(false)) return null;
            CampaignProcessBoundaryHooks.Reach(CampaignProcessBoundaryHooks.ScribeMemberAfterReadback);
            return new(submission.ScribeRequestSha256, submission.TargetId, submission.ContentSha256,
                DocumentationScribeAcknowledgementStage.DurablyRecorded);
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
