using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ContractScribe.Agent.Providers;
using ContractScribe.Agent.Runtime;
using ContractScribe.Core;

namespace ContractScribe.Tests;

public sealed partial class CampaignStateContractTests
{
    [Theory]
    [InlineData("input", false)]
    [InlineData("input", true)]
    [InlineData("cached", false)]
    [InlineData("cached", true)]
    [InlineData("uncached", false)]
    [InlineData("uncached", true)]
    [InlineData("output", false)]
    [InlineData("output", true)]
    [InlineData("reasoning", false)]
    [InlineData("reasoning", true)]
    public async Task Runtime_partial_usage_retains_observed_and_unknown_history_before_later_cap_admission(
        string dimension, bool missingFirst)
    {
        var scenario = CreateProposalScenario(targetLimit: 2, maximumProviderRequests: null,
            maximumInputTokens: null, maximumUncachedInputTokens: null, maximumOutputTokens: null,
            maximumCostMicrounits: null, maximumElapsedMilliseconds: null,
            costRates: new CampaignCostRates(1_000_000, 2_000_000, 1_000_000, 3_000_000));
        DocumentationScribeModelUsage Usage(bool missing) => new(
            inputTokens: missing && dimension == "input" ? null : 100,
            outputTokens: missing && dimension == "output" ? null : 100,
            cachedInputTokens: missing && dimension == "cached" ? null : 100,
            uncachedInputTokens: missing && dimension == "uncached" ? null : 100,
            reasoningTokens: missing && dimension == "reasoning" ? null : 100);
        var exchange = new ScriptedDocumentationScribeModelExchange([
            ScriptedDocumentationScribeStep.Return(new([], [],
                failure: new(DocumentationScribeModelFailureCode.TransientUnavailable), usage: Usage(missingFirst))),
            ScriptedDocumentationScribeStep.Return(new([], [new DocumentationScribeModelTerminalSubmission(C3ProposalTerminal(scenario))],
                usage: Usage(!missingFirst))),
        ]);
        var (artifact, result) = await CompleteC3RuntimeAsync(scenario, exchange);
        Assert.Equal(DocumentationScribeTerminalKind.Proposal, result.Terminal.Kind);
        Assert.Equal(CampaignWorkStatus.ProposalComplete, artifact.State.WorkItems[0].Status);
        Assert.Equal(2, result.RunEnvelope.ProviderRequestCount);
        Assert.Equal(2, exchange.Requests.Length);
        var charges = artifact.State.LineageCharges;
        var charge = dimension switch
        {
            "input" => charges.InputTokens,
            "cached" => charges.CachedInputTokens,
            "uncached" => charges.UncachedInputTokens,
            "output" => charges.OutputTokens,
            _ => charges.ReasoningTokens,
        };
        var limits = artifact.State.ConfiguredCeilings.ScribeRunLimits;
        var bound = dimension switch
        {
            "input" or "cached" => limits.MaximumInputTokens,
            "uncached" => limits.MaximumUncachedInputTokens,
            _ => limits.MaximumOutputTokens,
        };
        Assert.Equal(100, charge.Observed);
        Assert.Equal(bound - 100, charge.ConservativeUnobserved);
        Assert.Equal(bound, charge.TotalCharged);
        var restored = CampaignStateJson.Parse(artifact.ExactUtf8Json.AsMemory());
        Assert.True(restored.IsValid);
        Assert.Equal(charges, restored.Artifact!.State.LineageCharges);

        // Cached and reasoning are subsets; finite admission uses their parent dimensions.
        var budget = scenario.Input.ExecutionPolicy.CampaignBudget;
        budget = dimension switch
        {
            "input" or "cached" => budget with { MaximumInputTokens = bound + 1_000 },
            "uncached" => budget with { MaximumUncachedInputTokens = bound + 1_000 },
            _ => budget with { MaximumOutputTokens = bound + 1_000 },
        };
        var input = WithBudget(scenario, budget);
        var capped = RefreshCaps(restored.Artifact, scenario, input).Artifact;
        var nextWork = scenario.Plan.WorkItems[1];
        var nextRequest = CreateScribeExchange(nextWork).Request;
        var admission = CampaignStateReducer.AdmitProviderInvocation(capped, scenario.ExecutionAuthority,
            "style.synthetic", scenario.StyleProjection, input, CampaignPlanner.Plan(input), nextWork.WorkItemKey,
            nextRequest, CampaignStateFactory.CreateInvocationTargetAllowance(capped.State, new(100)));
        Assert.Equal(CampaignTransitionKind.Applied, admission.Kind);
        Assert.Equal(CampaignTerminalReason.LifetimeCap, admission.Artifact.State.TerminalOutcome!.Reason);
        Assert.Null(admission.Artifact.State.ActiveReservation);
        Assert.Equal(charges, admission.Artifact.State.LineageCharges);
        Assert.Null(capped.State.ActiveReservation);
        Assert.Equal(charges.OuterInvocations, capped.State.LineageCharges.OuterInvocations);
        Assert.Equal(2, exchange.Requests.Length);
    }

