using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Core;
using Json.Schema;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed partial class GitHubProposalCliProcessTests
{
    [ContractScribe.TestSupport.NativeFileSystemTheory]
    [InlineData("tool-diagnostic-range", "campaign.tool-failure.read-excerpt.invalid-request-eof", 2, 7, 20)]
    [InlineData("tool-diagnostic-cursor", "campaign.tool-failure.search-text.invalid-cursor", 2, 7, 20)]
    [InlineData("tool-diagnostic-semantic", "campaign.tool-failure.get-target-evidence.invalid-request", 1, 1, 6)]
    [InlineData("tool-diagnostic-positive", null, 4, 11, 41)]
    public async Task Tool_failures_preserve_the_original_bounded_reason_through_CLI_and_Action(
        string scenario, string? expectedCode, int sends, int tools, int revision)
    {
        await using var fixture = await Fixture.CreateAsync(targetCount: 12, providerScenario: scenario);
        var configuration = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Configuration!))!;
        configuration["budgets"]!["invocation"]!["maximumTargets"] = 5;
        configuration["budgets"]!["invocation"]!["maximumProviderRequests"] = 8;
        await File.WriteAllTextAsync(fixture.Configuration!, configuration.ToJsonString());
        var result = await fixture.Run();
        var failed = expectedCode is not null;
        AssertResult(result, failed ? 5 : 0, failed ? "host-failure" : "published");
        var checkpoint = fixture.Checkpoint(); var state = checkpoint.State;
        var progress = state.ScribeProgress!;
        using var parsed = JsonDocument.Parse(result.Stdout);
        var root = parsed.RootElement; var usage = root.GetProperty("invocationUsage");
        Assert.Equal(sends, fixture.Provider.RequestCount);
        Assert.Equal(sends, usage.GetProperty("calls").GetProperty("physicalProviderDispatches").GetInt32());
        Assert.Equal(sends, usage.GetProperty("calls").GetProperty("logicalProviderCalls").GetInt32());
        Assert.Equal(0, usage.GetProperty("calls").GetProperty("retryDispatches").GetInt32());
        Assert.Equal(tools, usage.GetProperty("calls").GetProperty("toolCalls").GetInt32());
        // Keep the original Theory row/identity as the pre-composition workload
        // reference. A successful batch now combines its last member/progress
        // projection instead of persisting one duplicate final scalar save.
        var currentRevision = failed ? revision : revision - 1;
        Assert.Equal(currentRevision, checkpoint.CheckpointRevision);
        Assert.Equal(currentRevision, usage.GetProperty("checkpointRevision").GetInt64());
        Assert.Equal("complete", usage.GetProperty("completeness").GetString());
        Assert.Equal(JsonValueKind.Null, usage.GetProperty("checkpointFailure").ValueKind);
        Assert.Equal(12, state.WorkItems.Length); Assert.Equal(12, state.TargetProgress.Length);
        Assert.Equal(5, progress.Session!.AdmittedWorkItemKeys.Length);
        Assert.True(progress.Session.Ended); Assert.Null(state.ActiveReservation);
        Assert.Equal(7, state.TargetProgress.Count(item => item.Kind == CampaignTargetProgressKind.Deferred));
        Assert.Equal(failed ? 0 : 5, usage.GetProperty("work").GetProperty("acceptedTargets").GetInt32());
        Assert.Equal(failed ? 1 : 0, usage.GetProperty("work").GetProperty("failedTargets").GetInt32());
        Assert.Equal(failed ? 5 : 0, usage.GetProperty("work").GetProperty("remainingTargets").GetInt32());
        Assert.Equal(failed ? 0 : 1, state.LineageCharges.PatchValidationInvocations);
        Assert.Equal(failed ? 0 : 1, fixture.GitHub.PullRequests.Count);
        if (failed)
        {
            Assert.Null(state.CandidateObservation); Assert.Equal(0, fixture.GitHub.PublicationWrites);
            Assert.Equal(0, fixture.GitHub.PrAttempts);
            Assert.All(state.WorkItems, item => Assert.Null(item.TrustedProposal));
            Assert.All(progress.Members, item => { Assert.Null(item.ContentSha256); Assert.Null(item.Skip); });
            using var raw = JsonDocument.Parse(fixture.GitHub.CheckpointBytes());
            var closed = Assert.Single(raw.RootElement.GetProperty("workItems").EnumerateArray(),
                item => item.GetProperty("closedOutcome").ValueKind != JsonValueKind.Null);
            Assert.Equal("tool-protocol-failure", closed.GetProperty("closedOutcome").GetProperty("code").GetString());
        }
        Assert.True(fixture.GitHub.CoordinationDocument()["claim"]!["expiresUnixSeconds"]!.GetValue<long>() <= fixture.GitHub.ServerNow.ToUnixTimeSeconds());
        await fixture.AssertSourceUnchanged();
        await AssertActualActionConsumer(fixture.Outside, result.Stdout, result.ExitCode,
            progressOutcome: failed ? "host-failure" : "published", actualStderr: result.Stderr);
        Assert.Equal(failed ? 1 : 0, result.Stderr.Count(character => character == '\n'));
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(result.Stdout), 1, 16 * 1024);
        var codes = root.GetProperty("diagnosticCodes").EnumerateArray().Select(code => code.GetString()!).ToArray();
        if (failed)
        {
            Assert.Contains("campaign.provider-terminal", codes);
            Assert.True(codes.Contains(expectedCode!, StringComparer.Ordinal), JsonSerializer.Serialize(new
            {
                scenario,
                expectedCode,
                actualCodes = codes,
                actualSDK = sends,
                actualEnteredTools = tools,
                actualACKandDurableCP = checkpoint.CheckpointRevision,
                actualWork = usage.GetProperty("work"),
                actualM2 = state.LineageCharges.PatchValidationInvocations,
                actualPRs = fixture.GitHub.PullRequests.Count,
                actualActionConsumerPassed = true,
                ended = progress.Session.Ended,
                activeReservation = state.ActiveReservation,
            }));
            Assert.Equal(2, codes.Length);
        }
        else Assert.Empty(codes);
        Assert.All(codes, code => Assert.InRange(code.Length, 1, 96));
    }
}

