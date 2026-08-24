using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContractScribe.Core;
using Json.Schema;

namespace ContractScribe.Tests;

public sealed class CampaignStateContractTests
{
    [Fact]
    public void Canonical_artifact_has_fixed_bytes_lf_and_digest()
    {
        var artifact = CampaignStateJson.CreateArtifact(CreateState());
        var expected = File.ReadAllBytes(FixturePath("empty-terminal.json"));

        Assert.Equal(expected, artifact.ExactUtf8Json.ToArray());
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(expected)).ToLowerInvariant(),
            artifact.Sha256);
        Assert.Equal("b772dabcbcce3435dcd4ff138dcc63e84fb6e19dc87d45e2f987cf0d89ac99ce", artifact.Sha256);
        Assert.Equal((byte)'\n', expected[^1]);
        Assert.NotEqual((byte)'\n', expected[^2]);
    }

    [Fact]
    public void Known_answer_conforms_to_the_published_registry()
    {
        var schema = JsonSchema.FromText(File.ReadAllText(SchemaPath()));
        using var document = JsonDocument.Parse(File.ReadAllBytes(FixturePath("empty-terminal.json")));

        Assert.True(schema.Evaluate(document.RootElement).IsValid);
    }

    [Theory]
    [InlineData("duplicate-root.json", CampaignStateValidationCode.DuplicateProperty)]
    [InlineData("unknown-extension.json", CampaignStateValidationCode.UnknownProperty)]
    public void Raw_invalid_vectors_fail_with_stable_codes(
        string name,
        CampaignStateValidationCode expected)
    {
        var result = CampaignStateJson.Parse(File.ReadAllBytes(FixturePath("invalid", name)));

        Assert.False(result.IsValid);
        Assert.Equal(expected, result.FailureCode);
    }

    [Fact]
    public void Canonical_round_trip_is_exact_and_culture_independent()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            var bytes = new[] { "en-US", "tr-TR", "zh-CN" }
                .Select(WriteUnderCulture)
                .ToArray();

            Assert.All(bytes.Skip(1), value => Assert.Equal(bytes[0], value));
            var parsed = CampaignStateJson.Parse(bytes[0]);
            Assert.True(parsed.IsValid);
            Assert.Equal(bytes[0], parsed.Artifact!.ExactUtf8Json.ToArray());
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Theory]
    [InlineData("whitespace", CampaignStateValidationCode.InvalidCanonicalBytes)]
    [InlineData("duplicate", CampaignStateValidationCode.DuplicateProperty)]
    [InlineData("unknown", CampaignStateValidationCode.UnknownProperty)]
    [InlineData("version", CampaignStateValidationCode.UnsupportedVersion)]
    [InlineData("overflow", CampaignStateValidationCode.InvalidShape)]
    [InlineData("closed-value", CampaignStateValidationCode.InvalidVocabulary)]
    [InlineData("unpaired-surrogate", CampaignStateValidationCode.InvalidShape)]
    public void Noncanonical_or_unrecognized_documents_fail_closed(
        string mutation,
        CampaignStateValidationCode expected)
    {
        var canonical = Encoding.UTF8.GetString(CampaignStateJson.Write(CreateState()));
        var mutated = mutation switch
        {
            "whitespace" => canonical.Replace("{\"campaignStateVersion\"", "{ \"campaignStateVersion\"", StringComparison.Ordinal),
            "duplicate" => canonical.Replace("{\"campaignStateVersion\":1", "{\"campaignStateVersion\":1,\"campaignStateVersion\":1", StringComparison.Ordinal),
            "unknown" => canonical.Replace("{\"campaignStateVersion\":1", "{\"campaignStateVersion\":1,\"unexpected\":null", StringComparison.Ordinal),
            "version" => canonical.Replace("\"campaignStateVersion\":1", "\"campaignStateVersion\":2", StringComparison.Ordinal),
            "overflow" => canonical.Replace("\"checkpointRevision\":0", "\"checkpointRevision\":9223372036854775808", StringComparison.Ordinal),
            "closed-value" => canonical.Replace("\"kind\":\"complete\"", "\"kind\":\"future\"", StringComparison.Ordinal),
            "unpaired-surrogate" => canonical.Replace("\"campaign.test\"", "\"\\uD800\"", StringComparison.Ordinal),
            _ => throw new InvalidOperationException(),
        };

        var parsed = CampaignStateJson.Parse(Encoding.UTF8.GetBytes(mutated));
        Assert.False(parsed.IsValid);
        Assert.Equal(expected, parsed.FailureCode);
    }

    [Fact]
    public void Over_bounded_work_collection_fails_before_item_projection()
    {
        var canonical = Encoding.UTF8.GetString(CampaignStateJson.Write(CreateState()));
        var oversized = canonical.Replace(
            "\"workItems\":[]",
            "\"workItems\":[" + string.Join(',', Enumerable.Repeat("null", CampaignStateContract.MaximumWorkItems + 1)) + "]",
            StringComparison.Ordinal);

        Assert.Equal(
            CampaignStateValidationCode.InvalidBound,
            CampaignStateJson.Parse(Encoding.UTF8.GetBytes(oversized)).FailureCode);
    }

    [Fact]
    public void Byte_transport_rejects_bom_invalid_utf8_and_oversize_before_projection()
    {
        var canonical = CampaignStateJson.Write(CreateState());
        Assert.Equal(
            CampaignStateValidationCode.BomNotAllowed,
            CampaignStateJson.Parse(new byte[] { 0xef, 0xbb, 0xbf }.Concat(canonical).ToArray()).FailureCode);
        Assert.Equal(
            CampaignStateValidationCode.InvalidUtf8,
            CampaignStateJson.Parse(new byte[] { 0x7b, 0x22, 0xc3, 0x28, 0x7d }).FailureCode);
        Assert.Equal(
            CampaignStateValidationCode.DocumentTooLarge,
            CampaignStateJson.Parse(new byte[CampaignStateContract.MaximumArtifactUtf8Bytes + 1]).FailureCode);
    }

    [Fact]
    public void Validation_diagnostics_do_not_echo_private_input()
    {
        const string marker = "PRIVATE/source/C:/secret/provider-response";
        var failure = Assert.Throws<CampaignStateValidationException>(() =>
            CampaignStateFactory.CreateValidated(
                ProductRevision(),
                marker,
                Snapshot(),
                0,
                Ceilings(),
                EmptyCharges(),
                []));

        Assert.DoesNotContain(marker, failure.Message, StringComparison.Ordinal);
        Assert.Equal(CampaignStateValidationCode.InvalidVocabulary, failure.Code);
    }

    [Fact]
    public void Charge_decomposition_is_checked_as_one_invariant()
    {
        var invalid = EmptyCharges() with
        {
            ProviderRequests = new CampaignChargeObservation(2, 3, 4),
        };

        var failure = Assert.Throws<CampaignStateValidationException>(() =>
            CampaignStateFactory.CreateValidated(
                ProductRevision(), "campaign.test", Snapshot(), 0, Ceilings(), invalid, []));
        Assert.Equal(CampaignStateValidationCode.InvalidCorrelation, failure.Code);
    }

    [Fact]
    public void Terminal_kind_reason_and_work_membership_are_one_closed_invariant()
    {
        var failure = Assert.Throws<CampaignStateValidationException>(() =>
            CampaignStateFactory.CreateValidated(
                ProductRevision(), "campaign.test", Snapshot(), 0, Ceilings(), EmptyCharges(), [],
                terminalOutcome: new CampaignTerminalOutcome(
                    CampaignTerminalKind.Complete,
                    CampaignTerminalReason.Budget)));

        Assert.Equal(CampaignStateValidationCode.InvalidShape, failure.Code);
    }

    [Fact]
    public void Predecessor_summary_is_bounded_and_cannot_invent_more_files_than_work()
    {
        var predecessor = new CampaignPredecessorSummary(
            ProductRevision(),
            Snapshot() with { OpaqueSnapshotBinding = "snapshot.previous" },
            Hash('7'),
            2,
            Hash('8'),
            CampaignTerminalKind.Complete,
            null,
            new CampaignPredecessorCandidateSummary(1, 2, 0, 0, 0, 0, null, null));

        var failure = Assert.Throws<CampaignStateValidationException>(() =>
            CampaignStateFactory.CreateValidated(
                ProductRevision(), "campaign.test", Snapshot(), 0, Ceilings(), EmptyCharges(), [],
                terminalOutcome: new CampaignTerminalOutcome(
                    CampaignTerminalKind.Complete,
                    CampaignTerminalReason.NoWork),
                predecessor: predecessor));
        Assert.Equal(CampaignStateValidationCode.InvalidCorrelation, failure.Code);
    }

    [Fact]
    public void Core_checkpoint_contract_has_no_host_or_execution_layer_reference()
    {
        var references = typeof(CampaignStateContract).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("ContractScribe.Agent", references);
        Assert.DoesNotContain("ContractScribe.Patching", references);
        Assert.DoesNotContain("ContractScribe.Roslyn", references);
        Assert.DoesNotContain("Octokit", references);
    }

    private static CampaignCheckpointState CreateState() =>
        CampaignStateFactory.CreateValidated(
            ProductRevision(),
            "campaign.test",
            Snapshot(),
            0,
            Ceilings(),
            EmptyCharges(),
            [],
            terminalOutcome: new CampaignTerminalOutcome(
                CampaignTerminalKind.Complete,
                CampaignTerminalReason.NoWork));

    private static CampaignStateProductRevision ProductRevision() =>
        new("contract-scribe.test", Hash('1'));

    private static CampaignStateSnapshotAuthority Snapshot() =>
        new(
            "snapshot.test",
            Hash('2'),
            Hash('3'),
            Hash('4'),
            TargetProfile.ExternalApi,
            Hash('5'));

    private static CampaignStateConfiguredCeilings Ceilings() =>
        new(
            new CampaignStateCampaignBudget(
                512, 512, 1_048_576, 8, 3, 100_000, 100_000, 100_000,
                1_000_000, 60_000, 3, false, null, null, null),
            new CampaignStateScribeLimits(
                32, 262_144, 64, 262_144, 8, 8, 16, 3,
                100_000, 100_000, 100_000, 1_000_000, 60_000),
            new CampaignStyleConfigurationAuthority("style.test", Hash('6')),
            Hash('7'));

    private static CampaignLineageCharges EmptyCharges()
    {
        var zero = new CampaignChargeObservation(0, 0, 0);
        return new CampaignLineageCharges(0, zero, zero, zero, zero, zero, zero, zero, zero, 0);
    }

    private static string Hash(char value) => new(value, 64);

    private static byte[] WriteUnderCulture(string cultureName)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        return CampaignStateJson.Write(CreateState());
    }

    private static string FixtureRoot() => Path.GetFullPath(Path.Join(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "fixtures", "campaign", "state"));

    private static string FixturePath(string name) =>
        Path.GetFullPath(Path.Join(FixtureRoot(), name));

    private static string FixturePath(string directory, string name) =>
        Path.GetFullPath(Path.Join(FixtureRoot(), directory, name));

    private static string SchemaPath() => Path.GetFullPath(Path.Join(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "schemas", "campaign-state", "v1.schema.json"));
}
