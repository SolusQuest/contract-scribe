using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Agent.Providers;
using ContractScribe.Agent.Runtime;
using ContractScribe.Core;

namespace ContractScribe.Tests;

public sealed partial class DocumentationScribeProviderTransportTests
{
    [Theory]
    [InlineData("syntax", "tool_calls", "codec.arguments.invalid", DocumentationScribeModelFailureCode.MalformedResponse)]
    [InlineData("non-string", "tool_calls", "codec.arguments.not-string", DocumentationScribeModelFailureCode.MalformedResponse)]
    [InlineData("duplicate", "tool_calls", "codec.arguments.duplicate-property", DocumentationScribeModelFailureCode.MalformedResponse)]
    [InlineData("depth", "tool_calls", "codec.arguments.invalid", DocumentationScribeModelFailureCode.MalformedResponse)]
    [InlineData("syntax", "length", "codec.finish-reason.length", DocumentationScribeModelFailureCode.Unsupported)]
    [InlineData("syntax", "stop", "codec.finish-reason.stop", DocumentationScribeModelFailureCode.Unsupported)]
    public async Task Rejected_arguments_retain_strict_usage_without_releasing_any_partial_tool_call(
        string shape, string finish, string expectedDisposition, DocumentationScribeModelFailureCode expectedFailure)
    {
        var body = CodecFailureBody(shape, finish, "valid");
        var exception = Assert.Throws<OpenAiCompatibleProtocolException>(() =>
            OpenAiCompatibleChatCompletionsCodec.ParseResponse(Encoding.UTF8.GetBytes(body),
                OpenAiCompatibleChatCompletionsCodec.Prepare(Request([]), "model")));
        Assert.Equal(expectedDisposition, OpenAiCompatibleResponseDiagnosticVocabulary.GetId(exception.Disposition!.Value));
        var handler = new CapturingHandler((_, _) => Task.FromResult(JsonResponse(body)));
        using var exchange = Exchange(handler);
        var response = await exchange.SendAsync(Request([]), default);
        Assert.Equal(expectedFailure, response.Failure!.Code);
        Assert.Equal(DocumentationScribeModelFailureOrigin.ResponseCodec, response.Failure.Origin);
        Assert.Empty(response.ToolCalls); Assert.Empty(response.TerminalSubmissions);
        Assert.Null(response.AssistantContinuation); Assert.Equal(1, handler.CallCount);
        Assert.NotNull(response.Usage);
        Assert.Equal(31, response.Usage.InputTokens); Assert.Equal(7, response.Usage.OutputTokens);
        Assert.Equal(29, response.Usage.CachedInputTokens); Assert.Equal(2, response.Usage.UncachedInputTokens);
        Assert.Equal(0, response.Usage.ReasoningTokens);
        foreach (var diagnostic in new[] { exception.Message, response.Failure.ToString(),
            JsonSerializer.Serialize(new OpenAiCompatibleResponseDiagnostic(1, exception.Disposition.Value)) })
            Assert.DoesNotContain("private-source-raw-wire-key-marker", diagnostic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("partial")]
    [InlineData("bad-kind")]
    [InlineData("negative")]
    [InlineData("bad-partition")]
    [InlineData("bad-reasoning")]
    public async Task Rejected_arguments_never_turn_missing_or_invalid_usage_into_observed_zero(string usage)
    {
        var body = CodecFailureBody("syntax", "tool_calls", usage);
        var exception = Assert.Throws<OpenAiCompatibleProtocolException>(() =>
            OpenAiCompatibleChatCompletionsCodec.ParseResponse(Encoding.UTF8.GetBytes(body),
                OpenAiCompatibleChatCompletionsCodec.Prepare(Request([]), "model")));
        Assert.Equal(OpenAiCompatibleResponseCodecDisposition.ArgumentsInvalid, exception.Disposition);
        var handler = new CapturingHandler((_, _) => Task.FromResult(JsonResponse(body)));
        using var exchange = Exchange(handler);
        var response = await exchange.SendAsync(Request([]), default);
        Assert.Equal(DocumentationScribeModelFailureCode.MalformedResponse, response.Failure!.Code);
        Assert.Empty(response.ToolCalls); Assert.Empty(response.TerminalSubmissions);
        if (usage == "partial")
        {
            Assert.Equal(31, response.Usage!.InputTokens);
            Assert.Null(response.Usage.OutputTokens); Assert.Null(response.Usage.CachedInputTokens);
            Assert.Null(response.Usage.UncachedInputTokens); Assert.Null(response.Usage.ReasoningTokens);
        }
        else Assert.Null(response.Usage);
        Assert.Null(response.Cache);
    }

    [Fact]
    public async Task Invalid_usage_still_rejects_an_otherwise_valid_tool_response()
    {
        var handler = new CapturingHandler((_, _) => Task.FromResult(
            JsonResponse(CodecFailureBody("valid", "tool_calls", "bad-kind"))));
        using var exchange = Exchange(handler);
        var response = await exchange.SendAsync(Request([]), default);
        Assert.Equal(DocumentationScribeModelFailureCode.MalformedResponse, response.Failure!.Code);
        Assert.Null(response.Usage); Assert.Empty(response.ToolCalls);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("duplicate-usage")]
    public async Task Invalid_outer_JSON_never_salvages_usage_from_an_ambiguous_or_partial_body(string defect)
    {
        var body = CodecFailureBody("syntax", "tool_calls", "valid");
        var broken = defect == "json" ? body[..^1]
            : body.Replace("\"prompt_tokens\":31", "\"prompt_tokens\":31,\"prompt_tokens\":32", StringComparison.Ordinal);
        Assert.NotEqual(body, broken);
        var handler = new CapturingHandler((_, _) => Task.FromResult(JsonResponse(broken)));
        using var exchange = Exchange(handler);
        var response = await exchange.SendAsync(Request([]), default);
        Assert.Equal(DocumentationScribeModelFailureCode.MalformedResponse, response.Failure!.Code);
        Assert.Null(response.Usage); Assert.Null(response.Cache);
        Assert.Empty(response.ToolCalls); Assert.Empty(response.TerminalSubmissions);
    }

    [Theory]
    [InlineData("length", OpenAiCompatibleResponseCodecDisposition.FinishReasonLength)]
    [InlineData("stop", OpenAiCompatibleResponseCodecDisposition.FinishReasonStop)]
    public async Task Known_incomplete_finish_keeps_its_primary_reason_without_accepting_invalid_usage(
        string finish, OpenAiCompatibleResponseCodecDisposition expected)
    {
        var body = CodecFailureBody("syntax", finish, "bad-kind");
        var exception = Assert.Throws<OpenAiCompatibleProtocolException>(() =>
            OpenAiCompatibleChatCompletionsCodec.ParseResponse(Encoding.UTF8.GetBytes(body),
                OpenAiCompatibleChatCompletionsCodec.Prepare(Request([]), "model")));
        Assert.Equal(expected, exception.Disposition);
        var handler = new CapturingHandler((_, _) => Task.FromResult(JsonResponse(body)));
        using var exchange = Exchange(handler);
        var response = await exchange.SendAsync(Request([]), default);
        Assert.Equal(DocumentationScribeModelFailureCode.Unsupported, response.Failure!.Code);
        Assert.Null(response.Usage); Assert.Empty(response.ToolCalls);
    }

    [Fact]
    public async Task Fully_valid_tool_batch_and_usage_remain_accepted_together()
    {
        var handler = new CapturingHandler((_, _) => Task.FromResult(
            JsonResponse(CodecFailureBody("valid", "tool_calls", "valid"))));
        using var exchange = Exchange(handler);
        var response = await exchange.SendAsync(Request([]), default);
        Assert.Null(response.Failure); Assert.Equal(2, response.ToolCalls.Length);
        Assert.Equal(31, response.Usage!.InputTokens); Assert.Equal(7, response.Usage.OutputTokens);
    }

    private static string CodecFailureBody(string shape, string finish, string usage)
    {
        JsonNode? arguments = shape switch
        {
            "valid" => JsonValue.Create("{}"),
            "non-string" => new JsonObject { ["private-source-raw-wire-key-marker"] = 1 },
            "duplicate" => JsonValue.Create("{\"private-source-raw-wire-key-marker\":1,\"private-source-raw-wire-key-marker\":2}"),
            "depth" => JsonValue.Create(new string('[', DocumentationScribeContract.MaximumJsonDepth + 1)
                + "0" + new string(']', DocumentationScribeContract.MaximumJsonDepth + 1)),
            _ => JsonValue.Create("{"),
        };
        JsonNode Call(string id, JsonNode? value) => new JsonObject
        {
            ["id"] = id,
            ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = "cs_tool_000", ["arguments"] = value },
        };
        var root = new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["message"] = new JsonObject
                {
                    ["role"] = "assistant",
                    ["reasoning_content"] = "private-source-raw-wire-key-marker",
                    ["tool_calls"] = new JsonArray(Call("call.prefix", JsonValue.Create("{}")), Call("call.rejected", arguments)),
                },
                ["finish_reason"] = finish,
            }),
        };
        if (usage != "missing") root["usage"] = usage switch
        {
            "null" => null,
            "empty" => new JsonObject(),
            "partial" => new JsonObject { ["prompt_tokens"] = 31 },
            "bad-kind" => new JsonObject { ["prompt_tokens"] = "private-source-raw-wire-key-marker" },
            "negative" => new JsonObject { ["prompt_tokens"] = -1 },
            "bad-partition" => new JsonObject { ["prompt_tokens"] = 31, ["prompt_cache_hit_tokens"] = 31, ["prompt_cache_miss_tokens"] = 1 },
            "bad-reasoning" => new JsonObject
            {
                ["completion_tokens"] = 7,
                ["completion_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 8 }
            },
            _ => new JsonObject
            {
                ["prompt_tokens"] = 31,
                ["completion_tokens"] = 7,
                ["prompt_cache_hit_tokens"] = 29,
                ["prompt_cache_miss_tokens"] = 2,
                ["completion_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 0 },
            },
        };
        return root.ToJsonString();
    }
}
