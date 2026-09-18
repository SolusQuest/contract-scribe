using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Cli;
using ContractScribe.Core;

namespace ContractScribe.Roslyn.IntegrationTests;

// End-to-end coverage for the layered consumer configuration: payload defaults,
// the optional downstream layer, the optional invocation override, invocation
// authority separation, and per-source admission/revalidation at the real
// composition boundary. The github-proposal runtime-authority path is unchanged
// and stays covered by its existing process tests.
[Collection("Integration process lane 2")]
public sealed class LayeredConfigurationProcessTests
{
    private static readonly string RepositoryRoot = CampaignCliProcessTests.RepositoryRoot;
    private static readonly string Configuration = AppContext.BaseDirectory.Contains(
        $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
        StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
    private static readonly string CliPath = Path.Join(
        RepositoryRoot, "src", "ContractScribe.Cli", "bin", Configuration, "net10.0", "ContractScribe.Cli.dll");

    [Fact]
    public async Task LayeredStartAndResume_ResolveAcrossInvocations()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        await using var fixture = await LoaderFixture.CreateAsync();
        await CampaignCliProcessTests.SetSingleWorkItemSourceAsync(fixture.Root);
        await File.WriteAllTextAsync(
            Path.Join(fixture.Root, "policy.json"), CampaignCliProcessTests.RequiredPolicy);
        await using var server = new CampaignCliProcessTests.ProposalLoopbackServer();
        var outside = CampaignCliProcessTests.CreatePrivateDirectory("contract-scribe-layered-run");
        try
        {
            var statePath = Path.Join(outside, "checkpoint.json");
            var downstream = Path.Join(outside, "consumer.json");
            var overlay = Path.Join(outside, "override.json");
            await File.WriteAllTextAsync(downstream,
                "{\"consumerConfigurationVersion\":1,\"provider\":{\"endpoint\":\"" + server.Endpoint.AbsoluteUri
                + "\"}}\n",
                new UTF8Encoding(false, true));
            await File.WriteAllTextAsync(overlay,
                "{\"consumerConfigurationVersion\":1,\"provider\":{\"model\":\"layered-model\"}}\n",
                new UTF8Encoding(false, true));

            var started = await CampaignCliProcessTests.RunAsync(
                CampaignCliProcessTests.Args("start", fixture.Root, statePath, downstream, "snapshot.layered.a")
                    .Concat(["--configuration-override", overlay]).ToArray(),
                TimeSpan.FromMinutes(3));
            Assert.True(started.ExitCode == 0,
                $"exit={started.ExitCode} stdout={started.Stdout} stderr={started.Stderr}");
            Assert.Contains("\"outcome\":\"campaign.complete\"", started.Stdout, StringComparison.Ordinal);
            var completedState = CampaignStateJson.Parse(await File.ReadAllBytesAsync(statePath));
            Assert.True(completedState.IsValid);
            Assert.Equal("campaign.integration", completedState.Artifact!.State.CampaignLineage);

            var resumed = await CampaignCliProcessTests.RunAsync(
                CampaignCliProcessTests.Args("resume", fixture.Root, statePath, downstream, "snapshot.layered.a")
                    .Concat(["--configuration-override", overlay]).ToArray(),
                TimeSpan.FromMinutes(3));
            Assert.Equal(0, resumed.ExitCode);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task LayerMutationBetweenInvocations_ChangesTheEffectiveConfiguration()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        await using var fixture = await LoaderFixture.CreateAsync();
        await CampaignCliProcessTests.SetSingleWorkItemSourceAsync(fixture.Root);
        await File.WriteAllTextAsync(
            Path.Join(fixture.Root, "policy.json"), CampaignCliProcessTests.RequiredPolicy);
        await using var server = new CampaignCliProcessTests.ProposalLoopbackServer();
        var outside = CampaignCliProcessTests.CreatePrivateDirectory("contract-scribe-layered-drift");
        try
        {
            var statePath = Path.Join(outside, "checkpoint.json");
            var layer = Path.Join(outside, "consumer.json");
            await CampaignCliProcessTests.WriteConsumerLayerAsync(layer, server.Endpoint);
            var started = await CampaignCliProcessTests.RunAsync(
                CampaignCliProcessTests.Args("start", fixture.Root, statePath, layer, "snapshot.layered.b"),
                TimeSpan.FromMinutes(3));
            Assert.Equal(0, started.ExitCode);
            var checkpoint = await File.ReadAllBytesAsync(statePath);
            var requestsAfterStart = server.RequestCount;
            var checkpointRevision = CampaignStateJson.Parse(checkpoint)
                .Artifact!.CheckpointRevision;

            // A corrupted layer fails closed before any execution.
            await File.WriteAllTextAsync(layer, "{\"consumerConfigurationVersion\":1", new UTF8Encoding(false, true));
            var invalid = await CampaignCliProcessTests.RunAsync(
                CampaignCliProcessTests.Args("resume", fixture.Root, statePath, layer, "snapshot.layered.b"),
                TimeSpan.FromMinutes(3));
            CampaignCliProcessTests.AssertCampaign(invalid, 4, "campaign.invalid-configuration", null);
            Assert.Equal(checkpoint, await File.ReadAllBytesAsync(statePath));

            // A changed effective value rejects the same-snapshot resume.
            await CampaignCliProcessTests.WriteConsumerLayerAsync(layer, server.Endpoint, 30_000);
            var drifted = await CampaignCliProcessTests.RunAsync(
                CampaignCliProcessTests.Args("resume", fixture.Root, statePath, layer, "snapshot.layered.b"),
                TimeSpan.FromMinutes(3));
            CampaignCliProcessTests.AssertCampaign(
                drifted, 4, "campaign.incompatible-snapshot", checkpointRevision);
            Assert.Equal(checkpoint, await File.ReadAllBytesAsync(statePath));

            // Provider, style, and campaign-budget drift each reject the
            // same-snapshot resume identically before any provider dispatch.
            foreach (var mutation in new JsonObject[]
            {
                new() { ["provider"] = new JsonObject
                    { ["model"] = "drifted-model", ["endpoint"] = server.Endpoint.AbsoluteUri } },
                new() { ["provider"] = new JsonObject { ["endpoint"] = server.Endpoint.AbsoluteUri },
                        ["scribeRequest"] = new JsonObject
                            { ["styleProfileTemplate"] = new JsonObject { ["maximumContentUnits"] = 8 } } },
                new() { ["provider"] = new JsonObject { ["endpoint"] = server.Endpoint.AbsoluteUri },
                        ["budgets"] = new JsonObject
                            { ["campaign"] = new JsonObject { ["maximumBlocks"] = 64 } } },
            })
            {
                var driftLayer = new JsonObject { ["consumerConfigurationVersion"] = 1 };
                foreach (var property in mutation)
                {
                    driftLayer[property.Key] = property.Value?.DeepClone();
                }
                await File.WriteAllTextAsync(layer, driftLayer.ToJsonString(), new UTF8Encoding(false, true));
                var resumed = await CampaignCliProcessTests.RunAsync(
                    CampaignCliProcessTests.Args("resume", fixture.Root, statePath, layer, "snapshot.layered.b"),
                    TimeSpan.FromMinutes(3));
                CampaignCliProcessTests.AssertCampaign(
                    resumed, 4, "campaign.incompatible-snapshot", checkpointRevision);
                Assert.Equal(checkpoint, await File.ReadAllBytesAsync(statePath));
            }
            Assert.Equal(requestsAfterStart, server.RequestCount);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task UndeclaredAuthorityField_IsRejectedBeforeCredentialAccess()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        await using var fixture = await LoaderFixture.CreateAsync();
        await CampaignCliProcessTests.SetSingleWorkItemSourceAsync(fixture.Root);
        await File.WriteAllTextAsync(
            Path.Join(fixture.Root, "policy.json"), CampaignCliProcessTests.RequiredPolicy);
        var outside = CampaignCliProcessTests.CreatePrivateDirectory("contract-scribe-layered-authority");
        try
        {
            var statePath = Path.Join(outside, "checkpoint.json");
            var layer = Path.Join(outside, "consumer.json");
            // An HTTPS endpoint would demand a credential; the excluded field must
            // be rejected before any credential read or provider dispatch.
            await File.WriteAllTextAsync(layer,
                "{\"consumerConfigurationVersion\":1,"
                + "\"provider\":{\"endpoint\":\"https://provider.invalid/v1/chat/completions\"},"
                + "\"planning\":{\"campaignLineage\":\"smuggled.lineage\"}}\n",
                new UTF8Encoding(false, true));
            var result = await CampaignCliProcessTests.RunAsync(
                CampaignCliProcessTests.Args("start", fixture.Root, statePath, layer, "snapshot.layered.c"),
                TimeSpan.FromMinutes(3));
            CampaignCliProcessTests.AssertCampaign(result, 4, "campaign.invalid-configuration", null);
            Assert.False(File.Exists(statePath));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task ArbitraryWorkingDirectory_LoadsDefaultsFromThePayload()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        await using var fixture = await LoaderFixture.CreateAsync();
        await CampaignCliProcessTests.SetSingleWorkItemSourceAsync(fixture.Root);
        await File.WriteAllTextAsync(
            Path.Join(fixture.Root, "policy.json"), CampaignCliProcessTests.RequiredPolicy);
        var outside = CampaignCliProcessTests.CreatePrivateDirectory("contract-scribe-layered-cwd");
        var arbitraryCwd = CampaignCliProcessTests.CreatePrivateDirectory("contract-scribe-layered-arbitrary-cwd");
        try
        {
            var statePath = Path.Join(outside, "checkpoint.json");
            // No layer files at all: defaults come from the selected payload, never
            // from the repository root, main, or the caller's working directory.
            var arguments = new[]
            {
                "campaign", "start",
                "--repository-root", fixture.Root,
                "--input", "App/App.csproj",
                "--policy", "policy.json",
                "--snapshot", "snapshot.layered.cwd",
                "--state", statePath,
                "--campaign-lineage", "campaign.layered.cwd",
            };
            using var process = StartFrom(arbitraryCwd, arguments);
            await process.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3));
            var result = await process.CompleteAsync();
            // Defaults carry the validated provider endpoint; the scrubbed
            // environment guarantees no credential, so the run must stop at the
            // provider boundary with exactly campaign.credential.invalid —
            // proving configuration resolved and no authenticated request left.
            using var envelope = JsonDocument.Parse(result.Stdout);
            var root = envelope.RootElement;
            var diagnostics = root.GetProperty("diagnosticCodes")
                .EnumerateArray().Select(code => code.GetString());
            Assert.True(
                string.Equals("execution", root.GetProperty("terminalLayer").GetString(), StringComparison.Ordinal)
                    && diagnostics.Contains("campaign.credential.invalid"),
                $"exit={result.ExitCode} stdout={result.Stdout} stderr={result.Stderr}");
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
            Directory.Delete(arbitraryCwd, recursive: true);
        }
    }

    [Fact]
    public async Task ProductAndLineageDrift_RejectAtTheResolverRunnerBoundary()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        await using var fixture = await LoaderFixture.CreateAsync();
        await CampaignCliProcessTests.SetSingleWorkItemSourceAsync(fixture.Root);
        await File.WriteAllTextAsync(
            Path.Join(fixture.Root, "policy.json"), CampaignCliProcessTests.RequiredPolicy);
        await using var server = new CampaignCliProcessTests.ProposalLoopbackServer();
        var outside = CampaignCliProcessTests.CreatePrivateDirectory("contract-scribe-layered-identity");
        try
        {
            var statePath = Path.Join(outside, "checkpoint.json");
            var layer = Path.Join(outside, "consumer.json");
            await CampaignCliProcessTests.WriteConsumerLayerAsync(layer, server.Endpoint);
            var revision = CommandLineApplication.ApplicationVersion.Split('+')[1];
            var identityA = CliBuildIdentity.Create("0.1.0-test+" + revision);
            var identityB = CliBuildIdentity.Create(
                "0.1.0-test+" + (revision[0] == 'a' ? "b" : "a") + revision[1..]);

            var start = await CampaignCommandRunner.RunAsync(
                identityA,
                CampaignPreflight.Run(
                    Arguments(CampaignOperation.Start, fixture.Root, statePath, layer, "snapshot.identity", "lineage.identity.a"),
                    fixture.Root,
                    identityA),
                CancellationToken.None);
            Assert.NotEqual("campaign.invalid-configuration", Outcome(start));
            Assert.True(File.Exists(statePath));
            var checkpoint = await File.ReadAllBytesAsync(statePath);
            var requestsBefore = server.RequestCount;

            // Same snapshot, different product identity: the running payload's
            // derived product commitment differs from the recorded checkpoint.
            var productDrift = await CampaignCommandRunner.RunAsync(
                identityB,
                CampaignPreflight.Run(
                    Arguments(CampaignOperation.Resume, fixture.Root, statePath, layer, "snapshot.identity", "lineage.identity.a"),
                    fixture.Root,
                    identityB),
                CancellationToken.None);
            Assert.Equal("campaign.incompatible-snapshot", Outcome(productDrift));
            Assert.Equal(checkpoint, await File.ReadAllBytesAsync(statePath));
            Assert.Equal(requestsBefore, server.RequestCount);

            // Same snapshot, different lineage authority: the invocation-supplied
            // identity differs from the recorded campaign lineage.
            var lineageDrift = await CampaignCommandRunner.RunAsync(
                identityA,
                CampaignPreflight.Run(
                    Arguments(CampaignOperation.Resume, fixture.Root, statePath, layer, "snapshot.identity", "lineage.identity.b"),
                    fixture.Root,
                    identityA),
                CancellationToken.None);
            Assert.Equal("campaign.incompatible-snapshot", Outcome(lineageDrift));
            Assert.Equal(checkpoint, await File.ReadAllBytesAsync(statePath));
            Assert.Equal(requestsBefore, server.RequestCount);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    private static CampaignCommandArguments Arguments(
        CampaignOperation operation,
        string repositoryRoot,
        string statePath,
        string layer,
        string snapshot,
        string lineage) =>
        new(
            operation,
            repositoryRoot,
            "App/App.csproj",
            "policy.json",
            snapshot,
            statePath,
            layer,
            null,
            lineage,
            CampaignConfigurationKind.Layered);

    private static string Outcome(CliExecutionResult result)
    {
        using var document = JsonDocument.Parse(result.StandardOutput);
        return document.RootElement.GetProperty("outcome").GetString()!;
    }

    private static CampaignCliProcessTests.RunningProcess StartFrom(
        string workingDirectory,
        IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
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
        // The defaults-only leg selects the payload's real HTTPS provider;
        // an ambient key must never reach this subprocess or it could make a
        // live authenticated request. Remove it from the child's environment
        // only — the parent environment is shared with parallel tests.
        start.Environment.Remove("CONTRACTSCRIBE_PROVIDER_API_KEY");
        Assert.False(start.Environment.ContainsKey("CONTRACTSCRIBE_PROVIDER_API_KEY"));
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "true";
        return new CampaignCliProcessTests.RunningProcess(Process.Start(start)
            ?? throw new InvalidOperationException("Campaign CLI process failed to start."));
    }
}
