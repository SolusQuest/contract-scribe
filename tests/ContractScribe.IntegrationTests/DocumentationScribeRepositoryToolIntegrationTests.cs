using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;
using ContractScribe.Core;

namespace ContractScribe.Roslyn.IntegrationTests;

[Collection("Integration process lane 2")]
public sealed class DocumentationScribeRepositoryToolIntegrationTests
{
    private const string ProjectionOutputVariable = "CONTRACTSCRIBE_REPOSITORY_PROJECTION_OUTPUT";

    [Fact]
    public async Task CursorCannotCrossFreshToolSessionAndOriginalChainRemainsUsable()
    {
        using var fixture = RepositoryToolFixture.Create();
        var firstBundle = fixture.Bundle();
        var first = await firstBundle.ListFiles.InvokeAsync(
            new("context.instructions", "docs", 1), default);
        var cursor = Assert.IsType<string>(first.Cursor);

        var freshBundle = fixture.Bundle();
        var crossSession = await freshBundle.ListFiles.InvokeAsync(
            new("context.instructions", "docs", 1, cursor), default);
        Assert.Same(DocumentationScribeToolOutcome.Failure, crossSession.Outcome);
        Assert.Equal(DocumentationScribeRepositoryToolFailureCodes.InvalidCursor, crossSession.FailureCode);
        Assert.Empty(crossSession.Items);
        Assert.Null(crossSession.Cursor);

        var continuation = await firstBundle.ListFiles.InvokeAsync(
            new("context.instructions", "docs", 1, cursor), default);
        Assert.Same(DocumentationScribeToolOutcome.Complete, continuation.Outcome);
        Assert.Equal("docs/unicode-newlines.txt", Assert.Single(continuation.Items).RepositoryPath);
    }

