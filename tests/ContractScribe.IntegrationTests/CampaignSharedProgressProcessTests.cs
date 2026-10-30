using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Cli;
using ContractScribe.Core;
using ContractScribe.IntegrationTests;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed partial class CampaignCliProcessTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Shared_loopback_selects_scoped_reads_or_component_skips_among_terminal_schemas(bool terminalFirst, bool componentSkip)
    {
        var reader = new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "alias.read",
                ["parameters"] = new JsonObject
                {
                    ["properties"] = new JsonObject
                    { ["scopeId"] = new JsonObject(), ["startLine"] = new JsonObject() }
                },
            }
        };
        var terminal = new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "cs_terminal",
                ["parameters"] = new JsonObject { ["oneOf"] = new JsonArray(new JsonObject()) },
            }
        };
        var facts = new JsonObject
        {
            ["executableTargetIds"] = new JsonArray("target.first"),
            ["progress"] = new JsonArray(),
            ["restoredClosedTargetIds"] = new JsonArray(),
            ["evidenceReferences"] = new JsonArray(),
            ["nextSequence"] = 1,
            ["manifest"] = new JsonArray(new JsonObject
            {
                ["targetId"] = "target.first",
                ["sourceScopeId"] = "target.first",
                ["applicableComponents"] = componentSkip ? new JsonArray(new JsonObject
                { ["kind"] = "component.parameter", ["identity"] = "parameter/0", ["name"] = "value" }) : new JsonArray()
            }),
        };
        var messages = new JsonArray(Enumerable.Range(0, 3).Select(_ => (JsonNode)new JsonObject
        { ["role"] = "system", ["content"] = "synthetic" }).ToArray());
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = new JsonObject { ["artifactSha256"] = new string('a', 64) }.ToJsonString() });
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = facts.ToJsonString() });
        var wire = new JsonObject
        {
            ["messages"] = messages,
            ["tools"] = terminalFirst ? new JsonArray(terminal, reader) : new JsonArray(reader, terminal)
        };
        using var response = JsonDocument.Parse(ProposalLoopbackServer.CreateSharedResponse(JsonSerializer.SerializeToUtf8Bytes(wire)));
        var firstCallId = response.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("tool_calls")[0].GetProperty("id").GetString();
        messages.Add(new JsonObject { ["role"] = "tool", ["content"] = "{}" });
        using var continued = JsonDocument.Parse(ProposalLoopbackServer.CreateSharedResponse(JsonSerializer.SerializeToUtf8Bytes(wire)));
        var nextCallId = continued.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("tool_calls")[0].GetProperty("id").GetString();
        Assert.NotEqual(firstCallId, nextCallId);
        var call = response.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("tool_calls")[0].GetProperty("function");
        using var arguments = JsonDocument.Parse(call.GetProperty("arguments").GetString()!);
        Assert.Equal(componentSkip ? "cs_terminal" : "alias.read", call.GetProperty("name").GetString());
        if (componentSkip)
            Assert.Equal("skip", arguments.RootElement.GetProperty("items")[0].GetProperty("result").GetProperty("kind").GetString());
        else Assert.Equal("target.first", arguments.RootElement.GetProperty("scopeId").GetString());
    }

    [ContractScribe.TestSupport.NativeFileSystemFact]
    public async Task Shared_accepted_members_reconstruct_in_a_fresh_process_without_regeneration_or_historical_recharge()
    {
        if (!OperatingSystem.IsLinux() && !ContractScribe.Roslyn.DarwinFileSystem.IsSupported) return;
        await using var fixture = await LoaderFixture.CreateAsync();
        var firstPath = Path.Join(fixture.Root, "App", "App.cs");
        var otherPath = Path.Join(fixture.Root, "App", "Other.cs");
        await File.WriteAllTextAsync(firstPath,
            "namespace Fixture;\n/// <summary>Provides fixture operations.</summary>\npublic static partial class App\n{\n    public static partial void First();\n    public static void Second() { }\n}\n");
        await File.WriteAllTextAsync(otherPath,
            "namespace Fixture;\npublic static partial class App\n{\n    public static partial void First() { }\n    public static void Third() { }\n}\n");
        await File.WriteAllTextAsync(Path.Join(fixture.Root, "policy.json"), RequiredPolicy);
        var originals = new[] { await File.ReadAllBytesAsync(firstPath), await File.ReadAllBytesAsync(otherPath) };
        await using var server = new ProposalLoopbackServer("shared");
        var outside = CreatePrivateDirectory("contract-scribe-shared-recovery");
        try
        {
            var configuration = Path.Join(outside, "consumer.json");
            await WriteConsumerLayerAsync(configuration, server.Endpoint);
            var stateDirectory = Path.Join(outside, "state");
            Directory.CreateDirectory(stateDirectory);
            File.SetUnixFileMode(stateDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var statePath = Path.Join(stateDirectory, "checkpoint.json");
            var acknowledgement = Path.Join(outside, "progress.ack");
            using var running = Start(Args("start", fixture.Root, statePath, configuration, "snapshot.shared"),
                new Dictionary<string, string?>
                {
                    ["DOTNET_STARTUP_HOOKS"] = StartupHookPath,
                    ["CONTRACTSCRIBE_TEST_CAMPAIGN_HOOK_NAME"] = CampaignProcessBoundaryHooks.ScribeMemberAfterReadback,
                    ["CONTRACTSCRIBE_TEST_CAMPAIGN_HOOK_OCCURRENCE"] = "3",
                    ["CONTRACTSCRIBE_TEST_CAMPAIGN_HOOK_ACK"] = acknowledgement,
                });
            try
            {
                await WaitForFileAsync(acknowledgement, running, "shared-progress", TimeSpan.FromMinutes(3));
                await StopAsync(running);
                var interrupted = await running.CompleteAsync();
                Assert.Empty(interrupted.Stdout);
                Assert.Empty(interrupted.Stderr);
            }
            finally { await StopAsync(running); }

            var parsed = CampaignStateJson.Parse(await File.ReadAllBytesAsync(statePath));
            Assert.True(parsed.IsValid, parsed.FailureCode?.ToString());
            var saved = parsed.Artifact!.State;
            Assert.Equal(3, saved.WorkItems.Length);
            Assert.All(saved.WorkItems, work => Assert.Equal(CampaignWorkStatus.ProposalComplete, work.Status));
            Assert.Null(saved.CandidateObservation);
            Assert.Equal(3, saved.ScribeProgress!.Members.Count(member => member.ContentSha256 is not null));
            Assert.Equal(2, saved.ScribeProgress.NextSequence);
            Assert.Equal(1, saved.LineageCharges.OuterInvocations);
            Assert.Equal(2, server.RequestCount); // Repository reads, then one three-member submit.
            Assert.Equal(server.RequestCount, saved.DispatchObservations.Length);
            Assert.All(saved.DispatchObservations, row => Assert.Equal(CampaignDispatchObservationState.Settled, row.State));
            var requests = server.RequestCount;
            var layer = JsonNode.Parse(await File.ReadAllTextAsync(configuration))!;
            layer["budgets"] = new JsonObject { ["invocation"] = new JsonObject { ["maximumTargets"] = 0 } };
            await File.WriteAllTextAsync(configuration, layer.ToJsonString());

            var recovered = await RunAsync(Args("resume", fixture.Root, statePath, configuration, "snapshot.shared"),
                timeout: TimeSpan.FromMinutes(5));
            AssertControlledCampaign(recovered, "shared-fresh-process");
            var final = CampaignStateJson.Parse(await File.ReadAllBytesAsync(statePath)).Artifact!.State;
            Assert.All(final.WorkItems, work => Assert.Equal(CampaignWorkStatus.Accepted, work.Status));
            Assert.Equal(saved.Batch.Identity, final.Batch.Identity);
            Assert.Equal(3, final.CandidateObservation!.AcceptedWorkItemKeys.Length);
            Assert.Equal(saved.LineageCharges.ProviderRequests, final.LineageCharges.ProviderRequests);
            Assert.Equal(saved.LineageCharges.InputTokens, final.LineageCharges.InputTokens);
            Assert.Equal(saved.LineageCharges.UncachedInputTokens, final.LineageCharges.UncachedInputTokens);
            Assert.Equal(saved.LineageCharges.OutputTokens, final.LineageCharges.OutputTokens);
            Assert.Equal(saved.LineageCharges.CostMicrounits, final.LineageCharges.CostMicrounits);
            Assert.Equal(saved.DispatchObservations.ToArray(), final.DispatchObservations.ToArray());
            Assert.Equal(requests, server.RequestCount);
            Assert.True(final.ScribeProgress!.Session!.Ended);
            Assert.All(final.WorkItems, work => Assert.Null(work.PausedProviderAttempt));
            Assert.Equal(originals[0], await File.ReadAllBytesAsync(firstPath));
            Assert.Equal(originals[1], await File.ReadAllBytesAsync(otherPath));
        }
        finally { Directory.Delete(outside, recursive: true); }
    }

    internal sealed partial class ProposalLoopbackServer
    {
        internal static byte[] CreateSharedResponse(byte[] body, bool batch = true, bool includeUsage = true,
            int usageOutputTokens = 7, string? summaryText = null, bool skipAll = false, int skipMembers = 0, string? repairScenario = null)
        {
            using var wire = JsonDocument.Parse(body);
            var messages = wire.RootElement.GetProperty("messages");
            using var policy = JsonDocument.Parse(messages[3].GetProperty("content").GetString()!);
            using var targets = JsonDocument.Parse(messages[4].GetProperty("content").GetString()!);
            var facts = targets.RootElement;
            var done = facts.GetProperty("progress").EnumerateArray().Select(item => item.GetProperty("targetId").GetString())
                .Concat(facts.GetProperty("restoredClosedTargetIds").EnumerateArray().Select(item => item.GetString())).ToHashSet(StringComparer.Ordinal);
            var pending = facts.GetProperty("executableTargetIds").EnumerateArray().Select(id => id.GetString()!)
                .Where(id => !done.Contains(id)).Select(id => facts.GetProperty("manifest").EnumerateArray()
                    .Single(member => member.GetProperty("targetId").GetString() == id)).ToArray();
            if (pending.Length == 0)
                return TerminalResponse(Encoding.UTF8.GetString(ScribeProtocolFixture.Finish(policy.RootElement)), true, 0, true, messages.GetArrayLength());
            pending = pending.Take(batch ? DocumentationScribeValidation.MaximumSubmissionBatchSize : 1).ToArray();
            var skipped = facts.GetProperty("executableTargetIds").EnumerateArray().Select(id => id.GetString()!)
                .TakeLast(skipMembers).ToHashSet(StringComparer.Ordinal);
            var references = facts.GetProperty("evidenceReferences").EnumerateArray().Select(item => item.Clone()).ToList();
            foreach (var message in messages.EnumerateArray().Where(message => message.GetProperty("role").GetString() == "tool"))
            {
                using var reply = JsonDocument.Parse(message.GetProperty("content").GetString()!);
                if (reply.RootElement.TryGetProperty("evidenceReferences", out var rows))
                    references.AddRange(rows.EnumerateArray().Select(item => item.Clone()));
            }
            JsonElement? Evidence(JsonElement member) => references.Where(reference =>
                    reference.GetProperty("subject").TryGetProperty("symbolRef", out var symbol)
                    && JsonNode.DeepEquals(JsonNode.Parse(symbol.GetRawText()), JsonNode.Parse(member.GetProperty("symbolRef").GetRawText())))
                .Cast<JsonElement?>().FirstOrDefault();
            var missing = pending.Where(member => !skipAll && !skipped.Contains(member.GetProperty("targetId").GetString()!)
                && member.GetProperty("applicableComponents").GetArrayLength() == 0
                && Evidence(member) is null).ToArray();
            if (missing.Length > 0)
            {
                var tool = wire.RootElement.GetProperty("tools").EnumerateArray().Select(item => item.GetProperty("function"))
                    .Single(item => item.GetProperty("parameters").TryGetProperty("properties", out var properties)
                        && properties.TryGetProperty("startLine", out _));
                var response = JsonSerializer.SerializeToNode(new
                {
                    choices = new[] { new { index = 0, message = new
                    {
                        role = "assistant", content = (string?)null, reasoning_content = "synthetic-shared-repository-read",
                        tool_calls = missing.Select((member, index) => new
                        {
                            id = "call.read." + messages.GetArrayLength() + "." + index, type = "function", function = new
                            {
                                name = tool.GetProperty("name").GetString(),
                                arguments = JsonSerializer.Serialize(new { scopeId = member.GetProperty("sourceScopeId").GetString() }),
                            },
                        }),
                    }, finish_reason = "tool_calls" } },
                    usage = new { prompt_tokens = 31, prompt_cache_hit_tokens = 29, prompt_cache_miss_tokens = 2, completion_tokens = 7, total_tokens = 38 },
                })!;
                if (!includeUsage) response.AsObject().Remove("usage");
                return JsonSerializer.SerializeToUtf8Bytes(response);
            }
            var items = new JsonArray();
            // Exercise durable closed skips before the unfinished document member.
            foreach (var member in pending.OrderBy(member => skipped.Contains(member.GetProperty("targetId").GetString()!) ? 0 : 1))
            {
                if (skipAll || skipped.Contains(member.GetProperty("targetId").GetString()!)
                    || member.GetProperty("applicableComponents").GetArrayLength() > 0)
                {
                    items.Add(new JsonObject
                    {
                        ["targetId"] = member.GetProperty("targetId").GetString(),
                        ["result"] = new JsonObject
                        {
                            ["kind"] = "skip",
                            ["reason"] = "scribe.skip.insufficient-evidence",
                            ["evidenceReferenceIds"] = new JsonArray()
                        }
                    });
                    continue;
                }
                var reference = Evidence(member)!.Value;
                var unit = ContentUnit("content.summary", null, null, reference.GetProperty("evidenceReferenceId").GetString()!, summaryText);
                unit["claimCategoryId"] = reference.GetProperty("claimCategoryIds")[0].GetString();
                items.Add(new JsonObject
                {
                    ["targetId"] = member.GetProperty("targetId").GetString(),
                    ["result"] = new JsonObject
                    {
                        ["kind"] = "proposal",
                        ["target"] = JsonNode.Parse(member.GetProperty("proposalTarget").GetRawText()),
                        ["contentUnits"] = new JsonArray(unit)
                    },
                });
            }
            if (repairScenario is not null && facts.GetProperty("nextSequence").GetInt32() == 1)
            {
                foreach (var item in items.Skip(repairScenario == "shared-mixed-repair" ? 1 : 0))
                    item!["result"]!["contentUnits"]![0]!["lines"] = new JsonArray("<summary>Raw documentation is not a logical line.</summary>");
            }
            var malformedRepair = repairScenario is "shared-malformed-repair" or "shared-malformed-repair-with-usage"
                && facts.GetProperty("nextSequence").GetInt32() == 2;
            return TerminalResponse(malformedRepair ? "{" : new JsonObject
            {
                ["kind"] = "submit",
                ["scribeRequestSha256"] = policy.RootElement.GetProperty("artifactSha256").GetString(),
                ["sequence"] = facts.GetProperty("nextSequence").GetInt32(),
                ["items"] = items
            }.ToJsonString(), includeUsage && (!malformedRepair || repairScenario == "shared-malformed-repair-with-usage"),
                usageOutputTokens, turn: messages.GetArrayLength());
        }
    }
}
