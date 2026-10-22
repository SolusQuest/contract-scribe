using ContractScribe.Cli;
using ContractScribe.Core;
using System.Text;
using System.Text.Json.Nodes;

namespace ContractScribe.Tests;

public sealed partial class DocumentationScribeCompositionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Member_and_progress_acknowledgement_failures_keep_the_first_store_failure(bool progress)
    {
        await using var fixture = await CompositionFixture.CreateProposalStageAsync();
        var campaign = fixture.CreateCampaign();
        using var stop = new CancellationTokenSource();
        var inner = new MemoryCampaignStore(CampaignStateJson.CreateArtifact(campaign.InitialState));
        var store = new CampaignAcknowledgementFaultStore(inner, "publication", stop, (before, after) => progress
            ? after.State.ScribeProgress?.LastProgressSha256 is not null
                && before.State.ScribeProgress?.LastProgressSha256 != after.State.ScribeProgress.LastProgressSha256
            : after.State.WorkItems.Any(work => work.TrustedProposal is not null)
                && before.State.WorkItems.All(work => work.TrustedProposal is null));
        var exchange = new CountingProposalExchange(fixture.Request);

        var outcome = await DocumentationCampaignProposalExecutor.ExecuteAsync(
            campaign.Input(fixture, store, exchange, RuntimeOptions()));

        Assert.Equal(CampaignCheckpointAcceptanceKind.PublicationFailure, outcome.CheckpointFailure);
        Assert.Equal(store.BeforeFault!.Sha256, outcome.Artifact!.Sha256);
        Assert.Equal(store.AfterFault!.Sha256, inner.Current!.Sha256);
        Assert.Equal(1, exchange.RequestCount);
        Assert.Equal(1, inner.Current.State.LineageCharges.ProviderRequests.Observed);
        Assert.NotNull(inner.Current.State.ActiveReservation);
        Assert.Null(inner.Current.State.CandidateObservation);
    }

    [Theory]
    [InlineData("publication", CampaignCheckpointAcceptanceKind.PublicationFailure)]
    [InlineData("readback", CampaignCheckpointAcceptanceKind.ReadbackMismatch)]
    [InlineData("observer", CampaignCheckpointAcceptanceKind.Conflict)]
    [InlineData("cancel", CampaignCheckpointAcceptanceKind.Cancelled)]
    public async Task Terminal_acknowledgement_failure_preserves_its_kind_and_latest_acknowledged_checkpoint(
        string fault, CampaignCheckpointAcceptanceKind expected)
    {
        await using var fixture = await CompositionFixture.CreateProposalStageAsync();
        var campaign = fixture.CreateCampaign();
        using var stop = new CancellationTokenSource();
        var inner = new MemoryCampaignStore(CampaignStateJson.CreateArtifact(campaign.InitialState));
        var store = new CampaignAcknowledgementFaultStore(inner, fault, stop);
        var exchange = new CountingProposalExchange(fixture.Request);
        var input = campaign.Input(fixture, store, exchange, RuntimeOptions(), settlementToken: stop.Token);

        var outcome = await DocumentationCampaignProposalExecutor.ExecuteAsync(input);

        Assert.Equal(DocumentationCampaignProposalOutcomeKind.StateConflict, outcome.Kind);
        Assert.Equal(expected, outcome.CheckpointFailure);
        Assert.Equal(store.BeforeFault!.Sha256, outcome.Artifact!.Sha256);
        Assert.Equal(store.BeforeFault.CheckpointRevision + 1, store.AfterFault!.CheckpointRevision);
        Assert.Equal(store.AfterFault.Sha256, inner.Current!.Sha256);
        Assert.Equal(CampaignProviderHostPhase.TerminalSubmission,
            Assert.IsType<CampaignProviderReservation>(inner.Current.State.ActiveReservation).HostPhase);
        Assert.Equal(1, exchange.RequestCount);
        Assert.Equal(1, input.InvocationAllowance!.ProviderRequestCount);
        Assert.Equal(0, input.InvocationAllowance!.ToolCallCount);
        Assert.All(inner.Current.State.WorkItems, work => Assert.Null(work.TrustedProposal));
    }
}

