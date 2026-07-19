using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Json.Schema;

namespace ContractScribe.Tests;

public sealed class AuditResultContractTests
{
    private static readonly Lazy<JsonSchema> AuditSchema = new(() => JsonSchema.FromText(File.ReadAllText(Path.Combine(FindRepositoryRoot(), "schemas", "audit-result", "v1.schema.json"))));
    private static readonly Lazy<JsonSchema> ClassificationSchema = new(LoadClassificationSchema);
    private static readonly Lazy<JsonSchema> EvidenceSchema = new(() => JsonSchema.FromText(File.ReadAllText(Path.Combine(FindRepositoryRoot(), "schemas", "symbol-evidence-taxonomy", "v1.schema.json")).Replace("  \"$id\": \"https://contract-scribe.dev/schemas/symbol-evidence-taxonomy/v1.schema.json\",\r\n", string.Empty, StringComparison.Ordinal).Replace("  \"$id\": \"https://contract-scribe.dev/schemas/symbol-evidence-taxonomy/v1.schema.json\",\n", string.Empty, StringComparison.Ordinal)));

    [Fact]
    public void PublicFixtures_CoverMatrixAndPassSchemaAndSemanticOracle()
    {
        var root = Path.Combine(FindRepositoryRoot(), "tests", "fixtures", "audit-result", "v1");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "cases.json")));
        var caseIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fixture in manifest.RootElement.GetProperty("cases").EnumerateArray())
        {
            var caseId = fixture.GetProperty("caseId").GetString()!;
            Assert.True(caseIds.Add(caseId));
            var payload = File.ReadAllBytes(Path.Combine(root, fixture.GetProperty("payloadFile").GetString()!.Replace('/', Path.DirectorySeparatorChar)));
            using var document = ParseStrict(payload);
            Assert.True(AuditSchema.Value.Evaluate(document.RootElement).IsValid, caseId);
            ValidateDocument(document.RootElement);
            var result = document.RootElement.GetProperty("results")[0];
            Assert.Equal(fixture.GetProperty("outcome").GetString(), result.GetProperty("auditOutcome").GetString());
            Assert.Equal(fixture.GetProperty("reason").GetString(), result.GetProperty("reasonCode").GetString());
        }

        Assert.Equal(8, caseIds.Count);
    }

    [Fact]
    public void CanonicalSerialization_SortsLogicalInputAndProducesStableUtf8Lf()
    {
        var root = Path.Combine(FindRepositoryRoot(), "tests", "fixtures", "audit-result", "v1", "payloads");
        var original = File.ReadAllBytes(Path.Combine(root, "required-present.json"));
        using var document = ParseStrict(original);
        var first = Canonicalize(document.RootElement);
        var shuffled = $"{{\"results\":{document.RootElement.GetProperty("results").GetRawText()},\"taxonomyRegistryVersion\":1,\"auditResultVersion\":1,\"policyConfigurationVersion\":1}}";
        using var shuffledDocument = ParseStrict(Encoding.UTF8.GetBytes(shuffled));
        var second = Canonicalize(document.RootElement);
        Assert.Equal(first, second);
        Assert.Equal(first, Canonicalize(shuffledDocument.RootElement));
        Assert.Equal(File.ReadAllBytes(Path.Combine(FindRepositoryRoot(), "tests", "fixtures", "audit-result", "v1", "golden", "required-present.canonical.json")), first);
        Assert.Equal((byte)'\n', first[^1]);
        Assert.NotEqual((byte)0xEF, first[0]);
        Assert.DoesNotContain((byte)'\r', first);
    }

    [Fact]
    public void InvalidVectors_FailClosed()
    {
        var valid = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "tests", "fixtures", "audit-result", "v1", "payloads", "required-present.json"));
        var invalid = new[]
        {
            valid.Replace("\"taxonomyRegistryVersion\": 1", "\"taxonomyRegistryVersion\": 2", StringComparison.Ordinal),
            valid.Replace("\"recordType\": \"TargetClassification\"", "\"recordType\": \"RelationObservation\"", StringComparison.Ordinal),
            valid.Replace("\"auditOutcome\": \"audit.outcome.compliant\"", "\"auditOutcome\": \"audit.outcome.violation\"", StringComparison.Ordinal),
            valid.Replace("\"evidenceIds\": [ \"evidence.xml-doc\" ]", "\"evidenceIds\": []", StringComparison.Ordinal),
            valid.Replace("\"matchedRuleId\": \"required-docs\"", "\"matchedRuleId\": null", StringComparison.Ordinal).Replace("\"matchedRuleId\": null", "\"matchedRuleId\": null, \"unexpected\": true", StringComparison.Ordinal)
        };

        foreach (var (payload, index) in invalid.Select((payload, index) => (payload, index)))
        {
            using var document = JsonDocument.Parse(payload);
            Assert.False(AuditSchema.Value.Evaluate(document.RootElement).IsValid && IsSemanticallyValid(document.RootElement), $"Invalid vector {index} was accepted.");
        }

        Assert.Throws<FormatException>(() => ParseStrict(Encoding.UTF8.GetBytes("\uFEFF{}")));
    }

    private static JsonDocument ParseStrict(byte[] payload)
    {
        if (payload.Length >= 3 && payload[0] == 0xEF && payload[1] == 0xBB && payload[2] == 0xBF) throw new FormatException("A UTF-8 BOM is not canonical.");
        try
        {
            var document = JsonDocument.Parse(payload, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            RejectDuplicateProperties(document.RootElement);
            return document;
        }
        catch (JsonException exception)
        {
            throw new FormatException("Invalid JSON or UTF-8.", exception);
        }
    }

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new FormatException($"Duplicate property: {property.Name}");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item);
        }
    }

    private static void ValidateDocument(JsonElement document)
    {
        Assert.Equal(1, document.GetProperty("auditResultVersion").GetInt32());
        Assert.Equal(1, document.GetProperty("policyConfigurationVersion").GetInt32());
        Assert.Equal(1, document.GetProperty("taxonomyRegistryVersion").GetInt32());
        var subjects = new HashSet<string>(StringComparer.Ordinal);
        foreach (var result in document.GetProperty("results").EnumerateArray())
        {
            ValidateClassification(result.GetProperty("classification"));
            var subjectKey = GetSubjectKey(result.GetProperty("classification"));
            Assert.True(subjects.Add(subjectKey), $"Duplicate result subject: {subjectKey}");
            ValidatePolicy(result);
            ValidateEvidence(result);
            ValidateOutcome(result);
        }
    }

    private static bool IsSemanticallyValid(JsonElement document)
    {
        try
        {
            ValidateDocument(document);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void ValidateClassification(JsonElement classification)
    {
        Assert.True(ClassificationSchema.Value.Evaluate(classification).IsValid);
        var recordType = classification.GetProperty("recordType").GetString();
        Assert.Contains(recordType, new[] { "TargetClassification", "ComponentClassification", "UnresolvedClassification" });
        Assert.False(classification.TryGetProperty("recordType", out var _) && recordType == "RelationObservation");
        var supportStatus = classification.GetProperty("supportStatus").GetString();
        if (recordType == "TargetClassification")
        {
            Assert.True(classification.TryGetProperty("symbolRef", out var symbolRef));
            ValidateSymbolRef(symbolRef);
            Assert.NotNull(classification.GetProperty("primaryKind").GetString());
            Assert.Equal(JsonValueKind.Array, classification.GetProperty("traits").ValueKind);
        }
        else if (recordType == "ComponentClassification")
        {
            ValidateSymbolRef(classification.GetProperty("parentSymbolRef"));
            Assert.StartsWith("component.", classification.GetProperty("componentKind").GetString());
            Assert.False(string.IsNullOrEmpty(classification.GetProperty("identity").GetString()));
        }
        else
        {
            Assert.Equal("support.unavailable-context", supportStatus);
            Assert.StartsWith("skip.unavailable.", classification.GetProperty("skipReason").GetString());
            Assert.True(classification.TryGetProperty("candidateLocator", out _));
        }

        if (supportStatus == "support.supported") Assert.False(classification.TryGetProperty("skipReason", out _));
        else Assert.True(classification.TryGetProperty("skipReason", out _));
    }

    private static void ValidateSymbolRef(JsonElement symbolRef)
    {
        Assert.Matches("^[a-z0-9][a-z0-9._-]{0,127}$", symbolRef.GetProperty("compilationContextRef").GetString()!);
        Assert.False(string.IsNullOrEmpty(symbolRef.GetProperty("documentationCommentId").GetString()));
    }

    private static void ValidatePolicy(JsonElement result)
    {
        var contributions = result.GetProperty("policyContributions").EnumerateArray().ToArray();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        string? previousKey = null;
        var expectations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var contribution in contributions)
        {
            var project = contribution.GetProperty("projectPath").GetString()!;
            var source = contribution.GetProperty("sourcePath").GetString()!;
            var key = project + "\u001f" + source;
            Assert.True(keys.Add(key), "Duplicate policy contribution pair.");
            Assert.True(previousKey is null || string.CompareOrdinal(previousKey, key) < 0, "Policy contributions are not sorted.");
            previousKey = key;
            expectations.Add(contribution.GetProperty("policyExpectation").GetString()!);
        }

        var resolution = result.GetProperty("policyResolution").GetString();
        var expectation = result.GetProperty("policyExpectation");
        if (contributions.Length == 0) Assert.Equal("unavailable", resolution);
        else if (expectations.Count > 1) Assert.Equal("conflict", resolution);
        else Assert.Contains(resolution, new[] { "single", "all-declarations-agree", "unavailable" });
        if (resolution == "conflict") Assert.Equal(JsonValueKind.Null, expectation.ValueKind);
    }

    private static void ValidateEvidence(JsonElement result)
    {
        var bundle = result.GetProperty("evidenceBundle");
        Assert.True(EvidenceSchema.Value.Evaluate(bundle).IsValid);
        var status = bundle.GetProperty("availabilityStatus").GetString();
        var items = bundle.GetProperty("items").EnumerateArray().ToArray();
        var ids = items.Select(item => item.GetProperty("evidenceId").GetString()!).ToArray();
        Assert.Equal(ids.OrderBy(id => id, StringComparer.Ordinal), ids);
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        var referenced = result.GetProperty("evidenceIds").EnumerateArray().Select(id => id.GetString()!).ToArray();
        Assert.Equal(referenced.OrderBy(id => id, StringComparer.Ordinal), referenced);
        Assert.Equal(referenced.Length, referenced.Distinct(StringComparer.Ordinal).Count());
        Assert.All(referenced, id => Assert.Contains(items, item => item.GetProperty("evidenceId").GetString() == id && !item.GetProperty("isTruncated").GetBoolean()));
        if (status == "evidence.bundle.unavailable") Assert.Empty(items);
        if (status == "evidence.bundle.complete")
        {
            Assert.NotEmpty(items);
            Assert.False(bundle.TryGetProperty("omissionReason", out _));
            Assert.All(items, item => Assert.False(item.GetProperty("isTruncated").GetBoolean()));
        }
        if (status == "evidence.bundle.partial")
        {
            Assert.NotEmpty(items);
            Assert.Equal("evidence.omission.budget-exhausted", bundle.GetProperty("omissionReason").GetString());
            Assert.Empty(referenced);
            Assert.Equal("audit.reason.evidence-incomplete", result.GetProperty("reasonCode").GetString());
        }
    }

    private static void ValidateOutcome(JsonElement result)
    {
        var outcome = result.GetProperty("auditOutcome").GetString();
        var reason = result.GetProperty("reasonCode").GetString();
        var expectation = result.GetProperty("policyExpectation").GetString();
        var observation = result.GetProperty("documentationObservation").GetString();
        if (reason == "audit.reason.classification-skipped")
        {
            Assert.Equal("audit.outcome.skipped", outcome);
            Assert.Equal(JsonValueKind.Null, result.GetProperty("documentationObservation").ValueKind);
            Assert.Equal(JsonValueKind.Null, result.GetProperty("policyExpectation").ValueKind);
            Assert.Empty(result.GetProperty("evidenceIds").EnumerateArray());
            return;
        }
        if (reason is "audit.reason.policy-conflict" or "audit.reason.policy-unavailable" or "audit.reason.documentation-unavailable" or "audit.reason.evidence-incomplete")
        {
            Assert.Equal("audit.outcome.skipped", outcome);
            return;
        }

        Assert.NotNull(expectation);
        Assert.NotNull(observation);
        var expected = (expectation, observation) switch
        {
            ("required", "documentation.present") => ("audit.outcome.compliant", "audit.reason.required-present"),
            ("required", "documentation.absent") => ("audit.outcome.violation", "audit.reason.required-absent"),
            ("optional", "documentation.present") => ("audit.outcome.compliant", "audit.reason.optional-present"),
            ("optional", "documentation.absent") => ("audit.outcome.compliant", "audit.reason.optional-absent"),
            ("forbidden", "documentation.present") => ("audit.outcome.violation", "audit.reason.forbidden-present"),
            ("forbidden", "documentation.absent") => ("audit.outcome.compliant", "audit.reason.forbidden-absent"),
            _ => throw new InvalidOperationException("Invalid matrix combination.")
        };
        if (!string.Equals(expected.Item1, outcome, StringComparison.Ordinal) || !string.Equals(expected.Item2, reason, StringComparison.Ordinal)) throw new InvalidOperationException("Outcome matrix mismatch.");
        if (!result.GetProperty("evidenceIds").EnumerateArray().Any()) throw new InvalidOperationException("A matrix result needs evidence.");
    }

    private static string GetSubjectKey(JsonElement classification)
    {
        return classification.GetProperty("recordType").GetString() switch
        {
            "TargetClassification" => "target|" + classification.GetProperty("symbolRef").GetProperty("compilationContextRef").GetString() + "|" + classification.GetProperty("symbolRef").GetProperty("documentationCommentId").GetString(),
            "ComponentClassification" => "component|" + classification.GetProperty("parentSymbolRef").GetProperty("compilationContextRef").GetString() + "|" + classification.GetProperty("parentSymbolRef").GetProperty("documentationCommentId").GetString() + "|" + classification.GetProperty("componentKind").GetString() + "|" + classification.GetProperty("identity").GetString(),
            "UnresolvedClassification" => "unresolved|" + classification.GetProperty("compilationContextRef").GetString() + "|" + classification.GetProperty("candidateLocator").GetRawText(),
            _ => throw new InvalidOperationException("Unknown subject.")
        };
    }

    private static byte[] Canonicalize(JsonElement root)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Indented = false, SkipValidation = false }))
        {
            WriteValue(writer, root, null);
        }
        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    private static void WriteValue(Utf8JsonWriter writer, JsonElement value, string? propertyName)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var properties = value.EnumerateObject().ToDictionary(property => property.Name, StringComparer.Ordinal);
                foreach (var name in OrderedProperties(properties.Keys, propertyName))
                {
                    writer.WritePropertyName(name);
                    WriteValue(writer, properties[name].Value, name);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in OrderedItems(value, propertyName)) WriteValue(writer, item, propertyName);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                var text = value.GetString()!;
                RejectUnpairedSurrogates(text);
                writer.WriteStringValue(text);
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
            null => new[] { "auditResultVersion", "policyConfigurationVersion", "taxonomyRegistryVersion", "results" },
            "results" => new[] { "classification", "policyContributions", "policyExpectation", "policyResolution", "documentationObservation", "auditOutcome", "reasonCode", "evidenceIds", "evidenceBundle" },
            "classification" => new[] { "recordType", "symbolRef", "primaryKind", "traits", "origin", "supportStatus", "skipReason", "parentSymbolRef", "componentKind", "identity", "compilationContextRef", "candidateLocator" },
            "symbolRef" or "parentSymbolRef" or "subject" => new[] { "compilationContextRef", "documentationCommentId" },
            "candidateLocator" or "locator" => new[] { "repository", "generatedSource", "metadata", "synthetic" },
            "repository" => new[] { "path", "span" },
            "generatedSource" => new[] { "generatorId", "hintNameId", "span" },
            "metadata" => new[] { "assemblyIdentity", "documentationCommentId" },
            "synthetic" => new[] { "fixtureId" },
            "span" => new[] { "start", "end" },
            "policyContributions" => new[] { "projectPath", "sourcePath", "policyExpectation", "matchedRuleId" },
            "evidenceBundle" => new[] { "evidenceBundleVersion", "availabilityStatus", "omissionReason", "items" },
            "items" => new[] { "evidenceId", "subject", "kind", "relation", "excerpt", "sha256", "originalUtf8ByteCount", "includedUtf8ByteCount", "omittedUtf8ByteCount", "isTruncated", "locator" },
            _ => Array.Empty<string>()
        };
        return names.OrderBy(name => Array.IndexOf(order, name) is var index && index >= 0 ? index : int.MaxValue).ThenBy(name => name, StringComparer.Ordinal);
    }

    private static IEnumerable<JsonElement> OrderedItems(JsonElement value, string? propertyName)
    {
        var items = value.EnumerateArray().ToArray();
        return propertyName switch
        {
            "results" => items.OrderBy(item => GetSubjectKey(item.GetProperty("classification")), StringComparer.Ordinal),
            "policyContributions" => items.OrderBy(item => item.GetProperty("projectPath").GetString(), StringComparer.Ordinal).ThenBy(item => item.GetProperty("sourcePath").GetString(), StringComparer.Ordinal),
            "evidenceIds" => items.OrderBy(item => item.GetString(), StringComparer.Ordinal),
            "items" => items.OrderBy(item => item.GetProperty("evidenceId").GetString(), StringComparer.Ordinal),
            "traits" => items.OrderBy(item => item.GetString(), StringComparer.Ordinal),
            _ => items
        };
    }

    private static void RejectUnpairedSurrogates(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1])) throw new FormatException("Unpaired UTF-16 surrogate.");
                index++;
            }
            else if (char.IsLowSurrogate(value[index])) throw new FormatException("Unpaired UTF-16 surrogate.");
        }
    }

    private static void ValidateCanonicalInteger(string raw)
    {
        if (raw == "0") return;
        var start = raw[0] == '-' ? 1 : 0;
        if (start == raw.Length || raw[start] == '0' || raw[start..].Any(character => character is < '0' or > '9')) throw new FormatException("Canonical JSON numbers must be signed integers without leading zeroes or negative zero.");
    }

    private static JsonSchema LoadClassificationSchema()
    {
        var manifestPath = Path.Combine(FindRepositoryRoot(), "schemas", "symbol-evidence-taxonomy", "v1.manifest.schema.json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var schema = $"{{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",\"$ref\":\"#/$defs/classificationRecord\",\"$defs\":{manifest.RootElement.GetProperty("$defs").GetRawText()}}}";
        return JsonSchema.FromText(schema);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ContractScribe.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
