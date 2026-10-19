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
    public async Task Solus_runtime_expired_tool_admission_closes_the_Host_without_invoking_the_port(bool invocationCut, bool callerCancelled)
    {
        var clock = new SolusDeadlineClock();
        var trial = await SolusTrialAsync(clock: clock, targetMilliseconds: 30000,
            invocationMilliseconds: invocationCut ? 9000 : 20000);
        using var caller = new CancellationTokenSource();
        trial.Store.DelayedReadbackPhase = CampaignProviderHostPhase.SemanticTool;
        trial.Store.DelayedReadback = () => { clock.Advance(10500); if (callerCancelled) caller.Cancel(); };
        var port = new ReviewBoundaryPort();
        var registry = ReviewBoundaryRegistry(trial.Request, port);
        var exchange = ReviewBoundaryToolExchange();
        var session = trial.Session(exchange, registry);
        var result = await session.RunAsync(caller.Token).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, port.Calls);
        Assert.Equal(0, result.RunEnvelope.ToolCallCount);
        Assert.Single(exchange.Requests);
        Assert.Contains(CampaignProviderHostPhase.SemanticTool, trial.Store.CompletedHostReadbacks);
        Assert.NotEqual(CampaignProviderHostPhase.SemanticTool,
            Assert.IsType<CampaignProviderReservation>(trial.Store.Current.State.ActiveReservation).HostPhase);
        if (callerCancelled) Assert.Equal(DocumentationScribeTerminalKind.Cancelled, result.Stop.Kind);
        else Assert.Equal(invocationCut ? DocumentationScribeFailureCode.Budget : DocumentationScribeFailureCode.Timeout,
            Assert.IsType<DocumentationScribeFailureTerminal>(result.Stop).Code);
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

    private static ScriptedDocumentationScribeModelExchange ReviewBoundaryToolExchange() => new([
        ScriptedDocumentationScribeStep.Return(new([new(0, "call.read", "get-target-evidence", "{}"u8.ToArray())], [], usage: new(0, 0, 0, 0)))]);
    private static DocumentationScribeToolRegistry ReviewBoundaryRegistry(DocumentationScribeRequest request, ReviewBoundaryPort port) =>
        new DocumentationScribeToolRegistryBuilder(request.ToolPolicyId).Add(new ReviewBoundaryDescriptor(), port,
            new ReviewBoundaryCodec(), "Read synthetic target evidence.", "{\"type\":\"object\",\"additionalProperties\":false}"u8.ToArray(), 16).Build();
    private sealed record ReviewBoundaryRequest : IDocumentationScribeToolRequest<ReviewBoundaryResult>;
    private sealed record ReviewBoundaryResult(DocumentationScribeToolOutcome Outcome) : IDocumentationScribeToolResult;
    private sealed class ReviewBoundaryDescriptor : IDocumentationScribeToolDescriptor<ReviewBoundaryRequest, ReviewBoundaryResult>
    { public string OperationId => "get-target-evidence"; }
    private sealed class ReviewBoundaryCodec : IDocumentationScribeToolCodec<ReviewBoundaryRequest, ReviewBoundaryResult>
    {
        public DocumentationScribeToolDecodeResult<ReviewBoundaryRequest> DecodeArguments(ReadOnlyMemory<byte> argumentsUtf8Json) =>
            DocumentationScribeToolDecodeResult<ReviewBoundaryRequest>.Accepted(new());
        public DocumentationScribeToolEncodeResult EncodeResult(ReviewBoundaryRequest request, ReviewBoundaryResult result) =>
            DocumentationScribeToolEncodeResult.Accepted(new("{}"u8.ToArray(), []));
    }
    private sealed class ReviewBoundaryPort(Exception? failure = null) : IDocumentationScribeToolPort<ReviewBoundaryRequest, ReviewBoundaryResult>
    {
        internal int Calls { get; private set; }
        public ValueTask<ReviewBoundaryResult> InvokeAsync(ReviewBoundaryRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return failure is null ? ValueTask.FromResult(new ReviewBoundaryResult(DocumentationScribeToolOutcome.Complete))
                : ValueTask.FromException<ReviewBoundaryResult>(failure);
        }
    }
}
