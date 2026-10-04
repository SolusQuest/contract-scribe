using System.Text;
using System.Text.Json;
using ContractScribe.Cli;
using ContractScribe.Core;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed partial class CampaignCliProcessTests
{
    [Fact]
    public async Task Production_large_plan_receives_typed_checkpoint_capacity_admission()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var outside = CreatePrivateDirectory("contract-scribe-capacity-plan");
        try
        {
            await File.WriteAllTextAsync(Path.Join(fixture.Root, "App", "App.cs"), CapacitySource(164, documentedClass: true, deferredMethods: 0, largeBindings: true), new UTF8Encoding(false, true));
            await File.WriteAllTextAsync(Path.Join(fixture.Root, "policy.json"), RequiredPolicy);
            var preflight = CampaignPreflight.Run(new(CampaignOperation.Start, fixture.Root, "App/App.csproj", "policy.json",
                "snapshot.capacity-native", Path.Join(outside, "state.json"),
                null, null, "campaign.integration"), RepositoryRoot);
            var configuration = preflight.Configuration.Document;
            CampaignWorkPlan? plan = null;
            CampaignStateValidationException? capacity = null;
            Exception? consumerFailure = null;
            var auditBytes = 0;
            var host = new ProductionRepositorySessionHost(new ContractScribe.Core.Hosting.HostBuildProvenance(CliBuildIdentity.Current.SourceRevision));
            var result = await host.RunAsync(new(fixture.Root, "App/App.csproj", preflight.PolicyBytes, PublicationTarget: null, PublishResult: false),
                new ProductionAuditHostControls(SessionConsumer: (bundle, token) =>
                {
                    try
                    {
                        var policy = configuration.CreateExecutionPolicy();
                        auditBytes = bundle.CanonicalAudit.Length;
                        var planning = CampaignCommandRunner.CreatePlanningInput(preflight, configuration, policy, bundle, token, new(0));
                        plan = CampaignPlanner.Plan(planning);
                        capacity = Assert.Throws<CampaignStateValidationException>(() => CampaignStateFactory.CreateInitial(
                        configuration.ScribeRequest.StyleProfileTemplate.StyleProfileId,
                        configuration.ScribeRequest.StyleProfileTemplate.ExactProjection, configuration.CreateExecutionCapability(policy),
                        bundle.Session.InputIdentity, planning, plan));
                    }
                    catch (Exception exception)
                    {
                        consumerFailure = exception;
                        throw;
                    }
                    return Task.CompletedTask;
                }), CancellationToken.None);
            Assert.True(plan is not null, result.Terminal.ExecutionOutcome + ": "
                + string.Join(",", result.Terminal.Diagnostics.Select(diagnostic => diagnostic.Code)) + "; consumer: "
                + consumerFailure?.GetType().Name + ": " + consumerFailure?.Message + "; audit bytes: " + auditBytes);
            Assert.Equal(656, plan.Batch.CompleteTargets.Length);
            Assert.Equal(164, plan.WorkItems.Length);
            Assert.NotNull(capacity);
            Assert.Equal(CampaignStateValidationCode.DocumentTooLarge, capacity.Code);
            Assert.Equal(ContractScribe.Core.Hosting.HostExecutionOutcome.Succeeded, result.Terminal.ExecutionOutcome);
            Assert.False(File.Exists(preflight.StatePath));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task Production_large_manifest_admission_preserves_the_byte_ceiling_and_changed_base_predecessor()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await LoaderFixture.CreateAsync();
        await using var server = new ProposalLoopbackServer();
        var outside = CreatePrivateDirectory("contract-scribe-checkpoint-capacity");
        try
        {
            var configuration = Path.Join(outside, "consumer.json");
            await WriteConsumerLayerAsync(configuration, server.Endpoint);
            await File.WriteAllTextAsync(Path.Join(fixture.Root, "policy.json"), RequiredPolicy);
            var stateDirectory = Path.Join(outside, "state");
            Directory.CreateDirectory(stateDirectory);
            File.SetUnixFileMode(stateDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var statePath = Path.Join(stateDirectory, "checkpoint.json");
            var rejectedPath = Path.Join(stateDirectory, "rejected.json");
            var sourcePath = Path.Join(fixture.Root, "App", "App.cs");
            var credentialReads = 0;
            string? Credential(string _) { credentialReads++; throw new InvalidOperationException("Capacity admission cannot read credentials."); }
            async Task<CliExecutionResult> Run(CampaignOperation operation, string snapshot, string path)
            {
                var preflight = CampaignPreflight.Run(new(operation, fixture.Root, "App/App.csproj", "policy.json",
                    snapshot, path, configuration, null, "campaign.integration"), RepositoryRoot);
                return await CampaignCommandRunner.RunAsync(CliBuildIdentity.Current, preflight, CancellationToken.None,
                    Credential, targetLimit: new(0));
            }

            await File.WriteAllTextAsync(sourcePath, CapacitySource(128, documentedClass: true), new UTF8Encoding(false, true));
            var started = await Run(CampaignOperation.Start, "snapshot.capacity-small", statePath);
            Assert.Equal(5, started.ExitCode);
            Assert.Contains("\"outcome\":\"campaign.unresolved\"", started.StandardOutput, StringComparison.Ordinal);
            var before = await File.ReadAllBytesAsync(statePath);
            var parsed = CampaignStateJson.Parse(before);
            Assert.True(parsed.IsValid, parsed.FailureCode?.ToString());
            var state = parsed.Artifact!.State;
            Assert.True(before.Length < CampaignStateContract.MaximumArtifactUtf8Bytes);
            Assert.Equal(528, state.Batch.CompleteTargets.Length);
            Assert.Equal(512, state.TargetProgress.Count(row => row.Kind == CampaignTargetProgressKind.Excluded));
            Assert.Equal(16, state.TargetProgress.Count(row => row.Kind == CampaignTargetProgressKind.Deferred));
            Assert.Empty(state.Batch.SelectedTargetKeys);
            Assert.Equal(before, CampaignStateJson.Write(state));

            await File.WriteAllTextAsync(sourcePath, CapacitySource(164, documentedClass: true, deferredMethods: 0, largeBindings: true), new UTF8Encoding(false, true));
            foreach (var operation in new[] { CampaignOperation.Start, CampaignOperation.Resume })
            {
                var result = await Run(operation, "snapshot.capacity-large", operation == CampaignOperation.Start ? rejectedPath : statePath);
                Assert.Equal(4, result.ExitCode);
                using var envelope = JsonDocument.Parse(result.StandardOutput);
                Assert.Equal("state", envelope.RootElement.GetProperty("terminalLayer").GetString());
                Assert.Equal("campaign.checkpoint-too-large", envelope.RootElement.GetProperty("outcome").GetString());
                if (operation == CampaignOperation.Start) Assert.Equal(JsonValueKind.Null, envelope.RootElement.GetProperty("checkpointRevision").ValueKind);
                else Assert.Equal(state.CheckpointRevision, envelope.RootElement.GetProperty("checkpointRevision").GetInt64());
                Assert.Single(result.Diagnostics);
                Assert.Equal("campaign.checkpoint-too-large", result.Diagnostics[0].Code);
                Assert.False(File.Exists(rejectedPath));
                Assert.Equal(before, await File.ReadAllBytesAsync(statePath));
                Assert.Equal(0, credentialReads);
                Assert.Equal(0, server.RequestCount);
            }
            Assert.Equal(0, state.LineageCharges.OuterInvocations);
            Assert.Equal(0, state.LineageCharges.PatchValidationInvocations);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    // These legal two-byte identifier scalars encode as six-byte JSON escapes. The
    // complete plan fits C1's independent bounds while its checkpoint exceeds 4 MiB.
    private static string CapacitySource(int groups, bool documentedClass = false, int deferredMethods = 16, bool largeBindings = false) =>
        "namespace " + (largeBindings ? new string('\u00e9', 1000) : "Fixture") + ";\n" + (documentedClass ? "/// <summary>Provides fixture fields.</summary>\n" : "") + "public class App {\n"
        + string.Join("\n", Enumerable.Range(0, groups).Select(group => "public int "
            + string.Join(",", Enumerable.Range(0, 4).Select(member => "F" + group + "_" + member)) + ";"))
        + "\n" + string.Join("\n", Enumerable.Range(0, deferredMethods).Select(index => $"public void Deferred{index}() {{ }}")) + "\n}\n";

}
