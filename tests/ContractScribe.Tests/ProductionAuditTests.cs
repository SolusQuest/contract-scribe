using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Core;

namespace ContractScribe.Tests;

public sealed class ProductionAuditTests
{
    [Fact]
    public void PublicValidCorpus_RoundTripsThroughProductionCore()
    {
        var root = FixtureRoot();
        using var manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Join(root, "cases.json")));
        foreach (var entry in manifest.RootElement.GetProperty("cases").EnumerateArray())
        {
            var caseId = entry.GetProperty("caseId").GetString()!;
            var path = SafeFixturePath(root, entry.GetProperty("payloadFile").GetString()!);
            using var logical = AuditResultConformance.ParseStrict(File.ReadAllBytes(path));
            AuditResultConformance.ValidateDocument(logical.RootElement);
            var canonical = AuditResultConformance.Canonicalize(logical.RootElement);

            var intrinsic = AuditParser.Parse(canonical);
            var validated = AuditParser.Promote(
                intrinsic,
                new Dictionary<AuditEvidenceKey, string>());

            Assert.Equal(
                logical.RootElement.GetProperty("results").GetArrayLength(),
                validated.ResultCount);
            Assert.Equal(canonical, AuditJson.Write(validated));
            Assert.Equal(
                caseId == "empty-results" ? 0 : logical.RootElement.GetProperty("results").GetArrayLength(),
                intrinsic.ResultCount);
        }
    }

    [Fact]
    public void PublicInvalidCorpus_FailsClosedInProductionCore()
    {
        var root = FixtureRoot();
        using var manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Join(root, "invalid-cases.json")));
        foreach (var payload in manifest.RootElement
                     .GetProperty("cases")
                     .EnumerateArray()
                     .Select(entry => entry.GetProperty("payloadFile").GetString()!)
                     .Select(relative => File.ReadAllBytes(SafeFixturePath(root, relative))))
        {
            Assert.Throws<AuditValidationException>(
                () => AuditParser.Parse(payload));
        }
    }

    [Fact]
    public void Parser_RequiresCanonicalBytesAndBothSupportedProfiles()
    {
        var canonical = File.ReadAllBytes(
            Path.Join(FixtureRoot(), "golden", "required-present.canonical.json"));
        Assert.Equal(TargetProfile.ExternalApi, AuditParser.Parse(canonical).TargetProfile);

        var pretty = File.ReadAllBytes(
            Path.Join(FixtureRoot(), "payloads", "required-present.json"));
        var failure = Assert.Throws<AuditValidationException>(
            () => AuditParser.Parse(pretty));
        Assert.Equal(AuditValidationCode.NonCanonicalBytes, failure.Code);

        var assemblyVisible = JsonNode.Parse(
            File.ReadAllText(Path.Join(FixtureRoot(), "payloads", "empty-results.json")))!
            .AsObject();
        assemblyVisible["targetProfile"] = "profile.assembly-visible";
        using var logical = JsonDocument.Parse(
            JsonSerializer.SerializeToUtf8Bytes(assemblyVisible));
        var bytes = AuditResultConformance.Canonicalize(logical.RootElement);
        Assert.Equal(
            TargetProfile.AssemblyVisible,
            AuditParser.Parse(bytes).TargetProfile);
    }

    [Fact]
    public void IntrinsicTruncatedEvidence_CannotAcquireSerializationAuthority()
    {
        var payload = JsonNode.Parse(File.ReadAllText(
            Path.Join(FixtureRoot(), "payloads", "evidence-incomplete.json")))!
            .AsObject();
        var item = payload["results"]![0]!["evidenceBundle"]!["items"]![0]!.AsObject();
        item["excerpt"] = "x";
        item["sha256"] = Sha256("xy");
        item["originalUtf8ByteCount"] = 2;
        item["includedUtf8ByteCount"] = 1;
        item["omittedUtf8ByteCount"] = 1;
        item["isTruncated"] = true;
        item["locator"]!["repository"]!["span"] = new JsonObject
        {
            ["start"] = 100,
            ["end"] = 102,
        };
        using var logical = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(payload));
        var canonical = AuditResultConformance.Canonicalize(logical.RootElement);
        var intrinsic = AuditParser.Parse(canonical);

        var missing = Assert.Throws<AuditValidationException>(() =>
            AuditParser.Promote(
                intrinsic,
                new Dictionary<AuditEvidenceKey, string>()));
        Assert.Equal(AuditValidationCode.MissingOriginalEvidence, missing.Code);

        var wrong = Assert.Throws<AuditValidationException>(() =>
            AuditParser.Promote(
                intrinsic,
                new Dictionary<AuditEvidenceKey, string>
                {
                    [new(0, "evidence.partial")] = "xz",
                }));
        Assert.Equal(AuditValidationCode.OriginalEvidenceMismatch, wrong.Code);

        var validated = AuditParser.Promote(
            intrinsic,
            new Dictionary<AuditEvidenceKey, string>
            {
                [new(0, "evidence.partial")] = "xy",
            });
        Assert.Equal(canonical, AuditJson.Write(validated));

        Assert.DoesNotContain(
            typeof(AuditJson).GetMethods(BindingFlags.Public | BindingFlags.Static),
            method => method.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(IntrinsicAuditDocument)));
        Assert.Empty(typeof(AuditDocument).GetConstructors());
    }

    [Fact]
    public void EvidenceSpans_AreAbsoluteAndMatchCompleteRegionLength()
    {
        foreach (var payload in new[] { "required-present.json", "optional-absent.json" }.Select(LoadPayload))
        {
            var item = payload["results"]![0]!["evidenceBundle"]!["items"]![0]!.AsObject();
            var excerpt = item["excerpt"]!.GetValue<string>();
            item["locator"]!["repository"]!["span"] = new JsonObject
            {
                ["start"] = 100,
                ["end"] = 100 + excerpt.Length,
            };
            AssertRoundTrips(payload);

            item["locator"]!["repository"]!["span"]!["end"] = 101 + excerpt.Length;
            var mismatch = Assert.Throws<AuditValidationException>(() =>
                AuditParser.Parse(Canonicalize(payload)));
            Assert.Equal(AuditValidationCode.OriginalEvidenceMismatch, mismatch.Code);
        }

        var generated = LoadPayload("required-present.json");
        var generatedItem = generated["results"]![0]!["evidenceBundle"]!["items"]![0]!.AsObject();
        var generatedText = generatedItem["excerpt"]!.GetValue<string>();
        generatedItem["locator"] = new JsonObject
        {
            ["generatedOutput"] = new JsonObject
            {
                ["producerKind"] = "source-generator",
                ["producerId"] = "sgp." + new string('a', 64),
                ["outputId"] = "sgo." + new string('b', 64),
                ["sourceSha256"] = Sha256(generatedText),
                ["span"] = new JsonObject
                {
                    ["start"] = 250,
                    ["end"] = 250 + generatedText.Length,
                },
            },
        };
        AssertRoundTrips(generated);
    }

    [Fact]
    public void EmbeddedClassifications_UseAcceptedTaxonomySemantics()
    {
        var mutations = new (string Payload, Action<JsonObject> Mutate)[]
        {
            ("required-present.json", payload =>
            {
                var classification = payload["results"]![0]!["classification"]!.AsObject();
                classification["primaryKind"] = "symbol.unknown";
            }),
            ("classification-skipped.json", payload =>
            {
                var classification = payload["results"]![0]!["classification"]!.AsObject();
                classification["primaryKind"] = "symbol.type.class";
            }),
            ("classification-skipped.json", payload =>
            {
                var classification = payload["results"]![0]!["classification"]!.AsObject();
                classification["skipReason"] = "skip.unsupported.component-kind";
            }),
            ("required-present.json", payload =>
            {
                var classification = payload["results"]![0]!["classification"]!.AsObject();
                classification["origin"] = "origin.compiler-synthesized";
            }),
        };

        foreach (var mutation in mutations)
        {
            var payload = LoadPayload(mutation.Payload);
            mutation.Mutate(payload);
            var failure = Assert.Throws<AuditValidationException>(() =>
                AuditParser.Parse(Canonicalize(payload)));
            Assert.Equal(AuditValidationCode.InvalidClassification, failure.Code);
        }

        var incompatibleComponent = LoadPayload("classification-not-applicable.json");
        var component = incompatibleComponent["results"]![0]!["classification"]!.AsObject();
        component["parentSymbolRef"]!["documentationCommentId"] =
            "M:AuditFixtures.Widget.Run";
        component["componentKind"] = "component.synthesized.delegate-invoke";
        component["identity"] = "synthesized/delegate-invoke";
        var incompatible = Assert.Throws<AuditValidationException>(() =>
            AuditParser.Parse(Canonicalize(incompatibleComponent)));
        Assert.Equal(AuditValidationCode.InvalidClassification, incompatible.Code);

        var unsupportedParent = LoadPayload("classification-skipped.json");
        var componentPayload = LoadPayload("classification-not-applicable.json");
        unsupportedParent["results"]![0]!["classification"]!["symbolRef"] =
            componentPayload["results"]![0]!["classification"]!["parentSymbolRef"]!
                .DeepClone();
        unsupportedParent["results"]!.AsArray().Add(
            componentPayload["results"]![0]!.DeepClone());
        var parentFailure = Assert.Throws<AuditValidationException>(() =>
            AuditParser.Parse(Canonicalize(unsupportedParent)));
        Assert.Equal(AuditValidationCode.InvalidClassification, parentFailure.Code);
    }

    [Fact]
    public void GeneratedAndEvidenceIdentifiers_EnforceExactSchemaBounds()
    {
        foreach (var invalidId in new[]
        {
            "sgp.x",
            "sgp." + new string('a', 63),
            "sgp." + new string('a', 65),
            "sgp." + new string('A', 64),
        })
        {
            var payload = LoadPayload("generated-policy-contribution.json");
            payload["results"]![0]!["policyContributions"]![0]!["generatedOutput"]!["producerId"] = invalidId;
            Assert.Throws<AuditValidationException>(() =>
                AuditParser.Parse(Canonicalize(payload)));
        }

        var valid128 = LoadPayload("evidence-incomplete.json");
        valid128["results"]![0]!["evidenceBundle"]!["items"]![0]!["evidenceId"] =
            "e." + new string('a', 126);
        AssertRoundTrips(valid128);

        var invalid129 = LoadPayload("evidence-incomplete.json");
        invalid129["results"]![0]!["evidenceBundle"]!["items"]![0]!["evidenceId"] =
            "e." + new string('a', 127);
        var failure = Assert.Throws<AuditValidationException>(() =>
            AuditParser.Parse(Canonicalize(invalid129)));
        Assert.Equal(AuditValidationCode.InvalidEvidence, failure.Code);

        var invalidReference = LoadPayload("required-present.json");
        invalidReference["results"]![0]!["evidenceIds"]![0] =
            "e." + new string('a', 127);
        var referenceFailure = Assert.Throws<AuditValidationException>(() =>
            AuditParser.Parse(Canonicalize(invalidReference)));
        Assert.Equal(AuditValidationCode.InvalidEvidence, referenceFailure.Code);
    }

    [Fact]
    public void Promotion_RejectsInvalidUnicodeInOmittedOriginalEvidence()
    {
        foreach (var suffix in new[] { "\ud800", "\udc00" })
        {
            var original = "x" + suffix;
            var payload = LoadPayload("evidence-incomplete.json");
            var item = payload["results"]![0]!["evidenceBundle"]!["items"]![0]!.AsObject();
            item["excerpt"] = "x";
            item["sha256"] = Sha256(original);
            item["originalUtf8ByteCount"] = Encoding.UTF8.GetByteCount(original);
            item["includedUtf8ByteCount"] = 1;
            item["omittedUtf8ByteCount"] = Encoding.UTF8.GetByteCount(original) - 1;
            item["isTruncated"] = true;
            item["locator"]!["repository"]!["span"] = new JsonObject
            {
                ["start"] = 100,
                ["end"] = 100 + original.Length,
            };
            var intrinsic = AuditParser.Parse(Canonicalize(payload));

            var failure = Assert.Throws<AuditValidationException>(() =>
                AuditParser.Promote(
                    intrinsic,
                    new Dictionary<AuditEvidenceKey, string>
                    {
                        [new(0, "evidence.partial")] = original,
                    }));
            Assert.Equal(AuditValidationCode.InvalidUtf8OrJson, failure.Code);
        }
    }

    [Fact]
    public void Promotion_AcceptsValidSupplementaryScalarAfterExcerptBoundary()
    {
        const string original = "x\U0001f600";
        var payload = LoadPayload("evidence-incomplete.json");
        var item = payload["results"]![0]!["evidenceBundle"]!["items"]![0]!.AsObject();
        item["excerpt"] = "x";
        item["sha256"] = Sha256(original);
        item["originalUtf8ByteCount"] = Encoding.UTF8.GetByteCount(original);
        item["includedUtf8ByteCount"] = 1;
        item["omittedUtf8ByteCount"] = Encoding.UTF8.GetByteCount(original) - 1;
        item["isTruncated"] = true;
        item["locator"]!["repository"]!["span"] = new JsonObject
        {
            ["start"] = 100,
            ["end"] = 100 + original.Length,
        };
        var canonical = Canonicalize(payload);

        var document = AuditParser.Promote(
            AuditParser.Parse(canonical),
            new Dictionary<AuditEvidenceKey, string>
            {
                [new(0, "evidence.partial")] = original,
            });
        Assert.Equal(canonical, AuditJson.Write(document));
    }

    [Fact]
    public void CanonicalWriter_PreservesDistinctUnicodeScalarSequences()
    {
        var payload = JsonNode.Parse(File.ReadAllText(
            Path.Join(FixtureRoot(), "payloads", "required-present.json")))!
            .AsObject();
        const string text = "\u00e9 e\u0301";
        var item = payload["results"]![0]!["evidenceBundle"]!["items"]![0]!.AsObject();
        item["excerpt"] = text;
        item["sha256"] = Sha256(text);
        item["originalUtf8ByteCount"] = Encoding.UTF8.GetByteCount(text);
        item["includedUtf8ByteCount"] = Encoding.UTF8.GetByteCount(text);
        using var logical = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(payload));
        var canonical = AuditResultConformance.Canonicalize(logical.RootElement);

        var validated = AuditParser.Promote(
            AuditParser.Parse(canonical),
            new Dictionary<AuditEvidenceKey, string>());
        var roundTrip = Encoding.UTF8.GetString(AuditJson.Write(validated));

        Assert.Contains("\u00e9", roundTrip, StringComparison.Ordinal);
        Assert.Contains("e\u0301", roundTrip, StringComparison.Ordinal);
    }

    [Fact]
    public void CanonicalByteMutations_FailClosedWithStableProductionFailures()
    {
        var canonical = File.ReadAllBytes(
            Path.Join(FixtureRoot(), "golden", "required-present.canonical.json"));
        var text = Encoding.UTF8.GetString(canonical);
        var mutations = new List<byte[]>
        {
            new byte[] { 0xef, 0xbb, 0xbf }.Concat(canonical).ToArray(),
            Encoding.UTF8.GetBytes(text.TrimEnd('\n')),
            Encoding.UTF8.GetBytes(text.TrimEnd('\n') + "\r\n"),
            Encoding.UTF8.GetBytes(text + "\n"),
            Encoding.UTF8.GetBytes(text.Replace(
                "{\"auditResultVersion\"",
                "{ \"auditResultVersion\"",
                StringComparison.Ordinal)),
            Encoding.UTF8.GetBytes(text.Replace(
                "\"auditResultVersion\":1",
                "\"auditResultVersion\":1.0",
                StringComparison.Ordinal)),
            Encoding.UTF8.GetBytes(text.Replace(
                "\"auditResultVersion\":1,\"policyConfigurationVersion\":1",
                "\"policyConfigurationVersion\":1,\"auditResultVersion\":1",
                StringComparison.Ordinal)),
            Encoding.UTF8.GetBytes(text.Replace(
                "/// <summary>",
                "\\/\\/\\/ <summary>",
                StringComparison.Ordinal)),
            new byte[] { 0x7b, 0x22, 0x78, 0x22, 0x3a, 0x22, 0xff, 0x22, 0x7d, 0x0a },
        };

        foreach (var mutation in mutations)
        {
            var failure = Assert.Throws<AuditValidationException>(() =>
                AuditParser.Parse(mutation));
            Assert.True(Enum.IsDefined(failure.Code));
        }
    }

    [Fact]
    public void PublicAuditVocabulary_EqualsFrozenRegistry()
    {
        using var registry = JsonDocument.Parse(File.ReadAllText(Path.Join(
            AuditResultConformance.FindRepositoryRoot(),
            "schemas",
            "audit-result",
            "v1.registry.json")));
        var sections = registry.RootElement.GetProperty("sections");

        Assert.Equal(
            sections.GetProperty("outcomes").EnumerateArray()
                .Select(item => item.GetProperty("id").GetString()!)
                .ToHashSet(StringComparer.Ordinal),
            Enum.GetValues<AuditOutcome>()
                .Select(AuditVocabulary.GetId)
                .ToHashSet(StringComparer.Ordinal));
        Assert.Equal(
            sections.GetProperty("policyResolutions").EnumerateArray()
                .Select(item => item.GetProperty("id").GetString()!)
                .ToHashSet(StringComparer.Ordinal),
            Enum.GetValues<AuditPolicyResolution>()
                .Select(AuditVocabulary.GetId)
                .ToHashSet(StringComparer.Ordinal));
        Assert.Equal(
            sections.GetProperty("reasons").EnumerateArray()
                .Select(item => item.GetProperty("id").GetString()!)
                .ToHashSet(StringComparer.Ordinal),
            Enum.GetValues<AuditReason>()
                .Select(AuditVocabulary.GetId)
                .ToHashSet(StringComparer.Ordinal));
    }

    private static string FixtureRoot() => Path.Join(
        AuditResultConformance.FindRepositoryRoot(),
        "tests",
        "fixtures",
        "audit-result",
        "v1");

    private static JsonObject LoadPayload(string name) => JsonNode.Parse(File.ReadAllText(
        Path.Join(FixtureRoot(), "payloads", name)))!.AsObject();

    private static byte[] Canonicalize(JsonObject payload)
    {
        using var logical = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(payload));
        return AuditResultConformance.Canonicalize(logical.RootElement);
    }

    private static void AssertRoundTrips(JsonObject payload)
    {
        var canonical = Canonicalize(payload);
        var document = AuditParser.Promote(
            AuditParser.Parse(canonical),
            new Dictionary<AuditEvidenceKey, string>());
        Assert.Equal(canonical, AuditJson.Write(document));
    }


    private static string SafeFixturePath(string root, string relativePath)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Join(fullRoot, normalized));
        Assert.StartsWith(
            fullRoot + Path.DirectorySeparatorChar,
            path,
            StringComparison.OrdinalIgnoreCase);
        return path;
    }

    private static string Sha256(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
