using System.Collections.Immutable;
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
            var parsed = DocumentationScribeValidation.ParseRunResult(request, attemptId, resultBytes);
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
        AssertLocalReferences(JsonNode.Parse(ReadContractFile("v1.request.schema.json"))!);
        AssertLocalReferences(JsonNode.Parse(ReadContractFile("v1.run-result.schema.json"))!);
    }

    [Fact]
    public void Invalid_fixture_manifest_has_stable_codes_and_pointers()
    {
        using var manifest = JsonDocument.Parse(ReadFixture("invalid-cases.json"));
        foreach (var invalidCase in manifest.RootElement.GetProperty("invalidCases").EnumerateArray())
        {
            var path = invalidCase.GetProperty("path").GetString()!.Split('/');
            var parsed = DocumentationScribeValidation.ParseRequest(ReadFixture(path));
            Assert.Null(parsed.Request);
            Assert.Equal(invalidCase.GetProperty("expectedCode").GetString(), parsed.Failure?.Code);
            Assert.Equal(invalidCase.GetProperty("expectedPointer").GetString(), parsed.Failure?.Pointer);
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
        Assert.True(DocumentationScribeValidation.ParseRunResult(request, attempt, Serialize(awkward)).IsValid);
    }

    [Fact]
    public void Proposal_projects_to_current_m2_structured_content_without_erasing_units()
    {
        var request = ParseValidRequest(ReadFixture("valid", "request.json"));
        Assert.True(DocumentationScribeAttemptId.TryParse(AttemptId, out var attempt));
        var parsed = DocumentationScribeValidation.ParseRunResult(
            request,
            attempt,
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
        var parsed = DocumentationScribeValidation.ParseRunResult(request, attempt, Serialize(result));
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
            "scribe.result.invalid-correlation");
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
    public void Schema_rejects_unknown_fields_and_mixed_terminal_payloads()
    {
        var request = ParseObject(ReadFixture("valid", "request.json"));
        request["target"]!["sourceText"] = "private source";
        Assert.False(Evaluate(RequestSchema.Value, Serialize(request)));

        var result = ParseObject(ReadFixture("valid", "failure-result.json"));
        result["terminal"]!["reason"] = "scribe.skip.insufficient-evidence";
        Assert.False(Evaluate(ResultSchema.Value, Serialize(result)));
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
        string expectedCode)
    {
        var requestNode = ParseObject(ReadFixture("valid", "request.json"));
        requestMutation?.Invoke(requestNode);
        var requestBytes = Serialize(requestNode);
        var request = ParseValidRequest(requestBytes);
        var resultNode = ParseObject(ReadFixture("valid", "proposal-result.json"));
        Correlate(resultNode, request.ArtifactSha256);
        resultMutation?.Invoke(resultNode);
        AssertResultFails(request, resultNode, expectedCode);
    }

    private static void AssertResultFails(
        DocumentationScribeRequest request,
        JsonObject result,
        string expectedCode)
    {
        Assert.True(DocumentationScribeAttemptId.TryParse(AttemptId, out var attempt));
        var parsed = DocumentationScribeValidation.ParseRunResult(request, attempt, Serialize(result));
        Assert.Null(parsed.Result);
        Assert.Equal(expectedCode, parsed.Failure?.Code);
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
