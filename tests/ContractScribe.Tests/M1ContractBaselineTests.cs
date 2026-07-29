using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ContractScribe.ContractBaselineProbe;
using ContractScribe.HostValidation;
using Json.Schema;

namespace ContractScribe.Tests;

public sealed class M1ContractBaselineTests
{
    private static readonly string Root = FindRepositoryRoot();
    private static readonly string FixtureRoot = Path.Join(Root, "tests", "fixtures", "m1-contract-baseline", "v1");
    private static readonly JsonSerializerOptions CanonicalJsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly string[] ExpectedClassificationOriginSkipIds =
    [
        "component.generated-and-semantic.generated-selection.accept",
        "component.generated-and-semantic.semantic-selection.reject",
        "component.generated-provenance.compiler-synthesized-origin.reject",
        "component.generated-provenance.mixed-origin.reject",
        "component.generated-provenance.source-generator-origin.reject",
        "component.generated-provenance.source-origin.reject",
        "component.generated-provenance.tool-generated-origin.reject",
        "component.generated-provenance.unknown-origin.accept",
        "component.semantic-context.compiler-synthesized-origin.reject",
        "component.semantic-context.component.accessor.get.ineligible.reject",
        "component.semantic-context.component.return.eligible.accept",
        "component.semantic-context.component.type-parameter.eligible.accept",
        "component.semantic-context.component.value.eligible.accept",
        "component.semantic-context.mixed-origin.accept",
        "component.semantic-context.source-generator-origin.accept",
        "component.semantic-context.source-origin.accept",
        "component.semantic-context.tool-generated-origin.accept",
        "component.semantic-context.unknown-origin.reject",
        "target.generated-and-semantic.generated-selection.accept",
        "target.generated-and-semantic.semantic-selection.reject",
        "target.generated-provenance.compiler-synthesized-origin.reject",
        "target.generated-provenance.mixed-origin.reject",
        "target.generated-provenance.source-generator-origin.reject",
        "target.generated-provenance.source-origin.reject",
        "target.generated-provenance.tool-generated-origin.reject",
        "target.generated-provenance.unknown-origin.accept",
        "target.semantic-context.compiler-synthesized-origin.reject",
        "target.semantic-context.mixed-origin.accept",
        "target.semantic-context.source-generator-origin.accept",
        "target.semantic-context.source-origin.accept",
        "target.semantic-context.tool-generated-origin.accept",
        "target.semantic-context.unknown-origin.reject",
        "unresolved.documentation-comment-id.compiler-synthesized-origin.reject",
        "unresolved.documentation-comment-id.mixed-origin.accept",
        "unresolved.documentation-comment-id.source-generator-origin.accept",
        "unresolved.documentation-comment-id.source-origin.accept",
        "unresolved.documentation-comment-id.tool-generated-origin.accept",
        "unresolved.documentation-comment-id.unknown-origin.reject",
        "unresolved.generated-and-semantic.generated-selection.accept",
        "unresolved.generated-and-semantic.semantic-selection.reject",
        "unresolved.generated-provenance.compiler-synthesized-origin.reject",
        "unresolved.generated-provenance.mixed-origin.reject",
        "unresolved.generated-provenance.source-generator-origin.reject",
        "unresolved.generated-provenance.source-origin.reject",
        "unresolved.generated-provenance.tool-generated-origin.reject",
        "unresolved.generated-provenance.unknown-origin.accept",
        "unresolved.semantic-context.compiler-synthesized-origin.reject",
        "unresolved.semantic-context.mixed-origin.accept",
        "unresolved.semantic-context.source-generator-origin.accept",
        "unresolved.semantic-context.source-origin.accept",
        "unresolved.semantic-context.tool-generated-origin.accept",
        "unresolved.semantic-context.unknown-origin.reject"
    ];
    private static readonly string[] ExpectedRepositoryCandidateLocatorIds =
    [
        "generated-source.accept",
        "repository.absolute.reject",
        "repository.backslash.reject",
        "repository.canonical.accept",
        "repository.dot-segment.reject",
        "repository.double-separator.reject",
        "repository.drive-relative.reject",
        "repository.drive-root.reject",
        "repository.empty.reject",
        "repository.leading-dot-segment.reject",
        "repository.nul.reject",
        "repository.parent-segment.reject",
        "repository.reversed-span.reject",
        "repository.trailing-separator.reject",
        "repository.unc-like.reject",
        "repository.whitespace-segment.accept",
        "synthetic.accept",
        "tool-generated.accept"
    ];

