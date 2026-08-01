using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.HostValidation;

namespace ContractScribe.Tests;

public sealed class M1HostValidationToolchainStateTests
{
    private const string Sha =
        "1111111111111111111111111111111111111111111111111111111111111111";
    private const string HostRevision =
        "2222222222222222222222222222222222222222";
    private static readonly string Root = FindRepositoryRoot();
    private static readonly string FixtureRoot = Path.Join(
        Root,
        "tests",
        "fixtures",
        "m1-host-validation",
        "v1");
    private static readonly string SubjectSchema = Path.Join(
        Root,
        "schemas",
        "validation",
        "m1-host-validation-subject-v1.schema.json");
    private static readonly string CellEvidenceSchema = Path.Join(
        Root,
        "schemas",
        "validation",
        "m1-host-validation-cell-evidence-v1.schema.json");
    private static readonly Lazy<string> StandaloneResponseSchema = new(() =>
        MaterializeDefinitionSchema(SubjectSchema, "subjectResponse"));
    private static readonly Lazy<string> CopiedResponseSchema = new(() =>
        MaterializeDefinitionSchema(CellEvidenceSchema, "subject"));
    private static readonly string[] PostSelectionStages =
    [
        "workspace-load",
        "classification",
        "documentation-observation",
        "policy-evidence",
        "audit",
        "result-validation",
        "shutdown",
        "internal"
    ];
    private static readonly string[] FrozenCancellationVectors =
    [
        "cancellation.before-commit",
        "cancellation.after-commit",
        "cancellation.late-completion",
        "cancellation.terminal-precedence"
    ];

