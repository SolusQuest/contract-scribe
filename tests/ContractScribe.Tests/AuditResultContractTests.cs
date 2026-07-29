using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.ContractBaselineProbe;

namespace ContractScribe.Tests;

using static ContractScribe.Tests.AuditResultConformance;

public sealed class AuditResultContractTests
{
    [Fact]
    public void ClassificationOriginSkipMatrix_IsConsumedAsFullAuditDocuments()
    {
        using var matrix = ParseStrict(File.ReadAllBytes(Path.Join(
            FindRepositoryRoot(),
            "tests",
            "fixtures",
            "m1-contract-baseline",
            "v1",
            "classification-origin-skip-vectors.json")));
        var rows = matrix.RootElement.GetProperty("cases").EnumerateArray()
            .ToDictionary(
                row => row.GetProperty("caseId").GetString()!,
                row => row,
                StringComparer.Ordinal);
        foreach (var (caseId, row) in rows)
        {
            using var document = BuildClassificationMatrixDocument(
                row.GetProperty("record"));
            var recordAccepted =
                AuditSchema.Value.Evaluate(document.RootElement).IsValid
                && IsSemanticallyValid(document.RootElement);
            var selectionAccepted = AuditConditionsSelectRecord(row);
            Assert.True(
                recordAccepted
                    == (row.GetProperty("recordOutcome").GetString() == "accept"),
                caseId);
            Assert.True(
                selectionAccepted
                    == (row.GetProperty("selectionOutcome").GetString() == "accept"),
                caseId);
            Assert.True(
                (recordAccepted && selectionAccepted)
                    == (row.GetProperty("outcome").GetString() == "accept"),
                caseId);
        }

        foreach (var caseId in RepresentativeClassificationRejections)
        {
            using var document = BuildClassificationMatrixDocument(
                rows[caseId].GetProperty("record"));
            Assert.False(
                AuditSchema.Value.Evaluate(document.RootElement).IsValid
                    && IsSemanticallyValid(document.RootElement),
                caseId);
        }

        var corrected = JsonNode.Parse(File.ReadAllText(
            FixturePath("payloads", "unresolved-classification.json")))!
            .AsObject();
        using (var document = JsonDocument.Parse(
            JsonSerializer.SerializeToUtf8Bytes(corrected)))
        {
            ValidateDocument(document.RootElement);
        }

        corrected["results"]![0]!["classification"]!["origin"] =
            "origin.unknown";
        using var mutation = JsonDocument.Parse(
            JsonSerializer.SerializeToUtf8Bytes(corrected));
        Assert.False(IsSemanticallyValid(mutation.RootElement));
    }

