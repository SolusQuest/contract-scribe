using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ContractScribe.ContractBaselineProbe;
using Json.Schema;

namespace ContractScribe.Tests;

/// <summary>
/// Test-only Audit Result conformance oracle shared by the Audit Result contract tests and the M1 audit CLI contract checker.
/// </summary>
internal static class AuditResultConformance
{
    internal static readonly Lazy<JsonSchema> AuditSchema = new(() => JsonSchema.FromText(File.ReadAllText(Path.Join(FindRepositoryRoot(), "schemas", "audit-result", "v1.schema.json"))));
    internal static readonly Lazy<JsonSchema> ClassificationSchema = new(LoadClassificationSchema);
    internal static readonly Lazy<JsonSchema> EvidenceSchema = new(() => JsonSchema.FromText(File.ReadAllText(Path.Join(FindRepositoryRoot(), "schemas", "symbol-evidence-taxonomy", "v1.schema.json")).Replace("  \"$id\": \"https://contract-scribe.dev/schemas/symbol-evidence-taxonomy/v1.schema.json\",\r\n", string.Empty, StringComparison.Ordinal).Replace("  \"$id\": \"https://contract-scribe.dev/schemas/symbol-evidence-taxonomy/v1.schema.json\",\n", string.Empty, StringComparison.Ordinal)));
    internal static readonly Lazy<JsonElement> TaxonomyRegistry = new(() => JsonDocument.Parse(File.ReadAllText(Path.Join(FindRepositoryRoot(), "schemas", "symbol-evidence-taxonomy", "v1.registry.json"))).RootElement.Clone());

    internal static JsonDocument ParseStrict(byte[] payload)
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

    internal static void RejectDuplicateProperties(JsonElement value)
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

    internal static void ValidateDocument(JsonElement document, IReadOnlyDictionary<(int ResultIndex, string EvidenceId), string>? originalEvidenceTexts = null)
    {
        Assert.Equal(1, document.GetProperty("auditResultVersion").GetInt32());
        Assert.Equal(1, document.GetProperty("policyConfigurationVersion").GetInt32());
        Assert.Equal(1, document.GetProperty("taxonomyRegistryVersion").GetInt32());
        Assert.Contains(document.GetProperty("targetProfile").GetString(), new[] { "profile.external-api", "profile.assembly-visible" });
        var subjects = new HashSet<string>(StringComparer.Ordinal);
        var resultIndex = 0;
        foreach (var result in document.GetProperty("results").EnumerateArray())
        {
            ValidateClassification(result.GetProperty("classification"));
            var subjectKey = GetSubjectKey(result.GetProperty("classification"));
            Assert.True(subjects.Add(subjectKey), $"Duplicate result subject: {subjectKey}");
            ValidatePolicy(result);
            ValidateEvidence(result, result.GetProperty("classification"), resultIndex, originalEvidenceTexts);
            ValidateEvidenceAuthority(result);
            ValidateOutcome(result);
            resultIndex++;
        }
    }

    internal static bool IsSemanticallyValid(JsonElement document, IReadOnlyDictionary<(int ResultIndex, string EvidenceId), string>? originalEvidenceTexts = null)
    {
        try
        {
            ValidateDocument(document, originalEvidenceTexts);
            return true;
        }
        catch (Xunit.Sdk.XunitException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (KeyNotFoundException)
        {
            return false;
        }
    }

    internal static void ValidateClassification(JsonElement classification)
    {
        Assert.True(ClassificationSchema.Value.Evaluate(classification).IsValid, classification.GetRawText());
        var recordType = classification.GetProperty("recordType").GetString();
        Assert.Contains(recordType, new[] { "TargetClassification", "ComponentClassification", "UnresolvedClassification" });
        Assert.False(classification.TryGetProperty("recordType", out var _) && recordType == "RelationObservation");
        var supportStatus = classification.GetProperty("supportStatus").GetString();
        ValidateTaxonomyEntry("supportStatuses", supportStatus, recordType);
        ValidateTaxonomyEntry("origins", classification.GetProperty("origin").GetString(), recordType);
        if (recordType == "TargetClassification")
        {
            Assert.True(classification.TryGetProperty("symbolRef", out var symbolRef));
            ValidateSymbolRef(symbolRef);
            Assert.NotNull(classification.GetProperty("primaryKind").GetString());
            Assert.Equal(JsonValueKind.Array, classification.GetProperty("traits").ValueKind);
            ValidateTaxonomyEntry("primaryKinds", classification.GetProperty("primaryKind").GetString(), recordType, supportStatus, classification.GetProperty("origin").GetString(), classification.TryGetProperty("skipReason", out var targetSkip) ? targetSkip.GetString() : null);
        }
        else if (recordType == "ComponentClassification")
        {
            ValidateSymbolRef(classification.GetProperty("parentSymbolRef"));
            Assert.StartsWith("component.", classification.GetProperty("componentKind").GetString());
            Assert.True(IsValidComponentIdentity(classification.GetProperty("componentKind").GetString()!, classification.GetProperty("identity").GetString()));
            ValidateTaxonomyEntry("componentKinds", classification.GetProperty("componentKind").GetString(), recordType, supportStatus, classification.GetProperty("origin").GetString(), classification.TryGetProperty("skipReason", out var componentSkip) ? componentSkip.GetString() : null);
        }
        else
        {
            Assert.Equal("support.unavailable-context", supportStatus);
            Assert.StartsWith("skip.unavailable.", classification.GetProperty("skipReason").GetString());
            Assert.True(classification.TryGetProperty("candidateLocator", out var candidateLocator));
            ValidateCandidateLocator(candidateLocator);
        }

        var origin = classification.GetProperty("origin").GetString();
        var skipReasonValue = classification.TryGetProperty("skipReason", out var skipValue) ? skipValue.GetString() : null;
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
            ValidateTaxonomyEntry("skipReasons", skipReason.GetString(), recordType, supportStatus);
        }
        ValidateOriginSpecificCombination(recordType, supportStatus, origin, skipReasonValue);
    }

