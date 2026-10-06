using System.Text;
using ContractScribe.Agent.Runtime;
using ContractScribe.Core;
using ContractScribe.Cli;

namespace ContractScribe.Tests;

public sealed partial class CampaignStateContractTests
{
    [Theory]
    [InlineData("input", false)]
    [InlineData("input", true)]
    [InlineData("uncached", false)]
    [InlineData("uncached", true)]
    [InlineData("output", false)]
    [InlineData("output", true)]
    [InlineData("cost", false)]
    [InlineData("cost", true)]
    public async Task Settled_lifetime_caps_gate_every_non_final_host_phase_and_keep_exact_equality(string dimension, bool equality)
    {
        var scenario = LifetimeScenario(dimension);
        var request = CreateScribeExchange(scenario.Plan.WorkItems[0], scenario: scenario).Request;
        var initial = CampaignStateJson.CreateArtifact(scenario.InitialState);
        var store = new TransitionCheckpointStore(initial);
        var allowance = new DocumentationScribeInvocationAllowance(LifetimeInvocationLimits());
        var admission = CampaignStateReducer.AdmitProviderInvocation(initial, scenario.ExecutionAuthority,
            "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan, scenario.Plan.WorkItems[0].WorkItemKey,
            request, CampaignStateFactory.CreateInvocationTargetAllowance(initial.State, new(100)), allowance);
        var accepted = await CampaignCheckpointAcceptance.AcceptAsync(store, admission);
        Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted, accepted.Kind);
        var parent = CampaignStateReducer.CreateProviderInvocationAuthority(accepted.AcceptedCheckpoint!, scenario.ExecutionAuthority,
            "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan, request);
        var coordinator = new CampaignScribeExecutionCoordinator(parent, allowance, store, default, null, allowance.Clock.GetTimestamp());
        Assert.True(parent.TryBeginDispatch(out _));
        var permit = await coordinator.Scope.ReserveProviderAsync(new(Sha256("lifetime cap dispatch"), 1, 1, 100), default);
        Assert.NotNull(permit);
        Assert.True(coordinator.Scope.BeginPhysicalDispatch(permit));
        var amount = dimension == "input" ? 65536 : dimension == "uncached" ? 32768 : 100;
        var charged = amount + (equality ? 0 : 1);
        var usage = dimension switch
        {
            "input" => new DocumentationScribeDispatchUsage(charged, 32768, charged - 32768, 0),
            "uncached" => new DocumentationScribeDispatchUsage(charged, 0, charged, 0),
            "cost" => new DocumentationScribeDispatchUsage(0, 0, 0, 0, CurrencyId: "currency.usd", CostMicrounits: charged),
            _ => new DocumentationScribeDispatchUsage(0, 0, 0, charged),
        };
        Assert.True(await coordinator.Scope.SettleProviderAsync(permit,
            new(DocumentationScribeDispatchDisposition.Success, usage), default));
        foreach (var phase in new[] { DocumentationScribeHostOperation.Preflight, DocumentationScribeHostOperation.RepositoryTool,
            DocumentationScribeHostOperation.SemanticTool, DocumentationScribeHostOperation.RegisteredTool,
            DocumentationScribeHostOperation.RetryWait, DocumentationScribeHostOperation.Continuation })
        {
            var transition = CampaignStateReducer.AdvanceHostOperation(parent, phase, 0, 10, completing: false);
            Assert.Equal(equality ? CampaignTransitionKind.Applied : CampaignTransitionKind.Rejected, transition.Kind);
            if (!equality)
            {
                Assert.Equal(CampaignTransitionFailure.BudgetExhausted, transition.Failure);
                Assert.Equal(parent.AcceptedCheckpoint.Artifact.ExactUtf8Json, transition.Artifact.ExactUtf8Json);
                Assert.Null(await coordinator.Scope.BeginHostAsync(phase, default));
            }
        }
        Assert.Equal(!equality, coordinator.LifetimeStop);
        var terminal = await coordinator.Scope.BeginHostAsync(DocumentationScribeHostOperation.TerminalSubmission, default);
        if (allowance.HasCheckedStop) Assert.Null(terminal);
        else
        {
            Assert.NotNull(terminal);
            Assert.True(terminal.TryBeginOperation());
            Assert.True(await coordinator.Scope.CompleteHostAsync(terminal, default));
            var postflight = await coordinator.Scope.BeginHostAsync(DocumentationScribeHostOperation.Postflight, default);
            Assert.NotNull(postflight);
            Assert.True(postflight.TryBeginOperation());
            Assert.True(await coordinator.Scope.CompleteHostAsync(postflight, default));
        }
        var final = CampaignStateReducer.PauseProviderInvocation(parent, coordinator.CurrentElapsed, coordinator.LifetimeStop);
        if (!equality)
        {
            Assert.Equal(CampaignTerminalReason.LifetimeCap, final.Artifact.State.TerminalOutcome!.Reason);
            var charge = dimension switch
            {
                "input" => final.Artifact.State.LineageCharges.InputTokens,
                "uncached" => final.Artifact.State.LineageCharges.UncachedInputTokens,
                "cost" => final.Artifact.State.LineageCharges.CostMicrounits,
                _ => final.Artifact.State.LineageCharges.OutputTokens
            };
            Assert.Equal(charged, charge.Observed);
        }
    }

