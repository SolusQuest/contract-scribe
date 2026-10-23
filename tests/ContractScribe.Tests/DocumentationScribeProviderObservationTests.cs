using System.Net;
using System.Text;
using System.Text.Json;
using ContractScribe.Agent.Providers;
using ContractScribe.Agent.Runtime;
using ContractScribe.Cli;
using ContractScribe.Core;

namespace ContractScribe.Tests;

public sealed partial class CampaignStateContractTests
{
    [Theory]
    [InlineData("401", "model.failure.authentication", "model.failure-origin.http-status", null)]
    [InlineData("402", "model.failure.permanent-unavailable", "model.failure-origin.http-status", null)]
    [InlineData("501", "model.failure.permanent-unavailable", "model.failure-origin.http-status", null)]
    [InlineData("json", "model.failure.malformed-response", "model.failure-origin.response-codec", "codec.json.invalid")]
    [InlineData("usage", "model.failure.malformed-response", "model.failure-origin.response-codec", "codec.usage.invalid")]
    [InlineData("length", "model.failure.unsupported", "model.failure-origin.response-codec", "codec.finish-reason.length")]
    [InlineData("stop", "model.failure.unsupported", "model.failure-origin.response-codec", "codec.finish-reason.stop")]
    [InlineData("continuation", "model.failure.malformed-response", "model.failure-origin.response-codec", "codec.continuation.missing")]
    [InlineData("transport", "model.failure.permanent-unavailable", "model.failure-origin.transport", null)]
    [InlineData("observer-throws", null, null, null)]
    public async Task Real_Solus_physical_failures_reach_closed_CLI_without_changing_settlement(
        string scenario, string? code, string? origin, string? codec)
    {
        var trial = await SolusTrialAsync(2, invocationLimits: DocumentationScribeInvocationLimits.Create(
            maximumProviderRequests: 8, maximumToolCalls: 64, maximumCachedInputTokens: 32768,
            maximumUncachedInputTokens: 32768, maximumOutputTokens: 32768, maximumRequestOutputTokens: 4096,
            maximumElapsedMilliseconds: 600000, maximumRequestElapsedMilliseconds: 90000,
            maximumConnectMilliseconds: 10000, maximumToolCallsPerResponse: 8));
        var allowance = trial.Coordinator.Scope.Allowance;
        var usage = new InvocationUsage();
        usage.Bind(allowance);
        usage.ObserveState(trial.Store.Current);
        var handlerCount = 0;
        var handler = new SolusHttpHandler(async (message, token) =>
        {
            using var wire = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(token));
            Assert.Equal(4096, wire.RootElement.GetProperty("max_tokens").GetInt32());
            Assert.Equal("auto", wire.RootElement.GetProperty("tool_choice").GetString());
            Assert.Equal("high", wire.RootElement.GetProperty("reasoning_effort").GetString());
            if (handlerCount++ == 0) return ObservationToolHttp();
            return scenario switch
            {
                "401" => new(HttpStatusCode.Unauthorized),
                "402" or "observer-throws" => new(HttpStatusCode.PaymentRequired),
                "501" => new(HttpStatusCode.NotImplemented),
                "transport" => throw new HttpRequestException(HttpRequestError.SecureConnectionError, "private-exception-marker"),
                "json" => ObservationJson("{private-body-marker"),
                "usage" => ObservationToolHttp(invalidUsage: true),
                "continuation" => ObservationToolHttp(missingContinuation: true),
                "length" or "stop" => ObservationJson("{\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"private-body-marker\"},\"finish_reason\":\"" + scenario + "\"}]}"),
                _ => throw new InvalidOperationException(),
            };
        });
        using var transport = ObservationTransport(handler, row =>
        {
            if (scenario == "observer-throws") throw new InvalidOperationException("private-observer-marker");
            usage.ObserveProvider(allowance, row);
        });
        var result = await trial.Session(transport, ObservationRegistry(trial.Request.ToolPolicyId)).RunAsync();
        var failure = Assert.IsType<DocumentationScribeFailureTerminal>(result.Stop);
        Assert.Equal(DocumentationScribeFailureCode.Provider, failure.Code);
        Assert.Equal(DocumentationScribeProviderFinalDisposition.Terminal, failure.ProviderFinalDisposition);
        Assert.Equal(2, handler.Count);
        Assert.Equal(2, trial.Store.Dispatches);
        Assert.Equal(2, trial.Store.Settlements);
        Assert.Equal(0, allowance.Observe().RetryDispatches);
        Assert.Equal(0, allowance.Observe().UnsettledDispatches);
        var history = trial.Store.Current.State.DispatchObservations;
        Assert.Equal(DocumentationScribeDispatchDisposition.Success, history[0].Disposition);
        Assert.Equal(10, history[0].Usage!.InputTokens);
        Assert.Equal(5, history[0].Usage!.OutputTokens);
        Assert.Equal(DocumentationScribeDispatchDisposition.TerminalFailure, history[1].Disposition);
        Assert.Null(history[1].Usage);
        Assert.True(history[1].Charges!.InputTokens.ConservativeUnobserved > 0);
        Assert.True(history[1].Charges!.OutputTokens.ConservativeUnobserved > 0);
        Assert.Empty(result.Submissions);
        await trial.CompleteAsync(result);
        Assert.Null(trial.Store.Current.State.ActiveReservation);
        // Accepted Core readback reparses reference containers. Compare every canonical
        // settlement commitment, which binds the full row, rather than object identity.
        Assert.Equal(history.Select(CampaignStateFactory.DispatchSettlementCommitment),
            trial.Store.Current.State.DispatchObservations.Select(CampaignStateFactory.DispatchSettlementCommitment));
        using var envelope = ObservationEnvelope(usage);
        var diagnostics = envelope.RootElement.GetProperty("invocationUsage").GetProperty("providerDiagnostics");
        var rows = diagnostics.GetProperty("observations").EnumerateArray().ToArray();
        Assert.Equal(scenario == "observer-throws" ? 2 : 0, diagnostics.GetProperty("missingDispatches").GetInt32());
        if (scenario != "observer-throws")
        {
            Assert.Equal(2, rows.Length);
            Assert.Equal("codec.accepted-tool", rows[0].GetProperty("codecDisposition").GetString());
            Assert.Equal(2, rows[1].GetProperty("physicalDispatchOrdinal").GetInt32());
            Assert.Equal(trial.Attempt.Value, rows[1].GetProperty("attemptId").GetString());
            Assert.Equal(1, rows[1].GetProperty("attemptNumber").GetInt32());
            Assert.Equal(2, rows[1].GetProperty("providerRequestNumber").GetInt32());
            Assert.Equal(code, rows[1].GetProperty("failureCode").GetString());
            Assert.Equal(origin, rows[1].GetProperty("failureOrigin").GetString());
            Assert.Equal(codec, rows[1].GetProperty("codecDisposition").GetString());
            Assert.Equal(int.TryParse(scenario, out var status) ? status : (int?)null,
                rows[1].GetProperty("httpStatusCode").ValueKind == JsonValueKind.Null ? null
                    : rows[1].GetProperty("httpStatusCode").GetInt32());
        }
        foreach (var marker in new[] { "private-body-marker", "private-exception-marker", "private-observer-marker", "synthetic-continuation" })
            Assert.DoesNotContain(marker, envelope.RootElement.GetRawText(), StringComparison.Ordinal);

    }

    [Fact]
    public async Task Late_HTTP_completion_cannot_replace_the_closed_cancellation_diagnostics()
    {
        var trial = await SolusTrialAsync();
        var usage = new InvocationUsage();
        usage.Bind(trial.Coordinator.Scope.Allowance);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new SolusHttpHandler((_, _) => { entered.TrySetResult(); return released.Task; });
        var callbacks = 0;
        using var transport = ObservationTransport(handler, row => { callbacks++; usage.ObserveProvider(trial.Coordinator.Scope.Allowance, row); });
        using var caller = new CancellationTokenSource();
        var running = trial.Session(transport).RunAsync(caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DocumentationScribeTerminalKind.Cancelled, result.Stop.Kind);
        Assert.Equal(DocumentationScribeDispatchDisposition.Interrupted, Assert.Single(trial.Store.Current.State.DispatchObservations).Disposition);
        using var before = ObservationEnvelope(usage);
        var saved = before.RootElement.GetProperty("invocationUsage").GetProperty("providerDiagnostics").GetRawText();
        released.TrySetResult(new(HttpStatusCode.OK) { Content = new LateObservationContent(disposed) });
        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var after = ObservationEnvelope(usage);
        Assert.Equal(saved, after.RootElement.GetProperty("invocationUsage").GetProperty("providerDiagnostics").GetRawText());
        Assert.Equal(1, callbacks);
        Assert.Equal(1, handler.Count);
        Assert.Equal(1, trial.Store.Settlements);
    }

    private static JsonDocument ObservationEnvelope(InvocationUsage usage) => JsonDocument.Parse(
        GitHubProposalPresentation.WithUsage(GitHubProposalPresentation.Campaign(CliBuildIdentity.Current,
            new("execution", CampaignOperation.Resume, "campaign.provider-terminal", null)), usage).StandardOutput);

    private static OpenAiCompatibleHttpModelExchange ObservationTransport(HttpMessageHandler handler,
        Action<OpenAiCompatibleProviderObservation> observer) => new(new(new Uri("https://example.test/chat/completions"),
            "synthetic-model", new(OpenAiCompatibleThinkingMode.Enabled, OpenAiCompatibleReasoningEffort.High,
                OpenAiCompatibleToolChoice.Auto, OpenAiCompatibleContinuationPolicy.RequiredForToolCalls,
                OpenAiCompatibleOutputTokenField.MaxTokens), networkEnabled: true), handler, observer: observer);

    private static HttpResponseMessage ObservationJson(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage ObservationToolHttp(bool invalidUsage = false, bool missingContinuation = false) => ObservationJson(
        "{\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\","
        + (missingContinuation ? "" : "\"reasoning_content\":\"synthetic-continuation\",")
        + "\"tool_calls\":[{\"id\":\"call." + (invalidUsage || missingContinuation ? "second" : "first") + "\",\"type\":\"function\",\"function\":{\"name\":\"cs_tool_000\",\"arguments\":\"{}\"}}]},\"finish_reason\":\"tool_calls\"}],\"usage\":"
        + (invalidUsage ? "[]" : "{\"prompt_tokens\":10,\"completion_tokens\":5,\"prompt_cache_hit_tokens\":4,\"prompt_cache_miss_tokens\":6}") + "}");

    private static DocumentationScribeToolRegistry ObservationRegistry(string policy) => new DocumentationScribeToolRegistryBuilder(policy)
        .Add(new ObservationDescriptor(), new ObservationPort(), new ObservationCodec(), "Read bounded synthetic evidence.", Encoding.UTF8.GetBytes("{\"type\":\"object\"}"), 8).Build();
    private sealed record ObservationRequest : IDocumentationScribeToolRequest<ObservationResult>;
    private sealed record ObservationResult : IDocumentationScribeToolResult { public DocumentationScribeToolOutcome Outcome => DocumentationScribeToolOutcome.Complete; }
    private sealed class ObservationDescriptor : IDocumentationScribeToolDescriptor<ObservationRequest, ObservationResult> { public string OperationId => "tool.synthetic.read"; }
    private sealed class ObservationPort : IDocumentationScribeToolPort<ObservationRequest, ObservationResult>
    { public ValueTask<ObservationResult> InvokeAsync(ObservationRequest request, CancellationToken token) => ValueTask.FromResult(new ObservationResult()); }
    private sealed class ObservationCodec : IDocumentationScribeToolCodec<ObservationRequest, ObservationResult>
    {
        public DocumentationScribeToolDecodeResult<ObservationRequest> DecodeArguments(ReadOnlyMemory<byte> bytes) => DocumentationScribeToolDecodeResult<ObservationRequest>.Accepted(new());
        public DocumentationScribeToolEncodeResult EncodeResult(ObservationRequest request, ObservationResult result) => DocumentationScribeToolEncodeResult.Accepted(new(Encoding.UTF8.GetBytes("{}"), []));
    }
    private sealed class LateObservationContent(TaskCompletionSource disposed) : StringContent("{private-late-body-marker", Encoding.UTF8, "application/json")
    { protected override void Dispose(bool disposing) { base.Dispose(disposing); disposed.TrySetResult(); } }
}

