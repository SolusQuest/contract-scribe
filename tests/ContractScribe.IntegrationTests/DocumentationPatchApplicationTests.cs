using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContractScribe.Core;
using ContractScribe.Patching;
using ContractScribe.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ContractScribe.IntegrationTests;

[Collection("Integration process lane 1")]
public sealed class DocumentationPatchApplicationTests
{
    [Fact]
    public void InsertBuildsACompleteIsolatedCandidateAndLeavesOriginalUntouched()
    {
        const string source = "namespace N;\n\npublic class C\n{\n    public void M() { }\n}\n";
        using var fixture = ApplicationFixture.Create(
            source,
            DocumentationPatchRepositoryEncoding.Utf8,
            additionalFiles: new Dictionary<string, byte[]>
            {
                ["notes.txt"] = Encoding.UTF8.GetBytes("unchanged\n"),
            });
        var original = File.ReadAllBytes(fixture.SourcePath);

        var result = new CandidatePatchApplicator().Apply(
            fixture.ClassifiedSession,
            fixture.Request());

        Assert.Equal(DocumentationPatchApplicationStatus.Complete, result.Status);
        Assert.Null(result.PrimaryCode);
        var candidate = Assert.IsType<DocumentationPatchCandidateHandle>(result.Candidate);
        var candidateRoot = candidate.RootPath;
        Assert.True(Directory.Exists(candidateRoot));
        Assert.Equal(2, candidate.Files.Length);
        Assert.Equal(
            source.Replace(
                "    public void M() { }",
                "    /// <inheritdoc/>\n    public void M() { }",
                StringComparison.Ordinal),
            Encoding.UTF8.GetString(CandidateBytes(candidate, "Sample.cs")));
        Assert.Equal(
            Encoding.UTF8.GetBytes("unchanged\n"),
            CandidateBytes(candidate, "notes.txt"));
        Assert.Equal(original, File.ReadAllBytes(fixture.SourcePath));
        var originalIdentity = candidate.Baseline.Entries.Single(entry =>
            entry.RepositoryPath == "Sample.cs").PhysicalIdentity;
        var candidateIdentity = candidate.Files.Single(file =>
            file.RepositoryPath == "Sample.cs").Identity;
        Assert.NotEqual(
            (originalIdentity.Volume, originalIdentity.FileId),
            (candidateIdentity.Volume, candidateIdentity.FileId));

        candidate.Dispose();
        Assert.True(candidate.IsInvalidated);
        Assert.False(Directory.Exists(candidateRoot));
        candidate.Dispose();
    }

    [Theory]
    [InlineData(DocumentationPatchRepositoryEncoding.Utf8, "\n")]
    [InlineData(DocumentationPatchRepositoryEncoding.Utf8Bom, "\r\n")]
    [InlineData(DocumentationPatchRepositoryEncoding.Utf16LittleEndianBom, "\n")]
    [InlineData(DocumentationPatchRepositoryEncoding.Utf16BigEndianBom, "\r\n")]
    public void InsertPreservesSupportedEncodingBomNewlineAndTerminalNewline(
        DocumentationPatchRepositoryEncoding encoding,
        string newline)
    {
        var vector = LoadRenderingVector("insert-inheritdoc");
        var source = string.Join(newline, vector.SourceLines) + newline;
        using var fixture = ApplicationFixture.Create(source, encoding);

        var result = new CandidatePatchApplicator().Apply(
            fixture.ClassifiedSession,
            fixture.Request());

        var candidate = Assert.IsType<DocumentationPatchCandidateHandle>(result.Candidate);
        var expectedText = string.Join(newline, vector.CandidateLines) + newline;
        Assert.Equal(
            ApplicationFixture.Encode(expectedText, encoding),
            CandidateBytes(candidate, "Sample.cs"));
        Assert.Equal(
            ApplicationFixture.Encode(source, encoding),
            File.ReadAllBytes(fixture.SourcePath));
        candidate.Dispose();
    }

