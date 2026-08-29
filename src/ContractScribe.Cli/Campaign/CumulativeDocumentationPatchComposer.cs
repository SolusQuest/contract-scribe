using System.Collections.Immutable;
using System.Text;
using ContractScribe.Core;
using ContractScribe.Roslyn;

namespace ContractScribe.Cli;

internal sealed record CumulativeDocumentationPatchComposition(
    DocumentationPatchRequest Request,
    bool AcceptedOnly);

internal static class CumulativeDocumentationPatchComposer
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static CumulativeDocumentationPatchComposition Compose(
        ClassifiedRepositorySession session,
        CampaignPlanningInput planningInput,
        CampaignWorkPlan acceptedPlan,
        DocumentationScribeAuditAuthority auditAuthority,
        CampaignCheckpointState state,
        bool acceptedOnly,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(planningInput);
        ArgumentNullException.ThrowIfNull(acceptedPlan);
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();

        if (!ReferenceEquals(session.Classification.ClassificationSet, planningInput.Classifications)
            || session.RepositorySession.IsDisposed)
        {
            throw new ArgumentException("campaign.patch.session-mismatch", nameof(session));
        }

        var selected = state.WorkItems.Zip(acceptedPlan.WorkItems)
            .Where(pair => acceptedOnly
                ? pair.First.Status == CampaignWorkStatus.Accepted
                : pair.First.Status is CampaignWorkStatus.Accepted or CampaignWorkStatus.ProposalComplete)
            .ToImmutableArray();
        if (selected.IsEmpty
            || selected.Any(pair => pair.First.TrustedProposal is null
                || !string.Equals(pair.First.WorkItemKey, pair.Second.WorkItemKey, StringComparison.Ordinal)))
        {
            throw new ArgumentException("campaign.patch.active-projection-invalid", nameof(state));
        }

        ValidateCurrentWorkAuthorities(
            session,
            planningInput,
            auditAuthority,
            selected.Select(pair => pair.Second).ToImmutableArray(),
            cancellationToken);

        var currentEvidence = RebuildEvidence(
            session,
            planningInput,
            acceptedPlan,
            selected.Select(pair => pair.First.TrustedProposal!).ToImmutableArray(),
            cancellationToken);
        var context = new DocumentationPatchContext(
            session.RepositorySession.RepositoryContextRef,
            session.RepositorySession.InputIdentity,
            planningInput.Snapshot.TargetProfile);
        var request = acceptedOnly
            ? CampaignStateFactory.ReconstructAcceptedPatchRequest(state, context, currentEvidence)
            : CampaignStateFactory.ReconstructPatchRequest(state, context, currentEvidence);
        var expectedIds = selected.Select(pair => pair.First.WorkItemKey).ToArray();
        if (!request.Blocks.Select(block => block.BlockId).SequenceEqual(expectedIds, StringComparer.Ordinal))
        {
            throw new ArgumentException("campaign.patch.active-projection-mismatch", nameof(state));
        }

        return new CumulativeDocumentationPatchComposition(request, acceptedOnly);
    }

    private static void ValidateCurrentWorkAuthorities(
        ClassifiedRepositorySession session,
        CampaignPlanningInput planningInput,
        DocumentationScribeAuditAuthority auditAuthority,
        ImmutableArray<CampaignPlanningWorkItem> selected,
        CancellationToken cancellationToken)
    {
        foreach (var work in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (work.Targets.Length != 1
                || work.Targets[0].Source is not CampaignPlanningRepositorySourceAuthority source)
            {
                throw new ArgumentException("campaign.patch.target-authority-invalid");
            }

            var targetFact = work.Targets[0];
            var target = planningInput.Classifications.Targets.SingleOrDefault(candidate =>
                candidate.SymbolRef == targetFact.SymbolRef);
            if (target is null
                || !ReferenceEquals(
                    target,
                    session.Classification.ClassificationSet!.Targets.SingleOrDefault(candidate =>
                        candidate.SymbolRef == targetFact.SymbolRef))
                || auditAuthority.Select(target) is not { IsCurrent: true } selectedAudit
                || selectedAudit.Outcome != targetFact.AuditOutcome)
            {
                throw new ArgumentException("campaign.patch.target-current-authority-mismatch");
            }

            var selection = DocumentationScribeContextValidation.CreateBootstrapSelection(
                session.RepositorySession.RepositoryContextRef,
                session.RepositorySession.InputIdentity,
                planningInput.Snapshot.TargetProfile,
                targetFact.SymbolRef,
                source.Path,
                source.RequestedDeclarationSpan.Start,
                source.RequestedDeclarationSpan.End,
                source.ContentSha256);
            var bootstrap = new DocumentationScribeContextBootstrapper().Bootstrap(session, selection);
            if (bootstrap.Status is not (DocumentationScribeContextBootstrapStatus.Succeeded
                    or DocumentationScribeContextBootstrapStatus.Incomplete)
                || bootstrap.Context is not { } context
                || context.Facts.RepositoryContextRef != session.RepositorySession.RepositoryContextRef
                || !string.Equals(
                    context.Facts.InputIdentity,
                    session.RepositorySession.InputIdentity,
                    StringComparison.Ordinal)
                || context.Facts.TargetProfile != planningInput.Snapshot.TargetProfile
                || context.Facts.SymbolRef != targetFact.SymbolRef
                || !context.VerifyFreshness(cancellationToken))
            {
                throw new ArgumentException("campaign.patch.target-context-stale");
            }
        }
    }

    private static ImmutableArray<DocumentationScribeEvidenceReference> RebuildEvidence(
        ClassifiedRepositorySession session,
        CampaignPlanningInput planningInput,
        CampaignWorkPlan acceptedPlan,
        ImmutableArray<CampaignTrustedProposal> proposals,
        CancellationToken cancellationToken)
    {
        var expected = proposals
            .SelectMany(proposal => proposal.Evidence)
            .GroupBy(item => item.EvidenceReferenceId, StringComparer.Ordinal)
            .Select(group => group.All(item => ProjectionEquals(item, group.First()))
                ? group.First()
                : throw new ArgumentException("campaign.patch.evidence-conflict"))
            .OrderBy(item => item.EvidenceReferenceId, StringComparer.Ordinal)
            .ToImmutableArray();
        if (expected.Length > CampaignStateContract.MaximumEvidenceReferences)
        {
            throw new ArgumentException("campaign.patch.evidence-over-bound");
        }

        var catalog = planningInput.EvidenceAuthority
            .SelectMany(authority => authority.Binding.Bundle.Items)
            .GroupBy(item => item.EvidenceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToImmutableArray(), StringComparer.Ordinal);
        DocumentationPatchRepositoryBaseline? baseline = null;
        var result = ImmutableArray.CreateBuilder<DocumentationScribeEvidenceReference>(expected.Length);
        foreach (var projection in expected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (catalog.TryGetValue(projection.EvidenceReferenceId, out var items))
            {
                var matching = items.Where(item => CatalogMatches(item, projection)).ToArray();
                if (matching.Length != 1)
                {
                    throw new ArgumentException("campaign.patch.evidence-current-catalog-mismatch");
                }
            }
            else
            {
                baseline ??= CaptureBaseline(session, cancellationToken);
                ValidateCurrentSource(session, baseline, projection, cancellationToken);
            }

            ValidateClaimAuthority(acceptedPlan, proposals, projection);
            result.Add(new DocumentationScribeEvidenceReference(
                projection.EvidenceReferenceId,
                session.RepositorySession.RepositoryContextRef,
                projection.Subject,
                projection.Kind,
                projection.Relation,
                projection.Authority,
                projection.Locator,
                projection.ContentSha256,
                projection.OriginalUtf8ByteCount,
                projection.IncludedUtf8ByteCount,
                projection.IsTruncated,
                projection.ClaimCategoryIds));
        }

        return result.ToImmutable();
    }

    private static DocumentationPatchRepositoryBaseline CaptureBaseline(
        ClassifiedRepositorySession session,
        CancellationToken cancellationToken)
    {
        var capture = session.RepositorySession.CaptureDocumentationPatchResolutionBaseline(cancellationToken);
        return capture.Baseline
            ?? throw new ArgumentException(capture.FailureCode ?? "campaign.patch.evidence-baseline-unavailable");
    }

    private static void ValidateCurrentSource(
        ClassifiedRepositorySession session,
        DocumentationPatchRepositoryBaseline baseline,
        CampaignEvidenceProjection projection,
        CancellationToken cancellationToken)
    {
        switch (projection.Locator)
        {
            case RepositoryEvidenceLocator repository:
                {
                    if (!baseline.TryGetEntry(repository.Path, out var entry)
                        || !string.Equals(entry.Sha256, projection.ContentSha256, StringComparison.Ordinal)
                        || entry.Bytes.Length != projection.OriginalUtf8ByteCount
                        || (projection.IsTruncated
                            ? IncludedByteCount(entry.Bytes, repository.Span)
                            : entry.Bytes.Length) != projection.IncludedUtf8ByteCount
                        || projection.IsTruncated != (projection.IncludedUtf8ByteCount < projection.OriginalUtf8ByteCount))
                    {
                        throw new ArgumentException("campaign.patch.evidence-repository-stale");
                    }

                    break;
                }
            case GeneratedOutputEvidenceLocator generated:
                {
                    var matches = session.RepositorySession.GeneratedSources.Where(item =>
                        string.Equals(item.ProducerId, generated.ProducerId, StringComparison.Ordinal)
                        && string.Equals(item.OutputId, generated.OutputId, StringComparison.Ordinal)
                        && string.Equals(item.SourceSha256, generated.SourceSha256, StringComparison.Ordinal)).ToArray();
                    if (matches.Length != 1)
                    {
                        throw new ArgumentException("campaign.patch.evidence-generated-unavailable");
                    }

                    var bytes = StrictUtf8.GetBytes(matches[0].SourceText);
                    if (!string.Equals(generated.SourceSha256, projection.ContentSha256, StringComparison.Ordinal)
                        || bytes.Length != projection.OriginalUtf8ByteCount
                        || (projection.IsTruncated
                            ? IncludedByteCount(bytes.ToImmutableArray(), generated.Span)
                            : bytes.Length) != projection.IncludedUtf8ByteCount)
                    {
                        throw new ArgumentException("campaign.patch.evidence-generated-stale");
                    }

                    break;
                }
            default:
                throw new ArgumentException("campaign.patch.evidence-locator-not-reconstructible");
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static int IncludedByteCount(ImmutableArray<byte> bytes, Utf16Span? span)
    {
        if (span is null)
        {
            return bytes.Length;
        }

        var text = StrictUtf8.GetString(bytes.AsSpan());
        if (span.Value.Start < 0 || span.Value.End < span.Value.Start || span.Value.End > text.Length)
        {
            return -1;
        }

        return StrictUtf8.GetByteCount(text[span.Value.Start..span.Value.End]);
    }

    private static void ValidateClaimAuthority(
        CampaignWorkPlan acceptedPlan,
        ImmutableArray<CampaignTrustedProposal> proposals,
        CampaignEvidenceProjection projection)
    {
        var profiles = proposals
            .Where(proposal => proposal.Evidence.Any(evidence => string.Equals(
                evidence.EvidenceReferenceId,
                projection.EvidenceReferenceId,
                StringComparison.Ordinal)))
            .Select(proposal => acceptedPlan.WorkItems.SingleOrDefault(work => string.Equals(
                work.WorkItemKey,
                proposal.PatchBlock.BlockId,
                StringComparison.Ordinal)))
            .Select(work => work is { Targets.Length: 1 } ? work.Targets[0].StyleProfile : null)
            .ToArray();
        if (profiles.Length == 0
            || profiles.Any(profile => profile is null)
            || projection.ClaimCategoryIds.IsDefaultOrEmpty
            || profiles.Any(profile => projection.ClaimCategoryIds.Any(category =>
                !profile!.ClaimPolicies.Any(policy =>
                    string.Equals(policy.ClaimCategoryId, category, StringComparison.Ordinal)
                    && policy.AllowedAuthorities.Contains(projection.Authority)))))
        {
            throw new ArgumentException("campaign.patch.evidence-policy-mismatch");
        }
    }

    private static bool CatalogMatches(EvidenceItem item, CampaignEvidenceProjection projection) =>
        item.Subject == projection.Subject
        && item.Kind == projection.Kind
        && item.Relation == projection.Relation
        && item.Locator == projection.Locator
        && string.Equals(item.Sha256, projection.ContentSha256, StringComparison.Ordinal)
        && item.OriginalUtf8ByteCount == projection.OriginalUtf8ByteCount
        && item.IncludedUtf8ByteCount == projection.IncludedUtf8ByteCount
        && item.IsTruncated == projection.IsTruncated;

    private static bool ProjectionEquals(CampaignEvidenceProjection left, CampaignEvidenceProjection right) =>
        left == right
        || left.EvidenceReferenceId == right.EvidenceReferenceId
        && left.Subject == right.Subject
        && left.Kind == right.Kind
        && left.Relation == right.Relation
        && left.Authority == right.Authority
        && left.Locator == right.Locator
        && left.ContentSha256 == right.ContentSha256
        && left.OriginalUtf8ByteCount == right.OriginalUtf8ByteCount
        && left.IncludedUtf8ByteCount == right.IncludedUtf8ByteCount
        && left.IsTruncated == right.IsTruncated
        && left.ClaimCategoryIds.SequenceEqual(right.ClaimCategoryIds, StringComparer.Ordinal);
}
