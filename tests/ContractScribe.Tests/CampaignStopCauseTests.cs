using ContractScribe.Core;

namespace ContractScribe.Tests;

public sealed partial class CampaignStateContractTests
{
    [Theory]
    [InlineData(CampaignTerminalKind.Cancelled, CampaignTerminalReason.Caller)]
    [InlineData(CampaignTerminalKind.Timeout, CampaignTerminalReason.Deadline)]
    [InlineData(CampaignTerminalKind.Exhausted, CampaignTerminalReason.Budget)]
    public async Task Zero_send_stop_settles_host_once_without_fabricated_provider_usage(
        CampaignTerminalKind kind, CampaignTerminalReason reason)
    {
        var scenario = UnlimitedScenario();
        var request = CreateScribeExchange(scenario.Plan.WorkItems[0], scenario: scenario).Request;
        var initial = CampaignStateJson.CreateArtifact(scenario.InitialState);
        var store = new TransitionCheckpointStore(initial);
        var allowance = CampaignInvocationTestPolicy.Create(request.Limits, new C4CampaignClock());
        var admitted = CampaignStateReducer.AdmitProviderInvocation(initial, scenario.ExecutionAuthority,
            "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan, scenario.Plan.WorkItems[0].WorkItemKey,
            request, CampaignStateFactory.CreateInvocationTargetAllowance(initial.State, new(100)), allowance);
        var accepted = await CampaignCheckpointAcceptance.AcceptAsync(store, admitted);
        var invocation = CampaignStateReducer.CreateProviderInvocationAuthority(accepted.AcceptedCheckpoint!,
            scenario.ExecutionAuthority, "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan, request);
        Assert.True(invocation.BindInvocationAllowance(allowance));
        Assert.True(invocation.TryBeginDispatch(out _));
        var invalid = CampaignStateReducer.StopProviderBeforePhysicalDispatch(invocation, CampaignTerminalKind.Failed, 37);
        Assert.Equal(CampaignTransitionFailure.InvalidAuthority, invalid.Failure);
        Assert.True(accepted.Artifact!.ExactUtf8Json.AsSpan().SequenceEqual(invalid.Artifact.ExactUtf8Json.AsSpan()));
        var stopped = CampaignStateReducer.StopProviderBeforePhysicalDispatch(invocation, kind, 37);
        Assert.Equal(CampaignTransitionKind.Applied, stopped.Kind);
        Assert.Equal(reason, stopped.Artifact.State.TerminalOutcome!.Reason);
        Assert.Null(stopped.Artifact.State.ActiveReservation);
        Assert.All(stopped.Artifact.State.WorkItems, work => Assert.Null(work.PausedProviderAttempt));
        Assert.Equal(37, stopped.Artifact.State.LineageCharges.ActiveElapsedMilliseconds.Observed);
        Assert.Equal(0, stopped.Artifact.State.LineageCharges.ActiveElapsedMilliseconds.ConservativeUnobserved);
        Assert.Equal(0, stopped.Artifact.State.LineageCharges.ProviderRequests.TotalCharged);
        Assert.Equal(0, stopped.Artifact.State.LineageCharges.InputTokens.TotalCharged);
        Assert.Equal(0, stopped.Artifact.State.LineageCharges.OutputTokens.TotalCharged);
        Assert.Equal(0, allowance.ProviderRequestCount);
        Assert.Equal(CampaignTransitionFailure.InvalidAuthority,
            CampaignStateReducer.StopProviderBeforePhysicalDispatch(invocation, kind, 37).Failure);
        var final = await CampaignCheckpointAcceptance.AcceptAsync(store, stopped);
        Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted, final.Kind);
    }
}

