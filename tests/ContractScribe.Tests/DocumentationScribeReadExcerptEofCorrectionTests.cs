using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Agent.Providers;
using ContractScribe.Agent.Runtime;
using ContractScribe.Cli;
using ContractScribe.Core;
using ContractScribe.Roslyn;

namespace ContractScribe.Tests;

public sealed partial class DocumentationScribeRepositoryToolTests
{
    [Theory]
    [InlineData(1, 999999)]
    [InlineData(999999, 999999)]
    public async Task Read_excerpt_EOF_refuses_once_without_content_then_allows_an_exact_whole_file(int start, int end)
    {
        using var fixture = RepositoryToolFixture.Create();
        var bundle = fixture.Bundle();
        var request = new DocumentationScribeRepositoryReadExcerptRequest("context.instructions", "docs/guide.md", start, end);
        var refused = await bundle.ReadExcerpt.InvokeAsync(request, default);
        Assert.Same(DocumentationScribeToolOutcome.Unavailable, refused.Outcome);
        Assert.Equal("repository.range-past-eof", refused.FailureCode);
        Assert.Null(refused.Excerpt); Assert.Null(refused.Route); Assert.Empty(refused.DynamicEvidence);
        var json = JsonSerializer.Serialize(refused);
        foreach (var privateValue in new[] { "guide.md", "999999", "bounded evidence.", "StartLine", fixture.Root })
            Assert.DoesNotContain(privateValue, json, StringComparison.Ordinal);

        var read = await bundle.ReadExcerpt.InvokeAsync(new("context.instructions", "docs/guide.md"), default);
        Assert.Same(DocumentationScribeToolOutcome.Complete, read.Outcome);
        Assert.Equal("Guide\r\nbounded evidence.\r\n", read.Excerpt!.Content);
        var repeated = await bundle.ReadExcerpt.InvokeAsync(request, default);
        Assert.Same(DocumentationScribeToolOutcome.Failure, repeated.Outcome);
        Assert.Equal(DocumentationScribeRepositoryToolFailureCodes.InvalidRequest, repeated.FailureCode);
        Assert.Equal(DocumentationScribeToolFailureReason.InvalidRequestEndOfFile,
            ((IDocumentationScribeToolResult)repeated).FailureReasonCategory);
        Assert.Null(repeated.Excerpt); Assert.Empty(repeated.DynamicEvidence);
    }

    [Theory]
    [InlineData("content")]
    [InlineData("identity")]
    [InlineData("parent")]
    public async Task Read_excerpt_EOF_revalidates_the_original_file_before_releasing_a_refusal(string change)
    {
        using var fixture = RepositoryToolFixture.Create();
        var entered = false;
        var bundle = fixture.Bundle(checkpoint: point =>
        {
            if (point != DocumentationScribeRepositoryToolCheckpoint.BeforeEvidencePublication || entered) return;
            entered = true;
            var path = Path.Join(fixture.Root, "docs", "guide.md");
            if (change == "content") File.AppendAllText(path, "changed");
            else if (change == "identity")
            {
                File.Move(path, path + ".previous");
                File.WriteAllText(path, "Guide\r\nbounded evidence.\r\n");
            }
            else
            {
                Directory.Move(Path.Join(fixture.Root, "docs"), Path.Join(fixture.Root, "previous"));
                Directory.CreateDirectory(Path.Join(fixture.Root, "docs"));
                File.WriteAllText(path, "Guide\r\nbounded evidence.\r\n");
            }
        });
        var result = await bundle.ReadExcerpt.InvokeAsync(new("context.instructions", "docs/guide.md", 1, 999999), default);
        Assert.True(entered);
        Assert.Same(DocumentationScribeToolOutcome.Failure, result.Outcome);
        Assert.Equal(DocumentationScribeRepositoryToolFailureCodes.Stale, result.FailureCode);
        Assert.Null(result.Excerpt); Assert.Null(result.Route); Assert.Empty(result.DynamicEvidence);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("timeout")]
    [InlineData("returned-bytes")]
    public async Task Read_excerpt_EOF_does_not_replace_cancellation_deadline_or_publication_budget(string stop)
    {
        using var fixture = RepositoryToolFixture.Create();
        using var cancellation = new CancellationTokenSource();
        var elapsed = TimeSpan.Zero;
        var bundle = fixture.Bundle(
            limits: DocumentationScribeRepositoryToolLimits.Create(maximumReturnedUtf8BytesPerRun: stop == "returned-bytes" ? 1 : 262144),
            elapsed: () => elapsed,
            checkpoint: point =>
            {
                if (point != DocumentationScribeRepositoryToolCheckpoint.BeforeEvidencePublication) return;
                if (stop == "cancel") cancellation.Cancel();
                if (stop == "timeout") elapsed = TimeSpan.FromSeconds(60);
            });
        var result = await bundle.ReadExcerpt.InvokeAsync(new("context.instructions", "docs/guide.md", 1, 999999), cancellation.Token);
        Assert.Same(stop == "cancel" ? DocumentationScribeToolOutcome.Cancelled
            : stop == "timeout" ? DocumentationScribeToolOutcome.TimedOut : DocumentationScribeToolOutcome.BudgetExhausted, result.Outcome);
        Assert.Null(result.Excerpt); Assert.Null(result.Route); Assert.Empty(result.DynamicEvidence);
    }

