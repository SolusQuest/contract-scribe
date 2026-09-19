using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Core;

namespace ContractScribe.Roslyn.IntegrationTests;

// Packed-payload execution matrix (issue #18 / M6-A1). These tests launch the
// CLI DLL named by CONTRACTSCRIBE_PACKAGED_CLI — the installed D2 payload in
// verify mode, the source-built DLL in record mode — through the production
// campaign/configuration paths against deterministic loopback substitutes.
// They are repo-root-free: every input arrives through the environment, so the
// same assembly runs in the no-checkout consumer job.
//
//   CONTRACTSCRIBE_PACKAGED_MODE        unset → ordinary suite: early return;
//                                       "record" → write expected projections;
//                                       "verify" → compare vs recorded projections
//   CONTRACTSCRIBE_PACKAGED_CLI         DLL under test (verify: must live
//                                       beneath a contract-scribe-*-linux-x64 dir)
//   CONTRACTSCRIBE_PACKAGING_EXPECTED   expected-projection directory
//   CONTRACTSCRIBE_PACKAGING_FIXTURE    static fixture source (kit fixture)
//   CONTRACTSCRIBE_STARTUP_HOOK_PATH    CampaignStartupHook.dll
//   CONTRACTSCRIBE_PACKAGED_CLI_IDENTITY  expected toolVersion (verify)
[Collection("Integration process lane 1")]
public sealed class PackagedCliProcessTests
{
    internal enum PackagedMode { None, Record, Verify }

    internal static PackagedMode Mode =>
        Environment.GetEnvironmentVariable("CONTRACTSCRIBE_PACKAGED_MODE") switch
        {
            null or "" => PackagedMode.None,
            "record" => PackagedMode.Record,
            "verify" => PackagedMode.Verify,
            var other => throw new InvalidOperationException(
                $"CONTRACTSCRIBE_PACKAGED_MODE must be record or verify, not '{other}'."),
        };

    private static readonly string[] ScrubbedEnvironment =
    [
        "CONTRACTSCRIBE_PROVIDER_API_KEY",
        "CONTRACTSCRIBE_GITHUB_TOKEN",
        "CONTRACTSCRIBE_TEST_GITHUB_ENDPOINT",
        "CONTRACTSCRIBE_TEST_GITHUB_OBSERVATIONS",
        "CONTRACTSCRIBE_TEST_GITHUB_FAULT",
        "CONTRACTSCRIBE_TEST_CAMPAIGN_HOOK_NAME",
        "CONTRACTSCRIBE_TEST_CAMPAIGN_HOOK_ACK",
        "CONTRACTSCRIBE_TEST_CAMPAIGN_HOOK_RELEASE",
        "CONTRACTSCRIBE_PACKAGED_MODE",
        "CONTRACTSCRIBE_PACKAGED_CLI",
        "CONTRACTSCRIBE_PACKAGED_CLI_IDENTITY",
        "CONTRACTSCRIBE_PACKAGING_EXPECTED",
        "CONTRACTSCRIBE_PACKAGING_FIXTURE",
        "DOTNET_STARTUP_HOOKS",
        "DOTNET_ADDITIONAL_DEPS",
        "DOTNET_SHARED_STORE",
        "GH_TOKEN",
        "GITHUB_TOKEN",
    ];

    private static string CliPath = "";
    private static string ExpectedDir = "";
    private static string FixtureDir = "";
    internal static string HookPath = "";

    internal static bool Activated()
    {
        if (!OperatingSystem.IsLinux() || Mode == PackagedMode.None)
        {
            return false;
        }
        CliPath = RequireEnv("CONTRACTSCRIBE_PACKAGED_CLI");
        ExpectedDir = RequireEnv("CONTRACTSCRIBE_PACKAGING_EXPECTED");
        FixtureDir = RequireEnv("CONTRACTSCRIBE_PACKAGING_FIXTURE");
        HookPath = RequireEnv("CONTRACTSCRIBE_STARTUP_HOOK_PATH");
        Assert.True(File.Exists(CliPath), $"packed CLI missing: {CliPath}");
        Assert.True(Directory.Exists(ExpectedDir), $"expected dir missing: {ExpectedDir}");
        Assert.True(Directory.Exists(FixtureDir), $"fixture dir missing: {FixtureDir}");
        Assert.True(File.Exists(HookPath), $"startup hook missing: {HookPath}");
        if (Mode == PackagedMode.Verify)
        {
            var parent = Path.GetFileName(Path.GetDirectoryName(CliPath));
            Assert.StartsWith("contract-scribe-", parent);
            Assert.EndsWith("-linux-x64", parent);
        }
        return true;
    }

