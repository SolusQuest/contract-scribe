using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ContractScribe.Agent.Providers;
using ContractScribe.Agent.Prompting;
using ContractScribe.Agent.Runtime;
using ContractScribe.Cli;
using ContractScribe.Core;

namespace ContractScribe.Tests;

public sealed partial class DocumentationScribeRuntimeTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("text")]
    public async Task Incremental_actual_HTTP_replays_exact_submit_content_and_reasoning_with_real_acknowledgements(string shape)
    {
        var request = IncrementalRequest(); var ids = request.ExecutableTargetIds;
        var sent = new List<JsonNode>();
        using var handler = new IncrementalProtocolHandler(async (message, token) =>
        {
            sent.Add(JsonNode.Parse(await message.Content!.ReadAsStringAsync(token))!);
            var number = sent.Count;
            var payload = number < 3 ? SubmitBatch(request, number, (ids[number - 1], IncrementalProposal(request, ids[number - 1])))
                .TerminalSubmissions[0].TerminalUtf8Json.ToArray() : ScribeResultFixture.Finish(request);
            return IncrementalHttp(payload, "call.incremental." + number, shape, "opaque-submit-reasoning." + number);
        });
        using var transport = IncrementalTransport(handler);
        var result = await ExecuteIncremental(request, transport, EmptyRegistry());
        Assert.Equal(3, sent.Count); Assert.Equal(3, result.RunEnvelope.ProviderRequestCount); Assert.Equal(2, result.Submissions.Length);
        Assert.Equal(DocumentationScribeFinishReason.Unresolved, Assert.IsType<DocumentationScribeFinishedTerminal>(result.Stop).Reason);
        for (var number = 1; number < 3; number++)
        {
            var messages = sent[number]!["messages"]!.AsArray();
            var assistants = messages.Where(item => item!["role"]!.GetValue<string>() == "assistant").ToArray();
            Assert.Equal(number, assistants.Length);
            for (var index = 0; index < number; index++)
            {
                var assistant = assistants[index]!.AsObject();
                Assert.Equal(shape != "missing", assistant.ContainsKey("content"));
                if (shape == "null") Assert.Null(assistant["content"]);
                else if (shape != "missing") Assert.Equal(shape == "empty" ? "" : "exact assistant text", assistant["content"]!.GetValue<string>());
                Assert.Equal("opaque-submit-reasoning." + (index + 1), assistant["reasoning_content"]!.GetValue<string>());
                var id = assistant["tool_calls"]![0]!["id"]!.GetValue<string>();
                var ack = messages.Single(item => item!["role"]!.GetValue<string>() == "tool" && item["tool_call_id"]!.GetValue<string>() == id)!;
                Assert.Contains("validated", ack["content"]!.GetValue<string>(), StringComparison.Ordinal);
                Assert.Contains(ids[index], ack["content"]!.GetValue<string>(), StringComparison.Ordinal);
            }
        }
        var output = Encoding.UTF8.GetString(DocumentationScribeRunResultWriter.Write(request, result).AsSpan());
        Assert.DoesNotContain("opaque-submit-reasoning", output, StringComparison.Ordinal);
        Assert.DoesNotContain("exact assistant text", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("headers", false)]
    [InlineData("body", false)]
    [InlineData("connect", false)]
    [InlineData("headers", true)]
    [InlineData("body", true)]
    [InlineData("connect", true)]
    public async Task Incremental_slow_HTTP_turn_and_cancellation_preserve_the_original_shared_deadline(string phase, bool cancelled)
    {
        var request = IncrementalRequest(root => root["limits"]!["maximumElapsedMilliseconds"] = 1_000);
        var clock = new IncrementalDeadlineClock(); var calls = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var connecting = OpenAiCompatibleHttpModelExchange.CreateProductionHandler(5_000);
        connecting.ConnectCallback = async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("synthetic-connect-unreachable");
        };
        using var connector = new HttpMessageInvoker(connecting, disposeHandler: false);
        using var handler = new IncrementalProtocolHandler(async (message, token) =>
        {
            if (++calls == 1)
            {
                clock.Advance(400);
                return IncrementalHttp(SubmitBatch(request, 1, (request.ExecutableTargetIds[0], IncrementalProposal(request, request.ExecutableTargetIds[0])))
                    .TerminalSubmissions[0].TerminalUtf8Json.ToArray(), "call.first", "null", "first-continuation");
            }
            // Exercise the selected BCL connection path before a socket or DNS operation.
            if (phase == "connect") return await connector.SendAsync(message, token);
            if (phase == "headers")
            { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new IncrementalBlockedStream(entered)) };
            response.Content.Headers.ContentType = new("application/json");
            return response;
        });
        using var transport = IncrementalTransport(handler, loopback: phase == "connect"); using var caller = new CancellationTokenSource();
        var scope = DocumentationScribeExecutionScope.ForStandalone(request, clock);
        var running = DocumentationScribeExecution.RunAsync(request, Attempt(), Prompt(request), EmptyRegistry(), transport,
            new("provider.synthetic.v1", "model.synthetic.v1", "scribe-protocol.v1"), clock, caller.Token, scope: scope);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(600, scope.Allowance.RemainingMilliseconds); Assert.Equal(2, calls);
        clock.Advance(599); Assert.False(running.IsCompleted);
        if (cancelled) caller.Cancel(); else clock.Advance(1);
        var result = await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, calls); Assert.Equal(2, result.RunEnvelope.ProviderRequestCount); Assert.Single(result.Submissions);
        if (cancelled) Assert.IsType<DocumentationScribeCancelledTerminal>(result.Stop);
        else Assert.Equal(DocumentationScribeFailureCode.Timeout, Assert.IsType<DocumentationScribeFailureTerminal>(result.Stop).Code);
        Assert.Equal(2, DocumentationScribeValidation.GetCoverage(request, result).Count(item => item.Status == "remaining"));
    }

    private static OpenAiCompatibleHttpModelExchange IncrementalTransport(HttpMessageHandler handler, bool loopback = false) => new(
        new(new Uri(loopback ? "http://127.0.0.1:1/v1/chat/completions" : "https://example.test/v1/chat/completions"), "synthetic-model",
            new(OpenAiCompatibleThinkingMode.Enabled, null, OpenAiCompatibleToolChoice.Required,
                OpenAiCompatibleContinuationPolicy.RequiredForToolCalls, OpenAiCompatibleOutputTokenField.MaxTokens), networkEnabled: true), handler, disposeHandler: false);

    private static HttpResponseMessage IncrementalHttp(byte[] payload, string callId, string shape, string? reasoning)
    {
        var assistant = new JsonObject
        {
            ["role"] = "assistant",
            ["reasoning_content"] = reasoning,
            ["tool_calls"] = new JsonArray(new JsonObject
            {
                ["id"] = callId,
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = "cs_terminal", ["arguments"] = Encoding.UTF8.GetString(payload) }
            })
        };
        if (shape != "missing") assistant["content"] = shape == "null" ? null : shape == "empty" ? "" : "exact assistant text";
        var response = new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["message"] = assistant, ["finish_reason"] = "tool_calls" }),
            ["usage"] = new JsonObject
            {
                ["prompt_tokens"] = 0,
                ["completion_tokens"] = 0,
                ["prompt_cache_hit_tokens"] = 0,
                ["prompt_cache_miss_tokens"] = 0,
                ["completion_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 0 }
            }
        };
        return new(HttpStatusCode.OK) { Content = new StringContent(response.ToJsonString(), Encoding.UTF8, "application/json") };
    }

    private sealed class IncrementalProtocolHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }

    private sealed class IncrementalBlockedStream(TaskCompletionSource entered) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return 0; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class IncrementalDeadlineClock : TimeProvider
    {
        private long timestamp;
        private readonly List<IncrementalTimer> timers = [];
        public override long TimestampFrequency => 1_000;
        public override long GetTimestamp() => timestamp;
        internal void Advance(int milliseconds)
        { timestamp += milliseconds; foreach (var timer in timers.ToArray()) timer.Tick(); }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new IncrementalTimer(this, callback, state); timers.Add(timer); timer.Change(dueTime, period); return timer; }
        private sealed class IncrementalTimer(IncrementalDeadlineClock owner, TimerCallback callback, object? state) : ITimer
        {
            private long due = long.MaxValue;
            private bool disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            { if (disposed) return false; due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner.timestamp + (long)dueTime.TotalMilliseconds; return true; }
            internal void Tick() { if (!disposed && owner.timestamp >= due) { due = long.MaxValue; callback(state); } }
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
