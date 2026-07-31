using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Core;

namespace ContractScribe.Tests;

public sealed class ProductionAuditResultTests
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

            var intrinsic = AuditResultParser.Parse(canonical);
            var validated = AuditResultParser.Promote(
                intrinsic,
                new Dictionary<AuditEvidenceKey, string>());

            Assert.Equal(
                logical.RootElement.GetProperty("results").GetArrayLength(),
                validated.ResultCount);
            Assert.Equal(canonical, AuditResultJson.Write(validated));
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
        foreach (var entry in manifest.RootElement.GetProperty("cases").EnumerateArray())
        {
            var relative = entry.GetProperty("payloadFile").GetString()!;
            var payload = File.ReadAllBytes(SafeFixturePath(root, relative));
            Assert.Throws<AuditResultValidationException>(
                () => AuditResultParser.Parse(payload));
        }
    }

    [Fact]
    public void Parser_RequiresCanonicalBytesAndBothSupportedProfiles()
    {
        var canonical = File.ReadAllBytes(
            Path.Join(FixtureRoot(), "golden", "required-present.canonical.json"));
        Assert.Equal(TargetProfile.ExternalApi, AuditResultParser.Parse(canonical).TargetProfile);

        var pretty = File.ReadAllBytes(
            Path.Join(FixtureRoot(), "payloads", "required-present.json"));
        var failure = Assert.Throws<AuditResultValidationException>(
            () => AuditResultParser.Parse(pretty));
        Assert.Equal(AuditResultValidationCode.NonCanonicalBytes, failure.Code);

        var assemblyVisible = JsonNode.Parse(
            File.ReadAllText(Path.Join(FixtureRoot(), "payloads", "empty-results.json")))!
            .AsObject();
        assemblyVisible["targetProfile"] = "profile.assembly-visible";
        using var logical = JsonDocument.Parse(
            JsonSerializer.SerializeToUtf8Bytes(assemblyVisible));
        var bytes = AuditResultConformance.Canonicalize(logical.RootElement);
        Assert.Equal(
            TargetProfile.AssemblyVisible,
            AuditResultParser.Parse(bytes).TargetProfile);
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
        using var logical = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(payload));
        var canonical = AuditResultConformance.Canonicalize(logical.RootElement);
        var intrinsic = AuditResultParser.Parse(canonical);

        var missing = Assert.Throws<AuditResultValidationException>(() =>
            AuditResultParser.Promote(
                intrinsic,
                new Dictionary<AuditEvidenceKey, string>()));
        Assert.Equal(AuditResultValidationCode.MissingOriginalEvidence, missing.Code);

        var wrong = Assert.Throws<AuditResultValidationException>(() =>
            AuditResultParser.Promote(
                intrinsic,
                new Dictionary<AuditEvidenceKey, string>
                {
                    [new(0, "evidence.partial")] = "xz",
                }));
        Assert.Equal(AuditResultValidationCode.OriginalEvidenceMismatch, wrong.Code);

        var validated = AuditResultParser.Promote(
            intrinsic,
            new Dictionary<AuditEvidenceKey, string>
            {
                [new(0, "evidence.partial")] = "xy",
            });
        Assert.Equal(canonical, AuditResultJson.Write(validated));

        Assert.DoesNotContain(
            typeof(AuditResultJson).GetMethods(BindingFlags.Public | BindingFlags.Static),
            method => method.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(IntrinsicAuditResultDocument)));
        Assert.Empty(typeof(AuditResultDocument).GetConstructors());
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

        var validated = AuditResultParser.Promote(
            AuditResultParser.Parse(canonical),
            new Dictionary<AuditEvidenceKey, string>());
        var roundTrip = Encoding.UTF8.GetString(AuditResultJson.Write(validated));

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
            var failure = Assert.Throws<AuditResultValidationException>(() =>
                AuditResultParser.Parse(mutation));
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
                .Select(AuditResultVocabulary.GetId)
                .ToHashSet(StringComparer.Ordinal));
        Assert.Equal(
            sections.GetProperty("policyResolutions").EnumerateArray()
                .Select(item => item.GetProperty("id").GetString()!)
                .ToHashSet(StringComparer.Ordinal),
            Enum.GetValues<AuditPolicyResolution>()
                .Select(AuditResultVocabulary.GetId)
                .ToHashSet(StringComparer.Ordinal));
        Assert.Equal(
            sections.GetProperty("reasons").EnumerateArray()
                .Select(item => item.GetProperty("id").GetString()!)
                .ToHashSet(StringComparer.Ordinal),
            Enum.GetValues<AuditReason>()
                .Select(AuditResultVocabulary.GetId)
                .ToHashSet(StringComparer.Ordinal));
    }

    private static string FixtureRoot() => Path.Join(
        AuditResultConformance.FindRepositoryRoot(),
        "tests",
        "fixtures",
        "audit-result",
        "v1");

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