    internal static void ValidateOriginSpecificCombination(string? recordType, string? supportStatus, string? origin, string? skipReason)
    {
        if (origin == "origin.unknown")
        {
            Assert.Equal("support.unavailable-context", supportStatus);
            Assert.Contains(skipReason, new[] { "skip.unavailable.generated-provenance", "skip.unavailable.semantic-context" });
        }

        if (origin == "origin.mixed")
        {
            Assert.True(
                supportStatus == "support.ambiguous" && skipReason is "skip.ambiguous.mixed-origin" or "skip.ambiguous.partial-declaration"
                || supportStatus == "support.unavailable-context" && skipReason is "skip.unavailable.generated-provenance" or "skip.unavailable.semantic-context",
                $"Invalid origin.mixed combination for {recordType}.");
        }

        if (skipReason == "skip.ambiguous.mixed-origin") Assert.Equal("origin.mixed", origin);
    }

    internal static void ValidateTaxonomyEntry(string section, string? id, string? recordType, string? supportStatus = null, string? origin = null, string? skipReason = null)
    {
        Assert.False(string.IsNullOrEmpty(id));
        var entry = TaxonomyRegistry.Value.GetProperty("sections").GetProperty(section).EnumerateArray().Single(candidate => candidate.GetProperty("id").GetString() == id);
        Assert.Contains(recordType, entry.GetProperty("recordTypes").EnumerateArray().Select(value => value.GetString()));
        if (supportStatus is not null && entry.TryGetProperty("allowedSupportStatuses", out var statuses)) Assert.Contains(supportStatus, statuses.EnumerateArray().Select(value => value.GetString()));
        if (origin is not null && entry.TryGetProperty("requiredOrigin", out var requiredOrigin)) Assert.Equal(requiredOrigin.GetString(), origin);
        if (skipReason is not null && entry.TryGetProperty("requiredSkip", out var requiredSkip)) Assert.Equal(requiredSkip.GetString(), skipReason);
    }

    internal static void ValidateSymbolRef(JsonElement symbolRef)
    {
        Assert.Matches("^[a-z0-9][a-z0-9._-]{0,127}$", symbolRef.GetProperty("compilationContextRef").GetString()!);
        Assert.False(string.IsNullOrEmpty(symbolRef.GetProperty("documentationCommentId").GetString()));
    }