    [Fact]
    public async Task Runtime_observed_output_overrun_is_not_replaced_by_a_smaller_reserved_bound()
    {
        var scenario = UnlimitedScenario();
        var bound = scenario.InitialState.ConfiguredCeilings.ScribeRunLimits.MaximumOutputTokens;
        var exchange = new ScriptedDocumentationScribeModelExchange([
            ScriptedDocumentationScribeStep.Return(new([], [],
                failure: new(DocumentationScribeModelFailureCode.TransientUnavailable),
                usage: new(outputTokens: bound - 1))),
            ScriptedDocumentationScribeStep.Return(new([], [],
                failure: new(DocumentationScribeModelFailureCode.Authentication),
                usage: new(outputTokens: 2))),
        ]);
        var (artifact, result) = await CompleteC3RuntimeAsync(scenario, exchange);
        Assert.Equal(2, result.RunEnvelope.ProviderRequestCount);
        Assert.Equal(bound + 1, artifact.State.LineageCharges.OutputTokens.Observed);
        Assert.Equal(0, artifact.State.LineageCharges.OutputTokens.ConservativeUnobserved);
        Assert.Equal(bound + 1, artifact.State.LineageCharges.OutputTokens.TotalCharged);
    }

    [Theory]
    [InlineData(1_000_000L, 3_000_000L)]
    [InlineData(3_000_000L, 1_000_000L)]
    public async Task Contradictory_reasoning_fails_actual_transport_runtime_and_preserves_full_exposure(
        long outputRate, long reasoningRate)
    {
        var scenario = CreateProposalScenario(targetLimit: 1, maximumProviderRequests: null,
            maximumInputTokens: null, maximumUncachedInputTokens: null, maximumOutputTokens: null,
            maximumCostMicrounits: null, maximumElapsedMilliseconds: null,
            scribeRequestTemplate: ReadScribeRequest(root => root["limits"]!["maximumCostMicrounits"] = 0),
            costRates: new CampaignCostRates(0, 0, outputRate, reasoningRate));
        using var handler = new C3UsageHandler("""
            {"choices":[{"index":0,"message":{"role":"assistant","tool_calls":[{"id":"call.terminal","type":"function","function":{"name":"cs_terminal","arguments":"{\"kind\":\"skip\"}"}}]},"finish_reason":"tool_calls"}],"usage":{"completion_tokens":1,"completion_tokens_details":{"reasoning_tokens":8192}}}
            """);
        using var exchange = new OpenAiCompatibleHttpModelExchange(
            new OpenAiCompatibleHttpTransportOptions(new Uri("https://example.test/v1"), "model", networkEnabled: true),
            handler, disposeHandler: false);
        var (artifact, result) = await CompleteC3RuntimeAsync(scenario, exchange);
        Assert.Equal(DocumentationScribeFailureCode.Provider,
            Assert.IsType<DocumentationScribeFailureTerminal>(result.Terminal).Code);
        Assert.Equal(1, handler.Calls);
        Assert.Null(result.RunEnvelope.Usage);
        var exposure = scenario.InitialState.ConfiguredCeilings.ScribeRunLimits.MaximumOutputTokens;
        Assert.Equal(exposure, artifact.State.LineageCharges.OutputTokens.ConservativeUnobserved);
        Assert.Equal(0, artifact.State.LineageCharges.OutputTokens.Observed);
        Assert.True(artifact.State.LineageCharges.CostMicrounits.ConservativeUnobserved >= exposure * Math.Max(outputRate, reasoningRate) / 1_000_000);
        var restored = CampaignStateJson.Parse(artifact.ExactUtf8Json.AsMemory()).Artifact!;
        var input = WithBudget(scenario, scenario.Input.ExecutionPolicy.CampaignBudget with { MaximumOutputTokens = exposure + 1_000 });
        var capped = RefreshCaps(restored, scenario, input).Artifact;
        Assert.Equal(CampaignBudgetDecisionKind.Exhausted, CampaignBudgetAccounting.ReserveProviderInvocation(capped.State).Kind);
        Assert.Equal(1, handler.Calls);
        var finiteCost = WithBudget(scenario, scenario.Input.ExecutionPolicy.CampaignBudget with
        { MaximumCostMicrounits = restored.State.LineageCharges.CostMicrounits.TotalCharged + 1 });
        var costCapped = RefreshCaps(restored, scenario, finiteCost).Artifact;
        Assert.Equal(CampaignBudgetDecisionKind.Exhausted, CampaignBudgetAccounting.ReserveProviderInvocation(costCapped.State).Kind);
        Assert.Equal(1, handler.Calls);
        Assert.Throws<ArgumentException>(() => new DocumentationScribeModelUsage(outputTokens: 1, reasoningTokens: 8192));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(8192)]
    public async Task Reasoning_only_runtime_usage_keeps_output_unknown_and_charges_no_second_output(int reasoning)
    {
        var scenario = UnlimitedScenario(new CampaignCostRates(0, 0, 1_000_000, 3_000_000));
        var exchange = new ScriptedDocumentationScribeModelExchange([
            ScriptedDocumentationScribeStep.Return(new([], [],
                failure: new(DocumentationScribeModelFailureCode.Authentication), usage: new(reasoningTokens: reasoning))),
        ]);
        var (artifact, _) = await CompleteC3RuntimeAsync(scenario, exchange);
        Assert.Equal(0, artifact.State.LineageCharges.OutputTokens.Observed);
        Assert.Equal(8192, artifact.State.LineageCharges.OutputTokens.ConservativeUnobserved);
        Assert.Equal(reasoning, artifact.State.LineageCharges.ReasoningTokens.Observed);
        Assert.Equal(0, artifact.State.LineageCharges.ReasoningTokens.ConservativeUnobserved);
    }