    [Fact]
    public void ToolchainSelection_CopiedSchemasHaveExactClosedParity()
    {
        AssertSchemaParity(
            ResponseNode("self-test.fake-subject", "invalid-input", "input", "not-selected"),
            expected: true);
        AssertSchemaParity(
            ResponseNode(
                "self-test.fake-subject",
                "environment-unavailable",
                "environment",
                "not-selected"),
            expected: true);
        AssertSchemaParity(
            ResponseNode("self-test.fake-subject", "load-failure", "workspace-load", "selected"),
            expected: true);
        AssertSchemaParity(
            ResponseNode("self-test.fake-subject", "audit-error", "audit", "selected"),
            expected: true);
        AssertSchemaParity(
            ResponseNode("self-test.fake-subject", "succeeded", null, "selected"),
            expected: true);
        AssertSchemaParity(
            ResponseNode("self-test.fake-subject", "invalid-input", "input", "selected"),
            expected: false);
        AssertSchemaParity(
            ResponseNode(
                "self-test.fake-subject",
                "environment-unavailable",
                "environment",
                "selected"),
            expected: false);
        AssertSchemaParity(
            ResponseNode("self-test.fake-subject", "load-failure", "workspace-load", "not-selected"),
            expected: false);
        AssertSchemaParity(
            ResponseNode("self-test.fake-subject", "audit-error", "audit", "not-selected"),
            expected: false);
        AssertSchemaParity(
            ResponseNode("self-test.fake-subject", "succeeded", null, "not-selected"),
            expected: false);
        AssertSchemaParity(
            ResponseNode(
                "failure.publication-invalidation",
                "publication-failure",
                "publication",
                "not-selected"),
            expected: true);
        AssertSchemaParity(
            ResponseNode(
                "failure.publication-finalization",
                "publication-failure",
                "publication",
                "selected"),
            expected: true);

        foreach (var outcome in new[] { "cancelled", "timeout" })
        {
            foreach (var stage in new[] { "input", "environment" })
            {
                AssertSchemaParity(
                    ResponseNode("self-test.fake-subject", outcome, stage, "not-selected"),
                    expected: true);
            }
            foreach (var state in new[] { "not-selected", "selected" })
            {
                AssertSchemaParity(
                    ResponseNode("self-test.fake-subject", outcome, "sdk-discovery", state),
                    expected: true);
                AssertSchemaParity(
                    ResponseNode("self-test.fake-subject", outcome, "publication", state),
                    expected: true);
            }
            foreach (var stage in new[] { "sdk-discovery", "publication" })
            {
                var nullHostFacts = ResponseNode(
                    "self-test.fake-subject",
                    outcome,
                    stage,
                    "not-selected");
                nullHostFacts["hostFacts"] = null;
                AssertSchemaParity(nullHostFacts, expected: false);
            }
            foreach (var stage in PostSelectionStages)
            {
                AssertSchemaParity(
                    ResponseNode("self-test.fake-subject", outcome, stage, "selected"),
                    expected: true);
            }
        }

        AssertSchemaParity(
            ResponseNode("self-test.fake-subject", "cancelled", "input", "selected"),
            expected: false);
        AssertSchemaParity(
            ResponseNode(
                "self-test.fake-subject",
                "timeout",
                "workspace-load",
                "not-selected"),
            expected: false);
        AssertSchemaParity(
            ResponseNode("self-test.fake-subject", "cancelled", "future-stage", "not-selected"),
            expected: false);

        var notSelectedWithIdentity = ResponseNode(
            "self-test.fake-subject",
            "cancelled",
            "input",
            "not-selected");
        notSelectedWithIdentity["hostFacts"]!["selectedSdk"] = "10.0.102";
        AssertSchemaParity(notSelectedWithIdentity, expected: false);

        var selectedMissingOne = ResponseNode(
            "self-test.fake-subject",
            "cancelled",
            "sdk-discovery",
            "selected");
        selectedMissingOne["hostFacts"]!.AsObject().Remove("selectedRuntime");
        AssertSchemaParity(selectedMissingOne, expected: false);

        var selectedMissingAll = ResponseNode(
            "self-test.fake-subject",
            "timeout",
            "sdk-discovery",
            "selected");
        var missingAllFacts = selectedMissingAll["hostFacts"]!.AsObject();
        missingAllFacts.Remove("selectedSdk");
        missingAllFacts.Remove("selectedRuntime");
        missingAllFacts.Remove("selectedMsbuild");
        AssertSchemaParity(selectedMissingAll, expected: false);

        var selectedNull = ResponseNode(
            "self-test.fake-subject",
            "cancelled",
            "sdk-discovery",
            "selected");
        selectedNull["hostFacts"]!["selectedRuntime"] = null;
        AssertSchemaParity(selectedNull, expected: false);

        var selectedEmpty = ResponseNode(
            "self-test.fake-subject",
            "cancelled",
            "sdk-discovery",
            "selected");
        selectedEmpty["hostFacts"]!["selectedRuntime"] = string.Empty;
        AssertSchemaParity(selectedEmpty, expected: false);

        var schemaValidMaterializationMismatch = ResponseNode(
            "self-test.fake-subject",
            "cancelled",
            "sdk-discovery",
            "selected");
        schemaValidMaterializationMismatch["hostFacts"]!["selectedSdk"] = "10.0.999";
        AssertSchemaParity(schemaValidMaterializationMismatch, expected: true);

        foreach (var vectorId in FrozenCancellationVectors)
        {
            foreach (var stage in new[] { "sdk-discovery", "publication" })
            {
                AssertSchemaParity(
                    ResponseNode(vectorId, "cancelled", stage, "selected"),
                    expected: true);
                AssertSchemaParity(
                    ResponseNode(vectorId, "cancelled", stage, "not-selected"),
                    expected: false);
            }
            foreach (var stage in new[] { "input", "environment", "future-stage" })
            {
                AssertSchemaParity(
                    ResponseNode(vectorId, "cancelled", stage, "selected"),
                    expected: false);
            }
        }
    }

