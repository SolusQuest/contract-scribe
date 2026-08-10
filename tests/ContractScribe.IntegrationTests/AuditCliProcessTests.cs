using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContractScribe.ContractBaselineProbe;
using ContractScribe.Core;
using ContractScribe.Core.Hosting;

namespace ContractScribe.Roslyn.IntegrationTests;

[Collection("Integration process lane 1")]
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
    private static readonly string LoaderProbePath = Path.Join(
        RepositoryRoot,
        "tests",
        "ContractScribe.LoaderProbe",
        "bin",
        Configuration,
        "net10.0",
        "ContractScribe.LoaderProbe.dll");
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
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
    public async Task SelectedToolchainPublicationFailure_IsARealProcessReturn()
    {
        await using var fixture = await CreateBlockingFixtureAsync();
        var outside = CreateOutsideDirectory();
        try
        {
            var output = Path.Join(outside, "result.json");
            var staging = Path.Join(outside, ".audit-result.json.contractscribe-stage");
            var release = Path.Join(
                fixture.Root,
                "App",
                "obj",
                "contracts-scribe-test",
                "blocking-generator.release");
            var marker = ConfigureBlockingGenerator(fixture, release);
            await fixture.PrepareEditorConfigAsync();
            var before = RepositoryInventory.Capture(fixture.Root, CancellationToken.None);
            using var process = Start(AuditArgs(
                fixture.Root,
                "App/App.csproj",
                "policy.json",
                output));
            CliProcessResult result;
            try
            {
                await WaitForMarkerAsync(marker, process.Process, TimeSpan.FromMinutes(1));
                Assert.False(File.Exists(output));
                await File.WriteAllTextAsync(output, "competing-result");
                await File.WriteAllTextAsync(release, "continue");
                await process.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
                result = await process.CompleteAsync();
            }
            finally
            {
                if (!process.Process.HasExited)
                {
                    process.Process.Kill(entireProcessTree: true);
                    await process.Process.WaitForExitAsync();
                }
            }

            Assert.True(
                result.Stdout.Contains("publication-failure", StringComparison.Ordinal),
                $"stdout={result.Stdout} stderr={result.Stderr} changed={string.Join(',', RepositoryInventory.ChangedPaths(before, RepositoryInventory.Capture(fixture.Root, CancellationToken.None)))}");
            AssertEnvelope(
                result,
                5,
                "execution",
                "executionClass",
                "publication-failure");
            Assert.Contains(
                "host.publication.finalization-failed",
                result.Stderr,
                StringComparison.Ordinal);
            using var envelope = JsonDocument.Parse(result.Stdout);
            Assert.True(envelope.RootElement.TryGetProperty("toolchain", out _));
            Assert.Equal("competing-result", await File.ReadAllTextAsync(output));
            Assert.False(File.Exists(staging));
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

    internal static async Task AssertWorkspaceLoadTimeoutUsesTheRealBlockingGeneratorSeamAsync()
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
    public async Task CanonicalArtifactAndEnvelope_AreIdenticalAcrossFreshProcesses()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        await File.WriteAllTextAsync(Path.Join(fixture.Root, "policy.json"), OptionalPolicy);
        var firstDirectory = CreateOutsideDirectory();
        var secondDirectory = CreateOutsideDirectory();
        try
        {
            var firstOutput = Path.Join(firstDirectory, "first.json");
            var secondOutput = Path.Join(secondDirectory, "second.json");
            var absoluteInput = Path.Join(fixture.Root, "App", "App.csproj");
            var absolutePolicy = Path.Join(fixture.Root, "policy.json");
            var first = await RunPublishedAsync(
                AuditArgs(fixture.Root, absoluteInput, absolutePolicy, firstOutput),
                firstOutput,
                firstDirectory,
                new Dictionary<string, string?>
                {
                    ["DOTNET_CLI_UI_LANGUAGE"] = "en-US",
                    ["LANG"] = "en_US.UTF-8",
                    ["TZ"] = "UTC",
                });
            var second = await RunPublishedAsync(
                AuditArgs(fixture.Root, absoluteInput, absolutePolicy, secondOutput),
                secondOutput,
                secondDirectory,
                new Dictionary<string, string?>
                {
                    ["DOTNET_CLI_UI_LANGUAGE"] = "fr-FR",
                    ["LANG"] = "fr_FR.UTF-8",
                    ["TZ"] = "Pacific/Auckland",
                });

            AssertEnvelope(first, 0, "audit", "disposition", "compliant-with-skipped");
            AssertEnvelope(second, 0, "audit", "disposition", "compliant-with-skipped");
            Assert.Equal(first.PublishedOutput, second.PublishedOutput);
            Assert.Equal(first.StdoutBytes, second.StdoutBytes);
            Assert.Equal(first.StderrBytes, second.StderrBytes);
        }
        finally
        {
            Directory.Delete(firstDirectory, recursive: true);
            Directory.Delete(secondDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData("App/App.csproj")]
    [InlineData("Fixture.sln")]
    [InlineData("Fixture.slnx")]
    public async Task SupportedInputKinds_RunFromAnUnrelatedWorkingDirectory(
        string input)
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        await File.WriteAllTextAsync(Path.Join(fixture.Root, "policy.json"), OptionalPolicy);
        var workingDirectory = CreateOutsideDirectory();
        try
        {
            var relativeRoot = Path.GetRelativePath(workingDirectory, fixture.Root);
            var output = Path.Join(workingDirectory, "result.json");
            var result = await RunPublishedAsync(
                AuditArgs(relativeRoot, input, "policy.json", "result.json"),
                output,
                workingDirectory);

            AssertEnvelope(result, 0, "audit", "disposition", "compliant-with-skipped");
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task MissingRestoreAssets_FailWithoutRestoreCredentialOrProtectedWrites()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var project = Path.Join(fixture.Root, "App", "App.csproj");
        var source = Path.Join(fixture.Root, "App", "App.cs");
        var assets = Path.Join(fixture.Root, "App", "obj", "project.assets.json");
        var projectBefore = await File.ReadAllBytesAsync(project);
        var sourceBefore = await File.ReadAllBytesAsync(source);
        File.Delete(assets);
        await File.WriteAllTextAsync(Path.Join(fixture.Root, "policy.json"), OptionalPolicy);
        var outside = CreateOutsideDirectory();
        var marker = "credential-marker-" + Guid.NewGuid().ToString("N");
        try
        {
            var output = Path.Join(outside, "result.json");
            var result = await RunAsync(
                AuditArgs(fixture.Root, "App/App.csproj", "policy.json", output),
                environment: new Dictionary<string, string?>
                {
                    ["CONTRACTSCRIBE_TEST_CREDENTIAL"] = marker,
                    ["NUGET_AUTH_TOKEN"] = marker,
                });

            AssertEnvelope(result, 5, "execution", "executionClass", "load-failure");
            Assert.Contains("graph.restore-assets-missing", result.Stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(marker, result.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain(marker, result.Stderr, StringComparison.Ordinal);
            Assert.False(File.Exists(assets));
            Assert.False(File.Exists(output));
            Assert.Equal(projectBefore, await File.ReadAllBytesAsync(project));
            Assert.Equal(sourceBefore, await File.ReadAllBytesAsync(source));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task RealProcessPreflight_RejectsEscapesAndNoFollowSpecialEntries()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        await File.WriteAllTextAsync(Path.Join(fixture.Root, "policy.json"), OptionalPolicy);
        var outside = CreateOutsideDirectory();
        try
        {
            var escaping = await RunAsync(AuditArgs(
                fixture.Root,
                "../outside/missing.csproj",
                "policy.json",
                Path.Join(outside, "escape.json")));
            AssertEnvelope(escaping, 4, "preflight", "executionClass", "invalid-input");
            Assert.StartsWith(
                "cli.preflight.input-escape: ",
                escaping.Stderr,
                StringComparison.Ordinal);

            if (OperatingSystem.IsWindows())
            {
                var device = await RunAsync(AuditArgs(
                    fixture.Root,
                    "App/NUL.csproj",
                    "policy.json",
                    Path.Join(outside, "device.json")));
                AssertEnvelope(device, 4, "preflight", "executionClass", "invalid-input");
                Assert.StartsWith("cli.preflight.input: ", device.Stderr, StringComparison.Ordinal);
            }
            else
            {
                var socketPath = Path.Join(fixture.Root, "App", "service.csproj");
                using var socket = new System.Net.Sockets.Socket(
                    System.Net.Sockets.AddressFamily.Unix,
                    System.Net.Sockets.SocketType.Stream,
                    System.Net.Sockets.ProtocolType.Unspecified);
                socket.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(socketPath));
                var special = await RunAsync(AuditArgs(
                    fixture.Root,
                    "App/service.csproj",
                    "policy.json",
                    Path.Join(outside, "special.json")));
                AssertEnvelope(special, 4, "preflight", "executionClass", "invalid-input");
                Assert.StartsWith("cli.preflight.input: ", special.Stderr, StringComparison.Ordinal);

                var finalLink = Path.Join(outside, "linked-output.json");
                File.CreateSymbolicLink(finalLink, Path.Join(outside, "missing-target.json"));
                var linked = await RunAsync(AuditArgs(
                    fixture.Root,
                    "App/App.csproj",
                    "policy.json",
                    finalLink));
                AssertEnvelope(linked, 4, "preflight", "executionClass", "invalid-input");
                Assert.StartsWith(
                    "cli.preflight.output-reparse: ",
                    linked.Stderr,
                    StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task WindowsSdkResolution_RestoresNativeManagedAndChildStdout()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using var fixture = await LoaderFixture.CreateAsync();
        var result = await RunProgramAsync(
            LoaderProbePath,
            [fixture.Root, "App/App.csproj", "stdout-after-success"],
            TimeSpan.FromMinutes(2));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("native-stdout-ok\n", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("child-stdout-ok:", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("managed-stdout-ok\n", result.Stdout, StringComparison.Ordinal);
        Assert.Matches("child-stdout-ok:\\d+\\.\\d+\\.\\d+", result.Stdout);
        Assert.Empty(result.Stderr);
    }

    [Fact]
    public async Task RepositoryControlledManagedAndChildOutput_IsSuppressed()
    {
        await using var fixture = await LoaderFixture.CreateAsync(withGenerator: true);
        await File.WriteAllTextAsync(Path.Join(fixture.Root, "policy.json"), OptionalPolicy);
        var marker = ConfigureConsoleOutputGenerator(fixture);
        await fixture.PrepareEditorConfigAsync();
        var before = RepositoryInventory.Capture(fixture.Root, CancellationToken.None);
        var outside = CreateOutsideDirectory();
        try
        {
            var output = Path.Join(outside, "result.json");
            var result = await RunPublishedAsync(
                AuditArgs(fixture.Root, "App/App.csproj", "policy.json", output),
                output);

            Assert.True(
                result.ExitCode == 0,
                $"stdout={result.Stdout} stderr={result.Stderr} changed={string.Join(',', RepositoryInventory.ChangedPaths(before, RepositoryInventory.Capture(fixture.Root, CancellationToken.None)))}");
            Assert.DoesNotContain(marker, result.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain(marker, result.Stderr, StringComparison.Ordinal);
            AssertEnvelope(result, 0, "audit", "disposition", "compliant-with-skipped");
            var artifact = Assert.IsType<byte[]>(result.PublishedOutput);
            _ = AuditParser.Parse(artifact);
            using var document = JsonDocument.Parse(artifact);
            Assert.Equal(artifact, AuditResultCanonicalizer.Canonicalize(document.RootElement));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    internal static async Task AssertTerminalCommitRemainsAuthoritativeWhenPresentationWriteFailsAsync()
    {
        await using (var success = await CreateBlockingFixtureAsync())
        {
            var release = BlockingGeneratorReleasePath(success);
            var marker = ConfigureBlockingGenerator(success, release);
            await success.PrepareEditorConfigAsync();
            var outside = CreateOutsideDirectory();
            try
            {
                var output = Path.Join(outside, "result.json");
                var result = await RunWithBrokenStdoutAsync(
                    AuditArgs(success.Root, "App/App.csproj", "policy.json", output),
                    marker,
                    release,
                    competingOutput: null);

                AssertExternalAbnormalTermination(result.Termination);
                var artifact = await File.ReadAllBytesAsync(output);
                _ = AuditParser.Parse(artifact);
                using var document = JsonDocument.Parse(artifact);
                Assert.Equal(artifact, AuditResultCanonicalizer.Canonicalize(document.RootElement));
            }
            finally
            {
                Directory.Delete(outside, recursive: true);
            }
        }

        await using (var failure = await CreateBlockingFixtureAsync())
        {
            var release = BlockingGeneratorReleasePath(failure);
            var marker = ConfigureBlockingGenerator(failure, release);
            await failure.PrepareEditorConfigAsync();
            var outside = CreateOutsideDirectory();
            try
            {
                var output = Path.Join(outside, "result.json");
                var result = await RunWithBrokenStdoutAsync(
                    AuditArgs(failure.Root, "App/App.csproj", "policy.json", output),
                    marker,
                    release,
                    competingOutput: output);

                AssertExternalAbnormalTermination(result.Termination);
                Assert.Contains(
                    "host.publication.finalization-failed",
                    result.Stderr,
                    StringComparison.Ordinal);
                Assert.Equal("competing-result", await File.ReadAllTextAsync(output));
            }
            finally
            {
                Directory.Delete(outside, recursive: true);
            }
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
            await File.WriteAllTextAsync(output, "stale-authoritative-output");
            using var process = Start(
                AuditArgs(fixture.Root, "App/App.csproj", "policy.json", output));
            await WaitForMarkerAsync(marker, process.Process, TimeSpan.FromMinutes(1));
            Assert.False(
                File.Exists(output),
                "The stale output survived beyond the post-invalidation workspace marker.");
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
            var output = Path.Join(outside, "result.json");
            var result = await RunAsync(
                AuditArgs(
                    fixture.Root,
                    "App/App.csproj",
                    "policy.json",
                    output),
                timeout);
            return result with
            {
                PublishedOutput = File.Exists(output)
                    ? await File.ReadAllBytesAsync(output)
                    : null,
            };
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

    private static string BlockingGeneratorReleasePath(LoaderFixture fixture) =>
        Path.Join(
            fixture.Root,
            "App",
            "obj",
            "contracts-scribe-test",
            "blocking-generator.release");

    private static string ConfigureBlockingGenerator(
        LoaderFixture fixture,
        string? releaseMarker = null)
    {
        var markerDirectory = Path.Join(
            fixture.Root,
            "App",
            "obj",
            "contracts-scribe-test");
        Directory.CreateDirectory(markerDirectory);
        var marker = Path.Join(markerDirectory, "blocking-generator.marker");
        var escapedMarker = System.Security.SecurityElement.Escape(marker);
        var escapedRelease = releaseMarker is null
            ? null
            : System.Security.SecurityElement.Escape(releaseMarker);
        var projectPath = Path.Join(fixture.Root, "App", "App.csproj");
        var project = File.ReadAllText(projectPath);
        File.WriteAllText(
            projectPath,
            project.Replace(
                "</Project>",
                $"""
                 <PropertyGroup>
                   <ContractScribeTestGeneratorBlockingMarker>{escapedMarker}</ContractScribeTestGeneratorBlockingMarker>
                   {(
                       escapedRelease is null
                           ? string.Empty
                           : $"<ContractScribeTestGeneratorReleaseMarker>{escapedRelease}</ContractScribeTestGeneratorReleaseMarker>")}
                 </PropertyGroup>
                 <ItemGroup>
                   <CompilerVisibleProperty Include="ContractScribeTestGeneratorBlockingMarker" />
                   {(
                       releaseMarker is null
                           ? string.Empty
                           : "<CompilerVisibleProperty Include=\"ContractScribeTestGeneratorReleaseMarker\" />")}
                 </ItemGroup>
                 </Project>
                 """,
                StringComparison.Ordinal));
        return marker;
    }

    private static string ConfigureConsoleOutputGenerator(LoaderFixture fixture)
    {
        var marker = "repository-stream-marker-" + Guid.NewGuid().ToString("N");
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var projectPath = Path.Join(fixture.Root, "App", "App.csproj");
        var project = File.ReadAllText(projectPath);
        File.WriteAllText(
            projectPath,
            project.Replace(
                "</Project>",
                $"""
                 <PropertyGroup>
                   <ContractScribeTestGeneratorConsoleMarker>{marker}</ContractScribeTestGeneratorConsoleMarker>
                   <ContractScribeTestGeneratorChildProgram>{System.Security.SecurityElement.Escape(LoaderProbePath)}</ContractScribeTestGeneratorChildProgram>
                   <ContractScribeTestGeneratorDotnetHost>{System.Security.SecurityElement.Escape(dotnet)}</ContractScribeTestGeneratorDotnetHost>
                 </PropertyGroup>
                 <ItemGroup>
                   <CompilerVisibleProperty Include="ContractScribeTestGeneratorConsoleMarker" />
                   <CompilerVisibleProperty Include="ContractScribeTestGeneratorChildProgram" />
                   <CompilerVisibleProperty Include="ContractScribeTestGeneratorDotnetHost" />
                 </ItemGroup>
                 </Project>
                 """,
                StringComparison.Ordinal));
        return marker;
    }

    private static async Task AssertUnixSignalAsync(int signal)
    {
        await using var fixture = await CreateBlockingFixtureAsync();
        var outside = CreateOutsideDirectory();
        try
        {
            var marker = ConfigureBlockingGenerator(fixture);
            using var process = Start(AuditArgs(
                fixture.Root,
                "App/App.csproj",
                "policy.json",
                Path.Join(outside, "result.json")));
            await WaitForMarkerAsync(marker, process.Process, TimeSpan.FromMinutes(1));
            Assert.Equal(0, Kill(process.Process.Id, signal));
            if (signal == 2)
            {
                Assert.Equal(0, Kill(process.Process.Id, signal));
            }
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
        await using var fixture = await CreateBlockingFixtureAsync();
        var outside = CreateOutsideDirectory();
        try
        {
            var marker = ConfigureBlockingGenerator(fixture);
            var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
            var harnessArguments = new List<string>
            {
                "harness",
                signal,
                fixture.Root,
                dotnet,
                marker,
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
            var root = document.RootElement;
            Assert.Equal(layer, root.GetProperty("terminalLayer").GetString());
            Assert.Equal(value, root.GetProperty(property).GetString());
            Assert.Equal(1, root.GetProperty("envelopeVersion").GetInt32());
            var baseline = root.GetProperty("cliContractBaseline").GetString();
            var toolVersion = root.GetProperty("toolVersion").GetString();
            Assert.Matches("^[0-9a-f]{40}$", baseline!);
            Assert.EndsWith("+" + baseline, toolVersion, StringComparison.Ordinal);

            if (layer is "execution" or "audit")
            {
                Assert.Equal(baseline, root.GetProperty("sourceRevision").GetString());
            }

            var diagnosticCodes = root.GetProperty("diagnosticCodes")
                .EnumerateArray()
                .Select(item => item.GetString())
                .ToArray();
            var stderrCodes = result.Stderr
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line[..line.IndexOf(':')])
                .ToArray();
            Assert.Equal(diagnosticCodes, stderrCodes);
            Assert.Equal(ExpectedEnvelopeProperties(root), root.EnumerateObject()
                .Select(item => item.Name)
                .ToArray());

            if (layer == "audit")
            {
                AssertPublishedArtifact(result, root);
            }
        }
        Assert.EndsWith("\n", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', result.Stdout);
        Assert.DoesNotContain('\r', result.Stderr);
        Assert.False(result.StdoutBytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.False(result.StderrBytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
    }

    private static string[] ExpectedEnvelopeProperties(JsonElement root)
    {
        var common = new List<string>
        {
            "envelopeVersion",
            "terminalLayer",
            "cliContractBaseline",
            "toolVersion",
            "diagnosticCodes",
        };
        switch (root.GetProperty("terminalLayer").GetString())
        {
            case "usage":
                common.Add("usageClass");
                break;
            case "preflight":
                common.Add("executionClass");
                break;
            case "execution":
                common.Add("terminalState");
                common.Add("sourceRevision");
                if (root.TryGetProperty("toolchain", out _))
                {
                    common.Add("toolchain");
                }
                common.Add("executionClass");
                break;
            case "audit":
                common.AddRange(
                [
                    "terminalState",
                    "sourceRevision",
                    "toolchain",
                    "disposition",
                    "counts",
                    "resultDigest",
                    "outputCommit",
                ]);
                break;
        }
        return [.. common];
    }

    private static void AssertPublishedArtifact(
        CliProcessResult result,
        JsonElement envelope)
    {
        var bytes = Assert.IsType<byte[]>(result.PublishedOutput);
        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.Equal((byte)'\n', bytes[^1]);
        Assert.DoesNotContain((byte)'\r', bytes);
        _ = StrictUtf8.GetString(bytes);
        _ = AuditParser.Parse(bytes);
        using var artifact = JsonDocument.Parse(bytes);
        Assert.Equal(bytes, AuditResultCanonicalizer.Canonicalize(artifact.RootElement));

        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Assert.Equal(digest, envelope.GetProperty("resultDigest").GetString());
        var outputCommit = envelope.GetProperty("outputCommit");
        Assert.Equal(
            new[] { "status", "identity" },
            outputCommit.EnumerateObject().Select(item => item.Name).ToArray());
        Assert.Equal("committed", outputCommit.GetProperty("status").GetString());
        Assert.Equal(digest, outputCommit.GetProperty("identity").GetString());

        var counts = envelope.GetProperty("counts");
        Assert.Equal(
            new[] { "compliant", "violation", "skipped" },
            counts.EnumerateObject().Select(item => item.Name).ToArray());
        var grouped = artifact.RootElement.GetProperty("results")
            .EnumerateArray()
            .GroupBy(item => item.GetProperty("auditOutcome").GetString())
            .ToDictionary(group => group.Key!, group => group.Count(), StringComparer.Ordinal);
        Assert.Equal(
            grouped.GetValueOrDefault("audit.outcome.compliant"),
            counts.GetProperty("compliant").GetInt32());
        Assert.Equal(
            grouped.GetValueOrDefault("audit.outcome.violation"),
            counts.GetProperty("violation").GetInt32());
        Assert.Equal(
            grouped.GetValueOrDefault("audit.outcome.skipped"),
            counts.GetProperty("skipped").GetInt32());

        Assert.Equal(-1, result.StdoutBytes.AsSpan().IndexOf(bytes));
        Assert.Equal(-1, result.StderrBytes.AsSpan().IndexOf(bytes));
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
        TimeSpan? timeout = null,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environment = null) =>
        await RunProgramAsync(
            CliPath,
            arguments,
            timeout ?? TimeSpan.FromMinutes(2),
            workingDirectory,
            environment);

    private static async Task<CliProcessResult> RunPublishedAsync(
        IReadOnlyList<string> arguments,
        string output,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var result = await RunAsync(
            arguments,
            workingDirectory: workingDirectory,
            environment: environment);
        return result with
        {
            PublishedOutput = File.Exists(output)
                ? await File.ReadAllBytesAsync(output)
                : null,
        };
    }

    private static async Task<CliProcessResult> RunProgramAsync(
        string assembly,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        using var running = StartProgram(
            assembly,
            arguments,
            workingDirectory,
            environment);
        try
        {
            await running.Process.WaitForExitAsync().WaitAsync(timeout);
            return await running.CompleteAsync();
        }
        catch
        {
            if (!running.Process.HasExited)
            {
                running.Process.Kill(entireProcessTree: true);
                await running.Process.WaitForExitAsync();
            }
            throw;
        }
    }

    private static async Task<BrokenStdoutResult> RunWithBrokenStdoutAsync(
        IReadOnlyList<string> arguments,
        string marker,
        string release,
        string? competingOutput)
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var harnessArguments = new List<string>
        {
            "broken-stdout",
            RepositoryRoot,
            marker,
            release,
            competingOutput ?? "-",
            dotnet,
            CliPath,
        };
        harnessArguments.AddRange(arguments);
        var result = await RunProgramAsync(
            SignalSenderPath,
            harnessArguments,
            TimeSpan.FromMinutes(3));
        Assert.True(
            result.ExitCode == 0,
            $"The broken-stdout harness failed with {result.ExitCode}: {result.Stderr}");
        return new BrokenStdoutResult(result.Stdout.Trim(), result.Stderr);
    }

    private static void AssertExternalAbnormalTermination(string termination)
    {
        if (termination.StartsWith("signal:", StringComparison.Ordinal))
        {
            Assert.True(
                int.TryParse(termination["signal:".Length..], out var signal) && signal > 0,
                $"Invalid signal termination: {termination}");
            return;
        }

        Assert.StartsWith("exit:", termination, StringComparison.Ordinal);
        Assert.True(
            int.TryParse(termination["exit:".Length..], out var exitCode),
            $"Invalid exit termination: {termination}");
        Assert.DoesNotContain(exitCode, Enumerable.Range(0, 8));
    }

    private static RunningProcess Start(IReadOnlyList<string> arguments) =>
        StartProgram(CliPath, arguments);

    private static RunningProcess StartProgram(
        string assembly,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        return new RunningProcess(
            Process.Start(CreateStartInfo(
                assembly,
                arguments,
                workingDirectory,
                environment))
            ?? throw new InvalidOperationException("CLI process failed to start."));
    }

    private static ProcessStartInfo CreateStartInfo(
        string assembly,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDirectory ?? RepositoryRoot,
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
        foreach (var (name, value) in environment ?? new Dictionary<string, string?>())
        {
            startInfo.Environment[name] = value;
        }
        return startInfo;
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

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int processId, int signal);

    private sealed record CliProcessResult(
        int ExitCode,
        byte[] StdoutBytes,
        byte[] StderrBytes,
        byte[]? PublishedOutput = null)
    {
        public string Stdout => StrictUtf8.GetString(StdoutBytes);
        public string Stderr => StrictUtf8.GetString(StderrBytes);
    }

    private sealed record BrokenStdoutResult(string Termination, string Stderr);

    private sealed class RunningProcess(Process process) : IDisposable
    {
        private readonly Task<byte[]> stdout = AuditCliProcessTests.ReadAllBytesAsync(
            process.StandardOutput.BaseStream);
        private readonly Task<byte[]> stderr = AuditCliProcessTests.ReadAllBytesAsync(
            process.StandardError.BaseStream);

        public Process Process { get; } = process;

        public async Task<CliProcessResult> CompleteAsync() =>
            new(Process.ExitCode, await stdout, await stderr);

        public void Dispose() => Process.Dispose();
    }

}

[CollectionDefinition("Integration process lane 1")]
public sealed class IntegrationProcessLaneOneCollection;