public sealed partial class DocumentationScribeProviderTransportTests
{
    [Fact]
    public async Task Report_freeze_cannot_mix_one_allowance_measurement_with_another_attempt_callback()
    {
        using var clock = new SnapshotBarrierClock();
        var first = new DocumentationScribeInvocationAllowance(DocumentationScribeInvocationLimits.Create(), clock);
        var next = new DocumentationScribeInvocationAllowance(DocumentationScribeInvocationLimits.Create());
        foreach (var pair in new[] { (first, 10), (next, 20) })
        {
            Assert.True(pair.Item1.TryReserveProvider(out var permit));
            Assert.True(permit!.TryBeginDispatch());
            Assert.True(pair.Item1.SettleProvider(permit, new(pair.Item2, 0, pair.Item2, 0, 0)));
        }
        var usage = new InvocationUsage();
        usage.Bind(first);
        var firstId = "scribe-attempt." + new string('a', 32);
        var nextId = "scribe-attempt." + new string('b', 32);
        usage.ObserveProvider(first, new(1, Request([]), firstId, null, "codec.accepted-tool"));
        clock.Arm();
        var snapshot = Task.Run(() => GitHubProposalPresentation.WithUsage(
            GitHubProposalPresentation.Local(CliBuildIdentity.Current, CampaignOperation.Resume, "timeout"), usage).StandardOutput);
        await clock.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var rebindingEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rebind = Task.Run(() =>
        {
            rebindingEntered.TrySetResult();
            usage.Bind(next);
            usage.ObserveProvider(next, new(1, Request([]), nextId, null, "codec.accepted-tool"));
        });
        try
        {
            await rebindingEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAny(rebind, Task.Delay(100));
            Assert.False(rebind.IsCompleted);
        }
        finally
        {
            clock.Release();
            await Task.WhenAll(snapshot, rebind).WaitAsync(TimeSpan.FromSeconds(5));
        }
        using var envelope = JsonDocument.Parse(await snapshot);
        var measured = envelope.RootElement.GetProperty("invocationUsage");
        Assert.Equal(10, measured.GetProperty("tokens").GetProperty("input").GetProperty("observed").GetInt64());
        Assert.Equal(firstId, Assert.Single(measured.GetProperty("providerDiagnostics").GetProperty("observations").EnumerateArray())
            .GetProperty("attemptId").GetString());
    }

