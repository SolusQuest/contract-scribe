using System.Text.Json;
using System.Text.Json.Serialization;
using ContractScribe.Core;

namespace ContractScribe.Agent.Prompting;

internal sealed class RepositoryContextRefJsonConverter : JsonConverter<RepositoryContextRef>
{
    public override RepositoryContextRef Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) => throw new NotSupportedException();

    public override void Write(
        Utf8JsonWriter writer,
        RepositoryContextRef value,
        JsonSerializerOptions options) => writer.WriteStringValue(value.Value);
}

internal sealed class DocumentationScribeTargetJsonConverter : JsonConverter<DocumentationScribeTarget>
{
    public override DocumentationScribeTarget Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) => throw new NotSupportedException();

    public override void Write(
        Utf8JsonWriter writer,
        DocumentationScribeTarget value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("symbolRef");
        JsonSerializer.Serialize(writer, value.SymbolRef, options);
        writer.WritePropertyName("sourceCommitment");
        writer.WriteStartObject();
        writer.WritePropertyName("locator");
        JsonSerializer.Serialize(writer, value.SourceLocator, options);
        writer.WriteString("contentSha256", value.SourceSha256);
        writer.WriteEndObject();
        writer.WritePropertyName("applicableComponents");
        JsonSerializer.Serialize(writer, value.ApplicableComponents, options);
        writer.WriteEndObject();
    }
}

internal sealed class EvidenceSubjectJsonConverter : JsonConverter<EvidenceSubject>
{
    public override EvidenceSubject Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) => throw new NotSupportedException();

    public override void Write(
        Utf8JsonWriter writer,
        EvidenceSubject value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case TargetEvidenceSubject target:
                writer.WritePropertyName("symbolRef");
                JsonSerializer.Serialize(writer, target.ParentSymbolRef, options);
                break;
            case ComponentEvidenceSubject component:
                writer.WritePropertyName("parentSymbolRef");
                JsonSerializer.Serialize(writer, component.ParentSymbolRef, options);
                writer.WritePropertyName("componentKind");
                JsonSerializer.Serialize(writer, component.ComponentKind, options);
                writer.WriteString("identity", component.Identity);
                break;
            default:
                throw new JsonException("The evidence subject is outside the closed product vocabulary.");
        }

        writer.WriteEndObject();
    }
}

internal sealed class EvidenceLocatorJsonConverter : JsonConverter<EvidenceLocator>
{
    public override EvidenceLocator Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) => throw new NotSupportedException();

    public override void Write(
        Utf8JsonWriter writer,
        EvidenceLocator value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case RepositoryEvidenceLocator repository:
                writer.WritePropertyName("repository");
                writer.WriteStartObject();
                writer.WriteString("path", repository.Path);
                WriteSpan(writer, repository.Span, options);
                writer.WriteEndObject();
                break;
            case MetadataEvidenceLocator metadata:
                writer.WritePropertyName("metadata");
                writer.WriteStartObject();
                writer.WriteString("assemblyIdentity", metadata.AssemblyIdentity);
                writer.WriteString("documentationCommentId", metadata.DocumentationCommentId);
                writer.WriteEndObject();
                break;
            case GeneratedOutputEvidenceLocator generated:
                writer.WritePropertyName("generatedOutput");
                writer.WriteStartObject();
                writer.WritePropertyName("producerKind");
                JsonSerializer.Serialize(writer, generated.ProducerKind, options);
                writer.WriteString("producerId", generated.ProducerId);
                writer.WriteString("outputId", generated.OutputId);
                writer.WriteString("sourceSha256", generated.SourceSha256);
                WriteSpan(writer, generated.Span, options);
                writer.WriteEndObject();
                break;
            case SyntheticEvidenceLocator synthetic:
                writer.WritePropertyName("synthetic");
                writer.WriteStartObject();
                writer.WriteString("fixtureId", synthetic.FixtureId);
                writer.WriteEndObject();
                break;
            default:
                throw new JsonException("The evidence locator is outside the closed product vocabulary.");
        }

        writer.WriteEndObject();
    }

    private static void WriteSpan(
        Utf8JsonWriter writer,
        Utf16Span? span,
        JsonSerializerOptions options)
    {
        if (span is { } present)
        {
            writer.WritePropertyName("span");
            JsonSerializer.Serialize(writer, present, options);
        }
    }
}

internal sealed class ProductEnumJsonConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    public override TEnum Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) => throw new NotSupportedException();

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
        writer.WriteStringValue(ProductPromptVocabulary.GetId(value));
}

internal static class ProductPromptVocabulary
{
    internal static string GetId<TEnum>(TEnum value)
        where TEnum : struct, Enum => value switch
        {
            TargetProfile typed => ClassificationVocabulary.GetId(typed),
            AuditOutcome typed => AuditVocabulary.GetId(typed),
            ComponentKind typed => ClassificationVocabulary.GetId(typed),
            DocumentationPatchComponentKind typed => typed switch
            {
                DocumentationPatchComponentKind.TypeParameter => "type-parameter",
                DocumentationPatchComponentKind.Parameter => "parameter",
                DocumentationPatchComponentKind.Return => "return",
                DocumentationPatchComponentKind.Value => "value",
                _ => throw new ArgumentOutOfRangeException(nameof(value)),
            },
            EvidenceKind typed => EvidenceVocabulary.GetId(typed),
            EvidenceRelation typed => EvidenceVocabulary.GetId(typed),
            GeneratedOutputKind typed => PolicyConfigurationVocabulary.GetId(typed),
            DocumentationScribePolicyDisposition typed => DocumentationScribeVocabulary.GetId(typed),
            DocumentationScribeInheritDocDisposition typed => DocumentationScribeVocabulary.GetId(typed),
            DocumentationScribeEvidenceAuthority typed => DocumentationScribeVocabulary.GetId(typed),
            DocumentationScribeContextReferenceKind typed => DocumentationScribeVocabulary.GetId(typed),
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };
}