    [Fact]
    public void ReplaceConsumesOnlyTheAttachedDocumentationRegion()
    {
        const string source = "namespace N;\n\npublic class C\n{\n    // unrelated\n    /// <summary>\n    /// Old.\n    /// </summary>\n    public void M() { }\n}\n";
        using var fixture = ApplicationFixture.Create(
            source,
            DocumentationPatchRepositoryEncoding.Utf8);

        var result = new CandidatePatchApplicator().Apply(
            fixture.ClassifiedSession,
            fixture.Request(DocumentationPatchEditKind.Replace));

        var candidate = Assert.IsType<DocumentationPatchCandidateHandle>(result.Candidate);
        Assert.Equal(
            "namespace N;\n\npublic class C\n{\n    // unrelated\n"
            + "    /// <inheritdoc/>\n"
            + "    public void M() { }\n}\n",
            Encoding.UTF8.GetString(CandidateBytes(candidate, "Sample.cs")));
        Assert.Equal(source, File.ReadAllText(fixture.SourcePath));
        candidate.Dispose();
    }

    [Fact]
    public void InsertPreservesAnAbsentTerminalNewline()
    {
        const string source = "namespace N;\npublic class C\n{\n    public void M() { }\n}";
        using var fixture = ApplicationFixture.Create(
            source,
            DocumentationPatchRepositoryEncoding.Utf8);

        var result = new CandidatePatchApplicator().Apply(
            fixture.ClassifiedSession,
            fixture.Request());

        var candidate = Assert.IsType<DocumentationPatchCandidateHandle>(result.Candidate);
        var bytes = CandidateBytes(candidate, "Sample.cs");
        Assert.False(bytes.AsSpan().EndsWith("\n"u8));
        Assert.Equal(
            source.Replace(
                "    public void M() { }",
                "    /// <inheritdoc/>\n    public void M() { }",
                StringComparison.Ordinal),
            Encoding.UTF8.GetString(bytes));
        candidate.Dispose();
    }

    [Fact]
    public void FileWithoutASeparatorUsesLfForATopLevelDeclaration()
    {
        const string source = "public class C { }";
        using var fixture = ApplicationFixture.Create(
            source,
            DocumentationPatchRepositoryEncoding.Utf8,
            targetClass: true);

        var result = new CandidatePatchApplicator().Apply(
            fixture.ClassifiedSession,
            fixture.Request());

        var candidate = Assert.IsType<DocumentationPatchCandidateHandle>(result.Candidate);
        Assert.Equal(
            "/// <inheritdoc/>\npublic class C { }",
            Encoding.UTF8.GetString(CandidateBytes(candidate, "Sample.cs")));
        candidate.Dispose();
    }

    [Fact]
    public void MultipleSameFileEditsAreComputedFromTheOriginalAndAppliedDescending()
    {
        const string source = "namespace N;\npublic class C\n{\n    public void A() { }\n\n    public void B() { }\n}\n";
        using var fixture = ApplicationFixture.Create(
            source,
            DocumentationPatchRepositoryEncoding.Utf8,
            targetAllMethods: true);

        var result = new CandidatePatchApplicator().Apply(
            fixture.ClassifiedSession,
            fixture.Request());

        var candidate = Assert.IsType<DocumentationPatchCandidateHandle>(result.Candidate);
        Assert.Equal(
            "namespace N;\npublic class C\n{\n"
            + "    /// <inheritdoc/>\n    public void A() { }\n\n"
            + "    /// <inheritdoc/>\n    public void B() { }\n}\n",
            Encoding.UTF8.GetString(CandidateBytes(candidate, "Sample.cs")));
        candidate.Dispose();
    }

    [Fact]
    public void InsertionUsesTheAttributedOwnerLineWithNonBmpPrefixText()
    {
        const string source = "namespace N;\n// 😀 prefix\npublic class C\n{\n    [Obsolete]\n    public void M() { }\n}\n";
        using var fixture = ApplicationFixture.Create(
            source,
            DocumentationPatchRepositoryEncoding.Utf8);

        var result = new CandidatePatchApplicator().Apply(
            fixture.ClassifiedSession,
            fixture.Request());

        var candidate = Assert.IsType<DocumentationPatchCandidateHandle>(result.Candidate);
        Assert.Equal(
            "namespace N;\n// 😀 prefix\npublic class C\n{\n"
            + "    /// <inheritdoc/>\n    [Obsolete]\n    public void M() { }\n}\n",
            Encoding.UTF8.GetString(CandidateBytes(candidate, "Sample.cs")));
        candidate.Dispose();
    }

