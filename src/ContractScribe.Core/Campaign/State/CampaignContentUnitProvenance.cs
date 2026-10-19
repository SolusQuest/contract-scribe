using System.Collections.Immutable;
using System.Text.Json;

namespace ContractScribe.Core;

/// <summary>Original field-to-claim/evidence associations; generated text stays solely in the patch block.</summary>
public sealed record CampaignContentUnitProvenance(DocumentationScribeContentUnitKind Kind,
    string? ComponentIdentity, string? TypeDocumentationId, string ClaimCategoryId,
    ImmutableArray<string> EvidenceReferenceIds);

public static partial class CampaignStateFactory
{
    private static void ValidateContentUnitProvenance(CampaignTrustedProposal proposal)
    {
        var provenance = proposal.ContentUnitProvenance;
        Require(!provenance.IsDefaultOrEmpty && provenance.Length <= DocumentationScribeContract.MaximumContentUnits,
            CampaignStateValidationCode.InvalidBound);
        var keys = new HashSet<(DocumentationScribeContentUnitKind, string?, string?)>();
        foreach (var unit in provenance)
        {
            Require(unit is not null && Enum.IsDefined(unit.Kind)
                && keys.Add((unit.Kind, unit.ComponentIdentity, unit.TypeDocumentationId))
                && IsOpaqueId(unit.ClaimCategoryId, DocumentationScribeContract.MaximumIdentifierScalars)
                && !unit.EvidenceReferenceIds.IsDefault
                && unit.EvidenceReferenceIds.Length <= CampaignStateContract.MaximumEvidenceReferencesPerBlock
                && unit.EvidenceReferenceIds.Distinct(StringComparer.Ordinal).Count() == unit.EvidenceReferenceIds.Length
                && unit.EvidenceReferenceIds.All(id => proposal.Evidence.Any(evidence => evidence.EvidenceReferenceId == id
                    && evidence.ClaimCategoryIds.Contains(unit.ClaimCategoryId, StringComparer.Ordinal))),
                CampaignStateValidationCode.InvalidReference);
        }
        var units = ReadContentUnits(proposal);
        var expectedCount = proposal.PatchBlock.Content is DocumentationPatchStructuredContent content
            ? 1 + content.TypeParameters.Length + content.Parameters.Length + content.Exceptions.Length
                + (content.Return is null ? 0 : 1) + (content.Value is null ? 0 : 1) + (content.RemarksLines is null ? 0 : 1)
            : 1;
        Require(units.Length == expectedCount
            && units.SelectMany(unit => unit.EvidenceReferenceIds).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                .SequenceEqual(proposal.Evidence.Select(evidence => evidence.EvidenceReferenceId), StringComparer.Ordinal),
            CampaignStateValidationCode.InvalidCorrelation);
    }

