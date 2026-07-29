using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ContractScribe.ContractBaselineProbe;

public static class AuditResultCanonicalizer
{
    private static readonly JsonSerializerOptions CompactJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static byte[] Canonicalize(JsonElement root)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
            stream,
            new JsonWriterOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                Indented = false,
                SkipValidation = false
            }))
        {
            WriteValue(writer, root, null);
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    public static byte[] CanonicalizeDeclarations(JsonElement declarations)
    {
        if (declarations.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("Authority declarations must be an array.");
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
            stream,
            new JsonWriterOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                Indented = false,
                SkipValidation = false
            }))
        {
            WriteValue(writer, declarations, "declarations");
        }

        return stream.ToArray();
    }

    public static string ComputeDeclarationDigest(JsonElement declarations) =>
        Convert.ToHexString(SHA256.HashData(CanonicalizeDeclarations(declarations))).ToLowerInvariant();

    public static string ComputeObservationSubjectRef(JsonElement observation)
    {
        var subject = observation.GetProperty("subject");
        var canonicalSubject = subject.TryGetProperty("parentSymbolRef", out var parentSymbolRef)
            ? new JsonObject
            {
                ["parentSymbolRef"] = new JsonObject
                {
                    ["compilationContextRef"] = parentSymbolRef.GetProperty("compilationContextRef").GetString(),
                    ["documentationCommentId"] = parentSymbolRef.GetProperty("documentationCommentId").GetString()
                },
                ["componentKind"] = subject.GetProperty("componentKind").GetString(),
                ["identity"] = subject.GetProperty("identity").GetString()
            }
            : new JsonObject
            {
                ["compilationContextRef"] = subject.GetProperty("compilationContextRef").GetString(),
                ["documentationCommentId"] = subject.GetProperty("documentationCommentId").GetString()
            };
        var preimage = new JsonObject
        {
            ["compilationContextRef"] = observation.GetProperty("compilationContextRef").GetString(),
            ["subject"] = canonicalSubject,
            ["authoritativeDeclarationSetDigest"] = observation.GetProperty("authoritativeDeclarationSetDigest").GetString(),
            ["authoritativeDeclarationCount"] = observation.GetProperty("authoritativeDeclarationCount").GetInt32()
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(preimage, CompactJson);
        return "obs." + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public static string DeriveDocumentationObservation(JsonElement subject, JsonElement authority)
    {
        var declarations = authority.GetProperty("declarations").EnumerateArray().ToArray();
        if (!HasValidAuthorityMode(declarations))
        {
            throw new FormatException("Authority declarations do not form one closed selection mode.");
        }

        var isComponent = subject.TryGetProperty("parentSymbolRef", out _);
        if (!isComponent)
        {
            if (declarations.Any(declaration => declaration.GetProperty("blockState").GetString() is "well-formed" or "malformed"))
            {
                return "documentation.present";
            }
            if (authority.GetProperty("completeness").GetString() == "complete"
                && declarations.All(declaration => declaration.GetProperty("blockState").GetString() is "no-block" or "whitespace-only"))
            {
                return "documentation.absent";
            }
            return "documentation.unavailable";
        }

        if (declarations.Any(declaration =>
            declaration.GetProperty("blockState").GetString() == "well-formed"
            && declaration.TryGetProperty("componentMatch", out var match)
            && match.GetString() == "present"))
        {
            return "documentation.present";
        }
        if (declarations.Any(declaration => declaration.GetProperty("blockState").GetString() == "malformed"))
        {
            return "documentation.unavailable";
        }
        if (authority.GetProperty("completeness").GetString() == "complete"
            && declarations.All(declaration =>
                declaration.GetProperty("blockState").GetString() is "no-block" or "whitespace-only"
                || declaration.TryGetProperty("componentMatch", out var match) && match.GetString() == "absent"))
        {
            return "documentation.absent";
        }
        return "documentation.unavailable";
    }

    public static void ValidateReplayDocument(JsonElement document)
    {
        Require(document.GetProperty("auditResultVersion").GetInt32() == 1, "Audit Result version must be 1.");
        Require(document.GetProperty("policyConfigurationVersion").GetInt32() == 1, "Policy version must be 1.");
        Require(document.GetProperty("taxonomyRegistryVersion").GetInt32() == 1, "Taxonomy version must be 1.");
        Require(document.GetProperty("targetProfile").GetString() is "profile.external-api" or "profile.assembly-visible", "Unknown target profile.");

        foreach (var result in document.GetProperty("results").EnumerateArray())
        {
            var classification = result.GetProperty("classification");
            var recordType = classification.GetProperty("recordType").GetString();
            Require(recordType is "TargetClassification" or "ComponentClassification" or "UnresolvedClassification", "Unknown classification.");
            if (recordType == "UnresolvedClassification")
            {
                ValidateCandidateLocator(classification.GetProperty("candidateLocator"));
            }

            var observationValue = result.GetProperty("documentationObservation");
            var requiresAuthority =
                observationValue.ValueKind == JsonValueKind.String
                && observationValue.GetString() is "documentation.present" or "documentation.absent"
                || result.GetProperty("reasonCode").GetString() == "audit.reason.documentation-unavailable.malformed-xml";
            var hasAuthority = result.TryGetProperty("evidenceAuthority", out var authority);
            var bundle = result.GetProperty("evidenceBundle");
            var hasObservation = bundle.TryGetProperty("observationSubject", out var observation);
            Require(hasAuthority == requiresAuthority, "Evidence authority presence does not match the result kind.");
            Require(hasObservation == requiresAuthority, "Observation subject presence does not match the result kind.");
            if (requiresAuthority)
            {
                ValidateAuthority(result, classification, authority, bundle, observation);
            }
        }
    }

    private static void ValidateAuthority(
        JsonElement result,
        JsonElement classification,
        JsonElement authority,
        JsonElement bundle,
        JsonElement observation)
    {
        var declarations = authority.GetProperty("declarations");
        var digest = ComputeDeclarationDigest(declarations);
        Require(authority.GetProperty("declarationSetId").GetString() == $"dset.{digest}", "Declaration set ID does not match canonical declarations.");
        Require(observation.GetProperty("authoritativeDeclarationSetDigest").GetString() == digest, "Observation digest does not match canonical declarations.");
        Require(observation.GetProperty("authoritativeDeclarationCount").GetInt32() == declarations.GetArrayLength(), "Observation declaration count mismatch.");
        Require(observation.GetProperty("observationSubjectRef").GetString() == ComputeObservationSubjectRef(observation), "Observation subject ref mismatch.");

        var expectedSubject = classification.GetProperty("recordType").GetString() == "ComponentClassification"
            ? CreateComponentSubject(classification)
            : classification.GetProperty("symbolRef");
        Require(JsonElement.DeepEquals(observation.GetProperty("subject"), expectedSubject), "Observation subject does not match classification.");
        Require(observation.GetProperty("compilationContextRef").GetString() == GetContext(expectedSubject), "Observation context does not match subject.");

        var declarationIds = new HashSet<string>(StringComparer.Ordinal);
        var declarationEvidenceIds = new HashSet<string>(StringComparer.Ordinal);
        var malformedEvidenceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var declaration in declarations.EnumerateArray())
        {
            Require(declarationIds.Add(declaration.GetProperty("declarationId").GetString()!), "Duplicate declaration ID.");
            var evidenceId = declaration.GetProperty("evidenceId").GetString()!;
            Require(declarationEvidenceIds.Add(evidenceId), "Duplicate declaration evidence ID.");
            if (declaration.GetProperty("blockState").GetString() == "malformed")
            {
                malformedEvidenceIds.Add(evidenceId);
            }
        }
        Require(HasValidAuthorityMode(declarations.EnumerateArray().ToArray()), "Authority declarations do not form one closed selection mode.");

        var evidenceItems = bundle.GetProperty("items").EnumerateArray()
            .ToDictionary(item => item.GetProperty("evidenceId").GetString()!, StringComparer.Ordinal);
        foreach (var evidenceId in declarationEvidenceIds)
        {
            Require(evidenceItems.TryGetValue(evidenceId, out var evidence), "Authority references missing evidence.");
            Require(JsonElement.DeepEquals(evidence.GetProperty("subject"), expectedSubject), "Authority evidence subject does not match classification.");
        }
        foreach (var evidence in malformedEvidenceIds.Select(evidenceId => evidenceItems[evidenceId]))
        {
            Require(evidence.GetProperty("kind").GetString() == "evidence.source.xml-documentation", "Malformed authority requires XML-documentation evidence.");
            Require(evidence.GetProperty("relation").GetString() == "evidence.documents", "Malformed authority requires documents relation.");
            Require(!evidence.GetProperty("isTruncated").GetBoolean(), "Malformed authority evidence must be untruncated.");
        }

        var referenced = result.GetProperty("evidenceIds").EnumerateArray().Select(value => value.GetString()!).ToHashSet(StringComparer.Ordinal);
        Require(referenced.SetEquals(declarationEvidenceIds), "Result evidence IDs do not cover the authority declaration set.");
        Require(malformedEvidenceIds.IsSubsetOf(referenced), "Malformed authority evidence must be referenced by the result.");

        var derivedObservation = DeriveDocumentationObservation(expectedSubject, authority);
        Require(result.GetProperty("documentationObservation").GetString() == derivedObservation, "Claimed documentation observation contradicts authority.");
        var malformedReason = result.GetProperty("reasonCode").GetString() == "audit.reason.documentation-unavailable.malformed-xml";
        Require(malformedReason == (derivedObservation == "documentation.unavailable" && malformedEvidenceIds.Count > 0), "Malformed reason does not match derived authority observation.");
    }

    private static JsonElement CreateComponentSubject(JsonElement classification)
    {
        var subject = new JsonObject
        {
            ["parentSymbolRef"] = JsonNode.Parse(classification.GetProperty("parentSymbolRef").GetRawText()),
            ["componentKind"] = classification.GetProperty("componentKind").GetString(),
            ["identity"] = classification.GetProperty("identity").GetString()
        };
        return JsonSerializer.SerializeToElement(subject);
    }

    private static string GetContext(JsonElement subject) =>
        subject.TryGetProperty("parentSymbolRef", out var parent)
            ? parent.GetProperty("compilationContextRef").GetString()!
            : subject.GetProperty("compilationContextRef").GetString()!;

    private static void ValidateCandidateLocator(JsonElement locator)
    {
        var variants = new[] { "repository", "generatedSource", "toolGenerated", "synthetic" }
            .Where(name => locator.TryGetProperty(name, out _))
            .ToArray();
        Require(variants.Length == 1, "Candidate locator must contain exactly one variant.");
        if (variants[0] == "repository")
        {
            var repository = locator.GetProperty("repository");
            Require(IsCanonicalRepositoryPath(repository.GetProperty("path").GetString()), "Repository candidate path must be canonical repository-relative form.");
            ValidateLocatorSpan(repository);
        }
        else if (variants[0] == "generatedSource")
        {
            var generated = locator.GetProperty("generatedSource");
            Require(IsGeneratedId(generated.GetProperty("generatorId").GetString(), "sgp."), "Invalid source generator ID.");
            Require(IsGeneratedId(generated.GetProperty("hintNameId").GetString(), "sgo."), "Invalid generated source output ID.");
            ValidateLocatorSpan(generated);
        }
        else if (variants[0] == "toolGenerated")
        {
            var generated = locator.GetProperty("toolGenerated");
            Require(IsGeneratedId(generated.GetProperty("producerId").GetString(), "tgp."), "Invalid tool producer ID.");
            Require(IsGeneratedId(generated.GetProperty("outputId").GetString(), "tgo."), "Invalid tool output ID.");
            ValidateLocatorSpan(generated);
        }
    }

    private static bool IsGeneratedId(string? value, string prefix) =>
        value is not null
        && value.Length == prefix.Length + 64
        && value.StartsWith(prefix, StringComparison.Ordinal)
        && value[prefix.Length..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void ValidateLocatorSpan(JsonElement locator)
    {
        if (locator.TryGetProperty("span", out var span))
        {
            Require(
                span.GetProperty("start").GetInt32()
                    <= span.GetProperty("end").GetInt32(),
                "Candidate locator span is reversed.");
        }
    }

    private static void WriteValue(Utf8JsonWriter writer, JsonElement value, string? propertyName)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var properties = value.EnumerateObject().ToDictionary(property => property.Name, StringComparer.Ordinal);
                var orderingParent = propertyName switch
                {
                    "classification" when value.TryGetProperty("recordType", out var recordType) => recordType.GetString(),
                    "policyContributions" when value.TryGetProperty("generatedOutput", out _) => "generatedPolicyContribution",
                    "policyContributions" => "repositoryPolicyContribution",
                    "subject" when value.TryGetProperty("parentSymbolRef", out _) => "componentSubject",
                    _ => propertyName
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
                RejectUnpairedSurrogates(text);
                writer.WriteRawValue(EscapeJsonString(text), skipInputValidation: false);
                break;
            case JsonValueKind.Number:
                ValidateCanonicalInteger(value.GetRawText());
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
                throw new InvalidOperationException("Unsupported JSON value.");
        }
    }

    private static IEnumerable<string> OrderedProperties(IEnumerable<string> names, string? parent)
    {
        var order = parent switch
        {
            null => new[] { "auditResultVersion", "policyConfigurationVersion", "taxonomyRegistryVersion", "targetProfile", "results" },
            "results" => new[] { "classification", "policyContributions", "policyExpectation", "policyResolution", "documentationObservation", "auditOutcome", "reasonCode", "evidenceIds", "evidenceAuthority", "evidenceBundle" },
            "TargetClassification" => new[] { "recordType", "symbolRef", "primaryKind", "traits", "origin", "supportStatus", "skipReason" },
            "ComponentClassification" => new[] { "recordType", "parentSymbolRef", "componentKind", "identity", "origin", "supportStatus", "skipReason" },
            "UnresolvedClassification" => new[] { "recordType", "compilationContextRef", "origin", "supportStatus", "skipReason", "candidateLocator" },
            "symbolRef" or "parentSymbolRef" or "subject" => new[] { "compilationContextRef", "documentationCommentId" },
            "componentSubject" => new[] { "parentSymbolRef", "componentKind", "identity" },
            "candidateLocator" or "locator" => new[] { "repository", "generatedSource", "toolGenerated", "metadata", "generatedOutput", "synthetic" },
            "repository" => new[] { "path", "span" },
            "generatedSource" => new[] { "generatorId", "hintNameId", "span" },
            "toolGenerated" => new[] { "producerId", "outputId", "span" },
            "generatedOutput" => new[] { "producerKind", "producerId", "outputId", "sourceSha256" },
            "metadata" => new[] { "assemblyIdentity", "documentationCommentId" },
            "synthetic" => new[] { "fixtureId" },
            "span" => new[] { "start", "end" },
            "repositoryPolicyContribution" => new[] { "projectPath", "sourcePath", "policyExpectation", "matchedRuleId" },
            "generatedPolicyContribution" => new[] { "projectPath", "generatedOutput", "policyExpectation", "matchedRuleId" },
            "evidenceAuthority" => new[] { "declarationSetId", "completeness", "declarations" },
            "declarations" => new[] { "declarationId", "authorityRole", "blockState", "evidenceId", "componentLocalName", "componentMatch" },
            "evidenceBundle" => new[] { "evidenceBundleVersion", "availabilityStatus", "omissionReason", "items", "observationSubject" },
            "observationSubject" => new[] { "observationSubjectRef", "compilationContextRef", "subject", "authoritativeDeclarationSetDigest", "authoritativeDeclarationCount" },
            "items" => new[] { "evidenceId", "subject", "kind", "relation", "excerpt", "sha256", "originalUtf8ByteCount", "includedUtf8ByteCount", "omittedUtf8ByteCount", "isTruncated", "locator" },
            _ => Array.Empty<string>()
        };
        return names
            .OrderBy(name => Array.IndexOf(order, name) is var index && index >= 0 ? index : int.MaxValue)
            .ThenBy(name => name, StringComparer.Ordinal);
    }

    private static IEnumerable<JsonElement> OrderedItems(JsonElement value, string? propertyName)
    {
        var items = value.EnumerateArray().ToArray();
        return propertyName switch
        {
            "results" => items.OrderBy(item => GetResultSortKey(item.GetProperty("classification"))),
            "policyContributions" => items.OrderBy(PolicyContributionKey, StringComparer.Ordinal),
            "evidenceIds" => items.OrderBy(item => item.GetString(), StringComparer.Ordinal),
            "declarations" => items.OrderBy(item => item.GetProperty("declarationId").GetString(), StringComparer.Ordinal),
            "items" => items.OrderBy(item => item.GetProperty("evidenceId").GetString(), StringComparer.Ordinal),
            "traits" => items.OrderBy(item => item.GetString(), StringComparer.Ordinal),
            _ => items
        };
    }

    private static ResultSortKey GetResultSortKey(JsonElement classification)
    {
        return classification.GetProperty("recordType").GetString() switch
        {
            "TargetClassification" => new ResultSortKey(0, classification.GetProperty("symbolRef").GetProperty("compilationContextRef").GetString()!, 0, classification.GetProperty("symbolRef").GetProperty("documentationCommentId").GetString()!, string.Empty, false, 0, 0),
            "ComponentClassification" => new ResultSortKey(1, classification.GetProperty("parentSymbolRef").GetProperty("compilationContextRef").GetString()!, 0, classification.GetProperty("parentSymbolRef").GetProperty("documentationCommentId").GetString()!, classification.GetProperty("componentKind").GetString()!, false, 0, 0, classification.GetProperty("identity").GetString()!),
            "UnresolvedClassification" => GetUnresolvedSortKey(classification),
            _ => throw new InvalidOperationException("Unknown result type.")
        };
    }

    private static ResultSortKey GetUnresolvedSortKey(JsonElement classification)
    {
        var locator = classification.GetProperty("candidateLocator");
        if (locator.TryGetProperty("repository", out var repository))
        {
            return CreateUnresolvedKey(classification, 0, repository.GetProperty("path").GetString()!, string.Empty, repository);
        }
        if (locator.TryGetProperty("generatedSource", out var generatedSource))
        {
            return CreateUnresolvedKey(classification, 1, generatedSource.GetProperty("generatorId").GetString()!, generatedSource.GetProperty("hintNameId").GetString()!, generatedSource);
        }
        if (locator.TryGetProperty("toolGenerated", out var toolGenerated))
        {
            return CreateUnresolvedKey(classification, 2, toolGenerated.GetProperty("producerId").GetString()!, toolGenerated.GetProperty("outputId").GetString()!, toolGenerated);
        }
        return new ResultSortKey(2, classification.GetProperty("compilationContextRef").GetString()!, 3, locator.GetProperty("synthetic").GetProperty("fixtureId").GetString()!, string.Empty, false, 0, 0);
    }

    private static ResultSortKey CreateUnresolvedKey(JsonElement classification, int rank, string field1, string field2, JsonElement locator)
    {
        var hasSpan = locator.TryGetProperty("span", out var span);
        return new ResultSortKey(2, classification.GetProperty("compilationContextRef").GetString()!, rank, field1, field2, hasSpan, hasSpan ? span.GetProperty("start").GetInt32() : 0, hasSpan ? span.GetProperty("end").GetInt32() : 0);
    }

    private static string PolicyContributionKey(JsonElement contribution)
    {
        var project = contribution.GetProperty("projectPath").GetString();
        if (contribution.TryGetProperty("sourcePath", out var source))
        {
            return $"A\0{project}\0{source.GetString()}";
        }

        var generated = contribution.GetProperty("generatedOutput");
        return $"B\0{project}\0{generated.GetProperty("producerKind").GetString()}\0{generated.GetProperty("producerId").GetString()}\0{generated.GetProperty("outputId").GetString()}";
    }

    private static bool HasValidAuthorityMode(IReadOnlyCollection<JsonElement> declarations)
    {
        var roles = declarations.Select(declaration => declaration.GetProperty("authorityRole").GetString()).ToArray();
        return roles.Length == 1 && roles[0] is "ordinary" or "partial-member-implementing" or "partial-member-defining-fallback"
            || roles.Length > 0 && roles.All(role => role == "partial-type-part");
    }

    private static string NormalizeRepositoryPath(string value) =>
        string.Join('/', value.Replace('\\', '/').Split('/').Where(segment => segment is not "" and not "."));

    private static bool IsCanonicalRepositoryPath(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !Path.IsPathRooted(value)
        && !(value.Length >= 2
            && value[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z'
            && value[1] == ':')
        && !value.Contains('\\', StringComparison.Ordinal)
        && !value.Split('/').Any(segment => segment is "" or "." or "..")
        && value == NormalizeRepositoryPath(value);

    private static void RejectUnpairedSurrogates(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                {
                    throw new FormatException("Unpaired UTF-16 surrogate.");
                }
                index++;
            }
            else if (char.IsLowSurrogate(value[index]))
            {
                throw new FormatException("Unpaired UTF-16 surrogate.");
            }
        }
    }

    private static string EscapeJsonString(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (character < 0x20)
                    {
                        builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
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

    private static void ValidateCanonicalInteger(string raw)
    {
        if (raw == "0")
        {
            return;
        }
        var start = raw[0] == '-' ? 1 : 0;
        if (start == raw.Length || raw[start] == '0' || raw[start..].Any(character => character is < '0' or > '9'))
        {
            throw new FormatException("Canonical JSON numbers must be signed integers without leading zeroes or negative zero.");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new FormatException(message);
        }
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
            if (result != 0) return result;
            result = string.CompareOrdinal(Primary, other.Primary);
            if (result != 0) return result;
            result = VariantRank.CompareTo(other.VariantRank);
            if (result != 0) return result;
            result = string.CompareOrdinal(Field1, other.Field1);
            if (result != 0) return result;
            result = string.CompareOrdinal(Field2, other.Field2);
            if (result != 0) return result;
            result = string.CompareOrdinal(Field3, other.Field3);
            if (result != 0) return result;
            result = HasSpan.CompareTo(other.HasSpan);
            if (result != 0) return result;
            result = Start.CompareTo(other.Start);
            return result != 0 ? result : End.CompareTo(other.End);
        }
    }
}
