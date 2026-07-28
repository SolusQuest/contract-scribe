using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ContractScribe.Roslyn.IntegrationTests;

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
    public async Task LoadsLegacySlnFromAnUnrelatedWorkingDirectory()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = Path.GetTempPath();
            var outcome = await new RepositoryLoader().LoadAsync(
                new RepositoryLoadRequest(fixture.Root, fixture.LegacySolutionPath));

            Assert.True(
                outcome.Status == RepositoryLoadStatus.Success,
                $"{outcome.PrimaryFailure?.Stage}:{outcome.PrimaryFailure?.Code}");
            await using var session = Assert.IsType<LoadedRepositorySession>(outcome.Session);
            Assert.Equal(2, session.Projects.Count);
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
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
        var previous = Environment.GetEnvironmentVariable("TargetFramework");
        try
        {
            Environment.SetEnvironmentVariable("TargetFramework", "net10.0");
            var outcome = await new RepositoryLoader().LoadAsync(
                new RepositoryLoadRequest(fixture.Root, "App/App.csproj"));

            Assert.Equal(RepositoryLoadStatus.Failure, outcome.Status);
            Assert.Equal("graph.target-framework-not-single", outcome.PrimaryFailure?.Code);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TargetFramework", previous);
        }
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
    public async Task BuildHostProcessEndsAfterTheSuccessfulSessionIsDisposed()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var ready = Path.Combine(fixture.Root, "unrelated.ready");
        var release = Path.Combine(fixture.Root, "unrelated.release");
        using var unrelated = StartLoaderProbe(fixture.Root, "churn", ready, release);
        try
        {
            Assert.True(
                SpinWait.SpinUntil(
                    () => File.Exists(ready),
                    TimeSpan.FromSeconds(30)),
                "The unrelated loader probe did not reach its held session.");
            int[] unrelatedHosts = [];
            Assert.True(
                SpinWait.SpinUntil(
                    () => (unrelatedHosts = CurrentBuildHostDescendants(unrelated.Id)).Length != 0,
                    TimeSpan.FromSeconds(30)),
                "The unrelated loader probe did not expose its own BuildHost descendant.");

            var spawned = await RunLoaderProbeAsync(fixture.Root, "success");

            Assert.False(unrelated.HasExited);
            Assert.Empty(spawned.Intersect(unrelatedHosts));
        }
        finally
        {
            await File.WriteAllTextAsync(release, "release");
            await unrelated.WaitForExitAsync();
        }
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("cancellation")]
    public async Task BuildHostProcessEndsAfterFailureOrCancellation(string mode)
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        await RunLoaderProbeAsync(fixture.Root, mode);
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

    private static async Task<int[]> RunLoaderProbeAsync(string repositoryRoot, string mode)
    {
        using var probe = StartLoaderProbe(repositoryRoot, mode);
        var observed = new System.Collections.Concurrent.ConcurrentDictionary<int, byte>();
        using var monitoring = new CancellationTokenSource();
        var monitor = MonitorBuildHostDescendantsAsync(probe.Id, observed, monitoring.Token);
        var stdout = probe.StandardOutput.ReadToEndAsync();
        var stderr = probe.StandardError.ReadToEndAsync();
        await probe.WaitForExitAsync();
        monitoring.Cancel();
        await monitor;
        var spawned = observed.Keys.Order().ToArray();
        var output = await stdout;
        var error = await stderr;

        Assert.True(
            probe.ExitCode == 0,
            $"Loader probe failed for {mode} with exit {probe.ExitCode}:{Environment.NewLine}{output}{Environment.NewLine}{error}");
        Assert.NotEmpty(spawned);
        Assert.True(
            SpinWait.SpinUntil(
                () => spawned.All(ProcessHasExited),
                TimeSpan.FromSeconds(10)),
            $"BuildHost descendant remained after {mode}: {string.Join(',', spawned)}");
        return spawned;
    }

    private static Process StartLoaderProbe(
        string repositoryRoot,
        string mode,
        string? readyPath = null,
        string? releasePath = null)
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
        if (readyPath is not null && releasePath is not null)
        {
            startInfo.ArgumentList.Add(readyPath);
            startInfo.ArgumentList.Add(releasePath);
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

    private static string ExpectedBuildHostPath() =>
        Path.Combine(
            Path.GetDirectoryName(LoaderProbePath())!,
            "BuildHost-netcore",
            "Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.dll");

    private static int[] CurrentBuildHostDescendants(int ancestorProcessId)
    {
        var expectedPath = ExpectedBuildHostPath();
        return Process.GetProcessesByName("dotnet")
            .Select(process =>
            {
                using (process)
                {
                    return process.Id != ancestorProcessId
                        && IsDescendantOf(process.Id, ancestorProcessId)
                        && IsExpectedBuildHostProcess(process, expectedPath)
                            ? process.Id
                            : (int?)null;
                }
            })
            .Where(processId => processId is not null)
            .Select(processId => processId!.Value)
            .Order()
            .ToArray();
    }

    private static bool IsExpectedBuildHostProcess(Process process, string expectedPath)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                var commandLine = File.ReadAllBytes($"/proc/{process.Id}/cmdline");
                return Encoding.UTF8.GetString(commandLine)
                    .Split('\0', StringSplitOptions.RemoveEmptyEntries)
                    .Any(argument => string.Equals(
                        Path.GetFullPath(argument),
                        Path.GetFullPath(expectedPath),
                        StringComparison.Ordinal));
            }

            return process.Modules.Cast<ProcessModule>().Any(module =>
                string.Equals(
                    Path.GetFullPath(module.FileName),
                    Path.GetFullPath(expectedPath),
                    StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static bool IsDescendantOf(int processId, int ancestorProcessId)
    {
        var visited = new HashSet<int>();
        var current = processId;
        while (visited.Add(current))
        {
            var parent = ParentProcessId(current);
            if (parent == ancestorProcessId)
            {
                return true;
            }

            if (parent is null or <= 1)
            {
                return false;
            }

            current = parent.Value;
        }

        return false;
    }

    private static int? ParentProcessId(int processId)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                var stat = File.ReadAllText($"/proc/{processId}/stat");
                var afterName = stat[(stat.LastIndexOf(')') + 2)..]
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return int.Parse(afterName[1], System.Globalization.CultureInfo.InvariantCulture);
            }

            using var process = Process.GetProcessById(processId);
            var information = new ProcessBasicInformation();
            var status = NtQueryInformationProcess(
                process.Handle,
                0,
                ref information,
                Marshal.SizeOf<ProcessBasicInformation>(),
                out _);
            return status == 0
                ? checked((int)information.InheritedFromUniqueProcessId)
                : null;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or InvalidOperationException
                or OverflowException
                or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static bool ProcessHasExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static async Task MonitorBuildHostDescendantsAsync(
        int ancestorProcessId,
        System.Collections.Concurrent.ConcurrentDictionary<int, byte> observed,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var processId in CurrentBuildHostDescendants(ancestorProcessId))
                {
                    observed.TryAdd(processId, 0);
                }

                await Task.Delay(10, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
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
            ?? throw new InvalidOperationException("Repository root not found.");
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref ProcessBasicInformation processInformation,
        int processInformationLength,
        out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }
}