    internal static void ValidatePolicy(JsonElement result)
    {
        var contributions = result.GetProperty("policyContributions").EnumerateArray().ToArray();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var expectations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var contribution in contributions)
        {
            var project = contribution.GetProperty("projectPath").GetString()!;
            Assert.True(IsCanonicalPolicyPath(project));
            var key = PolicyContributionKey(contribution);
            if (contribution.TryGetProperty("sourcePath", out var source))
            {
                Assert.True(IsCanonicalPolicyPath(source.GetString()!));
            }
            else
            {
                var generated = contribution.GetProperty("generatedOutput");
                var producerKind = generated.GetProperty("producerKind").GetString();
                var expectedPrefixes = producerKind == "source-generator" ? ("sgp.", "sgo.") : ("tgp.", "tgo.");
                Assert.StartsWith(expectedPrefixes.Item1, generated.GetProperty("producerId").GetString(), StringComparison.Ordinal);
                Assert.StartsWith(expectedPrefixes.Item2, generated.GetProperty("outputId").GetString(), StringComparison.Ordinal);
            }
            Assert.True(keys.Add(key), "Duplicate policy contribution key.");
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

    internal static void ValidateEvidence(JsonElement result, JsonElement classification, int resultIndex, IReadOnlyDictionary<(int ResultIndex, string EvidenceId), string>? originalEvidenceTexts = null)
    {
        var bundle = result.GetProperty("evidenceBundle");
        Assert.True(EvidenceSchema.Value.Evaluate(bundle).IsValid);
        var status = bundle.GetProperty("availabilityStatus").GetString();
        var items = bundle.GetProperty("items").EnumerateArray().ToArray();
        var ids = items.Select(item => item.GetProperty("evidenceId").GetString()!).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        var referenced = result.GetProperty("evidenceIds").EnumerateArray().Select(id => id.GetString()!).ToArray();
        Assert.Equal(referenced.Length, referenced.Distinct(StringComparer.Ordinal).Count());
        Assert.All(referenced, id => Assert.Contains(items, item => item.GetProperty("evidenceId").GetString() == id && !item.GetProperty("isTruncated").GetBoolean()));
        Assert.True(items.Length <= 32);
        Assert.True(items.Sum(item => (long)item.GetProperty("includedUtf8ByteCount").GetInt32()) <= 32768);
        foreach (var item in items)
        {
            var excerpt = item.GetProperty("excerpt").GetString()!;
            var included = item.GetProperty("includedUtf8ByteCount").GetInt32();
            var original = item.GetProperty("originalUtf8ByteCount").GetInt32();
            var omitted = item.GetProperty("omittedUtf8ByteCount").GetInt32();
            Assert.Equal(Encoding.UTF8.GetByteCount(excerpt), included);
            Assert.Equal(included + omitted, original);
            Assert.True(included <= 4096);
            var isTruncated = item.GetProperty("isTruncated").GetBoolean();
            Assert.Equal(omitted > 0, isTruncated);
            if (original > 0) Assert.NotEmpty(excerpt);
            var originalText = excerpt;
            if (isTruncated)
            {
                var evidenceId = item.GetProperty("evidenceId").GetString()!;
                Assert.True(originalEvidenceTexts?.TryGetValue((resultIndex, evidenceId), out originalText) == true, "Truncated evidence requires its original source text for hash validation.");
                Assert.StartsWith(excerpt, originalText, StringComparison.Ordinal);
                Assert.False(originalText.Length > excerpt.Length && excerpt.Length > 0 && char.IsHighSurrogate(excerpt[^1]));
            }
            Assert.Equal(original, Encoding.UTF8.GetByteCount(originalText));
            Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(originalText))).ToLowerInvariant(), item.GetProperty("sha256").GetString());
            ValidateEvidenceLocator(item.GetProperty("locator"), excerpt, originalText, isTruncated);
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
            var expectedSubject = GetExpectedEvidenceSubject(classification);
            Assert.All(referenced, id => Assert.True(JsonElement.DeepEquals(items.Single(item => item.GetProperty("evidenceId").GetString() == id).GetProperty("subject"), expectedSubject), $"Evidence {id} is bound to a different subject."));
            var relevant = items.Where(item => referenced.Contains(item.GetProperty("evidenceId").GetString()!, StringComparer.Ordinal) && JsonElement.DeepEquals(item.GetProperty("subject"), expectedSubject)).ToArray();
            if (result.GetProperty("documentationObservation").GetString() == "documentation.present") Assert.Contains(relevant, item => item.GetProperty("kind").GetString() == "evidence.source.xml-documentation" && item.GetProperty("relation").GetString() == "evidence.documents");
            else
            {
                Assert.Contains(relevant, item => item.GetProperty("kind").GetString() == "evidence.source.declaration" && item.GetProperty("relation").GetString() == "evidence.declares");
                var sameSubject = items.Where(item => JsonElement.DeepEquals(item.GetProperty("subject"), expectedSubject)).ToArray();
                Assert.DoesNotContain(sameSubject, item => item.GetProperty("kind").GetString() == "evidence.source.xml-documentation");
            }
        }
    }

    internal static void ValidateEvidenceAuthority(JsonElement result)
    {
        var hasAuthority = result.TryGetProperty("evidenceAuthority", out var authority);
        var bundle = result.GetProperty("evidenceBundle");
        var hasObservationSubject = bundle.TryGetProperty("observationSubject", out var observation);
        var documentationObservation = result.GetProperty("documentationObservation");
        var requiresAuthority =
            documentationObservation.ValueKind == JsonValueKind.String
            && documentationObservation.GetString() is "documentation.present" or "documentation.absent"
            || result.GetProperty("reasonCode").GetString() == "audit.reason.documentation-unavailable.malformed-xml";
        Assert.Equal(requiresAuthority, hasAuthority);
        Assert.Equal(requiresAuthority, hasObservationSubject);
        if (!requiresAuthority) return;

        var declarations = authority.GetProperty("declarations");
        var digest = AuditResultCanonicalizer.ComputeDeclarationDigest(declarations);
        Assert.Equal($"dset.{digest}", authority.GetProperty("declarationSetId").GetString());
        Assert.Equal(digest, observation.GetProperty("authoritativeDeclarationSetDigest").GetString());
        Assert.Equal(declarations.GetArrayLength(), observation.GetProperty("authoritativeDeclarationCount").GetInt32());
        Assert.Equal(GetClassificationContext(result.GetProperty("classification")), observation.GetProperty("compilationContextRef").GetString());
        Assert.True(ObservationSubjectMatchesClassification(observation.GetProperty("subject"), result.GetProperty("classification")));

        Assert.Equal(AuditResultCanonicalizer.ComputeObservationSubjectRef(observation), observation.GetProperty("observationSubjectRef").GetString());

        var classification = result.GetProperty("classification");
        var isComponent = classification.GetProperty("recordType").GetString() == "ComponentClassification";
        var componentKind = isComponent ? classification.GetProperty("componentKind").GetString() : null;
        var declarationIds = new HashSet<string>(StringComparer.Ordinal);
        var evidenceIds = new HashSet<string>(StringComparer.Ordinal);
        var malformedEvidenceIds = new HashSet<string>(StringComparer.Ordinal);
        var evidenceItems = bundle.GetProperty("items").EnumerateArray().ToDictionary(item => item.GetProperty("evidenceId").GetString()!, StringComparer.Ordinal);
        foreach (var declaration in declarations.EnumerateArray())
        {
            var declarationId = declaration.GetProperty("declarationId").GetString()!;
            Assert.True(declarationIds.Add(declarationId));
            var evidenceId = declaration.GetProperty("evidenceId").GetString()!;
            Assert.True(evidenceIds.Add(evidenceId));
            Assert.True(evidenceItems.TryGetValue(evidenceId, out var evidence));
            Assert.True(JsonElement.DeepEquals(evidence.GetProperty("subject"), observation.GetProperty("subject")));
            var hasLocalName = declaration.TryGetProperty("componentLocalName", out _);
            var hasMatch = declaration.TryGetProperty("componentMatch", out var componentMatch);
            var isMalformed = declaration.GetProperty("blockState").GetString() == "malformed";
            if (isMalformed) malformedEvidenceIds.Add(evidenceId);
            if (!isComponent)
            {
                Assert.False(hasLocalName || hasMatch);
            }
            else if (componentKind is "component.parameter" or "component.type-parameter")
            {
                Assert.True(hasLocalName);
                Assert.Equal(!isMalformed, hasMatch);
            }
            else
            {
                Assert.False(hasLocalName);
                Assert.Equal(!isMalformed, hasMatch);
            }
            if (isMalformed) Assert.False(hasMatch);
        }
        var derivedObservation = AuditResultCanonicalizer.DeriveDocumentationObservation(observation.GetProperty("subject"), authority);
        Assert.Equal(result.GetProperty("documentationObservation").GetString(), derivedObservation);
        var malformedReason = result.GetProperty("reasonCode").GetString() == "audit.reason.documentation-unavailable.malformed-xml";
        Assert.Equal(malformedReason, derivedObservation == "documentation.unavailable" && malformedEvidenceIds.Count > 0);
        foreach (var evidenceId in malformedEvidenceIds)
        {
            var evidence = evidenceItems[evidenceId];
            Assert.Equal("evidence.source.xml-documentation", evidence.GetProperty("kind").GetString());
            Assert.Equal("evidence.documents", evidence.GetProperty("relation").GetString());
            Assert.False(evidence.GetProperty("isTruncated").GetBoolean());
            Assert.Contains(evidenceId, result.GetProperty("evidenceIds").EnumerateArray().Select(value => value.GetString()));
        }

        if (result.GetProperty("documentationObservation").GetString() != "documentation.present")
        {
            Assert.Equal("complete", authority.GetProperty("completeness").GetString());
        }
        Assert.Equal(
            evidenceIds.Order(StringComparer.Ordinal),
            result.GetProperty("evidenceIds").EnumerateArray().Select(value => value.GetString()!).Order(StringComparer.Ordinal));
    }

    internal static string GetClassificationContext(JsonElement classification) =>
        classification.GetProperty("recordType").GetString() == "ComponentClassification"
            ? classification.GetProperty("parentSymbolRef").GetProperty("compilationContextRef").GetString()!
            : classification.GetProperty("symbolRef").GetProperty("compilationContextRef").GetString()!;

    internal static bool ObservationSubjectMatchesClassification(JsonElement subject, JsonElement classification)
    {
        if (classification.GetProperty("recordType").GetString() == "TargetClassification")
        {
            return JsonElement.DeepEquals(subject, classification.GetProperty("symbolRef"));
        }

        return subject.GetProperty("componentKind").GetString() == classification.GetProperty("componentKind").GetString()
            && subject.GetProperty("identity").GetString() == classification.GetProperty("identity").GetString()
            && JsonElement.DeepEquals(subject.GetProperty("parentSymbolRef"), classification.GetProperty("parentSymbolRef"));
    }

    internal static JsonElement GetExpectedEvidenceSubject(JsonElement classification)
    {
        if (classification.GetProperty("recordType").GetString() != "ComponentClassification")
        {
            return classification.GetProperty("symbolRef");
        }

        return JsonSerializer.SerializeToElement(new JsonObject
        {
            ["parentSymbolRef"] = JsonNode.Parse(classification.GetProperty("parentSymbolRef").GetRawText()),
            ["componentKind"] = classification.GetProperty("componentKind").GetString(),
            ["identity"] = classification.GetProperty("identity").GetString()
        });
    }

    internal static void ValidateEvidenceLocator(JsonElement locator, string excerpt, string originalText, bool isTruncated)
    {
        var variants = new[] { "repository", "generatedOutput", "metadata", "synthetic" }.Where(name => locator.TryGetProperty(name, out _)).ToArray();
        Assert.Single(variants);
        ValidateTaxonomyEntry("locatorKinds", "evidence.locator." + (variants[0] == "generatedOutput" ? "generated-output" : variants[0]), "EvidenceItem");
        if (variants[0] == "repository")
        {
            var repository = locator.GetProperty("repository");
            Assert.True(IsRepositoryRelativePath(repository.GetProperty("path").GetString()!));
            ValidateSpan(repository, excerpt, originalText, isTruncated);
        }
        else if (variants[0] == "generatedOutput")
        {
            var generated = locator.GetProperty("generatedOutput");
            var producerKind = generated.GetProperty("producerKind").GetString();
            var expectedPrefixes = producerKind == "source-generator" ? ("sgp.", "sgo.") : ("tgp.", "tgo.");
            Assert.Matches($"^{Regex.Escape(expectedPrefixes.Item1)}[0-9a-f]{{64}}$", generated.GetProperty("producerId").GetString()!);
            Assert.Matches($"^{Regex.Escape(expectedPrefixes.Item2)}[0-9a-f]{{64}}$", generated.GetProperty("outputId").GetString()!);
            Assert.Matches("^[0-9a-f]{64}$", generated.GetProperty("sourceSha256").GetString()!);
            ValidateSpan(generated);
        }
        else if (variants[0] == "metadata") Assert.Matches("^[a-z0-9][a-z0-9._-]{0,127}$", locator.GetProperty("metadata").GetProperty("assemblyIdentity").GetString()!);
        else Assert.Matches("^[a-z0-9][a-z0-9._-]{0,127}$", locator.GetProperty("synthetic").GetProperty("fixtureId").GetString()!);
    }

    internal static void ValidateCandidateLocator(JsonElement locator)
    {
        var variants = new[] { "repository", "generatedSource", "toolGenerated", "synthetic" }.Where(name => locator.TryGetProperty(name, out _)).ToArray();
        Assert.Single(variants);
        ValidateTaxonomyEntry("locatorKinds", "evidence.locator." + (variants[0] is "generatedSource" or "toolGenerated" ? "generated-output" : variants[0]), "UnresolvedClassification");
        if (variants[0] == "repository")
        {
            var repository = locator.GetProperty("repository");
            Assert.True(IsRepositoryRelativePath(repository.GetProperty("path").GetString()!));
            ValidateSpan(repository);
        }
        else if (variants[0] == "generatedSource")
        {
            var generated = locator.GetProperty("generatedSource");
            Assert.Matches("^sgp\\.[0-9a-f]{64}$", generated.GetProperty("generatorId").GetString()!);
            Assert.Matches("^sgo\\.[0-9a-f]{64}$", generated.GetProperty("hintNameId").GetString()!);
            ValidateSpan(generated);
        }
        else if (variants[0] == "toolGenerated")
        {
            var generated = locator.GetProperty("toolGenerated");
            Assert.Matches("^tgp\\.[0-9a-f]{64}$", generated.GetProperty("producerId").GetString()!);
            Assert.Matches("^tgo\\.[0-9a-f]{64}$", generated.GetProperty("outputId").GetString()!);
            ValidateSpan(generated);
        }
        else Assert.Matches("^[a-z0-9][a-z0-9._-]{0,127}$", locator.GetProperty("synthetic").GetProperty("fixtureId").GetString()!);
    }

    internal static void ValidateSpan(JsonElement parent, string? excerpt = null, string? originalText = null, bool isTruncated = false)
    {
        if (!parent.TryGetProperty("span", out var span)) return;
        var start = span.GetProperty("start").GetInt32();
        var end = span.GetProperty("end").GetInt32();
        Assert.True(start <= end);
        if (excerpt is not null) Assert.True(end <= (isTruncated ? originalText!.Length : excerpt.Length), "Repository span exceeds evidence text.");
    }

    internal static void ValidateOutcome(JsonElement result)
    {
        var outcome = result.GetProperty("auditOutcome").GetString();
        var reason = result.GetProperty("reasonCode").GetString();
        var expectation = result.GetProperty("policyExpectation").GetString();
        var observation = result.GetProperty("documentationObservation").GetString();
        Assert.Equal(DerivePrimaryReason(result), reason);
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
        if (reason == "audit.reason.documentation-unavailable.malformed-xml")
        {
            Assert.Equal("audit.outcome.skipped", outcome);
            Assert.Equal("documentation.unavailable", observation);
            Assert.NotEmpty(result.GetProperty("evidenceIds").EnumerateArray());
            Assert.Equal("evidence.bundle.complete", result.GetProperty("evidenceBundle").GetProperty("availabilityStatus").GetString());
            Assert.Contains(result.GetProperty("evidenceAuthority").GetProperty("declarations").EnumerateArray(), declaration => declaration.GetProperty("blockState").GetString() == "malformed");
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

    internal static string DerivePrimaryReason(JsonElement result)
    {
        var classification = result.GetProperty("classification");
        if (classification.GetProperty("supportStatus").GetString() != "support.supported") return "audit.reason.classification-skipped";
        var contributions = result.GetProperty("policyContributions").EnumerateArray().ToArray();
        var expectations = contributions.Select(contribution => contribution.GetProperty("policyExpectation").GetString()).Distinct(StringComparer.Ordinal).ToArray();
        if (expectations.Length > 1) return "audit.reason.policy-conflict";
        if (contributions.Length == 0) return "audit.reason.policy-unavailable";
        var observation = result.GetProperty("documentationObservation").GetString();
        var bundleStatus = result.GetProperty("evidenceBundle").GetProperty("availabilityStatus").GetString();
        if (observation == "documentation.unavailable" && bundleStatus == "evidence.bundle.partial") return "audit.reason.evidence-incomplete";
        if (observation == "documentation.unavailable"
            && result.TryGetProperty("evidenceAuthority", out var authority)
            && authority.GetProperty("declarations").EnumerateArray().Any(declaration => declaration.GetProperty("blockState").GetString() == "malformed"))
        {
            return "audit.reason.documentation-unavailable.malformed-xml";
        }
        if (observation == "documentation.unavailable") return "audit.reason.documentation-unavailable";
        if (bundleStatus == "evidence.bundle.partial") return "audit.reason.evidence-incomplete";
        return (expectations.Single(), observation) switch
        {
            ("required", "documentation.present") => "audit.reason.required-present",
            ("required", "documentation.absent") => "audit.reason.required-absent",
            ("optional", "documentation.present") => "audit.reason.optional-present",
            ("optional", "documentation.absent") => "audit.reason.optional-absent",
            ("forbidden", "documentation.present") => "audit.reason.forbidden-present",
            ("forbidden", "documentation.absent") => "audit.reason.forbidden-absent",
            _ => throw new InvalidOperationException("Cannot derive a primary audit reason.")
        };
    }

    internal static void AssertUnavailableBundle(JsonElement result, string omissionReason)
    {
        var bundle = result.GetProperty("evidenceBundle");
        Assert.Equal("evidence.bundle.unavailable", bundle.GetProperty("availabilityStatus").GetString());
        Assert.Equal(omissionReason, bundle.GetProperty("omissionReason").GetString());
        Assert.Empty(bundle.GetProperty("items").EnumerateArray());
    }

    internal static string GetSubjectKey(JsonElement classification)
    {
        return classification.GetProperty("recordType").GetString() switch
        {
            "TargetClassification" => "target|" + classification.GetProperty("symbolRef").GetProperty("compilationContextRef").GetString() + "|" + classification.GetProperty("symbolRef").GetProperty("documentationCommentId").GetString(),
            "ComponentClassification" => "component|" + classification.GetProperty("parentSymbolRef").GetProperty("compilationContextRef").GetString() + "|" + classification.GetProperty("parentSymbolRef").GetProperty("documentationCommentId").GetString() + "|" + classification.GetProperty("componentKind").GetString() + "|" + classification.GetProperty("identity").GetString(),
            "UnresolvedClassification" => "unresolved|" + classification.GetProperty("compilationContextRef").GetString() + "|" + CandidateLocatorKey(classification.GetProperty("candidateLocator")),
            _ => throw new InvalidOperationException("Unknown subject.")
        };
    }

    internal static string CandidateLocatorKey(JsonElement locator)
    {
        if (locator.TryGetProperty("repository", out var repository)) return "repository|" + NormalizeRepositoryPath(repository.GetProperty("path").GetString()!) + "|" + SpanKey(repository);
        if (locator.TryGetProperty("generatedSource", out var generated)) return "generatedSource|" + generated.GetProperty("generatorId").GetString() + "|" + generated.GetProperty("hintNameId").GetString() + "|" + SpanKey(generated);
        if (locator.TryGetProperty("toolGenerated", out var toolGenerated)) return "toolGenerated|" + toolGenerated.GetProperty("producerId").GetString() + "|" + toolGenerated.GetProperty("outputId").GetString() + "|" + SpanKey(toolGenerated);
        return "synthetic|" + locator.GetProperty("synthetic").GetProperty("fixtureId").GetString();
    }

    internal static string SpanKey(JsonElement parent)
    {
        return !parent.TryGetProperty("span", out var span) ? "absent" : "present|" + span.GetProperty("start").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + span.GetProperty("end").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static byte[] Canonicalize(JsonElement root)
    {
        return AuditResultCanonicalizer.Canonicalize(root);
    }

    internal static void WriteValue(Utf8JsonWriter writer, JsonElement value, string? propertyName)
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

    internal static IEnumerable<string> OrderedProperties(IEnumerable<string> names, string? parent)
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
        return names.OrderBy(name => Array.IndexOf(order, name) is var index && index >= 0 ? index : int.MaxValue).ThenBy(name => name, StringComparer.Ordinal);
    }

    internal static IEnumerable<JsonElement> OrderedItems(JsonElement value, string? propertyName)
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

    internal static int ClassificationOrder(JsonElement classification)
    {
        return classification.GetProperty("recordType").GetString() switch
        {
            "TargetClassification" => 0,
            "ComponentClassification" => 1,
            "UnresolvedClassification" => 2,
            _ => throw new InvalidOperationException("Unknown classification order.")
        };
    }

    internal static ResultSortKey GetResultSortKey(JsonElement classification)
    {
        return classification.GetProperty("recordType").GetString() switch
        {
            "TargetClassification" => new ResultSortKey(0, classification.GetProperty("symbolRef").GetProperty("compilationContextRef").GetString()!, 0, classification.GetProperty("symbolRef").GetProperty("documentationCommentId").GetString()!, string.Empty, false, 0, 0),
            "ComponentClassification" => new ResultSortKey(1, classification.GetProperty("parentSymbolRef").GetProperty("compilationContextRef").GetString()!, 0, classification.GetProperty("parentSymbolRef").GetProperty("documentationCommentId").GetString()!, classification.GetProperty("componentKind").GetString()!, false, 0, 0, classification.GetProperty("identity").GetString()!),
            "UnresolvedClassification" => GetUnresolvedSortKey(classification),
            _ => throw new InvalidOperationException("Unknown result type.")
        };
    }

    internal static ResultSortKey GetUnresolvedSortKey(JsonElement classification)
    {
        var locator = classification.GetProperty("candidateLocator");
        if (locator.TryGetProperty("repository", out var repository)) return CreateUnresolvedKey(classification, 0, NormalizeRepositoryPath(repository.GetProperty("path").GetString()!), string.Empty, repository);
        if (locator.TryGetProperty("generatedSource", out var generated)) return CreateUnresolvedKey(classification, 1, generated.GetProperty("generatorId").GetString()!, generated.GetProperty("hintNameId").GetString()!, generated);
        if (locator.TryGetProperty("toolGenerated", out var toolGenerated)) return CreateUnresolvedKey(classification, 2, toolGenerated.GetProperty("producerId").GetString()!, toolGenerated.GetProperty("outputId").GetString()!, toolGenerated);
        return new ResultSortKey(2, classification.GetProperty("compilationContextRef").GetString()!, 3, locator.GetProperty("synthetic").GetProperty("fixtureId").GetString()!, string.Empty, false, 0, 0);
    }

    internal static ResultSortKey CreateUnresolvedKey(JsonElement classification, int rank, string field1, string field2, JsonElement locator)
    {
        var hasSpan = locator.TryGetProperty("span", out var span);
        return new ResultSortKey(2, classification.GetProperty("compilationContextRef").GetString()!, rank, field1, field2, hasSpan, hasSpan ? span.GetProperty("start").GetInt32() : 0, hasSpan ? span.GetProperty("end").GetInt32() : 0);
    }

    internal readonly record struct ResultSortKey(int TypeRank, string Primary, int VariantRank, string Field1, string Field2, bool HasSpan, int Start, int End, string Field3 = "") : IComparable<ResultSortKey>
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

    internal static void RejectUnpairedSurrogates(string value)
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

    internal static string EscapeJsonString(string value)
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

    internal static bool IsCanonicalBytes(byte[] payload, IReadOnlyDictionary<(int ResultIndex, string EvidenceId), string>? originalEvidenceTexts = null)
    {
        try
        {
            using var document = ParseStrict(payload);
            if (!AuditSchema.Value.Evaluate(document.RootElement).IsValid) return false;
            ValidateDocument(document.RootElement, originalEvidenceTexts);
            return payload.SequenceEqual(Canonicalize(document.RootElement));
        }
        catch (Xunit.Sdk.XunitException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (KeyNotFoundException)
        {
            return false;
        }
    }

    internal static bool IsRepositoryRelativePath(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Contains('\0') || value.StartsWith('/') || value.StartsWith('\\') || System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Za-z]:")) return false;
        var normalized = value.Replace('\\', '/').Split('/').Where(segment => segment is not "" and not ".").ToArray();
        return normalized.Length > 0 && normalized.All(segment => segment != "..");
    }

    internal static string NormalizeRepositoryPath(string value) => string.Join('/', value.Replace('\\', '/').Split('/').Where(segment => segment is not "" and not "."));

    internal static bool IsCanonicalPolicyPath(string value) => IsRepositoryRelativePath(value) && value == NormalizeRepositoryPath(value);

    internal static bool IsValidComponentIdentity(string kind, string? identity) => identity is not null && kind switch
    {
        "component.parameter" => System.Text.RegularExpressions.Regex.IsMatch(identity, "^parameter/[0-9]+$"),
        "component.type-parameter" => System.Text.RegularExpressions.Regex.IsMatch(identity, "^type-parameter/[0-9]+$"),
        "component.return" => identity == "return",
        "component.value" => identity == "value",
        "component.accessor.get" => identity == "accessor/get",
        "component.accessor.set" => identity == "accessor/set",
        "component.accessor.init" => identity == "accessor/init",
        "component.accessor.add" => identity == "accessor/add",
        "component.accessor.remove" => identity == "accessor/remove",
        "component.backing-field" => identity == "backing-field",
        "component.synthesized.record-positional-property" => System.Text.RegularExpressions.Regex.IsMatch(identity, "^synthesized/record-positional-property/[0-9]+$"),
        "component.synthesized.implicit-constructor" => identity == "synthesized/implicit-constructor",
        "component.synthesized.record-copy-constructor" => identity == "synthesized/record-copy-constructor",
        "component.synthesized.delegate-invoke" => identity == "synthesized/delegate-invoke",
        "component.synthesized.delegate-begin-invoke" => identity == "synthesized/delegate-begin-invoke",
        "component.synthesized.delegate-end-invoke" => identity == "synthesized/delegate-end-invoke",
        "component.unknown" => System.Text.RegularExpressions.Regex.IsMatch(identity, "^unknown/[0-9]+$"),
        _ => false
    };

    internal static string PolicyContributionKey(JsonElement contribution)
    {
        var project = contribution.GetProperty("projectPath").GetString();
        if (contribution.TryGetProperty("sourcePath", out var source))
        {
            return $"A\0{project}\0{source.GetString()}";
        }

        var generated = contribution.GetProperty("generatedOutput");
        return $"B\0{project}\0{generated.GetProperty("producerKind").GetString()}\0{generated.GetProperty("producerId").GetString()}\0{generated.GetProperty("outputId").GetString()}";
    }

    internal static void ValidateCanonicalInteger(string raw)
    {
        if (raw == "0") return;
        var start = raw[0] == '-' ? 1 : 0;
        if (start == raw.Length || raw[start] == '0' || raw[start..].Any(character => character is < '0' or > '9')) throw new FormatException("Canonical JSON numbers must be signed integers without leading zeroes or negative zero.");
    }

    internal static JsonSchema LoadClassificationSchema()
    {
        var manifestPath = Path.Join(FindRepositoryRoot(), "schemas", "symbol-evidence-taxonomy", "v1.manifest.schema.json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var schema = $"{{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",\"$ref\":\"#/$defs/classificationRecord\",\"$defs\":{manifest.RootElement.GetProperty("$defs").GetRawText()}}}";
        return JsonSchema.FromText(schema);
    }

    internal static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "ContractScribe.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
