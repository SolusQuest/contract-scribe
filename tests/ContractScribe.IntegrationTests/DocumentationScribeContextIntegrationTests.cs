using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ContractScribe.Roslyn.IntegrationTests;

[Collection("Integration process lane 2")]
public sealed class DocumentationScribeContextIntegrationTests
{
    private const string TargetDocumentationId = "T:Fixture.Widget";
    private const string ProjectIdentity = "Fixture.csproj";
    private const string CompilationContext = "fixture.net10.0";
    private const string ChildOutputVariable = "CONTRACTSCRIBE_CONTEXT_PROBE_OUTPUT";

    [Fact]
    public void ConfiguredEntrypointExcludesRootDefaultAndLoadsNestedInstructionsInOrder()
    {
        using var fixture = ContextFixture.Create(BasicSource());
        fixture.PopulateBasicInstructions();

        var result = fixture.Bootstrap("custom-agent.md");

        Assert.Equal(DocumentationScribeContextBootstrapStatus.Succeeded, result.Status);
        var context = Assert.IsType<DocumentationScribeLoadedContext>(result.Context);
        Assert.Equal(
            ["custom-agent.md", "src/AGENTS.md", "src/App/AGENTS.md"],
            context.Facts.Instructions.Select(item => item.Commitment.RepositoryPath));
        Assert.DoesNotContain(
            context.Facts.Instructions,
            item => item.Content.Contains("root-default-marker", StringComparison.Ordinal));
        Assert.Equal(2, context.Facts.Routes.Length);
        Assert.All(
            context.Facts.Routes,
            route => Assert.Equal(
                DocumentationScribeContextRouteSelection.DeterministicBootstrap,
                route.Selection));
    }

    [Fact]
    public void MissingOptionalRootDefaultProducesIncompleteFactsWithoutFailure()
    {
        using var fixture = ContextFixture.Create(BasicSource());

        var result = fixture.Bootstrap();

        Assert.Equal(DocumentationScribeContextBootstrapStatus.Incomplete, result.Status);
        Assert.Null(result.Failure);
        var context = Assert.IsType<DocumentationScribeLoadedContext>(result.Context);
        var omission = Assert.Single(context.Facts.Omissions);
        Assert.Equal(DocumentationScribeContextOmissionReason.MissingOptional, omission.Reason);
        Assert.Empty(context.Facts.Instructions);
    }

    [Fact]
    public void MissingConfiguredEntrypointIsTerminalAndNeverFallsBackToRootDefault()
    {
        using var fixture = ContextFixture.Create(BasicSource());
        fixture.WriteText("AGENTS.md", "root-default-marker\n");

        var result = fixture.Bootstrap("missing-agent.md");

        Assert.Equal(DocumentationScribeContextBootstrapStatus.Failed, result.Status);
        Assert.Equal("context.stale.configured-entrypoint", result.Failure?.Code);
        Assert.Null(result.Context);
    }

    [Fact]
    public void ConfiguredNestedEntrypointIsIncludedExactlyOnce()
    {
        using var fixture = ContextFixture.Create(BasicSource());
        fixture.PopulateBasicInstructions();

        var result = fixture.Bootstrap("src/App/AGENTS.md");

        Assert.Equal(DocumentationScribeContextBootstrapStatus.Succeeded, result.Status);
        var context = Assert.IsType<DocumentationScribeLoadedContext>(result.Context);
        Assert.Equal(
            1,
            context.Facts.Instructions.Count(item =>
                item.Commitment.RepositoryPath == "src/App/AGENTS.md"));
        Assert.DoesNotContain(context.Facts.Instructions, item =>
            item.Commitment.RepositoryPath == "AGENTS.md");
    }