internal sealed class LoaderFixture : IAsyncDisposable
{
    private LoaderFixture(string root)
    {
        Root = root;
        SolutionPath = Path.Combine(root, "Fixture.slnx");
        LegacySolutionPath = Path.Combine(root, "Fixture.sln");
    }

    public string Root { get; }

    public string SolutionPath { get; }

    public string LegacySolutionPath { get; }

    public static async Task<LoaderFixture> CreateAsync(
        string? appProject = null,
        string? libraryProject = null,
        bool withGenerator = false,
        bool processSensitiveGenerator = false,
        bool selfObservingGenerator = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "contract-scribe-issue36", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "App"));
        Directory.CreateDirectory(Path.Combine(root, "Library"));
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
            """
            <Solution>
              <Project Path="App/App.csproj" />
              <Project Path="Library/Library.csproj" />
            </Solution>
            """);
        await File.WriteAllTextAsync(
            Path.Combine(root, "Fixture.sln"),
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
        await File.WriteAllTextAsync(
            Path.Combine(root, "App", "App.csproj"),
            PinFixtureFrameworkPacks(
                appProject
                ?? """
                   <Project Sdk="Microsoft.NET.Sdk">
                     <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                     <ItemGroup><ProjectReference Include="../Library/Library.csproj" /></ItemGroup>
                   </Project>
                   """));
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
        if (withGenerator || processSensitiveGenerator || selfObservingGenerator)
        {
            var repositoryRoot = FindRepositoryRoot();
            var configuration = AppContext.BaseDirectory.Contains(
                $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase)
                ? "Release"
                : "Debug";
            var generatorPath = Path.Combine(
                repositoryRoot,
                "tests",
                "ContractScribe.TestGenerator",
                "bin",
                configuration,
                "netstandard2.0",
                "ContractScribe.TestGenerator.dll");
            if (!File.Exists(generatorPath))
            {
                throw new InvalidOperationException("The test-owned generator helper was not built.");
            }

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
            projectText = projectText.Replace(
                "</Project>",
                $"""
                <ItemGroup><Analyzer Include="../Analyzers/ContractScribe.TestGenerator.dll" /></ItemGroup>
                {processSensitiveConfiguration}
                {selfObservingConfiguration}
                </Project>
                """,
                StringComparison.Ordinal);
            await File.WriteAllTextAsync(Path.Combine(root, "App", "App.csproj"), projectText);
        }

        await PrepareAsync(root);
        if (processSensitiveGenerator)
        {
            File.Delete(Path.Combine(root, "generator.marker"));
        }

        return new LoaderFixture(root);
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

    private static async Task PrepareAsync(string root)
    {
        await RunDotnetAsync(root, ["restore", "Fixture.slnx", "--configfile", "NuGet.Config"]);
        await RunDotnetAsync(root, ["build", "Fixture.slnx", "--no-restore"]);
    }

    private static string PinFixtureFrameworkPacks(string projectText) =>
        projectText.Replace(
            "</Project>",
            """
            <PropertyGroup>
              <TargetLatestRuntimePatch>false</TargetLatestRuntimePatch>
              <RuntimeFrameworkVersion Condition="'$(TargetFramework)' == 'net9.0'">9.0.0</RuntimeFrameworkVersion>
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
        var packageRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (string.IsNullOrWhiteSpace(packageRoot))
        {
            packageRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".nuget",
                "packages");
        }

        var destination = Path.Combine(root, "packages");
        Directory.CreateDirectory(destination);
        foreach (var packageId in new[]
                 {
                     "microsoft.aspnetcore.app.ref",
                     "microsoft.netcore.app.ref",
                     "microsoft.windowsdesktop.app.ref",
                 })
        {
            var package = Path.Combine(packageRoot, packageId, "9.0.0", $"{packageId}.9.0.0.nupkg");
            if (!File.Exists(package))
            {
                throw new InvalidOperationException($"Declared fixture package is unavailable: {packageId}.");
            }

            File.Copy(package, Path.Combine(destination, Path.GetFileName(package)));
        }
    }

    private static async Task RunDotnetAsync(string root, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "true";
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Fixture preparation failed to start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Fixture preparation failed: {await stdout}\n{await stderr}");
        }
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
}