    private static byte[] C3ProposalTerminal(ProposalScenario scenario)
    {
        var work = scenario.Plan.WorkItems[0];
        var target = Assert.Single(work.Targets);
        var terminal = ReadJsonFixture("documentation-scribe", "v1", "valid", "proposal-result.json")["terminal"]!;
        SetSymbol(terminal["target"]!["symbolRef"]!, target.SymbolRef);
        SetSource(terminal["target"]!["sourceCommitment"]!, Assert.IsType<CampaignPlanningRepositorySourceAuthority>(target.Source));
        foreach (var unit in terminal["contentUnits"]!.AsArray())
        {
            var ids = unit!["evidenceReferenceIds"]!.AsArray();
            for (var index = 0; index < ids.Count; index++)
                ids[index] = ids[index]!.GetValue<string>() + "." + work.WorkItemKey[^8..];
        }
        return Encoding.UTF8.GetBytes(terminal.ToJsonString());
    }

    private static async Task<(CampaignCheckpointArtifact Artifact, DocumentationScribeRunResult Result)>
        CompleteC3RuntimeAsync(ProposalScenario scenario, IDocumentationScribeModelExchange exchange)
    {
        var initial = CampaignStateJson.CreateArtifact(scenario.InitialState);
        var request = CreateScribeExchange(scenario.Plan.WorkItems[0], requestMutation: root =>
            root["limits"]!["maximumCostMicrounits"] = scenario.InitialState.ConfiguredCeilings.ScribeRunLimits.MaximumCostMicrounits,
            resultMutation: root => root["runEnvelope"]!.AsObject().Remove("cost")).Request;
        var admitted = AdmitCapProvider(scenario, initial, request);
        var reservation = Assert.IsType<CampaignProviderReservation>(admitted.Artifact.State.ActiveReservation);
        var authority = CampaignStateReducer.CreateProviderInvocationAuthority(AcceptForTest(initial, admitted),
            scenario.ExecutionAuthority, "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan, request);
        Assert.True(authority.TryBeginDispatch(out _));
        var runtime = new DocumentationScribeRuntime(exchange,
            new DocumentationScribeToolRegistryBuilder(request.ToolPolicyId).Build(),
            new DocumentationScribeRuntimeOptions("provider.synthetic.v1", "model.synthetic.v1", "scribe-protocol.v1"));
        var prompt = new DocumentationScribePromptInput(
            request.ContextReferences.Select(reference => new DocumentationScribeContextContent(reference.ContextReferenceId,
                reference.Kind, reference.ContentSha256, reference.IncludedUtf8ByteCount, reference.IsTruncated,
                new string('c', reference.IncludedUtf8ByteCount))).ToImmutableArray(),
            request.EvidenceReferences.Select(reference => new DocumentationScribeEvidenceContent(reference.EvidenceReferenceId,
                reference.Authority, reference.ContentSha256, reference.IncludedUtf8ByteCount, reference.IsTruncated,
                new string('e', reference.IncludedUtf8ByteCount))).ToImmutableArray());
        var result = await runtime.RunAsync(request, reservation.AttemptId, prompt);
        var outcome = DocumentationScribeValidation.BindValidatedRunOutcome(request, reservation.AttemptId, result);
        var complete = CampaignStateReducer.CompleteProviderInvocation(admitted.Artifact,
            OrdinaryCompletion(authority, outcome, Math.Max(100, result.RunEnvelope.ElapsedMilliseconds)),
            scenario.ExecutionAuthority, "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan);
        Assert.Equal(CampaignTransitionKind.Applied, complete.Kind);
        return (complete.Artifact, result);
    }

    private sealed class C3UsageHandler(string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}

public sealed partial class DocumentationScribeProviderTransportTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 100)]
    [InlineData(100, 1)]
    [InlineData(null, 100)]
    public void Actual_codec_keeps_valid_reasoning_subset_and_missing_output_neighbors(int? output, int reasoning)
    {
        var usage = new JsonObject { ["completion_tokens_details"] = new JsonObject { ["reasoning_tokens"] = reasoning } };
        if (output is not null) usage["completion_tokens"] = output;
        var prepared = OpenAiCompatibleChatCompletionsCodec.Prepare(Request([]), "model", RequiredContinuationProfile());
        var response = OpenAiCompatibleChatCompletionsCodec.ParseResponse(
            Encoding.UTF8.GetBytes(ThinkingToolResponseWithUsage(usage.ToJsonString())), prepared);
        Assert.Equal(output, response.Usage!.OutputTokens);
        Assert.Equal(reasoning, response.Usage.ReasoningTokens);
    }
}

