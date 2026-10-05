using System.Text;
using System.Text.Json.Nodes;
using ContractScribe.Cli;
using ContractScribe.Core;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed partial class CampaignCliProcessTests
{
    [Fact]
    public async Task Production_provider_proposal_capacity_persists_observed_settlement_and_bounded_result()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await LoaderFixture.CreateAsync();
        await using var server = new ProposalLoopbackServer(summaryText: new string('\u754c', 1024), includeUsage: true);
        var outside = CreatePrivateDirectory("contract-scribe-completion-capacity");
        try
        {
            var configurationPath = Path.Join(outside, "consumer.json");
            await WriteConsumerLayerAsync(configurationPath, server.Endpoint);
            var consumer = JsonNode.Parse(await File.ReadAllTextAsync(configurationPath))!;
            consumer["scribeRequest"] = new JsonObject
            {
                ["styleProfileTemplate"] = new JsonObject
                {
                    ["summary"] = new JsonObject { ["maximumScalars"] = 2048 },
                },
            };
            await File.WriteAllTextAsync(configurationPath, consumer.ToJsonString(), new UTF8Encoding(false, true));
            await File.WriteAllTextAsync(Path.Join(fixture.Root, "policy.json"), RequiredPolicy);
            var sourcePath = Path.Join(fixture.Root, "App", "App.cs");
            var stateDirectory = Path.Join(outside, "state");
            Directory.CreateDirectory(stateDirectory);
            File.SetUnixFileMode(stateDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var baselinePath = Path.Join(stateDirectory, "baseline.json");
            var nearPath = Path.Join(stateDirectory, "near.json");
            CampaignPreflightResult Preflight(string path) => CampaignPreflight.Run(new(CampaignOperation.Start,
                fixture.Root, "App/App.csproj", "policy.json", "snapshot.provider-completion", path,
                configurationPath, null, "campaign.integration"), RepositoryRoot);
            var baselineSource = ProviderCapacitySource(900, 0);
            await File.WriteAllTextAsync(sourcePath, baselineSource, new UTF8Encoding(false, true));
            var preflight = Preflight(baselinePath);
            var configuration = preflight.Configuration.Document;
            var policy = configuration.CreateExecutionPolicy();
            CampaignCheckpointArtifact? baseline = null;
            Exception? failure = null;
            var host = new ProductionRepositorySessionHost(new ContractScribe.Core.Hosting.HostBuildProvenance(CliBuildIdentity.Current.SourceRevision));
            var planningResult = await host.RunAsync(new(fixture.Root, "App/App.csproj", preflight.PolicyBytes, PublicationTarget: null, PublishResult: false),
                new ProductionAuditHostControls(SessionConsumer: (bundle, token) =>
                {
                    try
                    {
                        var input = CampaignCommandRunner.CreatePlanningInput(preflight, configuration, policy, bundle, token, new(1));
                        var plan = CampaignPlanner.Plan(input);
                        Assert.Equal(663, plan.Batch.CompleteTargets.Length);
                        baseline = CampaignStateJson.CreateArtifact(CampaignStateFactory.CreateInitial(
                            configuration.ScribeRequest.StyleProfileTemplate.StyleProfileId,
                            configuration.ScribeRequest.StyleProfileTemplate.ExactProjection, configuration.CreateExecutionCapability(policy),
                            bundle.Session.InputIdentity, input, plan));
                    }
                    catch (Exception exception) { failure = exception; throw; }
                    return Task.CompletedTask;
                }), CancellationToken.None);
            Assert.True(failure is null, failure?.ToString());
            Assert.Equal(ContractScribe.Core.Hosting.HostExecutionOutcome.Succeeded, planningResult.Terminal.ExecutionOutcome);
            Assert.NotNull(baseline);
            async Task<CliExecutionResult> Start(string path) => await CampaignCommandRunner.RunAsync(
                CliBuildIdentity.Current, Preflight(path), CancellationToken.None, _ => "loopback-test-only", targetLimit: new(1));
            _ = await Start(baselinePath);
            var valid = CampaignStateJson.Parse(await File.ReadAllBytesAsync(baselinePath)).Artifact!.State;
            // The fitting completion must persist its full proposal and reach M2.
            // The independently enforced patch validator may reject this long summary.
            Assert.Null(valid.ActiveReservation);
            Assert.Equal(1, valid.LineageCharges.PatchValidationInvocations);
            Assert.DoesNotContain(valid.WorkItems, item => item.ClosedOutcome?.Code == CampaignWorkOutcomeCode.CompletedOverBound);
            Assert.Equal(1, server.RequestCount);
            Assert.Equal(31, valid.LineageCharges.InputTokens.Observed);
            Assert.Equal(7, valid.LineageCharges.OutputTokens.Observed);

            const int RemainingBytes = 2000;
            const int BytesPerNamespaceScalar = 164 * 4 * 6;
            var growth = CampaignStateContract.MaximumArtifactUtf8Bytes - RemainingBytes - baseline.ExactUtf8Json.Length;
            await File.WriteAllTextAsync(sourcePath, ProviderCapacitySource(900 + growth / BytesPerNamespaceScalar,
                growth % BytesPerNamespaceScalar), new UTF8Encoding(false, true));
            var result = await Start(nearPath);
            var bytes = await File.ReadAllBytesAsync(nearPath);
            var parsed = CampaignStateJson.Parse(bytes);
            Assert.True(parsed.IsValid, parsed.FailureCode?.ToString());
            var state = parsed.Artifact!.State;
            Assert.Equal(2, server.RequestCount);
            Assert.Null(state.ActiveReservation);
            var closed = Assert.Single(state.WorkItems.Where(item => item.ClosedOutcome?.Code == CampaignWorkOutcomeCode.CompletedOverBound));
            Assert.Equal(1, closed.OuterAttemptCount);
            Assert.NotNull(closed.ClosedOutcome!.ScribeResultCommitmentSha256);
            Assert.Equal(64, closed.ClosedOutcome.ScribeResultCommitmentSha256!.Length);
            Assert.Equal(1, state.LineageCharges.OuterInvocations);
            Assert.Equal(1, state.LineageCharges.ProviderRequests.Observed);
            Assert.Equal(1, state.LineageCharges.ProviderRequests.TotalCharged);
            Assert.Equal(31, state.LineageCharges.InputTokens.Observed);
            Assert.Equal(7, state.LineageCharges.OutputTokens.Observed);
            Assert.Equal(0, state.LineageCharges.PatchValidationInvocations);
            Assert.True(state.CheckpointRevision >= 4);
            Assert.Equal(CampaignTerminalKind.Exhausted, state.TerminalOutcome!.Kind);
            Assert.Contains("campaign.budget-exhausted", result.StandardOutput, StringComparison.Ordinal);
            Assert.True(bytes.Length <= CampaignStateContract.MaximumArtifactUtf8Bytes);
            Assert.Equal(bytes, CampaignStateJson.Write(state));
            Assert.DoesNotContain(new string('\u754c', 1024), await File.ReadAllTextAsync(sourcePath), StringComparison.Ordinal);
        }
        finally { Directory.Delete(outside, recursive: true); }
    }
}
