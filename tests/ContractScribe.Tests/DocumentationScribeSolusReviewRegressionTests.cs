using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Agent.Runtime;
using ContractScribe.Cli;
using ContractScribe.Core;
using SolusAgent.Runtime.Api.Providers;

namespace ContractScribe.Tests;

public sealed partial class CampaignStateContractTests
{
    [Theory]
    [InlineData(2, 8, 8, 0, 1, false, true)]
    [InlineData(2, 8, 8, 0, 2, false, true)]
    [InlineData(2, 8, 8, 0, 3, false, false)]
    [InlineData(2, 8, 8, 0, 3, true, false)]
    [InlineData(4, 3, 8, 1, 3, false, false)]
    [InlineData(4, 8, 3, 1, 3, false, false)]
    [InlineData(2, 4, 8, 1, 2, false, true)]
    [InlineData(2, 8, 4, 1, 2, false, true)]
    public async Task Solus_runtime_checks_the_whole_batch_against_configured_width_and_both_remaining_budgets(
        int width, int invocationCalls, int targetCalls, int precedingCalls, int batchCalls, bool invalidTail, bool accepted)
    {
        var trial = await SolusTrialAsync(invocationLimits: DocumentationScribeInvocationLimits.Create(
            maximumProviderRequests: 128, maximumToolCalls: invocationCalls,
            maximumCachedInputTokens: 32768, maximumUncachedInputTokens: 32768,
            maximumOutputTokens: 8192, maximumElapsedMilliseconds: 120000,
            maximumRequestOutputTokens: 8192, maximumRequestElapsedMilliseconds: 120000,
            maximumToolCallsPerResponse: width), targetToolCalls: targetCalls);
        var port = new ReviewBoundaryPort();
        var steps = ImmutableArray.CreateBuilder<ScriptedDocumentationScribeStep>();
        if (precedingCalls > 0) steps.Add(Batch(precedingCalls, "prior"));
        steps.Add(Batch(batchCalls, "current"));
        steps.Add(ScriptedDocumentationScribeStep.Return(new([], [new(C3Submission(trial.Scenario, trial.Request))], usage: new(0, 0, 0, 0))));
        steps.Add(C3FinishStep(trial.Request));
        var exchange = new ScriptedDocumentationScribeModelExchange(steps.ToImmutable());
        var session = trial.Session(exchange, ReviewBoundaryRegistry(trial.Request, port));
        var result = await session.RunAsync();
        var reads = precedingCalls + (accepted ? batchCalls : 0);
        Assert.Equal(reads, port.Calls);
        var finishSent = accepted && reads + 1 < Math.Min(invocationCalls, targetCalls);
        Assert.Equal(reads + (accepted ? 1 : 0) + (finishSent ? 1 : 0), result.RunEnvelope.ToolCallCount);
        Assert.Equal(result.RunEnvelope.ToolCallCount, trial.Coordinator.Scope.Allowance.ToolCallCount);
        Assert.Equal(reads, trial.Store.CompletedHostReadbacks.Count(phase => phase == CampaignProviderHostPhase.SemanticTool));
        Assert.Equal(reads, trial.Store.AdmittedHostPhases.Count(phase => phase == CampaignProviderHostPhase.SemanticTool));
        Assert.Equal(reads, session.State.CompletedToolExchanges.Count(item => item.OperationId != DocumentationScribeBoundary.TerminalOperationId));
        Assert.Equal((precedingCalls > 0 ? accepted ? 3 : 2 : accepted ? 2 : 1) + (finishSent ? 1 : 0), exchange.Requests.Length);
        Assert.Equal(exchange.Requests.Length, trial.Store.Dispatches);
        Assert.Equal(exchange.Requests.Length, trial.Store.Settlements);
        Assert.Equal(exchange.Requests.Length, result.RunEnvelope.ProviderRequestCount);
        Assert.Equal((precedingCalls > 0 ? 2 : 1) * 3, result.RunEnvelope.Usage?.InputTokens);
        Assert.Equal((precedingCalls > 0 ? 2 : 1) * 2, result.RunEnvelope.Usage?.OutputTokens);
        if (accepted) Assert.Equal(DocumentationScribeTerminalKind.Proposal, Assert.Single(result.Submissions).Result.Kind);
        else
            Assert.Equal(DocumentationScribeFailureCode.Budget, Assert.IsType<DocumentationScribeFailureTerminal>(result.Stop).Code);
        await trial.CompleteAsync(result);
        Assert.Equal(exchange.Requests.Length, trial.Store.Current.State.LineageCharges.ProviderRequests.Observed);
        Assert.Equal((precedingCalls > 0 ? 2 : 1) * 3, trial.Store.Current.State.LineageCharges.InputTokens.Observed);

        ScriptedDocumentationScribeStep Batch(int count, string prefix) => ScriptedDocumentationScribeStep.Return(new(
            Enumerable.Range(0, count).Select(index => new DocumentationScribeModelToolCall(index, prefix + index,
                invalidTail && prefix == "current" && index == count - 1 ? "unregistered" : "get-target-evidence", "{}"u8.ToArray())).ToImmutableArray(),
            [], usage: new(3, 2, 1, 2)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Solus_runtime_insufficient_closure_headroom_denies_semantic_without_any_effect(bool invocationCut, bool callerCancelled)
    {
        var clock = new SolusDeadlineClock();
        var trial = await SolusTrialAsync(clock: clock, targetMilliseconds: 30000,
            invocationMilliseconds: invocationCut ? 9000 : 20000);
        using var caller = new CancellationTokenSource();
        trial.Store.DelayedReadbackPhase = CampaignProviderHostPhase.SemanticTool;
        trial.Store.DelayedReadback = () => throw new InvalidOperationException("Denied admission must not write a semantic checkpoint.");
        if (callerCancelled) caller.Cancel();
        var port = new ReviewBoundaryPort();
        var exchange = ReviewBoundaryToolExchange();
        var result = await trial.Session(exchange, ReviewBoundaryRegistry(trial.Request, port))
            .RunAsync(caller.Token).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, port.Calls);
        Assert.Equal(0, result.RunEnvelope.ToolCallCount);
        Assert.Equal(callerCancelled ? 0 : 1, exchange.Requests.Length);
        Assert.DoesNotContain(CampaignProviderHostPhase.SemanticTool, trial.Store.AdmittedHostPhases);
        Assert.DoesNotContain(CampaignProviderHostPhase.SemanticTool, trial.Store.CompletedHostReadbacks);
        if (callerCancelled) Assert.Equal(DocumentationScribeTerminalKind.Cancelled, result.Stop.Kind);
        else
        {
            Assert.True(trial.Coordinator.Scope.SemanticHeadroomReached);
            Assert.False(trial.Coordinator.Scope.Allowance.HasCheckedStop);
            Assert.Equal(DocumentationScribeFailureCode.Budget, Assert.IsType<DocumentationScribeFailureTerminal>(result.Stop).Code);
        }
        await trial.CompleteAsync(result);
    }

    [Theory]
    [InlineData(30000, false)]
    [InlineData(30001, false)]
    [InlineData(30000, true)]
    [InlineData(30001, true)]
    public async Task Semantic_authorization_cutoff_observes_only_for_completion_and_never_calls_the_port(int elapsed, bool callerCancelled)
    {
        var clock = new SolusDeadlineClock();
        var trial = await SolusTrialAsync(clock: clock);
        using var caller = new CancellationTokenSource();
        trial.Store.DelayedReadbackPhase = CampaignProviderHostPhase.SemanticTool;
        trial.Store.DelayedReadback = () => { clock.Advance(elapsed); if (callerCancelled) caller.Cancel(); };
        var port = new ReviewBoundaryPort();
        var exchange = ReviewBoundaryToolExchange();
        var result = await trial.Session(exchange, ReviewBoundaryRegistry(trial.Request, port))
            .RunAsync(caller.Token).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, port.Calls);
        Assert.Equal(0, result.RunEnvelope.ToolCallCount);
        Assert.Single(exchange.Requests);
        Assert.True(trial.Authority.ProgressStopped);
        Assert.True(trial.Coordinator.Scope.SemanticAuthorizationClosed);
        Assert.False(trial.Coordinator.Scope.Failed);
        Assert.False(trial.Coordinator.Conflict);
        Assert.DoesNotContain(CampaignProviderHostPhase.SemanticTool, trial.Store.CompletedHostReadbacks);
        if (callerCancelled) Assert.Equal(DocumentationScribeTerminalKind.Cancelled, result.Stop.Kind);
        else Assert.Equal(DocumentationScribeFailureCode.Timeout, Assert.IsType<DocumentationScribeFailureTerminal>(result.Stop).Code);
        await trial.CompleteAsync(result);
        Assert.Equal(elapsed, trial.Store.Current.State.LineageCharges.ActiveElapsedMilliseconds.Observed);
        Assert.Equal(0, trial.Store.Current.State.LineageCharges.ActiveElapsedMilliseconds.ConservativeUnobserved);
        Assert.Null(trial.Store.Current.State.ActiveReservation);
    }

    [Theory]
    [InlineData(CampaignProviderHostPhase.SemanticTool, "get-target-evidence")]
    [InlineData(CampaignProviderHostPhase.RepositoryTool, "read-excerpt")]
    [InlineData(CampaignProviderHostPhase.RegisteredTool, "synthetic-read")]
    public async Task Cancellation_during_tool_readback_never_invokes_the_port_before_the_time_budget_expires(
        CampaignProviderHostPhase phase, string operationId)
    {
        var trial = await SolusTrialAsync();
        using var execution = new CancellationTokenSource();
        trial.Store.DelayedReadbackPhase = phase;
        trial.Store.DelayedReadback = execution.Cancel;
        var port = new ReviewBoundaryPort();
        var result = await trial.Session(ReviewBoundaryToolExchange(operationId), ReviewBoundaryRegistry(trial.Request, port, operationId))
            .RunAsync(execution.Token).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, port.Calls);
        Assert.Equal(0, result.RunEnvelope.ToolCallCount);
        Assert.Equal(DocumentationScribeTerminalKind.Cancelled, result.Stop.Kind);
        if (phase == CampaignProviderHostPhase.SemanticTool)
        {
            Assert.True(trial.Authority.ProgressStopped);
            Assert.DoesNotContain(phase, trial.Store.CompletedHostReadbacks);
        }
        else
        {
            Assert.Contains(phase, trial.Store.CompletedHostReadbacks);
            Assert.NotEqual(phase, Assert.IsType<CampaignProviderReservation>(trial.Store.Current.State.ActiveReservation).HostPhase);
        }
        await trial.CompleteAsync(result);
        Assert.Null(trial.Store.Current.State.ActiveReservation);
    }

