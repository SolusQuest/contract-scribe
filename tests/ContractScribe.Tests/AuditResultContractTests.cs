using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        var referencedPayloads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fixture in manifest.RootElement.GetProperty("cases").EnumerateArray())
        {
            var caseId = fixture.GetProperty("caseId").GetString()!;
            Assert.True(caseIds.Add(caseId));
            var payloadFile = fixture.GetProperty("payloadFile").GetString()!;
            Assert.True(referencedPayloads.Add(payloadFile));
            var payload = File.ReadAllBytes(Path.Combine(root, payloadFile.Replace('/', Path.DirectorySeparatorChar)));
            using var document = ParseStrict(payload);
            Assert.True(AuditSchema.Value.Evaluate(document.RootElement).IsValid, caseId);
            ValidateDocument(document.RootElement);
            if (document.RootElement.GetProperty("results").GetArrayLength() > 0)
            {
                var result = document.RootElement.GetProperty("results")[0];
                Assert.Equal(fixture.GetProperty("outcome").GetString(), result.GetProperty("auditOutcome").GetString());
                Assert.Equal(fixture.GetProperty("reason").GetString(), result.GetProperty("reasonCode").GetString());
            }
        }

        Assert.Equal(18, caseIds.Count);
        Assert.Equal(referencedPayloads.Order(StringComparer.OrdinalIgnoreCase), Directory.EnumerateFiles(Path.Combine(root, "payloads"), "*.json", SearchOption.TopDirectoryOnly).Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/')).Order(StringComparer.OrdinalIgnoreCase));
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
    public void CanonicalEncoding_PreservesUnicodeScalarsAndUsesSpecifiedEscapes()
    {
        using var document = JsonDocument.Parse("{\"z\":\"\\u2028\",\"a\":\"\\u0001\"}");
        var text = Encoding.UTF8.GetString(Canonicalize(document.RootElement));
        Assert.Contains("\"a\":\"\\u0001\"", text, StringComparison.Ordinal);
        Assert.Contains("\"z\":\"\u2028\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u2028", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CanonicalSerialization_UsesClassificationTypeTotalOrder()
    {
        var root = FindRepositoryRoot();
        var combined = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "tests", "fixtures", "audit-result", "v1", "payloads", "locator-variants.json")))!.AsObject();
        var component = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "tests", "fixtures", "audit-result", "v1", "payloads", "optional-absent.json")))!.AsObject();
        var results = combined["results"]!.AsArray();
        results.Add(component["results"]!.AsArray()[0]!.DeepClone());
        results.Reverse();
        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(combined));
        var text = Encoding.UTF8.GetString(Canonicalize(document.RootElement));
        Assert.True(text.IndexOf("\"recordType\":\"TargetClassification\"", StringComparison.Ordinal) < text.IndexOf("\"recordType\":\"ComponentClassification\"", StringComparison.Ordinal));
        Assert.True(text.IndexOf("\"recordType\":\"ComponentClassification\"", StringComparison.Ordinal) < text.IndexOf("\"recordType\":\"UnresolvedClassification\"", StringComparison.Ordinal));
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

    [Fact]
    public void CheckedInInvalidCorpus_FailsClosed()
    {
        var root = Path.Combine(FindRepositoryRoot(), "tests", "fixtures", "audit-result", "v1");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "invalid-cases.json")));
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.RootElement.GetProperty("cases").EnumerateArray())
        {
            var relative = entry.GetProperty("payloadFile").GetString()!;
            Assert.True(listed.Add(relative));
            var payload = File.ReadAllBytes(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (entry.GetProperty("canonical").GetBoolean())
            {
                Assert.False(IsCanonicalBytes(payload), relative);
                continue;
            }

            try
            {
                using var document = ParseStrict(payload);
                Assert.False(AuditSchema.Value.Evaluate(document.RootElement).IsValid && IsSemanticallyValid(document.RootElement), relative);
            }
            catch (FormatException)
            {
            }
        }

        Assert.Equal(listed.Order(StringComparer.OrdinalIgnoreCase), Directory.EnumerateFiles(Path.Combine(root, "invalid"), "*.json", SearchOption.TopDirectoryOnly).Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/')).Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void CanonicalBytes_RejectNonCanonicalInputAndAcceptGoldenBytes()
    {
        var root = Path.Combine(FindRepositoryRoot(), "tests", "fixtures", "audit-result", "v1");
        var canonical = File.ReadAllBytes(Path.Combine(root, "golden", "required-present.canonical.json"));
        Assert.True(IsCanonicalBytes(canonical));
        var nonCanonical = new[]
        {
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(canonical).Replace("{\"auditResultVersion\"", "{ \"auditResultVersion\"", StringComparison.Ordinal)),
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(canonical).TrimEnd('\n') + "\r\n"),
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(canonical).Replace("\"auditResultVersion\":1,\"policyConfigurationVersion\":1", "\"policyConfigurationVersion\":1,\"auditResultVersion\":1", StringComparison.Ordinal)),
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(canonical).Replace("\"evidenceIds\":[\"evidence.xml-doc\"]", "\"evidenceIds\":[\"evidence.xml-doc\"]", StringComparison.Ordinal).Replace("\"/// <summary>Widget docs.</summary>\"", "\"\\/\\/\\/ <summary>Widget docs.</summary>\"", StringComparison.Ordinal))
        };

        foreach (var payload in nonCanonical) Assert.False(IsCanonicalBytes(payload));
    }

    [Fact]
    public void RegistryAndFixtures_AreClosedAndEachReasonIsExecutable()
    {
        var root = FindRepositoryRoot();
        using var registry = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "schemas", "audit-result", "v1.registry.json")));
        var ids = registry.RootElement.GetProperty("sections").EnumerateObject().SelectMany(section => section.Value.EnumerateArray().Where(entry => entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("id", out _))).Select(entry => entry.GetProperty("id").GetString()!).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        var schemaText = File.ReadAllText(Path.Combine(root, "schemas", "audit-result", "v1.schema.json"));
        foreach (var id in ids.Where(id => id.StartsWith("audit.", StringComparison.Ordinal))) Assert.Contains(id, schemaText, StringComparison.Ordinal);
        var reasonEntries = registry.RootElement.GetProperty("sections").GetProperty("reasons").EnumerateArray().ToArray();
        Assert.All(reasonEntries, entry =>
        {
            Assert.True(entry.TryGetProperty("legal", out var legal));
            Assert.True(legal.TryGetProperty("policyResolution", out _));
            Assert.True(legal.TryGetProperty("documentationObservation", out _));
            Assert.True(legal.TryGetProperty("bundleStatus", out _));
        });
        var reasons = reasonEntries.Select(entry => entry.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal);
        var fixtureReasons = Directory.EnumerateFiles(Path.Combine(root, "tests", "fixtures", "audit-result", "v1", "payloads"), "*.json").SelectMany(path => JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("results").EnumerateArray()).Select(result => result.GetProperty("reasonCode").GetString()!).ToHashSet(StringComparer.Ordinal);
        Assert.True(reasons.IsSubsetOf(fixtureReasons), "Every audit reason needs a checked-in valid fixture.");
    }

    [Fact]
    public void EvidenceBudgets_EnforceM03Boundaries()
    {
        using var thirtyTwo = BuildEvidenceBoundaryDocument(32, 1);
        Assert.True(IsSemanticallyValid(thirtyTwo.RootElement));
        using var thirtyThree = BuildEvidenceBoundaryDocument(33, 1);
        Assert.False(AuditSchema.Value.Evaluate(thirtyThree.RootElement).IsValid);
        using var itemLimit = BuildEvidenceBoundaryDocument(1, 4096);
        Assert.True(IsSemanticallyValid(itemLimit.RootElement));
        using var itemOverflow = BuildEvidenceBoundaryDocument(1, 4097);
        Assert.False(IsSemanticallyValid(itemOverflow.RootElement));
        using var bundleLimit = BuildEvidenceBoundaryDocument(8, 4096);
        Assert.True(IsSemanticallyValid(bundleLimit.RootElement));
        using var bundleOverflow = BuildEvidenceBoundaryDocument(8, 4096);
        var overflowResult = (JsonObject)bundleOverflow.RootElement.Deserialize<JsonObject>()!;
        var overflowItems = (JsonArray)((JsonObject)((JsonArray)overflowResult["results"]!)[0]!)["evidenceBundle"]!["items"]!;
        var extra = (JsonObject)overflowItems[0]!.DeepClone();
        extra["evidenceId"] = "evidence.overflow";
        extra["excerpt"] = "a";
        extra["sha256"] = "2d711642b726b04401627ca9fbac32f5c8530fb1903cc4db02258717921a4881";
        extra["originalUtf8ByteCount"] = 1;
        extra["includedUtf8ByteCount"] = 1;
        extra["omittedUtf8ByteCount"] = 0;
        overflowItems.Add(extra);
        ((JsonArray)((JsonObject)((JsonArray)overflowResult["results"]!)[0]!)["evidenceIds"]!).Add("evidence.overflow");
        using var overflowDocument = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(overflowResult));
        Assert.False(IsSemanticallyValid(overflowDocument.RootElement));
    }

    [Fact]
    public void EvidenceBindingAndMetadata_FailClosedOnHashCountAndSubjectChanges()
    {
        var root = FindRepositoryRoot();
        var payload = File.ReadAllText(Path.Combine(root, "tests", "fixtures", "audit-result", "v1", "payloads", "required-present.json"));
        var mutations = new[]
        {
            payload.Replace("7245070ca7cb1427ad4e7c502148e744d7f50f07ba86091b570795d5c1f7537f", "0000000000000000000000000000000000000000000000000000000000000000", StringComparison.Ordinal),
            payload.Replace("\"includedUtf8ByteCount\": 35", "\"includedUtf8ByteCount\": 34", StringComparison.Ordinal),
            payload.Replace("\"documentationCommentId\": \"T:AuditFixtures.Widget\" }, \"kind\":", "\"documentationCommentId\": \"T:AuditFixtures.Other\" }, \"kind\":", StringComparison.Ordinal)
        };

        foreach (var mutation in mutations)
        {
            using var document = JsonDocument.Parse(mutation);
            Assert.False(IsSemanticallyValid(document.RootElement));
        }
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
            ValidateEvidence(result, result.GetProperty("classification"));
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
        else
        {
            Assert.True(classification.TryGetProperty("skipReason", out var skipReason));
            Assert.StartsWith(supportStatus switch
            {
                "support.unsupported" => "skip.unsupported.",
                "support.ambiguous" => "skip.ambiguous.",
                "support.not-applicable" => "skip.not-applicable.",
                "support.unavailable-context" => "skip.unavailable.",
                _ => "skip.invalid."
            }, skipReason.GetString());
        }
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
            Assert.True(IsRepositoryRelativePath(project));
            Assert.True(IsRepositoryRelativePath(source));
            var key = project + "\u001f" + source;
            Assert.True(keys.Add(key), "Duplicate policy contribution pair.");
            Assert.True(previousKey is null || string.CompareOrdinal(previousKey, key) < 0, "Policy contributions are not sorted.");
            previousKey = key;
            expectations.Add(contribution.GetProperty("policyExpectation").GetString()!);
            if (contribution.GetProperty("matchedRuleId").ValueKind is not (JsonValueKind.Null or JsonValueKind.String)) throw new InvalidOperationException("Invalid matchedRuleId.");
            if (contribution.GetProperty("matchedRuleId").ValueKind == JsonValueKind.String) Assert.Matches("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", contribution.GetProperty("matchedRuleId").GetString()!);
        }

        var resolution = result.GetProperty("policyResolution").GetString();
        var expectation = result.GetProperty("policyExpectation");
        var classificationSupported = result.GetProperty("classification").GetProperty("supportStatus").GetString() == "support.supported";
        if (!classificationSupported) Assert.Equal("unavailable", resolution);
        else if (contributions.Length == 0) Assert.Equal("unavailable", resolution);
        else if (expectations.Count > 1) Assert.Equal("conflict", resolution);
        else if (contributions.Length == 1) Assert.Equal("single", resolution);
        else Assert.Equal("all-declarations-agree", resolution);
        if (resolution is "conflict" or "unavailable") Assert.Equal(JsonValueKind.Null, expectation.ValueKind);
        else Assert.Equal(expectations.Single(), expectation.GetString());
    }

    private static void ValidateEvidence(JsonElement result, JsonElement classification)
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
        Assert.True(items.Length <= 32);
        Assert.True(items.Sum(item => item.GetProperty("includedUtf8ByteCount").GetInt32()) <= 32768);
        foreach (var item in items)
        {
            var excerpt = item.GetProperty("excerpt").GetString()!;
            var included = item.GetProperty("includedUtf8ByteCount").GetInt32();
            var original = item.GetProperty("originalUtf8ByteCount").GetInt32();
            var omitted = item.GetProperty("omittedUtf8ByteCount").GetInt32();
            Assert.Equal(Encoding.UTF8.GetByteCount(excerpt), included);
            Assert.Equal(included + omitted, original);
            Assert.True(included <= 4096);
            Assert.Equal(omitted > 0, item.GetProperty("isTruncated").GetBoolean());
            if (!item.GetProperty("isTruncated").GetBoolean()) Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(excerpt))).ToLowerInvariant(), item.GetProperty("sha256").GetString());
        }
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

        var recordType = classification.GetProperty("recordType").GetString();
        if (recordType == "UnresolvedClassification") Assert.Empty(referenced);
        if (result.GetProperty("auditOutcome").GetString() is "audit.outcome.compliant" or "audit.outcome.violation")
        {
            Assert.Equal("evidence.bundle.complete", status);
            var expectedSubject = recordType == "ComponentClassification" ? classification.GetProperty("parentSymbolRef") : classification.GetProperty("symbolRef");
            var relevant = items.Where(item => referenced.Contains(item.GetProperty("evidenceId").GetString()!, StringComparer.Ordinal) && JsonEquals(item.GetProperty("subject"), expectedSubject)).ToArray();
            if (result.GetProperty("documentationObservation").GetString() == "documentation.present") Assert.Contains(relevant, item => item.GetProperty("kind").GetString() == "evidence.source.xml-documentation" && item.GetProperty("relation").GetString() == "evidence.documents");
            else
            {
                Assert.Contains(relevant, item => item.GetProperty("kind").GetString() == "evidence.source.declaration" && item.GetProperty("relation").GetString() == "evidence.declares");
                Assert.DoesNotContain(relevant, item => item.GetProperty("kind").GetString() == "evidence.source.xml-documentation" && item.GetProperty("relation").GetString() == "evidence.documents");
            }
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
            Assert.Equal("unavailable", result.GetProperty("policyResolution").GetString());
            Assert.Empty(result.GetProperty("evidenceIds").EnumerateArray());
            AssertUnavailableBundle(result, "evidence.omission.not-provided");
            return;
        }
        if (reason == "audit.reason.policy-conflict")
        {
            Assert.Equal("audit.outcome.skipped", outcome);
            Assert.NotEmpty(result.GetProperty("policyContributions").EnumerateArray());
            Assert.Equal("conflict", result.GetProperty("policyResolution").GetString());
            Assert.Equal(JsonValueKind.Null, result.GetProperty("policyExpectation").ValueKind);
            Assert.Equal(JsonValueKind.Null, result.GetProperty("documentationObservation").ValueKind);
            Assert.Empty(result.GetProperty("evidenceIds").EnumerateArray());
            AssertUnavailableBundle(result, "evidence.omission.not-provided");
            return;
        }
        if (reason == "audit.reason.policy-unavailable")
        {
            Assert.Equal("audit.outcome.skipped", outcome);
            Assert.Empty(result.GetProperty("policyContributions").EnumerateArray());
            Assert.Equal("unavailable", result.GetProperty("policyResolution").GetString());
            Assert.Equal(JsonValueKind.Null, result.GetProperty("policyExpectation").ValueKind);
            Assert.Equal(JsonValueKind.Null, result.GetProperty("documentationObservation").ValueKind);
            Assert.Empty(result.GetProperty("evidenceIds").EnumerateArray());
            AssertUnavailableBundle(result, "evidence.omission.not-provided");
            return;
        }
        if (reason == "audit.reason.documentation-unavailable")
        {
            Assert.Equal("audit.outcome.skipped", outcome);
            Assert.NotEqual("conflict", result.GetProperty("policyResolution").GetString());
            Assert.NotEqual("unavailable", result.GetProperty("policyResolution").GetString());
            Assert.NotEqual(JsonValueKind.Null, result.GetProperty("policyExpectation").ValueKind);
            Assert.Equal("documentation.unavailable", observation);
            Assert.Empty(result.GetProperty("evidenceIds").EnumerateArray());
            AssertUnavailableBundle(result, "evidence.omission.source-unavailable");
            return;
        }
        if (reason == "audit.reason.evidence-incomplete")
        {
            Assert.Equal("audit.outcome.skipped", outcome);
            Assert.NotEqual("conflict", result.GetProperty("policyResolution").GetString());
            Assert.NotEqual("unavailable", result.GetProperty("policyResolution").GetString());
            Assert.NotEqual(JsonValueKind.Null, result.GetProperty("policyExpectation").ValueKind);
            Assert.Equal("documentation.unavailable", observation);
            Assert.Empty(result.GetProperty("evidenceIds").EnumerateArray());
            Assert.Equal("evidence.bundle.partial", result.GetProperty("evidenceBundle").GetProperty("availabilityStatus").GetString());
            Assert.Equal("evidence.omission.budget-exhausted", result.GetProperty("evidenceBundle").GetProperty("omissionReason").GetString());
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

    private static void AssertUnavailableBundle(JsonElement result, string omissionReason)
    {
        var bundle = result.GetProperty("evidenceBundle");
        Assert.Equal("evidence.bundle.unavailable", bundle.GetProperty("availabilityStatus").GetString());
        Assert.Equal(omissionReason, bundle.GetProperty("omissionReason").GetString());
        Assert.Empty(bundle.GetProperty("items").EnumerateArray());
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
            "results" => items.OrderBy(item => ClassificationOrder(item.GetProperty("classification"))).ThenBy(item => GetSubjectKey(item.GetProperty("classification")), StringComparer.Ordinal),
            "policyContributions" => items.OrderBy(item => item.GetProperty("projectPath").GetString(), StringComparer.Ordinal).ThenBy(item => item.GetProperty("sourcePath").GetString(), StringComparer.Ordinal),
            "evidenceIds" => items.OrderBy(item => item.GetString(), StringComparer.Ordinal),
            "items" => items.OrderBy(item => item.GetProperty("evidenceId").GetString(), StringComparer.Ordinal),
            "traits" => items.OrderBy(item => item.GetString(), StringComparer.Ordinal),
            _ => items
        };
    }

    private static int ClassificationOrder(JsonElement classification)
    {
        return classification.GetProperty("recordType").GetString() switch
        {
            "TargetClassification" => 0,
            "ComponentClassification" => 1,
            "UnresolvedClassification" => 2,
            _ => throw new InvalidOperationException("Unknown classification order.")
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
                    if (character < 0x20) builder.Append("\\u").Append(((int)character).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    else builder.Append(character);
                    break;
            }
        }
        return builder.Append('"').ToString();
    }

    private static bool IsCanonicalBytes(byte[] payload)
    {
        try
        {
            using var document = ParseStrict(payload);
            ValidateDocument(document.RootElement);
            return payload.SequenceEqual(Canonicalize(document.RootElement));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsRepositoryRelativePath(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Contains('\0') || value.StartsWith('/') || value.StartsWith('\\') || value.Contains('\\') || System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Za-z]:")) return false;
        return value.Split('/').Length > 0 && value.Split('/').All(segment => segment.Length > 0 && segment != "." && segment != "..");
    }

    private static bool JsonEquals(JsonElement left, JsonElement right)
    {
        return left.ValueKind == JsonValueKind.Object && right.ValueKind == JsonValueKind.Object
            && left.GetProperty("compilationContextRef").GetString() == right.GetProperty("compilationContextRef").GetString()
            && left.GetProperty("documentationCommentId").GetString() == right.GetProperty("documentationCommentId").GetString();
    }

    private static JsonDocument BuildEvidenceBoundaryDocument(int itemCount, int excerptLength)
    {
        var fixturePath = Path.Combine(FindRepositoryRoot(), "tests", "fixtures", "audit-result", "v1", "payloads", "required-present.json");
        var root = JsonNode.Parse(File.ReadAllText(fixturePath))!.AsObject();
        var result = root["results"]!.AsArray()[0]!.AsObject();
        var bundle = result["evidenceBundle"]!.AsObject();
        var template = bundle["items"]!.AsArray()[0]!.AsObject();
        var items = bundle["items"]!.AsArray();
        items.Clear();
        var references = result["evidenceIds"]!.AsArray();
        references.Clear();
        var excerpt = new string('a', excerptLength);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(excerpt))).ToLowerInvariant();
        for (var index = 0; index < itemCount; index++)
        {
            var item = (JsonObject)template.DeepClone();
            var id = $"evidence.item{index:00}";
            item["evidenceId"] = id;
            item["excerpt"] = excerpt;
            item["sha256"] = hash;
            item["originalUtf8ByteCount"] = excerptLength;
            item["includedUtf8ByteCount"] = excerptLength;
            item["omittedUtf8ByteCount"] = 0;
            items.Add(item);
            references.Add(id);
        }

        return JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(root));
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
