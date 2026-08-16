using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Core;
using Json.Schema;

namespace ContractScribe.Tests;

public sealed class DocumentationScribeContractTests
{
    private const string AttemptId = "scribe-attempt.0123456789abcdef0123456789abcdef";
    private static readonly Lazy<JsonSchema> RequestSchema = new(() => LoadSchema("v1.request.schema.json"));
    private static readonly Lazy<JsonSchema> ResultSchema = new(() => LoadSchema("v1.run-result.schema.json"));

    [Fact]
    public void Public_family_is_cross_consistent_and_all_terminals_parse()
    {
        var requestBytes = ReadFixture("valid", "request.json");
        AssertSchemaValid(RequestSchema.Value, requestBytes, "request");
        var requestParse = DocumentationScribeValidation.ParseRequest(requestBytes);
        var request = Assert.IsType<DocumentationScribeRequest>(requestParse.Request);
        Assert.Null(requestParse.Failure);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(requestBytes)).ToLowerInvariant(),
            request.ArtifactSha256);
        Assert.True(DocumentationScribeAttemptId.TryParse(AttemptId, out var attemptId));

        var expectedKinds = new Dictionary<string, DocumentationScribeTerminalKind>(StringComparer.Ordinal)
        {
            ["proposal-result.json"] = DocumentationScribeTerminalKind.Proposal,
            ["skip-result.json"] = DocumentationScribeTerminalKind.Skip,
            ["failure-result.json"] = DocumentationScribeTerminalKind.Failure,
            ["cancelled-result.json"] = DocumentationScribeTerminalKind.Cancelled,
        };
        foreach (var pair in expectedKinds)
        {
            var resultBytes = ReadFixture("valid", pair.Key);
            AssertSchemaValid(ResultSchema.Value, resultBytes, pair.Key);
            var parsed = DocumentationScribeValidation.ParseRunResult(request, attemptId, [], resultBytes);
            var result = Assert.IsType<DocumentationScribeRunResult>(parsed.Result);
            Assert.Null(parsed.Failure);
            Assert.Equal(pair.Value, result.Terminal.Kind);
            Assert.Equal(request.ArtifactSha256, result.ScribeRequestSha256);
            Assert.Equal(attemptId, result.AttemptId);
        }

        var registry = JsonNode.Parse(ReadContractFile("v1.registry.json"))!.AsObject();
        Assert.Equal(1, registry["documentationScribeRegistryVersion"]!.GetValue<int>());
        Assert.Equal(
            Enum.GetValues<DocumentationScribeTerminalKind>()
                .Select(DocumentationScribeVocabulary.GetId)
                .Order(StringComparer.Ordinal),
            registry["terminalKinds"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.Equal(
            Enum.GetValues<DocumentationScribeContentUnitKind>()
                .Select(DocumentationScribeVocabulary.GetId)
                .Order(StringComparer.Ordinal),
            registry["contentUnitKinds"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.Equal(
            Enum.GetValues<DocumentationScribeEvidenceAuthority>()
                .Select(DocumentationScribeVocabulary.GetId),
            registry["evidenceAuthoritiesInAscendingOrder"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.Equal(
            Enum.GetValues<DocumentationScribeSkipReason>()
                .Select(DocumentationScribeVocabulary.GetId)
                .Order(StringComparer.Ordinal),
            registry["skipReasons"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.Equal(
            Enum.GetValues<DocumentationScribeFailureCode>()
                .Select(DocumentationScribeVocabulary.GetId)
                .Order(StringComparer.Ordinal),
            registry["failureCodes"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.Equal(
            Enum.GetValues<DocumentationScribeCancellationCode>()
                .Select(DocumentationScribeVocabulary.GetId)
                .Order(StringComparer.Ordinal),
            registry["cancellationCodes"]!.AsArray().Select(node => node!.GetValue<string>()));
        var toolOutcomeIds = typeof(DocumentationScribeToolOutcome)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(property => property.PropertyType == typeof(DocumentationScribeToolOutcome))
            .Select(property => ((DocumentationScribeToolOutcome)property.GetValue(null)!).Id)
            .Order(StringComparer.Ordinal);
        Assert.Equal(
            toolOutcomeIds,
            registry["toolOutcomes"]!.AsArray().Select(node => node!.GetValue<string>()));
        var schemaText = Encoding.UTF8.GetString(ReadContractFile("v1.request.schema.json"))
            + Encoding.UTF8.GetString(ReadContractFile("v1.run-result.schema.json"));
        foreach (var registeredValue in registry
            .Where(pair => pair.Key is not (
                "documentationScribeRegistryVersion" or "requestSchema" or "runResultSchema" or "toolOutcomes"))
            .SelectMany(pair => pair.Value!.AsArray())
            .Select(node => node!.GetValue<string>()))
        {
            Assert.Contains(registeredValue, schemaText, StringComparison.Ordinal);
        }
        AssertLocalReferences(JsonNode.Parse(ReadContractFile("v1.request.schema.json"))!);
        AssertLocalReferences(JsonNode.Parse(ReadContractFile("v1.run-result.schema.json"))!);
    }

    [Fact]
    public void Run_result_reuses_the_request_evidence_row_schema_and_requires_the_dynamic_overlay()
    {
        var requestSchema = JsonNode.Parse(ReadContractFile("v1.request.schema.json"))!.AsObject();
        var resultSchema = JsonNode.Parse(ReadContractFile("v1.run-result.schema.json"))!.AsObject();
        foreach (var definition in new[]
                 {
                     "repositoryPath",
                     "span",
                     "locator",
                     "symbolRef",
                     "byteCounts",
                     "targetSubject",
                     "componentSubject",
                     "evidenceReference",
                 })
        {
            Assert.True(
                JsonNode.DeepEquals(
                    requestSchema["$defs"]![definition],
                    resultSchema["$defs"]![definition]),
                definition);
        }

        var oldShape = ParseObject(ReadFixture("valid", "proposal-result.json"));
        oldShape.Remove("dynamicEvidenceReferences");
        var oldShapeBytes = Serialize(oldShape);
        Assert.False(Evaluate(ResultSchema.Value, oldShapeBytes));

        var request = ParseValidRequest(ReadFixture("valid", "request.json"));
        Assert.True(DocumentationScribeAttemptId.TryParse(AttemptId, out var attempt));
        var failure = DocumentationScribeValidation.ParseRunResult(request, attempt, [], oldShapeBytes).Failure;
        Assert.Equal("scribe.result.invalid-shape", failure?.Code);
        Assert.Equal("/dynamicEvidenceReferences", failure?.Pointer);
    }

    [Fact]
    public void Dynamic_evidence_identity_is_derived_and_the_result_overlay_is_exactly_correlated()
    {
        var request = ParseValidRequest(ReadFixture("valid", "request.json"));
        var input = DynamicEvidenceInput();

        Assert.True(DocumentationScribeValidation.TryCreateDynamicEvidenceReference(request, input, out var first));
        Assert.NotNull(first);
        Assert.Equal(
            "evidence.dynamic.ec032585c0209f0f26c4dd8a33adac8f1bea4bf3e7aeb13156d57576d068b55c",
            first.EvidenceReferenceId);
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.True(DocumentationScribeValidation.TryCreateDynamicEvidenceReference(request, input, out var second));
            Assert.Equal(first, second);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }

        var result = ParseObject(ReadFixture("valid", "proposal-result.json"));
        result["dynamicEvidenceReferences"] = new JsonArray(DynamicEvidenceJson(first));
        result["terminal"]!["contentUnits"]![0]!["evidenceReferenceIds"]!
            .AsArray().Insert(0, first.EvidenceReferenceId);
        var bytes = Serialize(result);
        AssertSchemaValid(ResultSchema.Value, bytes, "dynamic-evidence-result");
        Assert.True(DocumentationScribeAttemptId.TryParse(AttemptId, out var attempt));

        var parsed = DocumentationScribeValidation.ParseRunResult(request, attempt, [first], bytes);
        var accepted = Assert.IsType<DocumentationScribeRunResult>(parsed.Result);
        Assert.Null(parsed.Failure);
        var acceptedReference = Assert.Single(accepted.DynamicEvidenceReferences);
        Assert.Equal(first.EvidenceReferenceId, acceptedReference.EvidenceReferenceId);
        Assert.Equal(first.ContentSha256, acceptedReference.ContentSha256);
        Assert.Equal(first.ClaimCategoryIds.ToArray(), acceptedReference.ClaimCategoryIds.ToArray());

        var missingTrustedOverlay = DocumentationScribeValidation.ParseRunResult(request, attempt, [], bytes);
        Assert.Equal("scribe.result.invalid-correlation", missingTrustedOverlay.Failure?.Code);
        Assert.Equal("/dynamicEvidenceReferences", missingTrustedOverlay.Failure?.Pointer);

        var forged = ParseObject(bytes);
        forged["dynamicEvidenceReferences"]![0]!["contentSha256"] = new string('b', 64);
        var forgedParse = DocumentationScribeValidation.ParseRunResult(request, attempt, [first], Serialize(forged));
        Assert.Equal("scribe.result.invalid-correlation", forgedParse.Failure?.Code);
        Assert.Equal("/dynamicEvidenceReferences/0", forgedParse.Failure?.Pointer);

        var failureResult = ParseObject(ReadFixture("valid", "failure-result.json"));
        failureResult["dynamicEvidenceReferences"] = new JsonArray(DynamicEvidenceJson(first));
        var failureParse = DocumentationScribeValidation.ParseRunResult(
            request,
            attempt,
            [first],
            Serialize(failureResult));
        Assert.Equal("scribe.result.invalid-evidence", failureParse.Failure?.Code);
        Assert.Equal("/dynamicEvidenceReferences", failureParse.Failure?.Pointer);

        Assert.True(DocumentationScribeValidation.TryCreateDynamicEvidenceReference(
            request,
            DynamicEvidenceInput(contentSha256: new string('b', 64)),
            out var other));
        var orderedOverlay = new[] { first, other! }
            .OrderBy(item => item.EvidenceReferenceId, StringComparer.Ordinal)
            .ToImmutableArray();
        var skip = ParseObject(ReadFixture("valid", "skip-result.json"));
        skip["dynamicEvidenceReferences"] = new JsonArray(
            orderedOverlay.Select(reference => DynamicEvidenceJson(reference)).ToArray());
        skip["terminal"]!["evidenceReferenceIds"] = new JsonArray(first.EvidenceReferenceId);
        var skipBytes = Serialize(skip);
        AssertSchemaValid(ResultSchema.Value, skipBytes, "dynamic-evidence-skip");
        Assert.True(DocumentationScribeValidation.ParseRunResult(
            request,
            attempt,
            orderedOverlay,
            skipBytes).IsValid);
        var reordered = DocumentationScribeValidation.ParseRunResult(
            request,
            attempt,
            orderedOverlay.Reverse().ToImmutableArray(),
            skipBytes);
        Assert.Equal("scribe.result.invalid-correlation", reordered.Failure?.Code);
        Assert.Equal("/dynamicEvidenceReferences/0", reordered.Failure?.Pointer);
    }

    [Fact]
    public void Dynamic_evidence_factory_rejects_inert_or_uncommitted_inputs()
    {
        var request = ParseValidRequest(ReadFixture("valid", "request.json"));
        Assert.False(DocumentationScribeValidation.TryCreateDynamicEvidenceReference(
            request,
            DynamicEvidenceInput(
                locator: EvidenceInput.RepositoryLocator("src/Synthetic/Widget.cs"),
                originalUtf8ByteCount: 2,
                includedUtf8ByteCount: 1,
                isTruncated: true),
            out _));
        Assert.False(DocumentationScribeValidation.TryCreateDynamicEvidenceReference(
            request,
            DynamicEvidenceInput(
                originalUtf8ByteCount: 2,
                includedUtf8ByteCount: 1,
                isTruncated: true,
                claimCategoryIds: ["claim.behavior"]),
            out _));
        Assert.True(DocumentationScribeValidation.TryCreateDynamicEvidenceReference(
            request,
            DynamicEvidenceInput(
                originalUtf8ByteCount: 2,
                includedUtf8ByteCount: 1,
                isTruncated: true,
                claimCategoryIds: ["claim.purpose"]),
            out _));
        Assert.False(DocumentationScribeValidation.TryCreateDynamicEvidenceReference(
            request,
            DynamicEvidenceInput(locator: EvidenceInput.RepositoryLocator("../outside.cs")),
            out _));
        Assert.False(DocumentationScribeValidation.TryCreateDynamicEvidenceReference(
            request,
            DynamicEvidenceInput(
                kind: EvidenceKind.RepositoryDocumentation,
                authority: DocumentationScribeEvidenceAuthority.SourceDeclaration),
            out _));
        Assert.False(DocumentationScribeValidation.TryCreateDynamicEvidenceReference(
            request,
            DynamicEvidenceInput(claimCategoryIds: ["claim.unknown"]),
            out _));
        Assert.False(DocumentationScribeValidation.TryCreateDynamicEvidenceReference(
            request,
            new DocumentationScribeDynamicEvidenceInput(
                EvidenceInput.TargetSubject("other-context.v1", "M:Synthetic.Widget.Run(System.String)"),
                EvidenceKind.SourceDeclaration,
                EvidenceRelation.Declares,
                DocumentationScribeEvidenceAuthority.SourceDeclaration,
                EvidenceInput.RepositoryLocator("src/Synthetic/Widget.cs"),
                new string('a', 64),
                1,
                1,
                false,
                ["claim.purpose"]),
            out _));
    }

    [Fact]
    public void Invalid_fixture_manifest_has_stable_codes_and_pointers()
    {
        var request = ParseValidRequest(ReadFixture("valid", "request.json"));
        Assert.True(DocumentationScribeAttemptId.TryParse(AttemptId, out var attempt));
        using var manifest = JsonDocument.Parse(ReadFixture("invalid-cases.json"));
        foreach (var invalidCase in manifest.RootElement.GetProperty("invalidCases").EnumerateArray())
        {
            var path = invalidCase.GetProperty("path").GetString()!.Split('/');
            var artifact = invalidCase.GetProperty("artifact").GetString();
            var expectedCode = invalidCase.GetProperty("expectedCode").GetString();
            var expectedPointer = invalidCase.GetProperty("expectedPointer").GetString();
            if (artifact == "request")
            {
                var parsed = DocumentationScribeValidation.ParseRequest(ReadFixture(path));
                Assert.Null(parsed.Request);
                Assert.Equal(expectedCode, parsed.Failure?.Code);
                Assert.Equal(expectedPointer, parsed.Failure?.Pointer);
            }
            else
            {
                Assert.Equal("runResult", artifact);
                var parsed = DocumentationScribeValidation.ParseRunResult(request, attempt, [], ReadFixture(path));
                Assert.Null(parsed.Result);
                Assert.Equal(expectedCode, parsed.Failure?.Code);
                Assert.Equal(expectedPointer, parsed.Failure?.Pointer);
            }
        }
    }

    [Fact]
    public void Raw_request_boundary_rejects_bom_invalid_utf8_and_oversize()
    {
        var valid = ReadFixture("valid", "request.json");
        var bom = new byte[] { 0xef, 0xbb, 0xbf }.Concat(valid).ToArray();
        Assert.Equal(
            "scribe.request.bom-not-allowed",
            DocumentationScribeValidation.ParseRequest(bom).Failure?.Code);

        Assert.Equal(
            "scribe.request.invalid-utf8",
            DocumentationScribeValidation.ParseRequest(new byte[] { 0x7b, 0x22, 0x78, 0x22, 0x3a, 0xc3, 0x28, 0x7d }).Failure?.Code);

        var oversized = new byte[DocumentationScribeContract.MaximumArtifactUtf8Bytes + 1];
        Assert.Equal(
            "scribe.request.document-too-large",
            DocumentationScribeValidation.ParseRequest(oversized).Failure?.Code);
    }

    [Fact]
    public void Request_binds_one_target_context_components_and_budgets()
    {
        AssertRequestMutationFails(
            root => root["targets"] = new JsonArray(),
            "scribe.request.unknown-field",
            "/targets");
        AssertRequestMutationFails(
            root => root["context"]!["auditReason"] = "audit.reason.missing",
            "scribe.request.unknown-field",
            "/context/auditReason");
        AssertRequestMutationFails(
            root => root["contextReferences"]![0]!["repositoryContextRef"] = "repoctx-22222222222222222222222222222222",
            "scribe.request.stale-reference",
            "/contextReferences/0/repositoryContextRef");
        AssertRequestMutationFails(
            root => Reverse(root["target"]!["applicableComponents"]!.AsArray()),
            "scribe.request.invalid-order",
            "/target/applicableComponents/1/identity");
        AssertRequestMutationFails(
            root => root["limits"]!["maximumContextReferences"] = 0,
            "scribe.request.over-budget",
            "/contextReferences");
        AssertRequestMutationFails(
            root => root["limits"]!["maximumContextUtf8Bytes"] = 199,
            "scribe.request.over-budget",
            "/contextReferences");
        AssertRequestMutationFails(
            root => root["context"]!["inputIdentity"] = "samples/Bad\u0001.csproj",
            "scribe.request.invalid-vocabulary",
            "/context/inputIdentity");
    }

    [Fact]
    public void Reused_m1_m2_identity_locator_and_component_domains_are_exact()
    {
        var compatible = ParseObject(ReadFixture("valid", "request.json"));
        compatible["context"]!["inputIdentity"] = "samples/Synthetic.SLNX";
        compatible["contextReferences"]![0]!["path"] = "docs/contracts:v1.md";
        compatible["target"]!["symbolRef"]!["compilationContextRef"] = "0_synthetic.v1";
        compatible["evidenceReferences"]![0]!["subject"]!["parentSymbolRef"]!["compilationContextRef"] = "0_synthetic.v1";
        compatible["evidenceReferences"]![1]!["subject"]!["parentSymbolRef"]!["compilationContextRef"] = "0_synthetic.v1";
        compatible["evidenceReferences"]![2]!["subject"]!["symbolRef"]!["compilationContextRef"] = "0_synthetic.v1";
        Assert.NotNull(ParseValidRequest(Serialize(compatible)));

        static JsonObject CreateSyntheticLocator() => new()
        {
            ["synthetic"] = new JsonObject
            {
                ["fixtureId"] = "0_fixture",
            },
        };

        var syntheticRequestNode = ParseObject(ReadFixture("valid", "request.json"));
        syntheticRequestNode["target"]!["sourceCommitment"]!["locator"] = CreateSyntheticLocator();
        var syntheticRequestBytes = Serialize(syntheticRequestNode);
        AssertSchemaValid(RequestSchema.Value, syntheticRequestBytes, "m1-synthetic-locator");
        var syntheticRequest = ParseValidRequest(syntheticRequestBytes);
        var syntheticResult = ParseObject(ReadFixture("valid", "proposal-result.json"));
        syntheticResult["terminal"]!["target"]!["sourceCommitment"]!["locator"] = CreateSyntheticLocator();
        Correlate(syntheticResult, syntheticRequest.ArtifactSha256);
        var syntheticResultBytes = Serialize(syntheticResult);
        AssertSchemaValid(ResultSchema.Value, syntheticResultBytes, "m1-synthetic-result-locator");
        Assert.True(DocumentationScribeAttemptId.TryParse(AttemptId, out var syntheticAttempt));
        Assert.True(DocumentationScribeValidation.ParseRunResult(
            syntheticRequest,
            syntheticAttempt,
            [],
            syntheticResultBytes).IsValid);

        var supplementaryDocumentationId = "T:" + string.Concat(
            Enumerable.Repeat(char.ConvertFromUtf32(0x10400), 600));
        var supplementaryRequestNode = ParseObject(ReadFixture("valid", "request.json"));
        supplementaryRequestNode["target"]!["symbolRef"]!["documentationCommentId"] = supplementaryDocumentationId;
        foreach (var evidence in supplementaryRequestNode["evidenceReferences"]!.AsArray())
        {
            var subject = evidence!["subject"]!;
            var symbol = subject["symbolRef"] ?? subject["parentSymbolRef"]!;
            symbol!["documentationCommentId"] = supplementaryDocumentationId;
        }

        var supplementaryRequestBytes = Serialize(supplementaryRequestNode);
        AssertSchemaValid(RequestSchema.Value, supplementaryRequestBytes, "m1-scalar-bounded-symbol");
        var supplementaryRequest = ParseValidRequest(supplementaryRequestBytes);
        var supplementaryResult = ParseObject(ReadFixture("valid", "proposal-result.json"));
        supplementaryResult["terminal"]!["target"]!["symbolRef"]!["documentationCommentId"] = supplementaryDocumentationId;
        Correlate(supplementaryResult, supplementaryRequest.ArtifactSha256);
        var supplementaryResultBytes = Serialize(supplementaryResult);
        AssertSchemaValid(ResultSchema.Value, supplementaryResultBytes, "m1-scalar-bounded-result-symbol");
        Assert.True(DocumentationScribeValidation.ParseRunResult(
            supplementaryRequest,
            syntheticAttempt,
            [],
            supplementaryResultBytes).IsValid);

        AssertRequestMutationFails(
            root => root["context"]!["inputIdentity"] = "samples/Synthetic.txt",
            "scribe.request.invalid-vocabulary",
            "/context/inputIdentity");
        AssertRequestMutationFails(
            root => root["target"]!["symbolRef"]!["compilationContextRef"] = "_synthetic",
            "scribe.request.invalid-vocabulary",
            "/target/symbolRef/compilationContextRef");
        AssertRequestMutationFails(
            root => root["target"]!["symbolRef"]!["documentationCommentId"] = "X:Synthetic.Widget",
            "scribe.request.invalid-vocabulary",
            "/target/symbolRef/documentationCommentId");
        AssertRequestMutationFails(
            root => root["target"]!["applicableComponents"]![0]!["identity"] = "parameter/00",
            "scribe.request.invalid-vocabulary",
            "/target/applicableComponents/0/identity");
        AssertRequestMutationFails(
            root => root["target"]!["applicableComponents"]!.AsArray().Insert(1, new JsonObject
            {
                ["kind"] = "parameter",
                ["identity"] = "parameter/1",
                ["name"] = "value",
            }),
            "scribe.request.invalid-order",
            "/target/applicableComponents/1/identity");
        AssertRequestMutationFails(
            root => root["target"]!["applicableComponents"]!.AsArray().Add(new JsonObject
            {
                ["kind"] = "value",
                ["identity"] = "value",
            }),
            "scribe.request.invalid-component",
            "/target/applicableComponents/2");

        var generated = ParseObject(ReadFixture("valid", "request.json"));
        generated["target"]!["sourceCommitment"]!["locator"] = new JsonObject
        {
            ["generatedOutput"] = new JsonObject
            {
                ["producerKind"] = "source-generator",
                ["producerId"] = "sgp." + new string('a', 64),
                ["outputId"] = "sgo." + new string('b', 64),
                ["sourceSha256"] = new string('c', 64),
                ["span"] = new JsonObject
                {
                    ["start"] = 0,
                    ["end"] = 0,
                },
            },
        };
        var generatedBytes = Serialize(generated);
        AssertSchemaValid(RequestSchema.Value, generatedBytes, "generated-locator");
        Assert.NotNull(ParseValidRequest(generatedBytes));
        generated["target"]!["sourceCommitment"]!["locator"]!["generatedOutput"]!["producerId"] =
            "tgp." + new string('a', 64);
        Assert.False(Evaluate(RequestSchema.Value, Serialize(generated)));
        var generatedFailure = DocumentationScribeValidation.ParseRequest(Serialize(generated)).Failure;
        Assert.Equal("scribe.request.invalid-vocabulary", generatedFailure?.Code);
        Assert.Equal("/target/sourceCommitment/locator/generatedOutput/producerId", generatedFailure?.Pointer);
    }

    [Fact]
    public void Structured_proposals_are_complete_m2_safe_blocks()
    {
        AssertResultMutationFails(
            request => request["styleProfile"]!["summary"]!["disposition"] = "optional",
            result => result["terminal"]!["contentUnits"]!.AsArray().RemoveAt(0),
            "scribe.result.invalid-content");
        AssertResultMutationFails(
            request => request["styleProfile"]!["componentPolicies"]![1]!["disposition"] = "optional",
            result => result["terminal"]!["contentUnits"]!.AsArray().RemoveAt(2),
            "scribe.result.invalid-content");
        AssertResultMutationFails(
            null,
            result => result["terminal"]!["contentUnits"]![0]!["lines"]![0] = "Uses <see cref=\"T:System.String\"/>.",
            "scribe.result.invalid-content");
        AssertResultMutationFails(
            null,
            result => result["terminal"]!["contentUnits"]![0]!["lines"]![0] = "Uses &amp; escaping.",
            "scribe.result.invalid-content");
        AssertResultMutationFails(
            null,
            result => result["terminal"]!["contentUnits"]![0]!["lines"]![0] = "<é/>",
            "scribe.result.invalid-content");
        AssertResultMutationFails(
            null,
            result => result["terminal"]!["contentUnits"]![0]!["lines"]![0] = "&é;",
            "scribe.result.invalid-content");
        AssertResultMutationFails(
            null,
            result => result["terminal"]!["contentUnits"]![0]!["lines"]![0] = new string('a', 2_049),
            "scribe.result.invalid-vocabulary");

        var request = ParseValidRequest(ReadFixture("valid", "request.json"));
        Assert.True(DocumentationScribeAttemptId.TryParse(AttemptId, out var attempt));
        var ordinaryAmpersand = ParseObject(ReadFixture("valid", "proposal-result.json"));
        ordinaryAmpersand["terminal"]!["contentUnits"]![0]!["lines"]![0] = "Describes the R&D strategy; no entity is present.";
        Assert.True(DocumentationScribeValidation.ParseRunResult(request, attempt, [], Serialize(ordinaryAmpersand)).IsValid);
        var supplementaryException = ParseObject(ReadFixture("valid", "proposal-result.json"));
        supplementaryException["terminal"]!["contentUnits"]!.AsArray().Add(new JsonObject
        {
            ["kind"] = "content.exception",
            ["typeDocumentationId"] = "T:" + string.Concat(
                Enumerable.Repeat(char.ConvertFromUtf32(0x10400), 600)),
            ["lines"] = new JsonArray("Represents the documented failure."),
            ["claimCategoryId"] = "claim.purpose",
            ["evidenceReferenceIds"] = new JsonArray("evidence.summary"),
        });
        var supplementaryExceptionBytes = Serialize(supplementaryException);
        AssertSchemaValid(ResultSchema.Value, supplementaryExceptionBytes, "scalar-bounded-exception-id");
        Assert.True(DocumentationScribeValidation.ParseRunResult(
            request,
            attempt,
            [],
            supplementaryExceptionBytes).IsValid);
        var parsed = DocumentationScribeValidation.ParseRunResult(
            request,
            attempt,
            [],
            ReadFixture("valid", "proposal-result.json"));
        var proposal = Assert.IsType<DocumentationScribeProposalTerminal>(parsed.Result?.Terminal);
        var patch = ParseObject(ReadRepositoryFixture("documentation-patch", "v1", "valid", "repository-request.json"));
        var block = patch["blocks"]![0]!;
        block["applicableComponents"] = new JsonArray(request.Target.ApplicableComponents
            .Select(component =>
            {
                var node = new JsonObject
                {
                    ["kind"] = component.Kind switch
                    {
                        DocumentationPatchComponentKind.TypeParameter => "typeParameter",
                        DocumentationPatchComponentKind.Parameter => "parameter",
                        DocumentationPatchComponentKind.Return => "return",
                        DocumentationPatchComponentKind.Value => "value",
                        _ => throw new InvalidOperationException(),
                    },
                    ["identity"] = component.Identity,
                };
                if (component.Name is not null)
                {
                    node["name"] = component.Name;
                }

                return (JsonNode)node;
            })
            .ToArray());
        var projected = Assert.IsType<DocumentationPatchStructuredContent>(proposal.PatchContent);
        var webJson = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        block["content"] = new JsonObject
        {
            ["kind"] = "structured",
            ["summaryLines"] = JsonSerializer.SerializeToNode(projected.SummaryLines, webJson),
            ["typeParameters"] = JsonSerializer.SerializeToNode(projected.TypeParameters, webJson),
            ["parameters"] = JsonSerializer.SerializeToNode(projected.Parameters, webJson),
            ["return"] = JsonSerializer.SerializeToNode(projected.Return, webJson),
            ["value"] = JsonSerializer.SerializeToNode(projected.Value, webJson),
            ["exceptions"] = JsonSerializer.SerializeToNode(projected.Exceptions, webJson),
            ["remarksLines"] = JsonSerializer.SerializeToNode(projected.RemarksLines, webJson),
        };
        Assert.True(DocumentationPatchValidator.ParseRequest(Serialize(patch)).IsValid);
    }

    [Fact]
    public void Per_unit_evidence_rejects_missing_duplicate_dangling_and_wrong_subject()
    {
        AssertResultMutationFails(
            null,
            result => result["terminal"]!["contentUnits"]![0]!["evidenceReferenceIds"] = new JsonArray(),
            "scribe.result.invalid-shape");
        AssertResultMutationFails(
            null,
            result => result["terminal"]!["contentUnits"]![0]!["evidenceReferenceIds"] =
                new JsonArray("evidence.summary", "evidence.summary"),
            "scribe.result.invalid-order");
        AssertResultMutationFails(
            null,
            result => result["terminal"]!["contentUnits"]![0]!["evidenceReferenceIds"]![0] = "evidence.unknown",
            "scribe.result.invalid-reference");
        AssertResultMutationFails(
            null,
            result => result["terminal"]!["contentUnits"]![1]!["name"] = "other",
            "scribe.result.invalid-component");

        AssertRequestMutationFails(
            root => root["evidenceReferences"]![0]!["subject"]!["identity"] = "return",
            "scribe.request.wrong-subject",
            "/evidenceReferences/0/subject");
    }

    [Fact]
    public void Evidence_authority_truncation_and_explicit_conflict_fail_closed()
    {
        AssertResultMutationFails(
            request => request["styleProfile"]!["claimPolicies"]![0]!["allowedAuthorities"] =
                new JsonArray("authority.public-contract"),
            null,
            "scribe.result.invalid-evidence");
        AssertRequestMutationFails(
            request => request["evidenceReferences"]![0]!["authority"] = "authority.source-implementation",
            "scribe.request.invalid-vocabulary",
            "/evidenceReferences/0/authority");
        AssertResultMutationFails(
            request =>
            {
                request["evidenceReferences"]![0]!["originalUtf8ByteCount"] = 21;
                request["evidenceReferences"]![0]!["isTruncated"] = true;
            },
            null,
            "scribe.result.invalid-evidence");
        AssertResultMutationFails(
            request =>
            {
                var references = request["evidenceReferences"]!.AsArray();
                var higher = references[0]!.DeepClone().AsObject();
                higher["evidenceReferenceId"] = "evidence.parameter-high";
                higher["kind"] = "evidence.public-contract";
                higher["relation"] = "evidence.constrains";
                higher["authority"] = "authority.public-contract";
                references.Insert(1, higher);
                request["evidenceConflicts"] = new JsonArray(new JsonObject
                {
                    ["relation"] = "evidence-conflict.higher-authority-contradicts",
                    ["higherEvidenceReferenceId"] = "evidence.parameter-high",
                    ["lowerEvidenceReferenceId"] = "evidence.parameter",
                });
            },
            null,
            "scribe.result.invalid-evidence");
    }

    [Fact]
    public void Style_profile_checks_only_machine_visible_rules()
    {
        AssertRequestMutationFails(
            root => root["styleProfile"]!["forbiddenLiterals"] = new JsonArray("ContractScribe"),
            "scribe.request.invalid-style",
            "/styleProfile/forbiddenLiterals");
        AssertResultMutationFails(
            null,
            result => result["terminal"]!["contentUnits"]![0]!["lines"]![0] = "A guaranteed outcome.",
            "scribe.result.invalid-style");

        var awkward = ParseObject(ReadFixture("valid", "proposal-result.json"));
        awkward["terminal"]!["contentUnits"]![0]!["lines"]![0] = "Widget operation banana syntax perhaps.";
        var request = ParseValidRequest(ReadFixture("valid", "request.json"));
        Assert.True(DocumentationScribeAttemptId.TryParse(AttemptId, out var attempt));
        Assert.True(DocumentationScribeValidation.ParseRunResult(request, attempt, [], Serialize(awkward)).IsValid);
    }

    [Fact]
    public void Proposal_projects_to_current_m2_structured_content_without_erasing_units()
    {
        var request = ParseValidRequest(ReadFixture("valid", "request.json"));
        Assert.True(DocumentationScribeAttemptId.TryParse(AttemptId, out var attempt));
        var parsed = DocumentationScribeValidation.ParseRunResult(
            request,
            attempt,
            [],
            ReadFixture("valid", "proposal-result.json"));
        var proposal = Assert.IsType<DocumentationScribeProposalTerminal>(parsed.Result?.Terminal);
        var projected = Assert.IsType<DocumentationPatchStructuredContent>(proposal.PatchContent);
        Assert.Equal(3, proposal.ContentUnits.Length);
        Assert.Equal("Runs the synthetic widget operation.", Assert.Single(projected.SummaryLines));
        Assert.Equal("parameter/0", Assert.Single(projected.Parameters).ComponentIdentity);
        Assert.Equal("return", projected.Return?.ComponentIdentity);
        Assert.Empty(projected.Exceptions);
        Assert.Null(projected.RemarksLines);
    }

    [Fact]
    public void Inherit_doc_proposal_projects_to_current_m2_union()
    {
        var requestNode = ParseObject(ReadFixture("valid", "request.json"));
        var style = requestNode["styleProfile"]!;
        foreach (var policyName in new[] { "summary", "remarks", "exceptions" })
        {
            style[policyName]!["disposition"] = "forbidden";
            style[policyName]!["maximumScalars"] = 0;
        }

        foreach (var policy in style["componentPolicies"]!.AsArray())
        {
            policy!["disposition"] = "forbidden";
            policy["maximumScalars"] = 0;
        }

        style["inheritDocDisposition"] = "required";
        var request = ParseValidRequest(Serialize(requestNode));
        var result = ParseObject(ReadFixture("valid", "proposal-result.json"));
        Correlate(result, request.ArtifactSha256);
        result["terminal"]!["contentUnits"] = new JsonArray(new JsonObject
        {
            ["kind"] = "content.inherit-doc",
            ["lines"] = new JsonArray(),
            ["claimCategoryId"] = "claim.purpose",
            ["evidenceReferenceIds"] = new JsonArray("evidence.summary"),
        });
        Assert.True(DocumentationScribeAttemptId.TryParse(AttemptId, out var attempt));
        var parsed = DocumentationScribeValidation.ParseRunResult(request, attempt, [], Serialize(result));
        var proposal = Assert.IsType<DocumentationScribeProposalTerminal>(parsed.Result?.Terminal);
        Assert.IsType<DocumentationPatchInheritDocContent>(proposal.PatchContent);
        Assert.Single(proposal.ContentUnits);
    }

    [Fact]
    public void Terminal_union_correlation_and_envelope_are_fail_closed()
    {
        AssertResultMutationFails(
            null,
            result => result["scribeRequestSha256"] = new string('0', 64),
            "scribe.result.invalid-correlation",
            "/scribeRequestSha256");
        AssertResultMutationFails(
            null,
            result => result["attemptId"] = "scribe-attempt.ffffffffffffffffffffffffffffffff",
            "scribe.result.invalid-correlation",
            "/attemptId");
        AssertResultMutationFails(
            null,
            result => result["runEnvelope"]!["scribeRequestSha256"] = new string('0', 64),
            "scribe.result.invalid-correlation",
            "/runEnvelope/scribeRequestSha256");
        AssertResultMutationFails(
            null,
            result => result["runEnvelope"]!["attemptId"] = "scribe-attempt.ffffffffffffffffffffffffffffffff",
            "scribe.result.invalid-correlation",
            "/runEnvelope/attemptId");
        AssertResultMutationFails(
            null,
            result => result["terminal"]!["target"]!["symbolRef"]!["documentationCommentId"] = "M:Synthetic.Other.Run",
            "scribe.result.invalid-correlation");
        AssertResultMutationFails(
            null,
            result => result["terminal"]!["target"]!["sourceCommitment"]!["locator"]!["repository"]!["path"] =
                "src/Synthetic/Other.cs",
            "scribe.result.invalid-correlation");
        AssertResultMutationFails(
            null,
            result => result["terminal"]!["reason"] = "scribe.skip.insufficient-evidence",
            "scribe.result.unknown-field");

        var skip = ParseObject(ReadFixture("valid", "skip-result.json"));
        skip["terminal"]!["reason"] = "scribe.failure.provider";
        AssertResultFails(ParseValidRequest(ReadFixture("valid", "request.json")), skip, "scribe.result.invalid-vocabulary");

        AssertResultMutationFails(
            null,
            result => result["runEnvelope"]!["rawProviderError"] = "secret",
            "scribe.result.unknown-field");
        AssertResultMutationFails(
            null,
            result => result["runEnvelope"]!["toolCallCount"] = 17,
            "scribe.result.over-budget");
        AssertResultMutationFails(
            null,
            result => result["runEnvelope"]!["styleProfileId"] = "style.other.v1",
            "scribe.result.invalid-correlation");
    }

    [Fact]
    public void Inherit_doc_zero_ceilings_and_authority_strength_order_are_enforced()
    {
        AssertRequestMutationFails(
            request => Reverse(request["styleProfile"]!["claimPolicies"]![0]!["allowedAuthorities"]!.AsArray()),
            "scribe.request.invalid-order",
            "/styleProfile/claimPolicies/0/allowedAuthorities/1");
        AssertRequestMutationFails(
            request =>
            {
                var style = request["styleProfile"]!;
                foreach (var policyName in new[] { "summary", "remarks", "exceptions" })
                {
                    style[policyName]!["disposition"] = "forbidden";
                    style[policyName]!["maximumScalars"] = policyName == "summary" ? 1 : 0;
                }

                foreach (var policy in style["componentPolicies"]!.AsArray())
                {
                    policy!["disposition"] = "forbidden";
                    policy["maximumScalars"] = 0;
                }

                style["inheritDocDisposition"] = "required";
            },
            "scribe.request.invalid-style",
            "/styleProfile/inheritDocDisposition");
    }

    [Fact]
    public void Attempts_and_truthful_terminal_observations_use_separate_safety_bounds()
    {
        AssertResultMutationFails(
            null,
            result => result["runEnvelope"]!["attemptNumber"] = 4,
            "scribe.result.over-budget",
            "/runEnvelope/attemptNumber");
        AssertResultMutationFails(
            null,
            result => result["runEnvelope"]!["usage"]!["uncachedInputTokens"] = 32_769,
            "scribe.result.over-budget",
            "/runEnvelope/usage/uncachedInputTokens");
        AssertResultMutationFails(
            null,
            result => result["runEnvelope"]!["elapsedMilliseconds"] = 120_001,
            "scribe.result.over-budget",
            "/runEnvelope/elapsedMilliseconds");

        var request = ParseValidRequest(ReadFixture("valid", "request.json"));
        Assert.True(DocumentationScribeAttemptId.TryParse(AttemptId, out var attempt));
        var budgetEnvelope = CreateEnvelope(request) with
        {
            Usage = new DocumentationScribeUsageObservationInput(
                request.Limits.MaximumInputTokens + 1,
                request.Limits.MaximumOutputTokens + 1,
                request.Limits.MaximumInputTokens + 1,
                request.Limits.MaximumUncachedInputTokens + 1,
                request.Limits.MaximumOutputTokens + 1),
            Cost = new DocumentationScribeCostObservationInput(
                "currency.usd",
                request.Limits.MaximumCostMicrounits + 1),
        };
        var budget = DocumentationScribeValidation.CreateFailureResult(
            request,
            attempt,
            DocumentationScribeFailureCode.Budget,
            budgetEnvelope);
        Assert.Equal(request.Limits.MaximumUncachedInputTokens + 1, budget.RunEnvelope.Usage?.UncachedInputTokens);

        var timeout = DocumentationScribeValidation.CreateFailureResult(
            request,
            attempt,
            DocumentationScribeFailureCode.Timeout,
            CreateEnvelope(request) with
            {
                ElapsedMilliseconds = request.Limits.MaximumElapsedMilliseconds + 1,
            });
        Assert.Equal(request.Limits.MaximumElapsedMilliseconds + 1, timeout.RunEnvelope.ElapsedMilliseconds);

        Assert.Throws<ArgumentException>(() => DocumentationScribeValidation.CreateFailureResult(
            request,
            attempt,
            DocumentationScribeFailureCode.Internal,
            budgetEnvelope));
        Assert.Throws<ArgumentException>(() => DocumentationScribeValidation.CreateFailureResult(
            request,
            attempt,
            DocumentationScribeFailureCode.Budget,
            CreateEnvelope(request) with
            {
                AttemptNumber = request.Limits.MaximumAttempts + 1,
            }));

        var rawBudget = ParseObject(ReadFixture("valid", "failure-result.json"));
        rawBudget["terminal"]!["code"] = "scribe.failure.budget";
        rawBudget["runEnvelope"]!["usage"] = new JsonObject
        {
            ["uncachedInputTokens"] = request.Limits.MaximumUncachedInputTokens + 1,
        };
        rawBudget["runEnvelope"]!["cost"] = new JsonObject
        {
            ["currencyId"] = "currency.usd",
            ["amountMicrounits"] = request.Limits.MaximumCostMicrounits + 1,
        };
        Assert.True(DocumentationScribeValidation.ParseRunResult(request, attempt, [], Serialize(rawBudget)).IsValid);

        var boundaryRequestNode = ParseObject(ReadFixture("valid", "request.json"));
        var boundaryLimits = boundaryRequestNode["limits"]!;
        boundaryLimits["maximumInputTokens"] = DocumentationScribeContract.MaximumConfiguredInputTokens;
        boundaryLimits["maximumUncachedInputTokens"] = DocumentationScribeContract.MaximumConfiguredInputTokens;
        boundaryLimits["maximumOutputTokens"] = DocumentationScribeContract.MaximumConfiguredOutputTokens;
        boundaryLimits["maximumCostMicrounits"] = DocumentationScribeContract.MaximumConfiguredCostMicrounits;
        boundaryLimits["maximumElapsedMilliseconds"] = DocumentationScribeContract.MaximumConfiguredElapsedMilliseconds;
        var boundaryRequestBytes = Serialize(boundaryRequestNode);
        AssertSchemaValid(RequestSchema.Value, boundaryRequestBytes, "maximum-configured-execution-ceilings");
        var boundaryRequest = ParseValidRequest(boundaryRequestBytes);
        var maximumUsage = new DocumentationScribeUsageObservationInput(
            DocumentationScribeContract.MaximumObservedInputTokens,
            DocumentationScribeContract.MaximumObservedOutputTokens,
            DocumentationScribeContract.MaximumObservedInputTokens,
            DocumentationScribeContract.MaximumObservedInputTokens,
            DocumentationScribeContract.MaximumObservedOutputTokens);
        var maximumCost = new DocumentationScribeCostObservationInput(
            "currency.usd",
            DocumentationScribeContract.MaximumObservedCostMicrounits);

        var boundaryBudgetEnvelope = CreateEnvelope(boundaryRequest) with
        {
            Usage = maximumUsage,
            Cost = maximumCost,
        };
        var boundaryBudget = DocumentationScribeValidation.CreateFailureResult(
            boundaryRequest,
            attempt,
            DocumentationScribeFailureCode.Budget,
            boundaryBudgetEnvelope);
        Assert.Equal(DocumentationScribeContract.MaximumObservedInputTokens, boundaryBudget.RunEnvelope.Usage?.InputTokens);

        var rawBoundaryBudget = ParseObject(ReadFixture("valid", "failure-result.json"));
        Correlate(rawBoundaryBudget, boundaryRequest.ArtifactSha256);
        rawBoundaryBudget["terminal"]!["code"] = "scribe.failure.budget";
        rawBoundaryBudget["runEnvelope"]!["usage"] = new JsonObject
        {
            ["inputTokens"] = DocumentationScribeContract.MaximumObservedInputTokens,
        };
        rawBoundaryBudget["runEnvelope"]!["cost"] = new JsonObject
        {
            ["currencyId"] = "currency.usd",
            ["amountMicrounits"] = DocumentationScribeContract.MaximumObservedCostMicrounits,
        };
        var rawBoundaryBudgetBytes = Serialize(rawBoundaryBudget);
        AssertSchemaValid(ResultSchema.Value, rawBoundaryBudgetBytes, "maximum-bounded-budget-overrun");
        Assert.True(DocumentationScribeValidation.ParseRunResult(
            boundaryRequest,
            attempt,
            [],
            rawBoundaryBudgetBytes).IsValid);

        var simultaneousTimeoutEnvelope = boundaryBudgetEnvelope with
        {
            ElapsedMilliseconds = DocumentationScribeContract.MaximumObservedElapsedMilliseconds,
        };
        var simultaneousTimeout = DocumentationScribeValidation.CreateFailureResult(
            boundaryRequest,
            attempt,
            DocumentationScribeFailureCode.Timeout,
            simultaneousTimeoutEnvelope);
        Assert.Equal(DocumentationScribeContract.MaximumObservedElapsedMilliseconds, simultaneousTimeout.RunEnvelope.ElapsedMilliseconds);
        Assert.Equal(DocumentationScribeContract.MaximumObservedCostMicrounits, simultaneousTimeout.RunEnvelope.Cost?.AmountMicrounits);

        var rawSimultaneousTimeout = ParseObject(ReadFixture("valid", "failure-result.json"));
        Correlate(rawSimultaneousTimeout, boundaryRequest.ArtifactSha256);
        rawSimultaneousTimeout["terminal"]!["code"] = "scribe.failure.timeout";
        rawSimultaneousTimeout["runEnvelope"]!["elapsedMilliseconds"] =
            DocumentationScribeContract.MaximumObservedElapsedMilliseconds;
        rawSimultaneousTimeout["runEnvelope"]!["usage"] = new JsonObject
        {
            ["inputTokens"] = DocumentationScribeContract.MaximumObservedInputTokens,
        };
        rawSimultaneousTimeout["runEnvelope"]!["cost"] = new JsonObject
        {
            ["currencyId"] = "currency.usd",
            ["amountMicrounits"] = DocumentationScribeContract.MaximumObservedCostMicrounits,
        };
        var rawSimultaneousTimeoutBytes = Serialize(rawSimultaneousTimeout);
        AssertSchemaValid(ResultSchema.Value, rawSimultaneousTimeoutBytes, "simultaneous-timeout-budget-overrun");
        Assert.True(DocumentationScribeValidation.ParseRunResult(
            boundaryRequest,
            attempt,
            [],
            rawSimultaneousTimeoutBytes).IsValid);

        rawSimultaneousTimeout["runEnvelope"]!["usage"]!["inputTokens"] =
            (long)DocumentationScribeContract.MaximumObservedInputTokens + 1;
        var overArtifactMaximumBytes = Serialize(rawSimultaneousTimeout);
        Assert.False(Evaluate(ResultSchema.Value, overArtifactMaximumBytes));
        var overArtifactMaximum = DocumentationScribeValidation.ParseRunResult(
            boundaryRequest,
            attempt,
            [],
            overArtifactMaximumBytes);
        Assert.False(overArtifactMaximum.IsValid);
        Assert.Equal("/runEnvelope/usage/inputTokens", overArtifactMaximum.Failure?.Pointer);
        Assert.Throws<ArgumentException>(() => DocumentationScribeValidation.CreateFailureResult(
            boundaryRequest,
            attempt,
            DocumentationScribeFailureCode.Timeout,
            simultaneousTimeoutEnvelope with
            {
                Usage = maximumUsage with
                {
                    InputTokens = DocumentationScribeContract.MaximumObservedInputTokens + 1,
                },
            }));
    }

    [Fact]
    public async Task Typed_tool_extension_is_cross_assembly_and_registry_closed()
    {
        var descriptor = new SyntheticDescriptor();
        var port = new SyntheticPort();
        var registry = new SyntheticRegistry();
        Assert.False(registry.IsRegistered(descriptor));
        registry.Register(descriptor, port);
        Assert.True(registry.IsRegistered(descriptor));
        var result = await port.InvokeAsync(new SyntheticRequest("reference.synthetic"), CancellationToken.None);
        Assert.Same(DocumentationScribeToolOutcome.Complete, result.Outcome);
        Assert.Equal("bounded content", result.Content);

        var outcomeIds = typeof(DocumentationScribeToolOutcome)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(property => property.PropertyType == typeof(DocumentationScribeToolOutcome))
            .Select(property => ((DocumentationScribeToolOutcome)property.GetValue(null)!).Id)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(7, outcomeIds.Length);
        Assert.Equal(7, outcomeIds.Distinct(StringComparer.Ordinal).Count());

        var publicCoreToolSurface = new[]
            {
                typeof(IDocumentationScribeToolRequest<>),
                typeof(IDocumentationScribeToolResult),
                typeof(IDocumentationScribeToolDescriptor<,>),
                typeof(IDocumentationScribeToolPort<,>),
            }
            .SelectMany(type => type.GetProperties().Select(property => property.PropertyType)
                .Concat(type.GetMethods().SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType))))
            .ToArray();
        Assert.DoesNotContain(publicCoreToolSurface, type =>
            type == typeof(object)
            || type == typeof(JsonElement)
            || type == typeof(JsonNode)
            || typeof(IServiceProvider).IsAssignableFrom(type)
            || typeof(Delegate).IsAssignableFrom(type));
        Assert.DoesNotContain(
            typeof(IDocumentationScribeToolPort<,>).Assembly.GetExportedTypes(),
            type => type.Name.Contains("ReferenceRead", StringComparison.Ordinal));
    }

    [Fact]
    public void Runtime_terminal_factories_bind_the_parsed_request_and_attempt()
    {
        var request = ParseValidRequest(ReadFixture("valid", "request.json"));
        Assert.True(DocumentationScribeAttemptId.TryParse(AttemptId, out var attempt));
        var envelope = new DocumentationScribeRunEnvelopeInput(
            "provider.synthetic.v1",
            "model.synthetic.v1",
            "scribe-protocol.v1",
            1,
            0,
            0,
            0,
            5,
            null,
            null,
            null,
            ImmutableArray<DocumentationScribeDiagnosticInput>.Empty);
        var failure = DocumentationScribeValidation.CreateFailureResult(
            request,
            attempt,
            DocumentationScribeFailureCode.Internal,
            envelope);
        Assert.Equal(request.ArtifactSha256, failure.ScribeRequestSha256);
        Assert.Equal(attempt, failure.AttemptId);
        Assert.Equal(request.ToolPolicyId, failure.RunEnvelope.ToolPolicyId);
        Assert.IsType<DocumentationScribeFailureTerminal>(failure.Terminal);

        var skip = DocumentationScribeValidation.CreateSkipResult(
            request,
            attempt,
            DocumentationScribeSkipReason.InsufficientEvidence,
            new[] { "evidence.summary" },
            envelope);
        Assert.IsType<DocumentationScribeSkipTerminal>(skip.Terminal);

        Assert.Throws<ArgumentException>(() => DocumentationScribeValidation.CreateCancelledResult(
            request,
            attempt,
            DocumentationScribeCancellationCode.Caller,
            envelope with { ToolCallCount = request.Limits.MaximumToolCalls + 1 }));
    }

    [Fact]
    public void Schema_and_core_reject_unknown_fields_without_publishing_untrusted_names()
    {
        var request = ParseObject(ReadFixture("valid", "request.json"));
        request["target"]!["sourceText"] = "private source";
        var requestBytes = Serialize(request);
        Assert.False(Evaluate(RequestSchema.Value, requestBytes));
        var requestFailure = DocumentationScribeValidation.ParseRequest(requestBytes).Failure;
        Assert.Equal("scribe.request.unknown-field", requestFailure?.Code);
        Assert.Equal("/target/sourceText", requestFailure?.Pointer);

        var result = ParseObject(ReadFixture("valid", "failure-result.json"));
        result["terminal"]!["reason"] = "scribe.skip.insufficient-evidence";
        Assert.False(Evaluate(ResultSchema.Value, Serialize(result)));

        var untrustedPropertyName = "private provider content " + new string('x', 900_000);
        var boundedRequest = ParseObject(ReadFixture("valid", "request.json"));
        boundedRequest["target"]![untrustedPropertyName] = true;
        var boundedRequestBytes = Serialize(boundedRequest);
        Assert.True(boundedRequestBytes.Length < DocumentationScribeContract.MaximumArtifactUtf8Bytes);
        var boundedRequestFailure = DocumentationScribeValidation.ParseRequest(boundedRequestBytes).Failure;
        Assert.Equal("scribe.request.unknown-field", boundedRequestFailure?.Code);
        Assert.Equal("/target", boundedRequestFailure?.Pointer);
        Assert.DoesNotContain("private provider content", boundedRequestFailure?.Pointer, StringComparison.Ordinal);

        var parsedRequest = ParseValidRequest(ReadFixture("valid", "request.json"));
        Assert.True(DocumentationScribeAttemptId.TryParse(AttemptId, out var attempt));
        var boundedResult = ParseObject(ReadFixture("valid", "proposal-result.json"));
        Correlate(boundedResult, parsedRequest.ArtifactSha256);
        boundedResult["runEnvelope"]![untrustedPropertyName] = true;
        var boundedResultBytes = Serialize(boundedResult);
        Assert.True(boundedResultBytes.Length < DocumentationScribeContract.MaximumArtifactUtf8Bytes);
        var boundedResultFailure = DocumentationScribeValidation.ParseRunResult(
            parsedRequest,
            attempt,
            [],
            boundedResultBytes).Failure;
        Assert.Equal("scribe.result.unknown-field", boundedResultFailure?.Code);
        Assert.Equal("/runEnvelope", boundedResultFailure?.Pointer);
        Assert.DoesNotContain("private provider content", boundedResultFailure?.Pointer, StringComparison.Ordinal);

        var duplicateName = "private duplicate content " + new string('x', 8_192);
        var duplicateBytes = Encoding.UTF8.GetBytes(
            $"{{\"scribeRequestVersion\":1,\"{duplicateName}\":0,\"{duplicateName}\":1}}");
        var duplicateFailure = DocumentationScribeValidation.ParseRequest(duplicateBytes).Failure;
        Assert.Equal("scribe.request.duplicate-property", duplicateFailure?.Code);
        Assert.Equal(string.Empty, duplicateFailure?.Pointer);
    }

    private static void AssertRequestMutationFails(
        Action<JsonObject> mutation,
        string expectedCode,
        string expectedPointer)
    {
        var root = ParseObject(ReadFixture("valid", "request.json"));
        mutation(root);
        var parsed = DocumentationScribeValidation.ParseRequest(Serialize(root));
        Assert.Null(parsed.Request);
        Assert.Equal(expectedCode, parsed.Failure?.Code);
        Assert.Equal(expectedPointer, parsed.Failure?.Pointer);
    }

    private static void AssertResultMutationFails(
        Action<JsonObject>? requestMutation,
        Action<JsonObject>? resultMutation,
        string expectedCode,
        string? expectedPointer = null)
    {
        var requestNode = ParseObject(ReadFixture("valid", "request.json"));
        requestMutation?.Invoke(requestNode);
        var requestBytes = Serialize(requestNode);
        var request = ParseValidRequest(requestBytes);
        var resultNode = ParseObject(ReadFixture("valid", "proposal-result.json"));
        Correlate(resultNode, request.ArtifactSha256);
        resultMutation?.Invoke(resultNode);
        AssertResultFails(request, resultNode, expectedCode, expectedPointer);
    }

    private static void AssertResultFails(
        DocumentationScribeRequest request,
        JsonObject result,
        string expectedCode,
        string? expectedPointer = null)
    {
        Assert.True(DocumentationScribeAttemptId.TryParse(AttemptId, out var attempt));
        var parsed = DocumentationScribeValidation.ParseRunResult(request, attempt, [], Serialize(result));
        Assert.Null(parsed.Result);
        Assert.Equal(expectedCode, parsed.Failure?.Code);
        if (expectedPointer is not null)
        {
            Assert.Equal(expectedPointer, parsed.Failure?.Pointer);
        }
    }

    private static DocumentationScribeRequest ParseValidRequest(byte[] bytes)
    {
        var parsed = DocumentationScribeValidation.ParseRequest(bytes);
        Assert.Null(parsed.Failure);
        return Assert.IsType<DocumentationScribeRequest>(parsed.Request);
    }

    private static void Correlate(JsonObject result, string requestSha256)
    {
        result["scribeRequestSha256"] = requestSha256;
        result["runEnvelope"]!["scribeRequestSha256"] = requestSha256;
    }

    private static DocumentationScribeRunEnvelopeInput CreateEnvelope(DocumentationScribeRequest request) => new(
        "provider.synthetic.v1",
        "model.synthetic.v1",
        "scribe-protocol.v1",
        1,
        0,
        0,
        0,
        Math.Min(5, request.Limits.MaximumElapsedMilliseconds),
        null,
        null,
        null,
        ImmutableArray<DocumentationScribeDiagnosticInput>.Empty);

    private static DocumentationScribeDynamicEvidenceInput DynamicEvidenceInput(
        EvidenceLocator? locator = null,
        EvidenceKind kind = EvidenceKind.SourceDeclaration,
        DocumentationScribeEvidenceAuthority authority = DocumentationScribeEvidenceAuthority.SourceDeclaration,
        string? contentSha256 = null,
        int originalUtf8ByteCount = 1,
        int includedUtf8ByteCount = 1,
        bool isTruncated = false,
        ImmutableArray<string> claimCategoryIds = default) =>
        new(
            EvidenceInput.TargetSubject(
                "synthetic.v1",
                "M:Synthetic.Widget.Run(System.String)"),
            kind,
            EvidenceRelation.Declares,
            authority,
            locator ?? EvidenceInput.RepositoryLocator("src/Synthetic/Widget.cs", 100, 101),
            contentSha256 ?? new string('a', 64),
            originalUtf8ByteCount,
            includedUtf8ByteCount,
            isTruncated,
            claimCategoryIds.IsDefault ? ["claim.purpose"] : claimCategoryIds);

    private static JsonObject DynamicEvidenceJson(DocumentationScribeEvidenceReference reference)
    {
        var subject = Assert.IsType<TargetEvidenceSubject>(reference.Subject);
        var locator = Assert.IsType<RepositoryEvidenceLocator>(reference.Locator);
        var repository = new JsonObject
        {
            ["path"] = locator.Path,
        };
        if (locator.Span is { } span)
        {
            repository["span"] = new JsonObject
            {
                ["start"] = span.Start,
                ["end"] = span.End,
            };
        }

        var claimCategoryIds = new JsonArray();
        foreach (var claimCategoryId in reference.ClaimCategoryIds)
        {
            claimCategoryIds.Add(claimCategoryId);
        }

        return new JsonObject
        {
            ["evidenceReferenceId"] = reference.EvidenceReferenceId,
            ["repositoryContextRef"] = reference.RepositoryContextRef.Value,
            ["subject"] = new JsonObject
            {
                ["symbolRef"] = new JsonObject
                {
                    ["compilationContextRef"] = subject.ParentSymbolRef.CompilationContextRef,
                    ["documentationCommentId"] = subject.ParentSymbolRef.DocumentationCommentId,
                },
            },
            ["kind"] = EvidenceVocabulary.GetId(reference.Kind),
            ["relation"] = EvidenceVocabulary.GetId(reference.Relation),
            ["authority"] = DocumentationScribeVocabulary.GetId(reference.Authority),
            ["locator"] = new JsonObject
            {
                ["repository"] = repository,
            },
            ["contentSha256"] = reference.ContentSha256,
            ["originalUtf8ByteCount"] = reference.OriginalUtf8ByteCount,
            ["includedUtf8ByteCount"] = reference.IncludedUtf8ByteCount,
            ["isTruncated"] = reference.IsTruncated,
            ["claimCategoryIds"] = claimCategoryIds,
        };
    }

    private static void Reverse(JsonArray array)
    {
        var first = array[0]!.DeepClone();
        array[0] = array[1]!.DeepClone();
        array[1] = first;
    }

    private static JsonObject ParseObject(byte[] bytes) => JsonNode.Parse(bytes)!.AsObject();

    private static byte[] Serialize(JsonObject value) => Encoding.UTF8.GetBytes(value.ToJsonString());

    private static JsonSchema LoadSchema(string name) => JsonSchema.FromText(
        Encoding.UTF8.GetString(ReadContractFile(name)));

    private static bool Evaluate(JsonSchema schema, byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        return schema.Evaluate(document.RootElement).IsValid;
    }

    private static void AssertSchemaValid(JsonSchema schema, byte[] bytes, string caseId) =>
        Assert.True(Evaluate(schema, bytes), caseId);

    private static byte[] ReadFixture(params string[] relativeParts)
    {
        var parts = new[] { FindRepositoryRoot(), "tests", "fixtures", "documentation-scribe", "v1" }
            .Concat(relativeParts)
            .ToArray();
        return File.ReadAllBytes(Path.Combine(parts));
    }

    private static byte[] ReadContractFile(string name) => File.ReadAllBytes(Path.Combine(
        FindRepositoryRoot(), "schemas", "documentation-scribe", name));

    private static byte[] ReadRepositoryFixture(params string[] relativeParts) => File.ReadAllBytes(Path.Combine(
        new[] { FindRepositoryRoot(), "tests", "fixtures" }.Concat(relativeParts).ToArray()));

    private static void AssertLocalReferences(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj)
            {
                if (property.Key == "$ref")
                {
                    Assert.StartsWith("#", property.Value!.GetValue<string>(), StringComparison.Ordinal);
                }

                if (property.Value is not null)
                {
                    AssertLocalReferences(property.Value);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array.Where(item => item is not null))
            {
                AssertLocalReferences(item!);
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ContractScribe.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed record SyntheticRequest(string ReferenceId)
        : IDocumentationScribeToolRequest<SyntheticResult>;

    private sealed record SyntheticResult(
        DocumentationScribeToolOutcome Outcome,
        string Content) : IDocumentationScribeToolResult;

    private sealed class SyntheticDescriptor
        : IDocumentationScribeToolDescriptor<SyntheticRequest, SyntheticResult>
    {
        public string OperationId => "synthetic.reference-read";
    }

    private sealed class SyntheticPort
        : IDocumentationScribeToolPort<SyntheticRequest, SyntheticResult>
    {
        public ValueTask<SyntheticResult> InvokeAsync(
            SyntheticRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SyntheticResult(DocumentationScribeToolOutcome.Complete, "bounded content"));
    }

    private sealed class SyntheticRegistry
    {
        private readonly HashSet<(Type Descriptor, Type Request, Type Result)> registrations = [];

        internal void Register<TRequest, TResult>(
            IDocumentationScribeToolDescriptor<TRequest, TResult> descriptor,
            IDocumentationScribeToolPort<TRequest, TResult> port)
            where TRequest : IDocumentationScribeToolRequest<TResult>
            where TResult : IDocumentationScribeToolResult
        {
            registrations.Add((descriptor.GetType(), typeof(TRequest), typeof(TResult)));
        }

        internal bool IsRegistered<TRequest, TResult>(
            IDocumentationScribeToolDescriptor<TRequest, TResult> descriptor)
            where TRequest : IDocumentationScribeToolRequest<TResult>
            where TResult : IDocumentationScribeToolResult =>
            registrations.Contains((descriptor.GetType(), typeof(TRequest), typeof(TResult)));
    }
}