    [Theory]
    [InlineData("corrected", 4)]
    [InlineData("repeated", 2)]
    [InlineData("no-provider-budget", 1)]
    [InlineData("stale-release", 1)]
    public async Task Solus_runtime_EOF_feedback_allows_only_a_new_budgeted_request(string scenario, int expectedRequests)
    {
        using var fixture = RepositoryToolFixture.Create(requestMutation: root =>
        {
            if (scenario == "no-provider-budget") root["limits"]!["maximumProviderRequests"] = 1;
        });
        var changed = false;
        var bundle = fixture.Bundle(checkpoint: point =>
        {
            if (scenario != "stale-release" || changed || point != DocumentationScribeRepositoryToolCheckpoint.BeforeEvidencePublication) return;
            changed = true;
            File.AppendAllText(Path.Join(fixture.Root, "docs", "guide.md"), "changed");
        });
        var registry = new DocumentationScribeToolRegistryBuilder(fixture.Request.ToolPolicyId).Add(
            DocumentationScribeRepositoryToolBundle.ReadExcerptDescriptor, bundle.ReadExcerpt,
            new DocumentationScribeComposition.ReadCodec(fixture.Request.EvidenceReferences[0]),
            DocumentationScribeRepositoryToolSchemas.ReadExcerptDescription, BoundReadSchema(fixture.Request), 16).Build();
        const string wholeFile = "{\"scopeId\":\"context.instructions\",\"repositoryPath\":\"docs/guide.md\"}";
        const string pastEof = "{\"scopeId\":\"context.instructions\",\"repositoryPath\":\"docs/guide.md\",\"startLine\":1,\"endLine\":999999}";
        Exception? handlerFailure = null;
        var handler = new ScribeToolHttpHandler(async (message, token, number) =>
        {
            try
            {
                Assert.InRange(number, 1, expectedRequests);
                using var wire = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
                if (number == 2)
                {
                    var tool = Assert.Single(wire.RootElement.GetProperty("messages").EnumerateArray(), x => x.GetProperty("role").GetString() == "tool");
                    var content = tool.GetProperty("content").GetString()!;
                    Assert.Contains("repository.range-past-eof", content, StringComparison.Ordinal);
                    Assert.DoesNotContain("guide.md", content, StringComparison.Ordinal);
                    Assert.DoesNotContain("999999", content, StringComparison.Ordinal);
                    Assert.DoesNotContain("bounded evidence.", content, StringComparison.Ordinal);
                }
                if (number == 3)
                {
                    var tool = wire.RootElement.GetProperty("messages").EnumerateArray().Last(x => x.GetProperty("role").GetString() == "tool");
                    using var content = JsonDocument.Parse(tool.GetProperty("content").GetString()!);
                    Assert.Equal("Guide\nbounded evidence.\n", content.RootElement.GetProperty("result").GetProperty("excerpt").GetProperty("content").GetString());
                }
                var arguments = number == 1 || scenario == "repeated" ? pastEof : number == 2 ? wholeFile
                    : number == 3 ? Encoding.UTF8.GetString(ScribeResultFixture.Submit(fixture.Request, Encoding.UTF8.GetBytes(
                        "{\"kind\":\"skip\",\"reason\":\"scribe.skip.insufficient-evidence\",\"evidenceReferenceIds\":[]}")))
                    : Encoding.UTF8.GetString(ScribeResultFixture.Finish(fixture.Request));
                var response = new JsonObject
                {
                    ["choices"] = new JsonArray(new JsonObject
                    {
                        ["index"] = 0,
                        ["finish_reason"] = "tool_calls",
                        ["message"] = new JsonObject
                        {
                            ["role"] = "assistant",
                            ["reasoning_content"] = "synthetic continuation",
                            ["tool_calls"] = new JsonArray(new JsonObject
                            {
                                ["id"] = "call." + number,
                                ["type"] = "function",
                                ["function"] = new JsonObject { ["name"] = number <= 2 ? "cs_tool_000" : "cs_terminal", ["arguments"] = arguments },
                            }),
                        },
                    }),
                    ["usage"] = new JsonObject { ["prompt_tokens"] = 0, ["completion_tokens"] = 0, ["prompt_cache_hit_tokens"] = 0, ["prompt_cache_miss_tokens"] = 0 },
                };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response.ToJsonString(), Encoding.UTF8, "application/json") };
            }
            catch (Exception exception)
            {
                handlerFailure = exception;
                throw;
            }
        });
        using var transport = new OpenAiCompatibleHttpModelExchange(new(new Uri("https://example.test/chat/completions"), "synthetic-model",
            new(OpenAiCompatibleThinkingMode.Enabled, OpenAiCompatibleReasoningEffort.High, OpenAiCompatibleToolChoice.Auto,
                OpenAiCompatibleContinuationPolicy.RequiredForToolCalls, OpenAiCompatibleOutputTokenField.MaxTokens), networkEnabled: true), handler);
        var session = ToolSession(fixture.Request, registry, transport);
        var result = await session.RunAsync();
        if (handlerFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(handlerFailure).Throw();
        Assert.Equal(expectedRequests, handler.Count);
        Assert.Equal(expectedRequests, session.Requests.Count);
        Assert.All(session.Requests, request => Assert.Equal(1, request.AttemptNumber));
        Assert.Equal(0, session.State.Scope.Allowance.Observe().RetryDispatches);
        Assert.Equal(0, session.State.Scope.Allowance.Observe().UnsettledDispatches);
        var released = session.State.CompletedToolExchanges.Where(exchange => exchange.OperationId == DocumentationScribeRepositoryToolOperationIds.ReadExcerpt).ToArray();
        Assert.Equal(scenario == "corrected" ? 2 : scenario == "stale-release" ? 0 : 1, released.Length);
        if (released.Length > 0) Assert.Equal(DocumentationScribeToolOutcome.Unavailable.Id, released[0].OutcomeId);
        if (scenario == "corrected") Assert.Equal(DocumentationScribeTerminalKind.Skip, Assert.Single(result.Submissions).Result.Kind);
        else Assert.Equal(scenario == "no-provider-budget" ? DocumentationScribeFailureCode.Budget : DocumentationScribeFailureCode.ToolProtocol,
            Assert.IsType<DocumentationScribeFailureTerminal>(result.Stop).Code);
    }
}
