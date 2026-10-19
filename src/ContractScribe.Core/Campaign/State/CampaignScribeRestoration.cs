using System.Collections.Immutable;
using System.Text.Json;

namespace ContractScribe.Core;

public static partial class CampaignStateFactory
{
    internal static DocumentationScribeRunResult? RestoreScribeProgress(CampaignProviderInvocationAuthority invocation,
        ImmutableArray<DocumentationScribeEvidenceReference> currentEvidence)
    {
        var state = invocation.AcceptedCheckpoint.Artifact.State;
        if (state.ScribeProgress is not { } progress) return null;
        var request = invocation.Request;
        var submissions = ImmutableArray.CreateBuilder<DocumentationScribeTargetSubmission>();
        foreach (var member in progress.Members.Where(member => member.ContentSha256 is not null
                     && request.ExecutableTargetIds.Contains(member.TargetId, StringComparer.Ordinal)))
        {
            var work = state.WorkItems.Single(item => item.WorkItemKey == member.WorkItemKey);
            DocumentationScribeValidatedSubmission restored;
            if (work.TrustedProposal is { } proposal)
            {
                var ids = proposal.Evidence.Select(reference => reference.EvidenceReferenceId).ToHashSet(StringComparer.Ordinal);
                var memberEvidence = currentEvidence.Where(reference => ids.Contains(reference.EvidenceReferenceId))
                    .OrderBy(reference => reference.EvidenceReferenceId, StringComparer.Ordinal).ToImmutableArray();
                restored = RestoreTrustedSubmission(state, invocation.ExecutionCapability, invocation.StyleConfigurationId,
                    invocation.ValidatedStyleConfigurationProjection, invocation.PlanningInput, invocation.AcceptedPlan,
                    request, member.TargetId, memberEvidence);
            }
            else
            {
                var skip = member.Skip ?? throw Fail(CampaignStateValidationCode.InvalidCorrelation, "A durable member has no restored content.");
                _ = ValidateProviderRequestAuthority(state, invocation.ExecutionCapability, invocation.StyleConfigurationId,
                    invocation.ValidatedStyleConfigurationProjection, invocation.PlanningInput, invocation.AcceptedPlan, member.WorkItemKey, request);
                var references = skip.Evidence.Select(projection =>
                {
                    var fresh = currentEvidence.Single(reference => reference.EvidenceReferenceId == projection.EvidenceReferenceId);
                    Require(fresh.RepositoryContextRef == request.Context.RepositoryContextRef
                        && EvidenceProjectionEquals(ProjectEvidence(fresh), projection), CampaignStateValidationCode.InvalidReference);
                    return fresh;
                }).ToImmutableArray();
                var ids = references.Select(reference => reference.EvidenceReferenceId).ToImmutableArray();
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new { kind = "skip", reason = DocumentationScribeVocabulary.GetId(skip.Reason), evidenceReferenceIds = ids });
                var overlay = references.Where(reference => !request.EvidenceReferences.Any(seed => seed.EvidenceReferenceId == reference.EvidenceReferenceId))
                    .OrderBy(reference => reference.EvidenceReferenceId, StringComparer.Ordinal).ToImmutableArray();
                var parsed = DocumentationScribeValidation.ValidateTargetSubmission(request, member.TargetId, overlay, bytes);
                Require(parsed.Submission is not null && parsed.Submission.ContentSha256 == member.ContentSha256,
                    CampaignStateValidationCode.InvalidCorrelation);
                restored = parsed.Submission!;
            }
            var stage = work.Status == CampaignWorkStatus.Accepted ? DocumentationScribeAcknowledgementStage.PatchAccepted
                : DocumentationScribeAcknowledgementStage.DurablyRecorded;
            var receipt = DocumentationScribeValidation.AcknowledgeSubmission(restored,
                new(request.ArtifactSha256, restored.TargetId, restored.ContentSha256, stage));
            submissions.Add(receipt);
            invocation.RecordRestoredReceipt(restored);
        }
        var failures = progress.TargetFailures.Where(failure => request.ExecutableTargetIds.Contains(failure.TargetId, StringComparer.Ordinal))
            .ToImmutableArray();
        var execution = invocation.ExecutionCapability.Projection;
        var envelope = new DocumentationScribeRunEnvelopeInput(execution.ProviderConfigurationId, execution.ModelConfigurationId,
            execution.ScribeProtocolId, invocation.RestoredRetryableProviderFailures + 1, 0, 0, 0, 0, null, null, null, [])
        { RestoredRetryableProviderFailures = invocation.RestoredRetryableProviderFailures };
        DocumentationScribeTerminal stop = progress.LastProgressFinished
            ? new DocumentationScribeFinishedTerminal(DocumentationScribeFinishReason.Unresolved)
            : new DocumentationScribeFailureTerminal(DocumentationScribeFailureCode.Internal, null);
        return DocumentationScribeValidation.CreateSessionResult(request, invocation.AttemptId, stop, envelope,
            submissions.ToImmutable(), failures, progress.RepairCount, progress.NextSequence);
    }
}