public sealed partial class CampaignStateContractTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public void Persisted_envelope_rejects_single_request_contradiction_but_keeps_partial_multi_aggregates(
        int requests, bool valid)
    {
        var request = ReadScribeRequest();
        var root = ReadJsonFixture("documentation-scribe", "v1", "valid", "proposal-result.json");
        root["scribeRequestSha256"] = request.ArtifactSha256;
        root["runEnvelope"]!["scribeRequestSha256"] = request.ArtifactSha256;
        root["runEnvelope"]!["providerRequestCount"] = requests;
        root["runEnvelope"]!["usage"] = new JsonObject { ["outputTokens"] = 100, ["reasoningTokens"] = 200 };
        Assert.True(DocumentationScribeAttemptId.TryParse(root["attemptId"]!.GetValue<string>(), out var attempt));
        var parsed = DocumentationScribeValidation.ParseRunResult(request, attempt, [], Encoding.UTF8.GetBytes(root.ToJsonString()));
        if (valid) Assert.Null(parsed.Failure);
        else Assert.NotNull(parsed.Failure);
        if (valid)
        {
            var bound = DocumentationScribeValidation.BindValidatedRunOutcome(request, attempt, parsed.Result!);
            Assert.Equal(100, bound.RunResult.RunEnvelope.Usage!.OutputTokens);
            Assert.Equal(200, bound.RunResult.RunEnvelope.Usage.ReasoningTokens);
        }
    }
}