public sealed partial class CampaignStateContractTests
{
    [Theory]
    [InlineData("clean", null)]
    [InlineData("publication", CampaignCheckpointAcceptanceKind.PublicationFailure)]
    [InlineData("readback", CampaignCheckpointAcceptanceKind.ReadbackMismatch)]
    [InlineData("observer", CampaignCheckpointAcceptanceKind.Conflict)]
    [InlineData("cancel", CampaignCheckpointAcceptanceKind.Cancelled)]
    public async Task Terminal_acknowledgement_keeps_three_settled_sends_and_separates_ack13_from_persisted14(
        string fault, CampaignCheckpointAcceptanceKind? expected)
    {
        var templateNode = ReadJsonFixture("documentation-scribe", "v1", "valid", "request.json");
        templateNode["limits"]!["maximumElapsedMilliseconds"] = 600_000;
        var template = DocumentationScribeValidation.ParseRequest(Encoding.UTF8.GetBytes(templateNode.ToJsonString())).Request!;
        var scenario = CreateProposalScenario(workItemCount: 12, targetLimit: 5,
            maximumElapsedMilliseconds: 7_200_000, maximumProviderRequests: 104, scribeRequestTemplate: template);
        var exchange = CreateScribeExchange(scenario.Plan.WorkItems[0], scenario: scenario,
            requestMutation: root => root["limits"]!["maximumElapsedMilliseconds"] = 600_000);
        var request = exchange.Request;
        var initial = CampaignStateJson.CreateArtifact(scenario.InitialState);
        var clock = new C4CampaignClock();
        var allowance = new DocumentationScribeInvocationAllowance(DocumentationScribeInvocationLimits.Create(
            maximumProviderRequests: 8, maximumToolCalls: 16, maximumCachedInputTokens: 32768,
            maximumUncachedInputTokens: 32768, maximumOutputTokens: 8192, maximumRequestOutputTokens: 4096,
            maximumElapsedMilliseconds: 600_000, maximumRequestElapsedMilliseconds: 90_000), clock);
        using var stop = new CancellationTokenSource();
        var inner = new TransitionCheckpointStore(initial);
        var store = new CampaignAcknowledgementFaultStore(inner, fault, stop,
            beforeSemanticWrite: () => clock.Advance(13_164));
        var admitted = CampaignStateReducer.AdmitProviderInvocation(initial, scenario.ExecutionAuthority, "style.synthetic",
            scenario.StyleProjection, scenario.Input, scenario.Plan, scenario.Plan.WorkItems[0].WorkItemKey, request,
            CampaignStateFactory.CreateInvocationTargetAllowance(initial.State, new(5)), allowance);
        var accepted = await CampaignCheckpointAcceptance.AcceptAsync(store, admitted);
        var invocation = CampaignStateReducer.CreateProviderInvocationAuthority(accepted.AcceptedCheckpoint!, scenario.ExecutionAuthority,
            "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan, request);
        var coordinator = new CampaignScribeExecutionCoordinator(invocation, allowance, store, stop.Token, null, clock.GetTimestamp());
        coordinator.Scope.BindRuntimeWindow(request, clock, clock.GetTimestamp());
        Assert.True(invocation.TryBeginDispatch(out _));
        for (var index = 1; index <= 3; index++)
        {
            var provider = await coordinator.Scope.ReserveProviderAsync(new(Sha256("ack-test." + index), 1, index, 4096), default);
            Assert.NotNull(provider);
            Assert.True(coordinator.Scope.BeginPhysicalDispatch(provider));
            clock.Advance(100);
            Assert.True(await coordinator.Scope.SettleProviderAsync(provider,
                new(DocumentationScribeDispatchDisposition.Success, new(100, 50, 50, 10, 5)), default));
            if (index == 3) continue;
            var semantic = await coordinator.Scope.BeginHostAsync(DocumentationScribeHostOperation.SemanticTool, default);
            Assert.NotNull(semantic);
            Assert.True(semantic.TryBeginOperation());
            clock.Advance(1);
            Assert.True(await coordinator.Scope.CompleteHostAsync(semantic, default));
            if (index != 1) continue;
            var registered = await coordinator.Scope.BeginHostAsync(DocumentationScribeHostOperation.RegisteredTool, default);
            Assert.NotNull(registered);
            Assert.True(registered.TryBeginOperation());
            Assert.True(await coordinator.Scope.CompleteHostAsync(registered, default));
        }
        Assert.Equal(13, invocation.AcceptedCheckpoint.Artifact.CheckpointRevision);
        var terminal = await coordinator.Scope.BeginHostAsync(DocumentationScribeHostOperation.TerminalSubmission, default);
        var persisted = CampaignStateJson.Parse((await inner.ReadAsync(default)).ExactUtf8Json.AsMemory()).Artifact!;
        Assert.Equal(14, persisted.CheckpointRevision);
        var claim = Assert.IsType<CampaignProviderReservation>(persisted.State.ActiveReservation);
        Assert.Equal(CampaignProviderHostPhase.TerminalSubmission, claim.HostPhase);
        Assert.Equal(90_000, claim.Exposure.ElapsedMilliseconds);
        Assert.Equal(3, persisted.State.LineageCharges.ProviderRequests.Observed);
        Assert.Equal(300, persisted.State.LineageCharges.InputTokens.Observed);
        Assert.Equal(30, persisted.State.LineageCharges.OutputTokens.Observed);
        Assert.All(persisted.State.DispatchObservations, observation => Assert.Equal(CampaignDispatchObservationState.Settled, observation.State));
        Assert.Equal(expected, coordinator.CheckpointFailure);
        if (expected is null)
        {
            Assert.NotNull(terminal);
            Assert.True(terminal.TryBeginOperation());
            Assert.Equal(4, allowance.ToolCallCount);
            Assert.Equal(14, coordinator.LatestAcknowledgedArtifact.CheckpointRevision);
            Assert.True(await coordinator.Scope.CompleteHostAsync(terminal, default));
            Assert.False(coordinator.Conflict);
            return;
        }
        Assert.Null(terminal);
        Assert.True(coordinator.Conflict);
        Assert.Equal(3, allowance.ToolCallCount);
        Assert.Equal(13, coordinator.LatestAcknowledgedArtifact.CheckpointRevision);
        using var laterCancellation = new CancellationTokenSource();
        laterCancellation.Cancel();
        Assert.False(await coordinator.AcceptHostProgressAsync(
            CampaignStateReducer.AdvanceHostOperation(invocation, DocumentationScribeHostOperation.TerminalSubmission, 0, 90_000, false),
            laterCancellation.Token));
        Assert.Equal(expected, coordinator.CheckpointFailure);
        Assert.Equal(persisted.Sha256, CampaignStateJson.Parse((await inner.ReadAsync(default)).ExactUtf8Json.AsMemory()).Artifact!.Sha256);
        Assert.Equal(3, allowance.ProviderRequestCount);
        Assert.Equal(3, allowance.ToolCallCount);
        Assert.All(persisted.State.WorkItems, work => Assert.Null(work.TrustedProposal));
    }
}

internal sealed class CampaignAcknowledgementFaultStore(
    ICampaignSemanticCheckpointStore inner, string fault, CancellationTokenSource stop,
    Func<CampaignCheckpointArtifact, CampaignCheckpointArtifact, bool>? select = null,
    Action? beforeSemanticWrite = null) : ICampaignSemanticCheckpointStore
{
    internal CampaignCheckpointArtifact? BeforeFault { get; private set; }
    internal CampaignCheckpointArtifact? AfterFault { get; private set; }
    public object SemanticCheckpointIdentity => inner.SemanticCheckpointIdentity;
    public CampaignSemanticCheckpointStoreKind SemanticCheckpointKind => inner.SemanticCheckpointKind;
    public IDisposable EnterSemanticCheckpointWindow(CampaignSemanticCheckpointWindow window) =>
        inner.EnterSemanticCheckpointWindow(window);
    public ValueTask<CampaignCheckpointReadResult> ReadAsync(CancellationToken token) => inner.ReadAsync(token);
    public ValueTask<CampaignCheckpointWriteResult> CreateIfAbsentAsync(ReadOnlyMemory<byte> bytes,
        long revision, string hash, CancellationToken token) => inner.CreateIfAbsentAsync(bytes, revision, hash, token);
    public async ValueTask<CampaignCheckpointWriteResult> ReplaceIfCurrentAsync(long previousRevision, string previousHash,
        ReadOnlyMemory<byte> bytes, long revision, string hash, CancellationToken token)
    {
        var before = await inner.ReadAsync(token);
        var predecessor = CampaignStateJson.Parse(before.ExactUtf8Json.AsMemory()).Artifact!;
        var successor = CampaignStateJson.Parse(bytes).Artifact!;
        if (successor.State.ActiveReservation is CampaignProviderReservation { HostPhase: CampaignProviderHostPhase.SemanticTool })
            beforeSemanticWrite?.Invoke();
        var actual = await inner.ReplaceIfCurrentAsync(previousRevision, previousHash, bytes, revision, hash, token);
        var selected = select?.Invoke(predecessor, successor) ?? successor.State.ActiveReservation is
            CampaignProviderReservation { HostPhase: CampaignProviderHostPhase.TerminalSubmission };
        if (fault == "clean" || AfterFault is not null || !selected) return actual;
        BeforeFault = predecessor;
        AfterFault = successor;
        switch (fault)
        {
            case "publication": return new(CampaignCheckpointWriteKind.PublicationFailure);
            case "readback": return CampaignCheckpointWriteResult.Written(before);
            case "observer": return new(CampaignCheckpointWriteKind.CurrentMismatch);
            case "cancel": stop.Cancel(); token.ThrowIfCancellationRequested(); break;
        }
        return actual;
    }
}
