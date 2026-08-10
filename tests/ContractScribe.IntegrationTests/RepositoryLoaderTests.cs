using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContractScribe.Core;

namespace ContractScribe.Roslyn.IntegrationTests;

[Collection("Integration process lane 1")]
public sealed class RepositoryLoaderTests
{
    [Fact]
    public async Task LoadsExplicitProjectWithCompleteMixedSingleTfmClosure()
    {
        await using var fixture = await LoaderFixture.CreateAsync(
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <DefineConstants Condition="'$(TargetFramework)' == 'net10.0'">$(DefineConstants);APP_NET10</DefineConstants>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="../Library/Library.csproj" Condition="'$(TargetFramework)' == 'net10.0'" />
              </ItemGroup>
            </Project>
            """,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net9.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        var outcome = await new RepositoryLoader().LoadAsync(
            new RepositoryLoadRequest(fixture.Root, "App/App.csproj"));

        Assert.True(
            outcome.Status == RepositoryLoadStatus.Success,
            $"{outcome.PrimaryFailure?.Stage}:{outcome.PrimaryFailure?.Code}; secondary={string.Join(',', outcome.SecondaryFacts.Select(fact => fact.Code))}");
        Assert.NotNull(outcome.Session);
        await using var session = outcome.Session;
        Assert.Equal("X64", session.Toolchain.Architecture);
        Assert.Collection(
            session.Projects.OrderBy(project => project.ProjectIdentity, StringComparer.Ordinal),
            app =>
            {
                Assert.Equal("App/App.csproj", app.ProjectIdentity);
                Assert.Equal("net10.0", app.TargetFramework);
                Assert.Equal(LoadedProjectRole.AuditRoot, app.Role);
                Assert.Equal(["Library/Library.csproj"], app.ProjectReferences);
            },
            library =>
            {
                Assert.Equal("Library/Library.csproj", library.ProjectIdentity);
                Assert.Equal("net9.0", library.TargetFramework);
                Assert.Equal(LoadedProjectRole.DependencyOnly, library.Role);
            });
    }

    [Fact]
    public async Task LoadsSlnxAndKeepsEveryListedProjectAsAuditRoot()
    {
        await using var fixture = await LoaderFixture.CreateAsync();

        var outcome = await new RepositoryLoader().LoadAsync(
            new RepositoryLoadRequest(fixture.Root, fixture.SolutionPath));

        Assert.True(
            outcome.Status == RepositoryLoadStatus.Success,
            $"{outcome.PrimaryFailure?.Stage}:{outcome.PrimaryFailure?.Code}; secondary={string.Join(',', outcome.SecondaryFacts.Select(fact => fact.Code))}");
        await using var session = Assert.IsType<LoadedRepositorySession>(outcome.Session);
        Assert.All(session.Projects, project => Assert.Equal(LoadedProjectRole.AuditRoot, project.Role));
    }

    [Fact]
    public async Task LoadsSolutionThroughRepositoryRootAlias()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var alias = Path.Combine(
            Path.GetDirectoryName(fixture.Root)!,
            $"root-alias-{Guid.NewGuid():N}");
        CreateDirectoryLink(alias, fixture.Root);
        try
        {
            var outcome = await new RepositoryLoader().LoadAsync(
                new RepositoryLoadRequest(alias, "Fixture.slnx"));

            Assert.True(
                outcome.Status == RepositoryLoadStatus.Success,
                $"{outcome.PrimaryFailure?.Stage}:{outcome.PrimaryFailure?.Code}");
            await using var session = Assert.IsType<LoadedRepositorySession>(outcome.Session);
            Assert.Equal(2, session.Projects.Count);
        }
        finally
        {
            Directory.Delete(alias);
        }
    }

    [Fact]
    public async Task IncludesProjectReferencesConditionedOnDesignTimeBuild()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var projectPath = Path.Combine(fixture.Root, "App", "App.csproj");
        var text = await File.ReadAllTextAsync(projectPath);
        await File.WriteAllTextAsync(
            projectPath,
            text.Replace(
                """<ProjectReference Include="../Library/Library.csproj" />""",
                """<ProjectReference Include="../Library/Library.csproj" Condition="'$(DesignTimeBuild)' == 'true'" />""",
                StringComparison.Ordinal));

        var outcome = await new RepositoryLoader().LoadAsync(
            new RepositoryLoadRequest(fixture.Root, "App/App.csproj"));

        Assert.True(
            outcome.Status == RepositoryLoadStatus.Success,
            $"{outcome.PrimaryFailure?.Stage}:{outcome.PrimaryFailure?.Code}");
        await using var session = Assert.IsType<LoadedRepositorySession>(outcome.Session);
        var app = Assert.Single(
            session.Projects,
            project => project.ProjectIdentity == "App/App.csproj");
        Assert.Equal(["Library/Library.csproj"], app.ProjectReferences);
    }

    [Fact]
    public async Task DiscoversTargetFrameworkConditionedOnDesignTimeBuild()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var projectPath = Path.Combine(fixture.Root, "App", "App.csproj");
        var text = await File.ReadAllTextAsync(projectPath);
        await File.WriteAllTextAsync(
            projectPath,
            text.Replace(
                "<TargetFramework>net10.0</TargetFramework>",
                """<TargetFramework Condition="'$(DesignTimeBuild)' == 'true'">net10.0</TargetFramework>""",
                StringComparison.Ordinal));

        var outcome = await new RepositoryLoader().LoadAsync(
            new RepositoryLoadRequest(fixture.Root, "App/App.csproj"));

        Assert.True(
            outcome.Status == RepositoryLoadStatus.Success,
            $"{outcome.PrimaryFailure?.Stage}:{outcome.PrimaryFailure?.Code}");
        await using var session = Assert.IsType<LoadedRepositorySession>(outcome.Session);
        var app = Assert.Single(
            session.Projects,
            project => project.ProjectIdentity == "App/App.csproj");
        Assert.Equal("net10.0", app.TargetFramework);
    }

    [Theory]
    [InlineData("Fixture.sln")]
    [InlineData("Fixture.slnx")]
    public async Task IncludesProjectReferencesConditionedOnSolutionDir(string input)
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var projectPath = Path.Combine(fixture.Root, "App", "App.csproj");
        var text = await File.ReadAllTextAsync(projectPath);
        var solutionDirectory =
            Path.TrimEndingDirectorySeparator(fixture.Root)
            + Path.DirectorySeparatorChar;
        await File.WriteAllTextAsync(
            projectPath,
            text.Replace(
                """<ProjectReference Include="../Library/Library.csproj" />""",
                $"""<ProjectReference Include="../Library/Library.csproj" Condition="'$(SolutionDir)' == '{solutionDirectory}'" />""",
                StringComparison.Ordinal));

        var outcome = await new RepositoryLoader().LoadAsync(
            new RepositoryLoadRequest(fixture.Root, input));

        Assert.True(
            outcome.Status == RepositoryLoadStatus.Success,
            $"{outcome.PrimaryFailure?.Stage}:{outcome.PrimaryFailure?.Code}");
        await using var session = Assert.IsType<LoadedRepositorySession>(outcome.Session);
        var app = Assert.Single(
            session.Projects,
            project => project.ProjectIdentity == "App/App.csproj");
        Assert.Equal(["Library/Library.csproj"], app.ProjectReferences);
    }

    [Theory]
    [InlineData("Fixture.sln")]
    [InlineData("Fixture.slnx")]
    public void KeepsOneSolutionDirSeparatorAtFileSystemRoot(string input)
    {
        var fileSystemRoot = Path.GetPathRoot(Path.GetTempPath())
            ?? throw new InvalidOperationException("The temporary path must have a filesystem root.");
        var solutionPath = Path.Combine(fileSystemRoot, input);
        var paths = new ResolvedRepositoryPaths(
            fileSystemRoot,
            fileSystemRoot,
            solutionPath,
            solutionPath,
            []);

        var properties = PostRegistrationLoader.CreateEvaluationProperties(paths);

        Assert.Equal(fileSystemRoot, properties["SolutionDir"]);
    }

    [Fact]
    public async Task IncludesProjectReferencesConditionedOnPinnedNonExistentFile()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var projectPath = Path.Combine(fixture.Root, "App", "App.csproj");
        var text = await File.ReadAllTextAsync(projectPath);
        await File.WriteAllTextAsync(
            projectPath,
            text.Replace(
                """<ProjectReference Include="../Library/Library.csproj" />""",
                """<ProjectReference Include="../Library/Library.csproj" Condition="'$(NonExistentFile)' == '__NonExistentSubDir__\__NonExistentFile__'" />""",
                StringComparison.Ordinal));

        var outcome = await new RepositoryLoader().LoadAsync(
            new RepositoryLoadRequest(fixture.Root, "App/App.csproj"));

        Assert.True(
            outcome.Status == RepositoryLoadStatus.Success,
            $"{outcome.PrimaryFailure?.Stage}:{outcome.PrimaryFailure?.Code}");
        await using var session = Assert.IsType<LoadedRepositorySession>(outcome.Session);
        var app = Assert.Single(
            session.Projects,
            project => project.ProjectIdentity == "App/App.csproj");
        Assert.Equal(["Library/Library.csproj"], app.ProjectReferences);
    }

    [Fact]
    public async Task LoadsLegacySlnFromAnUnrelatedWorkingDirectory()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var output = await RunLoaderIsolationProbeAsync(
            fixture.Root,
            fixture.LegacySolutionPath,
            "legacy-success",
            Path.GetTempPath());

        Assert.Equal("legacy-success", output.Trim());
    }

    [Fact]
    public async Task RejectsMultiTargetingWithoutReturningPartialSession()
    {
        await using var fixture = await LoaderFixture.CreateAsync(
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>net10.0;net9.0</TargetFrameworks>
              </PropertyGroup>
            </Project>
            """);

        var outcome = await new RepositoryLoader().LoadAsync(
            new RepositoryLoadRequest(fixture.Root, "App/App.csproj"));

        Assert.Equal(RepositoryLoadStatus.Failure, outcome.Status);
        Assert.Equal("graph.target-framework-not-single", outcome.PrimaryFailure?.Code);
        Assert.Null(outcome.Session);
    }