    [Theory]
    [InlineData("output", "tool", false)]
    [InlineData("cost", "tool", false)]
    [InlineData("output", "tool", true)]
    [InlineData("cost", "tool", true)]
    [InlineData("output", "invalid", false)]
    [InlineData("cost", "invalid", false)]
    [InlineData("output", "retry", false)]
    [InlineData("cost", "retry", false)]
    [InlineData("output", "proposal", false)]
    [InlineData("cost", "proposal", false)]
    [InlineData("output", "proposal", true)]
    [InlineData("cost", "proposal", true)]
    public async Task Runtime_stops_new_work_after_lifetime_settlement_but_retains_returned_valid_proposal(
        string dimension, string responseKind, bool equality)
    {
        var scenario = LifetimeScenario(dimension);
        var count = equality ? 100 : 101;
        var first = responseKind switch
        {
            "tool" => new DocumentationScribeModelResponse([new(0, "call.lifetime", "tool.lifetime", "{}"u8.ToArray())], []),
            "retry" => new DocumentationScribeModelResponse([], [], failure: new(DocumentationScribeModelFailureCode.TransientUnavailable,
                retryAfterMilliseconds: 300000)),
            "invalid" => new DocumentationScribeModelResponse([], [new("{}"u8.ToArray())]),
            _ => new DocumentationScribeModelResponse([], [new(C3ProposalTerminal(scenario))]),
        };
        first = new(first.ToolCalls, first.TerminalSubmissions, failure: first.Failure,
            usage: new(inputTokens: 0, outputTokens: count, cachedInputTokens: 0, uncachedInputTokens: 0, reasoningTokens: 0), cost: dimension == "cost" ? new("currency.usd", count) : null);
        var exchange = new ScriptedDocumentationScribeModelExchange([ScriptedDocumentationScribeStep.Return(first)]);
        var tool = new LifetimeTool();
        var registry = new DocumentationScribeToolRegistryBuilder("tool-policy.read-only.v1");
        registry.Add(tool, tool, tool, "Count owned tool operations", "{}"u8.ToArray(), 16);
        var (artifact, result) = await CompleteC3RuntimeAsync(scenario, exchange, LifetimeInvocationLimits(), registry.Build());
        Assert.Single(exchange.Requests);
        Assert.Equal(responseKind == "tool" && equality ? 1 : 0, tool.Calls);
        Assert.Equal(count, artifact.State.LineageCharges.OutputTokens.Observed);
        Assert.Equal(responseKind == "proposal" ? DocumentationScribeTerminalKind.Proposal : DocumentationScribeTerminalKind.Failure,
            result.Terminal.Kind);
        Assert.Equal(responseKind == "proposal" ? CampaignWorkStatus.ProposalComplete : CampaignWorkStatus.Planned,
            artifact.State.WorkItems[0].Status);
        Assert.Equal(equality && responseKind == "proposal" ? null : CampaignTerminalReason.LifetimeCap, artifact.State.TerminalOutcome?.Reason);
        if (responseKind == "retry")
            Assert.Equal(300000, artifact.State.WorkItems[0].PausedProviderAttempt!.RetryProgress.PendingRetryAfterMilliseconds);
        Assert.Null(artifact.State.ActiveReservation);
    }

    private static DocumentationScribeInvocationLimits LifetimeInvocationLimits() => DocumentationScribeInvocationLimits.Create(
        maximumProviderRequests: 8, maximumToolCalls: 16, maximumCachedInputTokens: 32768, maximumUncachedInputTokens: 32768,
        maximumOutputTokens: 8192, maximumRequestOutputTokens: 100, maximumElapsedMilliseconds: 120000,
        maximumRequestElapsedMilliseconds: 120000);
    private static ProposalScenario LifetimeScenario(string dimension) => CreateProposalScenario(targetLimit: 1,
        maximumProviderRequests: null, maximumInputTokens: dimension == "input" ? 65536 : null,
        maximumUncachedInputTokens: dimension == "uncached" ? 32768 : null,
        maximumOutputTokens: dimension == "output" ? 100 : null,
        maximumCostMicrounits: dimension == "cost" ? 100 : null, maximumElapsedMilliseconds: null,
        costCurrency: "currency.usd", costRates: new(0, 0, 1000000, 0));
    private sealed record LifetimeToolRequest : IDocumentationScribeToolRequest<LifetimeToolResult>;
    private sealed record LifetimeToolResult : IDocumentationScribeToolResult
    { public DocumentationScribeToolOutcome Outcome => DocumentationScribeToolOutcome.Complete; }
    private sealed class LifetimeTool : IDocumentationScribeToolDescriptor<LifetimeToolRequest, LifetimeToolResult>,
        IDocumentationScribeToolPort<LifetimeToolRequest, LifetimeToolResult>, IDocumentationScribeToolCodec<LifetimeToolRequest, LifetimeToolResult>
    {
        public string OperationId => "tool.lifetime";
        internal int Calls { get; private set; }
        public ValueTask<LifetimeToolResult> InvokeAsync(LifetimeToolRequest request, CancellationToken cancellationToken)
        { Calls++; return ValueTask.FromResult(new LifetimeToolResult()); }
        public DocumentationScribeToolDecodeResult<LifetimeToolRequest> DecodeArguments(ReadOnlyMemory<byte> argumentsUtf8Json) =>
            DocumentationScribeToolDecodeResult<LifetimeToolRequest>.Accepted(new());
        public DocumentationScribeToolEncodeResult EncodeResult(LifetimeToolRequest request, LifetimeToolResult result) =>
            DocumentationScribeToolEncodeResult.Accepted(new("{}"u8.ToArray(), []));
    }
}
