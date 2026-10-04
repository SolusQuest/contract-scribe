using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Cli;
using ContractScribe.Core;
using Json.Schema;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed partial class CampaignCliProcessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Production_zero_creation_quota_preserves_deferred_manifest_and_batch_complete_across_fresh_invocations(
        bool unsupportedProperty)
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await LoaderFixture.CreateAsync();
        var source = "namespace Fixture;\n/// <summary>Provides fixture operations.</summary>\npublic static class App {\n"
            + "public static void First() { }\npublic static void Second() { }\n"
            + (unsupportedProperty ? "public static int Value { get; set; }\n" : "") + "}\n";
        var sourcePath = Path.Join(fixture.Root, "App", "App.cs");
        await File.WriteAllTextAsync(sourcePath, source);
        var sourceBytes = await File.ReadAllBytesAsync(sourcePath);
        await File.WriteAllTextAsync(Path.Join(fixture.Root, "policy.json"), RequiredPolicy);
        await using var server = new ProposalLoopbackServer();
        var outside = CreatePrivateDirectory("contract-scribe-zero-batch");
        try
        {
            var configurationPath = Path.Join(outside, "campaign.json");
            var stateDirectory = Path.Join(outside, "state");
            Directory.CreateDirectory(stateDirectory);
            File.SetUnixFileMode(stateDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var statePath = Path.Join(stateDirectory, "checkpoint.json");
            await WriteConsumerLayerAsync(configurationPath, server.Endpoint);
            var preflight = CampaignPreflight.Run(new(CampaignOperation.Start, fixture.Root, "App/App.csproj", "policy.json",
                "snapshot.batch", statePath, configurationPath, null, "campaign.integration"), RepositoryRoot);
            var result = await CampaignCommandRunner.RunAsync(CliBuildIdentity.Current, preflight, CancellationToken.None,
                _ => throw new InvalidOperationException("An empty fixed batch cannot request credentials."), targetLimit: new(0));
            using var envelope = JsonDocument.Parse(result.StandardOutput);
            Assert.Equal("campaign.batch-complete", envelope.RootElement.GetProperty("outcome").GetString());
            Assert.Equal(0, result.ExitCode);
            var bytes = await File.ReadAllBytesAsync(statePath);
            var parsed = CampaignStateJson.Parse(bytes);
            Assert.True(parsed.IsValid, parsed.FailureCode?.ToString());
            var initial = parsed.Artifact!.State;
            Assert.Equal(0, initial.Batch.CreationQuota);
            Assert.Empty(initial.Batch.SelectedTargetKeys);
            Assert.Equal(unsupportedProperty ? 3 : 2, initial.Batch.CompleteTargets.Length);
            Assert.All(initial.TargetProgress, item => Assert.Equal(CampaignTargetProgressKind.Deferred, item.Kind));
            Assert.Equal(CampaignTerminalReason.AllWorkClosed, initial.TerminalOutcome!.Reason);
            Assert.Equal(0, initial.LineageCharges.OuterInvocations);
            Assert.Equal(0, initial.LineageCharges.PatchValidationInvocations);
            Assert.Null(initial.ActiveReservation);
            Assert.Equal(bytes, CampaignStateJson.Write(initial));
            foreach (var limit in new[] { 0, 4096 })
            {
                await RunBatchWorkerAsync(fixture.Root, configurationPath, statePath, outside, limit, "campaign.batch-complete");
                Assert.Equal(bytes, await File.ReadAllBytesAsync(statePath));
            }
            Assert.Equal(0, server.RequestCount);
            Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(sourcePath));
        }
        finally { Directory.Delete(outside, recursive: true); }
    }

    [Fact]
    public async Task Production_saved100_batch_restores_at10_in_a_fresh_runner_process_and_reconstructs_at0()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await LoaderFixture.CreateAsync();
        await File.WriteAllTextAsync(Path.Join(fixture.Root, "App", "App.cs"),
            "namespace Fixture;\n/// <summary>Provides fixture operations.</summary>\npublic static class App {\n"
            + string.Join("\n", Enumerable.Range(0, 105).Select(index => $"public static void Operation{index:D3}() {{ }}")) + "\n}\n");
        await File.WriteAllTextAsync(Path.Join(fixture.Root, "policy.json"), RequiredPolicy);
        await using var server = new ProposalLoopbackServer();
        var outside = CreatePrivateDirectory("contract-scribe-fixed-batch");
        try
        {
            var configurationPath = Path.Join(outside, "campaign.json");
            var stateDirectory = Path.Join(outside, "state");
            Directory.CreateDirectory(stateDirectory);
            File.SetUnixFileMode(stateDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var statePath = Path.Join(stateDirectory, "checkpoint.json");
            await WriteConsumerLayerAsync(configurationPath, server.Endpoint);
            var layer = JsonNode.Parse(await File.ReadAllTextAsync(configurationPath))!.AsObject();
            // Ten cumulative acceptances revalidate the first block ten times, then reconstruction once.
            layer["budgets"] = new JsonObject
            {
                ["campaign"] = new JsonObject
                {
                    ["maximumCandidatesPerBlock"] = 12,
                    ["maximumElapsedMilliseconds"] = 300_000,
                },
            };
            await File.WriteAllTextAsync(configurationPath, layer.ToJsonString(), new UTF8Encoding(false, true));
            var preflight = CampaignPreflight.Run(new(CampaignOperation.Start, fixture.Root, "App/App.csproj", "policy.json",
                "snapshot.batch", statePath, configurationPath, null, "campaign.integration"), RepositoryRoot);
            CampaignCheckpointArtifact? initial = null;
            var host = new ProductionRepositorySessionHost(new ContractScribe.Core.Hosting.HostBuildProvenance(CliBuildIdentity.Current.SourceRevision));
            await host.RunAsync(new(fixture.Root, "App/App.csproj", preflight.PolicyBytes, PublicationTarget: null, PublishResult: false),
                new ProductionAuditHostControls(SessionConsumer: (bundle, token) =>
                {
                    var configuration = preflight.Configuration.Document;
                    var policy = configuration.CreateExecutionPolicy();
                    var execution = configuration.CreateExecutionCapability(policy);
                    var planning = CampaignCommandRunner.CreatePlanningInput(preflight, configuration, policy, bundle, token, new(100));
                    var plan = CampaignPlanner.Plan(planning);
                    initial = CampaignStateJson.CreateArtifact(CampaignStateFactory.CreateInitial(
                        configuration.ScribeRequest.StyleProfileTemplate.StyleProfileId,
                        configuration.ScribeRequest.StyleProfileTemplate.ExactProjection, execution,
                        bundle.Session.InputIdentity, planning, plan));
                    return Task.CompletedTask;
                }), CancellationToken.None);
            Assert.NotNull(initial);
            Assert.Equal(100, initial.State.Batch.SelectedTargetKeys.Length);
            Assert.Equal(105, initial.State.Batch.CompleteTargets.Length);
            var schemaOptions = new BuildOptions { SchemaRegistry = new SchemaRegistry() };
            _ = JsonSchema.FromText(await File.ReadAllTextAsync(Path.Join(RepositoryRoot, "schemas", "documentation-patch", "v1.request.schema.json")), schemaOptions);
            var schema = JsonSchema.FromText(await File.ReadAllTextAsync(Path.Join(RepositoryRoot, "schemas", "campaign-state", "v1.schema.json")), schemaOptions);
            using var initialDocument = JsonDocument.Parse(initial.ExactUtf8Json.ToArray());
            Assert.True(schema.Evaluate(initialDocument.RootElement).IsValid);
            await File.WriteAllBytesAsync(statePath, initial.ExactUtf8Json.ToArray());
            File.SetUnixFileMode(statePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await RunBatchWorkerAsync(fixture.Root, configurationPath, statePath, outside, 10);
            var restored = CampaignStateJson.Parse(await File.ReadAllBytesAsync(statePath));
            Assert.True(restored.IsValid, restored.FailureCode?.ToString());
            var state = restored.Artifact!.State;
            using var restoredDocument = JsonDocument.Parse(restored.Artifact.ExactUtf8Json.ToArray());
            Assert.True(schema.Evaluate(restoredDocument.RootElement).IsValid);
            Assert.Equal(initial.State.Snapshot, state.Snapshot);
            Assert.Equal(initial.State.Batch.Identity, state.Batch.Identity);
            Assert.True(initial.State.Batch.SelectedTargetKeys.SequenceEqual(state.Batch.SelectedTargetKeys, StringComparer.Ordinal));
            Assert.Equal(10, state.WorkItems.Count(work => work.OuterAttemptCount > 0));
            Assert.Equal(10, state.WorkItems.Count(work => work.Status == CampaignWorkStatus.Accepted));
            Assert.Equal(10, server.RequestCount);
            Assert.Equal(90, CampaignStateFactory.CreateInvocationTargetAllowance(state, new(4096)).WorkItemKeys.Length);
            var attempts = state.WorkItems.Select(work => work.OuterAttemptCount).ToArray();
            await RunBatchWorkerAsync(fixture.Root, configurationPath, statePath, outside, 0);
            var reconstructed = CampaignStateJson.Parse(await File.ReadAllBytesAsync(statePath)).Artifact!.State;
            Assert.Equal(attempts, reconstructed.WorkItems.Select(work => work.OuterAttemptCount));
            Assert.Equal(state.Batch.Identity, reconstructed.Batch.Identity);
            Assert.Equal(10, server.RequestCount);
            Assert.Equal(10, reconstructed.WorkItems.Count(work => work.Status == CampaignWorkStatus.Accepted));
            Assert.True(reconstructed.LineageCharges.PatchValidationInvocations > state.LineageCharges.PatchValidationInvocations);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task FixedBatchResumeWorker()
    {
        var root = Environment.GetEnvironmentVariable("CONTRACTSCRIBE_BATCH_WORKER_ROOT");
        if (root is null) return;
        var configuration = Environment.GetEnvironmentVariable("CONTRACTSCRIBE_BATCH_WORKER_CONFIG")!;
        var state = Environment.GetEnvironmentVariable("CONTRACTSCRIBE_BATCH_WORKER_STATE")!;
        var limit = int.Parse(Environment.GetEnvironmentVariable("CONTRACTSCRIBE_BATCH_WORKER_LIMIT")!, System.Globalization.CultureInfo.InvariantCulture);
        var expectedOutcome = Environment.GetEnvironmentVariable("CONTRACTSCRIBE_BATCH_WORKER_OUTCOME") ?? "campaign.target-limit";
        var preflight = CampaignPreflight.Run(new(CampaignOperation.Resume, root, "App/App.csproj", "policy.json",
            "snapshot.batch", state, configuration, null, "campaign.integration"), RepositoryRoot);
        var result = await CampaignCommandRunner.RunAsync(CliBuildIdentity.Current, preflight, CancellationToken.None,
            _ => throw new InvalidOperationException("Loopback batch execution cannot request credentials."), targetLimit: new(limit));
        using var envelope = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(expectedOutcome, envelope.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(expectedOutcome == "campaign.batch-complete" ? 0 : 3, result.ExitCode);
    }

    private static async Task RunBatchWorkerAsync(string root, string configuration, string state, string output, int limit,
        string expectedOutcome = "campaign.target-limit")
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = RepositoryRoot };
        foreach (var argument in new[] { "test", Path.Join(RepositoryRoot, "tests", "ContractScribe.IntegrationTests", "ContractScribe.IntegrationTests.csproj"),
                     "--configuration", Configuration, "--no-build", "--no-restore", "--filter",
                     "FullyQualifiedName=ContractScribe.Roslyn.IntegrationTests.CampaignCliProcessTests.FixedBatchResumeWorker",
                     "--results-directory", Path.Join(output, "worker-" + limit), "--logger", "trx;LogFilePrefix=batch-worker", "--nologo" }) start.ArgumentList.Add(argument);
        start.Environment["CONTRACTSCRIBE_BATCH_WORKER_ROOT"] = root;
        start.Environment["CONTRACTSCRIBE_BATCH_WORKER_CONFIG"] = configuration;
        start.Environment["CONTRACTSCRIBE_BATCH_WORKER_STATE"] = state;
        start.Environment["CONTRACTSCRIBE_BATCH_WORKER_LIMIT"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["CONTRACTSCRIBE_BATCH_WORKER_OUTCOME"] = expectedOutcome;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
        Assert.True(process.ExitCode == 0, (await stdout) + (await stderr));
    }
}
