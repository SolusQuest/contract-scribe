using System.Diagnostics;

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
    public async Task BuildHostProcessEndsAfterTheSuccessfulSessionIsDisposed()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var before = CurrentDotnetProcessIds();
        var observed = new System.Collections.Concurrent.ConcurrentDictionary<int, byte>();
        using var monitoring = new CancellationTokenSource();
        var monitor = MonitorDotnetProcessesAsync(observed, monitoring.Token);
        var loader = new RepositoryLoader();

        var outcome = await loader.LoadAsync(
            new RepositoryLoadRequest(fixture.Root, "App/App.csproj"));
        monitoring.Cancel();
        await monitor;
        var spawned = observed.Keys.Except(before).ToArray();
        var session = Assert.IsType<LoadedRepositorySession>(outcome.Session);
        Assert.NotEmpty(spawned);
        await session.DisposeAsync();

        Assert.True(
            SpinWait.SpinUntil(
                () => spawned.All(ProcessHasExited),
                TimeSpan.FromSeconds(10)),
            $"BuildHost process remained after disposal: {string.Join(',', spawned)}");
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("cancellation")]
    public async Task BuildHostProcessEndsAfterFailureOrCancellation(string mode)
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var before = CurrentDotnetProcessIds();
        var observed = new System.Collections.Concurrent.ConcurrentDictionary<int, byte>();
        using var monitoring = new CancellationTokenSource();
        var monitor = MonitorDotnetProcessesAsync(observed, monitoring.Token);
        using var cancellation = new CancellationTokenSource();
        var loader = new RepositoryLoader(stage =>
        {
            if (mode == "cancellation" && stage == LoaderStage.Compilation)
            {
                cancellation.Cancel();
            }
        });
        var generated = mode == "failure"
            ? new[]
            {
                new ToolGeneratedSourceInput(
                    "App/App.csproj",
                    "ContractScribe",
                    "FixtureTool",
                    "Broken",
                    "public class {"),
            }
            : null;

        var outcome = await loader.LoadAsync(
            new RepositoryLoadRequest(fixture.Root, "App/App.csproj", generated),
            cancellation.Token);
        monitoring.Cancel();
        await monitor;
        var spawned = observed.Keys.Except(before).ToArray();

        Assert.Equal(
            mode == "failure" ? RepositoryLoadStatus.Failure : RepositoryLoadStatus.Cancelled,
            outcome.Status);
        Assert.NotEmpty(spawned);
        Assert.True(
            SpinWait.SpinUntil(
                () => spawned.All(ProcessHasExited)
                    && !CurrentDotnetProcessIds().Except(before).Any(),
                TimeSpan.FromSeconds(10)),
            $"BuildHost process remained after {mode}: {string.Join(',', spawned)}");
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

    private static int[] CurrentDotnetProcessIds() =>
        Process.GetProcessesByName("dotnet")
            .Select(process =>
            {
                using (process)
                {
                    return process.Id;
                }
            })
            .Order()
            .ToArray();

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

    private static async Task MonitorDotnetProcessesAsync(
        System.Collections.Concurrent.ConcurrentDictionary<int, byte> observed,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var processId in CurrentDotnetProcessIds())
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
        bool withGenerator = false)
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
            appProject
            ?? """
               <Project Sdk="Microsoft.NET.Sdk">
                 <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                 <ItemGroup><ProjectReference Include="../Library/Library.csproj" /></ItemGroup>
               </Project>
               """);
        await File.WriteAllTextAsync(
            Path.Combine(root, "App", "App.cs"),
            """public static class App { public static string Value => "ok"; }""");
        await File.WriteAllTextAsync(
            Path.Combine(root, "Library", "Library.csproj"),
            libraryProject
            ?? """
               <Project Sdk="Microsoft.NET.Sdk">
                 <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
               </Project>
               """);
        await File.WriteAllTextAsync(
            Path.Combine(root, "Library", "Library.cs"),
            """public static class Library { public static string Value => "ok"; }""");
        if (withGenerator)
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
            projectText = projectText.Replace(
                "</Project>",
                """<ItemGroup><Analyzer Include="../Analyzers/ContractScribe.TestGenerator.dll" /></ItemGroup></Project>""",
                StringComparison.Ordinal);
            await File.WriteAllTextAsync(Path.Combine(root, "App", "App.csproj"), projectText);
        }

        await PrepareAsync(root);
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