    [Fact]
    public void ToolchainSelection_SharedSemanticsRejectReversalsAndMaterializationMismatch()
    {
        var materialization = ExactMaterialization();
        AssertSemanticAccepted("self-test.fake-subject", "invalid-input", "input", NotSelectedFacts(), materialization);
        AssertSemanticAccepted(
            "self-test.fake-subject",
            "environment-unavailable",
            "environment",
            NotSelectedFacts(),
            materialization);
        AssertSemanticAccepted(
            "self-test.fake-subject",
            "load-failure",
            "workspace-load",
            SelectedFacts(),
            materialization);
        AssertSemanticAccepted(
            "self-test.fake-subject",
            "audit-error",
            "audit",
            SelectedFacts(),
            materialization);
        AssertSemanticAccepted(
            "self-test.fake-subject",
            "succeeded",
            null,
            SelectedFacts(),
            materialization);
        AssertSemanticAccepted(
            "failure.publication-invalidation",
            "publication-failure",
            "publication",
            NotSelectedFacts(),
            materialization);
        AssertSemanticAccepted(
            "failure.publication-finalization",
            "publication-failure",
            "publication",
            SelectedFacts(),
            materialization);

        foreach (var outcome in new[] { "cancelled", "timeout" })
        {
            foreach (var stage in new[] { "input", "environment" })
            {
                AssertSemanticAccepted(
                    "self-test.fake-subject",
                    outcome,
                    stage,
                    NotSelectedFacts(),
                    materialization);
            }
            foreach (var state in new[] { "not-selected", "selected" })
            {
                var facts = state == "selected" ? SelectedFacts() : NotSelectedFacts();
                AssertSemanticAccepted(
                    "self-test.fake-subject",
                    outcome,
                    "sdk-discovery",
                    facts,
                    materialization);
                AssertSemanticAccepted(
                    "self-test.fake-subject",
                    outcome,
                    "publication",
                    facts,
                    materialization);
            }
            foreach (var stage in PostSelectionStages)
            {
                AssertSemanticAccepted(
                    "self-test.fake-subject",
                    outcome,
                    stage,
                    SelectedFacts(),
                    materialization);
            }
        }

        AssertSemanticRejected(
            "self-test.fake-subject",
            "cancelled",
            "input",
            SelectedFacts(),
            materialization);
        AssertSemanticRejected(
            "self-test.fake-subject",
            "timeout",
            "workspace-load",
            NotSelectedFacts(),
            materialization);
        AssertSemanticRejected(
            "self-test.fake-subject",
            "cancelled",
            "future-stage",
            NotSelectedFacts(),
            materialization);
        AssertSemanticRejected(
            "self-test.fake-subject",
            "cancelled",
            "sdk-discovery",
            NotSelectedFacts() with { SelectedSdk = "10.0.102" },
            materialization);
        AssertSemanticRejected(
            "self-test.fake-subject",
            "cancelled",
            "sdk-discovery",
            SelectedFacts() with { SelectedRuntime = null },
            materialization);
        AssertSemanticRejected(
            "self-test.fake-subject",
            "cancelled",
            "sdk-discovery",
            SelectedFacts() with { SelectedRuntime = string.Empty },
            materialization);
        AssertSemanticRejected(
            "self-test.fake-subject",
            "cancelled",
            "sdk-discovery",
            SelectedFacts() with { SelectedSdk = "10.0.999" },
            materialization);

        foreach (var vectorId in FrozenCancellationVectors)
        {
            foreach (var stage in new[] { "sdk-discovery", "publication" })
            {
                AssertSemanticAccepted(
                    vectorId,
                    "cancelled",
                    stage,
                    SelectedFacts(),
                    materialization);
                AssertSemanticRejected(
                    vectorId,
                    "cancelled",
                    stage,
                    NotSelectedFacts(),
                    materialization);
            }
            foreach (var stage in new[] { "input", "environment", "future-stage" })
            {
                AssertSemanticRejected(
                    vectorId,
                    "cancelled",
                    stage,
                    SelectedFacts(),
                    materialization);
            }
        }
    }

