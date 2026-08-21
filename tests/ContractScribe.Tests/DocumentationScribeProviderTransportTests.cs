using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Agent.Providers;
using ContractScribe.Agent.Runtime;
using ContractScribe.Core;
using ContractScribe.Evaluation;

namespace ContractScribe.Tests;

public sealed class DocumentationScribeProviderTransportTests
{
    [Fact]
    [Trait("MinimumRuntime", "10.0.2")]
    public void Minimum_runtime_probe_reports_the_requested_runtime()
    {
        var expected = Environment.GetEnvironmentVariable("CONTRACTSCRIBE_EXPECT_RUNTIME");
        if (expected is not null)
        {
            Assert.Equal(Version.Parse(expected), Environment.Version);
        }
    }

    [Fact]
    public void Selection_probe_preserves_the_complete_product_path()
    {
        var first = Request([]);
        var prepared = OpenAiCompatibleChatCompletionsCodec.Prepare(first, "synthetic-model-v1");
        var otherModel = OpenAiCompatibleChatCompletionsCodec.Prepare(first, "synthetic-model-v2");

        Assert.NotEqual(prepared.BodyUtf8, otherModel.BodyUtf8);
        Assert.Equal(prepared.ProductProjectionUtf8, otherModel.ProductProjectionUtf8);
        using (var wire = JsonDocument.Parse(prepared.BodyUtf8))
        {
            var root = wire.RootElement;
            Assert.Equal(
                ["model", "messages", "tools", "thinking", "tool_choice", "max_tokens", "stream"],
                root.EnumerateObject().Select(property => property.Name));
            Assert.Equal(
                ["system", "user", "user", "system", "user"],
                root.GetProperty("messages").EnumerateArray().Select(message => message.GetProperty("role").GetString()));
            Assert.Equal(
                ["cs_tool_000", "cs_tool_001", OpenAiCompatibleChatCompletionsCodec.TerminalAlias],
                root.GetProperty("tools").EnumerateArray()
                    .Select(tool => tool.GetProperty("function").GetProperty("name").GetString()));
        }

        var toolResponse = OpenAiCompatibleChatCompletionsCodec.ParseResponse(
            ReadProviderFixture("usage-cache-response.json"),
            prepared);

        var call = Assert.Single(toolResponse.ToolCalls);
        Assert.Equal("tool.alpha", call.OperationId);
        Assert.Equal(100, toolResponse.Usage!.InputTokens);
        Assert.Equal(80, toolResponse.Usage.CachedInputTokens);
        Assert.Equal(20, toolResponse.Usage.UncachedInputTokens);
        Assert.Equal(3, toolResponse.Usage.ReasoningTokens);
        Assert.Equal(DocumentationScribeCacheObservation.Mixed, toolResponse.Cache);

        var completed = new DocumentationScribeCompletedToolExchange(
            call.ResponseIndex,
            call.CallId,
            call.OperationId,
            call.ArgumentsUtf8Json.ToArray().ToImmutableArray(),
            DocumentationScribeToolOutcome.Complete.Id,
            Encoding.UTF8.GetBytes("{\"referenceId\":\"one\"}").ToImmutableArray(),
            []);
        var second = OpenAiCompatibleChatCompletionsCodec.Prepare(Request([completed]), "synthetic-model-v1");
        using (var wire = JsonDocument.Parse(second.BodyUtf8))
        {
            var messages = wire.RootElement.GetProperty("messages");
            Assert.Equal(7, messages.GetArrayLength());
            Assert.Equal("assistant", messages[5].GetProperty("role").GetString());
            Assert.Equal("tool", messages[6].GetProperty("role").GetString());
            using var wrapper = JsonDocument.Parse(messages[6].GetProperty("content").GetString()!);
            Assert.Equal(DocumentationScribeToolOutcome.Complete.Id, wrapper.RootElement.GetProperty("outcome").GetString());
            Assert.Equal("one", wrapper.RootElement.GetProperty("result").GetProperty("referenceId").GetString());
        }

        var terminalResponse = OpenAiCompatibleChatCompletionsCodec.ParseResponse(
            ReadProviderFixture("terminal-response.json"),
            second);
        var terminal = Assert.Single(terminalResponse.TerminalSubmissions);
        Assert.Equal("skip", JsonDocument.Parse(terminal.TerminalUtf8Json).RootElement.GetProperty("kind").GetString());

        Assert.Equal(
            "68781078e22df92791bdc62253fe1437f684ba8af78dd8373d7d70d7a98eeda2",
            OpenAiCompatibleChatCompletionsCodec.Digest(prepared.BodyUtf8));
        Assert.Equal(
            "d97a87532f0c1776bd07324fbeeadfd146d4968b9c0e81b1c0dfdf4e1edcf0d8",
            OpenAiCompatibleChatCompletionsCodec.Digest(prepared.ProductProjectionUtf8));
        Assert.Equal(
            "08882feee6b85b27af5dd0610e42f76252e2003f7ef6a107087e0330ad873199",
            OpenAiCompatibleChatCompletionsCodec.Digest(second.BodyUtf8));
        Assert.Equal(
            "057dd9c7ad1e0819cc2708520eda78ed8fb9f5fe3355330049b65de7aa067e51",
            OpenAiCompatibleChatCompletionsCodec.Digest(second.ProductProjectionUtf8));
    }

    [Fact]
    public void Selected_provider_profiles_emit_only_their_documented_request_fields()
    {
        var deepSeek = OpenAiCompatibleChatCompletionsCodec.Prepare(
            Request([]),
            "deepseek-v4-flash",
            new OpenAiCompatibleChatCompletionsRequestProfile(
                OpenAiCompatibleThinkingMode.Enabled,
                OpenAiCompatibleReasoningEffort.High,
                OpenAiCompatibleToolChoice.Omitted,
                OpenAiCompatibleContinuationPolicy.RequiredForToolCalls,
                OpenAiCompatibleOutputTokenField.MaxTokens));
        using (var document = JsonDocument.Parse(deepSeek.BodyUtf8))
        {
            var root = document.RootElement;
            Assert.Equal(
                ["model", "messages", "tools", "thinking", "reasoning_effort", "max_tokens", "stream"],
                root.EnumerateObject().Select(property => property.Name));
            Assert.Equal("enabled", root.GetProperty("thinking").GetProperty("type").GetString());
            Assert.Equal("high", root.GetProperty("reasoning_effort").GetString());
            Assert.False(root.TryGetProperty("tool_choice", out _));
            Assert.False(root.TryGetProperty("max_completion_tokens", out _));
            Assert.False(root.TryGetProperty("parallel_tool_calls", out _));
            Assert.False(root.TryGetProperty("n", out _));
        }

        var miMo = OpenAiCompatibleChatCompletionsCodec.Prepare(
            Request([]),
            "mimo-v2.5",
            new OpenAiCompatibleChatCompletionsRequestProfile(
                OpenAiCompatibleThinkingMode.Enabled,
                reasoningEffort: null,
                OpenAiCompatibleToolChoice.Auto,
                OpenAiCompatibleContinuationPolicy.RequiredForToolCalls,
                OpenAiCompatibleOutputTokenField.MaxCompletionTokens));
        using var miMoDocument = JsonDocument.Parse(miMo.BodyUtf8);
        var miMoRoot = miMoDocument.RootElement;
        Assert.Equal(
            ["model", "messages", "tools", "thinking", "tool_choice", "max_completion_tokens", "stream"],
            miMoRoot.EnumerateObject().Select(property => property.Name));
        Assert.Equal("auto", miMoRoot.GetProperty("tool_choice").GetString());
        Assert.False(miMoRoot.TryGetProperty("reasoning_effort", out _));
        Assert.False(miMoRoot.TryGetProperty("max_tokens", out _));
    }