    [Fact]
    public void ClassificationOriginSkipVectors_AreClosedAndAcceptedByIndependentConsumers()
    {
        using var fixture = Load("classification-origin-skip-vectors.json");
        var root = fixture.RootElement;
        Assert.Equal(
            new[] { "formatVersion", "cases" },
            root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(
            "contractscribe-m1-classification-origin-skip-v1",
            root.GetProperty("formatVersion").GetString());
        var cases = root.GetProperty("cases").EnumerateArray().ToArray();
        var ids = cases.Select(row => row.GetProperty("caseId").GetString()!).ToArray();
        Assert.Equal(ExpectedClassificationOriginSkipIds, ids);
        Assert.All(cases, row => Assert.Equal(
            new[]
            {
                "caseId",
                "conditions",
                "record",
                "recordOutcome",
                "selectionOutcome",
                "outcome"
            },
            row.EnumerateObject().Select(property => property.Name)));
        AssertClosedCatalogMutations(
            root,
            "contractscribe-m1-classification-origin-skip-v1",
            [
                "caseId",
                "conditions",
                "record",
                "recordOutcome",
                "selectionOutcome",
                "outcome"
            ],
            ExpectedClassificationOriginSkipIds);
        Assert.True(IsClosedClassificationCatalog(
            JsonNode.Parse(root.GetRawText())!.AsObject()));

        var taxonomyOracle = ClassificationConformanceOracle.Load(Root);
        foreach (var row in cases)
        {
            var record = row.GetProperty("record");
            var expectedRecord =
                row.GetProperty("recordOutcome").GetString() == "accept";
            var expectedSelection =
                row.GetProperty("selectionOutcome").GetString() == "accept";
            var taxonomyAccepted = taxonomyOracle.IsValidRecord(record);
            var auditAccepted = IsAuditClassificationValid(record);
            var selectionAccepted = ConditionsSelectRecord(row);
            Assert.Equal(expectedRecord, taxonomyAccepted);
            Assert.Equal(expectedRecord, auditAccepted);
            Assert.Equal(expectedSelection, selectionAccepted);
            Assert.Equal(
                row.GetProperty("outcome").GetString() == "accept",
                taxonomyAccepted && selectionAccepted);
        }

        var wrongPrecedence = cases.Single(row =>
            row.GetProperty("caseId").GetString()
                == "target.generated-and-semantic.semantic-selection.reject");
        Assert.True(taxonomyOracle.IsValidRecord(
            wrongPrecedence.GetProperty("record")));
        Assert.False(ConditionsSelectRecord(wrongPrecedence));

        var unknownCondition = JsonNode.Parse(root.GetRawText())!.AsObject();
        unknownCondition["cases"]![0]!["conditions"]![0] =
            "semantic-contex-unavailable";
        Assert.False(IsClosedClassificationCatalog(unknownCondition));
        var unknownRecordMember = JsonNode.Parse(root.GetRawText())!.AsObject();
        unknownRecordMember["cases"]![0]!["record"]!["unexpected"] = true;
        Assert.False(IsClosedClassificationCatalog(unknownRecordMember));
        AssertStrictJsonRejections();
    }

    [Fact]
    public void RepositoryCandidateLocatorVectors_RejectRawNonCanonicalFormsBeforeKeysOrOrdering()
    {
        using var fixture = Load("repository-candidate-locator-vectors.json");
        var root = fixture.RootElement;
        Assert.Equal(
            new[] { "formatVersion", "cases" },
            root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(
            "contractscribe-m1-repository-candidate-locator-v1",
            root.GetProperty("formatVersion").GetString());
        var cases = root.GetProperty("cases").EnumerateArray().ToArray();
        var ids = cases.Select(row => row.GetProperty("caseId").GetString()!).ToArray();
        Assert.Equal(ExpectedRepositoryCandidateLocatorIds, ids);
        Assert.All(cases, row => Assert.Equal(
            new[] { "caseId", "locator", "outcome" },
            row.EnumerateObject().Select(property => property.Name)));
        AssertClosedCatalogMutations(
            root,
            "contractscribe-m1-repository-candidate-locator-v1",
            ["caseId", "locator", "outcome"],
            ExpectedRepositoryCandidateLocatorIds);
        Assert.True(IsClosedLocatorCatalog(
            JsonNode.Parse(root.GetRawText())!.AsObject()));

        var taxonomyOracle = ClassificationConformanceOracle.Load(Root);
        var template = JsonNode.Parse(File.ReadAllText(Path.Join(
            Root,
            "tests",
            "fixtures",
            "audit-result",
            "v1",
            "payloads",
            "unresolved-classification.json")))!.AsObject();
        foreach (var row in cases)
        {
            var expected = row.GetProperty("outcome").GetString() == "accept";
            var locator = JsonNode.Parse(row.GetProperty("locator").GetRawText())!;
            var record = new JsonObject
            {
                ["recordType"] = "UnresolvedClassification",
                ["compilationContextRef"] = "synthetic.v1",
                ["origin"] = "origin.source",
                ["supportStatus"] = "support.unavailable-context",
                ["skipReason"] = "skip.unavailable.semantic-context",
                ["candidateLocator"] = locator.DeepClone()
            };
            Assert.Equal(expected, taxonomyOracle.IsValidRecord(Element(record)));
            Assert.Equal(expected, IsAuditClassificationValid(Element(record)));

            var document = (JsonObject)template.DeepClone();
            document["results"]![0]!["classification"]!["candidateLocator"] =
                locator.DeepClone();
            using var parsed = JsonDocument.Parse(
                JsonSerializer.SerializeToUtf8Bytes(document));
            Assert.Equal(expected, IsReplayDocumentValid(parsed.RootElement));
            Assert.Equal(expected, IsHostAuditResultValid(parsed.RootElement));
        }

        var unknownLocatorMember =
            JsonNode.Parse(root.GetRawText())!.AsObject();
        unknownLocatorMember["cases"]![0]!["locator"]!["unexpected"] =
            new JsonObject();
        Assert.False(IsClosedLocatorCatalog(unknownLocatorMember));
    }

    [Fact]
    public void CurrentManifest_BindsTheIssue55SuccessorAndExactCurrentInputClosure()
    {
        using var manifest = Load("manifest.json");
        var root = manifest.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("issue-55-classification-origin-closure-v1", root.GetProperty("contractRevision").GetString());
        Assert.Equal(
            new[] { "profile.external-api", "profile.assembly-visible" },
            root.GetProperty("profiles").EnumerateArray()
                .Select(value => value.GetString()));

        var predecessor = root.GetProperty("predecessor");
        Assert.Equal(
            "https://github.com/SolusQuest/contract-scribe/issues/35",
            predecessor.GetProperty("coordinatingIssue").GetString());
        Assert.Equal(
            "issue-35-pre-release-v1",
            predecessor.GetProperty("contractRevision").GetString());
        Assert.Equal(
            "bb4654edc180e2953dda6b89a29211b18778b78e",
            predecessor.GetProperty("mergeCommit").GetString());
        Assert.Equal(
            "tests/fixtures/m1-contract-baseline/v1/manifest.json",
            predecessor.GetProperty("contractManifest").GetString());
        Assert.Equal(
            "2872387ce9cfd8578c8f473ec26ab9f10dd44381edfbc0248e6fa370d797ab31",
            predecessor.GetProperty("contractManifestSha256").GetString());

        var expectedInputs = ExpectedCurrentInputs();
        var currentInputs = root.GetProperty("currentInputs");
        var currentPaths = currentInputs.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Equal(expectedInputs.Keys, currentPaths);
        Assert.All(currentInputs.EnumerateObject(), property =>
        {
            Assert.Matches("^[0-9a-f]{64}$", property.Value.GetString()!);
            Assert.Equal(
                Sha256(Path.Join(
                    Root,
                    property.Name.Replace('/', Path.DirectorySeparatorChar))),
                property.Value.GetString());
        });
        Assert.DoesNotContain(
            "tests/fixtures/m1-contract-baseline/v1/manifest.json",
            currentPaths,
            StringComparer.Ordinal);

        var fixtures = root.GetProperty("fixtures");
        Assert.Equal(
            new[]
            {
                "policy",
                "profiles",
                "generatedIdentity",
                "auditAuthority",
                "classificationOriginSkip",
                "repositoryCandidateLocator",
                "rowCrosswalk",
                "processReplay"
            },
            fixtures.EnumerateObject().Select(property => property.Name));
        Assert.All(
            fixtures.EnumerateObject(),
            property => Assert.True(File.Exists(Path.Join(
                Root,
                property.Value.GetString()!.Replace('/', Path.DirectorySeparatorChar)))));

        var disposition = root.GetProperty("implementationDisposition");
        Assert.Equal(
            new[] { 36 },
            disposition.GetProperty("completedImplementationIssues")
                .EnumerateArray().Select(value => value.GetInt32()));
        Assert.Equal(
            new[] { 37, 38, 39, 40 },
            disposition.GetProperty("activeImplementationIssues")
                .EnumerateArray().Select(value => value.GetInt32()));
        Assert.Equal(
            new[] { 41 },
            disposition.GetProperty("activeValidationIssues")
                .EnumerateArray().Select(value => value.GetInt32()));
        Assert.True(File.Exists(Path.Join(Root, root.GetProperty("inventory").GetString()!.Replace('/', Path.DirectorySeparatorChar))));

        var value = JsonNode.Parse(root.GetRawText())!.AsObject();
        Assert.True(IsValidSuccessorManifest(value, expectedInputs));
        var removed = (JsonObject)value.DeepClone();
        removed["currentInputs"]!.AsObject().Remove(expectedInputs.Keys.First());
        Assert.False(IsValidSuccessorManifest(removed, expectedInputs));
        var added = (JsonObject)value.DeepClone();
        added["currentInputs"]!["unexpected.txt"] = new string('0', 64);
        Assert.False(IsValidSuccessorManifest(added, expectedInputs));
        var changed = (JsonObject)value.DeepClone();
        changed["currentInputs"]![expectedInputs.Keys.First()] = new string('f', 64);
        Assert.False(IsValidSuccessorManifest(changed, expectedInputs));
        var reordered = (JsonObject)value.DeepClone();
        var reversedInputs = new JsonObject(
            reordered["currentInputs"]!.AsObject().Reverse()
                .Select(property => KeyValuePair.Create(
                    property.Key,
                    property.Value?.DeepClone())));
        reordered["currentInputs"] = reversedInputs;
        Assert.False(IsValidSuccessorManifest(reordered, expectedInputs));
        var unknownNested = (JsonObject)value.DeepClone();
        unknownNested["predecessor"]!["unexpected"] = true;
        Assert.False(IsValidSuccessorManifest(unknownNested, expectedInputs));
        var wrongInventory = (JsonObject)value.DeepClone();
        wrongInventory["inventory"] =
            "docs/20_architecture/contracts/audit-result-v1.md";
        Assert.False(IsValidSuccessorManifest(wrongInventory, expectedInputs));
        var wrongFixture = (JsonObject)value.DeepClone();
        wrongFixture["fixtures"]!["classificationOriginSkip"] =
            "tests/fixtures/m1-contract-baseline/v1/profile-cases.json";
        Assert.False(IsValidSuccessorManifest(wrongFixture, expectedInputs));
        var reorderedProfiles = (JsonObject)value.DeepClone();
        reorderedProfiles["profiles"] = new JsonArray(
            "profile.assembly-visible",
            "profile.external-api");
        Assert.False(IsValidSuccessorManifest(
            reorderedProfiles,
            expectedInputs));
        var wrongIssueSet = (JsonObject)value.DeepClone();
        wrongIssueSet["implementationDisposition"]!
            ["activeValidationIssues"] = new JsonArray(40, 41);
        Assert.False(IsValidSuccessorManifest(wrongIssueSet, expectedInputs));
        var wrongPredecessor = (JsonObject)value.DeepClone();
        wrongPredecessor["predecessor"]!["contractRevision"] =
            "issue-35-pre-release-v2";
        Assert.False(IsValidSuccessorManifest(wrongPredecessor, expectedInputs));
    }

    [Fact]
    public void RowCrosswalk_IsBidirectionallyCompleteForTheAcceptedDecisionAnnex()
    {
        using var decision = JsonDocument.Parse(File.ReadAllText(Path.Join(Root, "tests", "fixtures", "m1-target-observation", "adr-0003-vectors.json")));
        using var crosswalk = Load("row-crosswalk.json");
        var expected = decision.RootElement.EnumerateObject()
            .Where(property => property.Value.ValueKind == JsonValueKind.Array)
            .Where(property => property.Name is not ("impactIssues" or "postMvpIssues"))
            .SelectMany(property => property.Value.EnumerateArray()
                .Where(row => row.ValueKind == JsonValueKind.Object)
                .Select(row => (Collection: property.Name, Row: row)))
            .Where(pair => pair.Row.TryGetProperty("caseId", out _) || pair.Row.TryGetProperty("id", out _))
            .Select(pair => $"{pair.Collection}/{(pair.Row.TryGetProperty("caseId", out var caseId) ? caseId : pair.Row.GetProperty("id")).GetString()}")
            .ToHashSet(StringComparer.Ordinal);
        var actualRows = crosswalk.RootElement.GetProperty("rows").EnumerateArray().ToArray();
        var actual = actualRows.Select(row => row.GetProperty("rowKey").GetString()!).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
        Assert.Equal(actualRows.Length, actual.Count);
        Assert.All(actualRows, row =>
        {
            Assert.Contains(row.GetProperty("disposition").GetString(), new[] { "amended", "existing-v1", "downstream" });
            foreach (var property in new[] { "normativeSection", "schemaOrRegistry", "validFixture", "invalidOrMutation", "oracle" })
            {
                var relativePath = row.GetProperty(property).GetString();
                Assert.False(string.IsNullOrWhiteSpace(relativePath));
                Assert.True(File.Exists(Path.Join(Root, relativePath!.Replace('/', Path.DirectorySeparatorChar))), $"{row.GetProperty("rowKey").GetString()} references missing {property}: {relativePath}");
            }
            if (row.GetProperty("disposition").GetString() == "amended")
            {
                Assert.DoesNotContain("m1-target-observation/adr-0003-vectors.json", row.GetProperty("validFixture").GetString(), StringComparison.Ordinal);
            }
        });

        var closed = JsonNode.Parse(crosswalk.RootElement.GetRawText())!
            .AsObject();
        Assert.True(AreAffectedCrosswalkRowsExact(closed));
        var wrongOwner = (JsonObject)closed.DeepClone();
        FindCrosswalkRow(
            wrongOwner,
            "failureCases/failure.semantic-context")["downstreamIssue"] = 38;
        Assert.False(AreAffectedCrosswalkRowsExact(wrongOwner));
        var wrongPath = (JsonObject)closed.DeepClone();
        FindCrosswalkRow(
            wrongPath,
            "failureCases/failure.generated-provenance")["oracle"] =
                "tests/ContractScribe.Tests/SymbolEvidenceTaxonomyContractTests.cs";
        Assert.False(AreAffectedCrosswalkRowsExact(wrongPath));
    }

    [Fact]
    public void RunProfile_IsRequiredClosedAndIdenticalAcrossPolicyAndAudit()
    {
        using var fixture = Load("profile-cases.json");
        var policySchema = LoadRootSchema("schemas/policy-configuration/v1.schema.json");
        var auditSchema = LoadRootSchema("schemas/audit-result/v1.schema.json");
        var canonicalDocuments = new List<string>();
        foreach (var row in fixture.RootElement.GetProperty("valid").EnumerateArray())
        {
            var policy = row.GetProperty("policy");
            var audit = row.GetProperty("audit");
            Assert.True(policySchema.Evaluate(policy).IsValid);
            Assert.True(auditSchema.Evaluate(audit).IsValid);
            Assert.Equal(policy.GetProperty("targetProfile").GetString(), audit.GetProperty("targetProfile").GetString());
            canonicalDocuments.Add(JsonSerializer.Serialize(audit));
        }
        Assert.Equal(2, canonicalDocuments.Distinct(StringComparer.Ordinal).Count());

        foreach (var row in fixture.RootElement.GetProperty("invalid").EnumerateArray())
        {
            var policyProfile = row.GetProperty("policyProfile");
            var auditProfile = row.GetProperty("auditProfile");
            Assert.False(
                policyProfile.ValueKind == JsonValueKind.String
                && auditProfile.ValueKind == JsonValueKind.String
                && policyProfile.GetString() is "profile.external-api" or "profile.assembly-visible"
                && policyProfile.GetString() == auditProfile.GetString());
        }
    }

    [Fact]
    public void GeneratedIdentityVectors_AreOpaqueDomainSeparatedAndCollisionSafe()
    {
        using var vectors = Load("generated-identity-vectors.json");
        var registrations = new Dictionary<string, string>(StringComparer.Ordinal);
        var accepted = vectors.RootElement.GetProperty("vectors").EnumerateArray()
            .Concat(vectors.RootElement.GetProperty("designatedOpaqueSourceGeneratorInputs").EnumerateArray())
            .ToArray();
        foreach (var vector in accepted)
        {
            var prefix = vector.GetProperty("prefix").GetString()!;
            var fields = vector.GetProperty("fields").EnumerateArray().Select(value => value.GetString()!).ToArray();
            Assert.True(IsValidIdentityFields(prefix, fields));
            var preimage = BuildIdentityPreimage(vector.GetProperty("domain").GetString()!, fields);
            var id = $"{prefix}.{Sha256(preimage)}";
            Assert.Equal(vector.GetProperty("preimageHex").GetString(), Convert.ToHexString(preimage).ToLowerInvariant());
            Assert.Equal(vector.GetProperty("expectedId").GetString(), id);
            Assert.Null(RegisterIdentity(registrations, id, Convert.ToHexString(preimage)));
            Assert.Null(RegisterIdentity(registrations, id, Convert.ToHexString(preimage)));
        }

        foreach (var vector in vectors.RootElement.GetProperty("invalidVectors").EnumerateArray())
        {
            var category = vector.GetProperty("category").GetString();
            var fields = vector.TryGetProperty("fields", out var declaredFields)
                ? declaredFields.EnumerateArray().Select(value => value.GetString()!).ToArray()
                : new[] { new string('x', 4097) };
            var prefix = category == "tool" ? "tgo" : "sgo";
            var expected = vector.GetProperty("outcome").GetString();
            Assert.Equal(expected == "accept-opaque", IsValidIdentityFields(prefix, fields));
        }

        var forcedId = $"tgp.{"0".PadLeft(64, '0')}";
        Assert.Null(RegisterIdentity(registrations, forcedId, "aa"));
        Assert.Equal("run.generated.identity-collision", RegisterIdentity(registrations, forcedId, "bb"));
        Assert.Equal("run.generated.missing-identity", ValidateGeneratedFact(null, "tgo." + new string('1', 64)));
        Assert.Equal("run.generated.authority-conflict", ValidateGeneratedFact("tgp." + new string('2', 64), "sgo." + new string('3', 64)));
    }

    [Fact]
    public void GeneratedAndComponentShapes_AreClosedAndPrefixMatched()
    {
        var candidateSchema = LoadDefinition("schemas/symbol-evidence-taxonomy/v1.schema.json", "candidateLocator");
        var evidenceLocatorSchema = LoadDefinition("schemas/symbol-evidence-taxonomy/v1.schema.json", "locator");
        Assert.True(candidateSchema.Evaluate(Element(JsonNode.Parse($"{{\"toolGenerated\":{{\"producerId\":\"tgp.{new string('1', 64)}\",\"outputId\":\"tgo.{new string('2', 64)}\"}}}}")!)).IsValid);
        Assert.False(candidateSchema.Evaluate(Element(JsonNode.Parse($"{{\"toolGenerated\":{{\"producerId\":\"sgp.{new string('1', 64)}\",\"outputId\":\"sgo.{new string('2', 64)}\"}}}}")!)).IsValid);
        Assert.True(evidenceLocatorSchema.Evaluate(Element(JsonNode.Parse($"{{\"generatedOutput\":{{\"producerKind\":\"source-generator\",\"producerId\":\"sgp.{new string('3', 64)}\",\"outputId\":\"sgo.{new string('4', 64)}\",\"sourceSha256\":\"{new string('5', 64)}\"}}}}")!)).IsValid);
        Assert.False(evidenceLocatorSchema.Evaluate(Element(JsonNode.Parse($"{{\"generatedOutput\":{{\"producerKind\":\"tool-generated\",\"producerId\":\"sgp.{new string('3', 64)}\",\"outputId\":\"sgo.{new string('4', 64)}\",\"sourceSha256\":\"{new string('5', 64)}\"}}}}")!)).IsValid);
    }

    [Fact]
    public void ObservationAuthority_IsCommittedUpstreamAndMutationsFailClosed()
    {
        using var fixture = Load("audit-authority-cases.json");
        var cases = fixture.RootElement.GetProperty("valid").EnumerateArray()
            .ToDictionary(row => row.GetProperty("caseId").GetString()!, row => JsonNode.Parse(row.GetRawText())!.AsObject(), StringComparer.Ordinal);
        Assert.All(cases.Values, value => Assert.True(IsValidAuthorityCase(value)));

        var canonical = cases["component-complete-absent"];
        var permuted = (JsonObject)canonical.DeepClone();
        var permutedDeclarations = permuted["evidenceAuthority"]!["declarations"]!.AsArray();
        permutedDeclarations.Reverse();
        for (var index = 0; index < permutedDeclarations.Count; index++)
        {
            var row = permutedDeclarations[index]!.AsObject();
            permutedDeclarations[index] = new JsonObject(row.Reverse().Select(property => KeyValuePair.Create(property.Key, property.Value?.DeepClone())));
        }
        Assert.True(IsValidAuthorityCase(permuted));
        Assert.Equal(
            AuditResultCanonicalizer.CanonicalizeDeclarations(Element(canonical["evidenceAuthority"]!["declarations"]!)),
            AuditResultCanonicalizer.CanonicalizeDeclarations(Element(permutedDeclarations)));

        foreach (var mutation in fixture.RootElement.GetProperty("invalidMutations").EnumerateArray())
        {
            var value = (JsonObject)cases[mutation.GetProperty("sourceCaseId").GetString()!].DeepClone();
            ApplyMutation(value, mutation.GetProperty("mutation").GetString()!);
            Assert.False(IsValidAuthorityCase(value), mutation.GetProperty("caseId").GetString());
        }
    }

    [Fact]
    public void PublicFixtureSafety_AllowsOnlyDesignatedSyntheticIdentityInputs()
    {
        var identity = JsonNode.Parse(File.ReadAllText(Path.Join(FixtureRoot, "generated-identity-vectors.json")))!.AsObject();
        var designatedInputs = new List<string>();
        foreach (var values in identity["designatedOpaqueSourceGeneratorInputs"]!.AsArray().OfType<JsonObject>().Select(vector => vector["fields"]!.AsArray()))
        {
            for (var index = 0; index < values.Count; index++)
            {
                var value = values[index]!;
                designatedInputs.Add(value.GetValue<string>());
                values[index] = "[designated-synthetic-input]";
            }
        }

        var amendedFixtureRoots = new[]
        {
            FixtureRoot,
            Path.Join(Root, "tests", "fixtures", "policy-configuration", "v1"),
            Path.Join(Root, "tests", "fixtures", "symbol-evidence-taxonomy", "v1"),
            Path.Join(Root, "tests", "fixtures", "audit-result", "v1")
        };
        var outputShapedText = string.Join(
            "\n",
            amendedFixtureRoots
                .SelectMany(path => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                .Where(path => !path.EndsWith("generated-identity-vectors.json", StringComparison.Ordinal))
                .Select(File.ReadAllText));
        Assert.All(
            designatedInputs.Where(value => value.Contains("sk-test-not-a-credential", StringComparison.Ordinal) || Regex.IsMatch(value, @"^[A-Za-z]:\\")),
            value => Assert.DoesNotContain(value, outputShapedText, StringComparison.Ordinal));
        var scanned = identity.ToJsonString() + "\n" + outputShapedText;
        Assert.DoesNotMatch(new Regex(@"(?i)([a-z]:\\users\\|/users/[^/]+/|password\s*[=:]|access[_-]?token\s*[=:]|api[_-]?key\s*[=:]|client[_-]?secret\s*[=:]|-----begin [a-z ]+private key-----|sk-(?!test-not-a-credential)[a-z0-9_-]{12,})"), scanned);
    }

    [Fact]
    public void Replay_IsByteStableAcrossFreshProcessesCulturesAndTimeZones()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Release";
        var probe = Path.Join(Root, "tests", "ContractScribe.ContractBaselineProbe", "bin", configuration, "net10.0", "ContractScribe.ContractBaselineProbe.dll");
        Assert.True(File.Exists(probe), $"The contract replay probe was not built at {probe}.");
        var input = Path.Join(FixtureRoot, "process-replay-input.json");
        var first = RunProbe(probe, input, "tr-TR", "Pacific/Auckland");
        var second = RunProbe(probe, input, "ja-JP", "America/Los_Angeles");
        Assert.Matches("^[0-9a-f]{64}$", first);
        Assert.Equal(first, second);
    }

    private static bool IsValidAuthorityCase(JsonObject value)
    {
        try
        {
            var observation = value["observationSubject"]!.AsObject();
            var authority = value["evidenceAuthority"]!.AsObject();
            if (!LoadDefinition("schemas/symbol-evidence-taxonomy/v1.schema.json", "observationSubject").Evaluate(Element(observation)).IsValid
                || !LoadDefinition("schemas/audit-result/v1.schema.json", "evidenceAuthority").Evaluate(Element(authority)).IsValid)
            {
                return false;
            }

            var subject = value["subject"]!.AsObject();
            if (!JsonNode.DeepEquals(subject, observation["subject"])
                || Context(subject) != observation["compilationContextRef"]!.GetValue<string>())
            {
                return false;
            }

            var declarations = authority["declarations"]!.AsArray();
            var declarationDigest = AuditResultCanonicalizer.ComputeDeclarationDigest(Element(declarations));
            if (observation["authoritativeDeclarationSetDigest"]!.GetValue<string>() != declarationDigest
                || observation["authoritativeDeclarationCount"]!.GetValue<int>() != declarations.Count
                || authority["declarationSetId"]!.GetValue<string>() != $"dset.{declarationDigest}")
            {
                return false;
            }

            var computedObservationRef = AuditResultCanonicalizer.ComputeObservationSubjectRef(Element(observation));
            if (observation["observationSubjectRef"]!.GetValue<string>() != computedObservationRef)
            {
                return false;
            }

            var isComponent = subject.ContainsKey("parentSymbolRef");
            var componentKind = isComponent ? subject["componentKind"]!.GetValue<string>() : null;
            var declarationIds = new HashSet<string>(StringComparer.Ordinal);
            var evidenceIds = new HashSet<string>(StringComparer.Ordinal);
            var authorityRoles = new HashSet<string>(StringComparer.Ordinal);
            foreach (var declaration in declarations.OfType<JsonObject>())
            {
                var declarationId = declaration["declarationId"]!.GetValue<string>();
                if (!declarationIds.Add(declarationId)
                    || !evidenceIds.Add(declaration["evidenceId"]!.GetValue<string>()))
                {
                    return false;
                }
                authorityRoles.Add(declaration["authorityRole"]!.GetValue<string>());

                var hasLocalName = declaration.ContainsKey("componentLocalName");
                var hasMatch = declaration.ContainsKey("componentMatch");
                var isMalformed = declaration["blockState"]!.GetValue<string>() == "malformed";
                if (!isComponent && (hasLocalName || hasMatch)) return false;
                if (componentKind is "component.parameter" or "component.type-parameter")
                {
                    if (!hasLocalName || isMalformed == hasMatch) return false;
                }
                else if (isComponent && (hasLocalName || isMalformed == hasMatch))
                {
                    return false;
                }

                if (isMalformed && hasMatch) return false;
            }
            if (authorityRoles.Contains("partial-member-implementing") && authorityRoles.Contains("partial-member-defining-fallback")) return false;
            if (authorityRoles.Contains("ordinary") && declarations.Count != 1) return false;

            var observationValue = value["documentationObservation"]!.GetValue<string>();
            if (AuditResultCanonicalizer.DeriveDocumentationObservation(Element(subject), Element(authority)) != observationValue) return false;
            if (observationValue != "documentation.present" && authority["completeness"]!.GetValue<string>() != "complete") return false;
            var hasMalformed = declarations.OfType<JsonObject>().Any(row => row["blockState"]!.GetValue<string>() == "malformed");
            if ((value["reasonCode"]!.GetValue<string>() == "audit.reason.documentation-unavailable.malformed-xml")
                != (observationValue == "documentation.unavailable" && hasMalformed))
            {
                return false;
            }

            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (KeyNotFoundException)
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
    }

    private static void ApplyMutation(JsonObject value, string mutation)
    {
        var observation = value["observationSubject"]!.AsObject();
        var authority = value["evidenceAuthority"]!.AsObject();
        var declarations = authority["declarations"]!.AsArray();
        switch (mutation)
        {
            case "replace-declaration-set-id":
                authority["declarationSetId"] = "dset." + new string('0', 64);
                break;
            case "remove-authority-row-and-rehash-local-only":
                declarations.RemoveAt(declarations.Count - 1);
                authority["declarationSetId"] = "dset." + Sha256(Encoding.UTF8.GetBytes(declarations.ToJsonString(CanonicalJsonOptions)));
                break;
            case "replace-component-local-name":
                declarations[0]!["componentLocalName"] = "other";
                break;
            case "replace-observation-context":
                observation["compilationContextRef"] = "fixture.other-context";
                break;
            case "replace-observation-component":
                observation["subject"]!["identity"] = "parameter/1";
                break;
            case "replace-completeness-positive-only":
                authority["completeness"] = "positive-only";
                break;
            case "replace-component-match-present":
                declarations[0]!["componentMatch"] = "present";
                break;
            case "duplicate-declaration-id":
                declarations[1]!["declarationId"] = declarations[0]!["declarationId"]!.DeepClone();
                break;
            case "replace-authority-roles-with-conflict":
                declarations[0]!["authorityRole"] = "partial-member-implementing";
                declarations[1]!["authorityRole"] = "partial-member-defining-fallback";
                break;
            default:
                throw new InvalidOperationException($"Unknown mutation {mutation}.");
        }
    }

    private static string Context(JsonObject subject) =>
        subject.TryGetPropertyValue("parentSymbolRef", out var parentSymbolRef)
            ? parentSymbolRef!["compilationContextRef"]!.GetValue<string>()
            : subject["compilationContextRef"]!.GetValue<string>();

    private static string? RegisterIdentity(Dictionary<string, string> registrations, string id, string normalizedPreimage)
    {
        if (!registrations.TryAdd(id, normalizedPreimage) && registrations[id] != normalizedPreimage)
        {
            return "run.generated.identity-collision";
        }

        return null;
    }

    private static byte[] BuildIdentityPreimage(string domain, IReadOnlyList<string> fields)
    {
        using var stream = new MemoryStream();
        stream.Write(Encoding.ASCII.GetBytes(domain));
        stream.WriteByte(0);
        var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        Span<byte> length = stackalloc byte[4];
        foreach (var field in fields)
        {
            var bytes = strictUtf8.GetBytes(field.Normalize(NormalizationForm.FormC));
            BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)bytes.Length));
            stream.Write(length);
            stream.Write(bytes);
        }
        return stream.ToArray();
    }

