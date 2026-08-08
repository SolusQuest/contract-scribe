using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ContractScribe.Core.Hosting;

namespace ContractScribe.Roslyn.IntegrationTests;

[Collection("Audit CLI real process")]
public sealed class AuditCliProcessTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string Configuration = AppContext.BaseDirectory.Contains(
        $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
        StringComparison.OrdinalIgnoreCase)
        ? "Release"
        : "Debug";
    private static readonly string CliPath = Path.Join(
        RepositoryRoot,
        "src",
        "ContractScribe.Cli",
        "bin",
        Configuration,
        "net10.0",
        "ContractScribe.Cli.dll");
    private static readonly string SignalSenderPath = Path.Join(
        RepositoryRoot,
        "tests",
        "ContractScribe.ConsoleSignalSender",
        "bin",
        Configuration,
        "net10.0",
        "ContractScribe.ConsoleSignalSender.dll");
    private const string RequiredPolicy =
        "{\"defaultDecision\":\"required\",\"schemaVersion\":1,\"targetProfile\":\"profile.external-api\"}\n";
    private const string OptionalPolicy =
        "{\"defaultDecision\":\"optional\",\"schemaVersion\":1,\"targetProfile\":\"profile.external-api\"}\n";
    private const string ConflictPolicy =
        "{\"defaultDecision\":\"required\",\"rules\":[{\"decision\":\"required\",\"id\":\"part-one\",\"priority\":10,\"sourcePaths\":{\"include\":[\"App/Part1.cs\"]}},{\"decision\":\"forbidden\",\"id\":\"part-two\",\"priority\":20,\"sourcePaths\":{\"include\":[\"App/Part2.cs\"]}}],\"schemaVersion\":1,\"targetProfile\":\"profile.external-api\"}\n";

    [Fact]
    public async Task ExternalPublicationAndAbsoluteInput_AreAcceptedByTheProductionHost()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var outside = CreateOutsideDirectory();
        try
        {
            var outcome = await new ProductionAuditHost(
                    new HostBuildProvenance(new string('1', 40)))
                .RunAsync(
                    new ProductionAuditRequest(
                        fixture.Root,
                        Path.Join(fixture.Root, "App", "App.csproj"),
                        Encoding.UTF8.GetBytes(OptionalPolicy),
                        ResolvedPublicationTarget.ForExternalCli(
                            fixture.Root,
                            Path.Join(outside, "result.json"))),
                    new ProductionAuditHostControls());
            Assert.True(
                outcome.Terminal.ExecutionOutcome == HostExecutionOutcome.Succeeded,
                $"{outcome.LoaderFact?.Stage}:{outcome.LoaderFact?.Code}");
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task RetainedAndUsageClasses_AreRealProcessReturns()
    {
        Assert.True(File.Exists(CliPath), $"CLI not built: {CliPath}");
        foreach (var args in new[]
                 {
                     Array.Empty<string>(),
                     new[] { "--help" },
                     new[] { "--version" },
                     new[] { "doctor" },
                 })
        {
            var result = await RunAsync(args);
            Assert.Equal(0, result.ExitCode);
            Assert.Empty(result.Stderr);
        }

        var topLevel = await RunAsync(["bogus"]);
        Assert.Equal(2, topLevel.ExitCode);
        Assert.Empty(topLevel.Stdout);
        Assert.StartsWith("cli.usage.unknown-command: ", topLevel.Stderr, StringComparison.Ordinal);

        var auditUsage = await RunAsync(["audit", "--bogus"]);
        AssertEnvelope(auditUsage, 2, "usage", "usageClass", "unknown-option");

        var preflight = await RunAsync(
            AuditArgs(
                Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
                "missing.csproj",
                "missing-policy.json",
                Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "result.json")));
        AssertEnvelope(preflight, 4, "preflight", "executionClass", "invalid-input");
    }

    [Fact]
    public async Task EveryAuditDisposition_IsReturnedByTheRealProcess()
    {
        await using (var fixture = await LoaderFixture.CreateAsync())
        {
            await AssertDispositionAsync(
                fixture,
                appSource: "// no declarations",
                policy: RequiredPolicy,
                expectedDisposition: "no-results",
                expectedExit: 3);
            await AssertDispositionAsync(
                fixture,
                appSource: "/// <summary>Documented.</summary>\npublic static class App { }",
                policy: RequiredPolicy,
                expectedDisposition: "compliant",
                expectedExit: 0);
            await AssertDispositionAsync(
                fixture,
                appSource: "public static class App { }",
                policy: RequiredPolicy,
                expectedDisposition: "violations",
                expectedExit: 1);
        }

        await using (var fixture = await LoaderFixture.CreateAsync())
        {
            await File.WriteAllTextAsync(
                Path.Join(fixture.Root, "App", "Part1.cs"),
                "public static partial class Conflict { }");
            await File.WriteAllTextAsync(
                Path.Join(fixture.Root, "App", "Part2.cs"),
                "public static partial class Conflict { }");
            await AssertDispositionAsync(
                fixture,
                appSource: "// no declarations",
                policy: ConflictPolicy,
                expectedDisposition: "skipped-only",
                expectedExit: 3);
            await AssertDispositionAsync(
                fixture,
                appSource: "/// <summary>Documented.</summary>\npublic static class App { }",
                policy: ConflictPolicy,
                expectedDisposition: "compliant-with-skipped",
                expectedExit: 0);
            await AssertDispositionAsync(
                fixture,
                appSource: "public static class App { }",
                policy: ConflictPolicy,
                expectedDisposition: "violations-with-skipped",
                expectedExit: 1);
        }
    }

    [Fact]
    public async Task HostInvalidEnvironmentLoadAndAuditErrors_AreRealProcessReturns()
    {
        await using (var invalid = await LoaderFixture.CreateAsync())
        {
            var result = await RunFixtureAsync(invalid, "{}\n");
            AssertEnvelope(result, 4, "execution", "executionClass", "invalid-input");
        }

        await using (var unavailable = await LoaderFixture.CreateAsync())
        {
            await File.WriteAllTextAsync(
                Path.Join(unavailable.Root, "global.json"),
                "{\"sdk\":{\"version\":\"99.0.100\",\"rollForward\":\"disable\"}}\n");
            var result = await RunFixtureAsync(unavailable, OptionalPolicy);
            AssertEnvelope(
                result,
                4,
                "execution",
                "executionClass",
                "environment-unavailable");
        }

        await using (var loadFailure = await LoaderFixture.CreateAsync())
        {
            await File.WriteAllTextAsync(
                Path.Join(loadFailure.Root, "App", "App.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><broken></Project>");
            var result = await RunFixtureAsync(loadFailure, OptionalPolicy);
            AssertEnvelope(result, 5, "execution", "executionClass", "load-failure");
        }

        await using (var overBound = await LoaderFixture.CreateAsync())
        {
            var source = new StringBuilder();
            for (var index = 0; index < 25000; index++)
            {
                source.Append("public static class OverBound");
                source.Append(index.ToString("D5", System.Globalization.CultureInfo.InvariantCulture));
                source.AppendLine(" { }");
            }
            await File.WriteAllTextAsync(
                Path.Join(overBound.Root, "App", "App.cs"),
                source.ToString());
            await File.WriteAllTextAsync(
                Path.Join(overBound.Root, "Library", "Library.cs"),
                "// no declarations");
            var result = await RunFixtureAsync(overBound, OptionalPolicy, timeout: TimeSpan.FromMinutes(5));
            AssertEnvelope(result, 5, "execution", "executionClass", "audit-error");
            Assert.Contains(
                "host.result-validation.temporary-disk-bound",
                result.Stderr,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task PublicationFailure_IsARealProcessReturn()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var outside = CreateOutsideDirectory();
        try
        {
            var output = Path.Join(outside, "result.json");
            await File.WriteAllTextAsync(output, "prior");
            await File.WriteAllTextAsync(Path.Join(fixture.Root, "policy.json"), OptionalPolicy);

            if (OperatingSystem.IsWindows())
            {
                await using var locked = new FileStream(
                    output,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);
                var result = await RunAsync(AuditArgs(
                    fixture.Root,
                    "App/App.csproj",
                    "policy.json",
                    output));
                AssertEnvelope(
                    result,
                    5,
                    "execution",
                    "executionClass",
                    "publication-failure");
            }
            else
            {
                var originalMode = File.GetUnixFileMode(outside);
                try
                {
                    File.SetUnixFileMode(
                        outside,
                        UnixFileMode.UserRead | UnixFileMode.UserExecute);
                    var result = await RunAsync(AuditArgs(
                        fixture.Root,
                        "App/App.csproj",
                        "policy.json",
                        output));
                    AssertEnvelope(
                        result,
                        5,
                        "execution",
                        "executionClass",
                        "publication-failure");
                }
                finally
                {
                    File.SetUnixFileMode(outside, originalMode);
                }
            }
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task CooperativeSignals_UseThePlatformCorrectRealProcessPath()
    {
        if (OperatingSystem.IsWindows())
        {
            await AssertWindowsSignalAsync("ctrl-c");
            await AssertWindowsSignalAsync("ctrl-break");
        }
        else
        {
            await AssertUnixSignalAsync(2);
            await AssertUnixSignalAsync(15);
        }
    }

    [Fact]
    public async Task WorkspaceLoadTimeout_UsesTheRealBlockingGeneratorSeam()
    {
        await using var fixture = await CreateBlockingFixtureAsync();
        var outside = CreateOutsideDirectory();
        try
        {
            var marker = ConfigureBlockingGenerator(fixture);
            var result = await RunAsync(
                AuditArgs(fixture.Root, "App/App.csproj", "policy.json", Path.Join(outside, "result.json")),
                timeout: TimeSpan.FromMinutes(3));

            Assert.True(File.Exists(marker), "The real source generator never entered workspace load.");
            AssertEnvelope(result, 7, "execution", "executionClass", "timeout");
            Assert.Contains("host.workspace-load.timeout", result.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task AbruptTermination_IsDistinctFromEveryControlledExit()
    {
        await using var fixture = await CreateBlockingFixtureAsync();
        var outside = CreateOutsideDirectory();
        try
        {
            var marker = ConfigureBlockingGenerator(fixture);
            var output = Path.Join(outside, "result.json");
            using var process = Start(
                AuditArgs(fixture.Root, "App/App.csproj", "policy.json", output));
            await WaitForMarkerAsync(marker, process.Process, TimeSpan.FromMinutes(1));
            process.Process.Kill(entireProcessTree: true);
            await process.Process.WaitForExitAsync();
            var result = await process.CompleteAsync();

            Assert.DoesNotContain(result.ExitCode, Enumerable.Range(0, 8));
            Assert.Empty(result.Stdout);
            Assert.False(File.Exists(output));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    private static async Task AssertDispositionAsync(
        LoaderFixture fixture,
        string appSource,
        string policy,
        string expectedDisposition,
        int expectedExit)
    {
        await File.WriteAllTextAsync(Path.Join(fixture.Root, "App", "App.cs"), appSource);
        await File.WriteAllTextAsync(Path.Join(fixture.Root, "Library", "Library.cs"), "// no declarations");
        var result = await RunFixtureAsync(fixture, policy);
        AssertEnvelope(result, expectedExit, "audit", "disposition", expectedDisposition);
    }

    private static async Task<CliProcessResult> RunFixtureAsync(
        LoaderFixture fixture,
        string policy,
        TimeSpan? timeout = null)
    {
        await File.WriteAllTextAsync(Path.Join(fixture.Root, "policy.json"), policy);
        var outside = CreateOutsideDirectory();
        try
        {
            return await RunAsync(
                AuditArgs(
                    fixture.Root,
                    "App/App.csproj",
                    "policy.json",
                    Path.Join(outside, "result.json")),
                timeout);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    private static async Task<LoaderFixture> CreateBlockingFixtureAsync()
    {
        var fixture = await LoaderFixture.CreateAsync(withGenerator: true);
        await File.WriteAllTextAsync(Path.Join(fixture.Root, "policy.json"), OptionalPolicy);
        return fixture;
    }

    private static async Task<LoaderFixture> CreateSignalFixtureAsync()
    {
        var fixture = await LoaderFixture.CreateAsync();
        await File.WriteAllTextAsync(Path.Join(fixture.Root, "policy.json"), OptionalPolicy);
        var source = new StringBuilder();
        for (var index = 0; index < 50000; index++)
        {
            source.Append("public static class SignalTarget");
            source.Append(index.ToString("D5", System.Globalization.CultureInfo.InvariantCulture));
            source.AppendLine(" { }");
        }
        await File.WriteAllTextAsync(Path.Join(fixture.Root, "App", "App.cs"), source.ToString());
        await File.WriteAllTextAsync(
            Path.Join(fixture.Root, "Library", "Library.cs"),
            "// no declarations");
        return fixture;
    }

    private static string ConfigureBlockingGenerator(LoaderFixture fixture)
    {
        var marker = Path.Join(fixture.Root, "blocking-generator.marker");
        var projectPath = Path.Join(fixture.Root, "App", "App.csproj");
        var project = File.ReadAllText(projectPath);
        File.WriteAllText(
            projectPath,
            project.Replace(
                "</Project>",
                $"""
                 <PropertyGroup>
                   <ContractScribeTestGeneratorBlockingMarker>{marker}</ContractScribeTestGeneratorBlockingMarker>
                 </PropertyGroup>
                 <ItemGroup>
                   <CompilerVisibleProperty Include="ContractScribeTestGeneratorBlockingMarker" />
                 </ItemGroup>
                 </Project>
                 """,
                StringComparison.Ordinal));
        return marker;
    }

    private static async Task AssertUnixSignalAsync(int signal)
    {
        await using var fixture = await CreateSignalFixtureAsync();
        var outside = CreateOutsideDirectory();
        try
        {
            using var process = Start(AuditArgs(
                fixture.Root,
                "App/App.csproj",
                "policy.json",
                Path.Join(outside, "result.json")));
            await Task.Delay(TimeSpan.FromSeconds(15));
            Assert.False(process.Process.HasExited, "CLI exited before the Unix signal.");
            Assert.Equal(0, Kill(process.Process.Id, signal));
            await process.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var result = await process.CompleteAsync();
            AssertCancelled(result);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    private static async Task AssertWindowsSignalAsync(string signal)
    {
        await using var fixture = await CreateSignalFixtureAsync();
        var outside = CreateOutsideDirectory();
        try
        {
            var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
            var harnessArguments = new List<string>
            {
                "harness",
                signal,
                fixture.Root,
                dotnet,
                "15000",
                CliPath,
            };
            harnessArguments.AddRange(AuditArgs(
                fixture.Root,
                "App/App.csproj",
                "policy.json",
                Path.Join(outside, "result.json")));
            var result = await RunProgramAsync(
                SignalSenderPath,
                harnessArguments,
                TimeSpan.FromMinutes(2));
            AssertCancelled(result);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    private static void AssertCancelled(CliProcessResult result)
    {
        AssertEnvelope(result, 6, "execution", "executionClass", "cancelled");
        Assert.Equal(
            "cli.cancel.requested: a cancellation signal was received; cancelling\n",
            result.Stderr);
    }

    private static async Task WaitForMarkerAsync(
        string marker,
        Process process,
        TimeSpan timeout)
    {
        var started = Stopwatch.StartNew();
        while (!File.Exists(marker))
        {
            if (process.HasExited)
            {
                throw new Xunit.Sdk.XunitException(
                    $"CLI exited before the blocking workspace seam: {process.ExitCode}");
            }
            if (started.Elapsed > timeout)
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("The blocking workspace marker was not created.");
            }
            await Task.Delay(50);
        }
    }

    private static void AssertEnvelope(
        CliProcessResult result,
        int exitCode,
        string layer,
        string property,
        string value)
    {
        Assert.True(
            result.ExitCode == exitCode,
            $"Expected exit {exitCode}, got {result.ExitCode}. stdout={result.Stdout} stderr={result.Stderr}");
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(result.Stdout);
        }
        catch (JsonException exception)
        {
            throw new Xunit.Sdk.XunitException(
                $"Invalid envelope JSON. stdout={result.Stdout} stderr={result.Stderr} error={exception.Message}");
        }
        using (document)
        {
            Assert.Equal(layer, document.RootElement.GetProperty("terminalLayer").GetString());
            Assert.Equal(value, document.RootElement.GetProperty(property).GetString());
        }
        Assert.EndsWith("\n", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', result.Stdout);
        Assert.DoesNotContain('\r', result.Stderr);
    }

    private static string[] AuditArgs(
        string root,
        string input,
        string policy,
        string output) =>
        [
            "audit",
            "--repository-root", root,
            "--input", input,
            "--policy", policy,
            "--output", output,
        ];

    private static async Task<CliProcessResult> RunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan? timeout = null) =>
        await RunProgramAsync(CliPath, arguments, timeout ?? TimeSpan.FromMinutes(2));

    private static async Task<CliProcessResult> RunProgramAsync(
        string assembly,
        IReadOnlyList<string> arguments,
        TimeSpan timeout)
    {
        using var running = StartProgram(assembly, arguments);
        await running.Process.WaitForExitAsync().WaitAsync(timeout);
        return await running.CompleteAsync();
    }

    private static RunningProcess Start(IReadOnlyList<string> arguments) =>
        StartProgram(CliPath, arguments);

    private static RunningProcess StartProgram(
        string assembly,
        IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = RepositoryRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(assembly);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "true";
        return new RunningProcess(
            Process.Start(startInfo)
            ?? throw new InvalidOperationException("CLI process failed to start."));
    }

    private static string CreateOutsideDirectory()
    {
        var path = Path.Join(
            Path.GetTempPath(),
            "contract-scribe-cli-output",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Join(directory.FullName, "ContractScribe.slnx")))
            {
                return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int processId, int signal);

    private sealed record CliProcessResult(int ExitCode, string Stdout, string Stderr);

    private sealed class RunningProcess(Process process) : IDisposable
    {
        private readonly Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        private readonly Task<string> stderr = process.StandardError.ReadToEndAsync();

        public Process Process { get; } = process;

        public async Task<CliProcessResult> CompleteAsync() =>
            new(Process.ExitCode, await stdout, await stderr);

        public void Dispose() => Process.Dispose();
    }

}

[CollectionDefinition("Audit CLI real process", DisableParallelization = true)]
public sealed class AuditCliProcessCollection;
