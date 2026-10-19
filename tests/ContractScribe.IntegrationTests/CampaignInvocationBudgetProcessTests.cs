using System.Text;
using System.Text.Json.Nodes;
using ContractScribe.Core;
using ContractScribe.Cli;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed partial class CampaignCliProcessTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_single_executable_member_reaches_M2_after_one_shared_request_and_restores_without_regeneration(bool includeUsage)
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await LoaderFixture.CreateAsync();
        await SetSingleWorkItemSourceAsync(fixture.Root);
        await File.WriteAllTextAsync(Path.Join(fixture.Root, "policy.json"), RequiredPolicy);
        var sourcePath = Path.Join(fixture.Root, "App", "App.cs");
        var original = await File.ReadAllBytesAsync(sourcePath);
        await using var server = new ProposalLoopbackServer(includeUsage: includeUsage);
        var outside = CreatePrivateDirectory("contract-scribe-single-resource-stop");
        try
        {
            var configuration = Path.Join(outside, "consumer.json");
            await WriteConsumerLayerAsync(configuration, server.Endpoint);
            var layer = JsonNode.Parse(await File.ReadAllTextAsync(configuration))!;
            layer["budgets"] = new JsonObject
            {
                ["invocation"] = new JsonObject { ["maximumProviderRequests"] = 1 },
                ["campaign"] = new JsonObject { ["maximumCandidatesPerBlock"] = 3 },
            };
            await File.WriteAllTextAsync(configuration, layer.ToJsonString());
            var directory = Path.Join(outside, "state");
            Directory.CreateDirectory(directory);
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var path = Path.Join(directory, "checkpoint.json");
            var started = await RunAsync(Args("start", fixture.Root, path, configuration, "snapshot.single-stop"), TimeSpan.FromMinutes(3));
            AssertControlledCampaign(started, "single-resource-stop");
            var saved = CampaignStateJson.Parse(await File.ReadAllBytesAsync(path)).Artifact!.State;
            Assert.Equal(CampaignWorkStatus.Accepted, Assert.Single(saved.WorkItems).Status);
            Assert.Single(saved.CandidateObservation!.AcceptedWorkItemKeys);
            Assert.Equal(1, server.RequestCount);
            Assert.Equal(1, saved.LineageCharges.ProviderRequests.Observed);
            Assert.Equal(includeUsage, saved.DispatchObservations[0].Usage is not null);
            var resumed = await RunAsync(Args("resume", fixture.Root, path, configuration, "snapshot.single-stop"), TimeSpan.FromMinutes(3));
            AssertControlledCampaign(resumed, "single-resource-stop-resume");
            var final = CampaignStateJson.Parse(await File.ReadAllBytesAsync(path)).Artifact!.State;
            Assert.Equal(CampaignWorkStatus.Accepted, Assert.Single(final.WorkItems).Status);
            Assert.Equal(saved.LineageCharges.ProviderRequests, final.LineageCharges.ProviderRequests);
            Assert.Equal(saved.LineageCharges.InputTokens, final.LineageCharges.InputTokens);
            Assert.Equal(saved.LineageCharges.OutputTokens, final.LineageCharges.OutputTokens);
            Assert.Equal(1, server.RequestCount);
            Assert.Equal(original, await File.ReadAllBytesAsync(sourcePath));
        }
        finally { Directory.Delete(outside, recursive: true); }
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    public async Task Shared_request_stop_preserves_the_accepted_candidate_and_fresh_action_finishes_original_membership(bool includeUsage, int initialMaximumRequests)
    {
        if (!OperatingSystem.IsLinux() && !ContractScribe.Roslyn.DarwinFileSystem.IsSupported) return;
        await using var fixture = await LoaderFixture.CreateAsync();
        var sourcePath = Path.Join(fixture.Root, "App", "App.cs");
        await File.WriteAllTextAsync(sourcePath,
            "namespace Fixture;\n/// <summary>Provides fixture operations.</summary>\npublic static class App\n{\n    public static void First() { }\n    public static void Second() { }\n}\n");
        await File.WriteAllTextAsync(Path.Join(fixture.Root, "policy.json"), RequiredPolicy);
        var originalSource = await File.ReadAllBytesAsync(sourcePath);
        await using var server = new ProposalLoopbackServer(includeUsage: includeUsage);
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
                ["invocation"] = new JsonObject { ["maximumTargets"] = 2, ["maximumProviderRequests"] = initialMaximumRequests },
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
            if (!includeUsage)
                Assert.True(paused.State.LineageCharges.InputTokens.ConservativeUnobserved > 0);
            var batch = paused.State.Batch.Identity;
            layer["budgets"]!["invocation"]!["maximumProviderRequests"] = 2;
            await File.WriteAllTextAsync(configurationPath, layer.ToJsonString(), new UTF8Encoding(false, true));
            var completed = await RunAsync(Args("resume", fixture.Root, statePath, configurationPath, "snapshot.c4"),
                timeout: TimeSpan.FromMinutes(3));
            AssertControlledCampaign(completed, "fresh-action");
            // Known usage admits the explicit finish; unknown usage stops after
            // the returned proposal, which still closes the final work item.
            Assert.Equal(includeUsage ? 3 : 2, server.RequestCount);
            var final = CampaignStateJson.Parse(await File.ReadAllBytesAsync(statePath)).Artifact!;
            Assert.Equal(batch, final.State.Batch.Identity);
            Assert.True(final.State.WorkItems.Count(item => item.Status == CampaignWorkStatus.Accepted) == 2,
                $"resume={completed.Stdout}; terminal={final.State.TerminalOutcome}; works={string.Join(",", final.State.WorkItems.Select(item => $"{item.Status}:{item.ClosedOutcome?.Code}"))}; charges={System.Text.Json.JsonSerializer.Serialize(final.State.LineageCharges)}");
            Assert.Equal(includeUsage ? 3 : 2, final.State.LineageCharges.ProviderRequests.Observed);
            Assert.Equal(0, final.State.LineageCharges.ProviderRequests.ConservativeUnobserved);
            Assert.Equal(CampaignTerminalReason.AllWorkClosed, final.State.TerminalOutcome!.Reason);
            Assert.Equal(originalSource, await File.ReadAllBytesAsync(sourcePath));
        }
        finally { Directory.Delete(outside, recursive: true); }
    }
    [Fact]
    public async Task Fresh_action_restores_accepted_failure_and_retry_wait_with_a_new_process_context()
    {
        if (!OperatingSystem.IsLinux() && !ContractScribe.Roslyn.DarwinFileSystem.IsSupported) return;
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
            Assert.Equal(3, final.State.LineageCharges.ProviderRequests.Observed);
            Assert.Equal(0, final.State.LineageCharges.ProviderRequests.ConservativeUnobserved);
            Assert.Equal(3, server.RequestCount);
            Assert.Null(final.State.ActiveReservation);
            Assert.Null(accepted.PausedProviderAttempt);
            Assert.Equal(CampaignTerminalReason.AllWorkClosed, final.State.TerminalOutcome!.Reason);
            Assert.Equal(originalSource, await File.ReadAllBytesAsync(sourcePath));
        }
        finally { Directory.Delete(outside, recursive: true); }
    }

}