    private static ImmutableArray<DocumentationScribeContentUnit> ReadContentUnits(CampaignTrustedProposal proposal) =>
        proposal.ContentUnitProvenance.Select(unit =>
        {
            var content = proposal.PatchBlock.Content as DocumentationPatchStructuredContent;
            string? name = null;
            ImmutableArray<string> lines;
            switch (unit.Kind)
            {
                case DocumentationScribeContentUnitKind.InheritDoc when proposal.PatchBlock.Content is DocumentationPatchInheritDocContent
                    && unit.ComponentIdentity is null && unit.TypeDocumentationId is null:
                    lines = [];
                    break;
                case DocumentationScribeContentUnitKind.Summary when content is not null
                    && unit.ComponentIdentity is null && unit.TypeDocumentationId is null:
                    lines = content.SummaryLines;
                    break;
                case DocumentationScribeContentUnitKind.Remarks when content?.RemarksLines is { } remarks
                    && unit.ComponentIdentity is null && unit.TypeDocumentationId is null:
                    lines = remarks;
                    break;
                case DocumentationScribeContentUnitKind.Parameter when content is not null && unit.TypeDocumentationId is null:
                    var parameter = content.Parameters.Single(item => item.ComponentIdentity == unit.ComponentIdentity);
                    name = parameter.Name; lines = parameter.Lines;
                    break;
                case DocumentationScribeContentUnitKind.TypeParameter when content is not null && unit.TypeDocumentationId is null:
                    var typeParameter = content.TypeParameters.Single(item => item.ComponentIdentity == unit.ComponentIdentity);
                    name = typeParameter.Name; lines = typeParameter.Lines;
                    break;
                case DocumentationScribeContentUnitKind.Return when content?.Return is { } returns
                    && unit.ComponentIdentity == returns.ComponentIdentity && unit.TypeDocumentationId is null:
                    lines = returns.Lines;
                    break;
                case DocumentationScribeContentUnitKind.Value when content?.Value is { } value
                    && unit.ComponentIdentity == value.ComponentIdentity && unit.TypeDocumentationId is null:
                    lines = value.Lines;
                    break;
                case DocumentationScribeContentUnitKind.Exception when content is not null && unit.ComponentIdentity is null:
                    lines = content.Exceptions.Single(item => item.TypeDocumentationId == unit.TypeDocumentationId).Lines;
                    break;
                default:
                    throw Fail(CampaignStateValidationCode.InvalidCorrelation, "Content provenance does not match the generated patch fields.");
            }
            return new DocumentationScribeContentUnit(unit.Kind, unit.ComponentIdentity, name, unit.TypeDocumentationId,
                lines, unit.ClaimCategoryId, unit.EvidenceReferenceIds);
        }).ToImmutableArray();

    /// <summary>Revalidates generated fields with a fresh request and freshly rederived evidence, without provider work.</summary>
    internal static DocumentationScribeValidatedSubmission RestoreTrustedSubmission(CampaignCheckpointState state,
        CampaignScribeExecutionCapability capability, string styleConfigurationId, JsonElement styleConfiguration,
        CampaignPlanningInput planning, CampaignWorkPlan plan, DocumentationScribeRequest request, string targetId,
        ImmutableArray<DocumentationScribeEvidenceReference> currentEvidence)
    {
        ValidateCurrentContext(state, capability, styleConfigurationId, styleConfiguration,
            request.Context.InputIdentity, planning, plan);
        var target = request.Manifest.Single(member => member.TargetId == targetId);
        Require(request.ExecutableTargetIds.Contains(targetId, StringComparer.Ordinal), CampaignStateValidationCode.InvalidCorrelation);
        var planWork = plan.WorkItems.Single(work => work.Targets.Length == 1 && work.Targets[0].SymbolRef == target.Target.SymbolRef);
        var work = state.WorkItems.Single(item => item.WorkItemKey == planWork.WorkItemKey);
        ValidateCurrentTrustedProposalAuthority(state, work, planWork);
        var proposal = work.TrustedProposal!;
        ValidateContentUnitProvenance(proposal);
        ValidateCurrentEvidence([proposal], new(request.Context.RepositoryContextRef, request.Context.InputIdentity,
            request.Context.TargetProfile), currentEvidence);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "proposal");
            writer.WritePropertyName("target");
            writer.WriteStartObject();
            writer.WriteString("repositoryContextRef", request.Context.RepositoryContextRef.Value);
            writer.WritePropertyName("symbolRef");
            writer.WriteStartObject();
            writer.WriteString("compilationContextRef", target.Target.SymbolRef.CompilationContextRef);
            writer.WriteString("documentationCommentId", target.Target.SymbolRef.DocumentationCommentId);
            writer.WriteEndObject();
            writer.WritePropertyName("sourceCommitment"); writer.WriteStartObject();
            writer.WritePropertyName("locator"); writer.WriteStartObject();
            Require(target.Target.SourceLocator is RepositoryEvidenceLocator { Span: not null }, CampaignStateValidationCode.InvalidCorrelation);
            var locator = (RepositoryEvidenceLocator)target.Target.SourceLocator;
            writer.WritePropertyName("repository"); writer.WriteStartObject();
            writer.WriteString("path", locator.Path);
            writer.WritePropertyName("span"); writer.WriteStartObject();
            writer.WriteNumber("start", locator.Span!.Value.Start); writer.WriteNumber("end", locator.Span.Value.End);
            writer.WriteEndObject(); writer.WriteEndObject(); writer.WriteEndObject();
            writer.WriteString("contentSha256", target.Target.SourceSha256); writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WritePropertyName("contentUnits");
            writer.WriteStartArray();
            foreach (var unit in ReadContentUnits(proposal))
            {
                writer.WriteStartObject();
                writer.WriteString("kind", DocumentationScribeVocabulary.GetId(unit.Kind));
                if (unit.ComponentIdentity is { } component) writer.WriteString("componentIdentity", component);
                if (unit.Name is { } name) writer.WriteString("name", name);
                if (unit.TypeDocumentationId is { } type) writer.WriteString("typeDocumentationId", type);
                if (unit.Kind != DocumentationScribeContentUnitKind.InheritDoc)
                {
                    writer.WritePropertyName("lines"); writer.WriteStartArray();
                    foreach (var line in unit.Lines) writer.WriteStringValue(line);
                    writer.WriteEndArray();
                }
                writer.WriteString("claimCategoryId", unit.ClaimCategoryId);
                writer.WritePropertyName("evidenceReferenceIds"); writer.WriteStartArray();
                foreach (var id in unit.EvidenceReferenceIds) writer.WriteStringValue(id);
                writer.WriteEndArray(); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        var overlay = currentEvidence.Where(reference => proposal.Evidence.Any(item => item.EvidenceReferenceId == reference.EvidenceReferenceId)
            && !request.EvidenceReferences.Any(item => item.EvidenceReferenceId == reference.EvidenceReferenceId)).ToImmutableArray();
        var parsed = DocumentationScribeValidation.ValidateTargetSubmission(request, targetId, overlay, stream.ToArray());
        Require(parsed.Submission?.Result is DocumentationScribeProposalTerminal restored
            && CreatePatchContentCommitment(restored.PatchContent) == CreatePatchContentCommitment(proposal.PatchBlock.Content),
            CampaignStateValidationCode.InvalidCorrelation);
        return parsed.Submission!;
    }
}