    private static string RequireEnv(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{name} is required in packaged mode.");

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task PackedCampaign_StartCompleteAndResumeLifecycle()
    {
        if (!Activated()) return;
        var fixture = await MaterializeFixtureAsync("campaign");
        var outside = CampaignCliProcessTests.CreatePrivateDirectory("contract-scribe-packed-campaign");
        var stateDirectory = Path.Join(outside, "state");
        Directory.CreateDirectory(stateDirectory);
        File.SetUnixFileMode(stateDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var state = Path.Join(stateDirectory, "checkpoint.json");
        var missing = Path.Join(stateDirectory, "missing.json");
        var layer = Path.Join(outside, "consumer.json");
        await using var server = new CampaignCliProcessTests.ProposalLoopbackServer();
        await WriteLayerAsync(layer, server.Endpoint);
        var projection = new JsonObject();

        projection["start"] = await CampaignStepAsync(
            "start", fixture, state, layer, server, 0, "campaign.complete");
        Assert.True(File.Exists(state));
        var requestsAfterStart = server.RequestCount;

        projection["start-again"] = await CampaignStepAsync(
            "start", fixture, state, layer, server, 4, "campaign.state-present");

        projection["resume"] = await CampaignStepAsync(
            "resume", fixture, state, layer, server, 0, "campaign.complete");
        // Accepted-candidate reconstruction returns campaign.complete without
        // a new provider dispatch (B008: not campaign.no-work).
        Assert.Equal(requestsAfterStart, server.RequestCount);

        projection["resume-missing"] = await CampaignStepAsync(
            "resume", fixture, missing, layer, server, 4, "campaign.state-missing");

        await AssertProjectionAsync("campaign-lifecycle", projection);
    }

    [Fact]
    public async Task PackedCampaign_NoRequiredWorkIsNoWork()
    {
        if (!Activated()) return;
        var fixture = await MaterializeFixtureAsync("nowork");
        // Optional policy: findings are advisory, so the campaign has no
        // required work even though the audit still reports violations.
        await File.WriteAllTextAsync(
            Path.Join(fixture, "policy.json"),
            "{\"defaultDecision\":\"optional\",\"schemaVersion\":1,"
            + "\"targetProfile\":\"profile.external-api\"}\n",
            new UTF8Encoding(false, true));
        var outside = CampaignCliProcessTests.CreatePrivateDirectory("contract-scribe-packed-nowork");
        var state = Path.Join(outside, "checkpoint.json");
        var layer = Path.Join(outside, "consumer.json");
        await using var server = new CampaignCliProcessTests.ProposalLoopbackServer();
        await WriteLayerAsync(layer, server.Endpoint);
        var projection = new JsonObject();

        projection["start"] = await CampaignStepAsync(
            "start", fixture, state, layer, server, 0, "campaign.no-work");
        projection["resume"] = await CampaignStepAsync(
            "resume", fixture, state, layer, server, 0, "campaign.no-work");

        await AssertProjectionAsync("campaign-nowork", projection);
    }

    [Fact]
    public async Task PackedLayered_ResolutionAndBoundaries()
    {
        if (!Activated()) return;
        var fixture = await MaterializeFixtureAsync("layered");
        var outside = CampaignCliProcessTests.CreatePrivateDirectory("contract-scribe-packed-layered");
        var layer = Path.Join(outside, "consumer.json");
        await using var server = new CampaignCliProcessTests.ProposalLoopbackServer();
        var projection = new JsonObject();

        // Defaults-only: no consumer layer. The shipped default provider is an
        // HTTPS endpoint with no credential, so resolution reaches a bounded
        // configuration failure before any dispatch.
        projection["defaults-only"] = await CampaignStepAsync(
            "start", fixture, Path.Join(outside, "s1.json"), null, server,
            4, "campaign.invalid-configuration");
        Assert.Equal(0, server.RequestCount);

        // Consumer layer selects the loopback endpoint; the invocation
        // override layer wins through the same resolver for its own fields.
        await WriteLayerAsync(layer, server.Endpoint);
        var overridePath = Path.Join(outside, "override.json");
        await File.WriteAllTextAsync(
            overridePath,
            "{\"consumerConfigurationVersion\":1,"
            + "\"provider\":{\"model\":\"override-model\"}}\n",
            new UTF8Encoding(false, true));
        projection["layer"] = await CampaignStepAsync(
            "start", fixture, Path.Join(outside, "s2.json"), layer, server,
            0, "campaign.complete", "--configuration-override", overridePath);

        // A product-owned field inside a consumer layer is rejected at
        // admission before any execution.
        await File.WriteAllTextAsync(
            layer,
            "{\"consumerConfigurationVersion\":1,"
            + "\"provider\":{\"endpoint\":\"" + server.Endpoint.AbsoluteUri + "\"},"
            + "\"planning\":{\"campaignLineage\":\"smuggled.value\"}}\n",
            new UTF8Encoding(false, true));
        projection["product-owned"] = await CampaignStepAsync(
            "start", fixture, Path.Join(outside, "s3.json"), layer, server,
            4, "campaign.invalid-configuration");

        await AssertProjectionAsync("layered", projection);
    }

    [Fact]
    public async Task PackedIdentity_VersionDoctorFromArbitraryCwd()
    {
        if (!Activated()) return;
        var projection = new JsonObject();
        var version = await RunCliAsync(["--version"]);
        Assert.Equal(0, version.ExitCode);
        Assert.Empty(version.Stderr);
        Assert.StartsWith("ContractScribe 0.1.0-dev+", version.Stdout.TrimEnd());
        if (Mode == PackagedMode.Verify)
        {
            var identity = RequireEnv("CONTRACTSCRIBE_PACKAGED_CLI_IDENTITY");
            Assert.Equal("ContractScribe " + identity, version.Stdout.TrimEnd());
        }
        projection["version"] = JsonValue.Create(version.Stdout.TrimEnd());

        var doctor = await RunCliAsync(["doctor"]);
        Assert.Equal(0, doctor.ExitCode);
        var fields = doctor.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains(':', StringComparison.Ordinal))
            .ToDictionary(
                line => line[..line.IndexOf(':', StringComparison.Ordinal)].Trim(),
                line => line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim(),
                StringComparer.Ordinal);
        Assert.Equal("not performed", fields["network_access"]);
        Assert.Equal("not performed", fields["credential_access"]);
        Assert.Equal("linux-x64", fields["runtime_identifier"]);
        projection["doctor"] = new JsonObject
        {
            ["runtime_identifier"] = fields["runtime_identifier"],
            ["network_access"] = fields["network_access"],
            ["credential_access"] = fields["credential_access"],
        };

        await AssertProjectionAsync("identity", projection);
    }

    private static async Task<JsonObject> CampaignStepAsync(
        string operation,
        string fixtureRoot,
        string statePath,
        string? layer,
        CampaignCliProcessTests.ProposalLoopbackServer server,
        int expectedExit,
        string expectedOutcome,
        params string[] extraArgs)
    {
        var args = CampaignArgs(operation, fixtureRoot, statePath, layer, extraArgs);
        var result = await RunCliAsync(args);
        using var document = JsonDocument.Parse(result.Stdout);
        var root = document.RootElement;
        var outcome = root.GetProperty("outcome").GetString();
        Assert.True(
            result.ExitCode == expectedExit && outcome == expectedOutcome,
            $"{operation}: exit={result.ExitCode} outcome={outcome} "
            + $"stdout={result.Stdout} stderr={result.Stderr}");
        return CampaignProjection(operation, result, server.RequestCount, statePath);
    }

    private static JsonObject CampaignProjection(
        string operation,
        ProcessResult result,
        int providerRequests,
        string statePath)
    {
        using var document = JsonDocument.Parse(result.Stdout);
        var root = document.RootElement;
        return new JsonObject
        {
            ["operation"] = operation,
            ["exitCode"] = result.ExitCode,
            ["terminalLayer"] = root.GetProperty("terminalLayer").GetString(),
            ["outcome"] = root.GetProperty("outcome").GetString(),
            ["diagnosticCodes"] = new JsonArray(root.GetProperty("diagnosticCodes")
                .EnumerateArray()
                .Select(e => (JsonNode)JsonValue.Create(e.GetString())!)
                .ToArray()),
            ["checkpointRevision"] =
                root.GetProperty("checkpointRevision").ValueKind == JsonValueKind.Number
                    ? JsonValue.Create(root.GetProperty("checkpointRevision").GetInt64())
                    : null,
            ["stateFileExists"] = File.Exists(statePath),
            ["checkpointValid"] = File.Exists(statePath)
                && CampaignStateJson.Parse(File.ReadAllBytes(statePath)).IsValid,
            ["providerRequests"] = providerRequests,
        };
    }

    private static string[] CampaignArgs(
        string operation,
        string root,
        string state,
        string? configuration,
        string[] extraArgs)
    {
        var args = new List<string>
        {
            "campaign", operation,
            "--repository-root", root,
            "--input", "App/App.csproj",
            "--policy", "policy.json",
            "--snapshot", "snapshot.packed",
            "--state", state,
            "--campaign-lineage", "campaign.packed",
        };
        if (configuration is not null)
        {
            args.AddRange(["--configuration", configuration]);
        }
        args.AddRange(extraArgs);
        return args.ToArray();
    }

    internal static async Task<string> MaterializeFixtureAsync(string tag)
    {
        var destination = CampaignCliProcessTests.CreatePrivateDirectory(
            "contract-scribe-packed-fixture-" + tag);
        foreach (var file in Directory.EnumerateFiles(FixtureDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(FixtureDir, file);
            if (relative.Split(Path.DirectorySeparatorChar)
                    .Any(part => part is "bin" or "obj"))
            {
                continue;
            }
            var target = Path.Join(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        await OwnedProcessRunner.RunAsync(
            "dotnet", destination, ["restore", "Fixture.slnx"], TimeSpan.FromMinutes(3));
        // Debug — the loader's default design-time configuration — must be
        // generated before the first load or it is flagged as protected drift.
        await OwnedProcessRunner.RunAsync(
            "dotnet", destination, ["build", "Fixture.slnx", "--no-restore"],
            TimeSpan.FromMinutes(5));
        return destination;
    }

    internal static async Task WriteLayerAsync(
        string destination, Uri endpoint, int? maximumPatchElapsedMilliseconds = null)
    {
        var root = new JsonObject { ["consumerConfigurationVersion"] = 1 };
        if (maximumPatchElapsedMilliseconds is { } maximumPatchElapsed)
        {
            ((JsonObject)(root["planning"] ??= new JsonObject()))
                ["maximumPatchElapsedMilliseconds"] = maximumPatchElapsed;
        }
        ((JsonObject)(root["provider"] ??= new JsonObject()))["endpoint"] = endpoint.AbsoluteUri;
        await File.WriteAllTextAsync(destination, root.ToJsonString(), new UTF8Encoding(false, true));
    }

    internal static async Task AssertProjectionAsync(string caseName, JsonObject projection)
    {
        var path = Path.Join(ExpectedDir, caseName + ".expected.json");
        if (Mode == PackagedMode.Record)
        {
            await File.WriteAllTextAsync(
                path, projection.ToJsonString() + "\n", new UTF8Encoding(false, true));
            return;
        }
        Assert.True(File.Exists(path), $"missing expected projection: {path}");
        var expected = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        Assert.True(
            JsonNode.DeepEquals(expected, projection),
            $"projection mismatch for {caseName}:\nexpected: {expected.ToJsonString()}"
            + $"\nactual: {projection.ToJsonString()}");
    }

    internal static async Task<ProcessResult> RunCliAsync(
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment = null,
        string? workingDirectory = null,
        TimeSpan? timeout = null)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory
                ?? CampaignCliProcessTests.CreatePrivateDirectory("contract-scribe-packed-cwd"),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(CliPath);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "true";
        foreach (var name in ScrubbedEnvironment)
        {
            start.Environment.Remove(name);
        }
        foreach (var (name, value) in environment ?? new Dictionary<string, string?>())
        {
            if (value is null)
            {
                start.Environment.Remove(name);
            }
            else
            {
                start.Environment[name] = value;
            }
        }
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Packed CLI process failed to start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(timeout ?? TimeSpan.FromMinutes(5));
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            throw;
        }
        return new ProcessResult(
            process.ExitCode,
            (await stdout).Replace("\r\n", "\n", StringComparison.Ordinal),
            (await stderr).Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    internal sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}
