using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContractScribe.Core;
using ContractScribe.Patching;
using ContractScribe.Patching.Resolution;
using ContractScribe.Patching.Validation;
using ContractScribe.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace ContractScribe.Tests;

public sealed class DocumentationPatchValidationTests
{
    [Theory]
    [InlineData(DocumentationPatchRepositoryEncoding.Utf8, "\n")]
    [InlineData(DocumentationPatchRepositoryEncoding.Utf8Bom, "\r\n")]
    [InlineData(DocumentationPatchRepositoryEncoding.Utf16LittleEndianBom, "\n")]
    [InlineData(DocumentationPatchRepositoryEncoding.Utf16BigEndianBom, "\r\n")]
    public void ExactCapturedDocumentationEdit_IsAcceptedAcrossRepresentations(
        DocumentationPatchRepositoryEncoding encoding,
        string newline)
    {
        var source = string.Join(newline,
            "namespace N;",
            "public class C",
            "{",
            "    public void M() { }",
            "}",
            string.Empty);
        using var fixture = ValidationFixture.Create(source, encoding);
        var candidateSource = source.Replace(
            "    public void M() { }",
            "    /// <inheritdoc/>" + newline + "    public void M() { }",
            StringComparison.Ordinal);

        var decision = fixture.ValidateCaptured(candidateSource);

        Assert.True(decision.IsAccepted, decision.FailureCode);
        var changed = Assert.Single(decision.ChangedFiles);
        Assert.Equal("Sample.cs", changed.Path);
        Assert.Equal(1, changed.ChangedDocumentationBlockCount);
        Assert.Equal(0, changed.OriginalDocumentationByteCount);
        Assert.Equal(0, changed.OriginalDocumentationLineCount);
        Assert.Equal(1, changed.CandidateDocumentationLineCount);
        Assert.All(decision.Invariants, invariant =>
            Assert.Equal(DocumentationPatchInvariantStatus.Passed, invariant.Status));
        Assert.NotNull(decision.RoslynEvidence);
        Assert.True(decision.RoslynEvidence!.ValidatedCompilationContextCount > 0);
    }

    [Fact]
    public void OrdinaryCommentOrWhitespaceChangeOutsideSelectedRegion_FailsClosed()
    {
        const string source =
            "namespace N;\npublic class C\n{\n    // ordinary\n    public void M() { }\n}\n";
        using var fixture = ValidationFixture.Create(
            source,
            DocumentationPatchRepositoryEncoding.Utf8);
        var candidate = source.Replace(
                "    public void M() { }",
                "    /// <inheritdoc/>\n    public void M() { }",
                StringComparison.Ordinal)
            .Replace("// ordinary", "// changed ordinary", StringComparison.Ordinal);

        var decision = fixture.ValidateCaptured(candidate);

        Assert.False(decision.IsAccepted);
        Assert.Equal("patch.rejected.unsafe-change", decision.FailureCode);
    }

    [Theory]
    [InlineData((int)DocumentationPatchCandidateValidationCorruption.ParseOptions)]
    [InlineData((int)DocumentationPatchCandidateValidationCorruption.MetadataReferences)]
    [InlineData((int)DocumentationPatchCandidateValidationCorruption.CompilationContext)]
    public void CorruptedReconstructionInputsFailClosed(int corruptionValue)
    {
        const string source =
            "namespace N;\npublic class C\n{\n    public void M() { }\n}\n";
        using var fixture = ValidationFixture.Create(
            source,
            DocumentationPatchRepositoryEncoding.Utf8);
        var candidate = source.Replace(
            "    public void M() { }",
            "    /// <inheritdoc/>\n    public void M() { }",
            StringComparison.Ordinal);

        var result = fixture.ValidateRoslynCandidate(
            candidate,
            (DocumentationPatchCandidateValidationCorruption)corruptionValue);

        Assert.False(result.IsValid);
        Assert.Equal("patch.rejected.unsafe-change", result.FailureCode);
    }

