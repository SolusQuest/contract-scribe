using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ContractScribe.Core;

internal static class AuditCanonicalJson
{
    internal static byte[] Canonicalize(JsonElement root)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
            stream,
            new JsonWriterOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                Indented = false,
                SkipValidation = false,
            }))
        {
            WriteValue(writer, root, null);
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    internal static byte[] CanonicalizeProjection(JsonElement root)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
            stream,
            new JsonWriterOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                Indented = false,
                SkipValidation = false,
            }))
        {
            WriteProjectionValue(writer, root);
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    internal static byte[] CanonicalizeDeclarations(JsonElement declarations)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
            stream,
            new JsonWriterOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                Indented = false,
                SkipValidation = false,
            }))
        {
            WriteValue(writer, declarations, "declarations");
        }

        return stream.ToArray();
    }

    private static void WriteValue(
        Utf8JsonWriter writer,
        JsonElement value,
        string? propertyName)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var properties = value.EnumerateObject()
                    .ToDictionary(property => property.Name, StringComparer.Ordinal);
                var orderingParent = propertyName switch
                {
                    "classification" when value.TryGetProperty("recordType", out var recordType) =>
                        recordType.GetString(),
                    "policyContributions" when value.TryGetProperty("generatedOutput", out _) =>
                        "generatedPolicyContribution",
                    "policyContributions" => "repositoryPolicyContribution",
                    "subject" when value.TryGetProperty("parentSymbolRef", out _) =>
                        "componentSubject",
                    _ => propertyName,
                };
                foreach (var name in OrderedProperties(properties.Keys, orderingParent))
                {
                    writer.WritePropertyName(name);
                    WriteValue(writer, properties[name].Value, name);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in OrderedItems(value, propertyName))
                {
                    WriteValue(writer, item, propertyName);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                var text = value.GetString()!;
                AuditJsonModel.RejectUnpairedSurrogates(text);
                writer.WriteRawValue(EscapeJsonString(text), skipInputValidation: false);
                break;
            case JsonValueKind.Number:
                AuditJsonModel.ValidateCanonicalInteger(value.GetRawText());
                writer.WriteRawValue(value.GetRawText(), skipInputValidation: false);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw AuditJsonModel.Failure(
                    AuditValidationCode.InvalidShape,
                    "The Audit Result contains an unsupported JSON value.");
        }
    }

    private static void WriteProjectionValue(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var properties = value.EnumerateObject()
                    .ToDictionary(property => property.Name, StringComparer.Ordinal);
                foreach (var name in properties.Keys.OrderBy(name => name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(name);
                    WriteProjectionValue(writer, properties[name].Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteProjectionValue(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                var text = value.GetString()!;
                AuditJsonModel.RejectUnpairedSurrogates(text);
                writer.WriteRawValue(EscapeJsonString(text), skipInputValidation: false);
                break;
            case JsonValueKind.Number:
                AuditJsonModel.ValidateCanonicalInteger(value.GetRawText());
                writer.WriteRawValue(value.GetRawText(), skipInputValidation: false);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw AuditJsonModel.Failure(
                    AuditValidationCode.InvalidShape,
                    "The validated configuration projection contains an unsupported JSON value.");
        }
    }

    private static IEnumerable<string> OrderedProperties(
        IEnumerable<string> names,
        string? parent)
    {
        string[] order = parent switch
        {
            null => ["auditResultVersion", "policyConfigurationVersion", "taxonomyRegistryVersion", "targetProfile", "results"],
            "results" => ["classification", "policyContributions", "policyExpectation", "policyResolution", "documentationObservation", "auditOutcome", "reasonCode", "evidenceIds", "evidenceAuthority", "evidenceBundle"],
            "TargetClassification" => ["recordType", "symbolRef", "primaryKind", "traits", "origin", "supportStatus", "skipReason"],
            "ComponentClassification" => ["recordType", "parentSymbolRef", "componentKind", "identity", "origin", "supportStatus", "skipReason"],
            "UnresolvedClassification" => ["recordType", "compilationContextRef", "origin", "supportStatus", "skipReason", "candidateLocator"],
            "symbolRef" or "parentSymbolRef" or "subject" => ["compilationContextRef", "documentationCommentId"],
            "componentSubject" => ["parentSymbolRef", "componentKind", "identity"],
            "candidateLocator" or "locator" => ["repository", "generatedSource", "toolGenerated", "metadata", "generatedOutput", "synthetic"],
            "repository" => ["path", "span"],
            "generatedSource" => ["generatorId", "hintNameId", "span"],
            "toolGenerated" => ["producerId", "outputId", "span"],
            "generatedOutput" => ["producerKind", "producerId", "outputId", "sourceSha256", "span"],
            "metadata" => ["assemblyIdentity", "documentationCommentId"],
            "synthetic" => ["fixtureId"],
            "span" => ["start", "end"],
            "repositoryPolicyContribution" => ["projectPath", "sourcePath", "policyExpectation", "matchedRuleId"],
            "generatedPolicyContribution" => ["projectPath", "generatedOutput", "policyExpectation", "matchedRuleId"],
            "evidenceAuthority" => ["declarationSetId", "completeness", "declarations"],
            "declarations" => ["declarationId", "authorityRole", "blockState", "evidenceId", "componentLocalName", "componentMatch"],
            "evidenceBundle" => ["evidenceBundleVersion", "availabilityStatus", "omissionReason", "items", "observationSubject"],
            "observationSubject" => ["observationSubjectRef", "compilationContextRef", "subject", "authoritativeDeclarationSetDigest", "authoritativeDeclarationCount"],
            "items" => ["evidenceId", "subject", "kind", "relation", "excerpt", "sha256", "originalUtf8ByteCount", "includedUtf8ByteCount", "omittedUtf8ByteCount", "isTruncated", "locator"],
            _ => [],
        };
        return names
            .OrderBy(name => Array.IndexOf(order, name) is var index && index >= 0
                ? index
                : int.MaxValue)
            .ThenBy(name => name, StringComparer.Ordinal);
    }

    private static IEnumerable<JsonElement> OrderedItems(
        JsonElement value,
        string? propertyName)
    {
        var items = value.EnumerateArray().ToArray();
        return propertyName switch
        {
            "results" => items.OrderBy(item =>
                GetResultSortKey(item.GetProperty("classification"))),
            "policyContributions" => items.OrderBy(
                AuditJsonModel.PolicyContributionKey,
                StringComparer.Ordinal),
            "evidenceIds" => items.OrderBy(item => item.GetString(), StringComparer.Ordinal),
            "declarations" => items.OrderBy(
                item => item.GetProperty("declarationId").GetString(),
                StringComparer.Ordinal),
            "items" => items.OrderBy(
                item => item.GetProperty("evidenceId").GetString(),
                StringComparer.Ordinal),
            "traits" => items.OrderBy(item => item.GetString(), StringComparer.Ordinal),
            _ => items,
        };
    }

    private static ResultSortKey GetResultSortKey(JsonElement classification) =>
        classification.GetProperty("recordType").GetString() switch
        {
            "TargetClassification" => new(
                0,
                classification.GetProperty("symbolRef").GetProperty("compilationContextRef").GetString()!,
                0,
                classification.GetProperty("symbolRef").GetProperty("documentationCommentId").GetString()!,
                string.Empty,
                false,
                0,
                0),
            "ComponentClassification" => new(
                1,
                classification.GetProperty("parentSymbolRef").GetProperty("compilationContextRef").GetString()!,
                0,
                classification.GetProperty("parentSymbolRef").GetProperty("documentationCommentId").GetString()!,
                classification.GetProperty("componentKind").GetString()!,
                false,
                0,
                0,
                classification.GetProperty("identity").GetString()!),
            "UnresolvedClassification" => GetUnresolvedSortKey(classification),
            _ => throw AuditJsonModel.Failure(
                AuditValidationCode.InvalidClassification,
                "Unknown Audit Result classification type."),
        };

    private static ResultSortKey GetUnresolvedSortKey(JsonElement classification)
    {
        var locator = classification.GetProperty("candidateLocator");
        if (locator.TryGetProperty("repository", out var repository))
        {
            return CreateUnresolvedKey(
                classification,
                0,
                AuditJsonModel.NormalizeRepositoryPath(
                    repository.GetProperty("path").GetString()!),
                string.Empty,
                repository);
        }

        if (locator.TryGetProperty("generatedSource", out var generatedSource))
        {
            return CreateUnresolvedKey(
                classification,
                1,
                generatedSource.GetProperty("generatorId").GetString()!,
                generatedSource.GetProperty("hintNameId").GetString()!,
                generatedSource);
        }

        if (locator.TryGetProperty("toolGenerated", out var toolGenerated))
        {
            return CreateUnresolvedKey(
                classification,
                2,
                toolGenerated.GetProperty("producerId").GetString()!,
                toolGenerated.GetProperty("outputId").GetString()!,
                toolGenerated);
        }

        return new(
            2,
            classification.GetProperty("compilationContextRef").GetString()!,
            3,
            locator.GetProperty("synthetic").GetProperty("fixtureId").GetString()!,
            string.Empty,
            false,
            0,
            0);
    }

    private static ResultSortKey CreateUnresolvedKey(
        JsonElement classification,
        int rank,
        string field1,
        string field2,
        JsonElement locator)
    {
        var hasSpan = locator.TryGetProperty("span", out var span);
        return new(
            2,
            classification.GetProperty("compilationContextRef").GetString()!,
            rank,
            field1,
            field2,
            hasSpan,
            hasSpan ? span.GetProperty("start").GetInt32() : 0,
            hasSpan ? span.GetProperty("end").GetInt32() : 0);
    }

    private static string EscapeJsonString(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (character < 0x20)
                    {
                        builder.Append("\\u")
                            .Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        return builder.Append('"').ToString();
    }

    private readonly record struct ResultSortKey(
        int TypeRank,
        string Primary,
        int VariantRank,
        string Field1,
        string Field2,
        bool HasSpan,
        int Start,
        int End,
        string Field3 = "") : IComparable<ResultSortKey>
    {
        public int CompareTo(ResultSortKey other)
        {
            var result = TypeRank.CompareTo(other.TypeRank);
            if (result != 0)
            {
                return result;
            }

            result = string.CompareOrdinal(Primary, other.Primary);
            if (result != 0)
            {
                return result;
            }

            result = VariantRank.CompareTo(other.VariantRank);
            if (result != 0)
            {
                return result;
            }

            result = string.CompareOrdinal(Field1, other.Field1);
            if (result != 0)
            {
                return result;
            }

            result = string.CompareOrdinal(Field2, other.Field2);
            if (result != 0)
            {
                return result;
            }

            result = string.CompareOrdinal(Field3, other.Field3);
            if (result != 0)
            {
                return result;
            }

            result = HasSpan.CompareTo(other.HasSpan);
            if (result != 0)
            {
                return result;
            }

            result = Start.CompareTo(other.Start);
            return result != 0 ? result : End.CompareTo(other.End);
        }
    }
}
