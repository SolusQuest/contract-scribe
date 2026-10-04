using System.Text;
using System.Text.Json.Nodes;
using ContractScribe.Core;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed partial class CampaignCliProcessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LifetimeCapLayerChanges_StopBeforeDispatchAndResumeWithoutProviderReplay(bool monetary)
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await LoaderFixture.CreateAsync();
        await SetSingleWorkItemSourceAsync(fixture.Root);
        await File.WriteAllTextAsync(Path.Join(fixture.Root, "policy.json"), RequiredPolicy);
        await using var server = new ProposalLoopbackServer(includeUsage: true);
        var outside = CreatePrivateDirectory("contract-scribe-c3-lifetime-caps");
        var configurationPath = Path.Join(outside, "campaign.json");
        var stateDirectory = Path.Join(outside, "state");
        Directory.CreateDirectory(stateDirectory);
        File.SetUnixFileMode(stateDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var statePath = Path.Join(stateDirectory, "checkpoint.json");
        var field = monetary ? "maximumCostMicrounits" : "maximumProviderRequests";
        var layer = new JsonObject
        {
            ["consumerConfigurationVersion"] = 1,
            ["provider"] = new JsonObject { ["endpoint"] = server.Endpoint.AbsoluteUri },
            ["budgets"] = new JsonObject { ["campaign"] = new JsonObject { [field] = 0 } },
            ["costPolicy"] = new JsonObject
            {
                ["currencyId"] = "currency.usd",
                ["ratePolicyId"] = "rate.loopback",
                ["cachedInputMicrounitsPerMillion"] = 1_000_000,
                ["uncachedInputMicrounitsPerMillion"] = 2_000_000,
                ["outputMicrounitsPerMillion"] = 1_000_000,
                ["reasoningMicrounitsPerMillion"] = 3_000_000,
            },
        };
        try
        {
            await WriteLayerAsync();
            var stopped = await RunAsync(Args("start", fixture.Root, statePath, configurationPath, "snapshot.c3"),
                TimeSpan.FromMinutes(5));
            var exhausted = ReadState();
            AssertCampaign(stopped, 3, "campaign.budget-exhausted", exhausted.CheckpointRevision);
            Assert.Equal(CampaignTerminalReason.LifetimeCap, exhausted.State.TerminalOutcome!.Reason);
            Assert.Equal(0, server.RequestCount);
            Assert.Equal(0, exhausted.State.LineageCharges.OuterInvocations);
            Assert.Null(exhausted.State.ActiveReservation);
            Assert.Equal(0, exhausted.State.ConfiguredCeilings.ScribeRunLimits.MaximumCostMicrounits);

            layer["budgets"]!["campaign"]![field] = null;
            await WriteLayerAsync();
            var resumed = await RunAsync(Args("resume", fixture.Root, statePath, configurationPath, "snapshot.c3"),
                TimeSpan.FromMinutes(5));
            var accepted = ReadState();
            AssertCampaign(resumed, 0, "campaign.complete", accepted.CheckpointRevision);
            Assert.Equal(1, server.RequestCount);
            Assert.NotNull(accepted.State.CandidateObservation);
            Assert.True(accepted.State.LineageCharges.CostMicrounits.ConservativeUnobserved > 0);
            Assert.Equal(0, accepted.State.LineageCharges.CostMicrounits.Observed);
            var proposal = accepted.State.WorkItems.Single(item => item.Status == CampaignWorkStatus.Accepted)
                .TrustedProposal!.ProposalCommitmentSha256;

            layer["budgets"]!["campaign"]![field] = 0;
            await WriteLayerAsync();
            var lowered = await RunAsync(Args("resume", fixture.Root, statePath, configurationPath, "snapshot.c3"),
                TimeSpan.FromMinutes(5));
            var retained = ReadState();
            AssertCampaign(lowered, 3, "campaign.budget-exhausted", retained.CheckpointRevision);
            Assert.Equal(CampaignTerminalReason.LifetimeCap, retained.State.TerminalOutcome!.Reason);
            Assert.Equal(accepted.State.LineageCharges, retained.State.LineageCharges);
            Assert.Equal(accepted.State.CandidateObservation, retained.State.CandidateObservation);
            Assert.Equal(accepted.State.AcceptedCandidateOrigin!.CheckpointSha256 ?? accepted.Sha256,
                retained.State.AcceptedCandidateOrigin!.CheckpointSha256);
            Assert.Equal(1, server.RequestCount);

            layer["budgets"]!["campaign"]![field] = null;
            await WriteLayerAsync();
            var cleared = await RunAsync(Args("resume", fixture.Root, statePath, configurationPath, "snapshot.c3"),
                TimeSpan.FromMinutes(5));
            var final = ReadState();
            AssertCampaign(cleared, 0, "campaign.complete", final.CheckpointRevision);
            Assert.Equal(1, server.RequestCount);
            Assert.Equal(proposal, final.State.WorkItems.Single(item => item.Status == CampaignWorkStatus.Accepted)
                .TrustedProposal!.ProposalCommitmentSha256);
            Assert.Equal(accepted.State.LineageCharges.ProviderRequests, final.State.LineageCharges.ProviderRequests);
            Assert.Equal(accepted.State.LineageCharges.CostMicrounits, final.State.LineageCharges.CostMicrounits);
        }
        finally { Directory.Delete(outside, recursive: true); }

        Task WriteLayerAsync() => File.WriteAllTextAsync(configurationPath, layer.ToJsonString(), new UTF8Encoding(false, true));
        CampaignCheckpointArtifact ReadState()
        {
            var parsed = CampaignStateJson.Parse(File.ReadAllBytes(statePath));
            Assert.True(parsed.IsValid);
            return parsed.Artifact!;
        }
    }
}