public sealed partial class CampaignCliProcessTests
{
    internal sealed partial class ProposalLoopbackServer
    {
        private static byte[] CreateToolDiagnosticResponse(byte[] body, string scenario, int count)
        {
            JsonNode response;
            if (scenario == "tool-diagnostic-semantic")
            {
                Assert.Equal(1, count);
                response = JsonNode.Parse(CreateSemanticResponse(body, 21, sharedBatch: true))!;
            }
            else if (count == 1)
            {
                response = JsonNode.Parse(CreateSemanticResponse(body, 20, sharedBatch: true, targetCount: 5))!;
            }
            else
            {
                var wire = JsonNode.Parse(body)!; var messages = wire["messages"]!.AsArray();
                if (count == 2)
                {
                    var replies = messages.Where(message => message!["role"]!.GetValue<string>() == "tool").ToArray();
                    Assert.Equal(5, replies.Length);
                    var assistant = messages.Single(message => message!["role"]!.GetValue<string>() == "assistant")!;
                    var calls = assistant["tool_calls"]!.AsArray(); Assert.Equal(5, calls.Count);
                    for (var index = 0; index < 5; index++)
                    {
                        Assert.Equal("call.semantic." + index, calls[index]!["id"]!.GetValue<string>());
                        Assert.Equal(calls[index]!["id"]!.GetValue<string>(), replies[index]!["tool_call_id"]!.GetValue<string>());
                        var returned = JsonNode.Parse(replies[index]!["content"]!.GetValue<string>())!;
                        Assert.NotNull(returned["result"]!["page"]);
                        Assert.Empty(returned["evidenceReferences"]!.AsArray());
                    }
                }
                if (scenario == "tool-diagnostic-positive")
                {
                    Assert.InRange(count, 2, 4);
                    response = JsonNode.Parse(CreateSharedResponse(body))!;
                }
                else
                {
                    Assert.Equal(2, count);
                    var facts = JsonNode.Parse(messages[4]!["content"]!.GetValue<string>())!;
                    var targets = facts["executableTargetIds"]!.AsArray(); Assert.Equal(5, targets.Count);
                    JsonNode Tool(string key) => wire["tools"]!.AsArray().Single(tool =>
                        tool!["function"]!["parameters"]!["properties"] is JsonObject properties && properties.ContainsKey(key))!["function"]!;
                    var read = Tool("startLine"); var second = scenario == "tool-diagnostic-range" ? read : Tool("literal");
                    var firstArgs = new JsonObject { ["scopeId"] = targets[0]!.GetValue<string>() };
                    var secondArgs = new JsonObject { ["scopeId"] = targets[1]!.GetValue<string>() };
                    if (scenario == "tool-diagnostic-range") { secondArgs["startLine"] = 999999; secondArgs["endLine"] = 999999; }
                    else { secondArgs["literal"] = "public"; secondArgs["cursor"] = "not-a-returned-cursor"; }
                    object Call(JsonNode tool, string id, JsonObject args)
                    {
                        using var value = JsonDocument.Parse(args.ToJsonString());
                        Assert.True(JsonSchema.FromText(tool["parameters"]!.ToJsonString()).Evaluate(value.RootElement).IsValid);
                        return new { id, type = "function", function = new { name = tool["name"]!.GetValue<string>(), arguments = args.ToJsonString() } };
                    }
                    response = JsonSerializer.SerializeToNode(new
                    {
                        choices = new[] { new { index = 0, message = new
                        {
                            role = "assistant", content = (string?)null, reasoning_content = "synthetic-tool-diagnostic",
                            tool_calls = new[] { Call(read, "call.diagnostic.first", firstArgs), Call(second, "call.diagnostic.second", secondArgs) },
                        }, finish_reason = "tool_calls" } },
                        usage = new { prompt_tokens = 31, prompt_cache_hit_tokens = 29, prompt_cache_miss_tokens = 2, completion_tokens = 7, total_tokens = 38 },
                    })!;
                }
            }
            response["usage"]!["completion_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 0 };
            return JsonSerializer.SerializeToUtf8Bytes(response);
        }
    }
}