    private sealed class SnapshotBarrierClock : TimeProvider, IDisposable
    {
        private readonly ManualResetEventSlim released = new(true);
        private int armed;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override long TimestampFrequency => global::System.Diagnostics.Stopwatch.Frequency;
        internal void Arm() { released.Reset(); Interlocked.Exchange(ref armed, 1); }
        internal void Release() => released.Set();
        public override long GetTimestamp()
        {
            if (Interlocked.Exchange(ref armed, 0) == 1)
            {
                Entered.TrySetResult();
                if (!released.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            }
            return global::System.Diagnostics.Stopwatch.GetTimestamp();
        }
        public void Dispose() => released.Dispose();
    }

    [Fact]
    public void Provider_diagnostics_retain_bounded_latest_ordinals_and_reject_foreign_or_frozen_callbacks()
    {
        var allowance = new DocumentationScribeInvocationAllowance(DocumentationScribeInvocationLimits.Create(maximumProviderRequests: 20));
        var foreign = new DocumentationScribeInvocationAllowance(DocumentationScribeInvocationLimits.Create(maximumProviderRequests: 20));
        var usage = new InvocationUsage();
        usage.Bind(allowance);
        for (var ordinal = 1; ordinal <= 20; ordinal++)
        {
            Assert.True(allowance.TryReserveProvider(out var permit));
            Assert.True(permit!.TryBeginDispatch());
            Assert.Equal(ordinal, permit.PhysicalDispatchOrdinal);
            Assert.True(allowance.SettleProvider(permit, new(0, 0, 0, 0, 0)));
            var row = new OpenAiCompatibleProviderObservation(ordinal, Request([]), null, null, "codec.accepted-tool");
            usage.ObserveProvider(foreign, row);
            usage.ObserveProvider(allowance, row);
        }
        using var first = JsonDocument.Parse(GitHubProposalPresentation.WithUsage(
            GitHubProposalPresentation.Local(CliBuildIdentity.Current, CampaignOperation.Resume, "timeout"), usage).StandardOutput);
        var saved = first.RootElement.GetProperty("invocationUsage").GetProperty("providerDiagnostics");
        Assert.Equal(4, saved.GetProperty("missingDispatches").GetInt32());
        Assert.Equal(Enumerable.Range(5, 16), saved.GetProperty("observations").EnumerateArray()
            .Select(row => row.GetProperty("physicalDispatchOrdinal").GetInt32()));
        usage.ObserveProvider(allowance, new(20, Request([]), null,
            new(DocumentationScribeModelFailureCode.Authentication), null));
        using var second = JsonDocument.Parse(GitHubProposalPresentation.WithUsage(
            GitHubProposalPresentation.Local(CliBuildIdentity.Current, CampaignOperation.Resume, "timeout"), usage).StandardOutput);
        Assert.Equal(saved.GetRawText(), second.RootElement.GetProperty("invocationUsage").GetProperty("providerDiagnostics").GetRawText());
        usage.Bind(foreign);
        usage.ObserveProvider(allowance, new(1, Request([]), null, null, "codec.accepted-tool"));
        using var rebound = JsonDocument.Parse(GitHubProposalPresentation.WithUsage(
            GitHubProposalPresentation.Local(CliBuildIdentity.Current, CampaignOperation.Resume, "timeout"), usage).StandardOutput);
        Assert.Empty(rebound.RootElement.GetProperty("invocationUsage").GetProperty("providerDiagnostics").GetProperty("observations").EnumerateArray());
    }
}