    private static bool IsValidIdentityFields(string prefix, IReadOnlyList<string> fields)
    {
        if (fields.Count == 0) return false;
        var isTool = prefix is "tgp" or "tgo";
        foreach (var field in fields)
        {
            int byteCount;
            try
            {
                byteCount = new UTF8Encoding(false, true).GetByteCount(field.Normalize(NormalizationForm.FormC));
            }
            catch (EncoderFallbackException)
            {
                return false;
            }
            if (byteCount is 0 or > 4096) return false;
            if (isTool && !Regex.IsMatch(field, "^[A-Za-z][A-Za-z0-9._-]{0,127}$")) return false;
        }
        return true;
    }

    private static string? ValidateGeneratedFact(string? producerId, string? outputId)
    {
        if (producerId is null || outputId is null) return "run.generated.missing-identity";
        return producerId[..3] switch
        {
            "sgp" when outputId.StartsWith("sgo.", StringComparison.Ordinal) => null,
            "tgp" when outputId.StartsWith("tgo.", StringComparison.Ordinal) => null,
            _ => "run.generated.authority-conflict"
        };
    }

    private static JsonSchema LoadDefinition(string relativeSchemaPath, string definition)
    {
        var root = JsonNode.Parse(File.ReadAllText(Path.Join(Root, relativeSchemaPath.Replace('/', Path.DirectorySeparatorChar))))!.AsObject();
        var schema = root["$defs"]![definition]!.DeepClone().AsObject();
        schema["$schema"] = "https://json-schema.org/draft/2020-12/schema";
        if (root["$defs"] is { } definitions)
        {
            schema["$defs"] = definitions.DeepClone();
        }
        return JsonSchema.FromText(schema.ToJsonString());
    }