public static partial class CampaignStateJson
{
    private static void WriteContentUnitProvenance(Utf8JsonWriter writer, ImmutableArray<CampaignContentUnitProvenance> provenance)
    {
        writer.WritePropertyName("contentUnitProvenance"); writer.WriteStartArray();
        foreach (var unit in provenance)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", DocumentationScribeVocabulary.GetId(unit.Kind));
            writer.WriteString("componentIdentity", unit.ComponentIdentity);
            writer.WriteString("typeDocumentationId", unit.TypeDocumentationId);
            writer.WriteString("claimCategoryId", unit.ClaimCategoryId);
            writer.WritePropertyName("evidenceReferenceIds"); WriteStrings(writer, unit.EvidenceReferenceIds);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static CampaignContentUnitProvenance ParseContentUnitProvenance(JsonElement element)
    {
        ExpectObject(element, "kind", "componentIdentity", "typeDocumentationId", "claimCategoryId", "evidenceReferenceIds");
        var kind = ReadString(element, "kind") switch
        {
            "content.summary" => DocumentationScribeContentUnitKind.Summary,
            "content.parameter" => DocumentationScribeContentUnitKind.Parameter,
            "content.type-parameter" => DocumentationScribeContentUnitKind.TypeParameter,
            "content.return" => DocumentationScribeContentUnitKind.Return,
            "content.value" => DocumentationScribeContentUnitKind.Value,
            "content.exception" => DocumentationScribeContentUnitKind.Exception,
            "content.remarks" => DocumentationScribeContentUnitKind.Remarks,
            "content.inherit-doc" => DocumentationScribeContentUnitKind.InheritDoc,
            _ => throw Vocabulary(),
        };
        return new(kind, ReadNullableString(element, "componentIdentity"), ReadNullableString(element, "typeDocumentationId"),
            ReadString(element, "claimCategoryId"), ParseStrings(element.GetProperty("evidenceReferenceIds"),
                CampaignStateContract.MaximumEvidenceReferencesPerBlock));
    }
}
