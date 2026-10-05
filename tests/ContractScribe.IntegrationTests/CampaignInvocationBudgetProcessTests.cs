using System.Text;
using System.Text.Json.Nodes;
using ContractScribe.Core;
using ContractScribe.Cli;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed partial class CampaignCliProcessTests
{
    [Fact]
    public async Task Shared_request_stop_preserves_the_accepted_candidate_and_fresh_action_finishes_original_membership()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await LoaderFixture.CreateAsync();
        var sourcePath = Path.Join(fixture.Root, "App", "App.cs");
        await File.WriteAllTextAsync(sourcePath,
            "namespace Fixture;\n/// <summary>Provides fixture operations.</summary>\npublic static class App\n{\n    public static void First() { }\n    public static void Second() { }\n}\n");
        await File.WriteAllTextAsync(Path.Join(fixture.Root, "policy.json"), RequiredPolicy);
        var originalSource = await File.ReadAllBytesAsync(sourcePath);
        await using var server = new ProposalLoopbackServer(includeUsage: true);
        var outside = CreatePrivateDirectory("contract-scribe-c4-shared-budget");
        try
        {
            var configurationPath = Path.Join(outside, "consumer.json");
            var stateDirectory = Path.Join(outside, "state");
            Directory.CreateDirectory(stateDirectory);
            File.SetUnixFileMode(stateDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var statePath = Path.Join(stateDirectory, "checkpoint.json");
            await WriteConsumerLayerAsync(configurationPath, server.Endpoint);
            var layer = JsonNode.Parse(await File.ReadAllTextAsync(configurationPath))!.AsObject();
            layer["budgets"] = new JsonObject
            {
                ["invocation"] = new JsonObject { ["maximumTargets"] = 2, ["maximumProviderRequests"] = 1 },
                // C4 retains G4 quotas: first acceptance, stop handoff, resume reconstruction and the two-block acceptance.
                ["campaign"] = new JsonObject { ["maximumCandidatesPerBlock"] = 4 },
            };
            await File.WriteAllTextAsync(configurationPath, layer.ToJsonString(), new UTF8Encoding(false, true));
            var stopped = await RunAsync(Args("start", fixture.Root, statePath, configurationPath, "snapshot.c4"),
                timeout: TimeSpan.FromMinutes(3));
            AssertControlledCampaign(stopped, "shared-request-stop");
            Assert.Contains("campaign.invocation-budget-exhausted", stopped.Stdout, StringComparison.Ordinal);
            Assert.Equal(1, server.RequestCount);
            var paused = CampaignStateJson.Parse(await File.ReadAllBytesAsync(statePath)).Artifact!;
            Assert.Null(paused.State.ActiveReservation);
            Assert.Null(paused.State.TerminalOutcome);
            Assert.Single(paused.State.WorkItems, item => item.Status == CampaignWorkStatus.Accepted);
            Assert.Single(paused.State.WorkItems, item => item.Status == CampaignWorkStatus.Planned && item.OuterAttemptCount == 0);
            Assert.NotNull(paused.State.AcceptedCandidateOrigin);
            Assert.Single(paused.State.CandidateObservation!.AcceptedWorkItemKeys);
            Assert.Equal(1, paused.State.LineageCharges.ProviderRequests.Observed);
            Assert.Equal(0, paused.State.LineageCharges.ProviderRequests.ConservativeUnobserved);
            var batch = paused.State.Batch.Identity;
            layer["budgets"]!["invocation"]!["maximumProviderRequests"] = 2;
            await File.WriteAllTextAsync(configurationPath, layer.ToJsonString(), new UTF8Encoding(false, true));
            var completed = await RunAsync(Args("resume", fixture.Root, statePath, configurationPath, "snapshot.c4"),
                timeout: TimeSpan.FromMinutes(3));
            AssertControlledCampaign(completed, "fresh-action");
            Assert.Equal(2, server.RequestCount);
            var final = CampaignStateJson.Parse(await File.ReadAllBytesAsync(statePath)).Artifact!;
            Assert.Equal(batch, final.State.Batch.Identity);
            Assert.Equal(2, final.State.WorkItems.Count(item => item.Status == CampaignWorkStatus.Accepted));
            Assert.Equal(2, final.State.LineageCharges.ProviderRequests.Observed);
            Assert.Equal(0, final.State.LineageCharges.ProviderRequests.ConservativeUnobserved);
            Assert.Equal(CampaignTerminalReason.AllWorkClosed, final.State.TerminalOutcome!.Reason);
            Assert.Equal(originalSource, await File.ReadAllBytesAsync(sourcePath));
        }
        finally { Directory.Delete(outside, recursive: true); }
    }
    [Fact]
    public async Task Fresh_action_restores_accepted_failure_and_retry_wait_with_a_new_process_context()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await LoaderFixture.CreateAsync();
        await SetSingleWorkItemSourceAsync(fixture.Root);
        await File.WriteAllTextAsync(Path.Join(fixture.Root, "policy.json"), RequiredPolicy);
        var sourcePath = Path.Join(fixture.Root, "App", "App.cs");
        var originalSource = await File.ReadAllBytesAsync(sourcePath);
        await using var server = new ProposalLoopbackServer("retry-wait", includeUsage: true);
        var outside = CreatePrivateDirectory("contract-scribe-c4-retry-resume");
        try
        {
            var configurationPath = Path.Join(outside, "consumer.json");
            var stateDirectory = Path.Join(outside, "state");
            Directory.CreateDirectory(stateDirectory);
            File.SetUnixFileMode(stateDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var statePath = Path.Join(stateDirectory, "checkpoint.json");
            await WriteConsumerLayerAsync(configurationPath, server.Endpoint);
            var seeded = await RunAsync(Args("start", fixture.Root, statePath, configurationPath, "snapshot.c4.retry"),
                timeout: TimeSpan.FromMinutes(3));
            AssertControlledCampaign(seeded, "accepted-failure-before-fresh-action");
            Assert.Contains("campaign.invocation-budget-exhausted", seeded.Stdout, StringComparison.Ordinal);
            var paused = CampaignStateJson.Parse(await File.ReadAllBytesAsync(statePath)).Artifact!;
            var work = Assert.Single(paused.State.WorkItems.Where(item => item.OuterAttemptCount > 0));
            var attempt = Assert.IsType<CampaignPausedProviderAttempt>(work.PausedProviderAttempt);
            Assert.Equal(1, attempt.RetryProgress.RetryableFailureCount);
            Assert.Equal(1000, attempt.RetryProgress.PendingRetryAfterMilliseconds);
            Assert.Equal(1, server.RequestCount);
            Assert.Equal(1, paused.State.LineageCharges.OuterInvocations);
            var stoppedLayer = JsonNode.Parse(await File.ReadAllTextAsync(configurationPath))!;
            stoppedLayer["budgets"] = new JsonObject { ["invocation"] = new JsonObject { ["maximumProviderRequests"] = 0 } };
            await File.WriteAllTextAsync(configurationPath, stoppedLayer.ToJsonString());
            var credentialReads = 0;
            var preflight = CampaignPreflight.Run(new(CampaignOperation.Resume, fixture.Root, "App/App.csproj", "policy.json",
                "snapshot.c4.retry", statePath, configurationPath, null, "campaign.integration"), RepositoryRoot);
            var stopped = await CampaignCommandRunner.RunAsync(CliBuildIdentity.Current, preflight, CancellationToken.None,
                _ => { credentialReads++; throw new InvalidOperationException("A paused Action without provider quota cannot request credentials."); });
            Assert.Contains("campaign.invocation-budget-exhausted", stopped.StandardOutput, StringComparison.Ordinal);
            Assert.Equal(3, stopped.ExitCode);
            Assert.Equal(0, credentialReads);
            Assert.Equal(paused.ExactUtf8Json.ToArray(), await File.ReadAllBytesAsync(statePath));
            Assert.Equal(1, server.RequestCount);
            var stoppedProcess = await RunAsync(Args("resume", fixture.Root, statePath, configurationPath, "snapshot.c4.retry"),
                timeout: TimeSpan.FromMinutes(3));
            AssertCampaign(stoppedProcess, 3, "campaign.invocation-budget-exhausted", paused.CheckpointRevision);
            Assert.Equal(paused.ExactUtf8Json.ToArray(), await File.ReadAllBytesAsync(statePath));
            Assert.Equal(1, server.RequestCount);
            await WriteConsumerLayerAsync(configurationPath, server.Endpoint);
            var completed = await RunAsync(Args("resume", fixture.Root, statePath, configurationPath, "snapshot.c4.retry"),
                timeout: TimeSpan.FromMinutes(3));
            AssertControlledCampaign(completed, "fresh-process-retry-wait");
            var final = CampaignStateJson.Parse(await File.ReadAllBytesAsync(statePath)).Artifact!;
            var accepted = Assert.Single(final.State.WorkItems.Where(item => item.Status == CampaignWorkStatus.Accepted));
            Assert.Equal(1, accepted.OuterAttemptCount);
            Assert.Equal(1, final.State.LineageCharges.OuterInvocations);
            Assert.Equal(2, final.State.LineageCharges.ProviderRequests.Observed);
            Assert.Equal(0, final.State.LineageCharges.ProviderRequests.ConservativeUnobserved);
            Assert.Equal(2, server.RequestCount);
            Assert.Null(final.State.ActiveReservation);
            Assert.Null(accepted.PausedProviderAttempt);
            Assert.Equal(CampaignTerminalReason.AllWorkClosed, final.State.TerminalOutcome!.Reason);
            Assert.Equal(originalSource, await File.ReadAllBytesAsync(sourcePath));
        }
        finally { Directory.Delete(outside, recursive: true); }
    }

}
