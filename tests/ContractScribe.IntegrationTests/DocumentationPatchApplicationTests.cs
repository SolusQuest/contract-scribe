using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContractScribe.Core;
using ContractScribe.Patching;
using ContractScribe.Roslyn;
using ContractScribe.Roslyn.IntegrationTests;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ContractScribe.IntegrationTests;

[Collection("Integration process lane 1")]
public sealed class DocumentationPatchApplicationTests
{
    [Fact]
    public async Task RealLoaderPreservesRepositoryRolesAndLinkedLogicalPathsThroughE1()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        await using var fixture = await LoaderFixture.CreateAsync();
        var appSource = Path.Join(fixture.Root, "App", "App.cs");
        const string source =
            "public class RealApi\n{\n    public void M() { }\n}\n";
        await File.WriteAllTextAsync(appSource, source, new UTF8Encoding(false));
        var shared = Path.Join(fixture.Root, "Shared");
        var inputs = Path.Join(fixture.Root, "Inputs");
        var build = Path.Join(fixture.Root, "Build");
        Directory.CreateDirectory(shared);
        Directory.CreateDirectory(inputs);
        Directory.CreateDirectory(build);
        await File.WriteAllTextAsync(
            Path.Join(shared, "Linked.cs"),
            "public class LinkedApi { public void Linked() { } }\n");
        await File.WriteAllTextAsync(Path.Join(inputs, "input.txt"), "input\n");
        await File.WriteAllTextAsync(
            Path.Join(inputs, "settings.globalconfig"),
            "is_global = true\ncontract_scribe_fixture = enabled\n");
        await File.WriteAllTextAsync(
            Path.Join(fixture.Root, "Directory.Build.props"),
            "<Project><PropertyGroup><ContractScribeFixture>true</ContractScribeFixture></PropertyGroup></Project>");
        await File.WriteAllTextAsync(
            Path.Join(fixture.Root, "Directory.Build.targets"),
            "<Project />");
        await File.WriteAllTextAsync(
            Path.Join(build, "Custom.targets"),
            "<Project />");
        await File.WriteAllTextAsync(
            Path.Join(fixture.Root, "App", "obj", "GeneratedInput.txt"),
            "generated input\n");
        var projectPath = Path.Join(fixture.Root, "App", "App.csproj");
        var projectText = await File.ReadAllTextAsync(projectPath);
        await File.WriteAllTextAsync(
            projectPath,
            projectText.Replace(
                "</Project>",
                """
                <ItemGroup>
                  <Compile Include="../Shared/Linked.cs" Link="Logical/Nested/Linked.cs" />
                  <AdditionalFiles Include="App.cs" Link="Logical/Input/App-copy.cs" />
                  <AdditionalFiles Include="../Inputs/input.txt" Link="Logical/Input/input.txt" />
                  <AdditionalFiles Include="obj/GeneratedInput.txt" Link="Logical/Input/output.txt" />
                  <EditorConfigFiles Include="../Inputs/settings.globalconfig" Link="Logical/Config/settings.globalconfig" />
                </ItemGroup>
                <Import Project="../Build/Custom.targets" />
                </Project>
                """,
                StringComparison.Ordinal));