    [Fact]
    public void ToolchainSelection_SelfTestRegistryRowsAreExactAndFixtureOnly()
    {
        var registryPath = Path.Join(FixtureRoot, "self-test-host-failure-registry.json");
        SchemaValidation.Validate(
            registryPath,
            Path.Join(Root, "schemas", "validation", "m1-host-failure-registry-v1.schema.json"),
            requireCanonical: true);
        var registrySha = CanonicalJson.Sha256File(registryPath);
        var cases = new[]
        {
            ("host.test-only.cancelled-sdk-preselection", "cancelled", "not-selected"),
            ("host.test-only.timeout-sdk-preselection", "timeout", "not-selected"),
            ("host.test-only.cancelled-sdk-postselection", "cancelled", "selected"),
            ("host.test-only.timeout-sdk-postselection", "timeout", "selected")
        };

        using var registry = JsonDocument.Parse(File.ReadAllText(registryPath));
        var registeredCodes = registry.RootElement.GetProperty("entries")
            .EnumerateArray()
            .Select(entry => entry.GetProperty("code").GetString())
            .ToHashSet(StringComparer.Ordinal);
        foreach (var (code, outcome, state) in cases)
        {
            Assert.StartsWith("host.test-only.", code, StringComparison.Ordinal);
            Assert.Contains(code, registeredCodes);
            var subject = BuildSubject(
                "self-test.fake-subject",
                outcome,
                "sdk-discovery",
                state,
                code,
                registrySha);
            RunSemantics.ValidateSelfTestFailureRegistryRow(Root, subject);
        }

        var testOnlyCodes = cases.Select(item => item.Item1).ToArray();
        foreach (var productionRegistry in Directory.EnumerateFiles(
                     Path.Join(Root, "src"),
                     "host-failure-registry-v1.json",
                     SearchOption.AllDirectories))
        {
            var productionText = File.ReadAllText(productionRegistry);
            Assert.All(testOnlyCodes, code => Assert.DoesNotContain(code, productionText, StringComparison.Ordinal));
        }
        var productionTemplate = File.ReadAllText(Path.Join(FixtureRoot, "production-subject.template.json"));
        var executionTemplate = File.ReadAllText(Path.Join(FixtureRoot, "execution-subject.template.json"));
        Assert.All(testOnlyCodes, code =>
        {
            Assert.DoesNotContain(code, productionTemplate, StringComparison.Ordinal);
            Assert.DoesNotContain(code, executionTemplate, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ToolchainSelection_LayeredMutationCorpusIsExecutable()
    {
        using var corpus = JsonDocument.Parse(File.ReadAllText(Path.Join(
            FixtureRoot,
            "evidence-mutation-corpus.json")));
        var cases = corpus.RootElement.GetProperty("toolchainSelectionCases")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(16, cases.Length);
        Assert.Equal(
            cases.Length,
            cases.Select(item => item.GetProperty("caseId").GetString())
                .Distinct(StringComparer.Ordinal)
                .Count());

        foreach (var item in cases)
        {
            var layer = item.GetProperty("targetLayer").GetString();
            var operation = item.GetProperty("operation").GetString()
                ?? throw new InvalidOperationException("Mutation operation is required.");
            var expectedCode = item.GetProperty("expectedCode").ValueKind == JsonValueKind.Null
                ? null
                : item.GetProperty("expectedCode").GetString();
            switch (layer)
            {
                case "schema":
                    AssertSchemaParity(
                        ExecuteSchemaMutation(operation),
                        item.GetProperty("schemaAccepted").GetBoolean());
                    Assert.Null(expectedCode);
                    break;
                case "semantic":
                    var semanticNode = ExecuteSchemaMutation(operation);
                    AssertSchemaParity(
                        semanticNode,
                        item.GetProperty("schemaAccepted").GetBoolean());
                    var semanticFailure = Assert.Throws<ProtocolException>(() =>
                        RunSemantics.ValidateToolchainSelectionState(
                            "self-test.fake-subject",
                            "cancelled",
                            "sdk-discovery",
                            SelectedFacts() with { SelectedSdk = "10.0.999" },
                            ExactMaterialization()));
                    Assert.Equal(expectedCode, semanticFailure.Code);
                    break;
                case "registry":
                    var registryFailure = Assert.Throws<ProtocolException>(() =>
                        RunSemantics.ValidateSelfTestFailureRegistryRow(
                            Root,
                            ExecuteRegistryMutation(operation)));
                    Assert.Equal(expectedCode, registryFailure.Code);
                    Assert.Equal(JsonValueKind.Null, item.GetProperty("schemaAccepted").ValueKind);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown mutation layer: {layer}");
            }
        }
    }

    private static JsonObject ExecuteSchemaMutation(string operation)
    {
        JsonObject result;
        switch (operation)
        {
            case "mark-input-selected":
                return ResponseNode("self-test.fake-subject", "cancelled", "input", "selected");
            case "mark-workspace-not-selected":
                return ResponseNode(
                    "self-test.fake-subject",
                    "timeout",
                    "workspace-load",
                    "not-selected");
            case "replace-stage-with-unknown":
                return ResponseNode(
                    "self-test.fake-subject",
                    "cancelled",
                    "future-stage",
                    "not-selected");
            case "add-sdk-to-not-selected":
                result = ResponseNode(
                    "self-test.fake-subject",
                    "cancelled",
                    "input",
                    "not-selected");
                result["hostFacts"]!["selectedSdk"] = "10.0.102";
                return result;
            case "remove-selected-runtime":
                result = ResponseNode(
                    "self-test.fake-subject",
                    "cancelled",
                    "sdk-discovery",
                    "selected");
                result["hostFacts"]!.AsObject().Remove("selectedRuntime");
                return result;
            case "null-selected-runtime":
                result = ResponseNode(
                    "self-test.fake-subject",
                    "cancelled",
                    "sdk-discovery",
                    "selected");
                result["hostFacts"]!["selectedRuntime"] = null;
                return result;
            case "empty-selected-runtime":
                result = ResponseNode(
                    "self-test.fake-subject",
                    "cancelled",
                    "sdk-discovery",
                    "selected");
                result["hostFacts"]!["selectedRuntime"] = string.Empty;
                return result;
            case "mark-frozen-sdk-not-selected":
                return ResponseNode(
                    "cancellation.before-commit",
                    "cancelled",
                    "sdk-discovery",
                    "not-selected");
            case "mark-frozen-publication-not-selected":
                return ResponseNode(
                    "cancellation.terminal-precedence",
                    "cancelled",
                    "publication",
                    "not-selected");
            case "replace-selected-sdk-with-other-safe-token":
                result = ResponseNode(
                    "self-test.fake-subject",
                    "cancelled",
                    "sdk-discovery",
                    "selected");
                result["hostFacts"]!["selectedSdk"] = "10.0.999";
                return result;
            default:
                throw new InvalidOperationException($"Unknown schema mutation: {operation}");
        }
    }

    private static SubjectResponse ExecuteRegistryMutation(string operation)
    {
        var registrySha = CanonicalJson.Sha256File(Path.Join(
            FixtureRoot,
            "self-test-host-failure-registry.json"));
        var subject = BuildSubject(
            "self-test.fake-subject",
            "cancelled",
            "sdk-discovery",
            "selected",
            "host.test-only.cancelled-sdk-postselection",
            registrySha);
        return operation switch
        {
            "replace-response-registry-digest" => subject with
            {
                FailureRegistryIdentity = Sha
            },
            "replace-host-registry-digest" => subject with
            {
                HostFacts = subject.HostFacts! with { FailureRegistrySha256 = Sha }
            },
            "replace-registry-code" => subject with
            {
                FailureCode = "host.test-only.unknown-sdk"
            },
            "replace-registry-outcome" => subject with
            {
                ExecutionOutcome = "timeout"
            },
            "replace-registry-stage" => subject with
            {
                FailureStage = "audit"
            },
            "replace-registry-terminal" => subject with
            {
                TerminalState = "pending"
            },
            _ => throw new InvalidOperationException($"Unknown registry mutation: {operation}")
        };
    }

    private static void AssertSchemaParity(JsonObject response, bool expected)
    {
        var standalone = SchemaAccepts(response, SubjectSchema);
        var copied = SchemaAccepts(response, CellEvidenceSchema);
        Assert.Equal(standalone, copied);
        Assert.Equal(expected, standalone);
    }

    private static bool SchemaAccepts(JsonObject response, string schemaPath)
    {
        var path = Path.Join(
            Path.GetTempPath(),
            $"contractscribe-toolchain-state-{Guid.NewGuid():N}.json");
        try
        {
            using (var document = JsonDocument.Parse(response.ToJsonString()))
            {
                File.WriteAllBytes(
                    path,
                    CanonicalJson.SerializeCanonical(document.RootElement));
            }
            SchemaValidation.Validate(
                path,
                schemaPath == SubjectSchema
                    ? StandaloneResponseSchema.Value
                    : CopiedResponseSchema.Value,
                requireCanonical: true);
            return true;
        }
        catch (ProtocolException)
        {
            return false;
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string MaterializeDefinitionSchema(
        string schemaPath,
        string definition)
    {
        using var source = JsonDocument.Parse(File.ReadAllText(schemaPath));
        var root = JsonNode.Parse(source.RootElement.GetRawText())!.AsObject();
        var schema = root["$defs"]![definition]!.DeepClone().AsObject();
        schema["$schema"] = "https://json-schema.org/draft/2020-12/schema";
        schema["$defs"] = root["$defs"]!.DeepClone();
        var path = Path.Join(
            Path.GetTempPath(),
            $"contractscribe-toolchain-state-schema-{Guid.NewGuid():N}.json");
        using (var materialized = JsonDocument.Parse(schema.ToJsonString()))
        {
            File.WriteAllBytes(
                path,
                CanonicalJson.SerializeCanonical(materialized.RootElement));
        }
        return path;
    }

    private static void AssertSemanticAccepted(
        string vectorId,
        string? executionOutcome,
        string? failureStage,
        HostObservationFacts facts,
        CellMaterialization materialization) =>
        RunSemantics.ValidateToolchainSelectionState(
            vectorId,
            executionOutcome,
            failureStage,
            facts,
            materialization);

    private static void AssertSemanticRejected(
        string vectorId,
        string? executionOutcome,
        string? failureStage,
        HostObservationFacts facts,
        CellMaterialization materialization)
    {
        var failure = Assert.Throws<ProtocolException>(() =>
            RunSemantics.ValidateToolchainSelectionState(
                vectorId,
                executionOutcome,
                failureStage,
                facts,
                materialization));
        Assert.Equal("HV252_TOOLCHAIN_SELECTION_STATE", failure.Code);
    }

    private static JsonObject ResponseNode(
        string vectorId,
        string executionOutcome,
        string? failureStage,
        string selectionState) =>
        JsonNode.Parse(Encoding.UTF8.GetString(CanonicalJson.SerializeCanonical(BuildSubject(
            vectorId,
            executionOutcome,
            failureStage,
            selectionState,
            FailureCode(vectorId, executionOutcome, selectionState),
            Sha))))!.AsObject();

    private static SubjectResponse BuildSubject(
        string vectorId,
        string executionOutcome,
        string? failureStage,
        string selectionState,
        string failureCode,
        string registrySha)
    {
        var succeeded = executionOutcome == "succeeded";
        var selected = selectionState == "selected";
        var canonical = succeeded
            ? new CanonicalResultCommitment(
                Sha,
                1,
                "canonical-json-utf8-no-bom-single-lf",
                true)
            : null;
        var facts = new HostObservationFacts(
            $"source.{Sha}",
            HostRevision,
            Sha,
            registrySha,
            Sha,
            selected ? "10.0.102" : null,
            selected ? "10.0.0" : null,
            selected ? "18.0.0" : null,
            succeeded
                ? []
                : [new NormalizedDiagnosticFact(failureCode, failureStage!)],
            succeeded
                ? new OutputCommitFact("committed", Sha)
                : new OutputCommitFact("not-committed", null),
            [],
            null,
            selectionState);
        return new SubjectResponse(
            "contractscribe-m1-host-validation-subject-response-v1",
            vectorId,
            "run-1",
            "started",
            "normal",
            succeeded ? "compliant" : null,
            executionOutcome,
            succeeded ? null : registrySha,
            succeeded ? null : failureCode,
            succeeded ? null : failureStage,
            "committed",
            succeeded
                ? "published"
                : executionOutcome == "publication-failure" ? "invalidated" : "absent",
            "internally-enforceable",
            "self-test.toolchain-state",
            canonical,
            facts);
    }

    private static string FailureCode(
        string vectorId,
        string executionOutcome,
        string selectionState) => vectorId switch
        {
            "failure.publication-invalidation" => "host.test-only.publication-invalidation",
            "failure.publication-finalization" => "host.test-only.publication-finalization",
            _ when executionOutcome == "succeeded" => "host.test-only.succeeded",
            _ => $"host.test-only.{executionOutcome}-{selectionState}"
        };

    private static HostObservationFacts SelectedFacts() => new(
        $"source.{Sha}",
        HostRevision,
        Sha,
        Sha,
        Sha,
        "10.0.102",
        "10.0.0",
        "18.0.0",
        [],
        new OutputCommitFact("not-committed", null),
        [],
        null,
        "selected");

    private static HostObservationFacts NotSelectedFacts() => new(
        $"source.{Sha}",
        HostRevision,
        Sha,
        Sha,
        Sha,
        null,
        null,
        null,
        [],
        new OutputCommitFact("not-committed", null),
        [],
        null,
        "not-selected");

    private static CellMaterialization ExactMaterialization() => new(
        "ubuntu-x64",
        "1",
        "https://example.invalid/jobs/1",
        "self-test",
        "linux-x64",
        "X64",
        "10.0.102",
        "10.0.0",
        "18.0.0",
        []);

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Join(current.FullName, "ContractScribe.slnx")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new InvalidOperationException("Repository root not found.");
    }
}