    [Fact]
    public void ReplacementAndMultiTargetEditsProduceExactCountsAndHashes()
    {
        const string source =
            "namespace N;\npublic class C\n{\n    /// <summary>Old A.</summary>\n    public void A() { }\n\n    /// <summary>Old B.</summary>\n    public void B() { }\n}\n";
        using var fixture = ValidationFixture.Create(
            source,
            DocumentationPatchRepositoryEncoding.Utf8,
            DocumentationPatchEditKind.Replace,
            ["M:N.C.A", "M:N.C.B"]);
        var candidate = source
            .Replace(
                "    /// <summary>Old A.</summary>",
                "    /// <inheritdoc/>",
                StringComparison.Ordinal)
            .Replace(
                "    /// <summary>Old B.</summary>",
                "    /// <inheritdoc/>",
                StringComparison.Ordinal);

        var decision = fixture.ValidateCaptured(candidate);

        Assert.True(decision.IsAccepted, decision.FailureCode);
        var changed = Assert.Single(decision.ChangedFiles);
        Assert.Equal(2, changed.ChangedDocumentationBlockCount);
        Assert.Equal(ValidationFixture.Sha256(ValidationFixture.Encode(candidate,
            DocumentationPatchRepositoryEncoding.Utf8)), changed.CandidateFileSha256);
        Assert.Equal(2, changed.OriginalDocumentationLineCount);
        Assert.Equal(2, changed.CandidateDocumentationLineCount);
    }

    [Fact]
    public void PartialMultiTargetApplicationFailsAtomically()
    {
        const string source =
            "namespace N;\npublic class C\n{\n    public void A() { }\n    public void B() { }\n}\n";
        using var fixture = ValidationFixture.Create(
            source,
            DocumentationPatchRepositoryEncoding.Utf8,
            DocumentationPatchEditKind.Insert,
            ["M:N.C.A", "M:N.C.B"]);
        var partial = source.Replace(
            "    public void A() { }",
            "    /// <inheritdoc/>\n    public void A() { }",
            StringComparison.Ordinal);

        var decision = fixture.ValidateCaptured(partial);

        Assert.False(decision.IsAccepted);
        Assert.Equal("patch.rejected.unsafe-change", decision.FailureCode);
        Assert.Empty(decision.ChangedFiles);
    }

    [Fact]
    public void StringsDirectivesDisabledTextAndUnselectedDocsRemainByteExact()
    {
        const string source =
            "#if false\npublic class Disabled { public void D() { } }\n#endif\nnamespace N;\npublic class C\n{\n    /// <summary>Keep.</summary>\n    public void Keep() { }\n    public string Text => \"/// #if false\";\n    public void M() { }\n}\n";
        using var fixture = ValidationFixture.Create(
            source,
            DocumentationPatchRepositoryEncoding.Utf8);
        var candidate = source.Replace(
            "    public void M() { }",
            "    /// <inheritdoc/>\n    public void M() { }",
            StringComparison.Ordinal);

        Assert.True(fixture.ValidateCaptured(candidate).IsAccepted);

        var tampered = candidate
            .Replace("#if false", "#if true", StringComparison.Ordinal)
            .Replace("Keep.", "Changed.", StringComparison.Ordinal);
        var rejected = fixture.ValidateCaptured(tampered);
        Assert.False(rejected.IsAccepted);
        Assert.Equal("patch.rejected.unsafe-change", rejected.FailureCode);
    }

    [Fact]
    public void InvalidCandidateEncodingFailsClosed()
    {
        const string source =
            "namespace N;\npublic class C\n{\n    public void M() { }\n}\n";
        using var fixture = ValidationFixture.Create(
            source,
            DocumentationPatchRepositoryEncoding.Utf8);

        var decision = fixture.ValidateCapturedBytes([0xc3, 0x28]);

        Assert.False(decision.IsAccepted);
        Assert.Equal("patch.rejected.unsafe-change", decision.FailureCode);
    }

