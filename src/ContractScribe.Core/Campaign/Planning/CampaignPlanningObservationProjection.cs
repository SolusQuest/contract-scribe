using System.Text;

namespace ContractScribe.Core;

internal readonly record struct CampaignPlanningObservationProjectionResult(
    string CommitmentSha256,
    int DeclarationCount,
    long EstimatedUtf8Bytes);

internal static class CampaignPlanningObservationProjection
{
    internal const int MaximumDeclarationsPerObservation = 4_096;
    internal const int MaximumAggregateDeclarations = 65_536;
    internal const long MaximumProjectionUtf8Bytes = 4_194_304;
    internal const long MaximumAggregateProjectionUtf8Bytes = 33_554_432;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static string ComputeCommitment(DocumentationObservation observation) =>
        Project(observation).CommitmentSha256;

    internal static CampaignPlanningObservationProjectionResult Project(
        DocumentationObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.Declarations.IsDefault
            || observation.Declarations.Length > MaximumDeclarationsPerObservation)
        {
            throw BoundFailure();
        }

        var estimatedBytes = EstimateProjectionBytes(observation);
        if (estimatedBytes > MaximumProjectionUtf8Bytes)
        {
            throw BoundFailure();
        }

        using var writer = new CampaignPlanningCommitmentWriter(
            "contract-scribe/campaign/observation-authority/v1");
        AddSymbolRef(writer, "subject.parent", observation.Subject.ParentSymbolRef);
        writer.Add("subject.component.present", observation.Subject.ComponentKind is not null);
        if (observation.Subject.ComponentKind is { } componentKind)
        {
            writer.Add("subject.component.kind", ClassificationVocabulary.GetId(componentKind));
            writer.Add("subject.component.identity", observation.Subject.ComponentIdentity!);
        }

        writer.Add("observation.value", DocumentationObservationVocabulary.GetId(observation.Value));
        writer.Add(
            "observation.completeness",
            DocumentationObservationVocabulary.GetId(observation.Completeness));
        writer.Add(
            "observation.unavailable-cause",
            DocumentationObservationVocabulary.GetId(observation.UnavailableCause));
        var declarations = observation.Declarations
            .OrderBy(declaration => declaration.DeclarationId, StringComparer.Ordinal)
            .ToArray();
        writer.Add("declaration.count", declarations.Length);
        foreach (var declaration in declarations)
        {
            writer.Add("declaration.id", declaration.DeclarationId);
            writer.Add(
                "declaration.authority-role",
                DocumentationObservationVocabulary.GetId(declaration.AuthorityRole));
            AddSource(writer, declaration.Source);
            AddSpan(writer, "declaration.span", declaration.DeclarationSpan);
            writer.Add("declaration.sha256", declaration.DeclarationSha256);
            AddSpan(writer, "declaration.leading-trivia-span", declaration.LeadingTriviaSpan);
            writer.Add("declaration.leading-trivia-sha256", declaration.LeadingTriviaSha256);
            writer.Add("declaration.documentation.present", declaration.DocumentationSpan is not null);
            if (declaration.DocumentationSpan is { } documentationSpan)
            {
                AddSpan(writer, "declaration.documentation-span", documentationSpan);
                writer.Add("declaration.documentation-sha256", declaration.DocumentationSha256!);
            }

            writer.Add(
                "declaration.block-state",
                DocumentationObservationVocabulary.GetId(declaration.BlockState));
            writer.Add("declaration.parent-substantive", declaration.ParentSubstantive);
            writer.AddOptional("declaration.component-local-name", declaration.ComponentLocalName);
            writer.Add(
                "declaration.component-match.present",
                declaration.ComponentMatch is not null);
            if (declaration.ComponentMatch is { } componentMatch)
            {
                writer.Add(
                    "declaration.component-match",
                    DocumentationObservationVocabulary.GetId(componentMatch));
            }
        }

        return new CampaignPlanningObservationProjectionResult(
            writer.Complete(),
            declarations.Length,
            estimatedBytes);
    }

    private static long EstimateProjectionBytes(DocumentationObservation observation)
    {
        try
        {
            long bytes = 1_024;
            AddEstimatedString(ref bytes, observation.Subject.ParentSymbolRef.CompilationContextRef);
            AddEstimatedString(ref bytes, observation.Subject.ParentSymbolRef.DocumentationCommentId);
            AddEstimatedString(ref bytes, observation.Subject.ComponentIdentity);
            foreach (var declaration in observation.Declarations)
            {
                if (declaration is null || declaration.Source is null)
                {
                    throw BoundFailure();
                }

                bytes = checked(bytes + 1_024);
                AddEstimatedString(ref bytes, declaration.DeclarationId);
                AddEstimatedString(ref bytes, declaration.Source.ProjectIdentity);
                AddEstimatedString(ref bytes, declaration.Source.SourceSha256);
                switch (declaration.Source)
                {
                    case RepositoryDocumentationSourceIdentity repository:
                        AddEstimatedString(ref bytes, repository.Path);
                        break;
                    case GeneratedDocumentationSourceIdentity generated:
                        AddEstimatedString(ref bytes, generated.ProducerId);
                        AddEstimatedString(ref bytes, generated.OutputId);
                        break;
                    default:
                        throw BoundFailure();
                }

                AddEstimatedString(ref bytes, declaration.DeclarationSha256);
                AddEstimatedString(ref bytes, declaration.LeadingTriviaSha256);
                AddEstimatedString(ref bytes, declaration.DocumentationSha256);
                AddEstimatedString(ref bytes, declaration.ComponentLocalName);
                if (bytes > MaximumProjectionUtf8Bytes)
                {
                    throw BoundFailure();
                }
            }

            return bytes;
        }
        catch (CampaignPlanningValidationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is EncoderFallbackException or OverflowException)
        {
            throw BoundFailure();
        }
    }

    private static void AddEstimatedString(ref long bytes, string? value)
    {
        if (value is null)
        {
            bytes = checked(bytes + 16);
            return;
        }

        bytes = checked(bytes + 32 + (long)StrictUtf8.GetByteCount(value) * 6);
    }

    private static CampaignPlanningValidationException BoundFailure() =>
        new(
            CampaignPlanningValidationCode.InvalidBound,
            "Observation authority exceeds the finite planning projection bound.");

    private static void AddSource(
        CampaignPlanningCommitmentWriter writer,
        DocumentationSourceIdentity source)
    {
        writer.Add("source.project", source.ProjectIdentity);
        writer.Add("source.kind", DocumentationObservationVocabulary.GetId(source.Kind));
        writer.Add("source.sha256", source.SourceSha256);
        switch (source)
        {
            case RepositoryDocumentationSourceIdentity repository:
                writer.Add("source.repository.path", repository.Path);
                break;
            case GeneratedDocumentationSourceIdentity generated:
                writer.Add("source.generated.producer", generated.ProducerId);
                writer.Add("source.generated.output", generated.OutputId);
                break;
            default:
                throw new InvalidOperationException("Unknown documentation source identity.");
        }
    }

    private static void AddSymbolRef(
        CampaignPlanningCommitmentWriter writer,
        string label,
        SymbolRef symbol)
    {
        writer.Add(label + ".context", symbol.CompilationContextRef);
        writer.Add(label + ".documentation-id", symbol.DocumentationCommentId);
    }

    private static void AddSpan(
        CampaignPlanningCommitmentWriter writer,
        string label,
        Utf16Span span)
    {
        writer.Add(label + ".start", span.Start);
        writer.Add(label + ".end", span.End);
    }
}
