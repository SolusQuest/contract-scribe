using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed class DocumentationScribeSemanticToolIntegrationTests
{
    private const string MethodId = "M:SemanticFixture.Runner.Execute(System.String)";
    private const string FreshProcessOutputVariable = "CONTRACTSCRIBE_SEMANTIC_PROBE_OUTPUT";

    [Fact]
    public async Task ReturnsMethodCoreRelationsGeneratedUsagesAndTestEvidence()
    {
        using var fixture = SemanticFixture.Create(MethodId);
        var loaded = fixture.Bootstrap();
        var request = fixture.CreateRequest(loaded);
        var port = new DocumentationScribeSemanticToolPort(loaded, request);

        var result = await port.InvokeAsync(
            DocumentationScribeSemanticToolRequest.Create(20),
            CancellationToken.None);

        Assert.Contains(
            result.Outcome,
            new[] { DocumentationScribeToolOutcome.Complete, DocumentationScribeToolOutcome.Incomplete });
        var page = Assert.IsType<DocumentationScribeSemanticEvidencePage>(result.Page);
        Assert.Equal(MethodId, page.Core.Method.SymbolRef.DocumentationCommentId);
        Assert.Equal(PrimarySymbolKind.Method, page.Core.Method.PrimaryKind);
        Assert.Equal(DocumentationObservationValue.Present, page.Core.Documentation.Value);
        Assert.Contains(page.Core.ApplicableComponents, item =>
            item.Kind == DocumentationPatchComponentKind.Parameter
            && item.Name == "value");
        Assert.Contains(page.Core.ApplicableComponents, item =>
            item.Kind == DocumentationPatchComponentKind.Return);
        Assert.Contains(page.Items, item => item.Kind == DocumentationScribeSemanticEvidenceKind.Relation);
        Assert.Contains(page.Items, item => item.RelationKind == RelationKind.Overrides);
        Assert.Contains(page.Items, item => item.Kind == DocumentationScribeSemanticEvidenceKind.Usage);
        Assert.Contains(page.Items, item => item.Kind == DocumentationScribeSemanticEvidenceKind.TestUsage);
        Assert.Contains(page.Items, item => item.Source is DocumentationScribeSemanticSourceGeneratorEvidence);
        Assert.Contains(page.Items, item => item.Source is DocumentationScribeSemanticToolGeneratedEvidence);
        var consumerText = File.ReadAllText(fixture.ConsumerPath);
        var overloadStart = consumerText.IndexOf("Execute(42)", StringComparison.Ordinal);
        Assert.True(overloadStart >= 0);
        Assert.DoesNotContain(page.Items, item =>
            item.Source is DocumentationScribeSemanticRepositoryEvidence repository
            && repository.RepositoryPath == "Consumer.cs"
            && repository.Range.Start == overloadStart);
        Assert.All(page.Items, item => Assert.DoesNotContain("\\", item.ItemIdentity));
    }

    [Fact]
    public async Task CursorPagesHaveNoGapsDuplicatesAndRejectTampering()
    {
        using var fixture = SemanticFixture.Create(MethodId);
        var loaded = fixture.Bootstrap();
        var port = new DocumentationScribeSemanticToolPort(loaded, fixture.CreateRequest(loaded));
        var identities = new List<string>();
        DocumentationScribeContextCursor? cursor = null;
        do
        {
            var result = await port.InvokeAsync(
                DocumentationScribeSemanticToolRequest.Create(2, cursor),
                CancellationToken.None);
            var page = Assert.IsType<DocumentationScribeSemanticEvidencePage>(result.Page);
            Assert.True(page.NextCursor is null || page.Items.Length == 2);
            identities.AddRange(page.Items.Select(item => item.ItemIdentity));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.NotEmpty(identities);
        Assert.Equal(identities.Count, identities.Distinct(StringComparer.Ordinal).Count());

        var first = await port.InvokeAsync(
            DocumentationScribeSemanticToolRequest.Create(2),
            CancellationToken.None);
        var valid = Assert.IsType<DocumentationScribeSemanticEvidencePage>(first.Page).NextCursor;
        Assert.NotNull(valid);
        var value = valid.Value.Value;
        var replacement = value[^1] == 'a' ? 'b' : 'a';
        Assert.True(DocumentationScribeContextCursor.TryParse(value[..^1] + replacement, out var tampered));
        var rejected = await port.InvokeAsync(
            DocumentationScribeSemanticToolRequest.Create(2, tampered),
            CancellationToken.None);
        Assert.Same(DocumentationScribeToolOutcome.Failure, rejected.Outcome);
        Assert.Equal(DocumentationScribeSemanticFailureReason.InvalidCursor, rejected.FailureReason);
        Assert.Null(rejected.Page);
    }

    [Fact]
    public async Task UsageSourceDriftFailsClosedOnContinuation()
    {
        using var fixture = SemanticFixture.Create(MethodId);
        var loaded = fixture.Bootstrap();
        var port = new DocumentationScribeSemanticToolPort(loaded, fixture.CreateRequest(loaded));
        var first = await port.InvokeAsync(
            DocumentationScribeSemanticToolRequest.Create(2),
            CancellationToken.None);
        var cursor = Assert.IsType<DocumentationScribeSemanticEvidencePage>(first.Page).NextCursor;
        Assert.NotNull(cursor);

        File.AppendAllText(fixture.ConsumerPath, "// drift\n", new UTF8Encoding(false));
        var second = await port.InvokeAsync(
            DocumentationScribeSemanticToolRequest.Create(2, cursor),
            CancellationToken.None);

        Assert.Same(DocumentationScribeToolOutcome.Failure, second.Outcome);
        Assert.Equal(DocumentationScribeSemanticFailureReason.SourceDrift, second.FailureReason);
        Assert.Null(second.Page);
    }

    [Theory]
    [InlineData("Binding")]
    [InlineData("Usages")]
    [InlineData("Page")]
    [InlineData("FinalFreshness")]
    [InlineData("Cursor")]
    [InlineData("Publish")]
    public async Task CallerCancellationAtEveryPublicationPhasePublishesNoPageOrCursor(
        string cancellationStageId)
    {
        var cancellationStage = Enum.Parse<DocumentationScribeSemanticStage>(cancellationStageId);
        using var fixture = SemanticFixture.Create(MethodId);
        var loaded = fixture.Bootstrap();
        var request = fixture.CreateRequest(loaded);
        using var cancellation = new CancellationTokenSource();
        var port = new DocumentationScribeSemanticToolPort(
            loaded,
            request,
            null,
            stage =>
            {
                if (stage == cancellationStage)
                {
                    cancellation.Cancel();
                }
            },
            null);

        var result = await port.InvokeAsync(
            DocumentationScribeSemanticToolRequest.Create(2),
            cancellation.Token);

        Assert.Same(DocumentationScribeToolOutcome.Cancelled, result.Outcome);
        Assert.Equal(DocumentationScribeSemanticFailureReason.Cancelled, result.FailureReason);
        Assert.Null(result.Page);
    }

    [Fact]
    public async Task NonMethodTargetIsTerminalFailure()
    {
        using var fixture = SemanticFixture.Create("T:SemanticFixture.Runner");
        var loaded = fixture.Bootstrap();
        var port = new DocumentationScribeSemanticToolPort(loaded, fixture.CreateRequest(loaded));

        var result = await port.InvokeAsync(
            DocumentationScribeSemanticToolRequest.Create(20),
            CancellationToken.None);

        Assert.Same(DocumentationScribeToolOutcome.Failure, result.Outcome);
        Assert.Equal(DocumentationScribeSemanticFailureReason.UnsupportedTargetKind, result.FailureReason);
        Assert.Null(result.Page);
    }

    [Fact]
    public async Task UnsupportedMethodSignatureIsTerminalContentFreeFailure()
    {
        using var seed = SemanticFixture.Create(MethodId);
        var unsupported = Assert.Single(seed.ClassificationSet.Targets, item =>
            item.SymbolRef.DocumentationCommentId.StartsWith(
                "M:SemanticFixture.Runner.Deep(",
                StringComparison.Ordinal));
        using var fixture = SemanticFixture.Create(unsupported.SymbolRef.DocumentationCommentId);
        var loaded = fixture.Bootstrap();
        var port = new DocumentationScribeSemanticToolPort(loaded, fixture.CreateRequest(loaded));

        var result = await port.InvokeAsync(
            DocumentationScribeSemanticToolRequest.Create(20),
            CancellationToken.None);

        Assert.Same(DocumentationScribeToolOutcome.Failure, result.Outcome);
        Assert.Equal(DocumentationScribeSemanticFailureReason.UnsupportedSignature, result.FailureReason);
        Assert.Null(result.Page);
    }

    [Fact]
    public async Task SemanticIdentityCollisionFailsClosed()
    {
        using var fixture = SemanticFixture.Create(MethodId);
        var loaded = fixture.Bootstrap();
        var port = new DocumentationScribeSemanticToolPort(
            loaded,
            fixture.CreateRequest(loaded),
            null,
            null,
            _ => new string('a', 64));

        var result = await port.InvokeAsync(
            DocumentationScribeSemanticToolRequest.Create(20),
            CancellationToken.None);

        Assert.Same(DocumentationScribeToolOutcome.Failure, result.Outcome);
        Assert.Equal(DocumentationScribeSemanticFailureReason.IdentityCollision, result.FailureReason);
        Assert.Null(result.Page);
    }

    [Fact]
    public async Task CursorIsBoundToExactScribeArtifactAndFreshSession()
    {
        using var fixture = SemanticFixture.Create(MethodId);
        var loaded = fixture.Bootstrap();
        var firstPort = new DocumentationScribeSemanticToolPort(
            loaded,
            fixture.CreateRequest(loaded));
        var first = await firstPort.InvokeAsync(
            DocumentationScribeSemanticToolRequest.Create(2),
            CancellationToken.None);
        var cursor = Assert.IsType<DocumentationScribeSemanticEvidencePage>(first.Page).NextCursor;
        Assert.NotNull(cursor);

        var changedArtifactPort = new DocumentationScribeSemanticToolPort(
            loaded,
            fixture.CreateRequest(loaded, artifactVariant: true));
        var changedArtifact = await changedArtifactPort.InvokeAsync(
            DocumentationScribeSemanticToolRequest.Create(2, cursor),
            CancellationToken.None);
        Assert.Same(DocumentationScribeToolOutcome.Failure, changedArtifact.Outcome);
        Assert.Equal(DocumentationScribeSemanticFailureReason.InvalidCursor, changedArtifact.FailureReason);
        Assert.Null(changedArtifact.Page);

        using var secondFixture = SemanticFixture.Create(MethodId);
        var secondLoaded = secondFixture.Bootstrap();
        var secondPort = new DocumentationScribeSemanticToolPort(
            secondLoaded,
            secondFixture.CreateRequest(secondLoaded));
        var freshSession = await secondPort.InvokeAsync(
            DocumentationScribeSemanticToolRequest.Create(2, cursor),
            CancellationToken.None);
        Assert.Same(DocumentationScribeToolOutcome.Failure, freshSession.Outcome);
        Assert.Equal(DocumentationScribeSemanticFailureReason.InvalidCursor, freshSession.FailureReason);
        Assert.Null(freshSession.Page);
    }

    [Fact]
    public async Task SoftItemLimitIsExplicitIncompleteAndHardElapsedLimitIsTerminal()
    {
        using var fixture = SemanticFixture.Create(MethodId);
        var loaded = fixture.Bootstrap();
        var request = fixture.CreateRequest(loaded);
        var limited = new DocumentationScribeSemanticToolLimits(
            maximumPageSize: 20,
            maximumOptionalItems: 1,
            maximumResultUtf8Bytes: 65_536,
            maximumSourceFileUtf8Bytes: 4_194_304,
            maximumIncludedSourceUtf8Bytes: 8_192,
            maximumCompilations: 32,
            maximumSourceTrees: 512,
            maximumSyntaxNodes: 500_000,
            maximumElapsedMilliseconds: 10_000);
        var limitedPort = new DocumentationScribeSemanticToolPort(loaded, request, limited);
        var incomplete = await limitedPort.InvokeAsync(
            DocumentationScribeSemanticToolRequest.Create(20),
            CancellationToken.None);
        Assert.Same(DocumentationScribeToolOutcome.Incomplete, incomplete.Outcome);
        var page = Assert.IsType<DocumentationScribeSemanticEvidencePage>(incomplete.Page);
        Assert.Single(page.Items);
        Assert.Contains(page.Incomplete, item =>
            item.Reason == DocumentationScribeSemanticIncompleteReason.ItemLimit);

        var timeoutLimits = new DocumentationScribeSemanticToolLimits(
            20,
            256,
            65_536,
            4_194_304,
            8_192,
            32,
            512,
            500_000,
            1);
        var timedPort = new DocumentationScribeSemanticToolPort(
            loaded,
            request,
            timeoutLimits,
            stage =>
            {
                if (stage == DocumentationScribeSemanticStage.Binding)
                {
                    Thread.Sleep(10);
                }
            },
            null);
        var timed = await timedPort.InvokeAsync(
            DocumentationScribeSemanticToolRequest.Create(20),
            CancellationToken.None);
        Assert.Same(DocumentationScribeToolOutcome.TimedOut, timed.Outcome);
        Assert.Equal(DocumentationScribeSemanticFailureReason.TimedOut, timed.FailureReason);
        Assert.Null(timed.Page);
    }

    [Fact]
    public async Task RequestPageLimitFailureIsContentFree()
    {
        using var fixture = SemanticFixture.Create(MethodId);
        var loaded = fixture.Bootstrap();
        var limits = new DocumentationScribeSemanticToolLimits(
            2,
            256,
            65_536,
            4_194_304,
            8_192,
            32,
            512,
            500_000,
            10_000);
        var port = new DocumentationScribeSemanticToolPort(
            loaded,
            fixture.CreateRequest(loaded),
            limits);
        var result = await port.InvokeAsync(
            DocumentationScribeSemanticToolRequest.Create(3),
            CancellationToken.None);
        Assert.Same(DocumentationScribeToolOutcome.Failure, result.Outcome);
        Assert.Equal(DocumentationScribeSemanticFailureReason.InvalidRequest, result.FailureReason);
        Assert.Null(result.Page);
    }

    [Fact]
    public void RetainedCompilationContainsPostWorkspaceGeneratedTrees()
    {
        using var fixture = SemanticFixture.Create(MethodId);
        Assert.Null(fixture.ConsumerProject.Project.GetDocument(fixture.ToolGeneratedTree));
        Assert.Contains(fixture.ToolGeneratedTree, fixture.ConsumerProject.Compilation.SyntaxTrees);
        Assert.Contains(fixture.ToolGeneratedTree, fixture.ConsumerProject.SourceTrees.Keys);
    }

    [Fact]
    public async Task SupportsSelectedPartialAndExtensionMethodsWithoutBroadeningExplicitInterfacePolicy()
    {
        using var seed = SemanticFixture.Create(MethodId);
        var partial = Assert.Single(seed.ClassificationSet.Targets, item =>
            item.PrimaryKind == PrimarySymbolKind.Method
            && item.Traits.Contains(SymbolTrait.Partial)
            && item.SymbolRef.DocumentationCommentId.Contains("Trace", StringComparison.Ordinal));
        var extension = Assert.Single(seed.ClassificationSet.Targets, item =>
            item.PrimaryKind == PrimarySymbolKind.Method
            && item.Traits.Contains(SymbolTrait.Extension));
        var explicitRelation = Assert.Single(seed.ClassificationSet.Relations, item =>
            item.RelationKind == RelationKind.ExplicitInterfaceImplementation);

        Assert.DoesNotContain(
            seed.ClassificationSet.Targets,
            item => item.SymbolRef == explicitRelation.SourceSymbolRef
                && item.SupportStatus == SupportStatus.Supported);

        foreach (var target in new[]
                 {
                     partial.SymbolRef.DocumentationCommentId,
                     extension.SymbolRef.DocumentationCommentId,
                 })
        {
            using var fixture = SemanticFixture.Create(target);
            var loaded = fixture.Bootstrap();
            var port = new DocumentationScribeSemanticToolPort(
                loaded,
                fixture.CreateRequest(loaded));
            var result = await port.InvokeAsync(
                DocumentationScribeSemanticToolRequest.Create(20),
                CancellationToken.None);
            Assert.Contains(
                result.Outcome,
                new[]
                {
                    DocumentationScribeToolOutcome.Complete,
                    DocumentationScribeToolOutcome.Incomplete,
                });
            var page = Assert.IsType<DocumentationScribeSemanticEvidencePage>(result.Page);
            Assert.Equal(target, page.Core.Method.SymbolRef.DocumentationCommentId);
            if (target == partial.SymbolRef.DocumentationCommentId)
            {
                var typeParameter = Assert.Single(page.Core.Method.TypeParameters);
                Assert.True(typeParameter.HasReferenceTypeConstraint);
                Assert.True(typeParameter.HasConstructorConstraint);
                Assert.Equal(DocumentationScribeSemanticRefKind.Ref, page.Core.Method.Parameters[0].RefKind);
                Assert.Equal(DocumentationScribeSemanticRefKind.In, page.Core.Method.Parameters[1].RefKind);
                Assert.Equal(
                    DocumentationScribeSemanticNullability.Annotated,
                    page.Core.Method.Parameters[1].Type.Nullability);
            }
            else
            {
                Assert.Contains(SymbolTrait.Extension, page.Core.Method.Traits);
                Assert.Equal("runner", page.Core.Method.Parameters[0].Name);
            }
        }
    }

    [Fact]
    public async Task OptionalUtf16UsageIsOmittedAsExplicitIncomplete()
    {
        using var fixture = SemanticFixture.Create(MethodId, consumerUtf16: true);
        var loaded = fixture.Bootstrap();
        var port = new DocumentationScribeSemanticToolPort(loaded, fixture.CreateRequest(loaded));

        var result = await port.InvokeAsync(
            DocumentationScribeSemanticToolRequest.Create(20),
            CancellationToken.None);

        Assert.Same(DocumentationScribeToolOutcome.Incomplete, result.Outcome);
        var page = Assert.IsType<DocumentationScribeSemanticEvidencePage>(result.Page);
        Assert.Contains(page.Incomplete, item =>
            item.Reason == DocumentationScribeSemanticIncompleteReason.UnsupportedEncoding);
        Assert.DoesNotContain(page.Items, item =>
            item.Source is DocumentationScribeSemanticRepositoryEvidence repository
            && repository.RepositoryPath == "Consumer.cs");
    }

    [Fact]
    public async Task ContentIdentityIsStableWhileSessionCorrelationChanges()
    {
        using var firstFixture = SemanticFixture.Create(MethodId);
        using var secondFixture = SemanticFixture.Create(MethodId);
        var firstLoaded = firstFixture.Bootstrap();
        var secondLoaded = secondFixture.Bootstrap();
        var first = await new DocumentationScribeSemanticToolPort(
                firstLoaded,
                firstFixture.CreateRequest(firstLoaded))
            .InvokeAsync(DocumentationScribeSemanticToolRequest.Create(20), CancellationToken.None);
        var second = await new DocumentationScribeSemanticToolPort(
                secondLoaded,
                secondFixture.CreateRequest(secondLoaded))
            .InvokeAsync(DocumentationScribeSemanticToolRequest.Create(20), CancellationToken.None);
        var firstPage = Assert.IsType<DocumentationScribeSemanticEvidencePage>(first.Page);
        var secondPage = Assert.IsType<DocumentationScribeSemanticEvidencePage>(second.Page);

        Assert.Equal(firstPage.Core.ContentIdentity, secondPage.Core.ContentIdentity);
        Assert.NotEqual(firstPage.Core.CorrelationIdentity, secondPage.Core.CorrelationIdentity);
        Assert.Equal(
            firstPage.Items.Select(item => item.ItemIdentity),
            secondPage.Items.Select(item => item.ItemIdentity));
    }

    [Fact]
    public async Task TypedSemanticContentIsDeterministicAcrossFreshProcesses()
    {
        var childOutput = Environment.GetEnvironmentVariable(FreshProcessOutputVariable);
        if (!string.IsNullOrWhiteSpace(childOutput))
        {
            using var fixture = SemanticFixture.Create(MethodId);
            var loaded = fixture.Bootstrap();
            var port = new DocumentationScribeSemanticToolPort(loaded, fixture.CreateRequest(loaded));
            var items = new List<DocumentationScribeSemanticEvidenceItem>();
            DocumentationScribeSemanticTargetCore? core = null;
            ImmutableArray<DocumentationScribeSemanticIncomplete> incomplete = [];
            DocumentationScribeContextCursor? cursor = null;
            do
            {
                var result = await port.InvokeAsync(
                    DocumentationScribeSemanticToolRequest.Create(3, cursor),
                    CancellationToken.None);
                var page = Assert.IsType<DocumentationScribeSemanticEvidencePage>(result.Page);
                core ??= page.Core;
                incomplete = page.Incomplete;
                items.AddRange(page.Items);
                cursor = page.NextCursor;
            }
            while (cursor is not null);

            var projection = new
            {
                core.ContentIdentity,
                core.Method,
                core.ApplicableComponents,
                core.Documentation,
                Declaration = ProjectSource(core.Declaration),
                Items = items.Select(item => new
                {
                    item.ItemIdentity,
                    item.Kind,
                    Source = ProjectSource(item.Source),
                    item.UsageKind,
                    item.RelationKind,
                    item.RelationDirection,
                    item.RelatedSymbolRef,
                }),
                Incomplete = incomplete,
            };
            await File.WriteAllTextAsync(
                childOutput,
                JsonSerializer.Serialize(projection),
                new UTF8Encoding(false));
            return;
        }

        var firstOutput = Path.Join(
            Path.GetTempPath(),
            "contract-scribe-semantic-probe-" + Guid.NewGuid().ToString("N") + ".json");
        var secondOutput = Path.Join(
            Path.GetTempPath(),
            "contract-scribe-semantic-probe-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var repositoryRoot = FindRepositoryRootForProbe();
            await RunFreshProcessProbeAsync(firstOutput, repositoryRoot, "en-US", "UTC");
            await RunFreshProcessProbeAsync(
                secondOutput,
                Path.Join(repositoryRoot, "tests"),
                "tr-TR",
                "Asia/Shanghai");
            Assert.Equal(
                await File.ReadAllTextAsync(firstOutput),
                await File.ReadAllTextAsync(secondOutput));
        }
        finally
        {
            File.Delete(firstOutput);
            File.Delete(secondOutput);
        }
    }

    private static SemanticSourceProjection ProjectSource(
        DocumentationScribeSemanticSourceEvidence source) => source switch
        {
            DocumentationScribeSemanticRepositoryEvidence repository => CreateSourceProjection(
                source,
                "repository",
                repository.RepositoryPath,
                null,
                null),
            DocumentationScribeSemanticSourceGeneratorEvidence generated => CreateSourceProjection(
                source,
                "source-generator",
                null,
                generated.ProducerId,
                generated.OutputId),
            DocumentationScribeSemanticToolGeneratedEvidence generated => CreateSourceProjection(
                source,
                "tool-generated",
                null,
                generated.ProducerId,
                generated.OutputId),
            _ => throw new InvalidOperationException("Unexpected semantic source view."),
        };

    private static SemanticSourceProjection CreateSourceProjection(
        DocumentationScribeSemanticSourceEvidence source,
        string kind,
        string? repositoryPath,
        string? producerId,
        string? outputId) => new(
            kind,
            repositoryPath,
            producerId,
            outputId,
            source.ContentSha256,
            source.IncludedContentSha256,
            source.OriginalUtf8ByteCount,
            source.IncludedUtf8ByteCount,
            source.IsTruncated,
            source.HasUtf8Bom,
            source.Range,
            source.IncludedRange,
            source.Content);

    private static async Task RunFreshProcessProbeAsync(
        string outputPath,
        string workingDirectory,
        string culture,
        string timeZone)
    {
        var repositoryRoot = FindRepositoryRootForProbe();
        var projectPath = Path.Join(
            repositoryRoot,
            "tests",
            "ContractScribe.IntegrationTests",
            "ContractScribe.IntegrationTests.csproj");
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("test");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("--configuration");
        startInfo.ArgumentList.Add("Release");
        startInfo.ArgumentList.Add("--no-build");
        startInfo.ArgumentList.Add("--no-restore");
        startInfo.ArgumentList.Add("--filter");
        startInfo.ArgumentList.Add(
            "FullyQualifiedName=ContractScribe.Roslyn.IntegrationTests.DocumentationScribeSemanticToolIntegrationTests.TypedSemanticContentIsDeterministicAcrossFreshProcesses");
        startInfo.Environment[FreshProcessOutputVariable] = outputPath;
        startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = culture;
        startInfo.Environment["LANG"] = culture + ".UTF-8";
        startInfo.Environment["LC_ALL"] = culture + ".UTF-8";
        startInfo.Environment["TZ"] = timeZone;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The fresh-process semantic probe did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The fresh-process semantic probe timed out.");
        }

        var output = await standardOutput;
        var error = await standardError;
        Assert.True(
            process.ExitCode == 0,
            $"Fresh-process probe failed with exit code {process.ExitCode}.\nstdout:\n{output}\nstderr:\n{error}");
        Assert.True(File.Exists(outputPath), "Fresh-process probe did not publish its projection.");
    }

    private static string FindRepositoryRootForProbe()
    {
        var current = AppContext.BaseDirectory;
        while (!File.Exists(Path.Join(current, "ContractScribe.slnx")))
        {
            current = Directory.GetParent(current)?.FullName
                ?? throw new InvalidOperationException("Repository root not found.");
        }

        return current;
    }

    private sealed record SemanticSourceProjection(
        string Kind,
        string? RepositoryPath,
        string? ProducerId,
        string? OutputId,
        string ContentSha256,
        string IncludedContentSha256,
        int OriginalUtf8ByteCount,
        int IncludedUtf8ByteCount,
        bool IsTruncated,
        bool HasUtf8Bom,
        Utf16Span Range,
        Utf16Span? IncludedRange,
        string Content);

    private sealed class SemanticFixture : IDisposable
    {
        private static readonly ImmutableArray<MetadataReference> PlatformReferences =
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator)
                .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
                .ToImmutableArray();
        private readonly LoadedRepositorySession repository;
        private readonly ClassifiedRepositorySession classified;
        private readonly string targetDocumentationId;
        private readonly SyntaxReference targetReference;

        private SemanticFixture(
            string root,
            string consumerPath,
            LoadedRepositorySession repository,
            ClassifiedRepositorySession classified,
            string targetDocumentationId,
            SyntaxReference targetReference,
            LoadedProject consumerProject,
            SyntaxTree toolGeneratedTree)
        {
            Root = root;
            ConsumerPath = consumerPath;
            this.repository = repository;
            this.classified = classified;
            this.targetDocumentationId = targetDocumentationId;
            this.targetReference = targetReference;
            ConsumerProject = consumerProject;
            ToolGeneratedTree = toolGeneratedTree;
        }

        public string Root { get; }

        public string ConsumerPath { get; }

        public LoadedProject ConsumerProject { get; }

        public SyntaxTree ToolGeneratedTree { get; }

        public ClassificationSet ClassificationSet => classified.Classification.ClassificationSet!;

        public static SemanticFixture Create(
            string targetDocumentationId,
            bool consumerUtf16 = false)
        {
            var repositoryRoot = FindRepositoryRoot();
            var sourceRoot = Path.Join(
                repositoryRoot,
                "tests",
                "fixtures",
                "documentation-scribe",
                "semantic-tools");
            var root = Path.Join(
                Path.GetTempPath(),
                "contract-scribe-semantic-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllText(
                Path.Join(root, "Target.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />\n",
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Join(root, "Consumer.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />\n",
                new UTF8Encoding(false));

            var texts = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in new[]
                     {
                         "Target.Part1.cs",
                         "Target.Part2.cs",
                         "Consumer.cs",
                         "TestConsumer.cs",
                     })
            {
                var text = File.ReadAllText(Path.Join(sourceRoot, name));
                texts.Add(name, text);
                File.WriteAllText(
                    Path.Join(root, name),
                    text,
                    consumerUtf16 && name == "Consumer.cs"
                        ? Encoding.Unicode
                        : new UTF8Encoding(false));
            }

            var parseOptions = new CSharpParseOptions(
                LanguageVersion.Preview,
                documentationMode: DocumentationMode.Diagnose);
            var targetTrees = new[] { "Target.Part1.cs", "Target.Part2.cs" }
                .Select(name => CSharpSyntaxTree.ParseText(
                    texts[name],
                    parseOptions,
                    name,
                    Encoding.UTF8))
                .ToArray();
            var targetCompilation = CSharpCompilation.Create(
                "SemanticFixture",
                targetTrees,
                PlatformReferences,
                new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    deterministic: true,
                    nullableContextOptions: NullableContextOptions.Enable));

            const string xunitText =
                "namespace Xunit; public sealed class FactAttribute : System.Attribute { } public sealed class TheoryAttribute : System.Attribute { }";
            var xunitCompilation = CSharpCompilation.Create(
                "xunit.core",
                [CSharpSyntaxTree.ParseText(xunitText, parseOptions, "FactAttribute.cs", Encoding.UTF8)],
                PlatformReferences,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, deterministic: true));
            var consumerTrees = new[] { "Consumer.cs", "TestConsumer.cs" }
                .Select(name => CSharpSyntaxTree.ParseText(
                    texts[name],
                    parseOptions,
                    name,
                    Encoding.UTF8))
                .ToList();
            const string sourceGeneratedText =
                "using SemanticFixture; namespace GeneratedConsumer; public sealed class SourceGeneratedUse { public string Run() => new Runner().Execute(\"source-generator\"); }";
            const string toolGeneratedText =
                "using SemanticFixture; namespace GeneratedConsumer; public sealed class ToolGeneratedUse { public string Run() => new Runner().Execute(\"tool-generated\"); }";
            var sourceGeneratedTree = CSharpSyntaxTree.ParseText(
                sourceGeneratedText,
                parseOptions,
                "SourceGeneratedUse.g.cs",
                Encoding.UTF8);
            var toolGeneratedTree = CSharpSyntaxTree.ParseText(
                toolGeneratedText,
                parseOptions,
                "ToolGeneratedUse.g.cs",
                Encoding.UTF8);
            consumerTrees.Add(sourceGeneratedTree);
            consumerTrees.Add(toolGeneratedTree);
            var consumerCompilation = CSharpCompilation.Create(
                "SemanticConsumer",
                consumerTrees,
                PlatformReferences
                    .Add(targetCompilation.ToMetadataReference())
                    .Add(xunitCompilation.ToMetadataReference()),
                new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    deterministic: true,
                    nullableContextOptions: NullableContextOptions.Enable));

            var workspace = new AdhocWorkspace();
            var targetWorkspaceProject = workspace.AddProject("Target", LanguageNames.CSharp);
            var consumerWorkspaceProject = workspace.AddProject("Consumer", LanguageNames.CSharp);
            var resolver = new RepositoryPathResolver();
            var targetBindings = new Dictionary<SyntaxTree, LoadedSourceTree>(
                ReferenceEqualityComparer.Instance);
            foreach (var tree in targetTrees)
            {
                targetBindings.Add(tree, new LoadedSourceTree(
                    LoadedSourceKind.Repository,
                    tree.FilePath,
                    resolver.PhysicalIdentity(root, Path.Join(root, tree.FilePath)),
                    null));
            }
            var sourceFact = GeneratedFact(
                "Consumer.csproj",
                "consumer.net10.0",
                LoadedSourceKind.SourceGenerator,
                sourceGeneratedText,
                'a',
                'b');
            var toolFact = GeneratedFact(
                "Consumer.csproj",
                "consumer.net10.0",
                LoadedSourceKind.ToolGenerated,
                toolGeneratedText,
                'c',
                'd');
            var consumerBindings = new Dictionary<SyntaxTree, LoadedSourceTree>(
                ReferenceEqualityComparer.Instance);
            foreach (var tree in consumerTrees.Take(2))
            {
                consumerBindings.Add(
                    tree,
                    new LoadedSourceTree(
                        LoadedSourceKind.Repository,
                        tree.FilePath,
                        resolver.PhysicalIdentity(root, Path.Join(root, tree.FilePath)),
                        null));
            }
            consumerBindings.Add(
                sourceGeneratedTree,
                new LoadedSourceTree(LoadedSourceKind.SourceGenerator, null, null, sourceFact));
            consumerBindings.Add(
                toolGeneratedTree,
                new LoadedSourceTree(LoadedSourceKind.ToolGenerated, null, null, toolFact));

            var targetProject = new LoadedProject(
                "Target.csproj",
                "net10.0",
                "target.net10.0",
                LoadedProjectRole.AuditRoot,
                [],
                targetWorkspaceProject,
                targetCompilation,
                targetBindings);
            var consumerProject = new LoadedProject(
                "Consumer.csproj",
                "net10.0",
                "consumer.net10.0",
                LoadedProjectRole.AuditRoot,
                ["Target.csproj"],
                consumerWorkspaceProject,
                consumerCompilation,
                consumerBindings);
            Assert.True(RepositoryContextRef.TryParse(
                "repoctx-" + Guid.NewGuid().ToString("N"),
                out var repositoryContextRef));
            var repository = new LoadedRepositorySession(
                repositoryContextRef,
                root,
                "Target.csproj",
                new ToolchainIdentity("test", "test", "test", "test"),
                [targetProject, consumerProject],
                [sourceFact, toolFact],
                workspace);
            var classified = new SymbolClassifier().ClassifySession(
                repository,
                TargetProfile.ExternalApi);
            Assert.Equal(ClassificationRunStatus.Success, classified.Classification.Status);
            var target = Assert.Single(
                classified.Classification.ClassificationSet!.Targets,
                item => item.SymbolRef.DocumentationCommentId == targetDocumentationId
                    && item.SupportStatus == SupportStatus.Supported);
            var project = repository.Projects.Single(item =>
                item.CompilationContextRef == target.SymbolRef.CompilationContextRef);
            var symbol = Assert.Single(DocumentationCommentId.GetSymbolsForDeclarationId(
                targetDocumentationId,
                project.Compilation));
            var reference = symbol.DeclaringSyntaxReferences
                .OrderBy(item => item.SyntaxTree.FilePath, StringComparer.Ordinal)
                .First();
            return new SemanticFixture(
                root,
                Path.Join(root, "Consumer.cs"),
                repository,
                classified,
                targetDocumentationId,
                reference,
                consumerProject,
                toolGeneratedTree);
        }

        public DocumentationScribeLoadedContext Bootstrap()
        {
            var sourcePath = targetReference.SyntaxTree.FilePath;
            var sourceBytes = File.ReadAllBytes(Path.Join(Root, sourcePath));
            var selection = DocumentationScribeContextValidation.CreateBootstrapSelection(
                repository.RepositoryContextRef,
                repository.InputIdentity,
                TargetProfile.ExternalApi,
                classified.Classification.ClassificationSet!.Targets.Single(item =>
                    item.SymbolRef.DocumentationCommentId == targetDocumentationId).SymbolRef,
                sourcePath,
                targetReference.Span.Start,
                targetReference.Span.End,
                Sha(sourceBytes));
            var result = new DocumentationScribeContextBootstrapper().Bootstrap(classified, selection);
            Assert.Contains(
                result.Status,
                new[]
                {
                    DocumentationScribeContextBootstrapStatus.Succeeded,
                    DocumentationScribeContextBootstrapStatus.Incomplete,
                });
            return Assert.IsType<DocumentationScribeLoadedContext>(result.Context);
        }

        public DocumentationScribeRequest CreateRequest(
            DocumentationScribeLoadedContext loaded,
            bool artifactVariant = false)
        {
            var fixture = Path.Join(
                FindRepositoryRoot(),
                "tests",
                "fixtures",
                "documentation-scribe",
                "v1",
                "valid",
                "request.json");
            var root = JsonNode.Parse(File.ReadAllBytes(fixture))!.AsObject();
            var context = root["context"]!.AsObject();
            context["repositoryContextRef"] = loaded.Facts.RepositoryContextRef.Value;
            context["inputIdentity"] = loaded.Facts.InputIdentity;
            context["targetProfile"] = ClassificationVocabulary.GetId(loaded.Facts.TargetProfile);
            var target = root["target"]!.AsObject();
            var symbol = target["symbolRef"]!.AsObject();
            symbol["compilationContextRef"] = loaded.Facts.SymbolRef.CompilationContextRef;
            symbol["documentationCommentId"] = loaded.Facts.SymbolRef.DocumentationCommentId;
            var sourceFact = Assert.Single(loaded.Facts.Evidence);
            var sourceCommitment = target["sourceCommitment"]!.AsObject();
            sourceCommitment["contentSha256"] = sourceFact.Commitment.ContentSha256;
            var repositoryLocator = Assert.IsType<RepositoryEvidenceLocator>(
                sourceFact.Commitment.Locator);
            var repositoryJson = sourceCommitment["locator"]!["repository"]!.AsObject();
            repositoryJson["path"] = repositoryLocator.Path;
            repositoryJson["span"] = new JsonObject
            {
                ["start"] = repositoryLocator.Span!.Value.Start,
                ["end"] = repositoryLocator.Span.Value.End,
            };

            var classification = classified.Classification.ClassificationSet!;
            var targetSymbol = Assert.Single(DocumentationCommentId.GetSymbolsForDeclarationId(
                loaded.Facts.SymbolRef.DocumentationCommentId,
                repositorySessionProject(loaded).Compilation));
            var method = targetSymbol as IMethodSymbol;
            var components = classification.Components
                .Where(item => item.ParentSymbolRef == loaded.Facts.SymbolRef
                    && item.SupportStatus == SupportStatus.Supported
                    && item.ComponentKind is ComponentKind.TypeParameter
                        or ComponentKind.Parameter
                        or ComponentKind.Return)
                .OrderBy(item => item.ComponentKind switch
                {
                    ComponentKind.TypeParameter => 0,
                    ComponentKind.Parameter => 1,
                    ComponentKind.Return => 2,
                    _ => throw new InvalidOperationException(),
                })
                .ThenBy(item => item.Identity, StringComparer.Ordinal)
                .Select(item => new
                {
                    Kind = item.ComponentKind switch
                    {
                        ComponentKind.TypeParameter => "typeParameter",
                        ComponentKind.Parameter => "parameter",
                        ComponentKind.Return => "return",
                        _ => throw new InvalidOperationException(),
                    },
                    item.Identity,
                    Name = ComponentName(item, method),
                })
                .ToArray();
            target["applicableComponents"] = new JsonArray(components.Select(item =>
            {
                var node = new JsonObject
                {
                    ["kind"] = item.Kind,
                    ["identity"] = item.Identity,
                };
                if (item.Name is not null)
                {
                    node["name"] = item.Name;
                }
                return node;
            }).ToArray<JsonNode?>());
            root["styleProfile"]!["componentPolicies"] = new JsonArray(components.Select(item =>
                (JsonNode?)new JsonObject
                {
                    ["componentIdentity"] = item.Identity,
                    ["disposition"] = "required",
                    ["maximumScalars"] = 300,
                }).ToArray());
            root["contextReferences"] = new JsonArray(loaded.Facts.Instructions.Select((item, index) =>
                (JsonNode?)new JsonObject
                {
                    ["contextReferenceId"] = $"context.semantic.{index:D4}",
                    ["kind"] = "context.project-instruction",
                    ["repositoryContextRef"] = loaded.Facts.RepositoryContextRef.Value,
                    ["path"] = item.Commitment.RepositoryPath,
                    ["contentSha256"] = item.Commitment.ContentSha256,
                    ["originalUtf8ByteCount"] = item.Commitment.OriginalUtf8ByteCount,
                    ["includedUtf8ByteCount"] = item.Commitment.IncludedUtf8ByteCount,
                    ["isTruncated"] = item.Commitment.IsTruncated,
                }).ToArray());
            root["evidenceReferences"] = new JsonArray();
            root["evidenceConflicts"] = new JsonArray();
            if (artifactVariant)
            {
                root["styleProfile"]!["allowedLiterals"]!.AsArray().Add("semantic-variant");
            }
            var parsed = DocumentationScribeValidation.ParseRequest(
                Encoding.UTF8.GetBytes(root.ToJsonString()));
            Assert.True(
                parsed.IsValid,
                parsed.Failure?.Code + "|" + parsed.Failure?.Pointer + "|" + target["applicableComponents"]);
            return Assert.IsType<DocumentationScribeRequest>(parsed.Request);

            LoadedProject repositorySessionProject(DocumentationScribeLoadedContext context) =>
                this.repository.Projects.Single(item =>
                    item.CompilationContextRef == context.Facts.CompilationContextRef);
        }

        public void Dispose()
        {
            repository.Dispose();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // Best-effort fixture cleanup; test assertions own the result.
            }
        }

        private static GeneratedSourceFact GeneratedFact(
            string project,
            string context,
            LoadedSourceKind kind,
            string text,
            char producer,
            char output) => new(
                project,
                context,
                (kind == LoadedSourceKind.SourceGenerator ? "sgp." : "tgp.") + new string(producer, 64),
                (kind == LoadedSourceKind.SourceGenerator ? "sgo." : "tgo.") + new string(output, 64),
                Sha(Encoding.UTF8.GetBytes(text)),
                text);

        private static string? ComponentName(ComponentClassification component, IMethodSymbol? method)
        {
            if (method is null || component.ComponentKind is ComponentKind.Return or ComponentKind.Value)
            {
                return null;
            }
            var separator = component.Identity.LastIndexOf('/');
            if (separator < 0 || !int.TryParse(component.Identity[(separator + 1)..], out var ordinal))
            {
                return null;
            }
            return component.ComponentKind == ComponentKind.TypeParameter
                ? method.TypeParameters[ordinal].Name
                : method.Parameters[ordinal].Name;
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Join(directory.FullName, "ContractScribe.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new InvalidOperationException("Repository root was not found.");
    }

    private static string Sha(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