    [Fact]
    public async Task Execution_cancellation_reaches_an_active_semantic_port_and_preserves_its_single_debit()
    {
        var trial = await SolusTrialAsync();
        using var execution = new CancellationTokenSource();
        var port = new ActiveCancellationBoundaryPort();
        var running = trial.Session(ReviewBoundaryToolExchange(), ReviewBoundaryRegistry(trial.Request, port))
            .RunAsync(execution.Token);
        await port.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        execution.Cancel();
        await port.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var result = await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, result.RunEnvelope.ToolCallCount);
        Assert.Equal(1, trial.Coordinator.Scope.Allowance.ToolCallCount);
        Assert.Equal(DocumentationScribeTerminalKind.Cancelled, result.Stop.Kind);
        Assert.Contains(CampaignProviderHostPhase.SemanticTool, trial.Store.CompletedHostReadbacks);
        await trial.CompleteAsync(result);
    }

    [Fact]
    public async Task Cancellation_during_terminal_readback_never_debits_or_accepts_the_submission()
    {
        var trial = await SolusTrialAsync();
        using var execution = new CancellationTokenSource();
        trial.Store.DelayedReadbackPhase = CampaignProviderHostPhase.TerminalSubmission;
        trial.Store.DelayedReadback = execution.Cancel;
        var exchange = new ScriptedDocumentationScribeModelExchange([
            ScriptedDocumentationScribeStep.Return(new([], [new(C3Submission(trial.Scenario, trial.Request))], usage: new(0, 0, 0, 0)))]);
        var result = await trial.Session(exchange).RunAsync(execution.Token).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, result.RunEnvelope.ToolCallCount);
        Assert.Empty(result.Submissions);
        Assert.Equal(DocumentationScribeTerminalKind.Cancelled, result.Stop.Kind);
        Assert.Contains(CampaignProviderHostPhase.TerminalSubmission, trial.Store.CompletedHostReadbacks);
        await trial.CompleteAsync(result);
    }

    [Fact]
    public async Task Cancellation_during_dispatch_readback_never_dispatches_the_physical_request()
    {
        var trial = await SolusTrialAsync();
        using var execution = new CancellationTokenSource();
        trial.Store.DelayedReadbackPhase = null;
        trial.Store.DelayedReadback = execution.Cancel;
        var exchange = ReviewBoundaryToolExchange();
        var result = await trial.Session(exchange).RunAsync(execution.Token).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(exchange.Requests);
        Assert.Equal(0, result.RunEnvelope.ProviderRequestCount);
        Assert.Equal(0, trial.Coordinator.Scope.PhysicalProviderRequestCount);
        Assert.Equal(DocumentationScribeTerminalKind.Cancelled, result.Stop.Kind);
        Assert.Equal(1, trial.Store.Settlements);
        await trial.CompleteAsync(result);
    }

    [Fact]
    public async Task Cancellation_during_retry_readback_preserves_the_pending_wait_without_another_send()
    {
        var clock = new SolusDeadlineClock();
        var trial = await SolusTrialAsync(clock: clock);
        using var execution = new CancellationTokenSource();
        trial.Store.DelayedReadbackPhase = CampaignProviderHostPhase.RetryWait;
        trial.Store.DelayedReadback = execution.Cancel;
        var exchange = new ScriptedDocumentationScribeModelExchange([
            ScriptedDocumentationScribeStep.Return(new([], [], new(DocumentationScribeModelFailureCode.TransientUnavailable,
                retryAfterMilliseconds: 1000), usage: new(0, 0, 0, 0))), C3FinishStep(trial.Request)]);
        var result = await trial.Session(exchange).RunAsync(execution.Token).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(exchange.Requests);
        Assert.Equal(DocumentationScribeTerminalKind.Cancelled, result.Stop.Kind);
        Assert.Equal(1000, trial.Coordinator.Scope.PendingRetryAfterMilliseconds);
        Assert.Equal(DocumentationScribeInvocationStopReason.None, trial.Coordinator.Scope.Allowance.StopReason);
        await trial.CompleteAsync(result);
    }

    [Fact]
    public async Task Solus_runtime_expired_terminal_admission_closes_the_Host_without_a_terminal_debit()
    {
        var clock = new SolusDeadlineClock();
        var trial = await SolusTrialAsync(clock: clock, targetMilliseconds: 5000, invocationMilliseconds: 5000, hostMilliseconds: 500);
        trial.Store.DelayedReadbackPhase = CampaignProviderHostPhase.TerminalSubmission;
        trial.Store.DelayedReadback = () => clock.Advance(600);
        var exchange = new ScriptedDocumentationScribeModelExchange([
            ScriptedDocumentationScribeStep.Return(new([], [new(C3Submission(trial.Scenario, trial.Request))], usage: new(0, 0, 0, 0)))]);
        var result = await trial.Session(exchange).RunAsync();
        Assert.Equal(0, result.RunEnvelope.ToolCallCount);
        Assert.Single(exchange.Requests);
        Assert.Contains(CampaignProviderHostPhase.TerminalSubmission, trial.Store.CompletedHostReadbacks);
        Assert.NotEqual(CampaignProviderHostPhase.TerminalSubmission,
            Assert.IsType<CampaignProviderReservation>(trial.Store.Current.State.ActiveReservation).HostPhase);
        Assert.Equal(DocumentationScribeFailureCode.Timeout, Assert.IsType<DocumentationScribeFailureTerminal>(result.Stop).Code);
        await trial.CompleteAsync(result);
    }

    [Fact]
    public async Task Solus_runtime_unexpected_tool_fault_is_drained_as_Internal_and_closes_the_campaign_as_InternalFailure()
    {
        const string marker = "synthetic-private-tool-fault";
        var trial = await SolusTrialAsync();
        var port = new ReviewBoundaryPort(new IOException(marker));
        var exchange = ReviewBoundaryToolExchange();
        var result = await trial.Session(exchange, ReviewBoundaryRegistry(trial.Request, port)).RunAsync();
        Assert.Equal(1, port.Calls);
        Assert.Single(exchange.Requests);
        Assert.Equal(CampaignProviderHostPhase.Continuation,
            Assert.IsType<CampaignProviderReservation>(trial.Store.Current.State.ActiveReservation).HostPhase);
        Assert.Equal(DocumentationScribeFailureCode.Internal, Assert.IsType<DocumentationScribeFailureTerminal>(result.Stop).Code);
        Assert.DoesNotContain(marker, JsonSerializer.Serialize(result.RunEnvelope.Diagnostics), StringComparison.Ordinal);
        await trial.CompleteAsync(result);
        Assert.Equal(CampaignWorkOutcomeCode.InternalFailure,
            trial.Store.Current.State.WorkItems.Single(item => item.WorkItemKey == trial.Scenario.Plan.WorkItems[0].WorkItemKey).ClosedOutcome!.Code);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("malformed")]
    [InlineData("null")]
    [InlineData("foreign")]
    public async Task Solus_runtime_classified_prompt_is_reconstructed_and_bound_before_dispatch(string scenario)
    {
        var trial = await SolusTrialAsync();
        var exchange = new ScriptedDocumentationScribeModelExchange([
            ScriptedDocumentationScribeStep.Return(new([], [new(C3Submission(trial.Scenario, trial.Request))], usage: new(0, 0, 0, 0))), C3FinishStep(trial.Request)]);
        var session = DocumentationScribeExecution.Prepare(trial.Request, trial.Attempt, trial.Prompt,
            new DocumentationScribeToolRegistryBuilder(trial.Request.ToolPolicyId).Build(), exchange,
            new("provider.synthetic.v1", "model.synthetic.v1", "scribe-protocol.v1"), trial.Coordinator.Scope.Allowance.Clock,
            scope: trial.Coordinator.Scope);
        if (scenario == "valid")
        {
            var result = await DocumentationScribeExecution.RunAsync(session);
            Assert.Equal(DocumentationScribeTerminalKind.Proposal, Assert.Single(result.Submissions).Result.Kind);
            Assert.Equal(2, exchange.Requests.Length);
            await trial.CompleteAsync(result);
            return;
        }
        var content = scenario == "malformed" ? "{not-json" : "null";
        if (scenario == "foreign")
        {
            var node = JsonNode.Parse(JsonSerializer.Serialize(trial.Prompt))!;
            node["Context"]![0]!["ContextReferenceId"] = "context.foreign.v1";
            content = node.ToJsonString();
        }
        // Force malformed classified input at the real public SDK provider boundary;
        // the normal pinned SDK roundtrip is exercised above and by production tests.
        var provider = session.Configuration!.Provider;
        var request = new ProviderRequest(provider.Scope, new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
            [ProviderInput.Instruction("Execute the Host-bound Documentation Scribe protocol using only registered read tools."),
                ProviderInput.Data(content)], bounds: DocumentationScribeSession.Bounds());
        var rejected = await provider.ExchangeAsync(request);
        Assert.Empty(exchange.Requests);
        Assert.Equal(0, trial.Store.Dispatches);
        Assert.Equal(ProviderOutcome.Rejected, rejected.Outcome);
        var resultFailure = Assert.IsType<DocumentationScribeFailureTerminal>(session.Result!.Stop);
        Assert.Equal(DocumentationScribeFailureCode.Validation, resultFailure.Code);
        await trial.CompleteAsync(session.Result);
    }

    private static ScriptedDocumentationScribeModelExchange ReviewBoundaryToolExchange(string operationId = "get-target-evidence") => new([
        ScriptedDocumentationScribeStep.Return(new([new(0, "call.read", operationId, "{}"u8.ToArray())], [], usage: new(0, 0, 0, 0)))]);
    private static DocumentationScribeToolRegistry ReviewBoundaryRegistry(DocumentationScribeRequest request,
        IDocumentationScribeToolPort<ReviewBoundaryRequest, ReviewBoundaryResult> port, string operationId = "get-target-evidence") =>
        new DocumentationScribeToolRegistryBuilder(request.ToolPolicyId).Add(new ReviewBoundaryDescriptor(operationId), port,
            new ReviewBoundaryCodec(), "Read synthetic target evidence.", "{\"type\":\"object\",\"additionalProperties\":false}"u8.ToArray(), 16).Build();
    private sealed record ReviewBoundaryRequest : IDocumentationScribeToolRequest<ReviewBoundaryResult>;
    private sealed record ReviewBoundaryResult(DocumentationScribeToolOutcome Outcome) : IDocumentationScribeToolResult;
    private sealed class ReviewBoundaryDescriptor(string operationId) : IDocumentationScribeToolDescriptor<ReviewBoundaryRequest, ReviewBoundaryResult>
    { public string OperationId => operationId; }
    private sealed class ReviewBoundaryCodec : IDocumentationScribeToolCodec<ReviewBoundaryRequest, ReviewBoundaryResult>
    {
        public DocumentationScribeToolDecodeResult<ReviewBoundaryRequest> DecodeArguments(ReadOnlyMemory<byte> argumentsUtf8Json) =>
            DocumentationScribeToolDecodeResult<ReviewBoundaryRequest>.Accepted(new());
        public DocumentationScribeToolEncodeResult EncodeResult(ReviewBoundaryRequest request, ReviewBoundaryResult result) =>
            DocumentationScribeToolEncodeResult.Accepted(new("{}"u8.ToArray(), []));
    }
    private sealed class ActiveCancellationBoundaryPort : IDocumentationScribeToolPort<ReviewBoundaryRequest, ReviewBoundaryResult>
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<ReviewBoundaryResult> InvokeAsync(ReviewBoundaryRequest request, CancellationToken token)
        {
            Entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            { Cancelled.TrySetResult(); throw; }
            throw new InvalidOperationException("The infinite operation must end through cancellation.");
        }
    }
    private sealed class ReviewBoundaryPort(Exception? failure = null) : IDocumentationScribeToolPort<ReviewBoundaryRequest, ReviewBoundaryResult>, IDocumentationScribeToolResultReleaseValidator
    {
        public bool ValidateResultRelease(CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return true; }
        internal int Calls { get; private set; }
        public ValueTask<ReviewBoundaryResult> InvokeAsync(ReviewBoundaryRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return failure is null ? ValueTask.FromResult(new ReviewBoundaryResult(DocumentationScribeToolOutcome.Complete))
                : ValueTask.FromException<ReviewBoundaryResult>(failure);
        }
    }
}
