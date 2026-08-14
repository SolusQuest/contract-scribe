using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ContractScribe.Core;
using ContractScribe.Patching;
using ContractScribe.Roslyn;
using ContractScribe.Roslyn.IntegrationTests;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace ContractScribe.IntegrationTests;

[Collection("Integration process lane 1")]
public sealed class DocumentationPatchEndToEndTests
{
    [Fact]
    public async Task RealLoaderValidatesEverySourceAndAdditionalFileRoleAndRerunsGenerators()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        await using var fixture = await RealLoaderEngineFixture.CreateAsync(
            enableDocumentationSensitiveGenerator: false,
            enableNoOutputToOutputGenerator: false);

        var outcome = new DocumentationPatchEngine(
            () => fixture.StagingParent,
            null,
            null).Execute(fixture.ClassifiedSession, fixture.Request);

        var result = Assert.IsType<DocumentationPatchValidationResult>(outcome.Result);
        Assert.Equal(DocumentationPatchOutcome.Accepted, result.Outcome);
        var capability = Assert.IsType<DocumentationPatchAcceptedCandidate>(
            outcome.AcceptedCandidate);
        var appFacts = capability.Baseline.SemanticInputs.Where(fact =>
                fact.RepositoryPath == "App/App.cs")
            .ToArray();
        Assert.Equal(3, appFacts.Length);
        Assert.Equal(2, appFacts.Count(fact =>
            fact.Role == DocumentationPatchSemanticInputRole.Source));
        Assert.Single(appFacts, fact =>
            fact.Role == DocumentationPatchSemanticInputRole.AdditionalFile);
        Assert.All(result.Invariants, invariant =>
            Assert.Equal(DocumentationPatchInvariantStatus.Passed, invariant.Status));
    }

    [Fact]
    public async Task DocumentationSensitiveAndNoOutputToOutputGeneratorsFailClosed()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        await using var fixture = await RealLoaderEngineFixture.CreateAsync(
            enableDocumentationSensitiveGenerator: true,
            enableNoOutputToOutputGenerator: true);
        Assert.Contains(fixture.Repository.GeneratedSources, fact =>
            fact.SourceText.Contains(
                "FixtureDocumentationSensitive",
                StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Repository.GeneratedSources, fact =>
            fact.SourceText.Contains("FixtureNoOutputToOutput", StringComparison.Ordinal));

        var outcome = new DocumentationPatchEngine(
            () => fixture.StagingParent,
            null,
            null).Execute(fixture.ClassifiedSession, fixture.Request);

        Assert.Equal(DocumentationPatchExecutionStatus.Result, outcome.Status);
        var result = Assert.IsType<DocumentationPatchValidationResult>(outcome.Result);
        Assert.Equal(DocumentationPatchOutcome.Rejected, result.Outcome);
        Assert.All(result.Targets, target =>
            Assert.Equal(DocumentationPatchTargetStatus.Invalid, target.Status));
        Assert.Equal("patch.rejected.unsafe-change", Assert.Single(result.Diagnostics).Code);
        Assert.Null(outcome.AcceptedCandidate);
    }

    [Fact]
    public void AcceptedExecutionReturnsImmutableBytesReleasesStagingAndPublicReplayIsStale()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var fixture = EngineFixture.Create();
        string? stagingRoot = null;
        var engine = new DocumentationPatchEngine(
            () => fixture.StagingParent,
            (stage, root) =>
            {
                if (stage == DocumentationPatchApplicationStage.AfterSealBeforeReturn)
                {
                    stagingRoot = root;
                }
            },
            null);

        var outcome = engine.Execute(fixture.ClassifiedSession, fixture.Request);

        Assert.Equal(DocumentationPatchExecutionStatus.Result, outcome.Status);
        var result = Assert.IsType<DocumentationPatchValidationResult>(outcome.Result);
        Assert.Equal(DocumentationPatchOutcome.Accepted, result.Outcome);
        Assert.All(result.Targets, target =>
            Assert.Equal(DocumentationPatchTargetStatus.Valid, target.Status));
        Assert.Empty(result.Diagnostics);
        Assert.All(result.Invariants, invariant =>
            Assert.Equal(DocumentationPatchInvariantStatus.Passed, invariant.Status));
        var capability = Assert.IsType<DocumentationPatchAcceptedCandidate>(
            outcome.AcceptedCandidate);
        Assert.Same(result, capability.Result);
        var source = Assert.Single(capability.Files, file =>
            file.RepositoryPath == "Sample.cs");
        Assert.Contains("/// <inheritdoc/>", Encoding.UTF8.GetString(source.Bytes.AsSpan()));
        Assert.Equal(Sha256(source.Bytes.AsSpan()), source.Sha256);
        Assert.NotNull(stagingRoot);
        Assert.False(Directory.Exists(stagingRoot));

        var acceptedBytes = source.Bytes;
        File.WriteAllBytes(fixture.SourcePath, acceptedBytes.ToArray());
        var replay = new DocumentationPatchEngine(
            () => fixture.StagingParent,
            null,
            null).Execute(fixture.ClassifiedSession, fixture.Request);

        Assert.Equal(DocumentationPatchExecutionStatus.Result, replay.Status);
        Assert.Equal(DocumentationPatchOutcome.Stale, replay.Result!.Outcome);
        Assert.Null(replay.AcceptedCandidate);
        Assert.Equal(acceptedBytes, source.Bytes);
    }

    [Fact]
    public void CandidateMutationAtTerminalCaptureReturnsCandidateStateAndNoCapability()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var fixture = EngineFixture.Create();
        string? stagingRoot = null;
        var engine = new DocumentationPatchEngine(
            () => fixture.StagingParent,
            (stage, root) =>
            {
                if (stage == DocumentationPatchApplicationStage.AfterSealBeforeReturn)
                {
                    stagingRoot = root;
                }
            },
            stage =>
            {
                if (stage == DocumentationPatchEngineStage.BeforeCandidateTerminalPass)
                {
                    File.AppendAllText(
                        Path.Join(stagingRoot!, "Sample.cs"),
                        " ",
                        new UTF8Encoding(false));
                }
            });

        var outcome = engine.Execute(fixture.ClassifiedSession, fixture.Request);

        AssertRootExecution(
            outcome,
            DocumentationPatchOutcome.Rejected,
            DocumentationPatchTargetStatus.Valid,
            "patch.rejected.candidate-state");
        Assert.Null(outcome.AcceptedCandidate);
        Assert.False(Directory.Exists(stagingRoot));
    }

    [Fact]
    public void OriginalMutationBeforeE1SealReturnsRepositoryStateAndCleansCandidate()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var fixture = EngineFixture.Create();
        string? stagingRoot = null;
        var engine = new DocumentationPatchEngine(
            () => fixture.StagingParent,
            (stage, root) =>
            {
                stagingRoot = root ?? stagingRoot;
                if (stage == DocumentationPatchApplicationStage.BeforeOriginalRebind)
                {
                    File.AppendAllText(
                        fixture.SourcePath,
                        " ",
                        new UTF8Encoding(false));
                }
            },
            null);

        var outcome = engine.Execute(fixture.ClassifiedSession, fixture.Request);

        AssertRootExecution(
            outcome,
            DocumentationPatchOutcome.Stale,
            DocumentationPatchTargetStatus.NotEvaluated,
            "patch.stale.repository-state");
        Assert.Null(outcome.AcceptedCandidate);
        Assert.NotNull(stagingRoot);
        Assert.False(Directory.Exists(stagingRoot));
    }

    [Fact]
    public void OriginalMutationImmediatelyBeforeCommitReturnsRepositoryState()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var fixture = EngineFixture.Create();
        var engine = new DocumentationPatchEngine(
            () => fixture.StagingParent,
            null,
            stage =>
            {
                if (stage == DocumentationPatchEngineStage.BeforeFinalOriginalRebind)
                {
                    File.AppendAllText(
                        fixture.SourcePath,
                        " ",
                        new UTF8Encoding(false));
                }
            });

        var outcome = engine.Execute(fixture.ClassifiedSession, fixture.Request);

        AssertRootExecution(
            outcome,
            DocumentationPatchOutcome.Stale,
            DocumentationPatchTargetStatus.NotEvaluated,
            "patch.stale.repository-state");
        Assert.Null(outcome.AcceptedCandidate);
    }

    [Fact]
    public void SimultaneousCandidateAndRepositoryMutationGivesRepositoryStatePrecedence()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var fixture = EngineFixture.Create();
        string? stagingRoot = null;
        var engine = new DocumentationPatchEngine(
            () => fixture.StagingParent,
            (stage, root) =>
            {
                if (stage == DocumentationPatchApplicationStage.AfterSealBeforeReturn)
                {
                    stagingRoot = root;
                }
            },
            stage =>
            {
                if (stage == DocumentationPatchEngineStage.BeforeCandidateTerminalPass)
                {
                    File.AppendAllText(
                        Path.Join(stagingRoot!, "Sample.cs"),
                        " ",
                        new UTF8Encoding(false));
                }
                else if (stage == DocumentationPatchEngineStage.BeforeFinalOriginalRebind)
                {
                    File.AppendAllText(
                        fixture.SourcePath,
                        " ",
                        new UTF8Encoding(false));
                }
            });

        var outcome = engine.Execute(fixture.ClassifiedSession, fixture.Request);

        AssertRootExecution(
            outcome,
            DocumentationPatchOutcome.Stale,
            DocumentationPatchTargetStatus.NotEvaluated,
            "patch.stale.repository-state");
        Assert.Null(outcome.AcceptedCandidate);
    }

    [Fact]
    public void CancellationIsNotConvertedIntoAResultOrHostFailure()
    {
        using var fixture = EngineFixture.Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var engine = new DocumentationPatchEngine(
            () => fixture.StagingParent,
            null,
            null);

        Assert.Throws<OperationCanceledException>(() =>
            engine.Execute(fixture.ClassifiedSession, fixture.Request, cancellation.Token));
    }

    [Theory]
    [InlineData("namespace N;\r\npublic class C\n{\r\n    public void M() { }\r\n}\r\n")]
    [InlineData("namespace N;\rpublic class C { public void M() { } }")]
    public void RepresentationFailureBeforeMaterializationIsAProductRejection(string source)
    {
        using var fixture = EngineFixture.Create(source);

        var outcome = new DocumentationPatchEngine(
            () => fixture.StagingParent,
            null,
            null).Execute(fixture.ClassifiedSession, fixture.Request);

        Assert.Equal(DocumentationPatchExecutionStatus.Result, outcome.Status);
        var result = Assert.IsType<DocumentationPatchValidationResult>(outcome.Result);
        Assert.Equal(DocumentationPatchOutcome.Rejected, result.Outcome);
        Assert.Equal(
            DocumentationPatchTargetStatus.Invalid,
            Assert.Single(result.Targets).Status);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("patch.rejected.unsafe-change", diagnostic.Code);
        Assert.Equal("block-1", diagnostic.BlockId);
        Assert.Null(outcome.AcceptedCandidate);
    }

    [Fact]
    public void CandidateMaterializationFailureIsANonResultHostFailure()
    {
        using var fixture = EngineFixture.Create();
        var engine = new DocumentationPatchEngine(
            () => fixture.SourcePath,
            null,
            null);

        var outcome = engine.Execute(fixture.ClassifiedSession, fixture.Request);

        Assert.Equal(DocumentationPatchExecutionStatus.HostFailure, outcome.Status);
        Assert.Equal("patch.host.environment-failure", outcome.FailureCode);
        Assert.Null(outcome.Result);
        Assert.Null(outcome.AcceptedCandidate);
    }

    private static void AssertRootExecution(
        DocumentationPatchExecutionOutcome outcome,
        DocumentationPatchOutcome expectedOutcome,
        DocumentationPatchTargetStatus expectedTargetStatus,
        string expectedCode)
    {
        Assert.Equal(DocumentationPatchExecutionStatus.Result, outcome.Status);
        var result = Assert.IsType<DocumentationPatchValidationResult>(outcome.Result);
        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.All(result.Targets, target => Assert.Equal(expectedTargetStatus, target.Status));
        Assert.Empty(result.ChangedFiles);
        Assert.Equal(0, result.ChangedDocumentationBlockCount);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(expectedCode, diagnostic.Code);
        Assert.Null(diagnostic.BlockId);
        Assert.Null(diagnostic.Path);
        Assert.Null(diagnostic.Pointer);
        Assert.Equal(
            DocumentationPatchInvariantStatus.Passed,
            Assert.Single(result.Invariants, invariant =>
                invariant.Id == "patch.invariant.fail-closed").Status);
        Assert.All(result.Invariants.Where(invariant =>
            invariant.Id != "patch.invariant.fail-closed"), invariant =>
            Assert.Equal(DocumentationPatchInvariantStatus.NotRun, invariant.Status));
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class EngineFixture : IDisposable
    {
        private static readonly ImmutableArray<MetadataReference> PlatformReferences =
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator)
                .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
                .ToImmutableArray();

        private readonly LoadedRepositorySession repository;

        private EngineFixture(
            string root,
            string sourcePath,
            string stagingParent,
            LoadedRepositorySession repository,
            ClassifiedRepositorySession classifiedSession,
            DocumentationPatchRequest request)
        {
            Root = root;
            SourcePath = sourcePath;
            StagingParent = stagingParent;
            this.repository = repository;
            ClassifiedSession = classifiedSession;
            Request = request;
        }

        public string Root { get; }

        public string SourcePath { get; }

        public string StagingParent { get; }

        public ClassifiedRepositorySession ClassifiedSession { get; }

        public DocumentationPatchRequest Request { get; }

        public static EngineFixture Create(string? source = null)
        {
            source ??=
                "namespace N;\npublic class C\n{\n    public void M() { }\n}\n";
            var root = Path.Join(
                Path.GetTempPath(),
                "contract-scribe-patch-engine-source-" + Guid.NewGuid().ToString("N"));
            var stagingParent = Path.Join(
                Path.GetTempPath(),
                "contract-scribe-patch-engine-staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(stagingParent);
            var sourcePath = Path.Join(root, "Sample.cs");
            var projectPath = Path.Join(root, "Fixture.csproj");
            File.WriteAllText(sourcePath, source, new UTF8Encoding(false));
            File.WriteAllText(projectPath, "<Project />", new UTF8Encoding(false));

            var workspace = new AdhocWorkspace();
            var projectId = ProjectId.CreateNewId();
            var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                "Fixture",
                "Fixture",
                LanguageNames.CSharp,
                filePath: projectPath,
                compilationOptions: new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary),
                parseOptions: new CSharpParseOptions(
                    LanguageVersion.Preview,
                    documentationMode: DocumentationMode.Diagnose),
                metadataReferences: PlatformReferences));
            var documentId = DocumentId.CreateNewId(projectId);
            solution = solution.AddDocument(DocumentInfo.Create(
                documentId,
                "Sample.cs",
                filePath: sourcePath,
                loader: TextLoader.From(TextAndVersion.Create(
                    SourceText.From(source, new UTF8Encoding(false, true)),
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
            repository.SealDocumentationPatchRepositoryPolicyForTests([stagingParent]);
            var classified = new SymbolClassifier().ClassifySession(
                repository,
                TargetProfile.ExternalApi);
            var target = Assert.Single(
                classified.Classification.ClassificationSet!.Targets,
                candidate => candidate.SymbolRef.DocumentationCommentId == "M:N.C.M"
                    && candidate.SupportStatus == SupportStatus.Supported);
            var symbol = Assert.Single(DocumentationCommentId.GetSymbolsForDeclarationId(
                target.SymbolRef.DocumentationCommentId,
                compilation));
            var reference = Assert.Single(symbol.DeclaringSyntaxReferences);
            var request = new DocumentationPatchRequest(
                new string('0', 64),
                new DocumentationPatchContext(
                    repositoryContextRef,
                    "Fixture.csproj",
                    TargetProfile.ExternalApi),
                [],
                [new DocumentationPatchBlockRequest(
                    "block-1",
                    target.SymbolRef,
                    new DocumentationPatchRepositoryLocator(
                        "Sample.cs",
                        Sha256(File.ReadAllBytes(sourcePath)),
                        DocumentationPatchRepositoryEncoding.Utf8,
                        DocumentationObservationInput.Span(
                            reference.Span.Start,
                            reference.Span.End)),
                    DocumentationPatchEditKind.Insert,
                    [],
                    new DocumentationPatchInheritDocContent(),
                    [])]);
            return new EngineFixture(
                root,
                sourcePath,
                stagingParent,
                repository,
                classified,
                request);
        }

        public void Dispose()
        {
            repository.Dispose();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }

            if (Directory.Exists(StagingParent))
            {
                Directory.Delete(StagingParent, recursive: true);
            }
        }
    }

    private sealed class RealLoaderEngineFixture : IAsyncDisposable
    {
        private readonly LoaderFixture loaderFixture;

        private RealLoaderEngineFixture(
            LoaderFixture loaderFixture,
            LoadedRepositorySession repository,
            string stagingParent,
            ClassifiedRepositorySession classifiedSession,
            DocumentationPatchRequest request)
        {
            this.loaderFixture = loaderFixture;
            Repository = repository;
            StagingParent = stagingParent;
            ClassifiedSession = classifiedSession;
            Request = request;
        }

        public LoadedRepositorySession Repository { get; }

        public string StagingParent { get; }

        public ClassifiedRepositorySession ClassifiedSession { get; }

        public DocumentationPatchRequest Request { get; }

        public static async Task<RealLoaderEngineFixture> CreateAsync(
            bool enableDocumentationSensitiveGenerator,
            bool enableNoOutputToOutputGenerator)
        {
            var generatorProperties = new StringBuilder();
            var compilerVisibleProperties = new StringBuilder();
            if (enableDocumentationSensitiveGenerator)
            {
                generatorProperties.AppendLine(
                    "    <ContractScribeTestGeneratorDocumentationSensitive>true</ContractScribeTestGeneratorDocumentationSensitive>");
                compilerVisibleProperties.AppendLine(
                    "    <CompilerVisibleProperty Include=\"ContractScribeTestGeneratorDocumentationSensitive\" />");
            }

            if (enableNoOutputToOutputGenerator)
            {
                generatorProperties.AppendLine(
                    "    <ContractScribeTestGeneratorNoOutputToOutput>true</ContractScribeTestGeneratorNoOutputToOutput>");
                compilerVisibleProperties.AppendLine(
                    "    <CompilerVisibleProperty Include=\"ContractScribeTestGeneratorNoOutputToOutput\" />");
            }

            var appProject = $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                {{generatorProperties}}  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../Library/Library.csproj" />
                    <AdditionalFiles Include="App.cs" Link="Logical/Input/App-copy.cs" />
                {{compilerVisibleProperties}}  </ItemGroup>
                </Project>
                """;
            const string libraryProject = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <Compile Include="../App/App.cs" Link="Logical/Library/App-linked.cs" />
                  </ItemGroup>
                </Project>
                """;
            var loaderFixture = await LoaderFixture.CreateAsync(
                appProject: appProject,
                libraryProject: libraryProject,
                withGenerator: true);
            try
            {
                const string source =
                    "namespace N;\npublic class RealApi\n{\n    public void M() { }\n}\n";
                var sourcePath = Path.Join(loaderFixture.Root, "App", "App.cs");
                await File.WriteAllTextAsync(sourcePath, source, new UTF8Encoding(false));
                var load = await new RepositoryLoader().LoadAsync(
                    new RepositoryLoadRequest(loaderFixture.Root, "App/App.csproj"));
                if (load.Status != RepositoryLoadStatus.Success || load.Session is null)
                {
                    throw new InvalidOperationException(
                        $"{load.PrimaryFailure?.Stage}:{load.PrimaryFailure?.Code}");
                }

                var repository = load.Session;
                var stagingParent = Path.Join(
                    Path.GetTempPath(),
                    "contract-scribe-patch-real-loader-staging-"
                    + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(stagingParent);
                repository.SealDocumentationPatchRepositoryPolicyForTests([stagingParent]);
                var classified = new SymbolClassifier().ClassifySession(
                    repository,
                    TargetProfile.ExternalApi);
                var app = Assert.Single(repository.Projects, project =>
                    project.ProjectIdentity == "App/App.csproj");
                var target = Assert.Single(
                    classified.Classification.ClassificationSet!.Targets,
                    candidate => candidate.SymbolRef.DocumentationCommentId == "M:N.RealApi.M"
                        && candidate.SymbolRef.CompilationContextRef
                            == app.CompilationContextRef
                        && candidate.SupportStatus == SupportStatus.Supported);
                var symbol = Assert.Single(DocumentationCommentId.GetSymbolsForDeclarationId(
                    target.SymbolRef.DocumentationCommentId,
                    app.Compilation));
                var reference = Assert.Single(symbol.DeclaringSyntaxReferences);
                var loadedSource = app.SourceTrees[reference.SyntaxTree];
                var repositoryPath = Assert.IsType<string>(loadedSource.RepositoryPath);
                var bytes = await File.ReadAllBytesAsync(sourcePath);
                var request = new DocumentationPatchRequest(
                    new string('0', 64),
                    new DocumentationPatchContext(
                        repository.RepositoryContextRef,
                        repository.InputIdentity,
                        TargetProfile.ExternalApi),
                    [],
                    [new DocumentationPatchBlockRequest(
                        "block-1",
                        target.SymbolRef,
                        new DocumentationPatchRepositoryLocator(
                            repositoryPath,
                            Sha256(bytes),
                            DocumentationPatchRepositoryEncoding.Utf8,
                            DocumentationObservationInput.Span(
                                reference.Span.Start,
                                reference.Span.End)),
                        DocumentationPatchEditKind.Insert,
                        [],
                        new DocumentationPatchInheritDocContent(),
                        [])]);
                return new RealLoaderEngineFixture(
                    loaderFixture,
                    repository,
                    stagingParent,
                    classified,
                    request);
            }
            catch
            {
                await loaderFixture.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Repository.DisposeAsync();
            await loaderFixture.DisposeAsync();
            if (Directory.Exists(StagingParent))
            {
                Directory.Delete(StagingParent, recursive: true);
            }
        }
    }
}
