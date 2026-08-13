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
        foreach (var name in new[]
        {
            "repository-request.json",
            "inherit-doc-request.json",
            "same-file-request.json",
            "mixed-locators-request.json",
        })
        {
            var bytes = ReadFixture("valid", name);
            AssertSchemaValid(RequestSchema.Value, bytes, name);
            var parsed = DocumentationPatchValidator.ParseRequest(bytes);
            Assert.True(parsed.IsValid, name);
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                parsed.Request!.ArtifactSha256);
        }

        foreach (var (requestName, resultName) in new[]
        {
            ("inherit-doc-request.json", "accepted-result.json"),
            ("same-file-request.json", "same-file-accepted-result.json"),
            ("repository-request.json", "stale-result.json"),
            ("repository-request.json", "rejected-no-op-result.json"),
        })
        {
            var request = ParseRequest(ReadFixture("valid", requestName));
            var bytes = ReadFixture("valid", resultName);
            AssertSchemaValid(ResultSchema.Value, bytes, resultName);
            var parsed = DocumentationPatchValidator.ParseValidationResult(bytes);
            Assert.True(parsed.IsValid, resultName);
            Assert.True(DocumentationPatchValidator.ValidateResult(request, parsed.Result!).IsValid, resultName);
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
        var missingRequestDigest = Mutate(result, root => root.Remove("patchRequestSha256"));
        Assert.False(Evaluate(ResultSchema.Value, missingRequestDigest));
        Assert.Equal("patch.result.invalid-shape",
            DocumentationPatchValidator.ParseValidationResult(missingRequestDigest).Failure!.Code);
        var malformedRequestDigest = Mutate(result, root =>
            root["patchRequestSha256"] = new string('A', 64));
        Assert.False(Evaluate(ResultSchema.Value, malformedRequestDigest));
        Assert.Equal("patch.result.invalid-vocabulary",
            DocumentationPatchValidator.ParseValidationResult(malformedRequestDigest).Failure!.Code);

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
                caseId == "utf8-lf" ? 58 : 59);
            var check = DocumentationPatchValidator.ValidateRepositorySource(locator, bytes);
            if (caseId == "malformed-utf8")
            {
                Assert.Equal("patch.stale.source-encoding", check.Code);
            }
            else
            {
                Assert.True(check.IsValid, caseId);
                Assert.Contains("public delegate T Widget<T>(T value);", check.DecodedText, StringComparison.Ordinal);
            }
        }

        var lfBytes = Convert.FromBase64String("bmFtZXNwYWNlIFN5bnRoZXRpYzsKcHVibGljIGRlbGVnYXRlIFQgV2lkZ2V0PFQ+KFQgdmFsdWUpOwo=");
        var valid = BuildRepositoryLocator(
            "utf-8", "e55dcc5577377f530ff0a0a085cf775a24ce236095d83f7db99cba198fc16bb8", 21, 58);
        var drifted = lfBytes.ToArray();
        drifted[^1] = (byte)' ';
        Assert.Equal("patch.stale.source-bytes", DocumentationPatchValidator.ValidateRepositorySource(valid, drifted).Code);

        var wrongEncoding = BuildRepositoryLocator(
            "utf-8", "7c240ee5102683d7836f2270896e24a1ebdd0f4caa1e467ec8ff03c0f8b141a5", 22, 59);
        var bomBytes = Convert.FromBase64String(
            "77u/bmFtZXNwYWNlIFN5bnRoZXRpYzsNCnB1YmxpYyBkZWxlZ2F0ZSBUIFdpZGdldDxUPihUIHZhbHVlKTsNCg==");
        Assert.Equal("patch.stale.source-encoding", DocumentationPatchValidator.ValidateRepositorySource(wrongEncoding, bomBytes).Code);

        var wrongSpan = BuildRepositoryLocator(
            "utf-8", "e55dcc5577377f530ff0a0a085cf775a24ce236095d83f7db99cba198fc16bb8", 21, 200);
        Assert.Equal("patch.stale.source-span", DocumentationPatchValidator.ValidateRepositorySource(wrongSpan, lfBytes).Code);
    }

    [Fact]
    public void AcceptedResults_AreRecomputedFromPinnedCandidateBytes()
    {
        using var vectors = JsonDocument.Parse(ReadFixture("candidate-byte-vectors.json"));
        foreach (var vector in vectors.RootElement.GetProperty("vectors").EnumerateArray())
        {
            var caseId = vector.GetProperty("caseId").GetString()!;
            Assert.Equal("utf-8", vector.GetProperty("encoding").GetString());
            var original = Convert.FromBase64String(vector.GetProperty("originalBase64").GetString()!);
            var candidate = Convert.FromBase64String(vector.GetProperty("candidateBase64").GetString()!);
            Assert.Equal(vector.GetProperty("expectedOriginalLength").GetInt32(), original.Length);
            Assert.Equal(vector.GetProperty("expectedCandidateLength").GetInt32(), candidate.Length);
            Assert.Equal(vector.GetProperty("expectedOriginalSha256").GetString(),
                Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant());
            Assert.Equal(vector.GetProperty("expectedCandidateSha256").GetString(),
                Convert.ToHexString(SHA256.HashData(candidate)).ToLowerInvariant());

            var reconstructed = candidate.ToList();
            var candidateDocumentationBytes = 0;
            var candidateDocumentationLines = 0;
            var regions = vector.GetProperty("documentationRegions").EnumerateArray().ToArray();
            foreach (var region in regions)
            {
                var offset = region.GetProperty("offset").GetInt32();
                var byteLength = region.GetProperty("byteLength").GetInt32();
                var regionBytes = Convert.FromBase64String(region.GetProperty("base64").GetString()!);
                Assert.Equal(byteLength, regionBytes.Length);
                Assert.Equal(regionBytes, candidate.AsSpan(offset, byteLength).ToArray());
                Assert.Equal(region.GetProperty("physicalLineCount").GetInt32(),
                    regionBytes.Count(value => value == (byte)'\n'));
                candidateDocumentationBytes += byteLength;
                candidateDocumentationLines += region.GetProperty("physicalLineCount").GetInt32();
            }

            foreach (var region in regions.OrderByDescending(
                         item => item.GetProperty("offset").GetInt32()))
            {
                reconstructed.RemoveRange(
                    region.GetProperty("offset").GetInt32(),
                    region.GetProperty("byteLength").GetInt32());
            }

            Assert.Equal<byte>(original, reconstructed);
            var request = ParseRequest(ReadFixture(
                vector.GetProperty("requestFile").GetString()!.Split('/')));
            var result = ParseResult(ReadFixture(
                vector.GetProperty("resultFile").GetString()!.Split('/')));
            Assert.Equal(request.ArtifactSha256, result.PatchRequestSha256);
            Assert.True(DocumentationPatchValidator.ValidateResult(request, result).IsValid, caseId);
            var changedFile = Assert.Single(result.ChangedFiles);
            Assert.Equal(vector.GetProperty("expectedOriginalSha256").GetString(),
                changedFile.OriginalFileSha256);
            Assert.Equal(vector.GetProperty("expectedCandidateSha256").GetString(),
                changedFile.CandidateFileSha256);
            Assert.Equal(candidateDocumentationBytes, changedFile.CandidateDocumentationByteCount);
            Assert.Equal(candidateDocumentationLines, changedFile.CandidateDocumentationLineCount);
            Assert.Equal(regions.Length, changedFile.ChangedDocumentationBlockCount);
            Assert.Equal(regions.Length, result.ChangedDocumentationBlockCount);
        }
    }

    [Fact]
    public void GeneratedSourceLocator_UsesStrictUtf8HashAndUtf16Span()
    {
        var request = ParseRequest(ReadFixture("valid", "mixed-locators-request.json"));
        var locator = Assert.IsAssignableFrom<DocumentationPatchGeneratedLocator>(request.Blocks[1].Locator);
        const string source = "namespace Synthetic;\npublic delegate T Widget<T>(T value);\n";

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
                ["identity"] = "parameter/1",
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

        foreach (var (mutation, expectedCode) in new (Action<JsonObject>, string)[]
        {
            (root => root["blocks"]![0]!["applicableComponents"]![0]!["identity"] = "component.type-parameter.0",
                "patch.request.invalid-vocabulary"),
            (root => root["blocks"]![0]!["content"]!["parameters"]![0]!["componentIdentity"] = "component.parameter.0",
                "patch.request.invalid-content"),
        })
        {
            var dottedAlias = Mutate(valid, mutation);
            Assert.False(Evaluate(RequestSchema.Value, dottedAlias));
            Assert.Equal(expectedCode,
                DocumentationPatchValidator.ParseRequest(dottedAlias).Failure!.Code);
        }

        Assert.Equal("patch.request.invalid-content", DocumentationPatchValidator.ParseRequest(Mutate(valid, root =>
            root["blocks"]![0]!["content"]!["exceptions"]![0]!["typeDocumentationId"] =
                "T:Synthetic.\uFFFEException")).Failure!.Code);
        Assert.Equal("patch.request.invalid-vocabulary", DocumentationPatchValidator.ParseRequest(Mutate(valid, root =>
            root["blocks"]![0]!["applicableComponents"]![0]!["name"] = "T\uFFFF")).Failure!.Code);
    }

    [Fact]
    public void RequestValidation_AppliesCategoryPrecedenceAcrossAllBlocks()
    {
        var danglingReference = Mutate(ReadFixture("valid", "same-file-request.json"), root =>
        {
            root["blocks"]![0]!["provenanceRefs"]![0] = "prov.missing";
            root["blocks"]![1]!["editKind"] = "invalid";
        });

        AssertVocabularyPrecedesEarlierFailure(danglingReference);

        var duplicateReference = Mutate(ReadFixture("valid", "same-file-request.json"), root =>
        {
            root["blocks"]![0]!["provenanceRefs"]!.AsArray().Add("prov.synthetic");
            root["blocks"]![1]!["editKind"] = "invalid";
        });

        AssertVocabularyPrecedesEarlierFailure(duplicateReference);

        var sameBlockOrdering = Mutate(ReadFixture("valid", "same-file-request.json"), root =>
        {
            var components = root["blocks"]![0]!["applicableComponents"]!.AsArray();
            var first = components[0]!.DeepClone();
            components[0] = components[1]!.DeepClone();
            components[1] = first;
            root["blocks"]![0]!["content"]!["kind"] = "invalid";
        });

        var sameBlockFailure = DocumentationPatchValidator.ParseRequest(sameBlockOrdering).Failure;
        Assert.Equal("patch.request.invalid-vocabulary", sameBlockFailure!.Code);
        Assert.Equal("/blocks/0/content/kind", sameBlockFailure.Pointer);

        var catalogOrdering = Mutate(ReadFixture("valid", "same-file-request.json"), root =>
        {
            var catalog = root["provenanceCatalog"]!.AsArray();
            catalog.Add("prov.synthetic");
            catalog.Add("invalid id");
        });

        var catalogFailure = DocumentationPatchValidator.ParseRequest(catalogOrdering).Failure;
        Assert.Equal("patch.request.invalid-vocabulary", catalogFailure!.Code);
        Assert.Equal("/provenanceCatalog/2", catalogFailure.Pointer);

        static void AssertVocabularyPrecedesEarlierFailure(byte[] bytes)
        {
            var failure = DocumentationPatchValidator.ParseRequest(bytes).Failure;
            Assert.Equal("patch.request.invalid-vocabulary", failure!.Code);
            Assert.Equal("/blocks/1/editKind", failure.Pointer);
        }
    }

    [Fact]
    public void SchemaAndCoreRejectTheSameLogicalLineAndComponentIdentityOverflows()
    {
        var valid = ReadFixture("valid", "repository-request.json");
        var longLine = Mutate(valid, root =>
            root["blocks"]![0]!["content"]!["summaryLines"]![0] = new string('a', 2_049));
        Assert.False(Evaluate(RequestSchema.Value, longLine));
        Assert.False(DocumentationPatchValidator.ParseRequest(longLine).IsValid);

        var longIdentity = "parameter/" + new string('9', 119);
        Assert.Equal(129, longIdentity.Length);
        var longComponent = Mutate(valid, root =>
        {
            root["blocks"]![0]!["applicableComponents"]![1]!["identity"] = longIdentity;
            root["blocks"]![0]!["content"]!["parameters"]![0]!["componentIdentity"] = longIdentity;
        });
        Assert.False(Evaluate(RequestSchema.Value, longComponent));
        Assert.False(DocumentationPatchValidator.ParseRequest(longComponent).IsValid);
    }

    [Fact]
    public void RepositoryFixture_ReusesM1IdentitiesAndCommitsTheCompleteDeclaration()
    {
        var request = ParseRequest(ReadFixture("valid", "repository-request.json"));
        var block = Assert.Single(request.Blocks);
        Assert.Equal("T:Synthetic.Widget`1", block.SymbolRef.DocumentationCommentId);
        Assert.Equal(
            new[] { "type-parameter/0", "parameter/0", "return" },
            block.ApplicableComponents.Select(component => component.Identity));

        using var vectors = JsonDocument.Parse(ReadFixture("source-byte-vectors.json"));
        var lf = vectors.RootElement.GetProperty("vectors")[0];
        var bytes = Convert.FromBase64String(lf.GetProperty("base64").GetString()!);
        var locator = Assert.IsType<DocumentationPatchRepositoryLocator>(block.Locator);
        var check = DocumentationPatchValidator.ValidateRepositorySource(locator, bytes);
        Assert.True(check.IsValid);
        Assert.Equal(
            "public delegate T Widget<T>(T value);",
            check.DecodedText![locator.DeclarationSpan.Start..locator.DeclarationSpan.End]);
    }

    [Theory]
    [InlineData("CTX.synthetic")]
    [InlineData("ctx:synthetic")]
    [InlineData(".ctx")]
    [InlineData("ctx_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void CompilationContextRef_MustRemainInTheM1IdentityDomain(string value)
    {
        var bytes = Mutate(ReadFixture("valid", "repository-request.json"), root =>
            root["blocks"]![0]!["symbolRef"]!["compilationContextRef"] = value);
        Assert.False(Evaluate(RequestSchema.Value, bytes));
        Assert.Equal("patch.request.invalid-vocabulary",
            DocumentationPatchValidator.ParseRequest(bytes).Failure!.Code);
    }

    [Fact]
    public void CompilationContextRef_RejectsTheM1LengthOverflow()
    {
        var bytes = Mutate(ReadFixture("valid", "repository-request.json"), root =>
            root["blocks"]![0]!["symbolRef"]!["compilationContextRef"] = new string('a', 129));
        Assert.False(Evaluate(RequestSchema.Value, bytes));
        Assert.Equal("patch.request.invalid-vocabulary",
            DocumentationPatchValidator.ParseRequest(bytes).Failure!.Code);
    }

    [Fact]
    public void ResultCorrelationAndOutcomeSemantics_FailClosed()
    {
        var structuredRequest = ParseRequest(ReadFixture("valid", "repository-request.json"));
        var acceptedRequest = ParseRequest(ReadFixture("valid", "inherit-doc-request.json"));
        var accepted = ReadFixture("valid", "accepted-result.json");
        var acceptedResult = ParseResult(accepted);
        Assert.True(DocumentationPatchValidator.ValidateResult(acceptedRequest, acceptedResult).IsValid);
        Assert.Equal("patch.result.invalid-correlation",
            DocumentationPatchValidator.ValidateResult(structuredRequest, acceptedResult).Code);

        var zeroChange = ParseResult(Mutate(accepted, root =>
        {
            root["changedFiles"] = new JsonArray();
            root["changedDocumentationBlockCount"] = 0;
        }));
        Assert.Equal("patch.result.invalid-outcome", DocumentationPatchValidator.ValidateResult(acceptedRequest, zeroChange).Code);

        var substitutedContext = ParseResult(Mutate(accepted, root =>
            root["context"]!["repositoryContextRef"] = "repoctx-ffeeddccbbaa99887766554433221100"));
        Assert.Equal("patch.result.invalid-correlation", DocumentationPatchValidator.ValidateResult(acceptedRequest, substitutedContext).Code);

        var substitutedTarget = ParseResult(Mutate(accepted, root =>
            root["targets"]![0]!["symbolRef"]!["documentationCommentId"] = "T:Synthetic.Other"));
        Assert.Equal("patch.result.invalid-correlation", DocumentationPatchValidator.ValidateResult(acceptedRequest, substitutedTarget).Code);

        var identicalHash = ParseResult(Mutate(accepted, root =>
            root["changedFiles"]![0]!["candidateFileSha256"] =
                root["changedFiles"]![0]!["originalFileSha256"]!.GetValue<string>()));
        Assert.Equal("patch.result.invalid-outcome", DocumentationPatchValidator.ValidateResult(acceptedRequest, identicalHash).Code);

        var stale = ReadFixture("valid", "stale-result.json");
        var rootStaleWithEvaluatedTarget = ParseResult(Mutate(stale, root =>
        {
            root["diagnostics"]![0]!["code"] = "patch.stale.repository-context";
            root["diagnostics"]![0]!["blockId"] = null;
            root["diagnostics"]![0]!["path"] = null;
        }));
        Assert.Equal("patch.result.invalid-outcome",
            DocumentationPatchValidator.ValidateResult(structuredRequest, rootStaleWithEvaluatedTarget).Code);
        var rootStale = ParseResult(Mutate(stale, root =>
        {
            root["diagnostics"]![0]!["code"] = "patch.stale.repository-context";
            root["diagnostics"]![0]!["blockId"] = null;
            root["diagnostics"]![0]!["path"] = null;
            root["targets"]![0]!["status"] = "not-evaluated";
        }));
        Assert.True(DocumentationPatchValidator.ValidateResult(structuredRequest, rootStale).IsValid);
    }

    [Fact]
    public void AcceptedResultObservations_MustAccountForEveryRequestedBlock()
    {
        var request = ParseRequest(ReadFixture("valid", "inherit-doc-request.json"));
        var accepted = ReadFixture("valid", "accepted-result.json");

        foreach (var mutation in new Action<JsonObject>[]
        {
            root =>
            {
                root["changedFiles"]![0]!["changedDocumentationBlockCount"] = 2;
                root["changedDocumentationBlockCount"] = 2;
            },
            root =>
            {
                root["changedFiles"]![0]!["candidateDocumentationByteCount"] = 0;
                root["changedFiles"]![0]!["candidateDocumentationLineCount"] = 0;
            },
            root =>
            {
                root["changedFiles"]![0]!["originalDocumentationByteCount"] = 1;
                root["changedFiles"]![0]!["originalDocumentationLineCount"] = 1;
            },
        })
        {
            var result = ParseResult(Mutate(accepted, mutation));
            Assert.Equal("patch.result.invalid-outcome",
                DocumentationPatchValidator.ValidateResult(request, result).Code);
        }

        var replacementRequest = ParseRequest(Mutate(
            ReadFixture("valid", "inherit-doc-request.json"),
            root => root["blocks"]![0]!["editKind"] = "replace"));
        var replacementResult = ParseResult(Mutate(accepted, root =>
            root["patchRequestSha256"] = replacementRequest.ArtifactSha256));
        Assert.Equal("patch.result.invalid-outcome", DocumentationPatchValidator.ValidateResult(
            replacementRequest, replacementResult).Code);
    }

    [Fact]
    public void SameFileTargets_UseNumericSpanOrderAndAtomicResultAccounting()
    {
        var requestBytes = ReadFixture("valid", "same-file-request.json");
        AssertSchemaValid(RequestSchema.Value, requestBytes, "same-file-request");
        var request = ParseRequest(requestBytes);
        Assert.Equal(2, request.Blocks.Length);
        var firstLocator = Assert.IsType<DocumentationPatchRepositoryLocator>(request.Blocks[0].Locator);
        var secondLocator = Assert.IsType<DocumentationPatchRepositoryLocator>(request.Blocks[1].Locator);
        Assert.Equal(21, firstLocator.DeclarationSpan.Start);
        Assert.Equal(100, secondLocator.DeclarationSpan.Start);

        using var vectors = JsonDocument.Parse(ReadFixture("candidate-byte-vectors.json"));
        var vector = vectors.RootElement.GetProperty("vectors").EnumerateArray().Single(
            item => item.GetProperty("caseId").GetString() == "same-file-two-targets-lf");
        var original = Convert.FromBase64String(vector.GetProperty("originalBase64").GetString()!);
        Assert.True(DocumentationPatchValidator.ValidateRepositorySource(firstLocator, original).IsValid);
        Assert.True(DocumentationPatchValidator.ValidateRepositorySource(secondLocator, original).IsValid);

        var reversed = Mutate(requestBytes, root =>
        {
            var blocks = root["blocks"]!.AsArray();
            var first = blocks[0]!.DeepClone();
            blocks[0] = blocks[1]!.DeepClone();
            blocks[1] = first;
        });
        Assert.Equal("patch.request.invalid-order",
            DocumentationPatchValidator.ParseRequest(reversed).Failure!.Code);

        var duplicateSymbol = Mutate(requestBytes, root =>
            root["blocks"]![1]!["symbolRef"]!["documentationCommentId"] = "T:Synthetic.First`1");
        Assert.Equal("patch.request.invalid-order",
            DocumentationPatchValidator.ParseRequest(duplicateSymbol).Failure!.Code);

        var duplicateLocator = Mutate(requestBytes, root =>
        {
            root["blocks"]![1]!["locator"]!["declarationSpan"]!["start"] = 21;
            root["blocks"]![1]!["locator"]!["declarationSpan"]!["end"] = 57;
        });
        Assert.Equal("patch.request.invalid-order",
            DocumentationPatchValidator.ParseRequest(duplicateLocator).Failure!.Code);

        var acceptedBytes = ReadFixture("valid", "same-file-accepted-result.json");
        var accepted = ParseResult(acceptedBytes);
        Assert.True(DocumentationPatchValidator.ValidateResult(request, accepted).IsValid);

        var inconsistentHashRequest = ParseRequest(Mutate(requestBytes, root =>
            root["blocks"]![1]!["locator"]!["originalFileSha256"] = new string('a', 64)));
        var inconsistentlyCorrelatedResult = ParseResult(Mutate(acceptedBytes, root =>
        {
            root["patchRequestSha256"] = inconsistentHashRequest.ArtifactSha256;
            root["targets"]![1]!["locator"]!["originalFileSha256"] = new string('a', 64);
        }));
        Assert.Equal("patch.result.invalid-correlation", DocumentationPatchValidator.ValidateResult(
            inconsistentHashRequest,
            inconsistentlyCorrelatedResult).Code);

        var inconsistentEncodingRequest = ParseRequest(Mutate(requestBytes, root =>
            root["blocks"]![1]!["locator"]!["encoding"] = "utf-8-bom"));
        var inconsistentlyEncodedResult = ParseResult(Mutate(acceptedBytes, root =>
        {
            root["patchRequestSha256"] = inconsistentEncodingRequest.ArtifactSha256;
            root["targets"]![1]!["locator"]!["encoding"] = "utf-8-bom";
        }));
        Assert.Equal("patch.result.invalid-correlation", DocumentationPatchValidator.ValidateResult(
            inconsistentEncodingRequest,
            inconsistentlyEncodedResult).Code);

        foreach (var mutation in new Action<JsonObject>[]
        {
            root =>
            {
                root["changedFiles"]![0]!["changedDocumentationBlockCount"] = 1;
                root["changedDocumentationBlockCount"] = 1;
            },
            root => root["targets"]![1]!["status"] = "invalid",
            root => root["changedFiles"] = new JsonArray(),
            root =>
            {
                root["changedFiles"]![0]!["candidateDocumentationByteCount"] = 0;
                root["changedFiles"]![0]!["candidateDocumentationLineCount"] = 0;
            },
        })
        {
            var partial = ParseResult(Mutate(acceptedBytes, mutation));
            Assert.False(DocumentationPatchValidator.ValidateResult(request, partial).IsValid);
        }
    }

    [Fact]
    public void ResultDiagnostics_AreDeterministicAndRequestCorrelated()
    {
        var request = ParseRequest(ReadFixture("valid", "repository-request.json"));
        var stale = ReadFixture("valid", "stale-result.json");

        var warning = DocumentationPatchValidator.ParseValidationResult(Mutate(stale, root =>
            root["diagnostics"]![0]!["severity"] = "warning"));
        Assert.Equal("patch.result.invalid-vocabulary", warning.Failure!.Code);

        var duplicate = ParseResult(Mutate(stale, root =>
            root["diagnostics"]!.AsArray().Add(root["diagnostics"]![0]!.DeepClone())));
        Assert.Equal("patch.result.invalid-outcome",
            DocumentationPatchValidator.ValidateResult(request, duplicate).Code);

        var danglingBlock = ParseResult(Mutate(stale, root =>
            root["diagnostics"]![0]!["blockId"] = "block.other"));
        Assert.Equal("patch.result.invalid-correlation",
            DocumentationPatchValidator.ValidateResult(request, danglingBlock).Code);

        var wrongPath = ParseResult(Mutate(stale, root =>
            root["diagnostics"]![0]!["path"] = "src/Synthetic/Other.cs"));
        Assert.Equal("patch.result.invalid-correlation",
            DocumentationPatchValidator.ValidateResult(request, wrongPath).Code);

        var wrongStatus = ParseResult(Mutate(stale, root =>
            root["targets"]![0]!["status"] = "not-evaluated"));
        Assert.Equal("patch.result.invalid-outcome",
            DocumentationPatchValidator.ValidateResult(request, wrongStatus).Code);

        var wrongPrimary = ParseResult(Mutate(stale, root =>
        {
            var sourceEncoding = root["diagnostics"]![0]!.DeepClone();
            sourceEncoding["code"] = "patch.stale.source-encoding";
            root["diagnostics"]!.AsArray().Add(sourceEncoding);
        }));
        Assert.Equal("patch.result.invalid-outcome",
            DocumentationPatchValidator.ValidateResult(request, wrongPrimary).Code);

        var unsortedSecondaries = ParseResult(Mutate(stale, root =>
        {
            root["diagnostics"]![0]!["code"] = "patch.stale.compilation-context";
            var sourceSpan = root["diagnostics"]![0]!.DeepClone();
            sourceSpan["code"] = "patch.stale.source-span";
            var sourceBytes = root["diagnostics"]![0]!.DeepClone();
            sourceBytes["code"] = "patch.stale.source-bytes";
            root["diagnostics"]!.AsArray().Add(sourceSpan);
            root["diagnostics"]!.AsArray().Add(sourceBytes);
        }));
        Assert.Equal("patch.result.invalid-outcome",
            DocumentationPatchValidator.ValidateResult(request, unsortedSecondaries).Code);
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

        var requestSchema = JsonNode.Parse(ReadContractFile("v1.request.schema.json"))!;
        var logicalLineLimit = registry["limits"]!["logicalLineScalars"]!.GetValue<int>();
        var identifierLimit = registry["limits"]!["identifierScalars"]!.GetValue<int>();
        Assert.Equal(logicalLineLimit,
            requestSchema["$defs"]!["logicalLines"]!["items"]!["maxLength"]!.GetValue<int>());
        Assert.Equal(identifierLimit,
            requestSchema["$defs"]!["namedComponent"]!["properties"]!["identity"]!["maxLength"]!.GetValue<int>());
        Assert.Equal(identifierLimit,
            requestSchema["$defs"]!["namedContentEntry"]!["properties"]!["componentIdentity"]!["maxLength"]!.GetValue<int>());

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