    [Fact]
    public async Task RejectsMultiTargetingInTheDependencyClosure()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Root, "Library", "Library.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFrameworks>net10.0;net9.0</TargetFrameworks></PropertyGroup>
            </Project>
            """);

        var outcome = await new RepositoryLoader().LoadAsync(
            new RepositoryLoadRequest(fixture.Root, "App/App.csproj"));

        Assert.Equal(RepositoryLoadStatus.Failure, outcome.Status);
        Assert.Equal("graph.target-framework-not-single", outcome.PrimaryFailure?.Code);
        Assert.Null(outcome.Session);
    }

    [Fact]
    public async Task CanonicalRootOrderingMakesFailureIndependentOfSolutionOrder()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Root, "App", "App.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFrameworks>net10.0;net9.0</TargetFrameworks></PropertyGroup></Project>""");
        File.Delete(Path.Combine(fixture.Root, "Library", "obj", "project.assets.json"));
        var first = Path.Combine(fixture.Root, "First.slnx");
        var second = Path.Combine(fixture.Root, "Second.slnx");
        await File.WriteAllTextAsync(
            first,
            """<Solution><Project Path="App/App.csproj" /><Project Path="Library/Library.csproj" /></Solution>""");
        await File.WriteAllTextAsync(
            second,
            """<Solution><Project Path="Library/Library.csproj" /><Project Path="App/App.csproj" /></Solution>""");

        var loader = new RepositoryLoader();
        var left = await loader.LoadAsync(new RepositoryLoadRequest(fixture.Root, first));
        var right = await loader.LoadAsync(new RepositoryLoadRequest(fixture.Root, second));

        Assert.Equal(left.PrimaryFailure, right.PrimaryFailure);
        Assert.Equal("graph.target-framework-not-single", left.PrimaryFailure?.Code);
    }

    [Fact]
    public async Task RejectsLexicalEscapeBeforeLoading()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var outcome = await new RepositoryLoader().LoadAsync(
            new RepositoryLoadRequest(fixture.Root, "../outside.csproj"));

        Assert.Equal(RepositoryLoadStatus.Failure, outcome.Status);
        Assert.Equal("input.path-outside-root", outcome.PrimaryFailure?.Code);
    }

    [Fact]
    public async Task RejectsDirectorySlnfAndDirectNonCSharpInputs()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var filter = Path.Combine(fixture.Root, "Fixture.slnf");
        var visualBasic = Path.Combine(fixture.Root, "Other.vbproj");
        await File.WriteAllTextAsync(filter, "{}");
        await File.WriteAllTextAsync(
            visualBasic,
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        var loader = new RepositoryLoader();

        var directory = await loader.LoadAsync(new RepositoryLoadRequest(fixture.Root, fixture.Root));
        var slnf = await loader.LoadAsync(new RepositoryLoadRequest(fixture.Root, filter));
        var nonCSharp = await loader.LoadAsync(new RepositoryLoadRequest(fixture.Root, visualBasic));

        Assert.All(
            new[] { directory, slnf, nonCSharp },
            outcome =>
            {
                Assert.Equal(RepositoryLoadStatus.Failure, outcome.Status);
                Assert.Equal("input.path-not-supported", outcome.PrimaryFailure?.Code);
            });
    }

    [Fact]
    public async Task InvalidGlobalJsonReturnsABoundedToolchainFailure()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Root, "global.json"),
            """{""");

        var outcome = await new RepositoryLoader().LoadAsync(
            new RepositoryLoadRequest(fixture.Root, "App/App.csproj"));

        Assert.Equal(RepositoryLoadStatus.Failure, outcome.Status);
        Assert.Equal("toolchain.sdk-probe-failed", outcome.PrimaryFailure?.Code);
        Assert.Null(outcome.Toolchain);
        Assert.Empty(outcome.Diagnostics);
    }

    [Fact]
    public async Task RejectsTargetFrameworkSuppliedOnlyByTheEnvironment()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Root, "App", "App.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="../Library/Library.csproj" /></ItemGroup></Project>""");
        var output = await RunLoaderIsolationProbeAsync(
            fixture.Root,
            "App/App.csproj",
            "target-framework-environment",
            fixture.Root,
            new Dictionary<string, string?>
            {
                ["TargetFramework"] = "net10.0",
            });

        Assert.Equal("target-framework-rejected", output.Trim());
    }

    [Fact]
    public async Task MissingAssetsIsCallerOwnedAndDoesNotRestore()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        File.Delete(Path.Combine(fixture.Root, "App", "obj", "project.assets.json"));

        var outcome = await new RepositoryLoader().LoadAsync(
            new RepositoryLoadRequest(fixture.Root, "App/App.csproj"));

        Assert.Equal(RepositoryLoadStatus.Failure, outcome.Status);
        Assert.Equal("graph.restore-assets-missing", outcome.PrimaryFailure?.Code);
        Assert.NotNull(outcome.Toolchain);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "App", "obj", "project.assets.json")));
    }

    [Fact]
    public async Task ExposesAuthoritativeSourceGeneratorAndTrustedToolFacts()
    {
        await using var fixture = await LoaderFixture.CreateAsync(withGenerator: true);
        var request = new RepositoryLoadRequest(
            fixture.Root,
            "App/App.csproj",
            [
                new ToolGeneratedSourceInput(
                    "App/App.csproj",
                    "ContractScribe",
                    "FixtureTool",
                    "FixtureOutput",
                    "public static class ToolGenerated { }"),
            ]);

        var outcome = await new RepositoryLoader().LoadAsync(request);

        Assert.True(
            outcome.Status == RepositoryLoadStatus.Success,
            $"{outcome.PrimaryFailure?.Stage}:{outcome.PrimaryFailure?.Code}");
        await using var session = Assert.IsType<LoadedRepositorySession>(outcome.Session);
        Assert.Contains(session.GeneratedSources, fact =>
            fact.ProducerId.StartsWith("sgp.", StringComparison.Ordinal)
            && fact.OutputId.StartsWith("sgo.", StringComparison.Ordinal)
            && fact.SourceText.Contains("FixtureGenerated", StringComparison.Ordinal));
        Assert.Contains(session.GeneratedSources, fact =>
            fact.ProducerId.StartsWith("tgp.", StringComparison.Ordinal)
            && fact.OutputId.StartsWith("tgo.", StringComparison.Ordinal)
            && fact.SourceText.Contains("ToolGenerated", StringComparison.Ordinal));
        var app = Assert.Single(session.Projects, project => project.ProjectIdentity == "App/App.csproj");
        Assert.NotNull(app.Compilation.GetTypeByMetadataName("FixtureGenerated"));
        Assert.NotNull(app.Compilation.GetTypeByMetadataName("ToolGenerated"));
    }

    [Fact]
    public async Task GeneratedAuthorityMatchingUsesOneCandidateComparisonPerUniqueOutput()
    {
        await using var fixture = await LoaderFixture.CreateAsync(
            manyOutputGenerator: true);
        var candidateComparisons = 0;
        var loader = new RepositoryLoader(
            null,
            null,
            null,
            count => candidateComparisons += count);

        var outcome = await loader.LoadAsync(new RepositoryLoadRequest(
            fixture.Root,
            "App/App.csproj"));

        Assert.True(
            outcome.Status == RepositoryLoadStatus.Success,
            $"{outcome.PrimaryFailure?.Stage}:{outcome.PrimaryFailure?.Code}");
        await using var session = Assert.IsType<LoadedRepositorySession>(
            outcome.Session);
        var generatedCount = session.GeneratedSources.Count(fact =>
            fact.ProducerId.StartsWith("sgp.", StringComparison.Ordinal));
        Assert.Equal(129, generatedCount);
        Assert.Equal(generatedCount, candidateComparisons);
    }

    [Fact]
    public async Task GeneratedAuthorityMatchingKeysSameHintAndContentByGeneratorPath()
    {
        await using var fixture = await LoaderFixture.CreateAsync(
            collidingGeneratorOutputs: true);
        var candidateComparisons = 0;
        var loader = new RepositoryLoader(
            null,
            null,
            null,
            count => candidateComparisons += count);

        var outcome = await loader.LoadAsync(new RepositoryLoadRequest(
            fixture.Root,
            "App/App.csproj"));

        Assert.True(
            outcome.Status == RepositoryLoadStatus.Success,
            $"{outcome.PrimaryFailure?.Stage}:{outcome.PrimaryFailure?.Code}");
        await using var session = Assert.IsType<LoadedRepositorySession>(
            outcome.Session);
        var sourceGeneratorFacts = session.GeneratedSources
            .Where(fact =>
                fact.ProducerId.StartsWith("sgp.", StringComparison.Ordinal))
            .ToArray();
        var collidingFacts = sourceGeneratorFacts
            .Where(fact =>
                fact.SourceText == "// identical collision source")
            .ToArray();
        Assert.Equal(2, collidingFacts.Length);
        Assert.Equal(
            2,
            collidingFacts.Select(fact => fact.ProducerId).Distinct().Count());
        Assert.Single(
            collidingFacts.Select(fact => fact.OutputId).Distinct());
        Assert.Equal(sourceGeneratorFacts.Length, candidateComparisons);
    }

    [Theory]
    [InlineData("namespace")]
    [InlineData("producer")]
    [InlineData("output")]
    public async Task InvalidUnicodeToolIdentityUsesTheStableGeneratedFailure(string field)
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        const string invalidUnicode = "\uD800";
        var outcome = await new RepositoryLoader().LoadAsync(new RepositoryLoadRequest(
            fixture.Root,
            "App/App.csproj",
            [
                new(
                    "App/App.csproj",
                    field == "namespace" ? invalidUnicode : "ContractScribe",
                    field == "producer" ? invalidUnicode : "FixtureTool",
                    field == "output" ? invalidUnicode : "FixtureOutput",
                    "public static class ToolGenerated { }"),
            ]));

        Assert.Equal(RepositoryLoadStatus.Failure, outcome.Status);
        Assert.Equal("run.generated.missing-identity", outcome.PrimaryFailure?.Code);
        Assert.DoesNotContain(
            invalidUnicode,
            string.Join(
                '|',
                outcome.SecondaryFacts.Select(fact => fact.Code)
                    .Append(outcome.PrimaryFailure?.Code ?? string.Empty)));
        Assert.Null(outcome.Session);
    }

    [Fact]
    public async Task RejectsAuthoritativeGeneratedDocumentOmittedByTheRerun()
    {
        await using var fixture = await LoaderFixture.CreateAsync(processSensitiveGenerator: true);

        var outcome = await new RepositoryLoader().LoadAsync(
            new RepositoryLoadRequest(fixture.Root, "App/App.csproj"));

        Assert.Equal(RepositoryLoadStatus.Failure, outcome.Status);
        Assert.Equal("run.generated.authority-conflict", outcome.PrimaryFailure?.Code);
        Assert.Null(outcome.Session);
    }

    [Fact]
    public async Task RerunsGeneratorsAgainstThePreGenerationCompilation()
    {
        await using var fixture = await LoaderFixture.CreateAsync(selfObservingGenerator: true);

        var outcome = await new RepositoryLoader().LoadAsync(
            new RepositoryLoadRequest(fixture.Root, "App/App.csproj"));

        Assert.True(
            outcome.Status == RepositoryLoadStatus.Success,
            $"{outcome.PrimaryFailure?.Stage}:{outcome.PrimaryFailure?.Code}");
        await using var session = Assert.IsType<LoadedRepositorySession>(outcome.Session);
        Assert.Contains(session.GeneratedSources, fact =>
            fact.SourceText.Contains("FixtureSelfAware", StringComparison.Ordinal)
            && fact.SourceText.Contains("\"clean\"", StringComparison.Ordinal));
    }

    [Fact]
    public void GeneratedIdentityFramingMatchesTheFrozenAdrVector()
    {
        var identities = new GeneratedIdentityHasher(bytes =>
            System.Security.Cryptography.SHA256.HashData(bytes.Span));

        Assert.Equal(
            "54235a1180ce62bdca90001c8eedf3da4b4cdee29922080cd36237a065c1f08b",
            identities.Hash("contract-scribe/sgo/v1", "widget.g.cs"));
        Assert.Equal(
            "10de880f5c5bc026c951342dd026e0ccdac302edfcbe89e9b02d0efff7de6b3a",
            identities.Hash("contract-scribe/sgo/v1", "Cafe\u0301.g.cs"));
    }

    [Fact]
    public void LoaderDiagnosticsAreDeduplicatedOrderedAndBounded()
    {
        var state = new LoaderExecutionState();
        foreach (var index in Enumerable.Range(0, 40).Reverse())
        {
            var diagnostic = new LoaderDiagnostic(
                "workspace",
                $"workspace.diagnostic-{index:D2}",
                "warning");
            state.AddDiagnostic(diagnostic);
            state.AddDiagnostic(diagnostic);
        }

        Assert.Equal(32, state.Diagnostics.Count);
        Assert.Equal(
            state.Diagnostics.OrderBy(diagnostic => diagnostic.Code, StringComparer.Ordinal),
            state.Diagnostics);
        Assert.Equal(32, state.Diagnostics.Distinct().Count());
    }

    [Fact]
    public void RepositoryIdentitiesDoNotUnicodeNormalizeDistinctPaths()
    {
        var resolver = new RepositoryPathResolver();
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "identity-root"));
        var composed = resolver.RelativeIdentity(root, Path.Combine(root, "Café", "Project.csproj"));
        var decomposed = resolver.RelativeIdentity(root, Path.Combine(root, "Cafe\u0301", "Project.csproj"));

        Assert.NotEqual(composed, decomposed);
    }

    [Fact]
    public void RepositoryPathResolverAcceptsInputAndProjectBelowFileSystemRoot()
    {
        var repositoryRoot = FindRepositoryRoot();
        var projectPath = Path.Join(
            repositoryRoot,
            "src",
            "ContractScribe.Core",
            "ContractScribe.Core.csproj");
        var fileSystemRoot = Path.GetPathRoot(repositoryRoot)
            ?? throw new InvalidOperationException("The repository path must have a filesystem root.");
        var resolver = new RepositoryPathResolver();

        var resolved = resolver.Resolve(fileSystemRoot, projectPath);
        var project = resolver.ResolveProject(
            resolved.LexicalRoot,
            resolved.PhysicalRoot,
            projectPath);

        Assert.Equal(Path.GetFullPath(projectPath), resolved.PhysicalInput);
        Assert.Equal(Path.GetFullPath(projectPath), project.PhysicalPath);
    }

    [Fact]
    public async Task RepositoryLoaderLoadsProjectBelowFileSystemRoot()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var fileSystemRoot = Path.GetPathRoot(fixture.Root)
            ?? throw new InvalidOperationException("The fixture path must have a filesystem root.");
        var projectPath = Path.Join(fixture.Root, "App", "App.csproj");
        var expectedIdentity = Path.GetRelativePath(fileSystemRoot, projectPath)
            .Replace('\\', '/');
        var loader = new RepositoryLoader(
            observer: null,
            inventory: static (_, _) => new Dictionary<string, InventoryEntry>());

        var outcome = await loader.LoadAsync(
            new RepositoryLoadRequest(fileSystemRoot, projectPath));

        Assert.True(
            outcome.Status == RepositoryLoadStatus.Success,
            $"{outcome.PrimaryFailure?.Stage}:{outcome.PrimaryFailure?.Code}");
        await using var session = Assert.IsType<LoadedRepositorySession>(outcome.Session);
        Assert.Contains(
            session.Projects,
            project => project.ProjectIdentity == expectedIdentity);
    }

    [Fact]
    public async Task RejectsDistinctGeneratedIdentityPreimagesWhenTheDigestCollides()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var loader = new RepositoryLoader(
            observer: null,
            digest: _ => new byte[32]);
        var outcome = await loader.LoadAsync(new RepositoryLoadRequest(
            fixture.Root,
            "App/App.csproj",
            [
                new("App/App.csproj", "ContractScribe", "FixtureTool", "First", "public class FirstGenerated { }"),
                new("App/App.csproj", "ContractScribe", "FixtureTool", "Second", "public class SecondGenerated { }"),
            ]));

        Assert.Equal(RepositoryLoadStatus.Failure, outcome.Status);
        Assert.Equal("run.generated.identity-collision", outcome.PrimaryFailure?.Code);
        Assert.Null(outcome.Session);
    }

    [Fact]
    public async Task EqualToolInputsProduceOneFactAndOneCompilationTree()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var source = "public class DeduplicatedGenerated { }";
        var outcome = await new RepositoryLoader().LoadAsync(new RepositoryLoadRequest(
            fixture.Root,
            "App/App.csproj",
            [
                new("App/App.csproj", "ContractScribe", "FixtureTool", "Output", source),
                new("App/App.csproj", "ContractScribe", "FixtureTool", "Output", source),
            ]));

        var session = Assert.IsType<LoadedRepositorySession>(outcome.Session);
        var app = Assert.Single(session.Projects, project => project.ProjectIdentity == "App/App.csproj");
        Assert.Single(session.GeneratedSources, fact => fact.ProducerId.StartsWith("tgp.", StringComparison.Ordinal));
        Assert.Single(app.Compilation.SyntaxTrees, tree => tree.FilePath.StartsWith("tool-generated://", StringComparison.Ordinal));
        await session.DisposeAsync();
    }

    [Theory]
    [InlineData(nameof(LoaderStage.WorkspaceLoad))]
    [InlineData(nameof(LoaderStage.Compilation))]
    [InlineData(nameof(LoaderStage.GeneratedFacts))]
    [InlineData(nameof(LoaderStage.TerminalValidation))]
    public async Task CancellationAtLateObservedStagesNeverCommitsSuccess(string targetName)
    {
        var target = Enum.Parse<LoaderStage>(targetName);
        await using var fixture = await LoaderFixture.CreateAsync(withGenerator: true);
        using var cancellation = new CancellationTokenSource();
        var loader = new RepositoryLoader(stage =>
        {
            if (stage == target)
            {
                cancellation.Cancel();
            }
        });

        var outcome = await loader.LoadAsync(
            new RepositoryLoadRequest(fixture.Root, "App/App.csproj"),
            cancellation.Token);

        Assert.Equal(RepositoryLoadStatus.Cancelled, outcome.Status);
        Assert.NotNull(outcome.Toolchain);
        Assert.Null(outcome.Session);
    }

    [Fact]
    public async Task CancellationRemainsPrimaryWhenFinalInventoryThrows()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var loader = new RepositoryLoader(
            stage =>
            {
                if (stage == LoaderStage.TerminalValidation)
                {
                    cancellation.Cancel();
                }
            },
            inventory: (root, token) =>
            {
                if (calls++ == 0)
                {
                    return RepositoryInventory.Capture(root, token);
                }

                throw new IOException("injected final inventory failure");
            });

        var outcome = await loader.LoadAsync(
            new RepositoryLoadRequest(fixture.Root, "App/App.csproj"),
            cancellation.Token);

        Assert.Equal(RepositoryLoadStatus.Cancelled, outcome.Status);
        Assert.Contains(outcome.SecondaryFacts, fact => fact.Code == "repository.drift-scan-failed");
    }

    [Fact]
    public async Task ProtectedDriftSuppressesSuccessWithoutRepairingTheFile()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var source = Path.Combine(fixture.Root, "App", "App.cs");
        var marker = $"{Environment.NewLine}// repository-controlled drift";
        var loader = new RepositoryLoader(stage =>
        {
            if (stage == LoaderStage.WorkspaceLoad)
            {
                File.AppendAllText(source, marker);
            }
        });

        var outcome = await loader.LoadAsync(
            new RepositoryLoadRequest(fixture.Root, "App/App.csproj"));

        Assert.Equal(RepositoryLoadStatus.Failure, outcome.Status);
        Assert.Equal("repository.protected-drift", outcome.PrimaryFailure?.Code);
        Assert.EndsWith(marker, await File.ReadAllTextAsync(source), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationRemainsPrimaryAndProtectedDriftIsSecondary()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var source = Path.Combine(fixture.Root, "App", "App.cs");
        using var cancellation = new CancellationTokenSource();
        var loader = new RepositoryLoader(stage =>
        {
            if (stage == LoaderStage.WorkspaceLoad)
            {
                File.AppendAllText(source, $"{Environment.NewLine}// cancellation drift");
                cancellation.Cancel();
            }
        });

        var outcome = await loader.LoadAsync(
            new RepositoryLoadRequest(fixture.Root, "App/App.csproj"),
            cancellation.Token);

        Assert.Equal(RepositoryLoadStatus.Cancelled, outcome.Status);
        Assert.Equal("loader.cancelled", outcome.PrimaryFailure?.Code);
        Assert.Contains(outcome.SecondaryFacts, fact => fact.Code == "repository.protected-drift");
        Assert.Null(outcome.Session);
    }

    [Fact]
    public async Task GeneratedFailureRemainsPrimaryAndProtectedDriftIsSecondary()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var source = Path.Combine(fixture.Root, "App", "App.cs");
        var loader = new RepositoryLoader(stage =>
        {
            if (stage == LoaderStage.Compilation)
            {
                File.AppendAllText(source, $"{Environment.NewLine}// failure drift");
            }
        });
        var outcome = await loader.LoadAsync(new RepositoryLoadRequest(
            fixture.Root,
            "App/App.csproj",
            [
                new("App/App.csproj", "ContractScribe", "FixtureTool", "Broken", "public class {"),
            ]));

        Assert.Equal(RepositoryLoadStatus.Failure, outcome.Status);
        Assert.Equal("run.generated.authority-conflict", outcome.PrimaryFailure?.Code);
        Assert.Contains(outcome.SecondaryFacts, fact => fact.Code == "repository.protected-drift");
        Assert.Null(outcome.Session);
    }

    [Theory]
    [InlineData("creation")]
    [InlineData("deletion")]
    public async Task ProtectedCreationOrDeletionSuppressesSuccess(string mode)
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var path = mode == "creation"
            ? Path.Combine(fixture.Root, "Created.cs")
            : Path.Combine(fixture.Root, "App", "App.cs");
        var loader = new RepositoryLoader(stage =>
        {
            if (stage != LoaderStage.WorkspaceLoad)
            {
                return;
            }

            if (mode == "creation")
            {
                File.WriteAllText(path, "public class Created { }");
            }
            else
            {
                File.Delete(path);
            }
        });

        var outcome = await loader.LoadAsync(
            new RepositoryLoadRequest(fixture.Root, "App/App.csproj"));

        Assert.Equal(RepositoryLoadStatus.Failure, outcome.Status);
        Assert.Equal("repository.protected-drift", outcome.PrimaryFailure?.Code);
        Assert.Equal(mode == "creation", File.Exists(path));
    }

    [Fact]
    public async Task AllowedDesignTimeOutputDoesNotMasqueradeAsProtectedDriftOnFailure()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var output = Path.Combine(fixture.Root, "App", "obj", "loader-observation.tmp");
        var before = RepositoryInventory.Capture(fixture.Root, CancellationToken.None);
        var loader = new RepositoryLoader(stage =>
        {
            if (stage == LoaderStage.Compilation)
            {
                File.WriteAllText(output, "allowed");
            }
        });
        var outcome = await loader.LoadAsync(new RepositoryLoadRequest(
            fixture.Root,
            "App/App.csproj",
            [
                new("App/App.csproj", "ContractScribe", "FixtureTool", "Broken", "public class {"),
            ]));

        Assert.Equal(RepositoryLoadStatus.Failure, outcome.Status);
        Assert.Equal("run.generated.authority-conflict", outcome.PrimaryFailure?.Code);
        var after = RepositoryInventory.Capture(fixture.Root, CancellationToken.None);
        var changes = RepositoryInventory.ChangedPaths(before, after);
        Assert.True(
            outcome.SecondaryFacts.All(fact => fact.Code != "repository.protected-drift"),
            $"Allowed output was classified as protected: {string.Join(',', changes)}");
        Assert.True(File.Exists(output));
    }

    [Fact]
    public async Task NewSourceUnderAnAllowedOutputRootIsStillProtected()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var source = Path.Combine(fixture.Root, "App", "obj", "Injected.cs");
        var loader = new RepositoryLoader(stage =>
        {
            if (stage == LoaderStage.WorkspaceLoad)
            {
                File.WriteAllText(source, "public class Injected { }");
            }
        });

        var outcome = await loader.LoadAsync(
            new RepositoryLoadRequest(fixture.Root, "App/App.csproj"));

        Assert.Equal(RepositoryLoadStatus.Failure, outcome.Status);
        Assert.Equal("repository.protected-drift", outcome.PrimaryFailure?.Code);
    }

    [Fact]
    public async Task RetargetedTraversedAliasUnderAnOutputRootIsProtectedDrift()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var firstTarget = Path.Combine(fixture.Root, "SharedOne");
        var secondTarget = Path.Combine(fixture.Root, "SharedTwo");
        Directory.CreateDirectory(firstTarget);
        Directory.CreateDirectory(secondTarget);
        await File.WriteAllTextAsync(
            Path.Combine(firstTarget, "Linked.cs"),
            "public static class LinkedSource { }");
        await File.WriteAllTextAsync(
            Path.Combine(secondTarget, "Linked.cs"),
            "public static class LinkedSource { }");
        var alias = Path.Combine(fixture.Root, "App", "obj", "Alias");
        CreateDirectoryLink(alias, firstTarget);
        var project = Path.Combine(fixture.Root, "App", "App.csproj");
        var projectText = await File.ReadAllTextAsync(project);
        await File.WriteAllTextAsync(
            project,
            projectText.Replace(
                "</Project>",
                """<ItemGroup><Compile Include="obj/Alias/Linked.cs" /></ItemGroup></Project>""",
                StringComparison.Ordinal));
        var loader = new RepositoryLoader(stage =>
        {
            if (stage == LoaderStage.WorkspaceLoad)
            {
                Directory.Delete(alias);
                CreateDirectoryLink(alias, secondTarget);
            }
        });

        var outcome = await loader.LoadAsync(
            new RepositoryLoadRequest(fixture.Root, "App/App.csproj"));

        Assert.Equal(RepositoryLoadStatus.Failure, outcome.Status);
        Assert.Equal("repository.protected-drift", outcome.PrimaryFailure?.Code);
        Assert.Null(outcome.Session);
    }

    [Fact]
    public async Task ClassificationProjectionIsStableAcrossFreshProcesses()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Root, "App", "App.cs"),
            """
            public class Café
            {
                public string Value { get; init; } = "😀";
            }

            internal class AssemblyOnly { }
            """);

        var first = await RunClassificationProbeAsync(
            fixture.Root,
            "external-api",
            "zh-CN",
            "UTC");
        var second = await RunClassificationProbeAsync(
            fixture.Root,
            "external-api",
            "tr-TR",
            "Pacific Standard Time");
        var assemblyVisible = await RunClassificationProbeAsync(
            fixture.Root,
            "assembly-visible",
            "en-US",
            "UTC");

        Assert.Equal(first, second);
        Assert.StartsWith(
            """{"targetProfile":"profile.external-api","targets":[""",
            first,
            StringComparison.Ordinal);
        Assert.Contains("T:Café", first, StringComparison.Ordinal);
        Assert.DoesNotContain("AssemblyOnly", first, StringComparison.Ordinal);
        Assert.StartsWith(
            """{"targetProfile":"profile.assembly-visible","targets":[""",
            assemblyVisible,
            StringComparison.Ordinal);
        Assert.Contains("AssemblyOnly", assemblyVisible, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', first);
        Assert.DoesNotContain('\r', first);
    }

    [Fact]
    public async Task ClassificationIsInvariantToTwoDependencyReferenceOrder()
    {
        await using var forward = await LoaderFixture.CreateAsync(
            withSecondDependency: true);
        await using var reverse = await LoaderFixture.CreateAsync(
            withSecondDependency: true,
            reverseProjectReferences: true);
        const string appSource = """
            public class App : IFirstContract, ISecondContract
            {
                public int First() => 1;
                public int Second() => 2;
            }
            """;
        const string firstSource = """
            public interface IFirstContract
            {
                int First();
            }
            """;
        const string secondSource = """
            public interface ISecondContract
            {
                int Second();
            }
            """;
        foreach (var fixture in new[] { forward, reverse })
        {
            await File.WriteAllTextAsync(
                Path.Combine(fixture.Root, "App", "App.cs"),
                appSource);
            await File.WriteAllTextAsync(
                Path.Combine(fixture.Root, "Library", "Library.cs"),
                firstSource);
            await File.WriteAllTextAsync(
                Path.Combine(fixture.Root, "LibraryTwo", "LibraryTwo.cs"),
                secondSource);
        }

        var loader = new RepositoryLoader();
        var forwardLoad = await loader.LoadAsync(new RepositoryLoadRequest(
            forward.Root,
            "App/App.csproj"));
        var reverseLoad = await loader.LoadAsync(new RepositoryLoadRequest(
            reverse.Root,
            "App/App.csproj"));
        Assert.Equal(RepositoryLoadStatus.Success, forwardLoad.Status);
        Assert.Equal(RepositoryLoadStatus.Success, reverseLoad.Status);
        await using var forwardSession =
            Assert.IsType<LoadedRepositorySession>(forwardLoad.Session);
        await using var reverseSession =
            Assert.IsType<LoadedRepositorySession>(reverseLoad.Session);
        var classifier = new SymbolClassifier();
        var forwardClassification = classifier.Classify(
            forwardSession,
            TargetProfile.ExternalApi);
        var reverseClassification = classifier.Classify(
            reverseSession,
            TargetProfile.ExternalApi);

        Assert.Equal(
            forwardLoad.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Stage}|{diagnostic.Code}|{diagnostic.Severity}"),
            reverseLoad.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Stage}|{diagnostic.Code}|{diagnostic.Severity}"));
        Assert.Equal(
            forwardClassification.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Stage}|{diagnostic.Code}|{diagnostic.Severity}"),
            reverseClassification.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Stage}|{diagnostic.Code}|{diagnostic.Severity}"));
        Assert.Equal(
            ClassificationRunStatus.Success,
            forwardClassification.Status);
        Assert.Equal(
            ClassificationRunStatus.Success,
            reverseClassification.Status);

        var forwardBytes = await RunClassificationProbeAsync(
            forward.Root,
            "external-api",
            "en-US",
            "UTC");
        var reverseBytes = await RunClassificationProbeAsync(
            reverse.Root,
            "external-api",
            "en-US",
            "UTC");
        Assert.Equal(forwardBytes, reverseBytes);
        Assert.Contains(
            "M:IFirstContract.First",
            forwardBytes,
            StringComparison.Ordinal);
        Assert.Contains(
            "M:ISecondContract.Second",
            forwardBytes,
            StringComparison.Ordinal);
        Assert.Equal(
            2,
            forwardClassification.ClassificationSet!.Relations.Count(
                relation => relation.RelationKind
                    == RelationKind.ImplicitInterfaceImplementation));
    }

    [Fact]
    public async Task CancellationReturnsNoSession()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var outcome = await new RepositoryLoader().LoadAsync(
            new RepositoryLoadRequest(fixture.Root, "App/App.csproj"),
            cancellation.Token);

        Assert.Equal(RepositoryLoadStatus.Cancelled, outcome.Status);
        Assert.Null(outcome.Session);
    }

    [Fact]
    public async Task RejectsSolutionContainingANonCSharpProjectBeforePartialLoad()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var mixed = Path.Combine(fixture.Root, "Mixed.slnx");
        await File.WriteAllTextAsync(
            mixed,
            """
            <Solution>
              <Project Path="App/App.csproj" />
              <Project Path="Other/Other.vbproj" />
            </Solution>
            """);
        Directory.CreateDirectory(Path.Combine(fixture.Root, "Other"));
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Root, "Other", "Other.vbproj"),
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");

        var outcome = await new RepositoryLoader().LoadAsync(new RepositoryLoadRequest(fixture.Root, mixed));

        Assert.Equal(RepositoryLoadStatus.Failure, outcome.Status);
        Assert.Equal("graph.solution-not-all-csharp", outcome.PrimaryFailure?.Code);
        Assert.Null(outcome.Session);
    }

    [Fact]
    public async Task RejectsSolutionWithNoCSharpProject()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var input = Path.Combine(fixture.Root, "NoCSharp.slnx");
        await File.WriteAllTextAsync(
            input,
            """<Solution><Project Path="Other/Other.vbproj" /></Solution>""");

        var outcome = await new RepositoryLoader().LoadAsync(
            new RepositoryLoadRequest(fixture.Root, input));

        Assert.Equal(RepositoryLoadStatus.Failure, outcome.Status);
        Assert.Equal("graph.solution-not-all-csharp", outcome.PrimaryFailure?.Code);
        Assert.Null(outcome.Session);
    }

    [Fact]
    public async Task RejectsMissingTransitiveProjectWithoutMetadataFallback()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var appProject = Path.Combine(fixture.Root, "App", "App.csproj");
        var text = await File.ReadAllTextAsync(appProject);
        await File.WriteAllTextAsync(
            appProject,
            text.Replace("../Library/Library.csproj", "../Missing/Missing.csproj", StringComparison.Ordinal));

        var outcome = await new RepositoryLoader().LoadAsync(
            new RepositoryLoadRequest(fixture.Root, "App/App.csproj"));

        Assert.Equal(RepositoryLoadStatus.Failure, outcome.Status);
        Assert.Equal("graph.project-not-found", outcome.PrimaryFailure?.Code);
        Assert.Null(outcome.Session);
    }

    [Fact]
    public async Task RejectsNonCSharpTransitiveProject()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "Other"));
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Root, "Other", "Other.vbproj"),
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        var appProject = Path.Combine(fixture.Root, "App", "App.csproj");
        var text = await File.ReadAllTextAsync(appProject);
        await File.WriteAllTextAsync(
            appProject,
            text.Replace("../Library/Library.csproj", "../Other/Other.vbproj", StringComparison.Ordinal));

        var outcome = await new RepositoryLoader().LoadAsync(
            new RepositoryLoadRequest(fixture.Root, "App/App.csproj"));

        Assert.Equal(RepositoryLoadStatus.Failure, outcome.Status);
        Assert.Equal("graph.project-not-csharp", outcome.PrimaryFailure?.Code);
        Assert.Null(outcome.Session);
    }

    [Fact]
    public async Task RejectsOutsideRootTransitiveProject()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        await using var outside = await LoaderFixture.CreateAsync();
        var appProject = Path.Combine(fixture.Root, "App", "App.csproj");
        var text = await File.ReadAllTextAsync(appProject);
        await File.WriteAllTextAsync(
            appProject,
            text.Replace(
                "../Library/Library.csproj",
                Path.Combine(outside.Root, "Library", "Library.csproj"),
                StringComparison.Ordinal));

        var outcome = await new RepositoryLoader().LoadAsync(
            new RepositoryLoadRequest(fixture.Root, "App/App.csproj"));

        Assert.Equal(RepositoryLoadStatus.Failure, outcome.Status);
        Assert.Equal("graph.project-outside-root", outcome.PrimaryFailure?.Code);
        Assert.Null(outcome.Session);
    }

    [Fact]
    public async Task RejectsCompileSourceOutsideRepositoryRoot()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        await using var outside = await LoaderFixture.CreateAsync();
        var outsideSource = Path.Combine(outside.Root, "Secret.cs");
        await File.WriteAllTextAsync(
            outsideSource,
            """public static class Secret { public const string Value = "outside"; }""");
        var projectPath = Path.Combine(fixture.Root, "App", "App.csproj");
        var projectText = await File.ReadAllTextAsync(projectPath);
        var relativeSource = Path.GetRelativePath(
                Path.GetDirectoryName(projectPath)!,
                outsideSource)
            .Replace('\\', '/');
        await File.WriteAllTextAsync(
            projectPath,
            projectText.Replace(
                "</Project>",
                $"""<ItemGroup><Compile Include="{relativeSource}" /></ItemGroup></Project>""",
                StringComparison.Ordinal));

        var outcome = await new RepositoryLoader().LoadAsync(
            new RepositoryLoadRequest(fixture.Root, "App/App.csproj"));

        Assert.Equal(RepositoryLoadStatus.Failure, outcome.Status);
        Assert.Equal("graph.source-outside-root", outcome.PrimaryFailure?.Code);
        Assert.Null(outcome.Session);
    }

    [Fact]
    public async Task RejectsOutsideSourceAddedByDesignTimeTarget()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        await using var outside = await LoaderFixture.CreateAsync();
        var outsideSource = Path.Combine(outside.Root, "TargetSecret.cs");
        await File.WriteAllTextAsync(
            outsideSource,
            """public static class TargetSecret { public const string Value = "outside"; }""");
        var projectPath = Path.Combine(fixture.Root, "App", "App.csproj");
        var projectText = await File.ReadAllTextAsync(projectPath);
        var relativeSource = Path.GetRelativePath(
                Path.GetDirectoryName(projectPath)!,
                outsideSource)
            .Replace('\\', '/');
        await File.WriteAllTextAsync(
            projectPath,
            projectText.Replace(
                "</Project>",
                $"""
                <Target Name="AddExternalSource" BeforeTargets="CoreCompile">
                  <ItemGroup><Compile Include="{relativeSource}" /></ItemGroup>
                </Target>
                </Project>
                """,
                StringComparison.Ordinal));

        var outcome = await new RepositoryLoader().LoadAsync(
            new RepositoryLoadRequest(fixture.Root, "App/App.csproj"));

        Assert.Equal(RepositoryLoadStatus.Failure, outcome.Status);
        Assert.Equal("graph.source-outside-root", outcome.PrimaryFailure?.Code);
        Assert.Null(outcome.Session);
    }

    [Fact]
    public async Task ProtectsTargetAddedAdditionalFileConsumedByGenerator()
    {
        await using var fixture = await LoaderFixture.CreateAsync(withGenerator: true);
        var inputPath = Path.Combine(fixture.Root, "App", "obj", "DynamicGeneratorInput.txt");
        await File.WriteAllTextAsync(inputPath, "initial");
        var projectPath = Path.Combine(fixture.Root, "App", "App.csproj");
        var projectText = await File.ReadAllTextAsync(projectPath);
        await File.WriteAllTextAsync(
            projectPath,
            projectText.Replace(
                "</Project>",
                """
                <Target Name="AddDynamicAdditionalFile" BeforeTargets="CoreCompile">
                  <ItemGroup><AdditionalFiles Include="obj/DynamicGeneratorInput.txt" /></ItemGroup>
                </Target>
                </Project>
                """,
                StringComparison.Ordinal));

        var baseline = await new RepositoryLoader().LoadAsync(
            new RepositoryLoadRequest(fixture.Root, "App/App.csproj"));
        Assert.Equal(RepositoryLoadStatus.Success, baseline.Status);
        await using (var session = Assert.IsType<LoadedRepositorySession>(baseline.Session))
        {
            Assert.Contains(session.GeneratedSources, fact =>
                fact.SourceText.Contains("FixtureDynamicAdditional", StringComparison.Ordinal)
                && fact.SourceText.Contains("\"initial\"", StringComparison.Ordinal));
        }

        var loader = new RepositoryLoader(stage =>
        {
            if (stage == LoaderStage.WorkspaceLoad)
            {
                File.WriteAllText(inputPath, "changed");
            }
        });
        var outcome = await loader.LoadAsync(
            new RepositoryLoadRequest(fixture.Root, "App/App.csproj"));

        Assert.Equal(RepositoryLoadStatus.Failure, outcome.Status);
        Assert.Equal("repository.protected-drift", outcome.PrimaryFailure?.Code);
        Assert.Null(outcome.Session);
    }

    [Fact]
    public async Task ProtectsTargetAddedAnalyzerConfigConsumedByGenerator()
    {
        await using var fixture = await LoaderFixture.CreateAsync(withGenerator: true);
        var configPath = Path.Combine(fixture.Root, "App", "obj", "Dynamic.globalconfig");
        await File.WriteAllTextAsync(
            configPath,
            """
            is_global = true
            contract_scribe_dynamic_option = initial
            """);
        var projectPath = Path.Combine(fixture.Root, "App", "App.csproj");
        var projectText = await File.ReadAllTextAsync(projectPath);
        await File.WriteAllTextAsync(
            projectPath,
            projectText.Replace(
                "</Project>",
                """
                <Target Name="AddDynamicAnalyzerConfig" BeforeTargets="CoreCompile">
                  <ItemGroup><EditorConfigFiles Include="obj/Dynamic.globalconfig" /></ItemGroup>
                </Target>
                </Project>
                """,
                StringComparison.Ordinal));

        var baseline = await new RepositoryLoader().LoadAsync(
            new RepositoryLoadRequest(fixture.Root, "App/App.csproj"));
        Assert.Equal(RepositoryLoadStatus.Success, baseline.Status);
        await using (var session = Assert.IsType<LoadedRepositorySession>(baseline.Session))
        {
            Assert.Contains(session.GeneratedSources, fact =>
                fact.SourceText.Contains("FixtureDynamicAnalyzerConfig", StringComparison.Ordinal)
                && fact.SourceText.Contains("\"initial\"", StringComparison.Ordinal));
        }

        var loader = new RepositoryLoader(stage =>
        {
            if (stage == LoaderStage.WorkspaceLoad)
            {
                File.WriteAllText(
                    configPath,
                    """
                    is_global = true
                    contract_scribe_dynamic_option = changed
                    """);
            }
        });
        var outcome = await loader.LoadAsync(
            new RepositoryLoadRequest(fixture.Root, "App/App.csproj"));

        Assert.Equal(RepositoryLoadStatus.Failure, outcome.Status);
        Assert.Equal("repository.protected-drift", outcome.PrimaryFailure?.Code);
        Assert.Null(outcome.Session);
    }

    [Fact]
    public async Task CancellationKeepsTargetAddedAdditionalFileDriftSecondary()
    {
        await using var fixture = await LoaderFixture.CreateAsync(withGenerator: true);
        var inputPath = Path.Combine(fixture.Root, "App", "obj", "DynamicGeneratorInput.txt");
        await File.WriteAllTextAsync(inputPath, "initial");
        var projectPath = Path.Combine(fixture.Root, "App", "App.csproj");
        var projectText = await File.ReadAllTextAsync(projectPath);
        await File.WriteAllTextAsync(
            projectPath,
            projectText.Replace(
                "</Project>",
                """
                <Target Name="MutateDynamicAdditionalFile" BeforeTargets="CoreCompile">
                  <WriteLinesToFile File="$(MSBuildProjectDirectory)/obj/DynamicGeneratorInput.txt"
                                    Lines="changed"
                                    Overwrite="true" />
                  <ItemGroup><AdditionalFiles Include="obj/DynamicGeneratorInput.txt" /></ItemGroup>
                </Target>
                </Project>
                """,
                StringComparison.Ordinal));
        using var cancellation = new CancellationTokenSource();
        var loader = new RepositoryLoader(stage =>
        {
            if (stage == LoaderStage.WorkspaceLoad)
            {
                cancellation.Cancel();
            }
        });

        var outcome = await loader.LoadAsync(
            new RepositoryLoadRequest(fixture.Root, "App/App.csproj"),
            cancellation.Token);

        Assert.Equal(RepositoryLoadStatus.Cancelled, outcome.Status);
        Assert.Equal("loader.cancelled", outcome.PrimaryFailure?.Code);
        Assert.Contains(outcome.SecondaryFacts, fact => fact.Code == "repository.protected-drift");
        Assert.Null(outcome.Session);
    }

    [Fact]
    public async Task HandledFailureKeepsTargetAddedAnalyzerConfigDriftSecondary()
    {
        await using var fixture = await LoaderFixture.CreateAsync(withGenerator: true);
        var configPath = Path.Combine(fixture.Root, "App", "obj", "Dynamic.globalconfig");
        await File.WriteAllTextAsync(
            configPath,
            """
            is_global = true
            contract_scribe_dynamic_option = initial
            """);
        var projectPath = Path.Combine(fixture.Root, "App", "App.csproj");
        var projectText = await File.ReadAllTextAsync(projectPath);
        await File.WriteAllTextAsync(
            projectPath,
            projectText.Replace(
                "</Project>",
                """
                <Target Name="MutateDynamicAnalyzerConfig" BeforeTargets="CoreCompile">
                  <ItemGroup>
                    <DynamicConfigLine Include="is_global = true" />
                    <DynamicConfigLine Include="contract_scribe_dynamic_option = changed" />
                  </ItemGroup>
                  <WriteLinesToFile File="$(MSBuildProjectDirectory)/obj/Dynamic.globalconfig"
                                    Lines="@(DynamicConfigLine)"
                                    Overwrite="true" />
                  <ItemGroup><EditorConfigFiles Include="obj/Dynamic.globalconfig" /></ItemGroup>
                </Target>
                </Project>
                """,
                StringComparison.Ordinal));
        var loader = new RepositoryLoader(stage =>
        {
            if (stage == LoaderStage.WorkspaceLoad)
            {
                throw LoaderException.Workspace("workspace.injected-failure");
            }
        });

        var outcome = await loader.LoadAsync(
            new RepositoryLoadRequest(fixture.Root, "App/App.csproj"));

        Assert.Equal(RepositoryLoadStatus.Failure, outcome.Status);
        Assert.Equal("workspace.injected-failure", outcome.PrimaryFailure?.Code);
        Assert.Contains(outcome.SecondaryFacts, fact => fact.Code == "repository.protected-drift");
        Assert.Null(outcome.Session);
    }

    [Fact]
    public async Task RejectsUnloadableSolutionProjectWithAStableGraphFact()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "Broken"));
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Root, "Broken", "Broken.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>""");
        var input = Path.Combine(fixture.Root, "Broken.slnx");
        await File.WriteAllTextAsync(
            input,
            """<Solution><Project Path="Broken/Broken.csproj" /></Solution>""");

        var outcome = await new RepositoryLoader().LoadAsync(
            new RepositoryLoadRequest(fixture.Root, input));

        Assert.Equal(RepositoryLoadStatus.Failure, outcome.Status);
        Assert.Equal("graph.project-unloadable", outcome.PrimaryFailure?.Code);
        Assert.Null(outcome.Session);
    }

    [Fact]
    public async Task RejectsDirectoryReparseEscape()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        await using var outside = await LoaderFixture.CreateAsync();
        var link = Path.Combine(fixture.Root, "Escaping");
        CreateDirectoryLink(link, Path.Combine(outside.Root, "Library"));
        var escapedSolution = Path.Combine(fixture.Root, "Escaped.slnx");
        await File.WriteAllTextAsync(
            escapedSolution,
            """<Solution><Project Path="Escaping/Library.csproj" /></Solution>""");

        var outcome = await new RepositoryLoader().LoadAsync(
            new RepositoryLoadRequest(fixture.Root, escapedSolution));

        Assert.Equal(RepositoryLoadStatus.Failure, outcome.Status);
        Assert.Contains(outcome.PrimaryFailure?.Code, new[] { "graph.project-outside-root", "input.path-outside-root" });
    }

    [Fact]
    public async Task AcceptsContainedDirectoryReparseForDirectProject()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var link = Path.Combine(fixture.Root, "Contained");
        CreateDirectoryLink(link, Path.Combine(fixture.Root, "Library"));

        var outcome = await new RepositoryLoader().LoadAsync(
            new RepositoryLoadRequest(fixture.Root, "Contained/Library.csproj"));

        Assert.True(
            outcome.Status == RepositoryLoadStatus.Success,
            $"{outcome.PrimaryFailure?.Stage}:{outcome.PrimaryFailure?.Code}");
        await using var session = Assert.IsType<LoadedRepositorySession>(outcome.Session);
        Assert.Single(session.Projects);
    }

    [Fact]
    public async Task RejectsDuplicatePhysicalProjectAliases()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var alias = Path.Combine(fixture.Root, "Alias");
        CreateDirectoryLink(alias, Path.Combine(fixture.Root, "Library"));
        var input = Path.Combine(fixture.Root, "Aliases.sln");
        await File.WriteAllTextAsync(
            input,
            """
            Microsoft Visual Studio Solution File, Format Version 12.00
            # Visual Studio Version 17
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Library", "Library\Library.csproj", "{33333333-3333-3333-3333-333333333333}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Alias", "Alias\Library.csproj", "{44444444-4444-4444-4444-444444444444}"
            EndProject
            Global
            EndGlobal
            """);

        var outcome = await new RepositoryLoader().LoadAsync(
            new RepositoryLoadRequest(fixture.Root, input));

        Assert.Equal(RepositoryLoadStatus.Failure, outcome.Status);
        Assert.Equal("graph.duplicate-project", outcome.PrimaryFailure?.Code);
        Assert.Null(outcome.Session);
    }

    [Fact]
    public async Task RejectsDuplicatePhysicalTransitiveReferenceAliases()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        CreateDirectoryLink(
            Path.Combine(fixture.Root, "Alias"),
            Path.Combine(fixture.Root, "Library"));
        var project = Path.Combine(fixture.Root, "App", "App.csproj");
        var text = await File.ReadAllTextAsync(project);
        await File.WriteAllTextAsync(
            project,
            text.Replace(
                "</ItemGroup>",
                """<ProjectReference Include="../Alias/Library.csproj" /></ItemGroup>""",
                StringComparison.Ordinal));

        var outcome = await new RepositoryLoader().LoadAsync(
            new RepositoryLoadRequest(fixture.Root, "App/App.csproj"));

        Assert.Equal(RepositoryLoadStatus.Failure, outcome.Status);
        Assert.Equal("graph.duplicate-project", outcome.PrimaryFailure?.Code);
    }

    [Fact]
    public async Task RejectsReferenceThatEscapesAndThenReentersTheRepository()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        await using var outside = await LoaderFixture.CreateAsync();
        var escape = Path.Combine(fixture.Root, "Escape");
        var reentry = Path.Combine(outside.Root, "Reentry");
        CreateDirectoryLink(escape, outside.Root);
        CreateDirectoryLink(reentry, Path.Combine(fixture.Root, "Library"));
        var appProject = Path.Combine(fixture.Root, "App", "App.csproj");
        var text = await File.ReadAllTextAsync(appProject);
        await File.WriteAllTextAsync(
            appProject,
            text.Replace(
                "../Library/Library.csproj",
                "../Escape/Reentry/Library.csproj",
                StringComparison.Ordinal));

        var outcome = await new RepositoryLoader().LoadAsync(
            new RepositoryLoadRequest(fixture.Root, "App/App.csproj"));

        Assert.Equal(RepositoryLoadStatus.Failure, outcome.Status);
        Assert.Equal("input.path-outside-root", outcome.PrimaryFailure?.Code);
        Assert.Null(outcome.Session);
    }

    [Fact]
    public async Task RejectsDirectLinkToLinkEscapeAndReentry()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        await using var outside = await LoaderFixture.CreateAsync();
        var outsideLink = Path.Combine(outside.Root, "BackInside");
        CreateDirectoryLink(outsideLink, Path.Combine(fixture.Root, "Library"));
        CreateDirectoryLink(Path.Combine(fixture.Root, "Chained"), outsideLink);

        var outcome = await new RepositoryLoader().LoadAsync(
            new RepositoryLoadRequest(fixture.Root, "Chained/Library.csproj"));

        Assert.Equal(RepositoryLoadStatus.Failure, outcome.Status);
        Assert.Equal("input.path-outside-root", outcome.PrimaryFailure?.Code);
    }

    [Fact]
    public void OutputContainsPinnedBuildHostButNoApplicationRootMsbuildRuntime()
    {
        var rootAssets = Directory.EnumerateFiles(AppContext.BaseDirectory, "Microsoft.Build*.dll", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Select(name => name!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["Microsoft.Build.Locator.dll"], rootAssets);
        Assert.NotEmpty(Directory.EnumerateFiles(
            Path.Combine(AppContext.BaseDirectory, "BuildHost-netcore"),
            "Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.dll",
            SearchOption.TopDirectoryOnly));
    }

    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(link);
        startInfo.ArgumentList.Add(target);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Junction setup failed.");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("Mandatory Windows junction setup failed.");
        }
    }

    private static async Task<string> RunClassificationProbeAsync(
        string repositoryRoot,
        string profile,
        string culture,
        string timeZone)
    {
        using var probe = StartLoaderProbe(
            repositoryRoot,
            "classification",
            extraArguments: [profile, culture, timeZone]);
        var stdout = probe.StandardOutput.ReadToEndAsync();
        var stderr = probe.StandardError.ReadToEndAsync();
        await probe.WaitForExitAsync();
        var output = await stdout;
        var error = await stderr;
        Assert.True(
            probe.ExitCode == 0,
            $"Classification probe failed with exit {probe.ExitCode}:{Environment.NewLine}{output}{Environment.NewLine}{error}");
        return output;
    }

    private static async Task<string> RunLoaderIsolationProbeAsync(
        string repositoryRoot,
        string inputPath,
        string mode,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var probeEnvironment = new Dictionary<string, string?>(
            environment ?? new Dictionary<string, string?>(),
            StringComparer.Ordinal)
        {
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "true",
        };
        var result = await OwnedProcessRunner.RunAsync(
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            workingDirectory,
            [LoaderProbePath(), repositoryRoot, inputPath, mode],
            TimeSpan.FromMinutes(2),
            environment: probeEnvironment);
        return result.StandardOutput;
    }

    private static Process StartLoaderProbe(
        string repositoryRoot,
        string mode,
        IReadOnlyList<string>? extraArguments = null)
    {
        var probePath = LoaderProbePath();
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(probePath);
        startInfo.ArgumentList.Add(repositoryRoot);
        startInfo.ArgumentList.Add("App/App.csproj");
        startInfo.ArgumentList.Add(mode);
        foreach (var argument in extraArguments ?? [])
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "true";
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Loader probe failed to start.");
    }

    private static string LoaderProbePath()
    {
        var configuration = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";
        var path = Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "ContractScribe.LoaderProbe",
            "bin",
            configuration,
            "net10.0",
            "ContractScribe.LoaderProbe.dll");
        return File.Exists(path)
            ? path
            : throw new InvalidOperationException("The loader probe was not built.");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ContractScribe.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Repository root not found.");
    }

}