    [Fact]
    public async Task LinkEscapeIsRejectedWithoutContentOrAbsolutePathDisclosure()
    {
        using var fixture = RepositoryToolFixture.Create();
        if (!fixture.TryCreateEscapingLink())
        {
            Assert.True(OperatingSystem.IsWindows(), "Linux CI must support the authoritative symlink boundary test.");
            return;
        }

        var listed = await fixture.Bundle().ListFiles.InvokeAsync(
            new("context.instructions", "docs", 8), default);
        Assert.Same(DocumentationScribeToolOutcome.Failure, listed.Outcome);
        Assert.Equal(DocumentationScribeRepositoryToolFailureCodes.UnsafeObject, listed.FailureCode);
        Assert.Empty(listed.Items);
        Assert.Null(listed.Cursor);
        Assert.DoesNotContain(fixture.Root, listed.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.OutsideRoot, listed.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadExcerptRejectsLinuxChildMountWithoutContentOrEvidence()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var fixture = RepositoryToolFixture.CreateLinuxRoot();

        var result = await fixture.Bundle("proc/version").ReadExcerpt.InvokeAsync(
            new("context.instructions", "proc/version"), default);

        Assert.Same(DocumentationScribeToolOutcome.Failure, result.Outcome);
        Assert.Equal(DocumentationScribeRepositoryToolFailureCodes.UnsafeObject, result.FailureCode);
        Assert.Null(result.Excerpt);
        Assert.Empty(result.DynamicEvidence);
    }

    [Fact]
    public async Task UnicodeAndMixedNewlinesKeepExactStableExcerptCommitments()
    {
        using var fixture = RepositoryToolFixture.Create();
        var first = await fixture.Bundle().ReadExcerpt.InvokeAsync(
            new("context.instructions", "docs/unicode-newlines.txt", 2, 2), default);
        var second = await fixture.Bundle().ReadExcerpt.InvokeAsync(
            new("context.instructions", "docs/unicode-newlines.txt", 2, 2), default);

        Assert.Same(DocumentationScribeToolOutcome.Incomplete, first.Outcome);
        Assert.Equal("bounded 😀 evidence\r", first.Excerpt!.Content);
        Assert.Equal(first.Excerpt, second.Excerpt);
        Assert.Equal(
            first.Excerpt.IncludedUtf8ByteCount,
            Encoding.UTF8.GetByteCount(first.Excerpt.Content));
        Assert.False(char.IsHighSurrogate(first.Excerpt.Content[^1]));
    }

    [Fact]
    public async Task PhysicalAliasIsRejectedBeforeAnyInventoryPublication()
    {
        using var fixture = RepositoryToolFixture.Create();
        if (!fixture.TryCreateHardLinkAlias())
        {
            Assert.True(OperatingSystem.IsWindows(), "Linux CI must support the authoritative hard-link boundary test.");
            return;
        }

        var listed = await fixture.Bundle().ListFiles.InvokeAsync(
            new("context.instructions", "docs", 8), default);
        Assert.Same(DocumentationScribeToolOutcome.Failure, listed.Outcome);
        Assert.Equal(DocumentationScribeRepositoryToolFailureCodes.UnsafeObject, listed.FailureCode);
        Assert.Empty(listed.Items);
        Assert.Null(listed.Cursor);
    }

    [Fact]
    public async Task CaseCollisionIsRejectedOnCaseSensitiveHosts()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var fixture = RepositoryToolFixture.Create();
        File.WriteAllText(Path.Join(fixture.Root, "docs", "Case.md"), "upper\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Join(fixture.Root, "docs", "case.md"), "lower\n", new UTF8Encoding(false));

        var listed = await fixture.Bundle().ListFiles.InvokeAsync(
            new("context.instructions", "docs", 8), default);
        Assert.Same(DocumentationScribeToolOutcome.Failure, listed.Outcome);
        Assert.Equal(DocumentationScribeRepositoryToolFailureCodes.UnsafeObject, listed.FailureCode);
        Assert.Empty(listed.Items);
    }

    [Fact]
    public async Task SemanticCommitmentsAreIdenticalAcrossFreshProcesses()
    {
        var childOutput = Environment.GetEnvironmentVariable(ProjectionOutputVariable);
        if (!string.IsNullOrEmpty(childOutput))
        {
            using var fixture = RepositoryToolFixture.Create();
            var bundle = fixture.Bundle();
            var list = await bundle.ListFiles.InvokeAsync(
                new("context.instructions", "docs", 8), default);
            var search = await bundle.SearchText.InvokeAsync(
                new("context.instructions", "bounded", "docs", 8), default);
            var read = await bundle.ReadExcerpt.InvokeAsync(
                new("context.instructions", "docs/guide.md"), default);
            var evidenceIds = read.DynamicEvidence
                .Concat(search.DynamicEvidence)
                .Select(input =>
                {
                    Assert.True(DocumentationScribeValidation.TryCreateDynamicEvidenceReference(
                        fixture.Request, input, out var reference));
                    return reference!.EvidenceReferenceId;
                })
                .Order(StringComparer.Ordinal)
                .ToArray();
            var projection = new
            {
                list = list.Items.Select(item => new { item.RepositoryPath, item.ContentSha256, item.Utf8ByteCount }),
                search = search.Items.Select(item => new
                {
                    item.RepositoryPath,
                    item.ContentSha256,
                    item.StartUtf16,
                    item.EndUtf16,
                    item.MatchStartUtf16,
                    item.MatchEndUtf16,
                }),
                read = new
                {
                    read.Excerpt!.RepositoryPath,
                    read.Excerpt.ContentSha256,
                    read.Excerpt.OriginalUtf8ByteCount,
                    read.Excerpt.IncludedUtf8ByteCount,
                },
                evidenceIds,
            };
            File.WriteAllBytes(childOutput, JsonSerializer.SerializeToUtf8Bytes(projection));
            return;
        }

        var first = Path.GetTempFileName();
        var second = Path.GetTempFileName();
        try
        {
            await RunProjectionChild(first);
            await RunProjectionChild(second);
            Assert.Equal(await File.ReadAllTextAsync(first), await File.ReadAllTextAsync(second));
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }

    private static async Task RunProjectionChild(string outputPath)
    {
        var root = RepositoryToolFixture.FindRepositoryRoot();
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("test");
        start.ArgumentList.Add(Path.Join(root, "tests", "ContractScribe.IntegrationTests", "ContractScribe.IntegrationTests.csproj"));
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("Release");
        start.ArgumentList.Add("--no-build");
        start.ArgumentList.Add("--no-restore");
        start.ArgumentList.Add("--filter");
        start.ArgumentList.Add("FullyQualifiedName~SemanticCommitmentsAreIdenticalAcrossFreshProcesses");
        start.Environment[ProjectionOutputVariable] = outputPath;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Projection child did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(process.ExitCode == 0,
            $"projection child exit={process.ExitCode}; stdout={await standardOutput}; stderr={await standardError}");
        Assert.True(File.Exists(outputPath));
    }

    private sealed class RepositoryToolFixture : IDisposable
    {
        private readonly LoadedRepositorySession repository;
        private readonly DocumentationScribeLoadedContext loaded;
        private readonly DocumentationScribeRequest request;
        private readonly bool deleteRoots;

        private RepositoryToolFixture(
            string root,
            string outsideRoot,
            LoadedRepositorySession repository,
            DocumentationScribeLoadedContext loaded,
            DocumentationScribeRequest request,
            bool deleteRoots)
        {
            Root = root;
            OutsideRoot = outsideRoot;
            this.repository = repository;
            this.loaded = loaded;
            this.request = request;
            this.deleteRoots = deleteRoots;
        }

        internal string Root { get; }
        internal string OutsideRoot { get; }
        internal DocumentationScribeRequest Request => request;

        internal static RepositoryToolFixture Create()
        {
            var root = Path.Join(Path.GetTempPath(), "contract-scribe-x2-int-" + Guid.NewGuid().ToString("N"));
            var outside = Path.Join(Path.GetTempPath(), "contract-scribe-x2-outside-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Join(root, "docs"));
            Directory.CreateDirectory(outside);
            const string instructionContent = "accepted instruction\n";
            File.WriteAllText(Path.Join(root, "AGENTS.md"), instructionContent, new UTF8Encoding(false));
            File.WriteAllText(Path.Join(root, "docs", "guide.md"), "Guide\nbounded evidence.\n", new UTF8Encoding(false));
            File.WriteAllText(
                Path.Join(root, "docs", "unicode-newlines.txt"),
                "alpha\nbounded 😀 evidence\romega\r\n",
                new UTF8Encoding(false));
            File.WriteAllText(Path.Join(outside, "secret.md"), "outside-secret-marker\n", new UTF8Encoding(false));

            return Create(root, outside, instructionContent, deleteRoots: true);
        }

        internal static RepositoryToolFixture CreateLinuxRoot() =>
            Create("/", string.Empty, "accepted instruction\n", deleteRoots: false);

        private static RepositoryToolFixture Create(
            string root,
            string outside,
            string instructionContent,
            bool deleteRoots)
        {
            var request = ParseRequest(instructionContent);
            var repository = new LoadedRepositorySession(
                request.Context.RepositoryContextRef,
                root,
                request.Context.InputIdentity,
                new ToolchainIdentity("test", "test", "test", "test"),
                [],
                [],
                new Disposable());
            var classified = ClassifiedRepositorySession.Bind(repository, ClassificationOutcome.Failure());
            var selection = DocumentationScribeContextValidation.CreateBootstrapSelection(
                request.Context.RepositoryContextRef,
                request.Context.InputIdentity,
                request.Context.TargetProfile,
                request.Target.SymbolRef,
                request.Target.SourceLocator,
                request.Target.SourceSha256);
            var visible = Assert.Single(request.ContextReferences);
            var commitment = DocumentationScribeContextValidation.CreateSourceCommitment(
                visible.Path,
                visible.ContentSha256,
                visible.ContentSha256,
                visible.OriginalUtf8ByteCount,
                visible.IncludedUtf8ByteCount,
                visible.IsTruncated,
                false);
            var instruction = DocumentationScribeContextValidation.CreateInstructionFact(
                DocumentationScribeContextRole.AgentEntrypoint,
                0,
                commitment,
                instructionContent);
            var facts = DocumentationScribeContextValidation.CreateFacts(
                selection,
                [instruction],
                [],
                [],
                []);
            var freshness = new DocumentationScribeContextFreshnessGuard(
                root,
                DocumentationScribeContextStableFileReader.ReadDirectoryIdentity(root),
                [],
                [],
                classified,
                request.Context.RepositoryContextRef);
            var loaded = new DocumentationScribeLoadedContext(
                classified,
                selection,
                facts,
                new DocumentationScribeContextCursorAuthority(RandomNumberGenerator.GetBytes(32)),
                freshness,
                null);
            return new(root, outside, repository, loaded, request, deleteRoots);
        }

        internal DocumentationScribeRepositoryToolBundle Bundle(string? exactFilePath = null)
        {
            Assert.True(DocumentationScribeAttemptId.TryParse(
                "scribe-attempt.11111111111111111111111111111111",
                out var attempt));
            var subject = EvidenceInput.TargetSubject(
                request.Target.SymbolRef.CompilationContextRef,
                request.Target.SymbolRef.DocumentationCommentId);
            var scope = exactFilePath is null
                ? DocumentationScribeRepositoryToolScope.Directory(
                    "context.instructions",
                    string.Empty,
                    DocumentationScribeRepositoryToolOperations.ReadExcerpt
                        | DocumentationScribeRepositoryToolOperations.ListFiles
                        | DocumentationScribeRepositoryToolOperations.SearchText,
                    DocumentationScribeContextRole.MaintainedDocumentation,
                    extensions: [".md", ".txt"],
                    subject: subject,
                    claimCategoryIds: ["claim.purpose"])
                : DocumentationScribeRepositoryToolScope.Directory(
                    "context.instructions",
                    string.Empty,
                    DocumentationScribeRepositoryToolOperations.ReadExcerpt,
                    DocumentationScribeContextRole.MaintainedDocumentation,
                    subject: subject,
                    claimCategoryIds: ["claim.purpose"]);
            return DocumentationScribeRepositoryToolBundle.Create(
                request,
                attempt,
                loaded,
                [scope],
                DocumentationScribeRepositoryToolLimits.Create(maximumPageSize: 8));
        }

        internal bool TryCreateEscapingLink()
        {
            try
            {
                File.CreateSymbolicLink(
                    Path.Join(Root, "docs", "escape.md"),
                    Path.Join(OutsideRoot, "secret.md"));
                return true;
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                return false;
            }
        }

        internal bool TryCreateHardLinkAlias()
        {
            var source = Path.Join(Root, "docs", "hard-source.md");
            File.WriteAllText(source, "hard-link-content\n", new UTF8Encoding(false));
            try
            {
                var alias = Path.Join(Root, "docs", "hard-alias.md");
                return OperatingSystem.IsWindows()
                    ? CreateHardLinkW(alias, source, IntPtr.Zero)
                    : OperatingSystem.IsLinux() && Link(source, alias) == 0;
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                return false;
            }
        }

        public void Dispose()
        {
            repository.Dispose();
            if (deleteRoots)
            {
                Directory.Delete(Root, recursive: true);
                Directory.Delete(OutsideRoot, recursive: true);
            }
        }

        private static DocumentationScribeRequest ParseRequest(string instructionContent)
        {
            var path = Path.Join(
                FindRepositoryRoot(), "tests", "fixtures", "documentation-scribe", "v1", "valid", "request.json");
            var request = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var reference = request["contextReferences"]![0]!.AsObject();
            var bytes = Encoding.UTF8.GetBytes(instructionContent);
            reference["contentSha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            reference["originalUtf8ByteCount"] = bytes.Length;
            reference["includedUtf8ByteCount"] = bytes.Length;
            var parsed = DocumentationScribeValidation.ParseRequest(
                Encoding.UTF8.GetBytes(request.ToJsonString()));
            Assert.True(parsed.IsValid, parsed.Failure?.Code);
            return Assert.IsType<DocumentationScribeRequest>(parsed.Request);
        }

        internal static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Join(directory.FullName, "ContractScribe.slnx")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
        }

        private sealed class Disposable : IDisposable
        {
            public void Dispose()
            {
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateHardLinkW(
            string fileName,
            string existingFileName,
            IntPtr securityAttributes);

        [DllImport("libc", EntryPoint = "link", SetLastError = true)]
        private static extern int Link(string existingFileName, string fileName);
    }
}
