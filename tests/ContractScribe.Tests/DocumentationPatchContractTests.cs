using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Core;
using Json.Schema;

namespace ContractScribe.Tests;

public sealed class DocumentationPatchContractTests
{
    private static readonly Lazy<JsonSchema> RequestSchema = new(() => LoadSchema("v1.request.schema.json"));
    private static readonly Lazy<JsonSchema> ResultSchema = new(() => LoadSchema("v1.validation-result.schema.json"));

    [Fact]
    public void PublishedFixtures_AreSchemaValidAndSemanticallyValid()
    {
        foreach (var name in new[] { "repository-request.json", "mixed-locators-request.json" })
        {
            var bytes = ReadFixture("valid", name);
            AssertSchemaValid(RequestSchema.Value, bytes, name);
            Assert.True(DocumentationPatchValidator.ParseRequest(bytes).IsValid, name);
        }

        var request = ParseRequest(ReadFixture("valid", "repository-request.json"));
        foreach (var name in new[] { "accepted-result.json", "stale-result.json", "rejected-no-op-result.json" })
        {
            var bytes = ReadFixture("valid", name);
            AssertSchemaValid(ResultSchema.Value, bytes, name);
            var parsed = DocumentationPatchValidator.ParseValidationResult(bytes);
            Assert.True(parsed.IsValid, name);
            Assert.True(DocumentationPatchValidator.ValidateResult(request, parsed.Result!).IsValid, name);
        }
    }

    [Fact]
    public void InvalidFixtureManifest_ProducesStableFailureCodes()
    {
        using var manifest = JsonDocument.Parse(ReadFixture("invalid-cases.json"));
        foreach (var item in manifest.RootElement.GetProperty("cases").EnumerateArray())
        {
            var caseId = item.GetProperty("caseId").GetString()!;
            var bytes = ReadFixture(item.GetProperty("payloadFile").GetString()!.Split('/'));
            var parsed = DocumentationPatchValidator.ParseRequest(bytes);
            Assert.False(parsed.IsValid, caseId);
            Assert.Equal(item.GetProperty("expectedCode").GetString(), parsed.Failure!.Code);
        }
    }

    [Fact]
    public void RawRequestAndResultFailures_AreSeparatedFromExecutionOutcomes()
    {
        var request = ReadFixture("valid", "repository-request.json");
        var result = ReadFixture("valid", "accepted-result.json");

        Assert.Equal("patch.request.bom-not-allowed", DocumentationPatchValidator.ParseRequest(
            Encoding.UTF8.GetPreamble().Concat(request).ToArray()).Failure!.Code);
        Assert.Equal("patch.request.invalid-utf8", DocumentationPatchValidator.ParseRequest(new byte[] { 0xc3, 0x28 }).Failure!.Code);
        Assert.Equal("patch.request.document-too-large", DocumentationPatchValidator.ParseRequest(
            new byte[DocumentationPatchValidator.MaximumArtifactUtf8Bytes + 1]).Failure!.Code);
        Assert.Equal("patch.request.unsupported-version", DocumentationPatchValidator.ParseRequest(
            Mutate(request, root => root["patchRequestVersion"] = 2)).Failure!.Code);

        Assert.Equal("patch.result.bom-not-allowed", DocumentationPatchValidator.ParseValidationResult(
            Encoding.UTF8.GetPreamble().Concat(result).ToArray()).Failure!.Code);
        Assert.Equal("patch.result.invalid-utf8", DocumentationPatchValidator.ParseValidationResult(new byte[] { 0xc3, 0x28 }).Failure!.Code);
        Assert.Equal("patch.result.unsupported-version", DocumentationPatchValidator.ParseValidationResult(
            Mutate(result, root => root["patchValidationResultVersion"] = 2)).Failure!.Code);
        var resultWithUnknownField = Mutate(result, root => root["provider"] = "not-part-of-v1");
        Assert.False(Evaluate(ResultSchema.Value, resultWithUnknownField));
        Assert.Equal("patch.result.invalid-shape",
            DocumentationPatchValidator.ParseValidationResult(resultWithUnknownField).Failure!.Code);
        var duplicateResult = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(result).Replace(
            "\"outcome\": \"accepted\",",
            "\"outcome\": \"accepted\",\n  \"outcome\": \"accepted\",",
            StringComparison.Ordinal));
        Assert.Equal("patch.result.duplicate-property",
            DocumentationPatchValidator.ParseValidationResult(duplicateResult).Failure!.Code);

