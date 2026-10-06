using ContractScribe.Core;
using ContractScribe.Cli;

namespace ContractScribe.Tests;

public sealed partial class DocumentationScribeCompositionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Every_physical_checkpoint_response_loss_stops_before_another_send(bool settlement, bool applied)
    {
        await using var fixture = await CompositionFixture.CreateProposalStageAsync();
        var campaign = fixture.CreateCampaign();
        var store = new MemoryCampaignStore(CampaignStateJson.CreateArtifact(campaign.InitialState))
        {
            FailPhysicalSettlement = settlement,
            ApplyNextReplaceBeforeReporting = applied,
        };
        var exchange = new SemanticThenProposalExchange(fixture.Request);
        var outcome = await DocumentationCampaignProposalExecutor.ExecuteAsync(
            campaign.Input(fixture, store, exchange, RuntimeOptions()));
        Assert.Equal(DocumentationCampaignProposalOutcomeKind.StateConflict, outcome.Kind);
        Assert.Equal(settlement ? 1 : 0, exchange.RequestCount);
        var interrupted = store.Current!;
        var claim = Assert.IsType<CampaignProviderReservation>(interrupted.State.ActiveReservation);
        Assert.Equal(settlement && applied ? CampaignProviderOperationKind.Host
            : !settlement && !applied ? CampaignProviderOperationKind.Host : CampaignProviderOperationKind.Dispatch, claim.OperationKind);
        var resumedStore = new MemoryCampaignStore(CampaignStateJson.Parse(interrupted.ExactUtf8Json.AsMemory()).Artifact!);
        var recovered = await DocumentationCampaignProposalExecutor.ExecuteAsync(
            campaign.Input(fixture, resumedStore, new ProposalExchange(fixture.Request), RuntimeOptions()));
        Assert.Equal(DocumentationCampaignProposalOutcomeKind.ProposalReady, recovered.Kind);
        var state = resumedStore.Current!.State;
        Assert.Equal(1, state.LineageCharges.OuterInvocations);
        Assert.Equal(1, state.WorkItems.Single(item => item.Status == CampaignWorkStatus.ProposalComplete).OuterAttemptCount);
        Assert.Equal(settlement && applied ? 2 : 1, state.LineageCharges.ProviderRequests.Observed);
        Assert.Equal(claim.OperationKind == CampaignProviderOperationKind.Dispatch ? 1 : 0,
            state.LineageCharges.ProviderRequests.ConservativeUnobserved);
        var charges = state.LineageCharges;
        var replay = await DocumentationCampaignProposalExecutor.ExecuteAsync(
            campaign.Input(fixture, resumedStore, new CountingExchange(), RuntimeOptions()));
        Assert.Equal(DocumentationCampaignProposalOutcomeKind.ProposalReady, replay.Kind);
        Assert.Equal(charges, resumedStore.Current.State.LineageCharges);
    }
}