    [Fact]
    public async Task CandidateValidationDisambiguatesDuplicateLogicalPathsAcrossSourceAndAdditionalRoles()
    {
        const string firstSource =
            "namespace N;\npublic class First\n{\n    public void M() { }\n}\n";
        const string secondSource =
            "namespace N;\npublic class Second\n{\n    public void M() { }\n}\n";
        const string candidateSource =
            "namespace N;\npublic class First\n{\n    /// <inheritdoc/>\n    public void M() { }\n}\n";
        var root = Path.Join(
            Path.GetTempPath(),
            "contract-scribe-duplicate-logical-paths-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var firstPath = Path.Join(root, "First.cs");
        var secondPath = Path.Join(root, "Second.cs");
        File.WriteAllText(firstPath, firstSource, new UTF8Encoding(false));
        File.WriteAllText(secondPath, secondSource, new UTF8Encoding(false));
        File.WriteAllText(Path.Join(root, "Fixture.csproj"), "<Project />", new UTF8Encoding(false));

        using var workspace = new AdhocWorkspace();
        try
        {
            var projectId = ProjectId.CreateNewId();
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator)
                .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path));
            var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                "Fixture",
                "Fixture",
                LanguageNames.CSharp,
                filePath: Path.Join(root, "Fixture.csproj"),
                compilationOptions: new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary),
                parseOptions: new CSharpParseOptions(
                    LanguageVersion.Preview,
                    documentationMode: DocumentationMode.Diagnose),
                metadataReferences: references));
            var firstDocumentId = DocumentId.CreateNewId(projectId);
            var secondDocumentId = DocumentId.CreateNewId(projectId);
            var firstAdditionalId = DocumentId.CreateNewId(projectId);
            var secondAdditionalId = DocumentId.CreateNewId(projectId);
            solution = solution.AddDocument(DocumentInfo.Create(
                firstDocumentId,
                "Api.cs",
                folders: ["Shared"],
                filePath: firstPath,
                loader: TextLoader.From(TextAndVersion.Create(
                    SourceText.From(firstSource, new UTF8Encoding(false, true)),
                    VersionStamp.Create(),
                    firstPath))));
            solution = solution.AddDocument(DocumentInfo.Create(
                secondDocumentId,
                "Api.cs",
                folders: ["Shared"],
                filePath: secondPath,
                loader: TextLoader.From(TextAndVersion.Create(
                    SourceText.From(secondSource, new UTF8Encoding(false, true)),
                    VersionStamp.Create(),
                    secondPath))));
            solution = solution.AddAdditionalDocument(DocumentInfo.Create(
                firstAdditionalId,
                "Input.cs",
                folders: ["Shared"],
                filePath: firstPath,
                loader: TextLoader.From(TextAndVersion.Create(
                    SourceText.From(firstSource, new UTF8Encoding(false, true)),
                    VersionStamp.Create(),
                    firstPath))));
            solution = solution.AddAdditionalDocument(DocumentInfo.Create(
                secondAdditionalId,
                "Input.cs",
                folders: ["Shared"],
                filePath: secondPath,
                loader: TextLoader.From(TextAndVersion.Create(
                    SourceText.From(secondSource, new UTF8Encoding(false, true)),
                    VersionStamp.Create(),
                    secondPath))));
            Assert.True(workspace.TryApplyChanges(solution));
            var project = workspace.CurrentSolution.GetProject(projectId)!;
            var firstTree = (await project.GetDocument(firstDocumentId)!.GetSyntaxTreeAsync())!;
            var secondTree = (await project.GetDocument(secondDocumentId)!.GetSyntaxTreeAsync())!;
            var compilation = (await project.GetCompilationAsync())!;
            var loaded = new LoadedProject(
                "Fixture.csproj",
                "net10.0",
                "fixture.net10.0",
                LoadedProjectRole.AuditRoot,
                [],
                project,
                compilation,
                new Dictionary<SyntaxTree, LoadedSourceTree>(ReferenceEqualityComparer.Instance)
                {
                    [firstTree] = new(
                        LoadedSourceKind.Repository,
                        "First.cs",
                        new RepositoryPathResolver().PhysicalIdentity(root, firstPath),
                        null),
                    [secondTree] = new(
                        LoadedSourceKind.Repository,
                        "Second.cs",
                        new RepositoryPathResolver().PhysicalIdentity(root, secondPath),
                        null),
                });
            Assert.True(RepositoryContextRef.TryParse(
                "repoctx-0123456789abcdef0123456789abcdef",
                out var repositoryContextRef));
            using var repository = new LoadedRepositorySession(
                repositoryContextRef,
                root,
                "Fixture.csproj",
                new ToolchainIdentity("test", "test", "test", "test"),
                [loaded],
                [],
                workspace);
            repository.SealDocumentationPatchRepositoryPolicyForTests();
            var classified = new SymbolClassifier().ClassifySession(
                repository,
                TargetProfile.ExternalApi);
            var baseline = Assert.IsType<DocumentationPatchRepositoryBaseline>(
                repository.CaptureDocumentationPatchRepositoryBaseline().Baseline);
            var candidateBytes = Encoding.UTF8.GetBytes(candidateSource);

            var result = DocumentationPatchCandidateValidation.Validate(
                classified,
                baseline,
                [new DocumentationPatchCandidateValidationFile(
                    "First.cs",
                    DocumentationPatchRepositoryEncoding.Utf8,
                    ImmutableArray.CreateRange(candidateBytes))]);

            Assert.True(result.IsValid, result.FailureCode);
            Assert.Equal(2, result.ValidatedSemanticInputCount);
            Assert.Equal(
                [
                    DocumentationPatchSemanticInputRole.Source,
                    DocumentationPatchSemanticInputRole.AdditionalFile,
                ],
                result.ValidatedSemanticInputs.Select(fact => fact.Role).Order());
            Assert.All(result.ValidatedSemanticInputs, fact =>
            {
                Assert.Equal("First.cs", fact.RepositoryPath);
                Assert.Equal(
                    Convert.ToHexString(SHA256.HashData(candidateBytes)).ToLowerInvariant(),
                    fact.CandidateSha256);
            });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void RequestWideProductRejectionBoundsDiagnosticsForContractSizedBatches()
    {
        var request = CreateCanonicalRequest(
            Enumerable.Range(0, 129).Select(index => $"block-{index:000}").ToArray());

        var result = DocumentationPatchEngine.ProductRejectionForTests(
            request,
            "patch.rejected.unsafe-change");

        Assert.Equal(DocumentationPatchOutcome.Rejected, result.Outcome);
        Assert.Equal(129, result.Targets.Length);
        Assert.All(result.Targets, target =>
            Assert.Equal(DocumentationPatchTargetStatus.Invalid, target.Status));
        Assert.Equal(128, result.Diagnostics.Length);
        Assert.Equal("block-000", result.Diagnostics[0].BlockId);
        Assert.Equal(
            Enumerable.Range(1, 127).Select(index => $"block-{index:000}"),
            result.Diagnostics.Skip(1).Select(diagnostic => diagnostic.BlockId));
        Assert.True(DocumentationPatchValidator.ValidateResult(request, result).IsValid);
    }

    [Fact]
    public void RequestWideProductRejectionKeepsRequestPrimaryAndSortsSecondaries()
    {
        var request = CreateCanonicalRequest(["z", "b", "a"]);

        var result = DocumentationPatchEngine.ProductRejectionForTests(
            request,
            "patch.rejected.unsafe-change");

        Assert.Equal(
            ["z", "a", "b"],
            result.Diagnostics.Select(diagnostic => diagnostic.BlockId));
        Assert.True(DocumentationPatchValidator.ValidateResult(request, result).IsValid);
    }

    [Fact]
    public void SymbolProjectionContainsDelegateEnumAccessorAttributeAndRelationshipShapes()
    {
        const string source = """
            using System;
            namespace N;
            [AttributeUsage(AttributeTargets.All)]
            public sealed class MarkerAttribute : Attribute { }
            public interface IContract
            {
                [return: Marker]
                int M<[Marker] T>([Marker] ref T value);
                int P { get; set; }
            }
            public abstract class Base
            {
                public abstract int M<T>(ref T value);
                public abstract int P { get; set; }
            }
            public sealed class C : Base, IContract
            {
                [return: Marker]
                public override int M<[Marker] T>([Marker] ref T value) => 0;
                public override int P { get; set; }
            }
            [return: Marker]
            public delegate int Transform<[Marker] T>([Marker] ref T value);
            public enum Choice : byte { First }
            """;
        var compilation = CompileProjection("projection-shapes", source);

        var projection = DocumentationPatchCandidateValidation.SymbolProjectionForTests(
            compilation);

        var @delegate = Assert.Single(projection, item =>
            item.StartsWith("T:N.Transform`1", StringComparison.Ordinal));
        Assert.Contains("type-kind=Delegate", @delegate, StringComparison.Ordinal);
        Assert.Contains("delegate-invoke=kind=DelegateInvoke", @delegate, StringComparison.Ordinal);
        Assert.Contains("return-attributes=T:N.MarkerAttribute", @delegate, StringComparison.Ordinal);
        Assert.Contains("attributes=T:N.MarkerAttribute", @delegate, StringComparison.Ordinal);

        var @enum = Assert.Single(projection, item =>
            item.StartsWith("T:N.Choice", StringComparison.Ordinal));
        Assert.Contains("enum-underlying=byte?NotAnnotated", @enum, StringComparison.Ordinal);

        var method = Assert.Single(projection, item =>
            item.StartsWith("M:N.C.M", StringComparison.Ordinal));
        Assert.Contains("overridden=M:N.Base.M", method, StringComparison.Ordinal);
        Assert.Contains("implemented=M:N.IContract.M", method, StringComparison.Ordinal);
        Assert.Contains("return-attributes=T:N.MarkerAttribute", method, StringComparison.Ordinal);

        var property = Assert.Single(projection, item =>
            item.StartsWith("P:N.C.P", StringComparison.Ordinal));
        Assert.Contains("get=PropertyGet", property, StringComparison.Ordinal);
        Assert.Contains("set=PropertySet", property, StringComparison.Ordinal);
        Assert.Contains("overridden=P:N.Base.P", property, StringComparison.Ordinal);
        Assert.Contains("implemented=P:N.IContract.P", property, StringComparison.Ordinal);
    }

    [Fact]
    public void SymbolProjectionChangesWhenTheSameSourceRebindsInterfaceEndpoints()
    {
        var firstReference = EmitReference(
            "external-one",
            "namespace External; public interface I<T> { void M(T value); }");
        var secondReference = EmitReference(
            "external-two",
            "namespace External; public interface I<T> { void M(int value); }");
        const string source =
            "namespace N; public sealed class C : External.I<int> { public void M(int value) { } }";
        var first = CompileProjection("consumer-one", source, firstReference);
        var second = CompileProjection("consumer-two", source, secondReference);

        var firstProjection = DocumentationPatchCandidateValidation.SymbolProjectionForTests(first);
        var secondProjection = DocumentationPatchCandidateValidation.SymbolProjectionForTests(second);

        Assert.NotEqual(firstProjection, secondProjection);
        var firstMethod = Assert.Single(firstProjection, item =>
            item.StartsWith("M:N.C.M", StringComparison.Ordinal));
        var secondMethod = Assert.Single(secondProjection, item =>
            item.StartsWith("M:N.C.M", StringComparison.Ordinal));
        Assert.True(
            firstMethod.Contains(
                "implemented=M:External.I`1.M(`0)=>M:External.I{System.Int32}.M(System.Int32)",
                StringComparison.Ordinal),
            firstMethod);
        Assert.True(
            secondMethod.Contains(
                "implemented=M:External.I`1.M(System.Int32)=>M:External.I{System.Int32}.M(System.Int32)",
                StringComparison.Ordinal),
            secondMethod);
    }

    private static CSharpCompilation CompileProjection(
        string assemblyName,
        string source,
        params MetadataReference[] additionalReferences)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .Concat(additionalReferences);
        var compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error);
        return compilation;
    }

    private static MetadataReference EmitReference(string assemblyName, string source)
    {
        var compilation = CompileProjection(assemblyName, source);
        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        return MetadataReference.CreateFromImage(stream.ToArray());
    }

    private static DocumentationPatchRequest CreateCanonicalRequest(
        IReadOnlyList<string> blockIds)
    {
        var requestBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            patchRequestVersion = 1,
            context = new
            {
                repositoryContextRef = "repoctx-0123456789abcdef0123456789abcdef",
                inputIdentity = "Fixture.csproj",
                targetProfile = "profile.external-api",
            },
            provenanceCatalog = Array.Empty<string>(),
            blocks = blockIds.Select((blockId, index) => new
            {
                blockId,
                symbolRef = new
                {
                    compilationContextRef = "fixture.net10.0",
                    documentationCommentId = $"M:N.C.M{index:000}",
                },
                locator = new
                {
                    kind = "repository",
                    path = "Sample.cs",
                    originalFileSha256 = new string('0', 64),
                    encoding = "utf-8",
                    declarationSpan = new
                    {
                        start = index * 2,
                        end = index * 2 + 1,
                    },
                },
                editKind = "insert",
                applicableComponents = Array.Empty<object>(),
                content = new { kind = "inheritDoc" },
                provenanceRefs = Array.Empty<string>(),
            }).ToArray(),
        });
        var parsed = DocumentationPatchValidator.ParseRequest(requestBytes);
        Assert.True(parsed.IsValid, parsed.Failure?.Code);
        return Assert.IsType<DocumentationPatchRequest>(parsed.Request);
    }

    private sealed class ValidationFixture : IDisposable
    {
        private static readonly ImmutableArray<MetadataReference> PlatformReferences =
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator)
                .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
                .ToImmutableArray();

        private readonly AdhocWorkspace workspace;
        private readonly LoadedRepositorySession repository;
        private readonly DocumentationPatchRepositoryEncoding encoding;

        private ValidationFixture(
            string root,
            AdhocWorkspace workspace,
            LoadedRepositorySession repository,
            ClassifiedRepositorySession classified,
            DocumentationPatchRequest request,
            DocumentationPatchRepositoryBaseline baseline,
            DocumentationPatchResolutionResult resolution,
            DocumentationPatchRepositoryEncoding encoding)
        {
            Root = root;
            this.workspace = workspace;
            this.repository = repository;
            Classified = classified;
            Request = request;
            Baseline = baseline;
            Resolution = resolution;
            this.encoding = encoding;
        }

        public string Root { get; }

        public ClassifiedRepositorySession Classified { get; }

        public DocumentationPatchRequest Request { get; }

        public DocumentationPatchRepositoryBaseline Baseline { get; }

        public DocumentationPatchResolutionResult Resolution { get; }

        public static ValidationFixture Create(
            string source,
            DocumentationPatchRepositoryEncoding encoding,
            DocumentationPatchEditKind editKind = DocumentationPatchEditKind.Insert,
            IReadOnlyList<string>? documentationCommentIds = null)
        {
            var root = Path.Join(
                Path.GetTempPath(),
                "contract-scribe-patch-validation-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var sourcePath = Path.Join(root, "Sample.cs");
            var projectPath = Path.Join(root, "Fixture.csproj");
            File.WriteAllBytes(sourcePath, Encode(source, encoding));
            File.WriteAllText(projectPath, "<Project />", new UTF8Encoding(false));

            var workspace = new AdhocWorkspace();
            var projectId = ProjectId.CreateNewId();
            var parseOptions = new CSharpParseOptions(
                LanguageVersion.Preview,
                documentationMode: DocumentationMode.Diagnose);
            var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                "Fixture",
                "Fixture",
                LanguageNames.CSharp,
                filePath: projectPath,
                compilationOptions: new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary),
                parseOptions: parseOptions,
                metadataReferences: PlatformReferences));
            var documentId = DocumentId.CreateNewId(projectId);
            solution = solution.AddDocument(DocumentInfo.Create(
                documentId,
                "Sample.cs",
                filePath: sourcePath,
                loader: TextLoader.From(TextAndVersion.Create(
                    SourceText.From(source, GetEncoding(encoding)),
                    VersionStamp.Create(),
                    sourcePath))));
            Assert.True(workspace.TryApplyChanges(solution));
            var project = workspace.CurrentSolution.GetProject(projectId)!;
            var document = project.GetDocument(documentId)!;
            var tree = document.GetSyntaxTreeAsync().GetAwaiter().GetResult()!;
            var compilation = project.GetCompilationAsync().GetAwaiter().GetResult()!;
            var loaded = new LoadedProject(
                "Fixture.csproj",
                "net10.0",
                "fixture.net10.0",
                LoadedProjectRole.AuditRoot,
                [],
                project,
                compilation,
                new Dictionary<SyntaxTree, LoadedSourceTree>(ReferenceEqualityComparer.Instance)
                {
                    [tree] = new(
                        LoadedSourceKind.Repository,
                        "Sample.cs",
                        new RepositoryPathResolver().PhysicalIdentity(root, sourcePath),
                        null),
                });
            Assert.True(RepositoryContextRef.TryParse(
                "repoctx-0123456789abcdef0123456789abcdef",
                out var repositoryContextRef));
            var repository = new LoadedRepositorySession(
                repositoryContextRef,
                root,
                "Fixture.csproj",
                new ToolchainIdentity("test", "test", "test", "test"),
                [loaded],
                [],
                workspace);
            repository.SealDocumentationPatchRepositoryPolicyForTests();
            var classified = new SymbolClassifier().ClassifySession(
                repository,
                TargetProfile.ExternalApi);
            documentationCommentIds ??= ["M:N.C.M"];
            var selectedTargets = documentationCommentIds.Select(documentationCommentId =>
                    Assert.Single(
                        classified.Classification.ClassificationSet!.Targets,
                        item => item.SymbolRef.DocumentationCommentId == documentationCommentId
                            && item.SupportStatus == SupportStatus.Supported))
                .ToArray();
            var targetFacts = selectedTargets.Select(target =>
            {
                var symbol = Assert.Single(DocumentationCommentId.GetSymbolsForDeclarationId(
                    target.SymbolRef.DocumentationCommentId,
                    compilation));
                return (Target: target, Reference: Assert.Single(symbol.DeclaringSyntaxReferences));
            }).ToArray();
            var requestBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                patchRequestVersion = 1,
                context = new
                {
                    repositoryContextRef = repositoryContextRef.ToString(),
                    inputIdentity = "Fixture.csproj",
                    targetProfile = "profile.external-api",
                },
                provenanceCatalog = Array.Empty<string>(),
                blocks = targetFacts.Select((fact, index) => new
                {
                    blockId = $"block-{index + 1}",
                    symbolRef = new
                    {
                        compilationContextRef = fact.Target.SymbolRef.CompilationContextRef,
                        documentationCommentId = fact.Target.SymbolRef.DocumentationCommentId,
                    },
                    locator = new
                    {
                        kind = "repository",
                        path = "Sample.cs",
                        originalFileSha256 = Sha256(File.ReadAllBytes(sourcePath)),
                        encoding = EncodingValue(encoding),
                        declarationSpan = new
                        {
                            start = fact.Reference.Span.Start,
                            end = fact.Reference.Span.End,
                        },
                    },
                    editKind = editKind == DocumentationPatchEditKind.Insert
                        ? "insert"
                        : "replace",
                    applicableComponents = Array.Empty<object>(),
                    content = new { kind = "inheritDoc" },
                    provenanceRefs = Array.Empty<string>(),
                }).ToArray(),
            });
            var parsed = DocumentationPatchValidator.ParseRequest(requestBytes);
            Assert.True(parsed.IsValid, parsed.Failure?.Code);
            var request = Assert.IsType<DocumentationPatchRequest>(parsed.Request);
            var baseline = Assert.IsType<DocumentationPatchRepositoryBaseline>(
                repository.CaptureDocumentationPatchRepositoryBaseline().Baseline);
            var resolution = new DocumentationPatchResolver().Resolve(
                classified,
                request,
                baseline);
            Assert.Equal(DocumentationPatchResolutionStatus.Resolved, resolution.Status);
            return new ValidationFixture(
                root,
                workspace,
                repository,
                classified,
                request,
                baseline,
                resolution,
                encoding);
        }

        public DocumentationPatchCandidateValidationDecision ValidateCaptured(
            string candidateSource)
            => ValidateCapturedBytes(Encode(candidateSource, encoding));

        public DocumentationPatchCandidateValidationDecision ValidateCapturedBytes(
            byte[] candidateBytes)
        {
            var captured = Baseline.Entries.Select(entry => new CandidateWorkspaceFile(
                    entry.RepositoryPath,
                    entry.RepositoryPath == "Sample.cs"
                        ? ImmutableArray.CreateRange(candidateBytes)
                        : entry.Bytes,
                    default))
                .ToImmutableArray();
            return DocumentationPatchCandidateValidator.Validate(
                Classified,
                Request,
                Baseline,
                Resolution,
                captured,
                CancellationToken.None);
        }

        public DocumentationPatchCandidateValidationResult ValidateRoslynCandidate(
            string candidateSource,
            DocumentationPatchCandidateValidationCorruption corruption) =>
            DocumentationPatchCandidateValidation.ValidateForTests(
                Classified,
                Baseline,
                [new DocumentationPatchCandidateValidationFile(
                    "Sample.cs",
                    encoding,
                    ImmutableArray.CreateRange(Encode(candidateSource, encoding)))],
                corruption,
                CancellationToken.None);

        public void Dispose()
        {
            repository.Dispose();
            workspace.Dispose();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private static Encoding GetEncoding(
            DocumentationPatchRepositoryEncoding encoding) => encoding switch
            {
                DocumentationPatchRepositoryEncoding.Utf8
                    or DocumentationPatchRepositoryEncoding.Utf8Bom => new UTF8Encoding(false, true),
                DocumentationPatchRepositoryEncoding.Utf16LittleEndianBom =>
                    new UnicodeEncoding(false, false, true),
                DocumentationPatchRepositoryEncoding.Utf16BigEndianBom =>
                    new UnicodeEncoding(true, false, true),
                _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
            };

        public static byte[] Encode(
            string source,
            DocumentationPatchRepositoryEncoding encoding)
        {
            var content = GetEncoding(encoding).GetBytes(source);
            return encoding switch
            {
                DocumentationPatchRepositoryEncoding.Utf8 => content,
                DocumentationPatchRepositoryEncoding.Utf8Bom => [0xef, 0xbb, 0xbf, .. content],
                DocumentationPatchRepositoryEncoding.Utf16LittleEndianBom => [0xff, 0xfe, .. content],
                DocumentationPatchRepositoryEncoding.Utf16BigEndianBom => [0xfe, 0xff, .. content],
                _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
            };
        }

        public static string Sha256(byte[] bytes) =>
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        private static string EncodingValue(
            DocumentationPatchRepositoryEncoding encoding) => encoding switch
            {
                DocumentationPatchRepositoryEncoding.Utf8 => "utf-8",
                DocumentationPatchRepositoryEncoding.Utf8Bom => "utf-8-bom",
                DocumentationPatchRepositoryEncoding.Utf16LittleEndianBom => "utf-16le-bom",
                DocumentationPatchRepositoryEncoding.Utf16BigEndianBom => "utf-16be-bom",
                _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
            };
    }
}