    [Fact]
    public void MixedNewlinesAreRejectedWithoutPublishingAHandle()
    {
        const string source = "namespace N;\r\npublic class C\n{\r\n    public void M() { }\r\n}\r\n";
        using var fixture = ApplicationFixture.Create(
            source,
            DocumentationPatchRepositoryEncoding.Utf8);

        var result = new CandidatePatchApplicator().Apply(
            fixture.ClassifiedSession,
            fixture.Request());

        Assert.Equal(DocumentationPatchApplicationStatus.Rejected, result.Status);
        Assert.Equal("patch.rejected.unsafe-change", result.PrimaryCode);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public void NoEffectiveReplacementIsRejectedBeforeCandidatePublication()
    {
        const string source = "namespace N;\npublic class C\n{\n    /// <inheritdoc/>\n    public void M() { }\n}\n";
        using var fixture = ApplicationFixture.Create(
            source,
            DocumentationPatchRepositoryEncoding.Utf8);

        var result = new CandidatePatchApplicator().Apply(
            fixture.ClassifiedSession,
            fixture.Request(DocumentationPatchEditKind.Replace));

        Assert.Equal(DocumentationPatchApplicationStatus.Rejected, result.Status);
        Assert.Equal("patch.rejected.unsafe-change", result.PrimaryCode);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public void ProtectedDriftAfterAuthoritySealIsStaleAndProducesNoHandle()
    {
        const string source = "namespace N; public class C { public void M() { } }";
        using var fixture = ApplicationFixture.Create(
            source,
            DocumentationPatchRepositoryEncoding.Utf8);
        File.AppendAllText(fixture.SourcePath, " ");

        var result = new CandidatePatchApplicator().Apply(
            fixture.ClassifiedSession,
            fixture.Request());

        Assert.Equal(DocumentationPatchApplicationStatus.Stale, result.Status);
        Assert.Equal("patch.stale.repository-context", result.PrimaryCode);
        Assert.Null(result.PrimaryBlockId);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public void DriftAfterBaselineCaptureUsesTheCapturedBytesThenFailsFinalRebind()
    {
        const string source = "namespace N;\npublic class C\n{\n    public void M() { }\n}\n";
        using var fixture = ApplicationFixture.Create(
            source,
            DocumentationPatchRepositoryEncoding.Utf8);
        string? candidateRoot = null;
        var applicator = new CandidatePatchApplicator(
            new Patching.Resolution.DocumentationPatchResolver(),
            (stage, path) =>
            {
                if (stage == DocumentationPatchApplicationStage.BaselineCaptured)
                {
                    File.AppendAllText(fixture.SourcePath, "// drift\n");
                }
                else if (stage == DocumentationPatchApplicationStage.CandidateRootCreated)
                {
                    candidateRoot = path;
                }
            });

        var result = applicator.Apply(fixture.ClassifiedSession, fixture.Request());

        Assert.Equal(DocumentationPatchApplicationStatus.Stale, result.Status);
        Assert.Equal("patch.stale.repository-context", result.PrimaryCode);
        Assert.Null(result.PrimaryBlockId);
        Assert.Null(result.Candidate);
        Assert.NotNull(candidateRoot);
        Assert.False(Directory.Exists(candidateRoot));
    }

    [Fact]
    public void SessionWithoutSealedAuthorityFailsClosed()
    {
        const string source = "namespace N; public class C { public void M() { } }";
        using var fixture = ApplicationFixture.Create(
            source,
            DocumentationPatchRepositoryEncoding.Utf8,
            sealAuthority: false);

        var result = new CandidatePatchApplicator().Apply(
            fixture.ClassifiedSession,
            fixture.Request());

        Assert.Equal(DocumentationPatchApplicationStatus.Stale, result.Status);
        Assert.Equal("patch.stale.repository-context", result.PrimaryCode);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public void DisposedSessionFailsClosed()
    {
        const string source = "namespace N; public class C { public void M() { } }";
        using var fixture = ApplicationFixture.Create(
            source,
            DocumentationPatchRepositoryEncoding.Utf8);
        fixture.ClassifiedSession.RepositorySession.Dispose();

        var result = new CandidatePatchApplicator().Apply(
            fixture.ClassifiedSession,
            fixture.Request());

        Assert.Equal(DocumentationPatchApplicationStatus.Stale, result.Status);
        Assert.Equal("patch.stale.repository-context", result.PrimaryCode);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public void CancellationAfterTheFirstWritePublishesNoHandleAndCleansTheWorkspace()
    {
        const string source = "namespace N;\npublic class C\n{\n    public void M() { }\n}\n";
        using var fixture = ApplicationFixture.Create(
            source,
            DocumentationPatchRepositoryEncoding.Utf8,
            additionalFiles: new Dictionary<string, byte[]>
            {
                ["second.txt"] = Encoding.UTF8.GetBytes("second"),
            });
        using var cancellation = new CancellationTokenSource();
        string? candidateRoot = null;
        var applicator = new CandidatePatchApplicator(
            new Patching.Resolution.DocumentationPatchResolver(),
            (stage, path) =>
            {
                if (stage == DocumentationPatchApplicationStage.CandidateRootCreated)
                {
                    candidateRoot = path;
                }
                else if (stage == DocumentationPatchApplicationStage.CandidateEntryWritten)
                {
                    cancellation.Cancel();
                }
            });

        Assert.Throws<OperationCanceledException>(() => applicator.Apply(
            fixture.ClassifiedSession,
            fixture.Request(),
            cancellation.Token));
        Assert.NotNull(candidateRoot);
        Assert.False(Directory.Exists(candidateRoot));
    }

    [Fact]
    public void MutationAfterSealDoesNotTriggerLaterE1AuthorizationIo()
    {
        const string source = "namespace N;\npublic class C\n{\n    public void M() { }\n}\n";
        using var fixture = ApplicationFixture.Create(
            source,
            DocumentationPatchRepositoryEncoding.Utf8);
        var applicator = new CandidatePatchApplicator(
            new Patching.Resolution.DocumentationPatchResolver(),
            (stage, _) =>
            {
                if (stage == DocumentationPatchApplicationStage.AfterSealBeforeReturn)
                {
                    File.AppendAllText(fixture.SourcePath, "// drift after E1 seal\n");
                }
            });

        var result = applicator.Apply(fixture.ClassifiedSession, fixture.Request());

        Assert.Equal(DocumentationPatchApplicationStatus.Complete, result.Status);
        var candidate = Assert.IsType<DocumentationPatchCandidateHandle>(result.Candidate);
        Assert.DoesNotContain(
            "drift after E1 seal",
            Encoding.UTF8.GetString(CandidateBytes(candidate, "Sample.cs")),
            StringComparison.Ordinal);
        candidate.Dispose();
    }

    [Fact]
    public void ConsumptionTransfersCleanupOwnershipExactlyOnce()
    {
        const string source = "namespace N;\npublic class C\n{\n    public void M() { }\n}\n";
        using var fixture = ApplicationFixture.Create(
            source,
            DocumentationPatchRepositoryEncoding.Utf8);
        var result = new CandidatePatchApplicator().Apply(
            fixture.ClassifiedSession,
            fixture.Request());
        var candidate = Assert.IsType<DocumentationPatchCandidateHandle>(result.Candidate);
        var candidateRoot = candidate.RootPath;

        var consumption = Assert.IsType<DocumentationPatchCandidateConsumption>(
            candidate.TryConsume());

        Assert.True(candidate.IsInvalidated);
        Assert.Null(candidate.TryConsume());
        candidate.Dispose();
        Assert.True(Directory.Exists(candidateRoot));
        Assert.Equal(candidateRoot, consumption.RootPath);
        consumption.Dispose();
        Assert.False(Directory.Exists(candidateRoot));
        consumption.Dispose();
    }

    [Fact]
    public void CleanupAbandonsAReplacedRootWithoutTouchingTheReplacement()
    {
        const string source = "namespace N;\npublic class C\n{\n    public void M() { }\n}\n";
        using var fixture = ApplicationFixture.Create(
            source,
            DocumentationPatchRepositoryEncoding.Utf8);
        var result = new CandidatePatchApplicator().Apply(
            fixture.ClassifiedSession,
            fixture.Request());
        var candidate = Assert.IsType<DocumentationPatchCandidateHandle>(result.Candidate);
        var candidateRoot = candidate.RootPath;
        var movedRoot = candidateRoot + "-moved";
        Directory.Move(candidateRoot, movedRoot);
        Directory.CreateDirectory(candidateRoot);
        var replacementMarker = Path.Join(candidateRoot, "replacement.txt");
        File.WriteAllText(replacementMarker, "replacement");

        candidate.Dispose();

        Assert.True(candidate.IsInvalidated);
        Assert.Equal("replacement", File.ReadAllText(replacementMarker));
        Assert.True(Directory.Exists(movedRoot));
        Directory.Delete(candidateRoot, recursive: true);
        Directory.Delete(movedRoot, recursive: true);
    }

    [Fact]
    public void GovernedHardLinksFailClosedOnLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        const string source = "namespace N; public class C { public void M() { } }";
        using var fixture = ApplicationFixture.Create(
            source,
            DocumentationPatchRepositoryEncoding.Utf8);
        Assert.Equal(0, Link(fixture.SourcePath, Path.Join(fixture.Root, "alias.txt")));

        var result = new CandidatePatchApplicator().Apply(
            fixture.ClassifiedSession,
            fixture.Request());

        Assert.Equal(DocumentationPatchApplicationStatus.Rejected, result.Status);
        Assert.Equal("patch.rejected.unsafe-change", result.PrimaryCode);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public void GovernedSymbolicLinksFailClosedOnLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        const string source = "namespace N; public class C { public void M() { } }";
        using var fixture = ApplicationFixture.Create(
            source,
            DocumentationPatchRepositoryEncoding.Utf8);
        File.CreateSymbolicLink(Path.Join(fixture.Root, "alias.txt"), "Sample.cs");

        var result = new CandidatePatchApplicator().Apply(
            fixture.ClassifiedSession,
            fixture.Request());

        Assert.Equal(DocumentationPatchApplicationStatus.Rejected, result.Status);
        Assert.Equal("patch.rejected.unsafe-change", result.PrimaryCode);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public void CaseDistinctGovernedFilesRemainDistinctOnLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        const string source = "namespace N;\npublic class C\n{\n    public void M() { }\n}\n";
        using var fixture = ApplicationFixture.Create(
            source,
            DocumentationPatchRepositoryEncoding.Utf8,
            additionalSources: new Dictionary<string, string>
            {
                ["sample.cs"] = "namespace N; internal class LowerCase { }\n",
            });

        var result = new CandidatePatchApplicator().Apply(
            fixture.ClassifiedSession,
            fixture.Request());

        var candidate = Assert.IsType<DocumentationPatchCandidateHandle>(result.Candidate);
        Assert.Contains(candidate.Files, file => file.RepositoryPath == "Sample.cs");
        Assert.Contains(candidate.Files, file => file.RepositoryPath == "sample.cs");
        Assert.Equal(
            Encoding.UTF8.GetBytes("namespace N; internal class LowerCase { }\n"),
            CandidateBytes(candidate, "sample.cs"));
        candidate.Dispose();
    }

    private static byte[] CandidateBytes(
        DocumentationPatchCandidateHandle candidate,
        string path) => candidate.Files.Single(file => file.RepositoryPath == path).Bytes.ToArray();

    private static RenderingVector LoadRenderingVector(string id)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Join(directory.FullName, "ContractScribe.slnx")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName
            ?? throw new InvalidOperationException("Repository root not found.");
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Join(
            root,
            "tests",
            "fixtures",
            "documentation-patch",
            "rendering",
            "byte-vectors.json")));
        var vector = document.RootElement.GetProperty("vectors")
            .EnumerateArray()
            .Single(candidate => candidate.GetProperty("id").GetString() == id);
        Assert.True(vector.GetProperty("terminalNewline").GetBoolean());
        return new RenderingVector(
            vector.GetProperty("sourceLines").EnumerateArray()
                .Select(line => line.GetString()!).ToArray(),
            vector.GetProperty("candidateLines").EnumerateArray()
                .Select(line => line.GetString()!).ToArray());
    }

    private sealed record RenderingVector(string[] SourceLines, string[] CandidateLines);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(string existingPath, string newPath);

    private sealed class ApplicationFixture : IDisposable
    {
        private static readonly ImmutableArray<MetadataReference> PlatformReferences =
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator)
                .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
                .ToImmutableArray();

        private readonly LoadedRepositorySession repositorySession;
        private readonly ImmutableArray<ApplicationTarget> targets;

        private ApplicationFixture(
            string root,
            string sourcePath,
            RepositoryContextRef repositoryContextRef,
            LoadedRepositorySession repositorySession,
            ClassifiedRepositorySession classifiedSession,
            ImmutableArray<ApplicationTarget> targets)
        {
            Root = root;
            SourcePath = sourcePath;
            RepositoryContextRef = repositoryContextRef;
            this.repositorySession = repositorySession;
            ClassifiedSession = classifiedSession;
            this.targets = targets;
        }

        public string Root { get; }

        public string SourcePath { get; }

        public RepositoryContextRef RepositoryContextRef { get; }

        public ClassifiedRepositorySession ClassifiedSession { get; }

        public static ApplicationFixture Create(
            string source,
            DocumentationPatchRepositoryEncoding encoding,
            IReadOnlyDictionary<string, byte[]>? additionalFiles = null,
            IReadOnlyDictionary<string, string>? additionalSources = null,
            bool sealAuthority = true,
            bool targetClass = false,
            bool targetAllMethods = false)
        {
            var root = Path.Join(
                Path.GetTempPath(),
                "contract-scribe-patch-application-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var sourcePath = Path.Join(root, "Sample.cs");
            var exactBytes = Encode(source, encoding);
            File.WriteAllBytes(sourcePath, exactBytes);
            foreach (var file in additionalFiles ?? new Dictionary<string, byte[]>())
            {
                var path = Path.Join(root, file.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, file.Value);
            }

            var trees = ImmutableArray.CreateBuilder<SyntaxTree>();
            trees.Add(Parse(source, sourcePath));
            foreach (var additional in additionalSources ?? new Dictionary<string, string>())
            {
                var path = Path.Join(root, additional.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, Encoding.UTF8.GetBytes(additional.Value));
                trees.Add(Parse(additional.Value, path));
            }

            var compilation = CSharpCompilation.Create(
                "PatchApplicationFixture",
                trees,
                PlatformReferences,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var declarations = targetClass
                ? trees[0].GetRoot().DescendantNodes()
                    .OfType<ClassDeclarationSyntax>()
                    .Where(type => type.Identifier.ValueText == "C")
                    .Cast<SyntaxNode>()
                : trees[0].GetRoot().DescendantNodes()
                    .OfType<MethodDeclarationSyntax>()
                    .Where(method => targetAllMethods || method.Identifier.ValueText == "M")
                    .Cast<SyntaxNode>();
            var selectedDeclarations = declarations.OrderBy(node => node.SpanStart).ToArray();
            Assert.NotEmpty(selectedDeclarations);
            var model = compilation.GetSemanticModel(trees[0]);
            const string compilationContextRef = "fixture.net10.0";
            var targets = selectedDeclarations.Select((declaration, index) =>
            {
                var symbol = model.GetDeclaredSymbol(declaration)
                    ?? throw new InvalidOperationException("Fixture declaration did not bind.");
                var symbolRef = new SymbolRef(
                    compilationContextRef,
                    symbol.GetDocumentationCommentId()!);
                var reference = Assert.Single(symbol.DeclaringSyntaxReferences);
                return new ApplicationTarget(
                    symbolRef,
                    new DocumentationPatchRepositoryLocator(
                        "Sample.cs",
                        Sha256(exactBytes),
                        encoding,
                        DocumentationObservationInput.Span(
                            reference.Span.Start,
                            reference.Span.End)),
                    $"block-{index + 1}");
            }).ToImmutableArray();
            var workspace = new AdhocWorkspace();
            var project = workspace.AddProject("Fixture", LanguageNames.CSharp);
            var loadedProject = new LoadedProject(
                "Fixture.csproj",
                "net10.0",
                compilationContextRef,
                LoadedProjectRole.AuditRoot,
                [],
                project,
                compilation,
                compilation.SyntaxTrees.ToDictionary(
                    tree => tree,
                    tree => new LoadedSourceTree(
                        LoadedSourceKind.Repository,
                        Path.GetFileName(tree.FilePath),
                        new RepositoryPathResolver().PhysicalIdentity(root, tree.FilePath),
                        null)));
            Assert.True(RepositoryContextRef.TryParse(
                "repoctx-0123456789abcdef0123456789abcdef",
                out var repositoryContextRef));
            var repositorySession = new LoadedRepositorySession(
                repositoryContextRef,
                root,
                "Fixture.csproj",
                new ToolchainIdentity("test", "test", "test", "test"),
                [loadedProject],
                [],
                workspace);
            if (sealAuthority)
            {
                repositorySession.SealDocumentationPatchRepositoryPolicyForTests();
            }

            var classifications = new ClassificationSet(
                TargetProfile.ExternalApi,
                targets.Select(target => new TargetClassification(
                    target.SymbolRef,
                    targetClass ? PrimarySymbolKind.Class : PrimarySymbolKind.Method,
                    [],
                    ClassificationOrigin.Source,
                    SupportStatus.Supported)).ToImmutableArray(),
                [],
                [],
                []);
            var classified = ClassifiedRepositorySession.Bind(
                repositorySession,
                ClassificationOutcome.Success(classifications));
            return new ApplicationFixture(
                root,
                sourcePath,
                repositoryContextRef,
                repositorySession,
                classified,
                targets);
        }

        public DocumentationPatchRequest Request(
            DocumentationPatchEditKind editKind = DocumentationPatchEditKind.Insert) =>
            new(
                new string('0', 64),
                new DocumentationPatchContext(
                    RepositoryContextRef,
                    "Fixture.csproj",
                    TargetProfile.ExternalApi),
                [],
                targets.Select(target => new DocumentationPatchBlockRequest(
                    target.BlockId,
                    target.SymbolRef,
                    target.Locator,
                    editKind,
                    [],
                    new DocumentationPatchInheritDocContent(),
                    [])).ToImmutableArray());

        public static byte[] Encode(
            string source,
            DocumentationPatchRepositoryEncoding encoding) => encoding switch
            {
                DocumentationPatchRepositoryEncoding.Utf8 =>
                    new UTF8Encoding(false, true).GetBytes(source),
                DocumentationPatchRepositoryEncoding.Utf8Bom =>
                    new UTF8Encoding(true, true).GetPreamble()
                        .Concat(new UTF8Encoding(false, true).GetBytes(source))
                        .ToArray(),
                DocumentationPatchRepositoryEncoding.Utf16LittleEndianBom =>
                    new UnicodeEncoding(false, true, true).GetPreamble()
                        .Concat(new UnicodeEncoding(false, false, true).GetBytes(source))
                        .ToArray(),
                DocumentationPatchRepositoryEncoding.Utf16BigEndianBom =>
                    new UnicodeEncoding(true, true, true).GetPreamble()
                        .Concat(new UnicodeEncoding(true, false, true).GetBytes(source))
                        .ToArray(),
                _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
            };

        public void Dispose()
        {
            repositorySession.Dispose();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private static SyntaxTree Parse(string source, string path) =>
            CSharpSyntaxTree.ParseText(
                source,
                new CSharpParseOptions(
                    LanguageVersion.Preview,
                    documentationMode: DocumentationMode.Diagnose),
                path,
                Encoding.UTF8);

        private static string Sha256(byte[] bytes) =>
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        private sealed record ApplicationTarget(
            SymbolRef SymbolRef,
            DocumentationPatchRepositoryLocator Locator,
            string BlockId);
    }
}