    [Fact]
    public void Reasoning_continuation_is_replayed_exactly_once_with_parallel_tool_calls()
    {
        const string content = "assistant-content-marker";
        const string reasoning = "reasoning-\u4e2d-\ud83d\ude80-marker";
        var profile = RequiredContinuationProfile();
        var first = OpenAiCompatibleChatCompletionsCodec.Prepare(Request([]), "model", profile);
        var response = OpenAiCompatibleChatCompletionsCodec.ParseResponse(
            Encoding.UTF8.GetBytes("""
                {"choices":[{"index":0,"message":{"role":"assistant","content":"assistant-content-marker","reasoning_content":"reasoning-\u4e2d-\ud83d\ude80-marker","tool_calls":[{"id":"call.alpha","type":"function","function":{"name":"cs_tool_000","arguments":"{}"}},{"id":"call.zeta","type":"function","function":{"name":"cs_tool_001","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}
                """),
            first);

        Assert.Equal(
            DocumentationScribeContinuationObservation.Observed,
            response.ContinuationObservation);
        Assert.Equal(content, response.AssistantContinuation!.Content);
        Assert.Equal(reasoning, response.AssistantContinuation.ReasoningContent);

        var completed = response.ToolCalls.Select(call => new DocumentationScribeCompletedToolExchange(
            call.ResponseIndex,
            call.CallId,
            call.OperationId,
            call.ArgumentsUtf8Json.ToArray().ToImmutableArray(),
            DocumentationScribeToolOutcome.Complete.Id,
            Encoding.UTF8.GetBytes("{}").ToImmutableArray(),
            [],
            call.ResponseIndex == 0 ? response.AssistantContinuation : null)).ToImmutableArray();
        var second = OpenAiCompatibleChatCompletionsCodec.Prepare(Request(completed), "model", profile);
        using var document = JsonDocument.Parse(second.BodyUtf8);
        var assistant = document.RootElement.GetProperty("messages")[5];
        Assert.Equal(content, assistant.GetProperty("content").GetString());
        Assert.Equal(reasoning, assistant.GetProperty("reasoning_content").GetString());
        Assert.Equal(2, assistant.GetProperty("tool_calls").GetArrayLength());
        Assert.Single(
            document.RootElement.GetProperty("messages").EnumerateArray(),
            message => message.TryGetProperty("reasoning_content", out _));
        Assert.DoesNotContain(reasoning, Encoding.UTF8.GetString(second.ProductProjectionUtf8), StringComparison.Ordinal);

        var missingAfterReplay = Assert.Throws<OpenAiCompatibleProtocolException>(() =>
            OpenAiCompatibleChatCompletionsCodec.ParseResponse(
                Encoding.UTF8.GetBytes("""
                    {"choices":[{"index":0,"message":{"role":"assistant","content":"","tool_calls":[{"id":"call.missing-after-replay","type":"function","function":{"name":"cs_tool_000","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}
                    """),
                second));
        Assert.Equal(
            DocumentationScribeContinuationObservation.HistoryReplayed
                | DocumentationScribeContinuationObservation.MissingRequired,
            missingAfterReplay.ContinuationObservation);

        var nextResponse = OpenAiCompatibleChatCompletionsCodec.ParseResponse(
            Encoding.UTF8.GetBytes("""
                {"choices":[{"index":0,"message":{"role":"assistant","content":"","reasoning_content":"next","tool_calls":[{"id":"call.next","type":"function","function":{"name":"cs_tool_000","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}
                """),
            second);
        Assert.Equal(
            DocumentationScribeContinuationObservation.Observed
                | DocumentationScribeContinuationObservation.HistoryReplayed,
            nextResponse.ContinuationObservation);

        var nextCall = Assert.Single(nextResponse.ToolCalls);
        var nextCompleted = new DocumentationScribeCompletedToolExchange(
            nextCall.ResponseIndex,
            nextCall.CallId,
            nextCall.OperationId,
            nextCall.ArgumentsUtf8Json.ToArray().ToImmutableArray(),
            DocumentationScribeToolOutcome.Complete.Id,
            Encoding.UTF8.GetBytes("{}").ToImmutableArray(),
            [],
            nextResponse.AssistantContinuation);
        var third = OpenAiCompatibleChatCompletionsCodec.Prepare(
            Request([.. completed, nextCompleted]),
            "model",
            profile);
        using var thirdDocument = JsonDocument.Parse(third.BodyUtf8);
        var thirdMessages = thirdDocument.RootElement.GetProperty("messages");
        Assert.Equal(10, thirdMessages.GetArrayLength());
        Assert.Equal(reasoning, thirdMessages[5].GetProperty("reasoning_content").GetString());
        Assert.Equal("next", thirdMessages[8].GetProperty("reasoning_content").GetString());
        Assert.Equal(
            2,
            thirdMessages.EnumerateArray().Count(message =>
                message.TryGetProperty("reasoning_content", out _)));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("empty")]
    public void Assistant_content_wire_shape_is_replayed_exactly(string contentShape)
    {
        var contentProperty = contentShape switch
        {
            "missing" => string.Empty,
            "null" => "\"content\":null,",
            "empty" => "\"content\":\"\",",
            _ => throw new ArgumentOutOfRangeException(nameof(contentShape)),
        };
        var profile = RequiredContinuationProfile();
        var prepared = OpenAiCompatibleChatCompletionsCodec.Prepare(Request([]), "model", profile);
        var response = OpenAiCompatibleChatCompletionsCodec.ParseResponse(
            Encoding.UTF8.GetBytes("{\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\","
                + contentProperty
                + "\"reasoning_content\":\"reasoning-marker\",\"tool_calls\":[{\"id\":\"call.shape\",\"type\":\"function\",\"function\":{\"name\":\"cs_tool_000\",\"arguments\":\"{}\"}}]},\"finish_reason\":\"tool_calls\"}]}"),
            prepared);
        var call = Assert.Single(response.ToolCalls);
        var completed = new DocumentationScribeCompletedToolExchange(
            call.ResponseIndex,
            call.CallId,
            call.OperationId,
            call.ArgumentsUtf8Json.ToArray().ToImmutableArray(),
            DocumentationScribeToolOutcome.Complete.Id,
            Encoding.UTF8.GetBytes("{}").ToImmutableArray(),
            [],
            response.AssistantContinuation);

        var replay = OpenAiCompatibleChatCompletionsCodec.Prepare(Request([completed]), "model", profile);
        using var replayDocument = JsonDocument.Parse(replay.BodyUtf8);
        var assistant = replayDocument.RootElement.GetProperty("messages")[5];
        var contentPresent = assistant.TryGetProperty("content", out var content);

        Assert.Equal(contentShape != "missing", contentPresent);
        if (contentShape == "null")
        {
            Assert.Equal(JsonValueKind.Null, content.ValueKind);
        }
        else if (contentShape == "empty")
        {
            Assert.Equal(string.Empty, content.GetString());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Required_continuation_rejects_missing_or_null_but_accepts_empty(bool explicitNull)
    {
        var prepared = OpenAiCompatibleChatCompletionsCodec.Prepare(
            Request([]), "model", RequiredContinuationProfile());
        var reasoning = explicitNull ? ",\"reasoning_content\":null" : string.Empty;
        var exception = Assert.Throws<OpenAiCompatibleProtocolException>(() =>
            OpenAiCompatibleChatCompletionsCodec.ParseResponse(
                Encoding.UTF8.GetBytes("{\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":null"
                    + reasoning
                    + ",\"tool_calls\":[{\"id\":\"call.required\",\"type\":\"function\",\"function\":{\"name\":\"cs_tool_000\",\"arguments\":\"{}\"}}]},\"finish_reason\":\"tool_calls\"}]}"),
                prepared));

        Assert.Equal(DocumentationScribeModelFailureCode.MalformedResponse, exception.Code);
        Assert.Equal(
            DocumentationScribeContinuationObservation.MissingRequired,
            exception.ContinuationObservation);

        var accepted = OpenAiCompatibleChatCompletionsCodec.ParseResponse(
            Encoding.UTF8.GetBytes("""
                {"choices":[{"index":0,"message":{"role":"assistant","content":null,"reasoning_content":"","tool_calls":[{"id":"call.empty","type":"function","function":{"name":"cs_tool_000","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}
                """),
            prepared);
        Assert.Equal(string.Empty, accepted.AssistantContinuation!.ReasoningContent);
        Assert.Equal(DocumentationScribeContinuationObservation.Observed, accepted.ContinuationObservation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Optional_continuation_accepts_missing_or_null(bool explicitNull)
    {
        var prepared = OpenAiCompatibleChatCompletionsCodec.Prepare(Request([]), "model");
        var reasoning = explicitNull ? ",\"reasoning_content\":null" : string.Empty;
        var response = OpenAiCompatibleChatCompletionsCodec.ParseResponse(
            Encoding.UTF8.GetBytes("{\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":null"
                + reasoning
                + ",\"tool_calls\":[{\"id\":\"call.optional\",\"type\":\"function\",\"function\":{\"name\":\"cs_tool_000\",\"arguments\":\"{}\"}}]},\"finish_reason\":\"tool_calls\"}]}"),
            prepared);

        Assert.Null(response.AssistantContinuation!.ReasoningContent);
        Assert.Equal(DocumentationScribeContinuationObservation.None, response.ContinuationObservation);
    }

    [Fact]
    public void Continuation_fails_closed_for_wrong_type_duplicate_invalid_scalar_and_normalized_overflow()
    {
        var prepared = OpenAiCompatibleChatCompletionsCodec.Prepare(Request([]), "model");
        var malformed = new[]
        {
            "{\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"reasoning_content\":{},\"tool_calls\":[{\"id\":\"call.type\",\"type\":\"function\",\"function\":{\"name\":\"cs_tool_000\",\"arguments\":\"{}\"}}]},\"finish_reason\":\"tool_calls\"}]}",
            "{\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"reasoning_content\":\"one\",\"reasoning_content\":\"two\",\"tool_calls\":[{\"id\":\"call.duplicate\",\"type\":\"function\",\"function\":{\"name\":\"cs_tool_000\",\"arguments\":\"{}\"}}]},\"finish_reason\":\"tool_calls\"}]}",
            "{\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"reasoning_content\":\"\\uD800\",\"tool_calls\":[{\"id\":\"call.scalar\",\"type\":\"function\",\"function\":{\"name\":\"cs_tool_000\",\"arguments\":\"{}\"}}]},\"finish_reason\":\"tool_calls\"}]}",
        };

        foreach (var body in malformed)
        {
            Assert.Equal(
                DocumentationScribeModelFailureCode.MalformedResponse,
                Assert.Throws<OpenAiCompatibleProtocolException>(() =>
                    OpenAiCompatibleChatCompletionsCodec.ParseResponse(Encoding.UTF8.GetBytes(body), prepared)).Code);
        }

        var call = new DocumentationScribeModelToolCall(
            0, "call.overflow", "tool.alpha", Encoding.UTF8.GetBytes("{}"));
        var continuation = new DocumentationScribeAssistantContinuation(
            new string('x', DocumentationScribeBoundary.MaximumNormalizedResponseUtf8Bytes),
            reasoningContent: null);
        Assert.Throws<ArgumentException>(() => new DocumentationScribeModelResponse(
            [call],
            [],
            failure: null,
            usage: null,
            cache: null,
            cost: null,
            continuation,
            DocumentationScribeContinuationObservation.None));
    }

    [Fact]
    public void Cost_transformation_preserves_opaque_continuation_and_closed_observations()
    {
        var continuation = new DocumentationScribeAssistantContinuation("content", "reasoning-marker");
        var response = new DocumentationScribeModelResponse(
            [new DocumentationScribeModelToolCall(0, "call.cost", "tool.alpha", Encoding.UTF8.GetBytes("{}"))],
            [],
            failure: null,
            usage: new DocumentationScribeModelUsage(inputTokens: 1),
            cache: null,
            cost: null,
            continuation,
            DocumentationScribeContinuationObservation.Observed
                | DocumentationScribeContinuationObservation.HistoryReplayed);

        var transformed = response.WithCost(new DocumentationScribeModelCost("usd", 1));

        Assert.Same(continuation, transformed.AssistantContinuation);
        Assert.Equal(response.ContinuationObservation, transformed.ContinuationObservation);
        Assert.Equal(1, transformed.Cost!.AmountMicrounits);
        Assert.DoesNotContain("reasoning-marker", transformed.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("partial")]
    [InlineData("unpriced")]
    public async Task Cost_observer_preserves_opaque_continuation_for_every_cost_completeness(string scenario)
    {
        var continuation = new DocumentationScribeAssistantContinuation(
            string.Empty,
            "cost-observer-reasoning-marker");
        var usage = scenario == "partial"
            ? new DocumentationScribeModelUsage(inputTokens: 1)
            : new DocumentationScribeModelUsage(inputTokens: 1, outputTokens: 1);
        var response = new DocumentationScribeModelResponse(
            [new DocumentationScribeModelToolCall(0, "call.observer", "tool.alpha", Encoding.UTF8.GetBytes("{}"))],
            [],
            failure: null,
            usage,
            cache: null,
            cost: null,
            continuation,
            DocumentationScribeContinuationObservation.Observed);
        var policy = scenario == "unpriced"
            ? EvaluationCostPolicy.Unpriced
            : new EvaluationCostPolicy("usd", 1, 1, 1);
        var observer = new CostObservingExchange(new StaticExchange(response), policy);

        var transformed = await observer.SendAsync(Request([]), CancellationToken.None);

        Assert.Same(continuation, transformed.AssistantContinuation);
        Assert.Equal(
            scenario switch
            {
                "complete" => EvaluationCostCompleteness.Complete,
                "partial" => EvaluationCostCompleteness.Partial,
                _ => EvaluationCostCompleteness.NotReported,
            },
            Assert.Single(observer.Observations).Cost.Completeness);
        Assert.Equal(scenario == "complete", transformed.Cost is not null);
    }

    [Fact]
    public async Task Http_transport_and_cost_observer_preserve_exact_continuation_into_the_next_wire_request()
    {
        const string marker = "synthetic-reasoning-pass-back-marker";
        var responses = new Queue<HttpResponseMessage>(
        [
            JsonResponse("""
                {"choices":[{"index":0,"message":{"role":"assistant","content":"","reasoning_content":"synthetic-reasoning-pass-back-marker","tool_calls":[{"id":"call.chain","type":"function","function":{"name":"cs_tool_000","arguments":"{}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":1,"completion_tokens":1}}
                """),
            JsonResponse("""
                {"choices":[{"index":0,"message":{"role":"assistant","content":"","reasoning_content":"terminal-reasoning-is-not-retained","tool_calls":[{"id":"call.terminal","type":"function","function":{"name":"cs_terminal","arguments":"{\"kind\":\"skip\"}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":1,"completion_tokens":1}}
                """),
        ]);
        var handler = new CapturingHandler((_, _) => Task.FromResult(responses.Dequeue()));
        using var transport = new OpenAiCompatibleHttpModelExchange(
            new OpenAiCompatibleHttpTransportOptions(
                new Uri("https://example.test/v1/chat/completions"),
                "model",
                RequiredContinuationProfile(),
                networkEnabled: true,
                credential: "synthetic-credential"),
            handler,
            disposeHandler: false);
        var observer = new CostObservingExchange(
            transport,
            new EvaluationCostPolicy("usd", 1, 1, 1));

        var first = await observer.SendAsync(Request([]), CancellationToken.None);
        var call = Assert.Single(first.ToolCalls);
        var completed = new DocumentationScribeCompletedToolExchange(
            call.ResponseIndex,
            call.CallId,
            call.OperationId,
            call.ArgumentsUtf8Json.ToArray().ToImmutableArray(),
            DocumentationScribeToolOutcome.Complete.Id,
            Encoding.UTF8.GetBytes("{}").ToImmutableArray(),
            [],
            first.AssistantContinuation);
        var terminal = await observer.SendAsync(Request([completed]), CancellationToken.None);

        Assert.Single(terminal.TerminalSubmissions);
        Assert.Equal(2, handler.Snapshots.Count);
        Assert.DoesNotContain(marker, handler.Snapshots[0].Body, StringComparison.Ordinal);
        using var replay = JsonDocument.Parse(handler.Snapshots[1].Body);
        var assistant = replay.RootElement.GetProperty("messages")[5];
        Assert.Equal(marker, assistant.GetProperty("reasoning_content").GetString());
        Assert.Equal(
            DocumentationScribeContinuationObservation.Observed,
            observer.Observations[0].ContinuationObservation);
        Assert.Equal(
            DocumentationScribeContinuationObservation.Observed
                | DocumentationScribeContinuationObservation.HistoryReplayed,
            observer.Observations[1].ContinuationObservation);
        Assert.DoesNotContain(marker, string.Join(' ', observer.Observations), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Http_transport_required_continuation_fails_closed_without_another_request(bool explicitNull)
    {
        var reasoning = explicitNull ? ",\"reasoning_content\":null" : string.Empty;
        var handler = new CapturingHandler((_, _) => Task.FromResult(JsonResponse(
            "{\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"opaque-content\""
            + reasoning
            + ",\"tool_calls\":[{\"id\":\"call.required\",\"type\":\"function\",\"function\":{\"name\":\"cs_tool_000\",\"arguments\":\"{}\"}}]},\"finish_reason\":\"tool_calls\"}]}")));
        using var transport = new OpenAiCompatibleHttpModelExchange(
            new OpenAiCompatibleHttpTransportOptions(
                new Uri("https://example.test/v1/chat/completions"),
                "model",
                RequiredContinuationProfile(),
                networkEnabled: true,
                credential: "synthetic-credential"),
            handler,
            disposeHandler: false);

        var response = await transport.SendAsync(Request([]), CancellationToken.None);

        Assert.Equal(1, handler.CallCount);
        Assert.Equal(DocumentationScribeModelFailureCode.MalformedResponse, response.Failure!.Code);
        Assert.Equal(
            DocumentationScribeContinuationObservation.MissingRequired,
            response.ContinuationObservation);
        Assert.Null(response.AssistantContinuation);
        Assert.Empty(response.ToolCalls);
        Assert.Empty(response.TerminalSubmissions);
        Assert.DoesNotContain("opaque-content", response.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Null_refusal_is_absent_semantically_but_non_null_refusal_fails_closed()
    {
        var prepared = OpenAiCompatibleChatCompletionsCodec.Prepare(Request([]), "model");
        var accepted = OpenAiCompatibleChatCompletionsCodec.ParseResponse(Encoding.UTF8.GetBytes("""
            {"choices":[{"index":0,"message":{"role":"assistant","content":null,"refusal":null,"tool_calls":[{"id":"call.accepted","type":"function","function":{"name":"cs_tool_000","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}
            """), prepared);

        Assert.Single(accepted.ToolCalls);

        var rejected = Assert.Throws<OpenAiCompatibleProtocolException>(() =>
            OpenAiCompatibleChatCompletionsCodec.ParseResponse(Encoding.UTF8.GetBytes("""
                {"choices":[{"index":0,"message":{"role":"assistant","content":null,"refusal":"blocked","tool_calls":[{"id":"call.rejected","type":"function","function":{"name":"cs_tool_000","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}
                """), prepared));
        Assert.Equal(DocumentationScribeModelFailureCode.MalformedResponse, rejected.Code);
    }

    [Fact]
    public void Completed_exchange_preserves_typed_evidence_on_wire_and_in_product_projection()
    {
        var evidence = ScribeRequest(maximumElapsedMilliseconds: 5_000).EvidenceReferences[0];
        var completed = new DocumentationScribeCompletedToolExchange(
            0,
            "call.evidence",
            "tool.alpha",
            Encoding.UTF8.GetBytes("{}").ToImmutableArray(),
            DocumentationScribeToolOutcome.Complete.Id,
            Encoding.UTF8.GetBytes("{}").ToImmutableArray(),
            [evidence]);

        var prepared = OpenAiCompatibleChatCompletionsCodec.Prepare(Request([completed]), "model");
        using var wire = JsonDocument.Parse(prepared.BodyUtf8);
        using var wrapper = JsonDocument.Parse(
            wire.RootElement.GetProperty("messages")[6].GetProperty("content").GetString()!);
        AssertEvidence(Assert.Single(wrapper.RootElement.GetProperty("evidenceReferences").EnumerateArray()), evidence);

        using var projection = JsonDocument.Parse(prepared.ProductProjectionUtf8);
        var projected = Assert.Single(
            Assert.Single(projection.RootElement.GetProperty("completedToolExchanges").EnumerateArray())
                .GetProperty("evidenceReferences").EnumerateArray());
        AssertEvidence(projected, evidence);
    }

    [Theory]
    [InlineData("http://localhost:1234/v1")]
    [InlineData("http://127.1:1234/v1")]
    [InlineData("http://2130706433:1234/v1")]
    [InlineData("http://[::ffff:127.0.0.1]:1234/v1")]
    [InlineData("http://127.0.0.1/v1")]
    [InlineData("https://user@example.test/v1")]
    [InlineData("https://example.test/v1?credential=value")]
    [InlineData("https://example.test/v1#fragment")]
    public void Options_reject_endpoints_outside_the_exact_authority(string endpoint)
    {
        var exception = Assert.Throws<ArgumentException>(() => new OpenAiCompatibleHttpTransportOptions(
            new Uri(endpoint), "model", networkEnabled: true));

        Assert.DoesNotContain(endpoint, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Options_bound_model_credential_and_plaintext_secret_without_disclosure()
    {
        const string marker = "credential-marker-value";
        var validV4 = new OpenAiCompatibleHttpTransportOptions(
            new Uri("http://127.0.0.1:12345/v1"), "model", networkEnabled: true);
        var validV6 = new OpenAiCompatibleHttpTransportOptions(
            new Uri("http://[::1]:12345/v1"), "model", networkEnabled: true);
        var confidential = new OpenAiCompatibleHttpTransportOptions(
            new Uri("https://example.test/v1"), "model", networkEnabled: true, marker);

        Assert.Equal("127.0.0.1", validV4.Endpoint.Host);
        Assert.Equal("[::1]", validV6.Endpoint.Host);
        Assert.DoesNotContain(marker, confidential.ToString(), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => new OpenAiCompatibleHttpTransportOptions(
            validV4.Endpoint, "model", networkEnabled: true, marker));
        Assert.Throws<ArgumentException>(() => new OpenAiCompatibleHttpTransportOptions(
            confidential.Endpoint, new string('m', OpenAiCompatibleHttpTransportOptions.MaximumModelUtf8Bytes + 1),
            networkEnabled: true));
        Assert.Throws<ArgumentException>(() => new OpenAiCompatibleHttpTransportOptions(
            confidential.Endpoint, new string('\ud800', 1), networkEnabled: true));
        Assert.Throws<ArgumentException>(() => new OpenAiCompatibleHttpTransportOptions(
            confidential.Endpoint, "model", networkEnabled: true, "contains space"));
        Assert.Throws<ArgumentException>(() => new OpenAiCompatibleHttpTransportOptions(
            confidential.Endpoint, "model", networkEnabled: true, "invalid:token"));
        Assert.Throws<ArgumentException>(() => new OpenAiCompatibleHttpTransportOptions(
            confidential.Endpoint, "model", networkEnabled: true, "=="));
    }

    [Fact]
    public void Production_handler_is_fail_closed_and_has_no_second_deadline()
    {
        using var handler = OpenAiCompatibleHttpModelExchange.CreateProductionHandler();

        Assert.Null(handler.ActivityHeadersPropagator);
        Assert.False(handler.AllowAutoRedirect);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
        Assert.False(handler.UseCookies);
        Assert.False(handler.UseProxy);
        Assert.Equal(16, handler.MaxResponseHeadersLength);
        Assert.Equal(0, handler.MaxResponseDrainSize);
        Assert.Equal(TimeSpan.Zero, handler.ResponseDrainTimeout);
    }

    [Fact]
    public async Task Exchange_emits_the_frozen_non_secret_envelope_and_normalizes_terminal()
    {
        const string marker = "credential-envelope-marker";
        var handler = new CapturingHandler((_, _) => Task.FromResult(JsonResponse("""
            {"choices":[{"index":0,"message":{"role":"assistant","tool_calls":[{"id":"call.terminal","type":"function","function":{"name":"cs_terminal","arguments":"{\"kind\":\"skip\"}"}}]},"finish_reason":"tool_calls"}]}
            """)));
        using var exchange = new OpenAiCompatibleHttpModelExchange(
            new OpenAiCompatibleHttpTransportOptions(
                new Uri("https://example.test/v1/chat/completions"),
                "synthetic-model-v1",
                networkEnabled: true,
                marker),
            handler,
            disposeHandler: false);

        var response = await exchange.SendAsync(Request([]), CancellationToken.None);

        Assert.Single(response.TerminalSubmissions);
        Assert.NotNull(handler.Snapshot);
        Assert.Equal(HttpMethod.Post, handler.Snapshot!.Method);
        Assert.Equal(new Uri("https://example.test/v1/chat/completions"), handler.Snapshot.Uri);
        Assert.Equal(HttpVersion.Version11, handler.Snapshot.Version);
        Assert.Equal(HttpVersionPolicy.RequestVersionExact, handler.Snapshot.VersionPolicy);
        Assert.Equal("application/json; charset=utf-8", handler.Snapshot.ContentType);
        Assert.Equal("application/json", handler.Snapshot.Accept);
        Assert.Equal("Bearer " + marker, handler.Snapshot.Authorization);
        Assert.Null(handler.Snapshot.UserAgent);
        Assert.Empty(handler.Snapshot.TraceHeaders);
        Assert.DoesNotContain(marker, handler.Snapshot.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disabled_network_and_invalid_request_never_reach_the_handler()
    {
        var handler = new CapturingHandler((_, _) => throw new InvalidOperationException());
        using var disabled = new OpenAiCompatibleHttpModelExchange(
            new OpenAiCompatibleHttpTransportOptions(
                new Uri("https://example.test/v1"), "model", networkEnabled: false),
            handler,
            disposeHandler: false);

        var disabledResponse = await disabled.SendAsync(Request([]), CancellationToken.None);

        Assert.Equal(DocumentationScribeModelFailureCode.Unsupported, disabledResponse.Failure!.Code);
        Assert.Equal(0, handler.CallCount);

        var invalidRequest = Request([], tools:
            [new DocumentationScribeModelToolDefinition("tool.invalid", "Invalid.", "[]")]);
        using var enabled = new OpenAiCompatibleHttpModelExchange(
            new OpenAiCompatibleHttpTransportOptions(
                new Uri("https://example.test/v1"), "model", networkEnabled: true),
            handler,
            disposeHandler: false);
        var invalidResponse = await enabled.SendAsync(invalidRequest, CancellationToken.None);

        Assert.Equal(DocumentationScribeModelFailureCode.Unsupported, invalidResponse.Failure!.Code);
        Assert.Equal(0, handler.CallCount);
    }

    [Theory]
    [InlineData(401, DocumentationScribeModelFailureCode.Authentication)]
    [InlineData(403, DocumentationScribeModelFailureCode.Authentication)]
    [InlineData(408, DocumentationScribeModelFailureCode.TransientUnavailable)]
    [InlineData(302, DocumentationScribeModelFailureCode.Unsupported)]
    [InlineData(422, DocumentationScribeModelFailureCode.PermanentUnavailable)]
    [InlineData(500, DocumentationScribeModelFailureCode.TransientUnavailable)]
    [InlineData(501, DocumentationScribeModelFailureCode.PermanentUnavailable)]
    public async Task Non_success_status_wins_over_untrusted_body(
        int status,
        DocumentationScribeModelFailureCode expected)
    {
        const string marker = "provider-error-body-marker";
        var handler = new CapturingHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(marker, Encoding.UTF8, "text/html"),
        }));
        using var exchange = Exchange(handler);

        var response = await exchange.SendAsync(Request([]), CancellationToken.None);

        Assert.Equal(expected, response.Failure!.Code);
        Assert.DoesNotContain(marker, response.Failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Malformed_success_body_marker_does_not_cross_the_failure_boundary()
    {
        const string marker = "malformed-success-marker";
        var handler = new CapturingHandler((_, _) => Task.FromResult(JsonResponse(
            "{\"marker\":\"" + marker + "\",\"choices\":[]}")));
        using var exchange = Exchange(handler);

        var response = await exchange.SendAsync(Request([]), CancellationToken.None);

        Assert.Equal(DocumentationScribeModelFailureCode.MalformedResponse, response.Failure!.Code);
        Assert.DoesNotContain(marker, response.Failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retry_after_is_allowlisted_bounded_delta_seconds_only()
    {
        static HttpResponseMessage Response(string value)
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.TryAddWithoutValidation("Retry-After", value);
            return response;
        }

        var validHandler = new CapturingHandler((_, _) => Task.FromResult(Response("2")));
        using var valid = Exchange(validHandler);
        var validResponse = await valid.SendAsync(Request([]), CancellationToken.None);
        Assert.Equal(2_000, validResponse.Failure!.RetryAfterMilliseconds);

        var invalidHandler = new CapturingHandler((_, _) => Task.FromResult(Response("Wed, 21 Oct 2015 07:28:00 GMT")));
        using var invalid = Exchange(invalidHandler);
        var invalidResponse = await invalid.SendAsync(Request([]), CancellationToken.None);
        Assert.Null(invalidResponse.Failure!.RetryAfterMilliseconds);
    }

    [Fact]
    public async Task Cancellation_precedence_distinguishes_supplied_and_unowned_cancellation()
    {
        var unownedHandler = new CapturingHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new OperationCanceledException("unowned-marker")));
        using var unowned = Exchange(unownedHandler);
        var response = await unowned.SendAsync(Request([]), CancellationToken.None);
        Assert.Equal(DocumentationScribeModelFailureCode.TransientUnavailable, response.Failure!.Code);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await unowned.SendAsync(Request([]), cancellation.Token));
        Assert.Equal(1, unownedHandler.CallCount);
    }

    [Fact]
    public async Task Underlying_exception_is_sanitized_before_network_telemetry_observes_it()
    {
        const string outerMarker = "underlying-exception-secret-marker";
        const string innerMarker = "C:\\private\\machine-path-marker";
        using var observations = new NetworkObservationCollector();
        var handler = new CapturingHandler((_, _) => Task.FromException<HttpResponseMessage>(
            new HttpRequestException(outerMarker, new IOException(innerMarker))));
        using var exchange = Exchange(handler);

        var response = await exchange.SendAsync(Request([]), CancellationToken.None);
        var captured = observations.Text;

        Assert.Equal(DocumentationScribeModelFailureCode.TransientUnavailable, response.Failure!.Code);
        Assert.DoesNotContain(outerMarker, captured, StringComparison.Ordinal);
        Assert.DoesNotContain(innerMarker, captured, StringComparison.Ordinal);
        Assert.DoesNotContain(FindRepositoryRoot(), captured, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(outerMarker, response.Failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(innerMarker, response.Failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provider_response_markers_do_not_cross_observer_or_failure_boundaries()
    {
        const string normalMarker = "normal-provider-response-marker";
        const string malformedMarker = "malformed-provider-response-marker";
        const string errorMarker = "provider-error-response-marker";
        using var observations = new NetworkObservationCollector();
        var responses = new Queue<HttpResponseMessage>(
        [
            JsonResponse("{\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"tool_calls\":[{\"id\":\"call.normal\",\"type\":\"function\",\"function\":{\"name\":\"cs_tool_000\",\"arguments\":\"{\\\"marker\\\":\\\"" + normalMarker + "\\\"}\"}}]},\"finish_reason\":\"tool_calls\"}]}"),
            JsonResponse("{\"marker\":\"" + malformedMarker + "\",\"choices\":[]}"),
            new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent(errorMarker, Encoding.UTF8, "text/plain"),
            },
        ]);
        var handler = new CapturingHandler((_, _) => Task.FromResult(responses.Dequeue()));
        using var exchange = Exchange(handler);

        var normal = await exchange.SendAsync(Request([]), CancellationToken.None);
        var malformed = await exchange.SendAsync(Request([]), CancellationToken.None);
        var error = await exchange.SendAsync(Request([]), CancellationToken.None);
        var captured = observations.Text;

        Assert.Single(normal.ToolCalls);
        Assert.Equal(DocumentationScribeModelFailureCode.MalformedResponse, malformed.Failure!.Code);
        Assert.Equal(DocumentationScribeModelFailureCode.TransientUnavailable, error.Failure!.Code);
        foreach (var marker in new[] { normalMarker, malformedMarker, errorMarker })
        {
            Assert.DoesNotContain(marker, captured, StringComparison.Ordinal);
            Assert.DoesNotContain(marker, malformed.Failure.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(marker, error.Failure.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Response_structure_precedes_finish_reason_and_uses_array_position()
    {
        var prepared = OpenAiCompatibleChatCompletionsCodec.Prepare(Request([]), "model");
        var valid = OpenAiCompatibleChatCompletionsCodec.ParseResponse(Encoding.UTF8.GetBytes("""
            {"choices":[{"index":0,"message":{"role":"assistant","tool_calls":[{"id":"call.a","type":"function","function":{"name":"cs_tool_000","arguments":"{}"}},{"id":"call.z","type":"function","function":{"name":"cs_tool_001","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}
            """), prepared);
        Assert.Equal([0, 1], valid.ToolCalls.Select(call => call.ResponseIndex));

        var malformed = Assert.Throws<OpenAiCompatibleProtocolException>(() =>
            OpenAiCompatibleChatCompletionsCodec.ParseResponse(Encoding.UTF8.GetBytes("""
                {"choices":[{"index":0,"message":{"role":"assistant","tool_calls":[{"id":"call.a","type":"function","function":{"name":"unknown_alias","arguments":"{}"}}]},"finish_reason":"length"}]}
                """), prepared));
        Assert.Equal(DocumentationScribeModelFailureCode.MalformedResponse, malformed.Code);

        var unsupported = Assert.Throws<OpenAiCompatibleProtocolException>(() =>
            OpenAiCompatibleChatCompletionsCodec.ParseResponse(Encoding.UTF8.GetBytes("""
                {"choices":[{"index":0,"message":{"role":"assistant","content":"partial"},"finish_reason":"length"}]}
                """), prepared));
        Assert.Equal(DocumentationScribeModelFailureCode.Unsupported, unsupported.Code);
    }

    [Fact]
    public void Response_rejects_streaming_index_and_cross_round_call_id_before_normalization()
    {
        var completed = new DocumentationScribeCompletedToolExchange(
            0,
            "call.used",
            "tool.alpha",
            Encoding.UTF8.GetBytes("{}").ToImmutableArray(),
            DocumentationScribeToolOutcome.Complete.Id,
            Encoding.UTF8.GetBytes("{}").ToImmutableArray(),
            []);
        var prepared = OpenAiCompatibleChatCompletionsCodec.Prepare(Request([completed]), "model");

        foreach (var body in new[]
        {
            "{\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"tool_calls\":[{\"index\":0,\"id\":\"call.new\",\"type\":\"function\",\"function\":{\"name\":\"cs_tool_000\",\"arguments\":\"{}\"}}]},\"finish_reason\":\"tool_calls\"}]}",
            "{\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"tool_calls\":[{\"id\":\"call.used\",\"type\":\"function\",\"function\":{\"name\":\"cs_tool_000\",\"arguments\":\"{}\"}}]},\"finish_reason\":\"tool_calls\"}]}",
        })
        {
            var exception = Assert.Throws<OpenAiCompatibleProtocolException>(() =>
                OpenAiCompatibleChatCompletionsCodec.ParseResponse(Encoding.UTF8.GetBytes(body), prepared));
            Assert.Equal(DocumentationScribeModelFailureCode.MalformedResponse, exception.Code);
        }
    }

    [Fact]
    public void Terminal_call_ids_use_the_same_bounded_correlation_domain()
    {
        var prepared = OpenAiCompatibleChatCompletionsCodec.Prepare(Request([]), "model");
        foreach (var callId in new[]
        {
            string.Empty,
            "call.\0terminal",
            new string('x', DocumentationScribeBoundary.MaximumCorrelationIdUtf8Bytes + 1),
        })
        {
            var body = TerminalResponse(callId, "{\"kind\":\"skip\"}");
            var exception = Assert.Throws<OpenAiCompatibleProtocolException>(() =>
                OpenAiCompatibleChatCompletionsCodec.ParseResponse(body, prepared));
            Assert.Equal(DocumentationScribeModelFailureCode.MalformedResponse, exception.Code);
        }

        var boundary = OpenAiCompatibleChatCompletionsCodec.ParseResponse(
            TerminalResponse(
                new string('x', DocumentationScribeBoundary.MaximumCorrelationIdUtf8Bytes),
                "{\"kind\":\"skip\"}"),
            prepared);
        Assert.Single(boundary.TerminalSubmissions);

        var completed = new DocumentationScribeCompletedToolExchange(
            0,
            "call.used-terminal-id",
            "tool.alpha",
            Encoding.UTF8.GetBytes("{}").ToImmutableArray(),
            DocumentationScribeToolOutcome.Complete.Id,
            Encoding.UTF8.GetBytes("{}").ToImmutableArray(),
            []);
        var reused = OpenAiCompatibleChatCompletionsCodec.Prepare(Request([completed]), "model");
        var reuse = Assert.Throws<OpenAiCompatibleProtocolException>(() =>
            OpenAiCompatibleChatCompletionsCodec.ParseResponse(
                TerminalResponse("call.used-terminal-id", "{\"kind\":\"skip\"}"),
                reused));
        Assert.Equal(DocumentationScribeModelFailureCode.MalformedResponse, reuse.Code);
    }

    [Fact]
    public void Usage_cache_detail_is_bounded_by_direct_prompt_total()
    {
        var prepared = OpenAiCompatibleChatCompletionsCodec.Prepare(Request([]), "model");
        var invalidDetailOnly = ToolResponseWithUsage(promptTokens: 1, cachedDetail: 2, directHit: null);
        var invalidAgreement = ToolResponseWithUsage(promptTokens: 1, cachedDetail: 2, directHit: 2);
        var exact = ToolResponseWithUsage(promptTokens: 2, cachedDetail: 2, directHit: null);
        var within = ToolResponseWithUsage(promptTokens: 2, cachedDetail: 1, directHit: null);

        foreach (var body in new[] { invalidDetailOnly, invalidAgreement })
        {
            var exception = Assert.Throws<OpenAiCompatibleProtocolException>(() =>
                OpenAiCompatibleChatCompletionsCodec.ParseResponse(body, prepared));
            Assert.Equal(DocumentationScribeModelFailureCode.MalformedResponse, exception.Code);
        }

        Assert.Equal(2,
            OpenAiCompatibleChatCompletionsCodec.ParseResponse(exact, prepared).Usage!.CachedInputTokens);
        Assert.Equal(1,
            OpenAiCompatibleChatCompletionsCodec.ParseResponse(within, prepared).Usage!.CachedInputTokens);
    }

    [Fact]
    public void Usage_cache_components_cannot_exceed_the_prompt_total()
    {
        var prepared = OpenAiCompatibleChatCompletionsCodec.Prepare(Request([]), "model");
        var invalidDirect = ToolResponseWithUsage(
            promptTokens: 100, cachedDetail: 80, directHit: 80, directMiss: 80);
        var invalidDetailAlias = ToolResponseWithUsage(
            promptTokens: 100, cachedDetail: 80, directHit: null, directMiss: 80);
        var exact = ToolResponseWithUsage(
            promptTokens: 100, cachedDetail: 80, directHit: 80, directMiss: 20);

        foreach (var body in new[] { invalidDirect, invalidDetailAlias })
        {
            var exception = Assert.Throws<OpenAiCompatibleProtocolException>(() =>
                OpenAiCompatibleChatCompletionsCodec.ParseResponse(body, prepared));
            Assert.Equal(DocumentationScribeModelFailureCode.MalformedResponse, exception.Code);
        }

        var accepted = OpenAiCompatibleChatCompletionsCodec.ParseResponse(exact, prepared);
        Assert.Equal(80, accepted.Usage!.CachedInputTokens);
        Assert.Equal(20, accepted.Usage.UncachedInputTokens);
        Assert.Equal(DocumentationScribeCacheObservation.Mixed, accepted.Cache);
    }

    [Fact]
    public async Task Contradictory_cache_detail_fails_the_runtime_envelope_without_retry()
    {
        var body = Encoding.UTF8.GetBytes("{\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"tool_calls\":[{\"id\":\"call.terminal\",\"type\":\"function\",\"function\":{\"name\":\"cs_terminal\",\"arguments\":\"{\\\"kind\\\":\\\"skip\\\"}\"}}]},\"finish_reason\":\"tool_calls\"}],\"usage\":{\"prompt_tokens\":1,\"prompt_tokens_details\":{\"cached_tokens\":2}}}");
        var handler = new CapturingHandler((_, _) => Task.FromResult(JsonResponse(Encoding.UTF8.GetString(body))));
        using var exchange = Exchange(handler);
        var request = ScribeRequest(maximumElapsedMilliseconds: 5_000);
        var runtime = new DocumentationScribeRuntime(
            exchange,
            new DocumentationScribeToolRegistryBuilder(request.ToolPolicyId).Build(),
            new DocumentationScribeRuntimeOptions("provider.direct-http.synthetic.v1", "model.synthetic.v1", "protocol.v1"));
        Assert.True(DocumentationScribeAttemptId.TryParse(
            "scribe-attempt.0123456789abcdef0123456789abcdef",
            out var attempt));

        var result = await runtime.RunAsync(request, attempt, ScribePrompt(request));

        Assert.Equal(DocumentationScribeFailureCode.Provider,
            Assert.IsType<DocumentationScribeFailureTerminal>(result.Terminal).Code);
        Assert.Equal(1, result.RunEnvelope.ProviderRequestCount);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public void Malformed_duplicate_ambiguous_and_out_of_order_states_fail_closed()
    {
        var prepared = OpenAiCompatibleChatCompletionsCodec.Prepare(Request([]), "model");
        var malformedBodies = new[]
        {
            ReadProviderFixture("malformed-duplicate-response.json"),
            Encoding.UTF8.GetBytes("{\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"tool_calls\":[{\"id\":\"call.same\",\"type\":\"function\",\"function\":{\"name\":\"cs_tool_000\",\"arguments\":\"{}\"}},{\"id\":\"call.same\",\"type\":\"function\",\"function\":{\"name\":\"cs_tool_001\",\"arguments\":\"{}\"}}]},\"finish_reason\":\"tool_calls\"}]}"),
            Encoding.UTF8.GetBytes("{\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"tool_calls\":[{\"id\":\"call.tool\",\"type\":\"function\",\"function\":{\"name\":\"cs_tool_000\",\"arguments\":\"{}\"}},{\"id\":\"call.terminal\",\"type\":\"function\",\"function\":{\"name\":\"cs_terminal\",\"arguments\":\"{\\\"kind\\\":\\\"skip\\\"}\"}}]},\"finish_reason\":\"tool_calls\"}]}"),
            Encoding.UTF8.GetBytes("{\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"tool_calls\":[{\"id\":\"call.one\",\"type\":\"function\",\"function\":{\"name\":\"cs_terminal\",\"arguments\":\"{\\\"kind\\\":\\\"skip\\\"}\"}},{\"id\":\"call.two\",\"type\":\"function\",\"function\":{\"name\":\"cs_terminal\",\"arguments\":\"{\\\"kind\\\":\\\"skip\\\"}\"}}]},\"finish_reason\":\"tool_calls\"}]}"),
            Encoding.UTF8.GetBytes("{\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"tool_calls\":[]},\"finish_reason\":\"tool_calls\"}]}"),
            Encoding.UTF8.GetBytes("{\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\"},\"finish_reason\":\"tool_calls\"}]}"),
            Encoding.UTF8.GetBytes("{\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"tool_calls\":[{\"id\":\"call.bad-json\",\"type\":\"function\",\"function\":{\"name\":\"cs_tool_000\",\"arguments\":\"{\"}}]},\"finish_reason\":\"tool_calls\"}]}"),
            Encoding.UTF8.GetBytes("{\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"tool_calls\":[{\"id\":\"call.unknown\",\"type\":\"function\",\"function\":{\"name\":\"unknown_alias\",\"arguments\":\"{}\"}}]},\"finish_reason\":\"tool_calls\"}]}"),
            Encoding.UTF8.GetBytes("{\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"tool_calls\":[{\"id\":\"call.one\",\"type\":\"function\",\"function\":{\"name\":\"cs_tool_000\",\"arguments\":\"{}\"}}]},\"finish_reason\":\"tool_calls\"},{\"index\":1,\"message\":{\"role\":\"assistant\"},\"finish_reason\":\"stop\"}]}"),
            Encoding.UTF8.GetBytes("{\"choices\":[{\"index\":1,\"message\":{\"role\":\"assistant\",\"tool_calls\":[{\"id\":\"call.one\",\"type\":\"function\",\"function\":{\"name\":\"cs_tool_000\",\"arguments\":\"{}\"}}]},\"finish_reason\":\"tool_calls\"}]}"),
        };

        foreach (var body in malformedBodies)
        {
            var exception = Assert.Throws<OpenAiCompatibleProtocolException>(() =>
                OpenAiCompatibleChatCompletionsCodec.ParseResponse(body, prepared));
            Assert.Equal(DocumentationScribeModelFailureCode.MalformedResponse, exception.Code);
        }

        var invalidCompletedRound = new DocumentationScribeCompletedToolExchange(
            1,
            "call.gap",
            "tool.alpha",
            Encoding.UTF8.GetBytes("{}").ToImmutableArray(),
            DocumentationScribeToolOutcome.Complete.Id,
            Encoding.UTF8.GetBytes("{}").ToImmutableArray(),
            []);
        var sequencing = Assert.Throws<OpenAiCompatibleProtocolException>(() =>
            OpenAiCompatibleChatCompletionsCodec.Prepare(Request([invalidCompletedRound]), "model"));
        Assert.Equal(DocumentationScribeModelFailureCode.Unsupported, sequencing.Code);
    }

    [Fact]
    public void Cache_zero_zero_is_not_a_product_miss()
    {
        var prepared = OpenAiCompatibleChatCompletionsCodec.Prepare(Request([]), "model");
        var response = OpenAiCompatibleChatCompletionsCodec.ParseResponse(Encoding.UTF8.GetBytes("""
            {"choices":[{"index":0,"message":{"role":"assistant","tool_calls":[{"id":"call.a","type":"function","function":{"name":"cs_tool_000","arguments":"{}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":0,"prompt_cache_hit_tokens":0,"prompt_cache_miss_tokens":0}}
            """), prepared);

        Assert.Equal(0, response.Usage!.CachedInputTokens);
        Assert.Equal(0, response.Usage.UncachedInputTokens);
        Assert.Null(response.Cache);
    }

    [Fact]
    public void Raw_response_bound_is_derived_above_the_normalized_product_bound()
    {
        Assert.True(OpenAiCompatibleChatCompletionsCodec.MaximumRawResponseUtf8Bytes
            > DocumentationScribeContract.MaximumArtifactUtf8Bytes);
        var prepared = OpenAiCompatibleChatCompletionsCodec.Prepare(Request([]), "model");
        var exception = Assert.Throws<OpenAiCompatibleProtocolException>(() =>
            OpenAiCompatibleChatCompletionsCodec.ParseResponse(
                new byte[OpenAiCompatibleChatCompletionsCodec.MaximumRawResponseUtf8Bytes + 1],
                prepared));
        Assert.Equal(DocumentationScribeModelFailureCode.MalformedResponse, exception.Code);
    }

    [Fact]
    public void Derived_raw_bound_carries_large_terminal_and_multi_call_product_payloads()
    {
        var outputLimits = new DocumentationScribeModelOutputLimits(
            maximumToolCalls: 4,
            maximumToolArgumentUtf8Bytes: DocumentationScribeContract.MaximumArtifactUtf8Bytes,
            maximumTerminalUtf8Bytes: DocumentationScribeContract.MaximumArtifactUtf8Bytes,
            maximumOutputTokens: 512,
            maximumNormalizedResponseUtf8Bytes: DocumentationScribeContract.MaximumArtifactUtf8Bytes);
        var prepared = OpenAiCompatibleChatCompletionsCodec.Prepare(
            Request([], outputLimits: outputLimits),
            "model");
        var terminalArguments = JsonSerializer.Serialize(new { value = new string('\\', 400_000) });
        var terminalBody = JsonSerializer.SerializeToUtf8Bytes(new
        {
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new
                    {
                        role = "assistant",
                        tool_calls = new[]
                        {
                            new
                            {
                                id = "call.terminal",
                                type = "function",
                                function = new { name = "cs_terminal", arguments = terminalArguments },
                            },
                        },
                    },
                    finish_reason = "tool_calls",
                },
            },
        });

        Assert.True(terminalBody.Length > DocumentationScribeContract.MaximumArtifactUtf8Bytes);
        Assert.True(terminalBody.Length < OpenAiCompatibleChatCompletionsCodec.MaximumRawResponseUtf8Bytes);
        Assert.Single(OpenAiCompatibleChatCompletionsCodec.ParseResponse(terminalBody, prepared).TerminalSubmissions);

        var callArguments = JsonSerializer.Serialize(new { value = new string('\\', 180_000) });
        var callsBody = JsonSerializer.SerializeToUtf8Bytes(new
        {
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new
                    {
                        role = "assistant",
                        tool_calls = new[]
                        {
                            new
                            {
                                id = "call.alpha",
                                type = "function",
                                function = new { name = "cs_tool_000", arguments = callArguments },
                            },
                            new
                            {
                                id = "call.zeta",
                                type = "function",
                                function = new { name = "cs_tool_001", arguments = callArguments },
                            },
                        },
                    },
                    finish_reason = "tool_calls",
                },
            },
            usage = new
            {
                prompt_tokens = DocumentationScribeContract.MaximumObservedInputTokens,
                completion_tokens = DocumentationScribeContract.MaximumObservedOutputTokens,
                prompt_cache_hit_tokens = 1,
                prompt_cache_miss_tokens = 1,
            },
        });
        var calls = OpenAiCompatibleChatCompletionsCodec.ParseResponse(callsBody, prepared);
        Assert.Equal(2, calls.ToolCalls.Length);
        Assert.Equal(DocumentationScribeCacheObservation.Mixed, calls.Cache);
    }

    [Fact]
    public async Task Request_body_overflow_is_unsupported_before_handler_creation()
    {
        var handler = new CapturingHandler((_, _) => throw new InvalidOperationException());
        using var exchange = Exchange(handler);
        var oversizedMessages = ImmutableArray.Create(new DocumentationScribeModelMessage(
            DocumentationScribeMessageKind.SystemPolicy,
            new string('x', DocumentationScribeBoundary.MaximumLogicalRequestUtf8Bytes)));

        var response = await exchange.SendAsync(
            Request([], messages: oversizedMessages),
            CancellationToken.None);

        Assert.Equal(DocumentationScribeModelFailureCode.Unsupported, response.Failure!.Code);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public void Cumulative_continuation_history_is_rejected_at_the_logical_request_bound()
    {
        var reasoning = new string('r', 1_048_000);
        var completed = Enumerable.Range(0, 33)
            .Select(index => new DocumentationScribeCompletedToolExchange(
                0,
                $"call.round-{index:D2}",
                "tool.alpha",
                Encoding.UTF8.GetBytes("{}").ToImmutableArray(),
                DocumentationScribeToolOutcome.Complete.Id,
                Encoding.UTF8.GetBytes("{}").ToImmutableArray(),
                [],
                new DocumentationScribeAssistantContinuation(string.Empty, reasoning)))
            .ToImmutableArray();

        var exception = Assert.Throws<OpenAiCompatibleProtocolException>(() =>
            OpenAiCompatibleChatCompletionsCodec.Prepare(
                Request(completed),
                "model",
                RequiredContinuationProfile()));

        Assert.Equal(DocumentationScribeModelFailureCode.Unsupported, exception.Code);
    }

    [Fact]
    public void Completed_transcript_reconstructs_two_rounds_without_double_encoding()
    {
        static DocumentationScribeCompletedToolExchange Completed(
            int index,
            string callId,
            string operationId,
            string value) => new(
                index,
                callId,
                operationId,
                Encoding.UTF8.GetBytes("{\"value\":\"" + value + "\"}").ToImmutableArray(),
                DocumentationScribeToolOutcome.Complete.Id,
                Encoding.UTF8.GetBytes("{\"value\":\"" + value + "\"}").ToImmutableArray(),
                []);
        var prepared = OpenAiCompatibleChatCompletionsCodec.Prepare(Request(
        [
            Completed(0, "call.one", "tool.alpha", "one"),
            Completed(1, "call.two", "tool.zeta", "two"),
            Completed(0, "call.three", "tool.alpha", "three"),
        ]), "model");

        using var body = JsonDocument.Parse(prepared.BodyUtf8);
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal(10, messages.GetArrayLength());
        Assert.Equal(["assistant", "tool", "tool", "assistant", "tool"],
            messages.EnumerateArray().Skip(5).Select(message => message.GetProperty("role").GetString()));
        using var result = JsonDocument.Parse(messages[6].GetProperty("content").GetString()!);
        Assert.Equal("one", result.RootElement.GetProperty("result").GetProperty("value").GetString());
    }

    [Fact]
    public async Task Runtime_deadline_cancels_the_selected_exchange_and_remains_timeout()
    {
        const string marker = "deadline-transport-marker";
        var handler = new HoldingHandler(marker);
        using var exchange = Exchange(handler);
        var request = ScribeRequest(maximumElapsedMilliseconds: 100);
        var runtime = new DocumentationScribeRuntime(
            exchange,
            new DocumentationScribeToolRegistryBuilder(request.ToolPolicyId).Build(),
            new DocumentationScribeRuntimeOptions(
                "provider.direct-http.synthetic.v1",
                "model.synthetic.v1",
                "protocol.openai-compatible.v1"));
        Assert.True(DocumentationScribeAttemptId.TryParse(
            "scribe-attempt.0123456789abcdef0123456789abcdef",
            out var attempt));

        var result = await runtime.RunAsync(request, attempt, ScribePrompt(request));

        Assert.Equal(DocumentationScribeFailureCode.Timeout,
            Assert.IsType<DocumentationScribeFailureTerminal>(result.Terminal).Code);
        Assert.Equal(1, result.RunEnvelope.ProviderRequestCount);
        Assert.Equal(1, handler.CallCount);
        Assert.True(handler.CancellationObserved);
        Assert.DoesNotContain(marker, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(marker, result.Terminal.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Checked_in_transport_fixtures_are_minimized_and_marker_free()
    {
        var directory = Path.Join(
            FindRepositoryRoot(),
            "tests",
            "fixtures",
            "documentation-scribe",
            "provider-transport");
        var combined = string.Join('\n', Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)
            .Select(File.ReadAllText));

        foreach (var forbidden in new[]
        {
            "credential-marker-value",
            "provider-error-body-marker",
            "deadline-transport-marker",
            "C:\\",
            "/home/",
            "Authorization",
        })
        {
            Assert.DoesNotContain(forbidden, combined, StringComparison.OrdinalIgnoreCase);
        }

        Assert.True(Encoding.UTF8.GetByteCount(combined) < 8_192);
    }

    [Fact]
    public void Selection_record_pins_candidates_and_adds_no_package()
    {
        var root = FindRepositoryRoot();
        var record = File.ReadAllText(Path.Join(
            root, "src", "ContractScribe.Agent", "Providers", "transport-selection.md"));
        var project = File.ReadAllText(Path.Join(
            root, "src", "ContractScribe.Agent", "ContractScribe.Agent.csproj"));

        Assert.Contains("OpenAI` 2.12.0", record, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Extensions.AI` 10.8.3", record, StringComparison.Ordinal);
        Assert.Contains(".NET runtime v10.0.2", record, StringComparison.Ordinal);
        Assert.Contains("4d4fccd7033d2b4cdc5bd1bcf906c389d00e6cfe", record, StringComparison.Ordinal);
        Assert.DoesNotContain("PackageReference", project, StringComparison.Ordinal);
    }

    [Fact]
    public void Selected_tool_ceiling_includes_the_terminal_and_is_pre_network()
    {
        static ImmutableArray<DocumentationScribeModelToolDefinition> Tools(int count) =>
            Enumerable.Range(0, count)
                .Select(index => new DocumentationScribeModelToolDefinition(
                    $"tool.operation.{index:D3}",
                    "Reads evidence.",
                    "{\"type\":\"object\"}"))
                .ToImmutableArray();

        var exact = OpenAiCompatibleChatCompletionsCodec.Prepare(
            Request([], tools: Tools(OpenAiCompatibleChatCompletionsCodec.MaximumOrdinaryTools)),
            "model");
        using (var body = JsonDocument.Parse(exact.BodyUtf8))
        {
            Assert.Equal(128, body.RootElement.GetProperty("tools").GetArrayLength());
        }

        var over = Assert.Throws<OpenAiCompatibleProtocolException>(() =>
            OpenAiCompatibleChatCompletionsCodec.Prepare(
                Request([], tools: Tools(OpenAiCompatibleChatCompletionsCodec.MaximumOrdinaryTools + 1)),
                "model"));
        Assert.Equal(DocumentationScribeModelFailureCode.Unsupported, over.Code);
    }

    [Fact]
    public async Task Production_network_observers_do_not_receive_request_or_credential_markers()
    {
        const string credentialMarker = "diagnostic-credential-marker";
        const string systemMarker = "diagnostic-system-marker";
        const string repositoryMarker = "diagnostic-repository-marker";
        const string contextMarker = "diagnostic-context-marker";
        const string runMarker = "diagnostic-run-marker";
        const string evidenceMarker = "diagnostic-evidence-marker";
        const string resultMarker = "diagnostic-tool-result-marker";
        using var observations = new NetworkObservationCollector();
        using var exchange = new OpenAiCompatibleHttpModelExchange(
            new OpenAiCompatibleHttpTransportOptions(
                new Uri("https://127.0.0.1:1/v1"),
                "model",
                networkEnabled: true,
                credentialMarker));
        var messages = ImmutableArray.Create(
            new DocumentationScribeModelMessage(
                DocumentationScribeMessageKind.SystemPolicy,
                "{\"marker\":\"" + systemMarker + "\"}"),
            new DocumentationScribeModelMessage(
                DocumentationScribeMessageKind.RepositoryInstructions,
                "{\"marker\":\"" + repositoryMarker + "\"}"),
            new DocumentationScribeModelMessage(
                DocumentationScribeMessageKind.MaintainedContext,
                "{\"marker\":\"" + contextMarker + "\"}"),
            new DocumentationScribeModelMessage(
                DocumentationScribeMessageKind.RunPolicy,
                "{\"marker\":\"" + runMarker + "\"}"),
            new DocumentationScribeModelMessage(
                DocumentationScribeMessageKind.TargetEvidence,
                "{\"marker\":\"" + evidenceMarker + "\"}"));
        var completed = new DocumentationScribeCompletedToolExchange(
            0,
            "call.marker",
            "tool.alpha",
            Encoding.UTF8.GetBytes("{}").ToImmutableArray(),
            DocumentationScribeToolOutcome.Complete.Id,
            Encoding.UTF8.GetBytes("{\"marker\":\"" + resultMarker + "\"}").ToImmutableArray(),
            []);

        var response = await exchange.SendAsync(Request([completed], messages: messages), CancellationToken.None);
        var captured = observations.Text;

        Assert.NotNull(response.Failure);
        foreach (var marker in new[]
        {
            credentialMarker,
            systemMarker,
            repositoryMarker,
            contextMarker,
            runMarker,
            evidenceMarker,
            resultMarker,
        })
        {
            Assert.DoesNotContain(marker, captured, StringComparison.Ordinal);
            Assert.DoesNotContain(marker, response.Failure.ToString(), StringComparison.Ordinal);
        }
    }

    private static DocumentationScribeModelRequest Request(
        ImmutableArray<DocumentationScribeCompletedToolExchange> completed,
        ImmutableArray<DocumentationScribeModelToolDefinition>? tools = null,
        ImmutableArray<DocumentationScribeModelMessage>? messages = null,
        DocumentationScribeModelOutputLimits? outputLimits = null) => new(
            attemptNumber: 1,
            providerRequestNumber: completed.IsEmpty ? 1 : 2,
            messages: messages ??
            [
                new DocumentationScribeModelMessage(DocumentationScribeMessageKind.SystemPolicy, "{\"block\":\"system\"}"),
                new DocumentationScribeModelMessage(DocumentationScribeMessageKind.RepositoryInstructions, "{\"block\":\"repository\"}"),
                new DocumentationScribeModelMessage(DocumentationScribeMessageKind.MaintainedContext, "{\"block\":\"context\"}"),
                new DocumentationScribeModelMessage(DocumentationScribeMessageKind.RunPolicy, "{\"block\":\"run\"}"),
                new DocumentationScribeModelMessage(DocumentationScribeMessageKind.TargetEvidence, "{\"block\":\"evidence\"}"),
            ],
            tools: tools ??
            [
                new DocumentationScribeModelToolDefinition("tool.zeta", "Reads zeta.", "{\"type\":\"object\"}"),
                new DocumentationScribeModelToolDefinition("tool.alpha", "Reads alpha.", "{\"type\":\"object\"}"),
            ],
            terminal: new DocumentationScribeTerminalDefinition(
                "scribe.submit-terminal",
                "{\"type\":\"object\"}"),
            completedToolExchanges: completed,
            outputLimits: outputLimits ?? new DocumentationScribeModelOutputLimits(
                maximumToolCalls: 4,
                maximumToolArgumentUtf8Bytes: 4_096,
                maximumTerminalUtf8Bytes: 4_096,
                maximumOutputTokens: 512,
                maximumNormalizedResponseUtf8Bytes: DocumentationScribeContract.MaximumArtifactUtf8Bytes),
            deterministicUtf8: []);

    private static OpenAiCompatibleHttpModelExchange Exchange(HttpMessageHandler handler) => new(
        new OpenAiCompatibleHttpTransportOptions(
            new Uri("https://example.test/v1"), "model", networkEnabled: true),
        handler,
        disposeHandler: false);

    private static OpenAiCompatibleChatCompletionsRequestProfile RequiredContinuationProfile() => new(
        OpenAiCompatibleThinkingMode.Enabled,
        OpenAiCompatibleReasoningEffort.High,
        OpenAiCompatibleToolChoice.Omitted,
        OpenAiCompatibleContinuationPolicy.RequiredForToolCalls,
        OpenAiCompatibleOutputTokenField.MaxTokens);

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static byte[] TerminalResponse(string callId, string arguments) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new
                    {
                        role = "assistant",
                        tool_calls = new[]
                        {
                            new
                            {
                                id = callId,
                                type = "function",
                                function = new { name = OpenAiCompatibleChatCompletionsCodec.TerminalAlias, arguments },
                            },
                        },
                    },
                    finish_reason = "tool_calls",
                },
            },
        });

    private static byte[] ToolResponseWithUsage(
        int promptTokens,
        int cachedDetail,
        int? directHit,
        int? directMiss = null) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new
                    {
                        role = "assistant",
                        tool_calls = new[]
                        {
                            new
                            {
                                id = "call.usage",
                                type = "function",
                                function = new { name = "cs_tool_000", arguments = "{}" },
                            },
                        },
                    },
                    finish_reason = "tool_calls",
                },
            },
            usage = new
            {
                prompt_tokens = promptTokens,
                prompt_cache_hit_tokens = directHit,
                prompt_cache_miss_tokens = directMiss,
                prompt_tokens_details = new { cached_tokens = cachedDetail },
            },
        }, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });

    private static DocumentationScribeRequest ScribeRequest(int maximumElapsedMilliseconds)
    {
        var bytes = File.ReadAllBytes(Path.Join(
            FindRepositoryRoot(),
            "tests",
            "fixtures",
            "documentation-scribe",
            "v1",
            "valid",
            "request.json"));
        var root = JsonNode.Parse(bytes)!.AsObject();
        root["limits"]!["maximumElapsedMilliseconds"] = maximumElapsedMilliseconds;
        var parsed = DocumentationScribeValidation.ParseRequest(
            Encoding.UTF8.GetBytes(root.ToJsonString(new JsonSerializerOptions { WriteIndented = false })));
        return Assert.IsType<DocumentationScribeRequest>(parsed.Request);
    }

    private static DocumentationScribePromptInput ScribePrompt(DocumentationScribeRequest request)
    {
        var context = request.ContextReferences.Select(reference => new DocumentationScribeContextContent(
            reference.ContextReferenceId,
            reference.Kind,
            reference.ContentSha256,
            reference.IncludedUtf8ByteCount,
            reference.IsTruncated,
            new string('c', reference.IncludedUtf8ByteCount))).ToImmutableArray();
        var evidence = request.EvidenceReferences.Select(reference => new DocumentationScribeEvidenceContent(
            reference.EvidenceReferenceId,
            reference.Authority,
            reference.ContentSha256,
            reference.IncludedUtf8ByteCount,
            reference.IsTruncated,
            new string('e', reference.IncludedUtf8ByteCount))).ToImmutableArray();
        return new DocumentationScribePromptInput(context, evidence);
    }

    private static void AssertEvidence(
        JsonElement actual,
        DocumentationScribeEvidenceReference expected)
    {
        Assert.Equal(expected.EvidenceReferenceId, actual.GetProperty("evidenceReferenceId").GetString());
        Assert.Equal(
            DocumentationScribeVocabulary.GetId(expected.Authority),
            actual.GetProperty("authority").GetString());
        Assert.Equal(expected.ContentSha256, actual.GetProperty("contentSha256").GetString());
        Assert.Equal(expected.OriginalUtf8ByteCount, actual.GetProperty("originalUtf8ByteCount").GetInt32());
        Assert.Equal(expected.IncludedUtf8ByteCount, actual.GetProperty("includedUtf8ByteCount").GetInt32());
        Assert.Equal(expected.IsTruncated, actual.GetProperty("isTruncated").GetBoolean());
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "ContractScribe.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static byte[] ReadProviderFixture(string name) => File.ReadAllBytes(Path.Join(
        FindRepositoryRoot(),
        "tests",
        "fixtures",
        "documentation-scribe",
        "provider-transport",
        name));

    private sealed class CapturingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        internal int CallCount { get; private set; }

        internal RequestSnapshot? Snapshot { get; private set; }

        internal List<RequestSnapshot> Snapshots { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Snapshot = new RequestSnapshot(
                request.Method,
                request.RequestUri,
                request.Version,
                request.VersionPolicy,
                request.Content?.Headers.ContentType?.ToString(),
                string.Join(",", request.Headers.Accept),
                request.Headers.Authorization?.ToString(),
                request.Headers.UserAgent.Count == 0 ? null : request.Headers.UserAgent.ToString(),
                request.Headers.Where(header => header.Key.StartsWith("trace", StringComparison.OrdinalIgnoreCase))
                    .Select(header => header.Key)
                    .ToImmutableArray(),
                body);
            Snapshots.Add(Snapshot);
            return await response(request, cancellationToken);
        }
    }

    private sealed record RequestSnapshot(
        HttpMethod Method,
        Uri? Uri,
        Version Version,
        HttpVersionPolicy VersionPolicy,
        string? ContentType,
        string Accept,
        string? Authorization,
        string? UserAgent,
        ImmutableArray<string> TraceHeaders,
        string Body);

    private sealed class StaticExchange(DocumentationScribeModelResponse response) :
        IDocumentationScribeModelExchange
    {
        public ValueTask<DocumentationScribeModelResponse> SendAsync(
            DocumentationScribeModelRequest request,
            CancellationToken cancellationToken) => ValueTask.FromResult(response);
    }

    private sealed class HoldingHandler(string marker) : HttpMessageHandler
    {
        internal int CallCount { get; private set; }

        internal bool CancellationObserved { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            await request.Content!.CopyToAsync(Stream.Null, cancellationToken);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw new OperationCanceledException(marker, cancellationToken);
            }

            throw new InvalidOperationException();
        }
    }

    private sealed class NetworkObservationCollector : EventListener, IObserver<DiagnosticListener>, IDisposable
    {
        private readonly ConcurrentQueue<string> values = new();
        private readonly List<IDisposable> subscriptions = [];
        private readonly IDisposable allListeners;
        private readonly ActivityListener activityListener;

        internal NetworkObservationCollector()
        {
            allListeners = DiagnosticListener.AllListeners.Subscribe(this);
            activityListener = new ActivityListener
            {
                ShouldListenTo = _ => true,
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = activity =>
                {
                    values.Enqueue(activity.DisplayName);
                    foreach (var tag in activity.TagObjects)
                    {
                        values.Enqueue(tag.Key);
                        values.Enqueue(tag.Value?.ToString() ?? string.Empty);
                    }
                },
            };
            ActivitySource.AddActivityListener(activityListener);
            foreach (var source in EventSource.GetSources())
            {
                EnableIfNetwork(source);
            }
        }

        internal string Text => string.Join('\n', values);

        public void OnCompleted()
        {
        }

        public void OnError(Exception error) => values.Enqueue(error.GetType().Name);

        public void OnNext(DiagnosticListener value)
        {
            if (value.Name.Contains("Http", StringComparison.OrdinalIgnoreCase)
                || value.Name.Contains("Net", StringComparison.OrdinalIgnoreCase))
            {
                subscriptions.Add(value.Subscribe(new DiagnosticEventObserver(values)));
            }
        }

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (values is not null)
            {
                EnableIfNetwork(eventSource);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            values.Enqueue(eventData.EventName ?? string.Empty);
            if (eventData.Payload is not null)
            {
                foreach (var payload in eventData.Payload)
                {
                    values.Enqueue(payload?.ToString() ?? string.Empty);
                }
            }
        }

        public new void Dispose()
        {
            foreach (var subscription in subscriptions)
            {
                subscription.Dispose();
            }

            allListeners.Dispose();
            activityListener.Dispose();
            base.Dispose();
        }

        private void EnableIfNetwork(EventSource source)
        {
            if (source.Name.StartsWith("System.Net.", StringComparison.Ordinal))
            {
                EnableEvents(source, EventLevel.Verbose, EventKeywords.All);
            }
        }

        private sealed class DiagnosticEventObserver(ConcurrentQueue<string> values) :
            IObserver<KeyValuePair<string, object?>>
        {
            public void OnCompleted()
            {
            }

            public void OnError(Exception error) => values.Enqueue(error.GetType().Name);

            public void OnNext(KeyValuePair<string, object?> value)
            {
                values.Enqueue(value.Key);
                if (value.Value is HttpRequestMessage request)
                {
                    values.Enqueue(request.Headers.ToString());
                    return;
                }

                if (value.Value is not null)
                {
                    foreach (var property in value.Value.GetType().GetProperties())
                    {
                        if (property.GetIndexParameters().Length != 0)
                        {
                            continue;
                        }

                        try
                        {
                            var propertyValue = property.GetValue(value.Value);
                            values.Enqueue(propertyValue is HttpRequestMessage message
                                ? message.Headers.ToString()
                                : propertyValue?.ToString() ?? string.Empty);
                        }
                        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
                        {
                            values.Enqueue(exception.GetType().Name);
                        }
                    }
                }
            }
        }
    }
}