        var load = await new RepositoryLoader().LoadAsync(
            new RepositoryLoadRequest(fixture.Root, "App/App.csproj"));
        Assert.True(
            load.Status == RepositoryLoadStatus.Success,
            $"{load.PrimaryFailure?.Stage}:{load.PrimaryFailure?.Code}");
        await using var repository = Assert.IsType<LoadedRepositorySession>(load.Session);
        var classified = new SymbolClassifier().ClassifySession(
            repository,
            TargetProfile.ExternalApi);
        var target = Assert.Single(
            classified.Classification.ClassificationSet!.Targets,
            candidate => candidate.SymbolRef.DocumentationCommentId == "M:RealApi.M"
                && candidate.SupportStatus == SupportStatus.Supported);
        var project = Assert.Single(repository.Projects, candidate =>
            candidate.CompilationContextRef == target.SymbolRef.CompilationContextRef);
        var symbol = Assert.Single(DocumentationCommentId.GetSymbolsForDeclarationId(
            target.SymbolRef.DocumentationCommentId,
            project.Compilation));
        var reference = Assert.Single(symbol.DeclaringSyntaxReferences);
        var loadedSource = project.SourceTrees[reference.SyntaxTree];
        var repositoryPath = Assert.IsType<string>(loadedSource.RepositoryPath);
        var bytes = await File.ReadAllBytesAsync(appSource);
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
                    Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                    DocumentationPatchRepositoryEncoding.Utf8,
                    DocumentationObservationInput.Span(
                        reference.Span.Start,
                        reference.Span.End)),
                DocumentationPatchEditKind.Insert,
                [],
                new DocumentationPatchInheritDocContent(),
                [])]);

        var result = new CandidatePatchApplicator().Apply(classified, request);

        Assert.Equal(DocumentationPatchApplicationStatus.Complete, result.Status);
        using var candidate = Assert.IsType<DocumentationPatchCandidateHandle>(result.Candidate);
        Assert.Contains(candidate.Baseline.Entries, entry =>
            entry.RepositoryPath == "Directory.Build.props");
        Assert.Contains(candidate.Baseline.Entries, entry =>
            entry.RepositoryPath == "Directory.Build.targets");
        Assert.Contains(candidate.Baseline.Entries, entry =>
            entry.RepositoryPath == "Build/Custom.targets");
        Assert.Contains(candidate.Baseline.SemanticInputs, fact =>
            fact.RepositoryPath == "Shared/Linked.cs"
            && fact.Role == DocumentationPatchSemanticInputRole.Source
            && fact.LogicalPath == "Logical/Nested/Linked.cs");
        Assert.Contains(candidate.Baseline.SemanticInputs, fact =>
            fact.RepositoryPath == "App/App.cs"
            && fact.Role == DocumentationPatchSemanticInputRole.AdditionalFile
            && fact.LogicalPath == "Logical/Input/App-copy.cs");
        Assert.Contains(candidate.Baseline.SemanticInputs, fact =>
            fact.RepositoryPath == "App/obj/GeneratedInput.txt"
            && fact.Role == DocumentationPatchSemanticInputRole.AdditionalFile);
        Assert.Contains(candidate.Baseline.SemanticInputs, fact =>
            fact.RepositoryPath == "Inputs/settings.globalconfig"
            && fact.Role == DocumentationPatchSemanticInputRole.AnalyzerConfig);
        Assert.Contains(
            "/// <inheritdoc/>",
            Encoding.UTF8.GetString(CandidateBytes(candidate, "App/App.cs")),
            StringComparison.Ordinal);
        Assert.Equal(source, await File.ReadAllTextAsync(appSource));
    }

    [Fact]
    public void InsertBuildsACompleteIsolatedCandidateAndLeavesOriginalUntouched()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        const string source = "namespace N;\n\npublic class C\n{\n    public void M() { }\n}\n";
        using var fixture = ApplicationFixture.Create(
            source,
            DocumentationPatchRepositoryEncoding.Utf8,
            additionalFiles: new Dictionary<string, byte[]>
            {
                ["assets/nested/notes.txt"] = Encoding.UTF8.GetBytes("unchanged\n"),
                ["config/unchanged.txt"] = Encoding.UTF8.GetBytes("config\n"),
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
        Assert.Equal(3, candidate.Files.Length);
        Assert.Equal(
            source.Replace(
                "    public void M() { }",
                "    /// <inheritdoc/>\n    public void M() { }",
                StringComparison.Ordinal),
            Encoding.UTF8.GetString(CandidateBytes(candidate, "Sample.cs")));
        Assert.Equal(
            Encoding.UTF8.GetBytes("unchanged\n"),
            CandidateBytes(candidate, "assets/nested/notes.txt"));
        Assert.Equal(
            Encoding.UTF8.GetBytes("config\n"),
            CandidateBytes(candidate, "config/unchanged.txt"));
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
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

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
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

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

    [Theory]
    [InlineData(DocumentationPatchRepositoryEncoding.Utf8, "\r\n")]
    [InlineData(DocumentationPatchRepositoryEncoding.Utf8Bom, "\n")]
    [InlineData(DocumentationPatchRepositoryEncoding.Utf16LittleEndianBom, "\r\n")]
    [InlineData(DocumentationPatchRepositoryEncoding.Utf16BigEndianBom, "\n")]
    public void ReplacementPreservesExactEncodingBomAndNewlineRepresentation(
        DocumentationPatchRepositoryEncoding encoding,
        string newline)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var source = string.Join(newline,
            "namespace N;",
            "public class C",
            "{",
            "    /// <summary>",
            "    /// Old.",
            "    /// </summary>",
            "    public void M() { }",
            "}") + newline;
        using var fixture = ApplicationFixture.Create(source, encoding);

        using var candidate = Assert.IsType<DocumentationPatchCandidateHandle>(
            new CandidatePatchApplicator().Apply(
                fixture.ClassifiedSession,
                fixture.Request(DocumentationPatchEditKind.Replace)).Candidate);
        var expected = string.Join(newline,
            "namespace N;",
            "public class C",
            "{",
            "    /// <inheritdoc/>",
            "    public void M() { }",
            "}") + newline;

        Assert.Equal(
            ApplicationFixture.Encode(expected, encoding),
            CandidateBytes(candidate, "Sample.cs"));
    }

    [Fact]
    public void InsertPreservesAnAbsentTerminalNewline()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

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
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

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
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

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
    public void SeveralSelectedFilesAreRenderedFromTheSameBaseline()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        const string source =
            "namespace N;\npublic class C\n{\n    public void A() { }\n}\n";
        using var fixture = ApplicationFixture.Create(
            source,
            DocumentationPatchRepositoryEncoding.Utf8,
            additionalSources: new Dictionary<string, string>
            {
                ["Nested/Other.cs"] =
                    "namespace N;\npublic class Other\n{\n    public void B() { }\n}\n",
            },
            targetAllMethods: true,
            targetAllSources: true);

        using var candidate = Assert.IsType<DocumentationPatchCandidateHandle>(
            new CandidatePatchApplicator().Apply(
                fixture.ClassifiedSession,
                fixture.Request()).Candidate);

        Assert.Contains(
            "/// <inheritdoc/>",
            Encoding.UTF8.GetString(CandidateBytes(candidate, "Sample.cs")),
            StringComparison.Ordinal);
        Assert.Contains(
            "/// <inheritdoc/>",
            Encoding.UTF8.GetString(CandidateBytes(candidate, "Nested/Other.cs")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void InsertionUsesTheAttributedOwnerLineWithNonBmpPrefixText()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

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
    public void StandaloneCarriageReturnIsRejectedWithoutPublishingAHandle()
    {
        const string source = "namespace N;\rpublic class C { public void M() { } }";
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
    public void InvalidUtf8BytesAreRejectedWithoutPublishingAHandle()
    {
        const string source = "namespace N; public class C { public void M() { } }";
        using var fixture = ApplicationFixture.Create(
            source,
            DocumentationPatchRepositoryEncoding.Utf8,
            sourceBytesOverride: [0xc3, 0x28]);

        var result = new CandidatePatchApplicator().Apply(
            fixture.ClassifiedSession,
            fixture.Request());

        Assert.Equal(DocumentationPatchApplicationStatus.Stale, result.Status);
        Assert.Equal("patch.stale.source-encoding", result.PrimaryCode);
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
        Assert.Equal("patch.rejected.no-effective-change", result.PrimaryCode);
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
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

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
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        const string source = "namespace N;\npublic class C\n{\n    public void M() { }\n}\n";
        using var fixture = ApplicationFixture.Create(
            source,
            DocumentationPatchRepositoryEncoding.Utf8,
            additionalFiles: new Dictionary<string, byte[]>
            {
                ["nested/second.txt"] = Encoding.UTF8.GetBytes("second"),
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
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

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
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

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
    public void ConsumedCandidateCapturesCurrentDiskBytesExactlyOnce()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        const string source = "namespace N;\npublic class C\n{\n    public void M() { }\n}\n";
        using var fixture = ApplicationFixture.Create(
            source,
            DocumentationPatchRepositoryEncoding.Utf8,
            additionalFiles: new Dictionary<string, byte[]>
            {
                ["nested/input.txt"] = "input"u8.ToArray(),
            });
        var result = new CandidatePatchApplicator().Apply(
            fixture.ClassifiedSession,
            fixture.Request());
        var candidate = Assert.IsType<DocumentationPatchCandidateHandle>(result.Candidate);
        using var consumption = Assert.IsType<DocumentationPatchCandidateConsumption>(
            candidate.TryConsume());

        var capture = consumption.CaptureCandidateForValidation();

        Assert.Equal(DocumentationPatchCandidateCaptureStatus.Captured, capture.Status);
        Assert.Equal(candidate.Files.Length, capture.Files.Length);
        Assert.All(capture.Files, captured => Assert.True(
            candidate.Files.Single(expected =>
                    expected.RepositoryPath == captured.RepositoryPath)
                .Bytes.AsSpan().SequenceEqual(captured.Bytes.AsSpan())));
        Assert.Equal(
            DocumentationPatchCandidateCaptureStatus.Mismatch,
            consumption.CaptureCandidateForValidation().Status);
    }

    [Fact]
    public void ConsumedCandidateRejectsAReplacedRootWithoutReadingTheReplacement()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

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
        using var consumption = Assert.IsType<DocumentationPatchCandidateConsumption>(
            candidate.TryConsume());
        Directory.Move(candidateRoot, movedRoot);
        Directory.CreateDirectory(candidateRoot);
        var replacementMarker = Path.Join(candidateRoot, "replacement.txt");
        File.WriteAllText(replacementMarker, "replacement");

        var capture = consumption.CaptureCandidateForValidation();

        Assert.Equal(DocumentationPatchCandidateCaptureStatus.Mismatch, capture.Status);
        Assert.Empty(capture.Files);
        Assert.Equal("replacement", File.ReadAllText(replacementMarker));
        consumption.Dispose();
        Directory.Delete(candidateRoot, recursive: true);
        Directory.Delete(movedRoot, recursive: true);
    }

    [Fact]
    public void ConsumedCandidateRejectsAReplacedSubtreeWithoutReadingTheReplacement()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        const string source = "namespace N;\npublic class C\n{\n    public void M() { }\n}\n";
        using var fixture = ApplicationFixture.Create(
            source,
            DocumentationPatchRepositoryEncoding.Utf8,
            additionalFiles: new Dictionary<string, byte[]>
            {
                ["nested/input.txt"] = "input"u8.ToArray(),
            });
        var result = new CandidatePatchApplicator().Apply(
            fixture.ClassifiedSession,
            fixture.Request());
        var candidate = Assert.IsType<DocumentationPatchCandidateHandle>(result.Candidate);
        var candidateRoot = candidate.RootPath;
        using var consumption = Assert.IsType<DocumentationPatchCandidateConsumption>(
            candidate.TryConsume());
        var subtree = Path.Join(candidateRoot, "nested");
        var movedSubtree = Path.Join(candidateRoot, "nested-moved");
        Directory.Move(subtree, movedSubtree);
        Directory.CreateDirectory(subtree);
        var replacementMarker = Path.Join(subtree, "replacement.txt");
        File.WriteAllText(replacementMarker, "replacement");

        var capture = consumption.CaptureCandidateForValidation();

        Assert.Equal(DocumentationPatchCandidateCaptureStatus.Mismatch, capture.Status);
        Assert.Empty(capture.Files);
        Assert.Equal("replacement", File.ReadAllText(replacementMarker));
        consumption.Dispose();
        Directory.Delete(candidateRoot, recursive: true);
    }

    [Fact]
    public void StagingInsideTheOriginalCheckoutIsRejectedBeforeRootCreation()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        const string source =
            "namespace N;\npublic class C\n{\n    public void M() { }\n}\n";
        using var fixture = ApplicationFixture.Create(
            source,
            DocumentationPatchRepositoryEncoding.Utf8);
        var before = Directory.EnumerateFileSystemEntries(fixture.Root)
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var rootCreated = false;
        var applicator = new CandidatePatchApplicator(
            new Patching.Resolution.DocumentationPatchResolver(),
            (stage, _) => rootCreated |=
                stage == DocumentationPatchApplicationStage.CandidateRootCreated,
            () => fixture.Root);

        var result = applicator.Apply(fixture.ClassifiedSession, fixture.Request());

        Assert.Equal(DocumentationPatchApplicationStatus.Rejected, result.Status);
        Assert.False(rootCreated);
        Assert.Equal(before, Directory.EnumerateFileSystemEntries(fixture.Root)
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void StagingThroughAnAliasOfTheOriginalCheckoutIsRejectedBeforeRootCreation()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        const string source =
            "namespace N;\npublic class C\n{\n    public void M() { }\n}\n";
        using var fixture = ApplicationFixture.Create(
            source,
            DocumentationPatchRepositoryEncoding.Utf8);
        var alias = Path.Join(
            Path.GetTempPath(),
            "contract-scribe-patch-alias-" + Guid.NewGuid().ToString("N"));
        Directory.CreateSymbolicLink(alias, fixture.Root);
        try
        {
            var rootCreated = false;
            var applicator = new CandidatePatchApplicator(
                new Patching.Resolution.DocumentationPatchResolver(),
                (stage, _) => rootCreated |=
                    stage == DocumentationPatchApplicationStage.CandidateRootCreated,
                () => alias);

            var result = applicator.Apply(fixture.ClassifiedSession, fixture.Request());

            Assert.Equal(DocumentationPatchApplicationStatus.Rejected, result.Status);
            Assert.False(rootCreated);
        }
        finally
        {
            Directory.Delete(alias);
        }
    }

    [Fact]
    public void WindowsFailsClosedBeforeCandidateRootCreation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string source = "namespace N; public class C { public void M() { } }";
        using var fixture = ApplicationFixture.Create(
            source,
            DocumentationPatchRepositoryEncoding.Utf8);
        var rootCreated = false;
        var result = new CandidatePatchApplicator(
            new Patching.Resolution.DocumentationPatchResolver(),
            (stage, _) => rootCreated |=
                stage == DocumentationPatchApplicationStage.CandidateRootCreated)
            .Apply(fixture.ClassifiedSession, fixture.Request());

        Assert.True(result.Status is DocumentationPatchApplicationStatus.Rejected
            or DocumentationPatchApplicationStatus.Failure);
        Assert.Equal("patch.rejected.unsafe-change", result.PrimaryCode);
        Assert.False(rootCreated);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public void DeterministicWriteFailurePublishesNoHandleAndCleansTheWorkspace()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        const string source =
            "namespace N;\npublic class C\n{\n    public void M() { }\n}\n";
        using var fixture = ApplicationFixture.Create(
            source,
            DocumentationPatchRepositoryEncoding.Utf8,
            additionalFiles: new Dictionary<string, byte[]>
            {
                ["nested/input.txt"] = "input"u8.ToArray(),
            });
        string? candidateRoot = null;
        var writes = 0;
        var applicator = new CandidatePatchApplicator(
            new Patching.Resolution.DocumentationPatchResolver(),
            (stage, path) =>
            {
                if (stage == DocumentationPatchApplicationStage.CandidateRootCreated)
                {
                    candidateRoot = path;
                }
                else if (stage == DocumentationPatchApplicationStage.CandidateEntryWritten
                    && ++writes == 2)
                {
                    throw new IOException("Injected deterministic write failure.");
                }
            });

        var result = applicator.Apply(fixture.ClassifiedSession, fixture.Request());

        Assert.Equal(DocumentationPatchApplicationStatus.Failure, result.Status);
        Assert.Null(result.Candidate);
        Assert.NotNull(candidateRoot);
        Assert.False(Directory.Exists(candidateRoot));
    }

    [Fact]
    public void CandidateRootObserverFailureCleansTheCreatedWorkspace()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        const string source =
            "namespace N;\npublic class C\n{\n    public void M() { }\n}\n";
        using var fixture = ApplicationFixture.Create(
            source,
            DocumentationPatchRepositoryEncoding.Utf8);
        string? candidateRoot = null;
        var applicator = new CandidatePatchApplicator(
            new Patching.Resolution.DocumentationPatchResolver(),
            (stage, path) =>
            {
                if (stage == DocumentationPatchApplicationStage.CandidateRootCreated)
                {
                    candidateRoot = path;
                    throw new IOException("Injected candidate-root observer failure.");
                }
            });

        var result = applicator.Apply(fixture.ClassifiedSession, fixture.Request());

        Assert.Equal(DocumentationPatchApplicationStatus.Failure, result.Status);
        Assert.NotNull(candidateRoot);
        Assert.False(Directory.Exists(candidateRoot));
        Assert.Null(result.Candidate);
    }

    [Fact]
    public void RepeatedApplicationsProduceIdenticalCandidateBytes()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        const string source = "namespace N;\npublic class C\n{\n    public void M() { }\n}\n";
        using var fixture = ApplicationFixture.Create(
            source,
            DocumentationPatchRepositoryEncoding.Utf8,
            additionalFiles: new Dictionary<string, byte[]>
            {
                ["nested/input.txt"] = "input"u8.ToArray(),
            });

        using var first = Assert.IsType<DocumentationPatchCandidateHandle>(
            new CandidatePatchApplicator().Apply(
                fixture.ClassifiedSession,
                fixture.Request()).Candidate);
        using var second = Assert.IsType<DocumentationPatchCandidateHandle>(
            new CandidatePatchApplicator().Apply(
                fixture.ClassifiedSession,
                fixture.Request()).Candidate);

        Assert.Equal(
            first.Files.Select(file => file.RepositoryPath),
            second.Files.Select(file => file.RepositoryPath));
        Assert.All(first.Files, firstFile => Assert.True(
            second.Files.Single(secondFile =>
                    secondFile.RepositoryPath == firstFile.RepositoryPath)
                .Bytes.AsSpan().SequenceEqual(firstFile.Bytes.AsSpan())));
        Assert.NotEqual(first.RootPath, second.RootPath);
    }

    [Fact]
    public void CleanupAbandonsAReplacedRootWithoutTouchingTheReplacement()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

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
    public void OutputRootDirectoryLinksFailClosedBeforeCandidateCaptureOnLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        const string source = "namespace N; public class C { public void M() { } }";
        using var fixture = ApplicationFixture.Create(
            source,
            DocumentationPatchRepositoryEncoding.Utf8,
            additionalFiles: new Dictionary<string, byte[]>
            {
                ["output/placeholder.txt"] = "placeholder"u8.ToArray(),
            },
            allowedOutputRoots: ["output"]);
        var output = Path.Join(fixture.Root, "output");
        var outside = Path.Join(
            Path.GetTempPath(),
            "contract-scribe-output-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Join(outside, "Hidden.cs"), "internal class Hidden { }");
        Directory.Delete(output, recursive: true);
        Directory.CreateSymbolicLink(output, outside);
        try
        {
            var result = new CandidatePatchApplicator().Apply(
                fixture.ClassifiedSession,
                fixture.Request());

            Assert.Equal(DocumentationPatchApplicationStatus.Rejected, result.Status);
            Assert.Equal("patch.rejected.unsafe-change", result.PrimaryCode);
            Assert.Null(result.Candidate);
        }
        finally
        {
            Directory.Delete(output);
            Directory.Delete(outside, recursive: true);
        }
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
            bool targetAllMethods = false,
            bool targetAllSources = false,
            IReadOnlyList<string>? allowedOutputRoots = null,
            byte[]? sourceBytesOverride = null)
        {
            var root = Path.Join(
                Path.GetTempPath(),
                "contract-scribe-patch-application-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var sourcePath = Path.Join(root, "Sample.cs");
            var exactBytes = sourceBytesOverride ?? Encode(source, encoding);
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
            IEnumerable<SyntaxTree> targetTrees = targetAllSources
                ? trees
                : [trees[0]];
            var declarations = targetClass
                ? targetTrees.SelectMany(tree => tree.GetRoot().DescendantNodes())
                    .OfType<ClassDeclarationSyntax>()
                    .Where(type => type.Identifier.ValueText == "C")
                    .Cast<SyntaxNode>()
                : targetTrees.SelectMany(tree => tree.GetRoot().DescendantNodes())
                    .OfType<MethodDeclarationSyntax>()
                    .Where(method => targetAllMethods || method.Identifier.ValueText == "M")
                    .Cast<SyntaxNode>();
            var selectedDeclarations = declarations
                .OrderBy(node => node.SyntaxTree.FilePath, StringComparer.Ordinal)
                .ThenBy(node => node.SpanStart)
                .ToArray();
            Assert.NotEmpty(selectedDeclarations);
            const string compilationContextRef = "fixture.net10.0";
            var targets = selectedDeclarations.Select((declaration, index) =>
            {
                var model = compilation.GetSemanticModel(declaration.SyntaxTree);
                var symbol = model.GetDeclaredSymbol(declaration)
                    ?? throw new InvalidOperationException("Fixture declaration did not bind.");
                var symbolRef = new SymbolRef(
                    compilationContextRef,
                    symbol.GetDocumentationCommentId()!);
                var repositoryPath = Path.GetRelativePath(root, declaration.SyntaxTree.FilePath)
                    .Replace('\\', '/');
                var declarationBytes = File.ReadAllBytes(declaration.SyntaxTree.FilePath);
                return new ApplicationTarget(
                    symbolRef,
                    new DocumentationPatchRepositoryLocator(
                        repositoryPath,
                        Sha256(declarationBytes),
                        declaration.SyntaxTree == trees[0]
                            ? encoding
                            : DocumentationPatchRepositoryEncoding.Utf8,
                        DocumentationObservationInput.Span(
                            declaration.Span.Start,
                            declaration.Span.End)),
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
                        Path.GetRelativePath(root, tree.FilePath).Replace('\\', '/'),
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
                repositorySession.SealDocumentationPatchRepositoryPolicyForTests(
                    allowedOutputRoots);
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
