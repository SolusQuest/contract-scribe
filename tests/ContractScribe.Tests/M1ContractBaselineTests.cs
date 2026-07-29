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
        Assert.Equal(ids.Order(StringComparer.Ordinal), ids);
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(cases, row => Assert.Equal(
            new[] { "caseId", "conditions", "record", "outcome" },
            row.EnumerateObject().Select(property => property.Name)));
        AssertClosedCatalogMutations(
            root,
            "contractscribe-m1-classification-origin-skip-v1",
            ["caseId", "conditions", "record", "outcome"],
            ids);

        var taxonomyOracle = ClassificationConformanceOracle.Load(Root);
        foreach (var row in cases)
        {
            var expected = row.GetProperty("outcome").GetString() == "accept";
            var record = row.GetProperty("record");
            Assert.Equal(expected, taxonomyOracle.IsValidRecord(record));
            Assert.Equal(expected, IsAuditClassificationValid(record));
        }

        var precedence = cases.Single(row =>
            row.GetProperty("caseId").GetString()
                == "target.generated-and-semantic.generated-precedence");
        Assert.Equal(
            new[] { "generated-provenance-unavailable", "semantic-context-unavailable" },
            precedence.GetProperty("conditions").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(
            "skip.unavailable.generated-provenance",
            precedence.GetProperty("record").GetProperty("skipReason").GetString());
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
        Assert.Equal(ids.Order(StringComparer.Ordinal), ids);
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(cases, row => Assert.Equal(
            new[] { "caseId", "locator", "outcome" },
            row.EnumerateObject().Select(property => property.Name)));
        AssertClosedCatalogMutations(
            root,
            "contractscribe-m1-repository-candidate-locator-v1",
            ["caseId", "locator", "outcome"],
            ids);

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
    }

    [Fact]
    public void CurrentManifest_BindsTheIssue55SuccessorAndExactCurrentInputClosure()
    {
        using var manifest = Load("manifest.json");
        var root = manifest.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("issue-55-classification-origin-closure-v1", root.GetProperty("contractRevision").GetString());
        Assert.Equal(
            new[] { "profile.assembly-visible", "profile.external-api" },
            root.GetProperty("profiles").EnumerateArray().Select(value => value.GetString()).Order(StringComparer.Ordinal));

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
            disposition.GetProperty("completed").EnumerateArray().Select(value => value.GetInt32()));
        Assert.Equal(
            new[] { 37, 38, 39, 40 },
            disposition.GetProperty("activeImplementation").EnumerateArray().Select(value => value.GetInt32()));
        Assert.Equal(
            new[] { 41 },
            disposition.GetProperty("activeValidation").EnumerateArray().Select(value => value.GetInt32()));
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
            "tests/ContractScribe.Tests/M1TargetObservationDecisionTests.cs",
            "tests/ContractScribe.Tests/PolicyConfigurationConformanceTests.cs",
            "tests/ContractScribe.Tests/SymbolEvidenceTaxonomyContractTests.cs"
        };
        foreach (var relativeRoot in new[]
        {
            "schemas/audit-result",
            "schemas/policy-configuration",
            "schemas/symbol-evidence-taxonomy",
            "tests/fixtures/audit-result/v1",
            "tests/fixtures/m1-target-observation",
            "tests/fixtures/policy-configuration/v1",
            "tests/fixtures/symbol-evidence-taxonomy/v1",
            "tests/fixtures/m1-contract-baseline/v1"
        })
        {
            var fullRoot = Path.Join(
                Root,
                relativeRoot.Replace('/', Path.DirectorySeparatorChar));
            foreach (var file in Directory.EnumerateFiles(
                fullRoot,
                "*",
                SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(Root, file)
                    .Replace(Path.DirectorySeparatorChar, '/');
                if (relative
                    != "tests/fixtures/m1-contract-baseline/v1/manifest.json")
                {
                    paths.Add(relative);
                }
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
        if (!value.Select(property => property.Key).SequenceEqual(
                [
                    "schemaVersion",
                    "contractRevision",
                    "inventory",
                    "profiles",
                    "predecessor",
                    "currentInputs",
                    "fixtures",
                    "implementationDisposition"
                ],
                StringComparer.Ordinal)
            || value["schemaVersion"]?.GetValue<int>() != 1
            || value["contractRevision"]?.GetValue<string>()
                != "issue-55-classification-origin-closure-v1"
            || value["currentInputs"] is not JsonObject inputs)
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

    private static JsonDocument Load(string name) => JsonDocument.Parse(File.ReadAllText(Path.Join(FixtureRoot, name)));
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