internal sealed class LoaderFixture : IAsyncDisposable
{
    private static readonly LoaderFixtureCache SharedCache = new();
    private static readonly Lazy<Task<FixtureToolchainSnapshot>> ToolchainSnapshot =
        new(CaptureToolchainSnapshotAsync);

    static LoaderFixture()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            SharedCache.DisposeTemplatesAtProcessExit();
            if (ToolchainSnapshot.IsValueCreated
                && ToolchainSnapshot.Value.IsCompletedSuccessfully)
            {
                DeleteDirectory(ToolchainSnapshot.Value.Result.InputRoot);
            }
        };
    }

    private LoaderFixture(
        string root,
        string preparationId,
        string? shapeKey,
        string category)
    {
        Root = root;
        SolutionPath = Path.Combine(root, "Fixture.slnx");
        LegacySolutionPath = Path.Combine(root, "Fixture.sln");
        PreparationId = preparationId;
        ShapeKey = shapeKey;
        Category = category;
    }

    public string Root { get; }

    public string SolutionPath { get; }

    public string LegacySolutionPath { get; }

    internal string PreparationId { get; }

    internal string? ShapeKey { get; }

    internal string Category { get; }

    public Task PrepareEditorConfigAsync(CancellationToken cancellationToken = default) =>
        RunDotnetAsync(
            Root,
            ["msbuild", "App/App.csproj", "-target:GenerateMSBuildEditorConfigFile"],
            cancellationToken);

    public static async Task<LoaderFixture> CreateAsync(
        string? appProject = null,
        string? libraryProject = null,
        bool withGenerator = false,
        bool processSensitiveGenerator = false,
        bool selfObservingGenerator = false,
        bool withSecondDependency = false,
        bool reverseProjectReferences = false,
        bool manyOutputGenerator = false,
        bool collidingGeneratorOutputs = false,
        CancellationToken cancellationToken = default,
        LoaderFixtureCache? cache = null)
    {
        var category = GetReusableCategory(
            appProject,
            libraryProject,
            withGenerator,
            processSensitiveGenerator,
            selfObservingGenerator,
            withSecondDependency,
            reverseProjectReferences,
            manyOutputGenerator,
            collidingGeneratorOutputs);
        if (category is null)
        {
            return await CreateFreshOwnedAsync(
                appProject,
                libraryProject,
                withGenerator,
                processSensitiveGenerator,
                selfObservingGenerator,
                withSecondDependency,
                reverseProjectReferences,
                manyOutputGenerator,
                collidingGeneratorOutputs,
                cancellationToken,
                "fresh-custom").ConfigureAwait(false);
        }

        var shapeKey = await ComputeShapeKeyAsync(
            category,
            appProject,
            libraryProject,
            withGenerator,
            processSensitiveGenerator,
            selfObservingGenerator,
            withSecondDependency,
            reverseProjectReferences,
            manyOutputGenerator,
            collidingGeneratorOutputs,
            cancellationToken).ConfigureAwait(false);
        var selectedCache = cache ?? SharedCache;
        try
        {
            return await selectedCache.GetOrPrepareAndUseAsync(
                shapeKey,
                async cacheCancellation =>
                {
                    var prepared = await CreateFreshOwnedAsync(
                        appProject,
                        libraryProject,
                        withGenerator,
                        processSensitiveGenerator,
                        selfObservingGenerator,
                        withSecondDependency,
                        reverseProjectReferences,
                        manyOutputGenerator,
                        collidingGeneratorOutputs,
                        cacheCancellation,
                        $"template:{category}").ConfigureAwait(false);
                    try
                    {
                        await QualifyTemplateAsync(
                            prepared.Root,
                            processSensitiveGenerator,
                            cacheCancellation).ConfigureAwait(false);
                        return new LoaderFixtureTemplate(
                            prepared.Root,
                            prepared.PreparationId,
                            shapeKey,
                            category);
                    }
                    catch (Exception primary)
                    {
                        try
                        {
                            DeleteDirectoryStrict(prepared.Root);
                        }
                        catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                        {
                            throw new AggregateException(
                                "Template qualification failed and its prepared root could not be deleted.",
                                primary,
                                cleanup);
                        }
                        throw;
                    }
                },
                (template, useCancellation) => MaterializeAsync(
                    template,
                    processSensitiveGenerator,
                    useCancellation),
                cancellationToken).ConfigureAwait(false);
        }
        catch (LoaderFixtureCacheDisabledException)
        {
            return await CreateFreshOwnedAsync(
                appProject,
                libraryProject,
                withGenerator,
                processSensitiveGenerator,
                selfObservingGenerator,
                withSecondDependency,
                reverseProjectReferences,
                manyOutputGenerator,
                collidingGeneratorOutputs,
                cancellationToken,
                $"fresh-disabled:{category}").ConfigureAwait(false);
        }
        catch (TemplateBindingException)
        {
            await selectedCache.DisableAsync(shapeKey).ConfigureAwait(false);
            return await CreateFreshOwnedAsync(
                appProject,
                libraryProject,
                withGenerator,
                processSensitiveGenerator,
                selfObservingGenerator,
                withSecondDependency,
                reverseProjectReferences,
                manyOutputGenerator,
                collidingGeneratorOutputs,
                cancellationToken,
                $"fresh-qualification-fallback:{category}").ConfigureAwait(false);
        }
    }

    private static async Task<LoaderFixture> CreateFreshAsync(
        string? appProject = null,
        string? libraryProject = null,
        bool withGenerator = false,
        bool processSensitiveGenerator = false,
        bool selfObservingGenerator = false,
        bool withSecondDependency = false,
        bool reverseProjectReferences = false,
        bool manyOutputGenerator = false,
        bool collidingGeneratorOutputs = false,
        CancellationToken cancellationToken = default,
        string? rootOverride = null,
        string category = "fresh")
    {
        if (reverseProjectReferences && !withSecondDependency)
        {
            throw new ArgumentException(
                "Reference order can be reversed only for the two-dependency fixture.",
                nameof(reverseProjectReferences));
        }

        var root = rootOverride
            ?? Path.Combine(
                Path.GetTempPath(),
                "contract-scribe-issue36",
                Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "App"));
        Directory.CreateDirectory(Path.Combine(root, "Library"));
        if (withSecondDependency)
        {
            Directory.CreateDirectory(Path.Combine(root, "LibraryTwo"));
        }
        await File.WriteAllTextAsync(
            Path.Combine(root, "NuGet.Config"),
            """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="fixture" value="packages" />
              </packageSources>
            </configuration>
            """);
        PrepareLocalPackageSource(root);
        await File.WriteAllTextAsync(
            Path.Combine(root, "global.json"),
            """{"sdk":{"version":"10.0.102","rollForward":"latestFeature"}}""");
        await File.WriteAllTextAsync(
            Path.Combine(root, "Fixture.slnx"),
            withSecondDependency
                ?
                """
                <Solution>
                  <Project Path="App/App.csproj" />
                  <Project Path="Library/Library.csproj" />
                  <Project Path="LibraryTwo/LibraryTwo.csproj" />
                </Solution>
                """
                :
                """
                <Solution>
                  <Project Path="App/App.csproj" />
                  <Project Path="Library/Library.csproj" />
                </Solution>
                """);
        await File.WriteAllTextAsync(
            Path.Combine(root, "Fixture.sln"),
            withSecondDependency
                ?
                """
                Microsoft Visual Studio Solution File, Format Version 12.00
                # Visual Studio Version 17
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "App\App.csproj", "{11111111-1111-1111-1111-111111111111}"
                EndProject
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Library", "Library\Library.csproj", "{22222222-2222-2222-2222-222222222222}"
                EndProject
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "LibraryTwo", "LibraryTwo\LibraryTwo.csproj", "{33333333-3333-3333-3333-333333333333}"
                EndProject
                Global
                EndGlobal
                """
                :
                """
                Microsoft Visual Studio Solution File, Format Version 12.00
                # Visual Studio Version 17
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "App\App.csproj", "{11111111-1111-1111-1111-111111111111}"
                EndProject
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Library", "Library\Library.csproj", "{22222222-2222-2222-2222-222222222222}"
                EndProject
                Global
                EndGlobal
                """);
        var defaultAppProject = withSecondDependency
            ? reverseProjectReferences
                ?
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../LibraryTwo/LibraryTwo.csproj" />
                    <ProjectReference Include="../Library/Library.csproj" />
                  </ItemGroup>
                </Project>
                """
                :
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../Library/Library.csproj" />
                    <ProjectReference Include="../LibraryTwo/LibraryTwo.csproj" />
                  </ItemGroup>
                </Project>
                """
            :
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="../Library/Library.csproj" /></ItemGroup>
            </Project>
            """;
        await File.WriteAllTextAsync(
            Path.Combine(root, "App", "App.csproj"),
            PinFixtureFrameworkPacks(
                appProject
                ?? defaultAppProject));
        await File.WriteAllTextAsync(
            Path.Combine(root, "App", "App.cs"),
            """public static class App { public static string Value => "ok"; }""");
        await File.WriteAllTextAsync(
            Path.Combine(root, "Library", "Library.csproj"),
            PinFixtureFrameworkPacks(
                libraryProject
                ?? """
                   <Project Sdk="Microsoft.NET.Sdk">
                     <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                   </Project>
                   """));
        await File.WriteAllTextAsync(
            Path.Combine(root, "Library", "Library.cs"),
            """public static class Library { public static string Value => "ok"; }""");
        if (withSecondDependency)
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "LibraryTwo", "LibraryTwo.csproj"),
                PinFixtureFrameworkPacks(
                    """
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                    </Project>
                    """));
            await File.WriteAllTextAsync(
                Path.Combine(root, "LibraryTwo", "LibraryTwo.cs"),
                """public static class LibraryTwo { public static string Value => "ok"; }""");
        }
        if (withGenerator
            || processSensitiveGenerator
            || selfObservingGenerator
            || manyOutputGenerator
            || collidingGeneratorOutputs)
        {
            var generatorPath = ToolchainSnapshot.Value
                .GetAwaiter()
                .GetResult()
                .GeneratorPath;

            var analyzers = Path.Combine(root, "Analyzers");
            Directory.CreateDirectory(analyzers);
            File.Copy(generatorPath, Path.Combine(analyzers, Path.GetFileName(generatorPath)));
            var projectText = await File.ReadAllTextAsync(Path.Combine(root, "App", "App.csproj"));
            var processSensitiveConfiguration = processSensitiveGenerator
                ?
                """
                <PropertyGroup>
                  <ContractScribeTestGeneratorMarker>$(MSBuildProjectDirectory)/../generator.marker</ContractScribeTestGeneratorMarker>
                </PropertyGroup>
                <ItemGroup>
                  <CompilerVisibleProperty Include="ContractScribeTestGeneratorMarker" />
                </ItemGroup>
                """
                : string.Empty;
            var selfObservingConfiguration = selfObservingGenerator
                ?
                """
                <PropertyGroup>
                  <ContractScribeTestGeneratorSelfObserving>true</ContractScribeTestGeneratorSelfObserving>
                </PropertyGroup>
                <ItemGroup>
                  <CompilerVisibleProperty Include="ContractScribeTestGeneratorSelfObserving" />
                </ItemGroup>
                """
                : string.Empty;
            var manyOutputConfiguration = manyOutputGenerator
                ?
                """
                <PropertyGroup>
                  <ContractScribeTestGeneratorManyOutputs>128</ContractScribeTestGeneratorManyOutputs>
                </PropertyGroup>
                <ItemGroup>
                  <CompilerVisibleProperty Include="ContractScribeTestGeneratorManyOutputs" />
                </ItemGroup>
                """
                : string.Empty;
            var collisionConfiguration = collidingGeneratorOutputs
                ?
                """
                <PropertyGroup>
                  <ContractScribeTestGeneratorCollisions>true</ContractScribeTestGeneratorCollisions>
                </PropertyGroup>
                <ItemGroup>
                  <CompilerVisibleProperty Include="ContractScribeTestGeneratorCollisions" />
                </ItemGroup>
                """
                : string.Empty;
            projectText = projectText.Replace(
                "</Project>",
                $"""
                <ItemGroup><Analyzer Include="../Analyzers/ContractScribe.TestGenerator.dll" /></ItemGroup>
                {processSensitiveConfiguration}
                {selfObservingConfiguration}
                {manyOutputConfiguration}
                {collisionConfiguration}
                </Project>
                """,
                StringComparison.Ordinal);
            await File.WriteAllTextAsync(Path.Combine(root, "App", "App.csproj"), projectText);
        }

        await PrepareAsync(root, cancellationToken).ConfigureAwait(false);
        if (processSensitiveGenerator)
        {
            File.Delete(Path.Combine(root, "generator.marker"));
        }

        return new LoaderFixture(
            root,
            Guid.NewGuid().ToString("N"),
            shapeKey: null,
            category);
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return ValueTask.CompletedTask;
    }

    private static async Task PrepareAsync(
        string root,
        CancellationToken cancellationToken)
    {
        await RunDotnetAsync(
            root,
            ["restore", "Fixture.slnx", "--configfile", "NuGet.Config"],
            cancellationToken).ConfigureAwait(false);
        await RunDotnetAsync(
            root,
            ["build", "Fixture.slnx", "--no-restore"],
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<LoaderFixture> CreateFreshOwnedAsync(
        string? appProject,
        string? libraryProject,
        bool withGenerator,
        bool processSensitiveGenerator,
        bool selfObservingGenerator,
        bool withSecondDependency,
        bool reverseProjectReferences,
        bool manyOutputGenerator,
        bool collidingGeneratorOutputs,
        CancellationToken cancellationToken,
        string category)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "contract-scribe-issue80",
            $"consumer-{Guid.NewGuid():N}");
        try
        {
            return await CreateFreshAsync(
                appProject,
                libraryProject,
                withGenerator,
                processSensitiveGenerator,
                selfObservingGenerator,
                withSecondDependency,
                reverseProjectReferences,
                manyOutputGenerator,
                collidingGeneratorOutputs,
                cancellationToken,
                root,
                category).ConfigureAwait(false);
        }
        catch (Exception primary)
        {
            try
            {
                DeleteDirectoryStrict(root);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                throw new AggregateException(
                    "Fresh fixture creation failed and strict cleanup did not complete.",
                    primary,
                    cleanup);
            }
            throw;
        }
    }

    private static async Task<LoaderFixture> MaterializeAsync(
        LoaderFixtureTemplate template,
        bool processSensitiveGenerator,
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "contract-scribe-issue80",
            $"consumer-{Guid.NewGuid():N}");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            CopyDirectory(template.Root, root);
            RebaseProjectAssets(root, template.Root);
            DeleteTemplateBoundFiles(root, Path.GetFileName(template.Root));
            await RunDotnetAsync(
                root,
                ["build", "Fixture.slnx", "--no-restore"],
                cancellationToken).ConfigureAwait(false);
            RemoveTemplateOnlyRestoreDiagnostics(root);
            if (processSensitiveGenerator)
            {
                File.Delete(Path.Combine(root, "generator.marker"));
            }
            EnsureTemplateTokenAbsent(root, Path.GetFileName(template.Root));
            return new LoaderFixture(
                root,
                template.PreparationId,
                template.ShapeKey,
                template.Category);
        }
        catch (Exception primary)
        {
            try
            {
                DeleteDirectoryStrict(root);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                throw new AggregateException(
                    "Fixture materialization failed and strict cleanup did not complete.",
                    primary,
                    cleanup);
            }
            throw;
        }
    }

    private static async Task QualifyTemplateAsync(
        string templateRoot,
        bool processSensitiveGenerator,
        CancellationToken cancellationToken)
    {
        var qualificationRoot = Path.Combine(
            Path.GetDirectoryName(templateRoot)!,
            $"qualification-{Guid.NewGuid():N}");
        var unavailableRoot = $"{templateRoot}.offline-{Guid.NewGuid():N}";
        var templateMoved = false;
        Exception? primaryFailure = null;
        try
        {
            CopyDirectory(templateRoot, qualificationRoot);
            RebaseProjectAssets(qualificationRoot, templateRoot);
            DeleteTemplateBoundFiles(
                qualificationRoot,
                Path.GetFileName(templateRoot));
            await RunDotnetAsync(
                qualificationRoot,
                ["build", "Fixture.slnx", "--no-restore"],
                cancellationToken).ConfigureAwait(false);
            RemoveTemplateOnlyRestoreDiagnostics(qualificationRoot);
            if (processSensitiveGenerator)
            {
                File.Delete(Path.Combine(qualificationRoot, "generator.marker"));
            }
            EnsureTemplateTokenAbsent(
                qualificationRoot,
                Path.GetFileName(templateRoot));

            Directory.Move(templateRoot, unavailableRoot);
            templateMoved = true;
            await RunDotnetAsync(
                qualificationRoot,
                ["build", "Fixture.slnx", "--no-restore"],
                cancellationToken).ConfigureAwait(false);
            RemoveTemplateOnlyRestoreDiagnostics(qualificationRoot);
            EnsureTemplateTokenAbsent(
                qualificationRoot,
                Path.GetFileName(templateRoot));
        }
        catch (OperationCanceledException exception)
        {
            primaryFailure = exception;
        }
        catch (TemplateBindingException exception)
        {
            primaryFailure = exception;
        }
        catch (Exception exception) when (exception is
            IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or TimeoutException
            or ArgumentException
            or NotSupportedException
            or JsonException)
        {
            primaryFailure = new TemplateBindingException(
                "The prepared fixture did not qualify for isolated relocation.",
                exception);
        }

        try
        {
            if (templateMoved && Directory.Exists(unavailableRoot))
            {
                Directory.Move(unavailableRoot, templateRoot);
            }
            DeleteDirectoryStrict(qualificationRoot);
        }
        catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
        {
            throw primaryFailure is null
                ? cleanup
                : new AggregateException(
                    "Template qualification failed and strict cleanup did not complete.",
                    primaryFailure,
                    cleanup);
        }

        if (primaryFailure is not null)
        {
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }
    }

    private static string? GetReusableCategory(
        string? appProject,
        string? libraryProject,
        bool withGenerator,
        bool processSensitiveGenerator,
        bool selfObservingGenerator,
        bool withSecondDependency,
        bool reverseProjectReferences,
        bool manyOutputGenerator,
        bool collidingGeneratorOutputs)
    {
        // Reuse stays deliberately narrow. Custom XML, process-observing or
        // output-sensitive generators, dependency-order variants, and the
        // lifecycle probe retain fresh preparation until they each have a
        // direct relocation-and-behavior proof on both CI platforms.
        if (appProject is not null
            || libraryProject is not null
            || processSensitiveGenerator
            || selfObservingGenerator
            || withSecondDependency
            || reverseProjectReferences
            || manyOutputGenerator
            || collidingGeneratorOutputs)
        {
            return null;
        }
        return withGenerator
            ? "default-generator-ordinary"
            : "default-two-project";
    }

    private static async Task<string> ComputeShapeKeyAsync(
        string category,
        string? appProject,
        string? libraryProject,
        bool withGenerator,
        bool processSensitiveGenerator,
        bool selfObservingGenerator,
        bool withSecondDependency,
        bool reverseProjectReferences,
        bool manyOutputGenerator,
        bool collidingGeneratorOutputs,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var toolchain = await ToolchainSnapshot.Value
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        var fields = new List<(string Name, byte[] Value)>
        {
            ("format", "issue80-fixture-input-v1"u8.ToArray()),
            ("category", Encoding.UTF8.GetBytes(category)),
            ("appProject", Encoding.UTF8.GetBytes(appProject ?? string.Empty)),
            ("libraryProject", Encoding.UTF8.GetBytes(libraryProject ?? string.Empty)),
            ("withGenerator", BitConverter.GetBytes(withGenerator)),
            ("processSensitiveGenerator", BitConverter.GetBytes(processSensitiveGenerator)),
            ("selfObservingGenerator", BitConverter.GetBytes(selfObservingGenerator)),
            ("withSecondDependency", BitConverter.GetBytes(withSecondDependency)),
            ("reverseProjectReferences", BitConverter.GetBytes(reverseProjectReferences)),
            ("manyOutputGenerator", BitConverter.GetBytes(manyOutputGenerator)),
            ("collidingGeneratorOutputs", BitConverter.GetBytes(collidingGeneratorOutputs)),
            ("configuration", Encoding.UTF8.GetBytes(toolchain.Configuration)),
            ("dotnetHostPath", Encoding.UTF8.GetBytes(toolchain.DotnetHostPath)),
            ("dotnetHostSha256", Encoding.UTF8.GetBytes(toolchain.DotnetHostSha256)),
            ("sdkVersion", Encoding.UTF8.GetBytes(toolchain.SdkVersion)),
            ("msbuildPath", Encoding.UTF8.GetBytes(toolchain.MsbuildPath)),
            ("msbuildVersion", Encoding.UTF8.GetBytes(toolchain.MsbuildVersion)),
            ("msbuildSha256", Encoding.UTF8.GetBytes(toolchain.MsbuildSha256)),
            ("nugetPackages", Encoding.UTF8.GetBytes(
                Environment.GetEnvironmentVariable("NUGET_PACKAGES") ?? string.Empty)),
            ("fixtureAssemblySha256", Encoding.UTF8.GetBytes(
                toolchain.FixtureAssemblySha256)),
        };
        foreach (var package in toolchain.Packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            fields.Add((
                $"package:{package.LogicalName}:sha256",
                Encoding.UTF8.GetBytes(package.Sha256)));
        }
        if (withGenerator
            || processSensitiveGenerator
            || selfObservingGenerator
            || manyOutputGenerator
            || collidingGeneratorOutputs)
        {
            fields.Add(("generator:logicalName", Encoding.UTF8.GetBytes(
                Path.GetFileName(toolchain.GeneratorPath))));
            fields.Add(("generator:sha256", Encoding.UTF8.GetBytes(
                toolchain.GeneratorSha256)));
        }

        return ComputeFramedInputIdentity(fields);
    }

    internal static string ComputeFramedInputIdentity(
        IEnumerable<(string Name, byte[] Value)> fields)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var (name, value) in fields.OrderBy(field => field.Name, StringComparer.Ordinal))
            {
                var nameBytes = Encoding.UTF8.GetBytes(name);
                writer.Write(nameBytes.Length);
                writer.Write(nameBytes);
                writer.Write(value.Length);
                writer.Write(value);
            }
        }
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private static async Task<FixtureToolchainSnapshot> CaptureToolchainSnapshotAsync()
    {
        var dotnetHost = ResolveDotnetHost();
        var repositoryRoot = FindRepositoryRoot();
        var version = await OwnedProcessRunner.RunAsync(
            dotnetHost,
            repositoryRoot,
            ["--version"],
            TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        var sdkVersion = version.StandardOutput.Trim();
        var installedSdks = await OwnedProcessRunner.RunAsync(
            dotnetHost,
            repositoryRoot,
            ["--list-sdks"],
            TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        var sdkPrefix = $"{sdkVersion} [";
        var selectedSdk = installedSdks.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .SingleOrDefault(line =>
                line.StartsWith(sdkPrefix, StringComparison.Ordinal)
                && line.EndsWith(']'))
            ?? throw new InvalidOperationException(
                $"The selected SDK {sdkVersion} was not present in dotnet --list-sdks output.");
        var sdkRoot = selectedSdk[sdkPrefix.Length..^1];
        var msbuildPath = Path.Combine(
            sdkRoot,
            sdkVersion,
            "MSBuild.dll");
        if (!File.Exists(msbuildPath))
        {
            throw new InvalidOperationException(
                $"The selected SDK MSBuild assembly was not found: {msbuildPath}.");
        }
        var msbuildVersion = FileVersionInfo.GetVersionInfo(msbuildPath).FileVersion
            ?? string.Empty;
        var configuration = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";
        var inputRoot = Path.Combine(
            Path.GetTempPath(),
            "contract-scribe-issue80-inputs",
            Guid.NewGuid().ToString("N"));
        try
        {
            var packageRoot = Path.Combine(inputRoot, "packages");
            Directory.CreateDirectory(packageRoot);
            var packages = GetFixturePackagePaths()
                .Select(source =>
                {
                    if (!File.Exists(source))
                    {
                        throw new InvalidOperationException(
                            $"Declared fixture package is unavailable: {Path.GetFileName(source)}.");
                    }
                    var target = Path.Combine(packageRoot, Path.GetFileName(source));
                    File.Copy(source, target);
                    return new FixtureInputFile(
                        Path.GetFileName(source),
                        target,
                        HashFile(target));
                })
                .ToArray();
            var generatorSource = FindBuiltGeneratorPath();
            var generatorRoot = Path.Combine(inputRoot, "generator");
            Directory.CreateDirectory(generatorRoot);
            var generatorPath = Path.Combine(
                generatorRoot,
                Path.GetFileName(generatorSource));
            File.Copy(generatorSource, generatorPath);
            return new FixtureToolchainSnapshot(
                dotnetHost,
                HashFile(dotnetHost),
                sdkVersion,
                msbuildPath,
                msbuildVersion,
                HashFile(msbuildPath),
                configuration,
                inputRoot,
                packages,
                generatorPath,
                HashFile(generatorPath),
                HashFile(typeof(LoaderFixture).Assembly.Location));
        }
        catch (Exception primary)
        {
            try
            {
                DeleteDirectoryStrict(inputRoot);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                throw new AggregateException(
                    "Fixture input snapshot failed and strict cleanup did not complete.",
                    primary,
                    cleanup);
            }
            throw;
        }
    }

    private static string ResolveDotnetHost()
    {
        var configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }
        var executableName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        foreach (var candidate in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                     .Select(directory => Path.Combine(directory.Trim('"'), executableName)))
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }
        throw new InvalidOperationException("The resolved dotnet host was not found.");
    }

    private static IReadOnlyList<string> GetFixturePackagePaths()
    {
        var packageRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (string.IsNullOrWhiteSpace(packageRoot))
        {
            packageRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".nuget",
                "packages");
        }
        return new[]
        {
            "microsoft.aspnetcore.app.ref",
            "microsoft.netcore.app.ref",
            "microsoft.windowsdesktop.app.ref",
        }.Select(packageId =>
            Path.Combine(packageRoot, packageId, "9.0.0", $"{packageId}.9.0.0.nupkg"))
        .ToArray();
    }

    private static string FindBuiltGeneratorPath()
    {
        var configuration = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";
        var path = Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "ContractScribe.TestGenerator",
            "bin",
            configuration,
            "netstandard2.0",
            "ContractScribe.TestGenerator.dll");
        return File.Exists(path)
            ? Path.GetFullPath(path)
            : throw new InvalidOperationException("The test-owned generator helper was not built.");
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(
                destination,
                Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static void EnsureTemplateTokenAbsent(string root, string templateToken)
    {
        var tokens = TemplateTokenBytes(templateToken);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (IsImmutableSnapshotInput(root, file))
            {
                continue;
            }
            var bytes = File.ReadAllBytes(file);
            if (tokens.Any(token => ContainsSequence(bytes, token)))
            {
                throw new TemplateBindingException(
                    $"Materialized fixture retained the prepared-template token in {Path.GetRelativePath(root, file)}.");
            }
        }
    }

    private static byte[][] TemplateTokenBytes(string templateToken) =>
    [
        Encoding.UTF8.GetBytes(templateToken),
        Encoding.Unicode.GetBytes(templateToken),
        Encoding.BigEndianUnicode.GetBytes(templateToken),
    ];

    private static void RemoveTemplateOnlyRestoreDiagnostics(string root)
    {
        foreach (var path in Directory.EnumerateFiles(
                     root,
                     "*.nuget.dgspec.json",
                     SearchOption.AllDirectories))
        {
            File.Delete(path);
        }
    }

    private static void DeleteTemplateBoundFiles(string root, string templateToken)
    {
        var tokens = TemplateTokenBytes(templateToken);
        foreach (var file in Directory.EnumerateFiles(
                     root,
                     "*",
                     SearchOption.AllDirectories)
                     .ToArray())
        {
            if (IsImmutableSnapshotInput(root, file))
            {
                continue;
            }
            var bytes = File.ReadAllBytes(file);
            if (tokens.Any(token => ContainsSequence(bytes, token)))
            {
                File.Delete(file);
            }
        }
    }

    private static bool IsImmutableSnapshotInput(string root, string file)
    {
        var relative = Path.GetRelativePath(root, file);
        return relative.StartsWith(
                $"packages{Path.DirectorySeparatorChar}",
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal)
            || string.Equals(
                relative,
                Path.Combine("Analyzers", "ContractScribe.TestGenerator.dll"),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
    }

    private static void RebaseProjectAssets(string consumerRoot, string templateRoot)
    {
        foreach (var path in Directory.EnumerateFiles(
                     consumerRoot,
                     "project.assets.json",
                     SearchOption.AllDirectories))
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                WriteRebasedJson(
                    writer,
                    document.RootElement,
                    templateRoot,
                    consumerRoot);
            }
            File.WriteAllBytes(path, stream.ToArray());
        }
    }

    private static void WriteRebasedJson(
        Utf8JsonWriter writer,
        JsonElement element,
        string templateRoot,
        string consumerRoot)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(RebaseRootedValue(
                        property.Name,
                        templateRoot,
                        consumerRoot));
                    WriteRebasedJson(
                        writer,
                        property.Value,
                        templateRoot,
                        consumerRoot);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteRebasedJson(writer, item, templateRoot, consumerRoot);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(RebaseRootedValue(
                    element.GetString() ?? string.Empty,
                    templateRoot,
                    consumerRoot));
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidDataException(
                    $"Unsupported project.assets.json value kind: {element.ValueKind}.");
        }
    }

    private static string RebaseRootedValue(
        string value,
        string templateRoot,
        string consumerRoot)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!value.StartsWith(templateRoot, comparison))
        {
            return value;
        }
        if (value.Length > templateRoot.Length
            && value[templateRoot.Length] is not ('/' or '\\'))
        {
            return value;
        }
        return consumerRoot + value[templateRoot.Length..];
    }

    private static bool ContainsSequence(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> token)
    {
        if (token.IsEmpty || token.Length > bytes.Length)
        {
            return false;
        }
        for (var index = 0; index <= bytes.Length - token.Length; index++)
        {
            if (bytes.Slice(index, token.Length).SequenceEqual(token))
            {
                return true;
            }
        }
        return false;
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void DeleteDirectoryStrict(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        if (Directory.Exists(path))
        {
            throw new IOException($"Fixture directory still exists after cleanup: {path}");
        }
    }

    private static string PinFixtureFrameworkPacks(string projectText) =>
        projectText.Replace(
            "</Project>",
            """
            <PropertyGroup>
              <TargetLatestRuntimePatch>false</TargetLatestRuntimePatch>
              <RuntimeFrameworkVersion Condition="'$(TargetFramework)' == 'net9.0'">9.0.0</RuntimeFrameworkVersion>
              <PathMap>$(MSBuildProjectDirectory)=/_/contract-scribe-fixture</PathMap>
            </PropertyGroup>
            <ItemGroup>
              <KnownFrameworkReference Update="Microsoft.NETCore.App"
                                       Condition="'$(TargetFramework)' == 'net9.0'"
                                       TargetingPackVersion="9.0.0" />
              <KnownFrameworkReference Update="Microsoft.AspNetCore.App"
                                       Condition="'$(TargetFramework)' == 'net9.0'"
                                       TargetingPackVersion="9.0.0" />
              <KnownFrameworkReference Update="Microsoft.WindowsDesktop.App"
                                       Condition="'$(TargetFramework)' == 'net9.0'"
                                       TargetingPackVersion="9.0.0" />
            </ItemGroup>
            </Project>
            """,
            StringComparison.Ordinal);

    private static void PrepareLocalPackageSource(string root)
    {
        var destination = Path.Combine(root, "packages");
        Directory.CreateDirectory(destination);
        foreach (var package in ToolchainSnapshot.Value
                     .GetAwaiter()
                     .GetResult()
                     .Packages)
        {
            File.Copy(
                package.Path,
                Path.Combine(destination, package.LogicalName));
        }
    }

    private static async Task RunDotnetAsync(
        string root,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        var toolchain = await ToolchainSnapshot.Value
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        await OwnedProcessRunner.RunAsync(
            toolchain.DotnetHostPath,
            root,
            WithPersistentBuildServersDisabled(arguments),
            TimeSpan.FromMinutes(3),
            cancellationToken,
            new Dictionary<string, string?>
            {
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                ["DOTNET_NOLOGO"] = "true",
            }).ConfigureAwait(false);
    }

    internal static IReadOnlyList<string> WithPersistentBuildServersDisabled(
        IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            return arguments;
        }

        var option = arguments[0] switch
        {
            "restore" or "build" => "--disable-build-servers",
            "msbuild" => "-nodeReuse:false",
            _ => null,
        };
        if (option is null || arguments.Contains(option, StringComparer.OrdinalIgnoreCase))
        {
            return arguments;
        }

        return [.. arguments, option];
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ContractScribe.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private sealed record FixtureToolchainSnapshot(
        string DotnetHostPath,
        string DotnetHostSha256,
        string SdkVersion,
        string MsbuildPath,
        string MsbuildVersion,
        string MsbuildSha256,
        string Configuration,
        string InputRoot,
        IReadOnlyList<FixtureInputFile> Packages,
        string GeneratorPath,
        string GeneratorSha256,
        string FixtureAssemblySha256);

    private sealed record FixtureInputFile(
        string LogicalName,
        string Path,
        string Sha256);

    private sealed class TemplateBindingException : InvalidOperationException
    {
        public TemplateBindingException(string message)
            : base(message)
        {
        }

        public TemplateBindingException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