        Assert.Null(DocumentationPatchValidator.ParseRequest(new byte[] { 0xc3, 0x28 }).Request);
        Assert.Null(DocumentationPatchValidator.ParseValidationResult(new byte[] { 0xc3, 0x28 }).Result);
    }

    [Fact]
    public void RepositoryContextRef_IsOpaqueCanonicalAndCaseSensitive()
    {
        Assert.True(RepositoryContextRef.TryParse(
            "repoctx-00112233445566778899aabbccddeeff", out var parsed));
        Assert.Equal("repoctx-00112233445566778899aabbccddeeff", parsed.Value);
        Assert.False(RepositoryContextRef.TryParse("repoctx-00112233445566778899AABBCCDDEEFF", out _));
        Assert.False(RepositoryContextRef.TryParse("D:/code/private/repository", out _));
        Assert.False(RepositoryContextRef.TryParse("owner/repository", out _));
    }

    [Fact]
    public void ContextValidation_RejectsEachSubstitutionBeforeSourceUse()
    {
        using var vectors = JsonDocument.Parse(ReadFixture("context-binding-vectors.json"));
        var requestFile = vectors.RootElement.GetProperty("requestFile").GetString()!.Split('/');
        var request = ParseRequest(ReadFixture(requestFile));

        foreach (var vector in vectors.RootElement.GetProperty("vectors").EnumerateArray())
        {
            var caseId = vector.GetProperty("caseId").GetString()!;
            Assert.True(RepositoryContextRef.TryParse(
                vector.GetProperty("repositoryContextRef").GetString(), out var repositoryContextRef));
            var targetProfile = vector.GetProperty("targetProfile").GetString() switch
            {
                "profile.external-api" => TargetProfile.ExternalApi,
                "profile.assembly-visible" => TargetProfile.AssemblyVisible,
                _ => throw new InvalidOperationException("Fixture target profile is not closed."),
            };
            var compilationContexts = vector.GetProperty("compilationContextRefs")
                .EnumerateArray()
                .Select(item => item.GetString()!)
                .ToArray();
            var check = DocumentationPatchValidator.ValidateContext(request, new(
                repositoryContextRef,
                vector.GetProperty("inputIdentity").GetString()!,
                targetProfile,
                compilationContexts));
            var expectedCode = vector.GetProperty("expectedCode").ValueKind == JsonValueKind.Null
                ? null
                : vector.GetProperty("expectedCode").GetString();
            Assert.Equal(expectedCode, check.Code);
            Assert.Equal(expectedCode is null, check.IsValid);
            if (expectedCode == "patch.stale.compilation-context")
            {
                Assert.Equal("block.widget", check.BlockId);
            }
        }
    }

    [Fact]
    public void RepositorySourceVectors_PinExactBytesEncodingAndUtf16Span()
    {
        using var vectors = JsonDocument.Parse(ReadFixture("source-byte-vectors.json"));
        foreach (var vector in vectors.RootElement.GetProperty("vectors").EnumerateArray())
        {
            var caseId = vector.GetProperty("caseId").GetString()!;
            var bytes = Convert.FromBase64String(vector.GetProperty("base64").GetString()!);
            Assert.Equal(vector.GetProperty("expectedLength").GetInt32(), bytes.Length);
            var expectedDigest = vector.GetProperty("expectedSha256").GetString();
            var actualDigest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            Assert.True(string.Equals(expectedDigest, actualDigest, StringComparison.Ordinal),
                $"{caseId}: expected {expectedDigest}; actual {actualDigest}");

            var locator = BuildRepositoryLocator(
                vector.GetProperty("encoding").GetString()!,
                vector.GetProperty("expectedSha256").GetString()!,
                caseId == "utf8-lf" ? 21 : 22,
                caseId == "utf8-lf" ? 42 : 43);
            var check = DocumentationPatchValidator.ValidateRepositorySource(locator, bytes);
            if (caseId == "malformed-utf8")
            {
                Assert.Equal("patch.stale.source-encoding", check.Code);
            }
            else
            {
                Assert.True(check.IsValid, caseId);
                Assert.Contains("public class Widget", check.DecodedText, StringComparison.Ordinal);
            }
        }

        var lfBytes = Convert.FromBase64String("bmFtZXNwYWNlIFN5bnRoZXRpYzsKcHVibGljIGNsYXNzIFdpZGdldCB7fQo=");
        var valid = BuildRepositoryLocator(
            "utf-8", "91d19144488bb88906a805a24b4b6041638719a2b5d5a623eabff9dcc85b67d3", 21, 42);
        var drifted = lfBytes.ToArray();
        drifted[^1] = (byte)' ';
        Assert.Equal("patch.stale.source-bytes", DocumentationPatchValidator.ValidateRepositorySource(valid, drifted).Code);

        var wrongEncoding = BuildRepositoryLocator(
            "utf-8", "65102966154bd6bbd59b2b9cfa84d7f7c0a235ef6cde105cda3aa9e967c5800e", 22, 43);
        var bomBytes = Convert.FromBase64String(
            "77u/bmFtZXNwYWNlIFN5bnRoZXRpYzsNCnB1YmxpYyBjbGFzcyBXaWRnZXQge30NCg==");
        Assert.Equal("patch.stale.source-encoding", DocumentationPatchValidator.ValidateRepositorySource(wrongEncoding, bomBytes).Code);

        var wrongSpan = BuildRepositoryLocator(
            "utf-8", "91d19144488bb88906a805a24b4b6041638719a2b5d5a623eabff9dcc85b67d3", 21, 200);
        Assert.Equal("patch.stale.source-span", DocumentationPatchValidator.ValidateRepositorySource(wrongSpan, lfBytes).Code);
    }

    [Fact]
    public void GeneratedSourceLocator_UsesStrictUtf8HashAndUtf16Span()
    {
        var request = ParseRequest(ReadFixture("valid", "mixed-locators-request.json"));
        var locator = Assert.IsAssignableFrom<DocumentationPatchGeneratedLocator>(request.Blocks[1].Locator);
        const string source = "namespace Synthetic;\npublic class Widget {}\n";

        Assert.True(DocumentationPatchValidator.ValidateGeneratedSource(locator, source).IsValid);
        Assert.Equal("patch.stale.source-bytes", DocumentationPatchValidator.ValidateGeneratedSource(
            locator, source + " ").Code);
    }

    [Theory]
    [InlineData("gitCommit")]
    [InlineData("repositoryPath")]
    [InlineData("provider")]
    [InlineData("prompt")]
    [InlineData("proposalRunId")]
    public void ContextUnknownFields_DoNotAcquireAccidentalSemantics(string field)
    {
        var bytes = Mutate(ReadFixture("valid", "repository-request.json"), root =>
            root["context"]!.AsObject()[field] = "not-part-of-v1");
        Assert.False(Evaluate(RequestSchema.Value, bytes));
        Assert.Equal("patch.request.invalid-shape", DocumentationPatchValidator.ParseRequest(bytes).Failure!.Code);
    }

    [Fact]
    public void StructuredContent_RejectsInvalidXmlIdsAndComponentBindings()
    {
        var valid = ReadFixture("valid", "repository-request.json");

        Assert.Equal("patch.request.invalid-content", DocumentationPatchValidator.ParseRequest(Mutate(valid, root =>
            root["blocks"]![0]!["content"]!["summaryLines"]![0] = "bad\u0001line")).Failure!.Code);
        Assert.Equal("patch.request.invalid-content", DocumentationPatchValidator.ParseRequest(Mutate(valid, root =>
            root["blocks"]![0]!["content"]!["exceptions"]![0]!["typeDocumentationId"] = "System.Exception")).Failure!.Code);
        Assert.Equal("patch.request.invalid-content", DocumentationPatchValidator.ParseRequest(Mutate(valid, root =>
            root["blocks"]![0]!["content"]!["parameters"]![0]!["componentIdentity"] = "component.missing")).Failure!.Code);
        Assert.Equal("patch.request.invalid-content", DocumentationPatchValidator.ParseRequest(Mutate(valid, root =>
        {
            var components = root["blocks"]![0]!["applicableComponents"]!.AsArray();
            components.Insert(2, new JsonObject
            {
                ["kind"] = "parameter",
                ["identity"] = "component.parameter.1",
                ["name"] = "value",
            });
        })).Failure!.Code);
        Assert.Equal("patch.request.invalid-order", DocumentationPatchValidator.ParseRequest(Mutate(valid, root =>
        {
            var exceptions = root["blocks"]![0]!["content"]!["exceptions"]!.AsArray();
            var first = exceptions[0]!.DeepClone();
            var second = exceptions[1]!.DeepClone();
            exceptions[0] = second;
            exceptions[1] = first;
        })).Failure!.Code);
    }

    [Fact]
    public void ResultCorrelationAndOutcomeSemantics_FailClosed()
    {
        var request = ParseRequest(ReadFixture("valid", "repository-request.json"));
        var accepted = ReadFixture("valid", "accepted-result.json");

        var zeroChange = ParseResult(Mutate(accepted, root =>
        {
            root["changedFiles"] = new JsonArray();
            root["changedDocumentationBlockCount"] = 0;
        }));
        Assert.Equal("patch.result.invalid-outcome", DocumentationPatchValidator.ValidateResult(request, zeroChange).Code);

        var substitutedContext = ParseResult(Mutate(accepted, root =>
            root["context"]!["repositoryContextRef"] = "repoctx-ffeeddccbbaa99887766554433221100"));
        Assert.Equal("patch.result.invalid-correlation", DocumentationPatchValidator.ValidateResult(request, substitutedContext).Code);

        var substitutedTarget = ParseResult(Mutate(accepted, root =>
            root["targets"]![0]!["symbolRef"]!["documentationCommentId"] = "T:Synthetic.Other"));
        Assert.Equal("patch.result.invalid-correlation", DocumentationPatchValidator.ValidateResult(request, substitutedTarget).Code);

        var identicalHash = ParseResult(Mutate(accepted, root =>
            root["changedFiles"]![0]!["candidateFileSha256"] =
                root["changedFiles"]![0]!["originalFileSha256"]!.GetValue<string>()));
        Assert.Equal("patch.result.invalid-outcome", DocumentationPatchValidator.ValidateResult(request, identicalHash).Code);

        var stale = ReadFixture("valid", "stale-result.json");
        var rootStaleWithEvaluatedTarget = ParseResult(Mutate(stale, root =>
            root["diagnostics"]![0]!["code"] = "patch.stale.repository-context"));
        Assert.Equal("patch.result.invalid-outcome",
            DocumentationPatchValidator.ValidateResult(request, rootStaleWithEvaluatedTarget).Code);
        var rootStale = ParseResult(Mutate(stale, root =>
        {
            root["diagnostics"]![0]!["code"] = "patch.stale.repository-context";
            root["targets"]![0]!["status"] = "not-evaluated";
        }));
        Assert.True(DocumentationPatchValidator.ValidateResult(request, rootStale).IsValid);
    }

    [Fact]
    public void RegistryAndSchemas_AreClosedSelfContainedAndNumericallyAligned()
    {
        var registry = JsonNode.Parse(ReadContractFile("v1.registry.json"))!.AsObject();
        Assert.Equal(DocumentationPatchValidator.MaximumArtifactUtf8Bytes,
            registry["limits"]!["artifactUtf8Bytes"]!.GetValue<int>());
        Assert.Equal(DocumentationPatchValidator.MaximumLogicalLineScalars,
            registry["limits"]!["logicalLineScalars"]!.GetValue<int>());
        Assert.Equal(DocumentationPatchValidator.MaximumBlockTextScalars,
            registry["limits"]!["logicalTextScalarsPerBlock"]!.GetValue<int>());
        Assert.Equal(DocumentationPatchValidator.InvariantIds,
            registry["invariants"]!.AsArray().Select(item => item!.GetValue<string>()).ToArray());

        foreach (var name in new[] { "v1.request.schema.json", "v1.validation-result.schema.json" })
        {
            var schema = JsonNode.Parse(ReadContractFile(name))!;
            AssertLocalReferences(schema);
            AssertClosedObjects(schema);
        }
    }

    private static DocumentationPatchRequest ParseRequest(byte[] bytes)
    {
        var parsed = DocumentationPatchValidator.ParseRequest(bytes);
        Assert.True(parsed.IsValid, parsed.Failure?.Code);
        return parsed.Request!;
    }

    private static DocumentationPatchValidationResult ParseResult(byte[] bytes)
    {
        var parsed = DocumentationPatchValidator.ParseValidationResult(bytes);
        Assert.True(parsed.IsValid, parsed.Failure?.Code);
        return parsed.Result!;
    }

    private static DocumentationPatchRepositoryLocator BuildRepositoryLocator(
        string encoding,
        string digest,
        int start,
        int end)
    {
        var bytes = Mutate(ReadFixture("valid", "repository-request.json"), root =>
        {
            var locator = root["blocks"]![0]!["locator"]!;
            locator["encoding"] = encoding;
            locator["originalFileSha256"] = digest;
            locator["declarationSpan"]!["start"] = start;
            locator["declarationSpan"]!["end"] = end;
        });
        return Assert.IsType<DocumentationPatchRepositoryLocator>(ParseRequest(bytes).Blocks[0].Locator);
    }

    private static byte[] Mutate(byte[] original, Action<JsonObject> mutation)
    {
        var root = JsonNode.Parse(original)!.AsObject();
        mutation(root);
        return JsonSerializer.SerializeToUtf8Bytes(root);
    }

    private static JsonSchema LoadSchema(string name) => JsonSchema.FromText(File.ReadAllText(
        Path.Combine(FindRepositoryRoot(), "schemas", "documentation-patch", name)));

    private static bool Evaluate(JsonSchema schema, byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        return schema.Evaluate(document.RootElement).IsValid;
    }

    private static void AssertSchemaValid(JsonSchema schema, byte[] bytes, string caseId) =>
        Assert.True(Evaluate(schema, bytes), caseId);

    private static byte[] ReadFixture(params string[] relativeParts)
    {
        var parts = new[] { FindRepositoryRoot(), "tests", "fixtures", "documentation-patch", "v1" }
            .Concat(relativeParts)
            .ToArray();
        return File.ReadAllBytes(Path.Combine(parts));
    }

    private static byte[] ReadContractFile(string name) => File.ReadAllBytes(Path.Combine(
        FindRepositoryRoot(), "schemas", "documentation-patch", name));

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

    private static void AssertClosedObjects(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            if (obj["type"]?.GetValue<string>() == "object")
            {
                Assert.True(obj["additionalProperties"]?.GetValue<bool>() == false);
            }

            foreach (var value in obj.Select(property => property.Value).Where(value => value is not null))
            {
                AssertClosedObjects(value!);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array.Where(item => item is not null))
            {
                AssertClosedObjects(item!);
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

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
