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
    public async Task RejectsLexicalEscapeBeforeLoading()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var outcome = await new RepositoryLoader().LoadAsync(
            new RepositoryLoadRequest(fixture.Root, "../outside.csproj"));

        Assert.Equal(RepositoryLoadStatus.Failure, outcome.Status);
        Assert.Equal("input.path-outside-root", outcome.PrimaryFailure?.Code);
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
            fact.ProducerId.StartsWith("sgp-", StringComparison.Ordinal)
            && fact.OutputId.StartsWith("sgo-", StringComparison.Ordinal)
            && fact.SourceText.Contains("FixtureGenerated", StringComparison.Ordinal));
        Assert.Contains(session.GeneratedSources, fact =>
            fact.ProducerId.StartsWith("tgp-", StringComparison.Ordinal)
            && fact.OutputId.StartsWith("tgo-", StringComparison.Ordinal)
            && fact.SourceText.Contains("ToolGenerated", StringComparison.Ordinal));
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
