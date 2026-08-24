using System.Text;

namespace ContractScribe.Core;

internal static class CampaignPlanningPartialEvidenceValidator
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static bool MatchesCurrentObservation(
        DocumentationObservation observation,
        EvidenceBundle bundle)
    {
        if (bundle.AvailabilityStatus == EvidenceAvailabilityStatus.Complete
            && bundle.OmissionReason is null)
        {
            return true;
        }

        if (bundle.ObservationSubject is not null
            || bundle.Items.IsDefault
            || bundle.AvailabilityStatus == EvidenceAvailabilityStatus.Unavailable
                && !bundle.Items.IsEmpty
            || bundle.AvailabilityStatus == EvidenceAvailabilityStatus.Partial
                && bundle.Items.IsEmpty)
        {
            return false;
        }

        if (bundle.Items.IsEmpty)
        {
            return true;
        }

        EvidenceSubject expectedSubject = observation.Subject.ComponentKind is { } componentKind
            && observation.Subject.ComponentIdentity is { } identity
            ? EvidenceInput.ComponentSubject(
                observation.Subject.ParentSymbolRef.CompilationContextRef,
                observation.Subject.ParentSymbolRef.DocumentationCommentId,
                componentKind,
                identity)
            : EvidenceInput.TargetSubject(
                observation.Subject.ParentSymbolRef.CompilationContextRef,
                observation.Subject.ParentSymbolRef.DocumentationCommentId);
        var evidenceIds = new HashSet<string>(StringComparer.Ordinal);
        var declarationKinds = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var item in bundle.Items)
            {
                if (item is null
                    || !Equals(item.Subject, expectedSubject)
                    || !evidenceIds.Add(item.EvidenceId)
                    || observation.Value == DocumentationObservationValue.Absent
                        && item.Kind == EvidenceKind.SourceXmlDocumentation)
                {
                    return false;
                }

                DocumentationDeclarationFact? matched = null;
                foreach (var declaration in observation.Declarations)
                {
                    if (!MatchesDeclarationItem(item, declaration))
                    {
                        continue;
                    }

                    if (matched is not null)
                    {
                        return false;
                    }

                    matched = declaration;
                }

                if (matched is null
                    || !declarationKinds.Add(
                        matched.DeclarationId + "\u001f" + item.Kind.ToString()))
                {
                    return false;
                }
            }

            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static bool MatchesDeclarationItem(
        EvidenceItem item,
        DocumentationDeclarationFact declaration)
    {
        var documentation = item.Kind == EvidenceKind.SourceXmlDocumentation
            && item.Relation == EvidenceRelation.Documents;
        var declarationEvidence = item.Kind == EvidenceKind.SourceDeclaration
            && item.Relation == EvidenceRelation.Declares;
        if (!documentation && !declarationEvidence)
        {
            return false;
        }

        var expectedText = documentation
            ? declaration.DocumentationText
            : declaration.DeclarationText;
        var expectedSha256 = documentation
            ? declaration.DocumentationSha256
            : declaration.DeclarationSha256;
        var expectedSpan = documentation
            ? declaration.DocumentationSpan
            : declaration.DeclarationSpan;
        if (expectedText is null
            || expectedSha256 is null
            || expectedSpan is null
            || !expectedText.StartsWith(item.Excerpt, StringComparison.Ordinal)
            || item.Sha256 != expectedSha256)
        {
            return false;
        }

        var originalBytes = StrictUtf8.GetByteCount(expectedText);
        var includedBytes = StrictUtf8.GetByteCount(item.Excerpt);
        if (item.OriginalUtf8ByteCount != originalBytes
            || item.IncludedUtf8ByteCount != includedBytes
            || item.OmittedUtf8ByteCount != originalBytes - includedBytes
            || item.IsTruncated != (includedBytes != originalBytes))
        {
            return false;
        }

        return (declaration.Source, item.Locator) switch
        {
            (
                RepositoryDocumentationSourceIdentity repository,
                RepositoryEvidenceLocator locator) =>
                repository.Path == locator.Path
                && locator.Span == expectedSpan,
            (
                GeneratedDocumentationSourceIdentity generated,
                GeneratedOutputEvidenceLocator locator) =>
                GeneratedKind(generated.Kind) == locator.ProducerKind
                && generated.ProducerId == locator.ProducerId
                && generated.OutputId == locator.OutputId
                && generated.SourceSha256 == locator.SourceSha256
                && locator.Span == expectedSpan,
            _ => false,
        };
    }

    private static GeneratedOutputKind GeneratedKind(DocumentationSourceKind kind) => kind switch
    {
        DocumentationSourceKind.SourceGenerator => GeneratedOutputKind.SourceGenerator,
        DocumentationSourceKind.ToolGenerated => GeneratedOutputKind.ToolGenerated,
        _ => (GeneratedOutputKind)(-1),
    };
}