    private static JsonSchema LoadRootSchema(string relativeSchemaPath)
    {
        var schema = JsonNode.Parse(File.ReadAllText(Path.Join(Root, relativeSchemaPath.Replace('/', Path.DirectorySeparatorChar))))!.AsObject();
        schema.Remove("$id");
        return JsonSchema.FromText(schema.ToJsonString());
    }

    private static string RunProbe(string probe, string input, string culture, string timezone)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(probe);
        start.ArgumentList.Add(input);
        start.Environment["DOTNET_CLI_UI_LANGUAGE"] = culture;
        start.Environment["LANG"] = culture;
        start.Environment["TZ"] = timezone;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start contract replay probe.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output.Trim();
    }

    private static bool IsAuditClassificationValid(JsonElement record)
    {
        try
        {
            AuditResultConformance.ValidateClassification(record);
            return true;
        }
        catch (Xunit.Sdk.XunitException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsReplayDocumentValid(JsonElement document)
    {
        try
        {
            AuditResultCanonicalizer.ValidateReplayDocument(document);
            return true;
        }
        catch (Exception exception) when (
            exception is FormatException
                or InvalidOperationException
                or KeyNotFoundException)
        {
            return false;
        }
    }

    private static bool IsHostAuditResultValid(JsonElement document)
    {
        try
        {
            AuditResultSemanticValidator.Validate(Root, document);
            return true;
        }
        catch (ProtocolException)
        {
            return false;
        }
    }

    private static void AssertClosedCatalogMutations(
        JsonElement original,
        string formatVersion,
        IReadOnlyList<string> caseProperties,
        IReadOnlyList<string> expectedIds)
    {
        Assert.True(IsClosedCatalog(
            JsonNode.Parse(original.GetRawText())!.AsObject(),
            formatVersion,
            caseProperties,
            expectedIds));

        JsonObject Mutate(Action<JsonArray> mutation)
        {
            var value = JsonNode.Parse(original.GetRawText())!.AsObject();
            mutation(value["cases"]!.AsArray());
            return value;
        }

        Assert.False(IsClosedCatalog(
            Mutate(cases => cases.RemoveAt(cases.Count - 1)),
            formatVersion,
            caseProperties,
            expectedIds));
        Assert.False(IsClosedCatalog(
            Mutate(cases => cases.Add(cases[0]!.DeepClone())),
            formatVersion,
            caseProperties,
            expectedIds));
        Assert.False(IsClosedCatalog(
            Mutate(cases =>
            {
                var added = cases[0]!.DeepClone().AsObject();
                added["caseId"] = "unexpected.added-case";
                cases.Add(added);
            }),
            formatVersion,
            caseProperties,
            expectedIds));
        Assert.False(IsClosedCatalog(
            Mutate(cases =>
            {
                var reversed = cases
                    .Select(row => row!.DeepClone())
                    .Reverse()
                    .ToArray();
                cases.Clear();
                foreach (var row in reversed)
                {
                    cases.Add(row);
                }
            }),
            formatVersion,
            caseProperties,
            expectedIds));
    }

    private static bool IsClosedCatalog(
        JsonObject value,
        string formatVersion,
        IReadOnlyList<string> caseProperties,
        IReadOnlyList<string> expectedIds)
    {
        if (!value.Select(property => property.Key)
                .SequenceEqual(["formatVersion", "cases"], StringComparer.Ordinal)
            || value["formatVersion"]?.GetValue<string>() != formatVersion
            || value["cases"] is not JsonArray cases)
        {
            return false;
        }

        var objects = cases.OfType<JsonObject>().ToArray();
        var ids = objects.Select(row => row["caseId"]?.GetValue<string>()).ToArray();
        return objects.Length == cases.Count
            && objects.All(row => row.Select(property => property.Key)
                .SequenceEqual(caseProperties, StringComparer.Ordinal))
            && objects.All(row => row["outcome"]?.GetValue<string>()
                is "accept" or "reject")
            && ids.All(id => id is not null)
            && ids.Distinct(StringComparer.Ordinal).Count() == ids.Length
            && ids.SequenceEqual(expectedIds, StringComparer.Ordinal);
    }

    private static bool IsClosedClassificationCatalog(JsonObject value)
    {
        try
        {
            if (!IsClosedCatalog(
                    value,
                    "contractscribe-m1-classification-origin-skip-v1",
                    [
                        "caseId",
                        "conditions",
                        "record",
                        "recordOutcome",
                        "selectionOutcome",
                        "outcome"
                    ],
                    ExpectedClassificationOriginSkipIds))
            {
                return false;
            }

            return value["cases"]!.AsArray().All(node =>
            {
                var row = node!.AsObject();
                var conditions = row["conditions"]!.AsArray()
                    .Select(condition => condition!.GetValue<string>())
                    .ToArray();
                var allowedConditions = conditions.SequenceEqual(
                        ["generated-provenance-unavailable"],
                        StringComparer.Ordinal)
                    || conditions.SequenceEqual(
                        ["semantic-context-unavailable"],
                        StringComparer.Ordinal)
                    || conditions.SequenceEqual(
                        ["documentation-comment-id-unavailable"],
                        StringComparer.Ordinal)
                    || conditions.SequenceEqual(
                        [
                            "generated-provenance-unavailable",
                            "semantic-context-unavailable"
                        ],
                        StringComparer.Ordinal);
                var recordOutcome =
                    row["recordOutcome"]?.GetValue<string>();
                var selectionOutcome =
                    row["selectionOutcome"]?.GetValue<string>();
                var combinedOutcome = recordOutcome == "accept"
                    && selectionOutcome == "accept"
                    ? "accept"
                    : "reject";
                return allowedConditions
                    && recordOutcome is "accept" or "reject"
                    && selectionOutcome is "accept" or "reject"
                    && row["outcome"]?.GetValue<string>() == combinedOutcome
                    && IsClosedClassificationRecord(row["record"]!.AsObject());
            });
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or KeyNotFoundException)
        {
            return false;
        }
    }

    private static bool IsClosedClassificationRecord(JsonObject record)
    {
        var recordType = record["recordType"]?.GetValue<string>();
        if (recordType == "TargetClassification")
        {
            return HasExactProperties(
                    record,
                    "recordType",
                    "symbolRef",
                    "primaryKind",
                    "traits",
                    "origin",
                    "supportStatus",
                    "skipReason")
                && record["symbolRef"] is JsonObject symbolRef
                && HasExactProperties(
                    symbolRef,
                    "compilationContextRef",
                    "documentationCommentId")
                && record["traits"] is JsonArray;
        }

        if (recordType == "ComponentClassification")
        {
            return HasExactProperties(
                    record,
                    "recordType",
                    "parentSymbolRef",
                    "componentKind",
                    "identity",
                    "origin",
                    "supportStatus",
                    "skipReason")
                && record["parentSymbolRef"] is JsonObject parentSymbolRef
                && HasExactProperties(
                    parentSymbolRef,
                    "compilationContextRef",
                    "documentationCommentId");
        }

        return recordType == "UnresolvedClassification"
            && HasExactProperties(
                record,
                "recordType",
                "compilationContextRef",
                "origin",
                "supportStatus",
                "skipReason",
                "candidateLocator")
            && record["candidateLocator"] is JsonObject candidateLocator
            && HasExactProperties(candidateLocator, "synthetic")
            && candidateLocator["synthetic"] is JsonObject synthetic
            && HasExactProperties(synthetic, "fixtureId");
    }

    private static bool IsClosedLocatorCatalog(JsonObject value)
    {
        try
        {
            if (!IsClosedCatalog(
                    value,
                    "contractscribe-m1-repository-candidate-locator-v1",
                    ["caseId", "locator", "outcome"],
                    ExpectedRepositoryCandidateLocatorIds))
            {
                return false;
            }

            return value["cases"]!.AsArray().All(node =>
            {
                var locator = node!["locator"]!.AsObject();
                if (locator.Count != 1)
                {
                    return false;
                }

                if (locator["repository"] is JsonObject repository)
                {
                    if (!HasExactProperties(repository, "path")
                        && !HasExactProperties(repository, "path", "span"))
                    {
                        return false;
                    }

                    return repository["span"] is not JsonObject span
                        || HasExactProperties(span, "start", "end");
                }

                return locator["generatedSource"] is JsonObject generatedSource
                        && HasExactProperties(
                            generatedSource,
                            "generatorId",
                            "hintNameId")
                    || locator["toolGenerated"] is JsonObject toolGenerated
                        && HasExactProperties(
                            toolGenerated,
                            "producerId",
                            "outputId")
                    || locator["synthetic"] is JsonObject synthetic
                        && HasExactProperties(synthetic, "fixtureId");
            });
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or KeyNotFoundException)
        {
            return false;
        }
    }

    private static bool ConditionsSelectRecord(JsonElement row)
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

    private static bool HasExactProperties(
        JsonObject value,
        params string[] properties) =>
        value.Select(property => property.Key)
            .SequenceEqual(properties, StringComparer.Ordinal);

    private static void AssertStrictJsonRejections()
    {
        Assert.Throws<InvalidDataException>(() =>
            ParseStrict(Encoding.UTF8.GetBytes("{\"a\":1,\"a\":2}")));
        Assert.Throws<InvalidDataException>(() =>
            ParseStrict([0xef, 0xbb, 0xbf, (byte)'{', (byte)'}']));
        Assert.Throws<DecoderFallbackException>(() =>
            ParseStrict([0xff]));
    }

    private static SortedDictionary<string, string> ExpectedCurrentInputs()
    {
        var paths = new HashSet<string>(StringComparer.Ordinal)
        {
            "docs/20_architecture/contracts/audit-result-v1.md",
            "docs/20_architecture/contracts/policy-configuration-v1.md",
            "docs/20_architecture/contracts/pre-release-v1-baseline.md",
            "docs/20_architecture/contracts/symbol-evidence-taxonomy-v1.md",
            "docs/20_architecture/decisions/0003-target-profiles-and-documentation-observation.md",
            "tests/ContractScribe.ContractBaselineProbe/AuditResultCanonicalizer.cs",
            "tests/ContractScribe.ContractBaselineProbe/ClassificationConformanceOracle.cs",
            "tests/ContractScribe.HostValidation/AuditResultSemanticValidator.cs",
            "tests/ContractScribe.Tests/AuditResultConformance.cs",
            "tests/ContractScribe.Tests/AuditResultContractTests.cs",
            "tests/ContractScribe.Tests/M1ContractBaselineTests.cs",
            "tests/ContractScribe.Tests/M1HostValidationProtocolTests.cs",
            "tests/ContractScribe.Tests/M1TargetObservationDecisionTests.cs",
            "tests/ContractScribe.Tests/PolicyConfigurationConformanceTests.cs",
            "tests/ContractScribe.Tests/SymbolEvidenceTaxonomyContractTests.cs"
        };
        foreach (var fullRoot in new[]
            {
                "schemas/audit-result",
                "schemas/policy-configuration",
                "schemas/symbol-evidence-taxonomy",
                "tests/fixtures/audit-result/v1",
                "tests/fixtures/m1-target-observation",
                "tests/fixtures/policy-configuration/v1",
                "tests/fixtures/symbol-evidence-taxonomy/v1",
                "tests/fixtures/m1-contract-baseline/v1"
            }
            .Select(relativeRoot => Path.Join(
                Root,
                relativeRoot.Replace('/', Path.DirectorySeparatorChar))))
        {
            foreach (var relative in Directory.EnumerateFiles(
                    fullRoot,
                    "*",
                    SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(Root, file)
                    .Replace(Path.DirectorySeparatorChar, '/'))
                .Where(relative => relative
                    != "tests/fixtures/m1-contract-baseline/v1/manifest.json"))
            {
                paths.Add(relative);
            }
        }

        return new SortedDictionary<string, string>(
            paths.ToDictionary(
                path => path,
                path => Sha256(Path.Join(
                    Root,
                    path.Replace('/', Path.DirectorySeparatorChar))),
                StringComparer.Ordinal),
            StringComparer.Ordinal);
    }

    private static bool IsValidSuccessorManifest(
        JsonObject value,
        IReadOnlyDictionary<string, string> expectedInputs)
    {
        try
        {
            if (!HasExactProperties(
                    value,
                    "schemaVersion",
                    "contractRevision",
                    "inventory",
                    "profiles",
                    "predecessor",
                    "currentInputs",
                    "fixtures",
                    "implementationDisposition")
                || value["schemaVersion"]?.GetValue<int>() != 1
                || value["contractRevision"]?.GetValue<string>()
                    != "issue-55-classification-origin-closure-v1"
                || value["inventory"]?.GetValue<string>()
                    != "docs/20_architecture/contracts/pre-release-v1-baseline.md"
                || value["profiles"] is not JsonArray profiles
                || !profiles.Select(profile => profile?.GetValue<string>())
                    .SequenceEqual(
                        [
                            "profile.external-api",
                            "profile.assembly-visible"
                        ],
                        StringComparer.Ordinal)
                || value["predecessor"] is not JsonObject predecessor
                || !HasExactProperties(
                    predecessor,
                    "coordinatingIssue",
                    "contractRevision",
                    "mergeCommit",
                    "contractManifest",
                    "contractManifestSha256")
                || predecessor["coordinatingIssue"]?.GetValue<string>()
                    != "https://github.com/SolusQuest/contract-scribe/issues/35"
                || predecessor["contractRevision"]?.GetValue<string>()
                    != "issue-35-pre-release-v1"
                || predecessor["mergeCommit"]?.GetValue<string>()
                    != "bb4654edc180e2953dda6b89a29211b18778b78e"
                || predecessor["contractManifest"]?.GetValue<string>()
                    != "tests/fixtures/m1-contract-baseline/v1/manifest.json"
                || predecessor["contractManifestSha256"]?.GetValue<string>()
                    != "2872387ce9cfd8578c8f473ec26ab9f10dd44381edfbc0248e6fa370d797ab31"
                || value["currentInputs"] is not JsonObject inputs
                || value["fixtures"] is not JsonObject fixtures
                || !HasExactProperties(
                    fixtures,
                    "policy",
                    "profiles",
                    "generatedIdentity",
                    "auditAuthority",
                    "classificationOriginSkip",
                    "repositoryCandidateLocator",
                    "rowCrosswalk",
                    "processReplay")
                || fixtures["policy"]?.GetValue<string>()
                    != "tests/fixtures/policy-configuration/v1/cases.json"
                || fixtures["profiles"]?.GetValue<string>()
                    != "tests/fixtures/m1-contract-baseline/v1/profile-cases.json"
                || fixtures["generatedIdentity"]?.GetValue<string>()
                    != "tests/fixtures/m1-contract-baseline/v1/generated-identity-vectors.json"
                || fixtures["auditAuthority"]?.GetValue<string>()
                    != "tests/fixtures/m1-contract-baseline/v1/audit-authority-cases.json"
                || fixtures["classificationOriginSkip"]?.GetValue<string>()
                    != "tests/fixtures/m1-contract-baseline/v1/classification-origin-skip-vectors.json"
                || fixtures["repositoryCandidateLocator"]?.GetValue<string>()
                    != "tests/fixtures/m1-contract-baseline/v1/repository-candidate-locator-vectors.json"
                || fixtures["rowCrosswalk"]?.GetValue<string>()
                    != "tests/fixtures/m1-contract-baseline/v1/row-crosswalk.json"
                || fixtures["processReplay"]?.GetValue<string>()
                    != "tests/fixtures/m1-contract-baseline/v1/process-replay-input.json"
                || value["implementationDisposition"] is not JsonObject disposition
                || !HasExactProperties(
                    disposition,
                    "completedImplementationIssues",
                    "activeImplementationIssues",
                    "activeValidationIssues")
                || !MatchesIntegerArray(
                    disposition["completedImplementationIssues"],
                    36)
                || !MatchesIntegerArray(
                    disposition["activeImplementationIssues"],
                    37,
                    38,
                    39,
                    40)
                || !MatchesIntegerArray(
                    disposition["activeValidationIssues"],
                    41))
            {
                return false;
            }

            var actual = inputs.ToArray();
            return actual.Select(property => property.Key)
                    .SequenceEqual(expectedInputs.Keys, StringComparer.Ordinal)
                && actual.All(property =>
                    property.Value?.GetValue<string>()
                        == expectedInputs[property.Key]);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or KeyNotFoundException)
        {
            return false;
        }
    }

    private static bool MatchesIntegerArray(
        JsonNode? value,
        params int[] expected) =>
        value is JsonArray array
        && array.Select(item => item?.GetValue<int>())
            .SequenceEqual(expected.Select(number => (int?)number));

    private static bool AreAffectedCrosswalkRowsExact(JsonObject root)
    {
        try
        {
            if (!HasExactProperties(
                    root,
                    "schemaVersion",
                    "sourceDecision",
                    "rows")
                || root["schemaVersion"]?.GetValue<int>() != 1
                || root["sourceDecision"]?.GetValue<string>()
                    != "docs/20_architecture/decisions/0003-target-profiles-and-documentation-observation.md"
                || root["rows"] is not JsonArray rows)
            {
                return false;
            }

            var expectedKeys = new[]
            {
                "failureCases/failure.documentation-id-unknown-origin",
                "failureCases/failure.generated-provenance",
                "failureCases/failure.semantic-context",
                "failureCases/failure.unrepresentable-generated"
            };
            return expectedKeys
                .Select(rowKey => rows.OfType<JsonObject>().Single(candidate =>
                    candidate["rowKey"]?.GetValue<string>() == rowKey))
                .All(row => HasExactProperties(
                        row,
                        "rowKey",
                        "disposition",
                        "normativeSection",
                        "schemaOrRegistry",
                        "validFixture",
                        "invalidOrMutation",
                        "oracle",
                        "downstreamIssue")
                    && row["disposition"]?.GetValue<string>() == "amended"
                    && row["normativeSection"]?.GetValue<string>()
                        == "docs/20_architecture/contracts/symbol-evidence-taxonomy-v1.md"
                    && row["schemaOrRegistry"]?.GetValue<string>()
                        == "schemas/symbol-evidence-taxonomy/v1.registry.json"
                    && row["validFixture"]?.GetValue<string>()
                        == "tests/fixtures/m1-contract-baseline/v1/classification-origin-skip-vectors.json"
                    && row["invalidOrMutation"]?.GetValue<string>()
                        == "tests/fixtures/m1-contract-baseline/v1/classification-origin-skip-vectors.json"
                    && row["oracle"]?.GetValue<string>()
                        == "tests/ContractScribe.ContractBaselineProbe/ClassificationConformanceOracle.cs"
                    && row["downstreamIssue"]?.GetValue<int>() == 37);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or KeyNotFoundException)
        {
            return false;
        }
    }

    private static JsonObject FindCrosswalkRow(
        JsonObject root,
        string rowKey) =>
        root["rows"]!.AsArray().OfType<JsonObject>().Single(row =>
            row["rowKey"]?.GetValue<string>() == rowKey);

    private static JsonDocument Load(string name) =>
        ParseStrict(File.ReadAllBytes(Path.Join(FixtureRoot, name)));

    private static JsonDocument ParseStrict(byte[] payload)
    {
        if (payload.Length >= 3
            && payload[0] == 0xef
            && payload[1] == 0xbb
            && payload[2] == 0xbf)
        {
            throw new InvalidDataException("UTF-8 BOM is forbidden.");
        }

        var text = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true).GetString(payload);
        var document = JsonDocument.Parse(
            text,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
        try
        {
            RejectDuplicateProperties(document.RootElement);
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException(
                        $"Duplicate JSON property '{property.Name}'.");
                }

                RejectDuplicateProperties(property.Value);
            }

            return;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                RejectDuplicateProperties(item);
            }
        }
    }

    private static JsonElement Element(JsonNode node) => node.Deserialize<JsonElement>();
    private static string Sha256(string path) => Sha256(File.ReadAllBytes(path));
    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Join(current.FullName, "ContractScribe.slnx"))) return current.FullName;
            current = current.Parent;
        }
        throw new InvalidOperationException("Could not find the repository root.");
    }
}