    [Theory]
    [InlineData("repository")]
    [InlineData("input")]
    [InlineData("profile")]
    [InlineData("compilation")]
    [InlineData("symbol")]
    public void CorrelationMismatchesFailBeforeAnyFileOpenOrRead(string mismatch)
    {
        using var fixture = ContextFixture.Create(BasicSource());
        fixture.PopulateBasicInstructions();
        var observed = new List<DocumentationScribeContextBootstrapStage>();
        var bootstrapper = new DocumentationScribeContextBootstrapper(observed.Add);
        var selection = mismatch switch
        {
            "repository" => fixture.CreateSelection(
                repositoryContextRef: ParseRepositoryContext('9')),
            "input" => fixture.CreateSelection(inputIdentity: "Other.csproj"),
            "profile" => fixture.CreateSelection(targetProfile: TargetProfile.AssemblyVisible),
            "compilation" => fixture.CreateSelection(symbolRef: new SymbolRef(
                "other.net10.0",
                fixture.SymbolRef.DocumentationCommentId)),
            "symbol" => fixture.CreateSelection(symbolRef: new SymbolRef(
                fixture.SymbolRef.CompilationContextRef,
                "T:Fixture.Missing")),
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch)),
        };

        var result = bootstrapper.Bootstrap(fixture.ClassifiedSession, selection);

        Assert.Null(result.Context);
        Assert.Contains(
            result.Status,
            new[]
            {
                DocumentationScribeContextBootstrapStatus.Failed,
                DocumentationScribeContextBootstrapStatus.Unavailable,
            });
        Assert.DoesNotContain(DocumentationScribeContextBootstrapStage.Open, observed);
        Assert.DoesNotContain(DocumentationScribeContextBootstrapStage.Read, observed);
    }

    [Fact]
    public void DisposedSessionFailsBeforeAnyFileOpenOrRead()
    {
        using var fixture = ContextFixture.Create(BasicSource());
        var observed = new List<DocumentationScribeContextBootstrapStage>();
        var bootstrapper = new DocumentationScribeContextBootstrapper(observed.Add);
        var selection = fixture.CreateSelection();
        fixture.DisposeSession();

        var result = bootstrapper.Bootstrap(fixture.ClassifiedSession, selection);

        Assert.Equal(DocumentationScribeContextBootstrapStatus.Failed, result.Status);
        Assert.Equal("context.correlation.session", result.Failure?.Code);
        Assert.DoesNotContain(DocumentationScribeContextBootstrapStage.Open, observed);
        Assert.DoesNotContain(DocumentationScribeContextBootstrapStage.Read, observed);
    }

    [Fact]
    public void PartialDeclarationsWithinOneScopeAreAccepted()
    {
        using var fixture = ContextFixture.Create(
            new SourceInput(
                "src/App/Widget.Part1.cs",
                "namespace Fixture; public partial class Widget { public void First() { } }\n"),
            new SourceInput(
                "src/App/Widget.Part2.cs",
                "namespace Fixture; public partial class Widget { public void Second() { } }\n"));
        fixture.WriteText("AGENTS.md", "root instruction\n");

        var result = fixture.Bootstrap(selectionPath: "src/App/Widget.Part1.cs");

        Assert.Equal(DocumentationScribeContextBootstrapStatus.Succeeded, result.Status);
        Assert.NotNull(result.Context);
    }

    [Fact]
    public void PartialDeclarationsAcrossScopesAreRejectedBeforeReadingFiles()
    {
        using var fixture = ContextFixture.Create(
            new SourceInput(
                "src/A/Widget.Part1.cs",
                "namespace Fixture; public partial class Widget { public void First() { } }\n"),
            new SourceInput(
                "src/B/Widget.Part2.cs",
                "namespace Fixture; public partial class Widget { public void Second() { } }\n"));
        var observed = new List<DocumentationScribeContextBootstrapStage>();
        var bootstrapper = new DocumentationScribeContextBootstrapper(observed.Add);

        var result = bootstrapper.Bootstrap(
            fixture.ClassifiedSession,
            fixture.CreateSelection(sourcePath: "src/A/Widget.Part1.cs"));

        Assert.Equal(DocumentationScribeContextBootstrapStatus.Unavailable, result.Status);
        Assert.Equal("context.scope.not-unique", result.Failure?.Code);
        Assert.DoesNotContain(DocumentationScribeContextBootstrapStage.Open, observed);
        Assert.DoesNotContain(DocumentationScribeContextBootstrapStage.Read, observed);
    }

    [Theory]
    [InlineData(
        "M:Fixture.Widget.Run",
        "namespace Fixture; public partial class Widget { public partial void Run(); }\n",
        "namespace Fixture; public partial class Widget { public partial void Run() { } }\n")]
    [InlineData(
        "P:Fixture.Widget.Value",
        "namespace Fixture; public partial class Widget { public partial int Value { get; } }\n",
        "namespace Fixture; public partial class Widget { public partial int Value { get => 42; } }\n")]
    public void PartialMembersWithinOneScopeUseDefinitionAndImplementationDeclarations(
        string documentationId,
        string definition,
        string implementation)
    {
        using var fixture = ContextFixture.CreateForTarget(
            documentationId,
            new SourceInput("src/App/Widget.Definition.cs", definition),
            new SourceInput("src/App/Widget.Implementation.cs", implementation));
        fixture.WriteText("AGENTS.md", "root instruction\n");

        var result = fixture.Bootstrap(selectionPath: "src/App/Widget.Definition.cs");

        Assert.Equal(DocumentationScribeContextBootstrapStatus.Succeeded, result.Status);
        Assert.NotNull(result.Context);
    }

    [Fact]
    public void PartialMethodAcrossScopesIsRejectedBeforeReadingFiles()
    {
        using var fixture = ContextFixture.CreateForTarget(
            "M:Fixture.Widget.Run",
            new SourceInput(
                "src/A/Widget.Definition.cs",
                "namespace Fixture; public partial class Widget { public partial void Run(); }\n"),
            new SourceInput(
                "src/B/Widget.Implementation.cs",
                "namespace Fixture; public partial class Widget { public partial void Run() { } }\n"));
        var observed = new List<DocumentationScribeContextBootstrapStage>();

        var result = new DocumentationScribeContextBootstrapper(observed.Add).Bootstrap(
            fixture.ClassifiedSession,
            fixture.CreateSelection(sourcePath: "src/A/Widget.Definition.cs"));

        Assert.Equal(DocumentationScribeContextBootstrapStatus.Unavailable, result.Status);
        Assert.Equal("context.scope.not-unique", result.Failure?.Code);
        Assert.DoesNotContain(DocumentationScribeContextBootstrapStage.Open, observed);
    }

    [Fact]
    public void LongDocumentationIdPublishesADigestSubjectWithoutInternalFailure()
    {
        var name = new string('A', 1_014);
        var documentationId = "T:Fixture." + name;
        Assert.Equal(1_024, documentationId.Length);
        using var fixture = ContextFixture.CreateForTarget(
            documentationId,
            new SourceInput(
                "src/App/LongWidget.cs",
                "namespace Fixture; public class " + name + " { }\n"));

        var result = fixture.Bootstrap();

        Assert.Equal(DocumentationScribeContextBootstrapStatus.Incomplete, result.Status);
        var evidence = Assert.Single(
            Assert.IsType<DocumentationScribeLoadedContext>(result.Context).Facts.Evidence);
        Assert.StartsWith("symbol.", evidence.SubjectId, StringComparison.Ordinal);
        Assert.Equal("symbol.".Length + 64, evidence.SubjectId.Length);
    }

    [Fact]
    public void DeclarationReferenceAndInspectedByteBudgetsFailBeforeFileReads()
    {
        using var fixture = ContextFixture.Create(
            new SourceInput(
                "src/App/Widget.Part1.cs",
                "namespace Fixture; public partial class Widget { }\n"),
            new SourceInput(
                "src/App/Widget.Part2.cs",
                "namespace Fixture; public partial class Widget { }\n"));
        foreach (var boundary in new[] { "references", "files", "inspected-bytes" })
        {
            var observed = new List<DocumentationScribeContextBootstrapStage>();
            var limits = ScopeLimits(
                maximumDeclarationReferences: boundary == "references" ? 1 : 64,
                maximumDeclarationFiles: boundary == "files" ? 1 : 16,
                maximumInspectedSourceUtf8Bytes: boundary == "inspected-bytes" ? 1 : 4096);

            var result = new DocumentationScribeContextBootstrapper(observed.Add).Bootstrap(
                fixture.ClassifiedSession,
                fixture.CreateSelection(
                    sourcePath: "src/App/Widget.Part1.cs",
                    limits: limits));

            Assert.Equal(DocumentationScribeContextBootstrapStatus.BudgetExhausted, result.Status);
            Assert.Equal(
                boundary == "references"
                    ? "context.budget.declaration-references"
                    : boundary == "files"
                        ? "context.budget.declaration-files"
                    : "context.budget.inspected-source-bytes",
                result.Failure?.Code);
            Assert.DoesNotContain(DocumentationScribeContextBootstrapStage.Open, observed);
        }
    }

    [Fact]
    public void ScopeResolutionHonorsTheElapsedDeadlineInsideEnumeration()
    {
        using var fixture = ContextFixture.Create(
            new SourceInput(
                "src/App/Widget.Part1.cs",
                "namespace Fixture; public partial class Widget { }\n"),
            new SourceInput(
                "src/App/Widget.Part2.cs",
                "namespace Fixture; public partial class Widget { }\n"));
        var scopeEntered = false;
        var scopeChecks = 0;
        var observed = new List<DocumentationScribeContextBootstrapStage>();
        var bootstrapper = new DocumentationScribeContextBootstrapper(
            stage =>
            {
                observed.Add(stage);
                scopeEntered |= stage == DocumentationScribeContextBootstrapStage.ScopeResolution;
            },
            clock: () => scopeEntered && ++scopeChecks >= 3 ? 2 : 0);
        var limits = DocumentationScribeContextValidation.CreateLimits(
            maximumInstructionFiles: 8,
            maximumInstructionDepth: 8,
            maximumInstructionFileUtf8Bytes: 1024,
            maximumDeclarationReferences: 64,
            maximumDeclarationFiles: 16,
            maximumInspectedSourceUtf8Bytes: 4096,
            maximumSourceFileUtf8Bytes: 4096,
            maximumIncludedSourceUtf8Bytes: 1024,
            maximumTotalContextUtf8Bytes: 4096,
            maximumElapsedMilliseconds: 1);

        var result = bootstrapper.Bootstrap(
            fixture.ClassifiedSession,
            fixture.CreateSelection(
                sourcePath: "src/App/Widget.Part1.cs",
                limits: limits));

        Assert.Equal(DocumentationScribeContextBootstrapStatus.TimedOut, result.Status);
        Assert.Equal("context.timeout.operation", result.Failure?.Code);
        Assert.DoesNotContain(DocumentationScribeContextBootstrapStage.Open, observed);
    }

    [Fact]
    public void GeneratedTargetWithoutRepositoryBackedScopeDoesNotUseAProjectFallback()
    {
        using var fixture = ContextFixture.CreateGeneratedTarget();
        var observed = new List<DocumentationScribeContextBootstrapStage>();
        var bootstrapper = new DocumentationScribeContextBootstrapper(observed.Add);
        var selection = fixture.CreateGeneratedSelection();

        Assert.IsType<GeneratedOutputEvidenceLocator>(selection.SourceLocator);

        var result = bootstrapper.Bootstrap(fixture.ClassifiedSession, selection);

        Assert.Equal(DocumentationScribeContextBootstrapStatus.Unavailable, result.Status);
        Assert.Equal("context.scope.not-unique", result.Failure?.Code);
        Assert.DoesNotContain(DocumentationScribeContextBootstrapStage.Open, observed);
        Assert.DoesNotContain(DocumentationScribeContextBootstrapStage.Read, observed);
    }

    [Fact]
    public void RepositoryRootContainmentAlsoWorksForAFileSystemRoot()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        var repositoryPath = Path.GetRelativePath(root, Path.Join(Path.GetTempPath(), "context-probe"))
            .Replace(Path.DirectorySeparatorChar, '/');
        var method = typeof(DocumentationScribeContextBootstrapper).GetMethod(
            "FullPath",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        var result = Assert.IsType<string>(method!.Invoke(null, [root, repositoryPath]));

        Assert.Equal(
            Path.GetFullPath(Path.Join(root, repositoryPath.Replace('/', Path.DirectorySeparatorChar))),
            result);
    }

    [Fact]
    public void MultiProjectSessionPublishesOnlyTheExactlyCorrelatedProjectContext()
    {
        using var fixture = ContextFixture.CreateWithDependency(BasicSource());
        fixture.PopulateBasicInstructions();

        var result = fixture.Bootstrap("custom-agent.md");

        Assert.Equal(DocumentationScribeContextBootstrapStatus.Succeeded, result.Status);
        var context = Assert.IsType<DocumentationScribeLoadedContext>(result.Context);
        var project = Assert.Single(context.Facts.Projects);
        Assert.Equal(ProjectIdentity, project.ProjectIdentity);
        Assert.Equal(CompilationContext, project.CompilationContextRef);
        Assert.Equal(DocumentationScribeContextProjectRole.AuditRoot, project.Role);
    }

    [Fact]
    public void InvalidInstructionEncodingFailsWithoutPublishingAuthorizedContent()
    {
        using var fixture = ContextFixture.Create(BasicSource());
        fixture.WriteBytes("custom-agent.md", [0xff, 0xfe, 0xfd]);

        var result = fixture.Bootstrap("custom-agent.md");

        Assert.Equal(DocumentationScribeContextBootstrapStatus.Failed, result.Status);
        Assert.Equal(DocumentationScribeContextFailureCategory.InvalidEncoding, result.Failure?.Category);
        Assert.Equal("context.invalid-encoding", result.Failure?.Code);
        Assert.Null(result.Context);
        Assert.DoesNotContain("custom-agent", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ChangedSourceBytesAreRejectedAsStale()
    {
        using var fixture = ContextFixture.Create(BasicSource());
        fixture.WriteText("AGENTS.md", "root instruction\n");
        fixture.WriteText(
            "src/App/Widget.cs",
            "namespace Fixture; public class Widget { public void Changed() { } }\n");

        var result = fixture.Bootstrap();

        Assert.Equal(DocumentationScribeContextBootstrapStatus.Failed, result.Status);
        Assert.Equal(DocumentationScribeContextFailureCategory.Stale, result.Failure?.Category);
        Assert.Contains(
            result.Failure?.Code,
            new[] { "context.stale.source-text", "context.stale.source-commitment" });
        Assert.Null(result.Context);
    }

    [Theory]
    [InlineData((int)DocumentationScribeContextBootstrapStage.Normalize)]
    [InlineData((int)DocumentationScribeContextBootstrapStage.Publish)]
    public void SameLengthInstructionMutationBeforePublicationIsRejected(int stageValue)
    {
        var stage = (DocumentationScribeContextBootstrapStage)stageValue;
        using var fixture = ContextFixture.Create(BasicSource());
        fixture.WriteText("AGENTS.md", "root-instruction-a\n");
        var bootstrapper = new DocumentationScribeContextBootstrapper(current =>
        {
            if (current == stage)
            {
                fixture.WriteText("AGENTS.md", "root-instruction-b\n");
            }
        });

        var result = fixture.Bootstrap(bootstrapper: bootstrapper);

        Assert.Equal(DocumentationScribeContextBootstrapStatus.Failed, result.Status);
        Assert.Equal(DocumentationScribeContextFailureCategory.Stale, result.Failure?.Category);
        Assert.Equal("context.stale.publication", result.Failure?.Code);
        Assert.Null(result.Context);
    }

    [Fact]
    public void ParentLinkSubstitutionBeforePublicationIsRejectedWhenSupported()
    {
        using var fixture = ContextFixture.Create(BasicSource());
        fixture.WriteText("AGENTS.md", "root instruction\n");
        var parent = Path.GetDirectoryName(fixture.FullPath("src/App/Widget.cs"))!;
        var backup = parent + ".real";
        var substituted = false;
        var bootstrapper = new DocumentationScribeContextBootstrapper(stage =>
        {
            if (stage != DocumentationScribeContextBootstrapStage.Publish)
            {
                return;
            }

            try
            {
                Directory.Move(parent, backup);
                Directory.CreateSymbolicLink(parent, backup);
                substituted = true;
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException
                or PlatformNotSupportedException
                or IOException)
            {
                if (!Directory.Exists(parent) && Directory.Exists(backup))
                {
                    Directory.Move(backup, parent);
                }
            }
        });

        var result = fixture.Bootstrap(bootstrapper: bootstrapper);
        if (!substituted)
        {
            return;
        }

        Directory.Delete(parent);
        Directory.Move(backup, parent);

        Assert.Equal(DocumentationScribeContextBootstrapStatus.Failed, result.Status);
        Assert.Null(result.Context);
    }

    [Fact]
    public void SessionDisposalAtFinalPublicationCheckpointPublishesNoCapability()
    {
        using var fixture = ContextFixture.Create(BasicSource());
        fixture.WriteText("AGENTS.md", "root instruction\n");
        var bootstrapper = new DocumentationScribeContextBootstrapper(stage =>
        {
            if (stage == DocumentationScribeContextBootstrapStage.Publish)
            {
                fixture.DisposeSession();
            }
        });

        var result = fixture.Bootstrap(bootstrapper: bootstrapper);

        Assert.Equal(DocumentationScribeContextBootstrapStatus.Failed, result.Status);
        Assert.Equal("context.stale.publication", result.Failure?.Code);
        Assert.Null(result.Context);
    }

    [Fact]
    public void InstructionFileBudgetExhaustionIsTerminalAndPublishesNoContext()
    {
        using var fixture = ContextFixture.Create(BasicSource());
        fixture.PopulateBasicInstructions();
        var limits = DocumentationScribeContextValidation.CreateLimits(
            maximumInstructionFiles: 1,
            maximumInstructionDepth: 16,
            maximumInstructionFileUtf8Bytes: 1024,
            maximumDeclarationReferences: 64,
            maximumDeclarationFiles: 16,
            maximumInspectedSourceUtf8Bytes: 4096,
            maximumSourceFileUtf8Bytes: 4096,
            maximumIncludedSourceUtf8Bytes: 1024,
            maximumTotalContextUtf8Bytes: 4096,
            maximumElapsedMilliseconds: 30_000);

        var result = fixture.Bootstrap(
            "custom-agent.md",
            limits: limits);

        Assert.Equal(DocumentationScribeContextBootstrapStatus.BudgetExhausted, result.Status);
        Assert.Equal("context.budget.instruction-files", result.Failure?.Code);
        Assert.Null(result.Context);
    }

    [Fact]
    public void IncludedSourceBudgetTruncatesOnAUnicodeScalarBoundary()
    {
        using var fixture = ContextFixture.Create(new SourceInput(
            "src/App/Widget.cs",
            "// prefix-marker-that-must-not-replace-the-target-window-😀-alpha-omega\n"
                + "namespace Fixture; public class Widget { }\n"));
        var limits = DocumentationScribeContextValidation.CreateLimits(
            maximumInstructionFiles: 4,
            maximumInstructionDepth: 8,
            maximumInstructionFileUtf8Bytes: 1024,
            maximumDeclarationReferences: 64,
            maximumDeclarationFiles: 16,
            maximumInspectedSourceUtf8Bytes: 4096,
            maximumSourceFileUtf8Bytes: 4096,
            maximumIncludedSourceUtf8Bytes: 64,
            maximumTotalContextUtf8Bytes: 4096,
            maximumElapsedMilliseconds: 30_000);

        var result = fixture.Bootstrap(limits: limits);

        Assert.Equal(DocumentationScribeContextBootstrapStatus.Incomplete, result.Status);
        var context = Assert.IsType<DocumentationScribeLoadedContext>(result.Context);
        var evidence = Assert.Single(context.Facts.Evidence);
        Assert.True(evidence.Commitment.IsTruncated);
        Assert.True(evidence.Commitment.IncludedUtf8ByteCount <= 64);
        Assert.Equal(evidence.Range, evidence.IncludedRange);
        Assert.Contains("class Widget", evidence.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("prefix-marker", evidence.Content, StringComparison.Ordinal);
        Assert.Contains(
            context.Facts.Omissions,
            omission => omission.Role == DocumentationScribeContextRole.SourceDeclaration
                && omission.Reason == DocumentationScribeContextOmissionReason.ByteLimit);
        _ = new UTF8Encoding(false, true).GetBytes(evidence.Content);
    }

    [Theory]
    [InlineData("file-bytes", "context.budget.file-bytes")]
    [InlineData("depth", "context.budget.instruction-depth")]
    public void FileByteAndDepthBudgetsFailWithStableNoContentResults(
        string boundary,
        string expectedCode)
    {
        using var fixture = ContextFixture.Create(BasicSource());
        fixture.PopulateBasicInstructions();
        var limits = DocumentationScribeContextValidation.CreateLimits(
            maximumInstructionFiles: 8,
            maximumInstructionDepth: boundary == "depth" ? 1 : 8,
            maximumInstructionFileUtf8Bytes: boundary == "file-bytes" ? 8 : 1024,
            maximumDeclarationReferences: 64,
            maximumDeclarationFiles: 16,
            maximumInspectedSourceUtf8Bytes: 4096,
            maximumSourceFileUtf8Bytes: 4096,
            maximumIncludedSourceUtf8Bytes: 1024,
            maximumTotalContextUtf8Bytes: 4096,
            maximumElapsedMilliseconds: 30_000);

        var result = fixture.Bootstrap(
            "custom-agent.md",
            limits: limits);

        Assert.Equal(DocumentationScribeContextBootstrapStatus.BudgetExhausted, result.Status);
        Assert.Equal(expectedCode, result.Failure?.Code);
        Assert.Null(result.Context);
    }

    [Fact]
    public void Utf8BomIsHashedAndCountedButRemovedFromAuthorizedText()
    {
        using var fixture = ContextFixture.Create(BasicSource());
        var original = File.ReadAllBytes(fixture.FullPath("src/App/Widget.cs"));
        fixture.WriteBytes(
            "src/App/Widget.cs",
            new byte[] { 0xef, 0xbb, 0xbf }.Concat(original).ToArray());

        var result = fixture.Bootstrap();

        Assert.Equal(DocumentationScribeContextBootstrapStatus.Incomplete, result.Status);
        var evidence = Assert.Single(
            Assert.IsType<DocumentationScribeLoadedContext>(result.Context).Facts.Evidence);
        Assert.True(evidence.Commitment.HasUtf8Bom);
        Assert.True(evidence.Commitment.IncludedHasUtf8Bom);
        Assert.Equal(original.Length + 3, evidence.Commitment.OriginalUtf8ByteCount);
        Assert.Equal(original.Length + 3, evidence.Commitment.IncludedUtf8ByteCount);
        Assert.False(evidence.Commitment.IsTruncated);
        Assert.False(evidence.Content.StartsWith('\ufeff'));
    }

    [Fact]
    public void OperationTimeoutIsDistinctFromCallerCancellation()
    {
        using var fixture = ContextFixture.Create(BasicSource());
        var ticks = new Queue<long>([0, 2]);
        var bootstrapper = new DocumentationScribeContextBootstrapper(
            null,
            clock: () => ticks.Count == 0 ? 2 : ticks.Dequeue());
        var limits = DocumentationScribeContextValidation.CreateLimits(
            maximumInstructionFiles: 4,
            maximumInstructionDepth: 8,
            maximumInstructionFileUtf8Bytes: 1024,
            maximumDeclarationReferences: 64,
            maximumDeclarationFiles: 16,
            maximumInspectedSourceUtf8Bytes: 4096,
            maximumSourceFileUtf8Bytes: 4096,
            maximumIncludedSourceUtf8Bytes: 1024,
            maximumTotalContextUtf8Bytes: 4096,
            maximumElapsedMilliseconds: 1);

        var result = bootstrapper.Bootstrap(
            fixture.ClassifiedSession,
            fixture.CreateSelection(limits: limits));

        Assert.Equal(DocumentationScribeContextBootstrapStatus.TimedOut, result.Status);
        Assert.Equal("context.timeout.operation", result.Failure?.Code);
        Assert.Null(result.Context);
    }

    [Fact]
    public void SymbolicLinkEntrypointIsRejectedWhenThePlatformSupportsLinks()
    {
        using var fixture = ContextFixture.Create(BasicSource());
        fixture.WriteText("actual-agent.md", "linked instruction\n");
        var link = fixture.FullPath("linked-agent.md");
        try
        {
            File.CreateSymbolicLink(link, fixture.FullPath("actual-agent.md"));
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or PlatformNotSupportedException
            or IOException)
        {
            return;
        }

        var result = fixture.Bootstrap("linked-agent.md");

        Assert.Equal(DocumentationScribeContextBootstrapStatus.Failed, result.Status);
        Assert.Equal(
            DocumentationScribeContextFailureCategory.UnsafeRepositoryObject,
            result.Failure?.Category);
        Assert.Null(result.Context);
    }

    [Fact]
    public void HardLinkedEntrypointIsRejectedWhenThePlatformSupportsLinks()
    {
        using var fixture = ContextFixture.Create(BasicSource());
        fixture.WriteText("actual-agent.md", "linked instruction\n");
        try
        {
            CreateHardLinkForTest(
                fixture.FullPath("actual-agent.md"),
                fixture.FullPath("linked-agent.md"));
        }
        catch (Exception exception) when (exception is Win32Exception
            or PlatformNotSupportedException
            or IOException)
        {
            return;
        }

        var result = fixture.Bootstrap("linked-agent.md");

        Assert.Equal(DocumentationScribeContextBootstrapStatus.Failed, result.Status);
        Assert.Equal(
            DocumentationScribeContextFailureCategory.UnsafeRepositoryObject,
            result.Failure?.Category);
        Assert.Equal("context.unsafe.physical-identity", result.Failure?.Code);
        Assert.Null(result.Context);
    }

    [Fact]
    public void UnknownFaultsCollapseToOneContentFreeInternalFailure()
    {
        const string secret = "credential-marker-never-publish";
        using var fixture = ContextFixture.Create(BasicSource());
        fixture.WriteText("AGENTS.md", secret);
        var bootstrapper = new DocumentationScribeContextBootstrapper(stage =>
        {
            if (stage == DocumentationScribeContextBootstrapStage.Normalize)
            {
                throw new InvalidOperationException(
                    secret + "|" + fixture.Root + "|" + Environment.OSVersion);
            }
        });

        var result = fixture.Bootstrap(bootstrapper: bootstrapper);
        var serialized = JsonSerializer.Serialize(result);

        Assert.Equal(DocumentationScribeContextBootstrapStatus.Failed, result.Status);
        Assert.Equal(DocumentationScribeContextFailureCategory.Internal, result.Failure?.Category);
        Assert.Equal("context.internal-error", result.Failure?.Code);
        Assert.Null(result.Context);
        Assert.DoesNotContain(secret, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Root, serialized, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData((int)DocumentationScribeContextBootstrapStage.Correlation)]
    [InlineData((int)DocumentationScribeContextBootstrapStage.ScopeResolution)]
    [InlineData((int)DocumentationScribeContextBootstrapStage.Open)]
    [InlineData((int)DocumentationScribeContextBootstrapStage.Read)]
    [InlineData((int)DocumentationScribeContextBootstrapStage.Decode)]
    [InlineData((int)DocumentationScribeContextBootstrapStage.Normalize)]
    [InlineData((int)DocumentationScribeContextBootstrapStage.Cursor)]
    [InlineData((int)DocumentationScribeContextBootstrapStage.Publish)]
    public void CancellationAtEveryBootstrapStageIsTerminalAndPublishesNoPartialContext(
        int cancelAtValue)
    {
        var cancelAt = (DocumentationScribeContextBootstrapStage)cancelAtValue;
        using var fixture = ContextFixture.Create(BasicSource());
        fixture.PopulateBasicInstructions();
        using var cancellation = new CancellationTokenSource();
        var bootstrapper = new DocumentationScribeContextBootstrapper(stage =>
        {
            if (stage == cancelAt)
            {
                cancellation.Cancel();
            }
        });

        var result = bootstrapper.Bootstrap(
            fixture.ClassifiedSession,
            fixture.CreateSelection(configuredAgentEntrypoint: "custom-agent.md"),
            cancellation.Token);

        Assert.Equal(DocumentationScribeContextBootstrapStatus.Cancelled, result.Status);
        Assert.Null(result.Context);
        Assert.Null(result.Failure);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void CursorBindsQueryAndSessionCapabilityAndRejectsTampering()
    {
        using var firstFixture = ContextFixture.Create(BasicSource());
        firstFixture.WriteText("AGENTS.md", "root instruction\n");
        var first = Assert.IsType<DocumentationScribeLoadedContext>(
            firstFixture.Bootstrap(
                bootstrapper: new DocumentationScribeContextBootstrapper(
                    null,
                    randomBytes: length => Enumerable.Repeat((byte)0x11, length).ToArray()))
                .Context);
        var scope = CreateCursorScope(first.Facts, "tool.repository.search", "request-a");
        var issued = first.IssueCursor(scope, null, 20, hasMore: true);
        Assert.True(issued.HasValue);
        var cursor = issued.Value;

        Assert.True(first.TryValidateCursor(cursor, scope, out var next));
        Assert.Equal(20, next);
        var secondPage = first.IssueCursor(scope, cursor, 20, hasMore: true);
        Assert.True(secondPage.HasValue);
        Assert.True(first.TryValidateCursor(secondPage.Value, scope, out var secondNext));
        Assert.Equal(40, secondNext);
        Assert.Null(first.IssueCursor(scope, secondPage, 7, hasMore: false));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            first.IssueCursor(scope, null, 19, hasMore: true));
        Assert.False(first.TryValidateCursor(default, scope, out _));
        var substitutedCommitments = DocumentationScribeContextValidation.CreateCursorScope(
            scope.ToolKindId,
            scope.NormalizedRequestSha256,
            scope.RepositoryContextRef,
            scope.SymbolRef,
            scope.OrderingId,
            scope.PageSize,
            Sha256(Encoding.UTF8.GetBytes("substituted-commitments")));
        Assert.False(first.TryValidateCursor(cursor, substitutedCommitments, out _));
        var otherScope = CreateCursorScope(first.Facts, "tool.repository.read", "request-a");
        Assert.False(first.TryValidateCursor(cursor, otherScope, out _));
        var tamperedValue = cursor.Value[..^1]
            + (cursor.Value[^1] == 'A' ? "B" : "A");
        Assert.True(DocumentationScribeContextCursor.TryParse(tamperedValue, out var tampered));
        Assert.False(first.TryValidateCursor(tampered, scope, out _));
        var parallelPositions = new int[64];
        Parallel.For(0, parallelPositions.Length, index =>
        {
            Assert.True(first.TryValidateCursor(cursor, scope, out parallelPositions[index]));
        });
        Assert.All(parallelPositions, position => Assert.Equal(20, position));

        using var secondFixture = ContextFixture.Create(BasicSource());
        secondFixture.WriteText("AGENTS.md", "root instruction\n");
        var second = Assert.IsType<DocumentationScribeLoadedContext>(
            secondFixture.Bootstrap(
                bootstrapper: new DocumentationScribeContextBootstrapper(
                    null,
                    randomBytes: length => Enumerable.Repeat((byte)0x22, length).ToArray()))
                .Context);
        var equivalentScope = CreateCursorScope(
            second.Facts,
            "tool.repository.search",
            "request-a");
        Assert.False(second.TryValidateCursor(cursor, equivalentScope, out _));
    }

    [Fact]
    public void CursorEncodingUsesABoundedSymbolDigestForMaximumLengthDocumentationIds()
    {
        var documentationId = "T:" + string.Concat(Enumerable.Repeat("😀", 1_022));
        var scope = DocumentationScribeContextValidation.CreateCursorScope(
            "tool.repository.search",
            Sha256(Encoding.UTF8.GetBytes("request")),
            ParseRepositoryContext('7'),
            new SymbolRef("fixture.net10.0", documentationId),
            "order.path",
            20,
            Sha256(Encoding.UTF8.GetBytes("commitments")));
        var authority = new DocumentationScribeContextCursorAuthority(
            Enumerable.Repeat((byte)0x44, 32).ToArray());

        var cursor = authority.Issue(scope, 20);

        Assert.InRange(cursor.Value.Length, 32, 4096);
        Assert.True(authority.TryValidate(cursor, scope, out var next));
        Assert.Equal(20, next);
        Assert.False(authority.TryValidate(default, scope, out _));
    }

    [Fact]
    public void CursorOperationsFailClosedAfterAcceptedBytesDrift()
    {
        using var fixture = ContextFixture.Create(BasicSource());
        fixture.WriteText("AGENTS.md", "root-instruction-a\n");
        var loaded = Assert.IsType<DocumentationScribeLoadedContext>(fixture.Bootstrap().Context);
        var scope = CreateCursorScope(loaded.Facts, "tool.repository.search", "request-a");
        fixture.WriteText("AGENTS.md", "root-instruction-b\n");

        Assert.Throws<InvalidOperationException>(() =>
            loaded.IssueCursor(scope, null, 20, hasMore: true));
        Assert.False(loaded.TryValidateCursor(default, scope, out _));
        Assert.False(loaded.IsCurrent);
    }

    [Fact]
    public void GeneralFreshnessGateProtectsNonPagedDownstreamOperations()
    {
        using var fixture = ContextFixture.Create(BasicSource());
        fixture.WriteText("AGENTS.md", "root-instruction-a\n");
        var loaded = Assert.IsType<DocumentationScribeLoadedContext>(fixture.Bootstrap().Context);

        Assert.True(loaded.VerifyFreshness());

        fixture.WriteText("AGENTS.md", "root-instruction-b\n");

        Assert.False(loaded.VerifyFreshness());
        Assert.False(loaded.IsCurrent);
    }

    [Fact]
    public async Task ConcurrentFreshnessVerificationCannotSucceedAfterDetectedDrift()
    {
        using var fixture = ContextFixture.Create(BasicSource());
        fixture.WriteText("AGENTS.md", "root-instruction-a\n");
        using var enteredRead = new ManualResetEventSlim();
        using var releaseRead = new ManualResetEventSlim();
        var armed = 0;
        var checkpoints = 0;
        var bootstrapper = new DocumentationScribeContextBootstrapper(
            null,
            freshnessCheckpoint: () =>
            {
                if (Volatile.Read(ref armed) == 1
                    && Interlocked.Increment(ref checkpoints) == 3)
                {
                    enteredRead.Set();
                    if (!releaseRead.Wait(TimeSpan.FromSeconds(10)))
                    {
                        throw new TimeoutException("Freshness verification was not released.");
                    }
                }
            });
        var loaded = Assert.IsType<DocumentationScribeLoadedContext>(
            fixture.Bootstrap(bootstrapper: bootstrapper).Context);
        var scope = CreateCursorScope(loaded.Facts, "tool.repository.search", "request-a");
        Volatile.Write(ref armed, 1);

        var first = Task.Run(() => loaded.VerifyFreshness());
        Assert.True(enteredRead.Wait(TimeSpan.FromSeconds(10)));
        fixture.WriteText("AGENTS.md", "root-instruction-b\n");
        var second = Task.Run(() => loaded.VerifyFreshness());
        releaseRead.Set();

        Assert.False(await first);
        Assert.False(await second);
        Assert.False(loaded.IsCurrent);
        Assert.Throws<InvalidOperationException>(() =>
            loaded.IssueCursor(scope, null, 20, hasMore: true));
    }

    [Fact]
    public void CancellationDuringFreshnessReadDoesNotStaleTheCapability()
    {
        using var fixture = ContextFixture.Create(BasicSource());
        fixture.WriteText("AGENTS.md", "root instruction\n");
        using var cancellation = new CancellationTokenSource();
        var armed = 0;
        var checkpoints = 0;
        var bootstrapper = new DocumentationScribeContextBootstrapper(
            null,
            freshnessCheckpoint: () =>
            {
                if (Volatile.Read(ref armed) == 1
                    && Interlocked.Increment(ref checkpoints) == 3)
                {
                    cancellation.Cancel();
                }
            });
        var loaded = Assert.IsType<DocumentationScribeLoadedContext>(
            fixture.Bootstrap(bootstrapper: bootstrapper).Context);
        var scope = CreateCursorScope(loaded.Facts, "tool.repository.search", "request-a");
        Volatile.Write(ref armed, 1);

        Assert.Throws<OperationCanceledException>(() =>
            loaded.IssueCursor(scope, null, 20, hasMore: true, cancellation.Token));

        Volatile.Write(ref armed, 0);
        Assert.True(loaded.IsCurrent);
        Assert.True(loaded.VerifyFreshness());
        Assert.NotNull(loaded.IssueCursor(scope, null, 20, hasMore: true));
    }

    [Fact]
    public void CursorPublicationHonorsCancellationBeforeHmacAndNoMoreResult()
    {
        using var fixture = ContextFixture.Create(BasicSource());
        fixture.WriteText("AGENTS.md", "root instruction\n");
        CancellationTokenSource? activeCancellation = null;
        var armed = 0;
        var bootstrapper = new DocumentationScribeContextBootstrapper(
            null,
            cursorPublicationObserver: () =>
            {
                if (Volatile.Read(ref armed) == 1)
                {
                    activeCancellation!.Cancel();
                }
            });
        var loaded = Assert.IsType<DocumentationScribeLoadedContext>(
            fixture.Bootstrap(bootstrapper: bootstrapper).Context);
        var scope = CreateCursorScope(loaded.Facts, "tool.repository.search", "request-a");

        using (var beforeHmac = new CancellationTokenSource())
        {
            activeCancellation = beforeHmac;
            Volatile.Write(ref armed, 1);
            Assert.Throws<OperationCanceledException>(() =>
                loaded.IssueCursor(scope, null, 20, hasMore: true, beforeHmac.Token));
        }

        Volatile.Write(ref armed, 0);
        Assert.True(loaded.IsCurrent);
        var issued = Assert.IsType<DocumentationScribeContextCursor>(
            loaded.IssueCursor(scope, null, 20, hasMore: true));
        using (var validationCancellation = new CancellationTokenSource())
        {
            validationCancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() =>
                loaded.TryValidateCursor(
                    issued,
                    scope,
                    out _,
                    validationCancellation.Token));
        }

        using var finalPage = new CancellationTokenSource();
        activeCancellation = finalPage;
        Volatile.Write(ref armed, 1);
        Assert.Throws<OperationCanceledException>(() =>
            loaded.IssueCursor(scope, null, 7, hasMore: false, finalPage.Token));
        Volatile.Write(ref armed, 0);
        Assert.True(loaded.VerifyFreshness());
    }

    [Fact]
    public void CancellationConcurrentWithDriftDoesNotMaskLaterStaleDetection()
    {
        using var fixture = ContextFixture.Create(BasicSource());
        fixture.WriteText("AGENTS.md", "root-instruction-a\n");
        using var cancellation = new CancellationTokenSource();
        var armed = 0;
        var checkpoints = 0;
        var bootstrapper = new DocumentationScribeContextBootstrapper(
            null,
            freshnessCheckpoint: () =>
            {
                if (Volatile.Read(ref armed) == 1
                    && Interlocked.Increment(ref checkpoints) == 3)
                {
                    fixture.WriteText("AGENTS.md", "root-instruction-b\n");
                    cancellation.Cancel();
                }
            });
        var loaded = Assert.IsType<DocumentationScribeLoadedContext>(
            fixture.Bootstrap(bootstrapper: bootstrapper).Context);
        var scope = CreateCursorScope(loaded.Facts, "tool.repository.search", "request-a");
        Volatile.Write(ref armed, 1);

        Assert.Throws<OperationCanceledException>(() =>
            loaded.IssueCursor(scope, null, 20, hasMore: true, cancellation.Token));

        Volatile.Write(ref armed, 0);
        Assert.True(loaded.IsCurrent);
        Assert.False(loaded.VerifyFreshness());
        Assert.False(loaded.IsCurrent);
        Assert.Throws<InvalidOperationException>(() =>
            loaded.IssueCursor(scope, null, 20, hasMore: true));
    }

    [Fact]
    public void LoadedContextExportsFactsAndBindingButNoConstructibleReaderOrCursorKey()
    {
        var type = typeof(DocumentationScribeLoadedContext);

        Assert.Empty(type.GetConstructors());
        Assert.Equal(
            ["Facts"],
            type.GetProperties(
                    System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.DeclaredOnly)
                .Select(property => property.Name));
        Assert.DoesNotContain(
            type.GetMembers(
                System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Static),
            member => member.Name.Contains("Key", StringComparison.OrdinalIgnoreCase)
                || member.Name.Contains("Reader", StringComparison.OrdinalIgnoreCase)
                || member.Name.Contains("FileSystem", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FinalRequestBindingRequiresTheCompleteBootstrappedInstructionSetWithoutReads()
    {
        using var fixture = ContextFixture.Create(BasicSource());
        fixture.PopulateBasicInstructions();
        var observed = new List<DocumentationScribeContextBootstrapStage>();
        var bootstrapper = new DocumentationScribeContextBootstrapper(observed.Add);
        var selection = fixture.CreateSelection(configuredAgentEntrypoint: "custom-agent.md");
        var loaded = Assert.IsType<DocumentationScribeLoadedContext>(
            bootstrapper.Bootstrap(fixture.ClassifiedSession, selection).Context);
        var valid = ParseBoundRequest(loaded, selection, loaded.Facts.Instructions);
        var readsBeforeBinding = observed.Count(stage =>
            stage is DocumentationScribeContextBootstrapStage.Open
                or DocumentationScribeContextBootstrapStage.Read);

        var accepted = loaded.ValidateRequestBinding(valid);
        var readsAfterBinding = observed.Count(stage =>
            stage is DocumentationScribeContextBootstrapStage.Open
                or DocumentationScribeContextBootstrapStage.Read);

        Assert.True(accepted.IsValid, accepted.FailureCode);
        Assert.Equal(readsBeforeBinding, readsAfterBinding);
        var withLaterContext = ParseBoundRequest(
            loaded,
            selection,
            loaded.Facts.Instructions,
            includeNonInstructionContext: true);
        Assert.True(loaded.ValidateRequestBinding(withLaterContext).IsValid);
        var missing = ParseBoundRequest(
            loaded,
            selection,
            loaded.Facts.Instructions.RemoveAt(0));
        var rejected = loaded.ValidateRequestBinding(missing);
        Assert.False(rejected.IsValid);
        Assert.Equal("context.binding.instruction-set-mismatch", rejected.FailureCode);

        var extraText = "extra instruction\n";
        var extraBytes = Encoding.UTF8.GetBytes(extraText);
        var extraCommitment = DocumentationScribeContextValidation.CreateSourceCommitment(
            "extra/AGENTS.md",
            Sha256(extraBytes),
            Sha256(extraBytes),
            extraBytes.Length,
            extraBytes.Length,
            false,
            false);
        var extra = DocumentationScribeContextValidation.CreateInstructionFact(
            DocumentationScribeContextRole.ScopedInstruction,
            1,
            extraCommitment,
            extraText);
        var variants = new[]
        {
            loaded.Facts.Instructions.Add(extra),
            loaded.Facts.Instructions.Add(loaded.Facts.Instructions[0]),
            loaded.Facts.Instructions.SetItem(0, extra),
        };
        Assert.All(variants, variant =>
        {
            var invalid = loaded.ValidateRequestBinding(
                ParseBoundRequest(loaded, selection, variant));
            Assert.False(invalid.IsValid);
            Assert.Equal("context.binding.instruction-set-mismatch", invalid.FailureCode);
        });
    }

    [Fact]
    public void BomInstructionCountsRemainValidForTheFinalParsedRequest()
    {
        using var fixture = ContextFixture.Create(BasicSource());
        var instructionText = "root instruction\n";
        fixture.WriteBytes(
            "AGENTS.md",
            new byte[] { 0xef, 0xbb, 0xbf }
                .Concat(Encoding.UTF8.GetBytes(instructionText))
                .ToArray());
        var selection = fixture.CreateSelection();
        var observed = new List<DocumentationScribeContextBootstrapStage>();
        var result = new DocumentationScribeContextBootstrapper(observed.Add)
            .Bootstrap(fixture.ClassifiedSession, selection);
        Assert.True(
            result.Context is not null,
            result.Status + "|" + result.Failure?.Category + "|" + result.Failure?.Code
                + "|" + string.Join(',', observed));
        var loaded = Assert.IsType<DocumentationScribeLoadedContext>(result.Context);
        var instruction = Assert.Single(loaded.Facts.Instructions);

        Assert.True(instruction.Commitment.IncludedHasUtf8Bom);
        Assert.False(instruction.Commitment.IsTruncated);
        var request = ParseBoundRequest(
            loaded,
            selection,
            loaded.Facts.Instructions);
        Assert.True(loaded.ValidateRequestBinding(request).IsValid);
    }

    [Fact]
    public async Task FactsAreDeterministicAcrossFreshProcesses()
    {
        var childOutput = Environment.GetEnvironmentVariable(ChildOutputVariable);
        if (!string.IsNullOrWhiteSpace(childOutput))
        {
            using var childFixture = ContextFixture.Create(BasicSource());
            childFixture.PopulateBasicInstructions();
            var childContext = Assert.IsType<DocumentationScribeLoadedContext>(
                childFixture.Bootstrap("custom-agent.md").Context);
            var projection = new
            {
                childContext.Facts.ContentIdentity,
                Instructions = childContext.Facts.Instructions.Select(item => new
                {
                    item.InstructionId,
                    item.Role,
                    item.Depth,
                    item.Commitment,
                }),
                Projects = childContext.Facts.Projects,
                Evidence = childContext.Facts.Evidence.Select(item => new
                {
                    item.EvidenceId,
                    item.Authority,
                    item.Role,
                    item.SubjectId,
                    item.KindId,
                    item.Range,
                    item.IncludedRange,
                    item.Commitment,
                }),
                Routes = childContext.Facts.Routes,
                Omissions = childContext.Facts.Omissions,
                Diagnostics = childContext.Facts.Diagnostics,
            };
            await File.WriteAllTextAsync(
                childOutput,
                JsonSerializer.Serialize(projection),
                new UTF8Encoding(false));
            return;
        }

        var firstOutput = Path.Join(
            Path.GetTempPath(),
            "contract-scribe-context-probe-" + Guid.NewGuid().ToString("N") + ".json");
        var secondOutput = Path.Join(
            Path.GetTempPath(),
            "contract-scribe-context-probe-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var repositoryRoot = FindRepositoryRoot();
            await RunFreshProcessProbeAsync(
                firstOutput,
                repositoryRoot,
                "en-US",
                "UTC");
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

    private static DocumentationScribeRequest ParseBoundRequest(
        DocumentationScribeLoadedContext loaded,
        DocumentationScribeContextBootstrapSelection selection,
        ImmutableArray<DocumentationScribeInstructionContextFact> instructions,
        bool includeNonInstructionContext = false)
    {
        var fixturePath = Path.Join(
            FindRepositoryRoot(),
            "tests",
            "fixtures",
            "documentation-scribe",
            "v1",
            "valid",
            "request.json");
        var root = JsonNode.Parse(File.ReadAllBytes(fixturePath))!.AsObject();
        var context = root["context"]!.AsObject();
        context["repositoryContextRef"] = loaded.Facts.RepositoryContextRef.ToString();
        context["inputIdentity"] = loaded.Facts.InputIdentity;
        context["targetProfile"] = ClassificationVocabulary.GetId(loaded.Facts.TargetProfile);
        var target = root["target"]!.AsObject();
        var symbol = target["symbolRef"]!.AsObject();
        symbol["compilationContextRef"] = loaded.Facts.SymbolRef.CompilationContextRef;
        symbol["documentationCommentId"] = loaded.Facts.SymbolRef.DocumentationCommentId;
        var sourceCommitment = target["sourceCommitment"]!.AsObject();
        sourceCommitment["contentSha256"] = selection.SourceSha256;
        var repository = sourceCommitment["locator"]!["repository"]!.AsObject();
        var locator = Assert.IsType<RepositoryEvidenceLocator>(selection.SourceLocator);
        repository["path"] = locator.Path;
        repository["span"] = new JsonObject
        {
            ["start"] = locator.Span!.Value.Start,
            ["end"] = locator.Span.Value.End,
        };
        target["applicableComponents"] = new JsonArray();
        root["styleProfile"]!["componentPolicies"] = new JsonArray();
        var contextReferences = new JsonArray();
        foreach (var pair in instructions.Select((instruction, index) => (instruction, index)))
        {
            var commitment = pair.instruction.Commitment;
            contextReferences.Add(new JsonObject
            {
                ["contextReferenceId"] = $"context.x1.{pair.index + 1:D4}",
                ["kind"] = "context.project-instruction",
                ["repositoryContextRef"] = loaded.Facts.RepositoryContextRef.ToString(),
                ["path"] = commitment.RepositoryPath,
                ["contentSha256"] = commitment.ContentSha256,
                ["originalUtf8ByteCount"] = commitment.OriginalUtf8ByteCount,
                ["includedUtf8ByteCount"] = commitment.IncludedUtf8ByteCount,
                ["isTruncated"] = commitment.IsTruncated,
            });
        }

        if (includeNonInstructionContext)
        {
            contextReferences.Add(new JsonObject
            {
                ["contextReferenceId"] = "context.x2.0001",
                ["kind"] = "context.repository-documentation",
                ["repositoryContextRef"] = loaded.Facts.RepositoryContextRef.ToString(),
                ["path"] = "docs/context.md",
                ["contentSha256"] = Sha256(Encoding.UTF8.GetBytes("later-context")),
                ["originalUtf8ByteCount"] = 13,
                ["includedUtf8ByteCount"] = 13,
                ["isTruncated"] = false,
            });
        }

        root["contextReferences"] = contextReferences;
        root["evidenceReferences"] = new JsonArray();
        var parsed = DocumentationScribeValidation.ParseRequest(
            Encoding.UTF8.GetBytes(root.ToJsonString()));
        Assert.True(parsed.IsValid, parsed.Failure?.Code + "|" + parsed.Failure?.Pointer);
        return Assert.IsType<DocumentationScribeRequest>(parsed.Request);
    }

    private static DocumentationScribeContextCursorScope CreateCursorScope(
        DocumentationScribeContextFacts facts,
        string toolKind,
        string request) =>
        DocumentationScribeContextValidation.CreateCursorScope(
            toolKind,
            Sha256(Encoding.UTF8.GetBytes(request)),
            facts.RepositoryContextRef,
            facts.SymbolRef,
            "order.path-ordinal",
            20,
            DocumentationScribeContextValidation.ComputeCommitmentsSha256(
                facts.Instructions.Select(item => item.Commitment)
                    .Concat(facts.Evidence.Select(item => item.Commitment))));

    private static async Task RunFreshProcessProbeAsync(
        string outputPath,
        string workingDirectory,
        string culture,
        string timeZone)
    {
        var repositoryRoot = FindRepositoryRoot();
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
            "FullyQualifiedName=ContractScribe.Roslyn.IntegrationTests.DocumentationScribeContextIntegrationTests.FactsAreDeterministicAcrossFreshProcesses");
        startInfo.Environment[ChildOutputVariable] = outputPath;
        startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = culture;
        startInfo.Environment["LANG"] = culture + ".UTF-8";
        startInfo.Environment["LC_ALL"] = culture + ".UTF-8";
        startInfo.Environment["TZ"] = timeZone;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The fresh-process context probe did not start.");
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
            throw new TimeoutException("The fresh-process context probe timed out.");
        }

        var output = await standardOutput;
        var error = await standardError;
        Assert.True(
            process.ExitCode == 0,
            $"Fresh-process probe failed with exit code {process.ExitCode}.\nstdout:\n{output}\nstderr:\n{error}");
        Assert.True(File.Exists(outputPath), "Fresh-process probe did not publish its projection.");
    }

    private static SourceInput BasicSource() => new(
        "src/App/Widget.cs",
        File.ReadAllText(Path.Join(BasicFixtureRoot(), "src", "App", "Widget.cs")));

    private static RepositoryContextRef ParseRepositoryContext(char value)
    {
        Assert.True(RepositoryContextRef.TryParse(
            "repoctx-" + new string(value, 32),
            out var result));
        return result;
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static DocumentationScribeContextBootstrapLimits ScopeLimits(
        int maximumDeclarationReferences = 64,
        int maximumDeclarationFiles = 16,
        int maximumInspectedSourceUtf8Bytes = 4096) =>
        DocumentationScribeContextValidation.CreateLimits(
            maximumInstructionFiles: 8,
            maximumInstructionDepth: 8,
            maximumInstructionFileUtf8Bytes: 1024,
            maximumDeclarationReferences: maximumDeclarationReferences,
            maximumDeclarationFiles: maximumDeclarationFiles,
            maximumInspectedSourceUtf8Bytes: maximumInspectedSourceUtf8Bytes,
            maximumSourceFileUtf8Bytes: 4096,
            maximumIncludedSourceUtf8Bytes: 1024,
            maximumTotalContextUtf8Bytes: 4096,
            maximumElapsedMilliseconds: 30_000);

    private static string BasicFixtureRoot() => Path.Join(
        FindRepositoryRoot(),
        "tests",
        "fixtures",
        "documentation-scribe",
        "context",
        "basic");

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "ContractScribe.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Repository root was not found.");
    }

    private static void CreateHardLinkForTest(string existingPath, string linkPath)
    {
        var succeeded = OperatingSystem.IsWindows()
            ? CreateHardLinkW(linkPath, existingPath, IntPtr.Zero)
            : Link(existingPath, linkPath) == 0;
        if (!succeeded)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(string existingPath, string newPath);

    private sealed record SourceInput(
        string RepositoryPath,
        string Text,
        LoadedSourceKind Kind = LoadedSourceKind.Repository,
        GeneratedSourceFact? GeneratedSource = null);

    private sealed class ContextFixture : IDisposable
    {
        private static readonly ImmutableArray<MetadataReference> PlatformReferences =
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator)
                .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
                .ToImmutableArray();

        private readonly LoadedRepositorySession repositorySession;
        private readonly IReadOnlyDictionary<string, SyntaxReference> references;

        private ContextFixture(
            string root,
            LoadedRepositorySession repositorySession,
            ClassifiedRepositorySession classifiedSession,
            SymbolRef symbolRef,
            IReadOnlyDictionary<string, SyntaxReference> references)
        {
            Root = root;
            this.repositorySession = repositorySession;
            ClassifiedSession = classifiedSession;
            SymbolRef = symbolRef;
            this.references = references;
        }

        public string Root { get; }

        public ClassifiedRepositorySession ClassifiedSession { get; }

        public SymbolRef SymbolRef { get; }

        public RepositoryContextRef RepositoryContextRef => repositorySession.RepositoryContextRef;

        public static ContextFixture Create(params SourceInput[] sources) =>
            CreateCore(includeDependencyProject: false, TargetDocumentationId, sources);

        public static ContextFixture CreateWithDependency(params SourceInput[] sources) =>
            CreateCore(includeDependencyProject: true, TargetDocumentationId, sources);

        public static ContextFixture CreateForTarget(
            string targetDocumentationId,
            params SourceInput[] sources) =>
            CreateCore(includeDependencyProject: false, targetDocumentationId, sources);

        public static ContextFixture CreateGeneratedTarget()
        {
            const string generatedText =
                "namespace Fixture; public class GeneratedWidget { public void Run() { } }\n";
            var generated = new GeneratedSourceFact(
                ProjectIdentity,
                CompilationContext,
                "tgp." + new string('a', 64),
                "tgo." + new string('b', 64),
                Sha256(Encoding.UTF8.GetBytes(generatedText)),
                generatedText);
            return CreateCore(
                includeDependencyProject: false,
                "T:Fixture.GeneratedWidget",
                new SourceInput(
                    "src/App/Fallback.cs",
                    "namespace Fixture; public class Fallback { }\n"),
                new SourceInput(
                    "GeneratedWidget.g.cs",
                    generatedText,
                    LoadedSourceKind.ToolGenerated,
                    generated));
        }

        private static ContextFixture CreateCore(
            bool includeDependencyProject,
            string targetDocumentationId,
            params SourceInput[] sources)
        {
            var root = Path.Join(
                Path.GetTempPath(),
                "contract-scribe-context-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllText(
                Path.Join(root, ProjectIdentity),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />\n",
                new UTF8Encoding(false));
            foreach (var source in sources.Where(source =>
                         source.Kind == LoadedSourceKind.Repository))
            {
                var fullPath = FullPath(root, source.RepositoryPath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                File.WriteAllText(fullPath, source.Text, new UTF8Encoding(false));
            }

            var parseOptions = new CSharpParseOptions(
                LanguageVersion.Preview,
                documentationMode: DocumentationMode.Diagnose);
            var syntaxTrees = sources.Select(source => CSharpSyntaxTree.ParseText(
                    source.Text,
                    parseOptions,
                    source.RepositoryPath,
                    Encoding.UTF8))
                .ToArray();
            var compilation = CSharpCompilation.Create(
                "Fixture",
                syntaxTrees,
                PlatformReferences,
                new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    deterministic: true));
            var workspace = new AdhocWorkspace();
            var project = workspace.AddProject("Fixture", LanguageNames.CSharp);
            var bindings = new Dictionary<SyntaxTree, LoadedSourceTree>(
                ReferenceEqualityComparer.Instance);
            foreach (var pair in syntaxTrees.Zip(sources))
            {
                bindings.Add(
                    pair.First,
                    new LoadedSourceTree(
                        pair.Second.Kind,
                        pair.Second.Kind == LoadedSourceKind.Repository
                            ? pair.Second.RepositoryPath
                            : null,
                        pair.Second.Kind == LoadedSourceKind.Repository
                            ? new RepositoryPathResolver().PhysicalIdentity(
                                root,
                                FullPath(root, pair.Second.RepositoryPath))
                            : null,
                        pair.Second.GeneratedSource));
            }
            var loadedProject = new LoadedProject(
                ProjectIdentity,
                "net10.0",
                CompilationContext,
                LoadedProjectRole.AuditRoot,
                [],
                project,
                compilation,
                bindings);
            var repositoryContextRef = ParseRepositoryContext('1');
            var projects = new List<LoadedProject> { loadedProject };
            if (includeDependencyProject)
            {
                const string dependencyPath = "src/Dependency/Other.cs";
                const string dependencyText =
                    "namespace Dependency; public class Other { public void Run() { } }\n";
                var dependencyFullPath = FullPath(root, dependencyPath);
                Directory.CreateDirectory(Path.GetDirectoryName(dependencyFullPath)!);
                File.WriteAllText(
                    dependencyFullPath,
                    dependencyText,
                    new UTF8Encoding(false));
                var dependencyTree = CSharpSyntaxTree.ParseText(
                    dependencyText,
                    parseOptions,
                    dependencyPath,
                    Encoding.UTF8);
                var dependencyCompilation = CSharpCompilation.Create(
                    "Dependency",
                    [dependencyTree],
                    PlatformReferences,
                    new CSharpCompilationOptions(
                        OutputKind.DynamicallyLinkedLibrary,
                        deterministic: true));
                var dependencyProject = workspace.AddProject("Dependency", LanguageNames.CSharp);
                projects.Add(new LoadedProject(
                    "Dependency.csproj",
                    "net10.0",
                    "dependency.net10.0",
                    LoadedProjectRole.DependencyOnly,
                    [],
                    dependencyProject,
                    dependencyCompilation,
                    new Dictionary<SyntaxTree, LoadedSourceTree>(
                        ReferenceEqualityComparer.Instance)
                    {
                        [dependencyTree] = new(
                            LoadedSourceKind.Repository,
                            dependencyPath,
                            new RepositoryPathResolver().PhysicalIdentity(
                                root,
                                dependencyFullPath),
                            null),
                    }));
            }

            var repository = new LoadedRepositorySession(
                repositoryContextRef,
                root,
                ProjectIdentity,
                new ToolchainIdentity("test", "test", "test", "test"),
                projects,
                sources
                    .Select(source => source.GeneratedSource)
                    .Where(fact => fact is not null)
                    .Cast<GeneratedSourceFact>()
                    .ToArray(),
                workspace);
            var classified = new SymbolClassifier().ClassifySession(
                repository,
                TargetProfile.ExternalApi);
            Assert.Equal(ClassificationRunStatus.Success, classified.Classification.Status);
            var target = Assert.Single(
                classified.Classification.ClassificationSet!.Targets,
                candidate => candidate.SymbolRef.DocumentationCommentId == targetDocumentationId
                    && candidate.SupportStatus == SupportStatus.Supported);
            var symbol = Assert.Single(DocumentationCommentId.GetSymbolsForDeclarationId(
                targetDocumentationId,
                compilation));
            var references = symbol.DeclaringSyntaxReferences
                .Where(reference => bindings[reference.SyntaxTree].RepositoryPath is not null)
                .ToDictionary(
                    reference => bindings[reference.SyntaxTree].RepositoryPath!,
                    StringComparer.Ordinal);
            return new ContextFixture(root, repository, classified, target.SymbolRef, references);
        }

        public DocumentationScribeContextBootstrapResult Bootstrap(
            string? configuredAgentEntrypoint = null,
            string? selectionPath = null,
            DocumentationScribeContextBootstrapper? bootstrapper = null,
            DocumentationScribeContextBootstrapLimits? limits = null) =>
            (bootstrapper ?? new DocumentationScribeContextBootstrapper()).Bootstrap(
                ClassifiedSession,
                CreateSelection(
                    configuredAgentEntrypoint: configuredAgentEntrypoint,
                    sourcePath: selectionPath,
                    limits: limits));

        public DocumentationScribeContextBootstrapSelection CreateSelection(
            RepositoryContextRef? repositoryContextRef = null,
            string? inputIdentity = null,
            TargetProfile? targetProfile = null,
            SymbolRef? symbolRef = null,
            string? sourcePath = null,
            string? configuredAgentEntrypoint = null,
            DocumentationScribeContextBootstrapLimits? limits = null)
        {
            sourcePath ??= references.Keys.Order(StringComparer.Ordinal).First();
            var reference = references[sourcePath];
            return DocumentationScribeContextValidation.CreateBootstrapSelection(
                repositoryContextRef ?? repositorySession.RepositoryContextRef,
                inputIdentity ?? repositorySession.InputIdentity,
                targetProfile ?? TargetProfile.ExternalApi,
                symbolRef ?? SymbolRef,
                sourcePath,
                reference.Span.Start,
                reference.Span.End,
                Sha256(File.ReadAllBytes(FullPath(sourcePath))),
                configuredAgentEntrypoint,
                limits);
        }

        public DocumentationScribeContextBootstrapSelection CreateGeneratedSelection()
        {
            var project = Assert.Single(repositorySession.Projects, candidate =>
                string.Equals(
                    candidate.CompilationContextRef,
                    SymbolRef.CompilationContextRef,
                    StringComparison.Ordinal));
            var symbol = Assert.Single(DocumentationCommentId.GetSymbolsForDeclarationId(
                SymbolRef.DocumentationCommentId,
                project.Compilation));
            var reference = Assert.Single(symbol.DeclaringSyntaxReferences);
            var source = project.SourceTrees[reference.SyntaxTree];
            var generated = Assert.IsType<GeneratedSourceFact>(source.GeneratedSource);
            var kind = source.Kind == LoadedSourceKind.SourceGenerator
                ? GeneratedOutputKind.SourceGenerator
                : GeneratedOutputKind.ToolGenerated;
            var locator = EvidenceInput.GeneratedOutputLocator(
                kind,
                generated.ProducerId,
                generated.OutputId,
                generated.SourceSha256,
                reference.Span.Start,
                reference.Span.End);
            return DocumentationScribeContextValidation.CreateBootstrapSelection(
                repositorySession.RepositoryContextRef,
                repositorySession.InputIdentity,
                TargetProfile.ExternalApi,
                SymbolRef,
                locator,
                generated.SourceSha256);
        }

        public void PopulateBasicInstructions()
        {
            WriteText("AGENTS.md", File.ReadAllText(Path.Join(BasicFixtureRoot(), "AGENTS.md")));
            WriteText(
                "custom-agent.md",
                File.ReadAllText(Path.Join(BasicFixtureRoot(), "custom-agent.md")));
            WriteText(
                "src/AGENTS.md",
                File.ReadAllText(Path.Join(BasicFixtureRoot(), "src", "AGENTS.md")));
            WriteText(
                "src/App/AGENTS.md",
                File.ReadAllText(Path.Join(BasicFixtureRoot(), "src", "App", "AGENTS.md")));
        }

        public void DisposeSession() => repositorySession.Dispose();

        public void WriteText(string repositoryPath, string content) =>
            WriteBytes(repositoryPath, new UTF8Encoding(false).GetBytes(content));

        public void WriteBytes(string repositoryPath, byte[] content)
        {
            var fullPath = FullPath(repositoryPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllBytes(fullPath, content);
        }

        public string FullPath(string repositoryPath) => FullPath(Root, repositoryPath);

        public void Dispose()
        {
            repositorySession.Dispose();
            Directory.Delete(Root, recursive: true);
        }

        private static string FullPath(string root, string repositoryPath) => Path.Join(
            root,
            repositoryPath.Replace('/', Path.DirectorySeparatorChar));
    }
}
