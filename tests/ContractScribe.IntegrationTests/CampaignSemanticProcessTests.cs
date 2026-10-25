using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Cli;
using ContractScribe.Core;
using ContractScribe.IntegrationTests;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed partial class CampaignCliProcessTests
{
    [ContractScribe.TestSupport.NativeFileSystemTheory]
    [InlineData(CampaignProcessBoundaryHooks.SemanticAuthorizationReplacementScope + "." + CampaignProcessBoundaryHooks.InReplacement, false)]
    [InlineData(CampaignProcessBoundaryHooks.SemanticAuthorizationReplacementScope + "." + CampaignProcessBoundaryHooks.AfterReplacementBeforeReadback, true)]
    [InlineData(CampaignProcessBoundaryHooks.SemanticAuthorizationAfterReadback, true)]
    public async Task Fresh_process_retires_only_the_actual_semantic_checkpoint_exposure_without_restoring_a_query(
        string boundary, bool semanticCommitted)
    {
        if (!OperatingSystem.IsLinux() && !DarwinFileSystem.IsSupported) return;
        await using var fixture = await LoaderFixture.CreateAsync();
        await SetSingleWorkItemSourceAsync(fixture.Root);
        await File.WriteAllTextAsync(Path.Join(fixture.Root, "policy.json"), RequiredPolicy);
        await using var server = new ProposalLoopbackServer("semantic");
        var outside = CreatePrivateDirectory("contract-scribe-semantic-recovery");
        try
        {
            var configuration = Path.Join(outside, "consumer.json");
            await WriteConsumerLayerAsync(configuration, server.Endpoint);
            var stateDirectory = Path.Join(outside, "state");
            Directory.CreateDirectory(stateDirectory);
            File.SetUnixFileMode(stateDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var statePath = Path.Join(stateDirectory, "checkpoint.json");
            var acknowledgement = Path.Join(outside, "semantic.ack");
            using var running = Start(Args("start", fixture.Root, statePath, configuration, "snapshot.semantic"),
                new Dictionary<string, string?>
                {
                    ["DOTNET_STARTUP_HOOKS"] = StartupHookPath,
                    ["CONTRACTSCRIBE_TEST_CAMPAIGN_HOOK_NAME"] = boundary,
                    ["CONTRACTSCRIBE_TEST_CAMPAIGN_HOOK_ACK"] = acknowledgement,
                });
            try
            {
                await WaitForFileAsync(acknowledgement, running, "semantic-checkpoint", TimeSpan.FromMinutes(3));
                await StopAsync(running);
                var interrupted = await running.CompleteAsync();
                Assert.Empty(interrupted.Stdout);
                Assert.Empty(interrupted.Stderr);
            }
            finally { await StopAsync(running); }
            var parsed = CampaignStateJson.Parse(await File.ReadAllBytesAsync(statePath));
            Assert.True(parsed.IsValid, parsed.FailureCode?.ToString());
            var saved = parsed.Artifact!.State;
            var outstanding = Assert.IsType<CampaignProviderReservation>(saved.ActiveReservation);
            Assert.Equal(semanticCommitted ? CampaignProviderHostPhase.SemanticTool : CampaignProviderHostPhase.Continuation,
                outstanding.HostPhase);
            if (semanticCommitted) Assert.Equal(70000, outstanding.Exposure.ElapsedMilliseconds);
            Assert.Equal(1, server.RequestCount);
            Assert.Equal(1, saved.LineageCharges.ProviderRequests.Observed);
            Assert.All(saved.DispatchObservations, row => Assert.Equal(CampaignDispatchObservationState.Settled, row.State));
            Assert.All(saved.WorkItems, work => Assert.Null(work.TrustedProposal));

            var layer = JsonNode.Parse(await File.ReadAllTextAsync(configuration))!;
            // Zero new-target quota still permits an already-started attempt to
            // resume. Deny new physical sends explicitly for this retirement proof.
            layer["budgets"] = new JsonObject
            {
                ["invocation"] = new JsonObject
                { ["maximumTargets"] = 0, ["maximumProviderRequests"] = 0 }
            };
            await File.WriteAllTextAsync(configuration, layer.ToJsonString());
            var resumed = await RunAsync(Args("resume", fixture.Root, statePath, configuration, "snapshot.semantic"),
                timeout: TimeSpan.FromMinutes(5));
            AssertControlledCampaign(resumed, "semantic-fresh-process");
            var retiredBytes = await File.ReadAllBytesAsync(statePath);
            var retired = CampaignStateJson.Parse(retiredBytes).Artifact!.State;
            Assert.Null(retired.ActiveReservation);
            Assert.Equal(saved.LineageCharges.ActiveElapsedMilliseconds.Observed,
                retired.LineageCharges.ActiveElapsedMilliseconds.Observed);
            Assert.Equal(saved.LineageCharges.ActiveElapsedMilliseconds.ConservativeUnobserved + outstanding.Exposure.ElapsedMilliseconds,
                retired.LineageCharges.ActiveElapsedMilliseconds.ConservativeUnobserved);
            Assert.Equal(saved.LineageCharges.ProviderRequests, retired.LineageCharges.ProviderRequests);
            Assert.Equal(saved.LineageCharges.InputTokens, retired.LineageCharges.InputTokens);
            Assert.Equal(saved.LineageCharges.OutputTokens, retired.LineageCharges.OutputTokens);
            Assert.Equal(saved.LineageCharges.CostMicrounits, retired.LineageCharges.CostMicrounits);
            Assert.Equal(saved.LineageCharges.OuterInvocations, retired.LineageCharges.OuterInvocations);
            Assert.Equal(outstanding.AttemptId, retired.WorkItems.Single(item => item.WorkItemKey == outstanding.WorkItemKey).PausedProviderAttempt!.AttemptId);
            Assert.Equal(1, server.RequestCount);
            var second = await RunAsync(Args("resume", fixture.Root, statePath, configuration, "snapshot.semantic"),
                timeout: TimeSpan.FromMinutes(5));
            AssertControlledCampaign(second, "semantic-second-resume");
            Assert.Equal(retiredBytes, await File.ReadAllBytesAsync(statePath));
            Assert.Equal(1, server.RequestCount);
        }
        finally { Directory.Delete(outside, recursive: true); }
    }

    internal sealed partial class ProposalLoopbackServer
    {
        private static byte[] CreateSemanticResponse(byte[] body, int? pageSize = 1, bool sharedBatch = false,
            int targetCount = 1)
        {
            using var wire = JsonDocument.Parse(body);
            var messages = wire.RootElement.GetProperty("messages");
            if (messages.EnumerateArray().Any(message => message.GetProperty("role").GetString() == "tool"))
                return CreateSharedResponse(body, batch: sharedBatch, includeUsage: true, usageOutputTokens: 0);
            using var targets = JsonDocument.Parse(messages[4].GetProperty("content").GetString()!);
            var selected = targets.RootElement.GetProperty("executableTargetIds").EnumerateArray()
                .Take(targetCount).Select(target => target.GetString()).ToArray();
            var tool = wire.RootElement.GetProperty("tools").EnumerateArray().Select(item => item.GetProperty("function"))
                .Single(item => item.GetProperty("parameters").TryGetProperty("properties", out var properties)
                    && properties.TryGetProperty("targetId", out _) && properties.TryGetProperty("pageSize", out _));
            var calls = selected.Select((target, index) =>
            {
                var arguments = new JsonObject { ["targetId"] = target };
                if (pageSize is { } size) arguments["pageSize"] = size;
                return new
                {
                    id = targetCount == 1 ? "call.semantic" : "call.semantic." + index,
                    type = "function",
                    function = new { name = tool.GetProperty("name").GetString(), arguments = arguments.ToJsonString() },
                };
            }).ToArray();
            return JsonSerializer.SerializeToUtf8Bytes(new
            {
                choices = new[] { new { index = 0, message = new
                {
                    role = "assistant", content = (string?)null, reasoning_content = "synthetic-semantic-read",
                    tool_calls = calls,
                }, finish_reason = "tool_calls" } },
                usage = new
                {
                    prompt_tokens = 31,
                    prompt_cache_hit_tokens = 29,
                    prompt_cache_miss_tokens = 2,
                    completion_tokens = 7,
                    total_tokens = 38
                },
            });
        }
    }
}