    [Fact]
    public void PublicFixtures_CoverMatrixAndPassSchemaAndSemanticOracle()
    {
        var root = FixtureRoot();
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Join(root, "cases.json")));
        var caseIds = new HashSet<string>(StringComparer.Ordinal);
        var referencedPayloads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fixture in manifest.RootElement.GetProperty("cases").EnumerateArray())
        {
            var caseId = fixture.GetProperty("caseId").GetString()!;
            Assert.True(caseIds.Add(caseId));
            var payloadFile = fixture.GetProperty("payloadFile").GetString()!;
            Assert.True(referencedPayloads.Add(payloadFile));
            var payload = File.ReadAllBytes(SafeFixturePath(root, payloadFile));
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

        Assert.Equal(20, caseIds.Count);
        Assert.Equal(referencedPayloads.Order(StringComparer.OrdinalIgnoreCase), Directory.EnumerateFiles(Path.Join(root, "payloads"), "*.json", SearchOption.TopDirectoryOnly).Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/')).Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void CanonicalSerialization_SortsLogicalInputAndProducesStableUtf8Lf()
    {
        var root = FixturePath("payloads");
        var original = File.ReadAllBytes(Path.Join(root, "required-present.json"));
        using var document = ParseStrict(original);
        var first = Canonicalize(document.RootElement);
        var shuffled = $"{{\"results\":{document.RootElement.GetProperty("results").GetRawText()},\"targetProfile\":\"profile.external-api\",\"taxonomyRegistryVersion\":1,\"auditResultVersion\":1,\"policyConfigurationVersion\":1}}";
        using var shuffledDocument = ParseStrict(Encoding.UTF8.GetBytes(shuffled));
        var second = Canonicalize(document.RootElement);
        Assert.Equal(first, second);
        Assert.Equal(first, Canonicalize(shuffledDocument.RootElement));
        Assert.Equal(File.ReadAllBytes(Path.GetFullPath(Path.Join(root, "..", "golden", "required-present.canonical.json"))), first);
        Assert.Equal((byte)'\n', first[^1]);
        Assert.NotEqual((byte)0xEF, first[0]);
        Assert.DoesNotContain((byte)'\r', first);
    }

    [Fact]
    public void CanonicalSerialization_OrdersRepositoryBeforeGeneratedPolicyContributions()
    {
        var payload = JsonNode.Parse(File.ReadAllText(FixturePath("payloads", "required-present.json")))!.AsObject();
        var result = payload["results"]!.AsArray()[0]!.AsObject();
        var repository = result["policyContributions"]!.AsArray()[0]!.DeepClone();
        repository!["projectPath"] = "z/Z.csproj";
        var generated = new JsonObject
        {
            ["projectPath"] = "a/A.csproj",
            ["generatedOutput"] = new JsonObject
            {
                ["producerKind"] = "tool-generated",
                ["producerId"] = "tgp." + new string('1', 64),
                ["outputId"] = "tgo." + new string('2', 64)
            },
            ["policyExpectation"] = "required",
            ["matchedRuleId"] = "generated-required"
        };
        result["policyContributions"] = new JsonArray(generated, repository);
        result["policyResolution"] = "all-declarations-agree";

        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(payload));
        var canonical = Canonicalize(document.RootElement);
        using var canonicalDocument = ParseStrict(canonical);
        var contributions = canonicalDocument.RootElement.GetProperty("results")[0].GetProperty("policyContributions").EnumerateArray().ToArray();
        Assert.True(contributions[0].TryGetProperty("sourcePath", out _));
        Assert.True(contributions[1].TryGetProperty("generatedOutput", out _));
        Assert.True(IsCanonicalBytes(canonical));
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
        var combined = JsonNode.Parse(File.ReadAllText(FixturePath("payloads", "locator-variants.json")))!.AsObject();
        var component = JsonNode.Parse(File.ReadAllText(FixturePath("payloads", "optional-absent.json")))!.AsObject();
        var results = combined["results"]!.AsArray();
        results.Add(component["results"]!.AsArray()[0]!.DeepClone());
        results.Reverse();
        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(combined));
        var text = Encoding.UTF8.GetString(Canonicalize(document.RootElement));
        Assert.True(text.IndexOf("\"recordType\":\"TargetClassification\"", StringComparison.Ordinal) < text.IndexOf("\"recordType\":\"ComponentClassification\"", StringComparison.Ordinal));
        Assert.True(text.IndexOf("\"recordType\":\"ComponentClassification\"", StringComparison.Ordinal) < text.IndexOf("\"recordType\":\"UnresolvedClassification\"", StringComparison.Ordinal));
        var componentStart = text.IndexOf("\"recordType\":\"ComponentClassification\"", StringComparison.Ordinal);
        var componentEnd = text.IndexOf("\"recordType\":\"UnresolvedClassification\"", StringComparison.Ordinal);
        var componentSlice = text[componentStart..componentEnd];
        Assert.True(componentSlice.IndexOf("\"parentSymbolRef\"", StringComparison.Ordinal) < componentSlice.IndexOf("\"origin\"", StringComparison.Ordinal));
        var unresolvedSlice = text[componentEnd..];
        Assert.True(unresolvedSlice.IndexOf("\"compilationContextRef\"", StringComparison.Ordinal) < unresolvedSlice.IndexOf("\"origin\"", StringComparison.Ordinal));
    }

    [Fact]
    public void CanonicalSerialization_OrdersAllUnresolvedLocatorVariants()
    {
        var payload = JsonNode.Parse(File.ReadAllText(FixturePath("payloads", "locator-variants.json")))!.AsObject();
        payload["results"]!.AsArray().Reverse();
        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(payload));
        var canonical = Canonicalize(document.RootElement);
        using var canonicalDocument = ParseStrict(canonical);
        var variants = canonicalDocument.RootElement.GetProperty("results")
            .EnumerateArray()
            .Where(result => result.GetProperty("classification").GetProperty("recordType").GetString() == "UnresolvedClassification")
            .Select(result => result.GetProperty("classification").GetProperty("candidateLocator").EnumerateObject().Single().Name)
            .ToArray();

        Assert.Equal(new[] { "repository", "generatedSource", "toolGenerated", "synthetic" }, variants);
    }

    [Fact]
    public void ReplayLogicalInputs_PassFullOracleAndCanonicalizeIdentically()
    {
        var path = Path.Join(FindRepositoryRoot(), "tests", "fixtures", "m1-contract-baseline", "v1", "process-replay-input.json");
        using var replay = ParseStrict(File.ReadAllBytes(path));
        var canonical = new List<byte[]>();
        foreach (var logicalInput in replay.RootElement.GetProperty("logicalInputs").EnumerateArray())
        {
            Assert.True(AuditSchema.Value.Evaluate(logicalInput).IsValid);
            ValidateDocument(logicalInput);
            canonical.Add(Canonicalize(logicalInput));
        }

        Assert.True(canonical.Count >= 2);
        Assert.All(canonical.Skip(1), candidate => Assert.Equal(canonical[0], candidate));
    }

    [Fact]
    public void InvalidVectors_FailClosed()
    {
        var valid = JsonNode.Parse(File.ReadAllText(FixturePath("payloads", "required-present.json")))!.AsObject();
        var invalid = new List<JsonObject>();
        JsonObject Mutate(Action<JsonObject> mutation)
        {
            var value = (JsonObject)valid.DeepClone();
            mutation(value);
            invalid.Add(value);
            return value;
        }
        Mutate(value => value["taxonomyRegistryVersion"] = 2);
        Mutate(value => value["results"]![0]!["classification"]!["recordType"] = "RelationObservation");
        Mutate(value => value["results"]![0]!["auditOutcome"] = "audit.outcome.violation");
        Mutate(value => value["results"]![0]!["evidenceIds"]!.AsArray().Clear());
        Mutate(value =>
        {
            var contribution = value["results"]![0]!["policyContributions"]![0]!.AsObject();
            contribution["matchedRuleId"] = null;
            contribution["unexpected"] = true;
        });

        foreach (var (payload, index) in invalid.Select((payload, index) => (payload, index)))
        {
            using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(payload));
            Assert.False(AuditSchema.Value.Evaluate(document.RootElement).IsValid && IsSemanticallyValid(document.RootElement), $"Invalid vector {index} was accepted.");
        }

        Assert.Throws<FormatException>(() => ParseStrict(Encoding.UTF8.GetBytes("\uFEFF{}")));
    }

    [Fact]
    public void CheckedInInvalidCorpus_FailsClosed()
    {
        var root = FixtureRoot();
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Join(root, "invalid-cases.json")));
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.RootElement.GetProperty("cases").EnumerateArray())
        {
            var relative = entry.GetProperty("payloadFile").GetString()!;
            Assert.True(listed.Add(relative));
            var payload = File.ReadAllBytes(SafeFixturePath(root, relative));
            if (entry.GetProperty("canonical").GetBoolean())
            {
                Assert.False(IsCanonicalBytes(payload), relative);
                continue;
            }

            var rejected = false;
            try
            {
                using var document = ParseStrict(payload);
                rejected = !AuditSchema.Value.Evaluate(document.RootElement).IsValid || !IsSemanticallyValid(document.RootElement);
            }
            catch (FormatException)
            {
                rejected = true;
            }
            Assert.True(rejected, relative);
        }

        Assert.Equal(listed.Order(StringComparer.OrdinalIgnoreCase), Directory.EnumerateFiles(Path.Join(root, "invalid"), "*.json", SearchOption.TopDirectoryOnly).Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/')).Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void CanonicalBytes_RejectNonCanonicalInputAndAcceptGoldenBytes()
    {
        var root = FixtureRoot();
        var canonical = File.ReadAllBytes(Path.Join(root, "golden", "required-present.canonical.json"));
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
        using var registry = JsonDocument.Parse(File.ReadAllText(Path.Join(root, "schemas", "audit-result", "v1.registry.json")));
        var ids = registry.RootElement.GetProperty("sections").EnumerateObject().SelectMany(section => section.Value.EnumerateArray().Where(entry => entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("id", out _))).Select(entry => entry.GetProperty("id").GetString()!).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        var schemaText = File.ReadAllText(Path.Join(root, "schemas", "audit-result", "v1.schema.json"));
        foreach (var id in ids.Where(id => id.StartsWith("audit.", StringComparison.Ordinal))) Assert.Contains(id, schemaText, StringComparison.Ordinal);
        var reasonEntries = registry.RootElement.GetProperty("sections").GetProperty("reasons").EnumerateArray().ToArray();
        Assert.All(reasonEntries, entry =>
        {
            Assert.True(entry.TryGetProperty("legal", out var legal));
            Assert.True(legal.TryGetProperty("policyResolution", out _));
            Assert.True(legal.TryGetProperty("policyExpectation", out _));
            Assert.True(legal.TryGetProperty("documentationObservation", out _));
            Assert.True(legal.TryGetProperty("bundleStatus", out _));
            Assert.True(legal.TryGetProperty("contributionCount", out _));
        });
        var reasons = reasonEntries.Select(entry => entry.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal);
        var fixtureReasons = Directory.EnumerateFiles(Path.Join(root, "tests", "fixtures", "audit-result", "v1", "payloads"), "*.json").SelectMany(path => JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("results").EnumerateArray()).Select(result => result.GetProperty("reasonCode").GetString()!).ToHashSet(StringComparer.Ordinal);
        using var authorityFixtures = JsonDocument.Parse(File.ReadAllText(Path.Join(root, "tests", "fixtures", "m1-contract-baseline", "v1", "audit-authority-cases.json")));
        fixtureReasons.UnionWith(authorityFixtures.RootElement.GetProperty("valid").EnumerateArray().Select(result => result.GetProperty("reasonCode").GetString()!));
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
        var payload = JsonNode.Parse(File.ReadAllText(FixturePath("payloads", "required-present.json")))!.AsObject();
        var mutations = new List<JsonObject>();
        JsonObject Mutate(Action<JsonObject> mutation)
        {
            var value = (JsonObject)payload.DeepClone();
            mutation(value);
            mutations.Add(value);
            return value;
        }
        Mutate(value => value["results"]![0]!["evidenceBundle"]!["items"]![0]!["sha256"] = new string('0', 64));
        Mutate(value => value["results"]![0]!["evidenceBundle"]!["items"]![0]!["includedUtf8ByteCount"] = 34);
        Mutate(value => value["results"]![0]!["evidenceBundle"]!["items"]![0]!["subject"]!["documentationCommentId"] = "T:AuditFixtures.Other");

        foreach (var document in mutations.Select(mutation => JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(mutation))))
        {
            using (document)
            {
                Assert.False(IsSemanticallyValid(document.RootElement));
            }
        }

        var wrongM03 = File.ReadAllText(FixturePath("payloads", "classification-not-applicable.json")).Replace("skip.not-applicable.synthesized-non-target", "skip.not-applicable.non-documentation-component", StringComparison.Ordinal);
        using var wrongM03Document = JsonDocument.Parse(wrongM03);
        Assert.False(IsSemanticallyValid(wrongM03Document.RootElement));
        var invalidLocator = (JsonObject)payload.DeepClone();
        invalidLocator["results"]![0]!["evidenceBundle"]!["items"]![0]!["locator"]!["repository"]!["path"] = "../outside.cs";
        using var invalidLocatorDocument = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(invalidLocator));
        Assert.False(IsSemanticallyValid(invalidLocatorDocument.RootElement));

        var nonCanonicalPolicy = File.ReadAllText(FixturePath("payloads", "required-present.json")).Replace("\"src/Audit.csproj\"", "\"./src//Audit.csproj\"", StringComparison.Ordinal);
        using var nonCanonicalPolicyDocument = JsonDocument.Parse(nonCanonicalPolicy);
        Assert.False(IsSemanticallyValid(nonCanonicalPolicyDocument.RootElement));

        var supportedUnknown = File.ReadAllText(FixturePath("payloads", "required-present.json")).Replace("\"origin\": \"origin.source\"", "\"origin\": \"origin.unknown\"", StringComparison.Ordinal);
        using var supportedUnknownDocument = JsonDocument.Parse(supportedUnknown);
        Assert.False(IsSemanticallyValid(supportedUnknownDocument.RootElement));
        var supportedMixed = supportedUnknown.Replace("origin.unknown", "origin.mixed", StringComparison.Ordinal);
        using var supportedMixedDocument = JsonDocument.Parse(supportedMixed);
        Assert.False(IsSemanticallyValid(supportedMixedDocument.RootElement));
        var wrongMixedOrigin = File.ReadAllText(FixturePath("payloads", "classification-ambiguous.json")).Replace("\"origin\": \"origin.mixed\"", "\"origin\": \"origin.source\"", StringComparison.Ordinal);
        using var wrongMixedOriginDocument = JsonDocument.Parse(wrongMixedOrigin);
        Assert.False(IsSemanticallyValid(wrongMixedOriginDocument.RootElement));

        var absent = JsonNode.Parse(File.ReadAllText(FixturePath("payloads", "required-absent.json")))!.AsObject();
        var xmlEvidence = (JsonObject)absent["results"]![0]!["evidenceBundle"]!["items"]![0]!.DeepClone();
        xmlEvidence["evidenceId"] = "evidence.xml-doc";
        xmlEvidence["kind"] = "evidence.source.xml-documentation";
        xmlEvidence["relation"] = "evidence.references";
        absent["results"]![0]!["evidenceBundle"]!["items"]!.AsArray().Add(xmlEvidence);
        using var contradictoryDocumentation = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(absent));
        Assert.False(IsSemanticallyValid(contradictoryDocumentation.RootElement));

        using var overflowCounts = BuildEvidenceBoundaryDocument(2, 1);
        var overflowPayload = (JsonObject)overflowCounts.RootElement.Deserialize<JsonObject>()!;
        var overflowResult = ((JsonArray)overflowPayload["results"]!)[0]!.AsObject();
        foreach (var evidenceItem in overflowResult["evidenceBundle"]!["items"]!.AsArray().OfType<JsonObject>())
        {
            evidenceItem["includedUtf8ByteCount"] = int.MaxValue;
            evidenceItem["originalUtf8ByteCount"] = int.MaxValue;
            evidenceItem["omittedUtf8ByteCount"] = 0;
        }
        using var overflowDocument = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(overflowPayload));
        Assert.False(IsSemanticallyValid(overflowDocument.RootElement));
    }

    [Fact]
    public void EvidenceBinding_RejectsCrossSubjectReferencesAndOutOfRangeSpans()
    {
        var payload = JsonNode.Parse(File.ReadAllText(FixturePath("payloads", "required-present.json")))!.AsObject();
        var result = payload["results"]!.AsArray()[0]!.AsObject();
        var item = (JsonObject)result["evidenceBundle"]!["items"]!.AsArray()[0]!.DeepClone();
        item["evidenceId"] = "evidence.xml-doc.z";
        item["subject"]!["documentationCommentId"] = "T:AuditFixtures.Other";
        result["evidenceBundle"]!["items"]!.AsArray().Add(item);
        result["evidenceIds"]!.AsArray().Add("evidence.xml-doc.z");
        using var crossSubject = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(payload));
        Assert.False(IsSemanticallyValid(crossSubject.RootElement));

        var spanPayload = JsonNode.Parse(File.ReadAllText(FixturePath("payloads", "required-present.json")))!.AsObject();
        var span = new JsonObject { ["start"] = 0, ["end"] = 1000 };
        spanPayload["results"]![0]!["evidenceBundle"]!["items"]![0]!["locator"]!["repository"]!["span"] = span;
        using var outOfRangeSpan = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(spanPayload));
        Assert.False(IsSemanticallyValid(outOfRangeSpan.RootElement));
    }

    [Fact]
    public void TruncatedEvidence_RequiresOriginalTextForHashValidation()
    {
        var payload = JsonNode.Parse(File.ReadAllText(FixturePath("payloads", "evidence-incomplete.json")))!.AsObject();
        var item = payload["results"]![0]!["evidenceBundle"]!["items"]![0]!.AsObject();
        item["excerpt"] = "x";
        item["sha256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("xy"))).ToLowerInvariant();
        item["originalUtf8ByteCount"] = 2;
        item["includedUtf8ByteCount"] = 1;
        item["omittedUtf8ByteCount"] = 1;
        item["isTruncated"] = true;
        using var truncated = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(payload));
        Assert.False(IsSemanticallyValid(truncated.RootElement));
        var originals = new Dictionary<(int ResultIndex, string EvidenceId), string> { [(0, "evidence.partial")] = "xy" };
        Assert.True(IsSemanticallyValid(truncated.RootElement, originals));
        var canonical = Canonicalize(truncated.RootElement);
        Assert.False(IsCanonicalBytes(canonical));
        Assert.True(IsCanonicalBytes(canonical, originals));

        item["locator"]!["repository"]!["span"] = new JsonObject { ["start"] = 0, ["end"] = 3 };
        using var outOfRangeTruncatedSpan = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(payload));
        Assert.False(IsSemanticallyValid(outOfRangeTruncatedSpan.RootElement, originals));
    }

    [Fact]
    public void TruncatedEvidence_OriginalTextIsScopedToEachResultBundle()
    {
        var payload = JsonNode.Parse(File.ReadAllText(FixturePath("payloads", "evidence-incomplete.json")))!.AsObject();
        var firstResult = payload["results"]![0]!.AsObject();
        firstResult["evidenceBundle"]!["items"]![0]!["sha256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("xy"))).ToLowerInvariant();
        firstResult["evidenceBundle"]!["items"]![0]!["originalUtf8ByteCount"] = 2;
        firstResult["evidenceBundle"]!["items"]![0]!["includedUtf8ByteCount"] = 1;
        firstResult["evidenceBundle"]!["items"]![0]!["omittedUtf8ByteCount"] = 1;
        firstResult["evidenceBundle"]!["items"]![0]!["isTruncated"] = true;
        var secondResult = (JsonObject)firstResult.DeepClone();
        secondResult["classification"]!["symbolRef"]!["compilationContextRef"] = "synthetic.second";
        secondResult["classification"]!["symbolRef"]!["documentationCommentId"] = "T:AuditFixtures.Second";
        secondResult["evidenceBundle"]!["items"]![0]!["subject"]!["compilationContextRef"] = "synthetic.second";
        secondResult["evidenceBundle"]!["items"]![0]!["subject"]!["documentationCommentId"] = "T:AuditFixtures.Second";
        secondResult["evidenceBundle"]!["items"]![0]!["sha256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("xz"))).ToLowerInvariant();
        payload["results"]!.AsArray().Add(secondResult);
        var originals = new Dictionary<(int ResultIndex, string EvidenceId), string>
        {
            [(0, "evidence.partial")] = "xy",
            [(1, "evidence.partial")] = "xz"
        };
        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(payload));
        Assert.True(IsSemanticallyValid(document.RootElement, originals));
    }

    [Fact]
    public void UnresolvedSubjectKey_IsStructuralAndRejectsNonCanonicalRepositoryPaths()
    {
        using var first = JsonDocument.Parse("{\"repository\":{\"path\":\"src/Missing.cs\",\"span\":{\"start\":0,\"end\":1}}}");
        using var second = JsonDocument.Parse("{\"repository\":{\"span\":{\"end\":1,\"start\":0},\"path\":\"src/Missing.cs\"}}");
        Assert.Equal(CandidateLocatorKey(first.RootElement), CandidateLocatorKey(second.RootElement));
        using var lexical = JsonDocument.Parse("{\"repository\":{\"path\":\"./src//Missing.cs\",\"span\":{\"start\":0,\"end\":1}}}");
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => ValidateCandidateLocator(lexical.RootElement));
    }

    [Fact]
    public void UnresolvedCanonicalOrder_UsesLocatorRankAndNumericSpanOffsets()
    {
        using var repository2 = JsonDocument.Parse("{\"recordType\":\"UnresolvedClassification\",\"compilationContextRef\":\"synthetic.order\",\"candidateLocator\":{\"repository\":{\"path\":\"src/Missing.cs\",\"span\":{\"start\":2,\"end\":3}}}}");
        using var repository10 = JsonDocument.Parse("{\"recordType\":\"UnresolvedClassification\",\"compilationContextRef\":\"synthetic.order\",\"candidateLocator\":{\"repository\":{\"path\":\"src/Missing.cs\",\"span\":{\"start\":10,\"end\":11}}}}");
        using var generated = JsonDocument.Parse("{\"recordType\":\"UnresolvedClassification\",\"compilationContextRef\":\"synthetic.order\",\"candidateLocator\":{\"generatedSource\":{\"generatorId\":\"synthetic.generator\",\"hintNameId\":\"widget.g.cs\"}}}");
        using var synthetic = JsonDocument.Parse("{\"recordType\":\"UnresolvedClassification\",\"compilationContextRef\":\"synthetic.order\",\"candidateLocator\":{\"synthetic\":{\"fixtureId\":\"synthetic-fixture\"}}}");
        using var lexicalZ = JsonDocument.Parse("{\"recordType\":\"UnresolvedClassification\",\"compilationContextRef\":\"synthetic.order\",\"candidateLocator\":{\"repository\":{\"path\":\"z.cs\"}}}");
        using var plainA = JsonDocument.Parse("{\"recordType\":\"UnresolvedClassification\",\"compilationContextRef\":\"synthetic.order\",\"candidateLocator\":{\"repository\":{\"path\":\"a.cs\"}}}");
        Assert.True(GetResultSortKey(repository2.RootElement).CompareTo(GetResultSortKey(repository10.RootElement)) < 0);
        Assert.True(GetResultSortKey(plainA.RootElement).CompareTo(GetResultSortKey(lexicalZ.RootElement)) < 0);
        Assert.True(GetResultSortKey(repository10.RootElement).CompareTo(GetResultSortKey(generated.RootElement)) < 0);
        Assert.True(GetResultSortKey(generated.RootElement).CompareTo(GetResultSortKey(synthetic.RootElement)) < 0);
    }
    private static string FixtureRoot() => FixturePath();

    private static string FixturePath(params string[] segments) => Path.GetFullPath(Path.Join(new[] { FindRepositoryRoot(), "tests", "fixtures", "audit-result", "v1" }.Concat(segments).ToArray()));

    private static string SafeFixturePath(string root, string relativePath)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        Assert.False(Path.IsPathRooted(normalized), $"Fixture path must be relative: {relativePath}");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Join(fullRoot, normalized));
        Assert.True(fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), $"Fixture path escapes its root: {relativePath}");
        return fullPath;
    }

    private static JsonDocument BuildClassificationMatrixDocument(
        JsonElement classification)
    {
        var template = classification.GetProperty("recordType").GetString()
            == "UnresolvedClassification"
            ? "unresolved-classification.json"
            : "classification-skipped.json";
        var document = JsonNode.Parse(File.ReadAllText(
            FixturePath("payloads", template)))!.AsObject();
        document["results"]![0]!["classification"] =
            JsonNode.Parse(classification.GetRawText());
        return JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(document));
    }

    private static bool AuditConditionsSelectRecord(JsonElement row)
    {
        var conditions = row.GetProperty("conditions").EnumerateArray()
            .Select(value => value.GetString()!)
            .ToArray();
        var selectedSkipReason = row.GetProperty("record")
            .GetProperty("skipReason")
            .GetString();
        var expectedSkipReason = conditions.Contains(
            "generated-provenance-unavailable",
            StringComparer.Ordinal)
            ? "skip.unavailable.generated-provenance"
            : conditions.Contains(
                "semantic-context-unavailable",
                StringComparer.Ordinal)
                ? "skip.unavailable.semantic-context"
                : conditions.Contains(
                    "documentation-comment-id-unavailable",
                    StringComparer.Ordinal)
                    ? "skip.unavailable.documentation-comment-id"
                    : null;
        return selectedSkipReason == expectedSkipReason;
    }

    private static readonly string[] RepresentativeClassificationRejections =
    [
        "target.generated-provenance.source-origin.reject",
        "target.semantic-context.unknown-origin.reject",
        "target.generated-provenance.compiler-synthesized-origin.reject",
        "unresolved.documentation-comment-id.unknown-origin.reject",
        "component.semantic-context.component.accessor.get.ineligible.reject"
    ];

    private static JsonDocument BuildEvidenceBoundaryDocument(int itemCount, int excerptLength)
    {
        var fixturePath = FixturePath("payloads", "required-present.json");
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

        var declarations = new JsonArray();
        foreach (var item in items.OfType<JsonObject>())
        {
            var suffix = declarations.Count.ToString("x64", System.Globalization.CultureInfo.InvariantCulture);
            declarations.Add(new JsonObject
            {
                ["declarationId"] = $"decl.{suffix}",
                ["authorityRole"] = "partial-type-part",
                ["blockState"] = "well-formed",
                ["evidenceId"] = item["evidenceId"]!.GetValue<string>()
            });
        }
        var authority = result["evidenceAuthority"]!.AsObject();
        authority["declarations"] = declarations;
        var declarationDigest = AuditResultCanonicalizer.ComputeDeclarationDigest(JsonSerializer.SerializeToElement(declarations));
        authority["declarationSetId"] = $"dset.{declarationDigest}";
        var observation = bundle["observationSubject"]!.AsObject();
        observation["authoritativeDeclarationSetDigest"] = declarationDigest;
        observation["authoritativeDeclarationCount"] = declarations.Count;
        observation["observationSubjectRef"] = AuditResultCanonicalizer.ComputeObservationSubjectRef(JsonSerializer.SerializeToElement(observation));

        return JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(root));
    }
}
