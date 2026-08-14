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
