using System.Text.Json.Nodes;
using ContractScribe.Core;

namespace ContractScribe.Tests;

public sealed partial class CampaignStateContractTests
{
    [Theory]
    [InlineData("skip-result.json", false)]
    [InlineData("skip-result.json", true)]
    [InlineData("failure-result.json", false)]
    [InlineData("failure-result.json", true)]
    [InlineData("retryable-failure-result.json", false)]
    [InlineData("retryable-failure-result.json", true)]
    [InlineData("cancelled-result.json", false)]
    [InlineData("cancelled-result.json", true)]
    public void Provider_completion_headroom_covers_closed_outcomes_and_known_or_conservative_usage(
        string fixture, bool knownUsage)
    {
        var scenario = CreateProposalScenario(targetLimit: 1, maximumAttemptsPerTarget: 1);
        var work = scenario.Plan.WorkItems[0];
        var predecessor = CampaignStateJson.CreateArtifact(scenario.InitialState);
        var request = CreateScribeExchange(work).Request;
        var admitted = CampaignStateReducer.AdmitProviderInvocation(predecessor, scenario.ExecutionAuthority,
            "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan, work.WorkItemKey,
            request, CampaignStateFactory.CreateInvocationTargetAllowance(predecessor.State, new(1)));
        Assert.Equal(CampaignTransitionKind.Applied, admitted.Kind);
        var upperBytes = CampaignStateReducer.ValidateProviderSettlementCapacity(admitted.Artifact.State);
        var attempt = Assert.IsType<CampaignProviderReservation>(admitted.Artifact.State.ActiveReservation).AttemptId;
        var exchange = CreateScribeExchange(work, attemptId: attempt.Value, resultFixture: fixture,
            resultMutation: root =>
            {
                if (knownUsage) root["runEnvelope"]!["usage"] = new JsonObject
                {
                    ["inputTokens"] = 31,
                    ["uncachedInputTokens"] = 23,
                    ["outputTokens"] = 7,
                };
                else root["runEnvelope"]!.AsObject().Remove("usage");
            });
        var outcome = DocumentationScribeValidation.BindValidatedRunOutcome(exchange.Request, attempt, exchange.Result);
        var invocation = CampaignStateReducer.CreateProviderInvocationAuthority(AcceptForTest(predecessor, admitted),
            scenario.ExecutionAuthority, "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan, exchange.Request);
        Assert.True(invocation.TryBeginDispatch(out _));
        var elapsed = exchange.Result.RunEnvelope.ElapsedMilliseconds;
        var settlement = CampaignBudgetAccounting.SettleProviderInvocation(admitted.Artifact.State, outcome, elapsed);
        var completion = OrdinaryCompletion(invocation, outcome, elapsed);
        var completed = CampaignStateReducer.CompleteProviderInvocation(admitted.Artifact, completion,
            scenario.ExecutionAuthority, "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan);
        Assert.Equal(CampaignTransitionKind.Applied, completed.Kind);
        Assert.True(completed.Artifact.ExactUtf8Json.Length <= upperBytes);
        var parsed = CampaignStateJson.Parse(completed.Artifact.ExactUtf8Json.AsMemory());
        Assert.True(parsed.IsValid, parsed.FailureCode?.ToString());
        Assert.Null(parsed.Artifact!.State.ActiveReservation);
        Assert.Equal(settlement.Charges, parsed.Artifact.State.LineageCharges);
        Assert.Equal(knownUsage ? 31L : predecessor.State.LineageCharges.InputTokens.Observed,
            parsed.Artifact.State.LineageCharges.InputTokens.Observed);
        var inputBound = admitted.Artifact.State.ConfiguredCeilings.ScribeRunLimits.MaximumInputTokens;
        var expectedUnknown = !knownUsage ? inputBound
            : exchange.Result.RunEnvelope.ProviderRequestCount > 1 ? inputBound : 0;
        Assert.Equal(expectedUnknown,
            parsed.Artifact.State.LineageCharges.InputTokens.ConservativeUnobserved);
        Assert.Equal(CampaignTransitionFailure.InvalidAuthority,
            CampaignStateReducer.CompleteProviderInvocation(admitted.Artifact, completion,
                scenario.ExecutionAuthority, "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan).Failure);
    }
}
