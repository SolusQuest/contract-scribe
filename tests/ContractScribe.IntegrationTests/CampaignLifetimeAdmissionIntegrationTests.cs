using System.Collections.Immutable;
using System.Text.Json;
using ContractScribe.Agent.Runtime;
using ContractScribe.Cli;
using ContractScribe.Core;
using ContractScribe.Roslyn;

namespace ContractScribe.IntegrationTests;

public sealed partial class DocumentationScribeEndToEndIntegrationTests
{
    [Theory]
    [InlineData("output", true)]
    [InlineData("output", false)]
    [InlineData("cost", true)]
    [InlineData("cost", false)]
    [InlineData("elapsed-provider", true)]
    [InlineData("elapsed-provider", false)]
    [InlineData("elapsed-host", true)]
    [InlineData("elapsed-host", false)]
    public async Task Campaign_lifetime_admission_denial_after_exact_settlement_preserves_attempt_and_resumes(
        string dimension, bool clear)
    {
        if (!OperatingSystem.IsLinux()) return;
        await VerifyLifetimeAdmissionAsync(dimension, clear, overrun: false);
    }

    [Theory]
    [InlineData("output")]
    [InlineData("cost")]
    [InlineData("elapsed-provider")]
    public async Task Campaign_lifetime_settlement_overrun_preserves_the_same_resource_owner(string dimension)
    {
        if (!OperatingSystem.IsLinux()) return;
        await VerifyLifetimeAdmissionAsync(dimension, clear: true, overrun: true);
    }

    private static async Task VerifyLifetimeAdmissionAsync(string dimension, bool clear, bool overrun)
    {
        await using var fixture = await EndToEndFixture.CreateAsync(additionalSources: AdditionalPatchSources());
        var elapsed = dimension.StartsWith("elapsed", StringComparison.Ordinal);
        var campaign = LifetimeAdmissionCampaign(fixture, dimension, elapsed ? 1000 : 100);
        var store = new PatchMemoryStore(CampaignStateJson.CreateArtifact(campaign.InitialState));
        var clock = new LifetimeAdmissionClock(() => store.Current.State, elapsed ? overrun ? 1001 : 1000 : 0);
        var allowance = StopCauseAllowance(fixture, clock, output: 100);
        var work = campaign.Plan.WorkItems.First(w => w.Disposition.Kind == CampaignPlanningDispositionKind.Executable);
        var request = campaign.Requests[work.Targets[0].SymbolRef];
        var exchange = new LifetimeAdmissionExchange(request.Request, dimension, overrun);
        var credentials = 0;
        var stopped = await DocumentationCampaignProposalExecutor.ExecuteAsync(StopCauseInput(fixture, campaign, store,
            request, allowance, clock, exchange: null, deferred: () => { credentials++; return exchange; }));
        Assert.Equal(DocumentationCampaignProposalOutcomeKind.BudgetExhausted, stopped.Kind);
        Assert.Equal(1, exchange.Sends);
        Assert.Equal(1, credentials);
        Assert.Equal(overrun && !elapsed ? 0 : 1, clock.ToolCompletions);
        Assert.Equal(overrun && !elapsed ? 0 : 1, allowance.ToolCallCount);
        Assert.Null(store.Current.State.ActiveReservation);
        Assert.Equal(CampaignTerminalReason.LifetimeCap, store.Current.State.TerminalOutcome!.Reason);
        var retained = store.Current.State.WorkItems.Single(w => w.WorkItemKey == work.WorkItemKey);
        var paused = Assert.IsType<CampaignPausedProviderAttempt>(retained.PausedProviderAttempt);
        Assert.Null(retained.ClosedOutcome);
        Assert.Null(retained.TrustedProposal);
        Assert.Equal(1, retained.OuterAttemptCount);
        Assert.Equal(1, store.Current.State.LineageCharges.ProviderRequests.Observed);
        Assert.Equal(0, store.Current.State.LineageCharges.ProviderRequests.ConservativeUnobserved);
        Assert.Equal(dimension == "output" ? overrun ? 101 : 100 : 1, store.Current.State.LineageCharges.OutputTokens.Observed);
        Assert.Equal(0, store.Current.State.LineageCharges.OutputTokens.ConservativeUnobserved);
        Assert.Equal(elapsed ? overrun ? 1001 : 1000 : 0, store.Current.State.LineageCharges.ActiveElapsedMilliseconds.Observed);
        Assert.Equal(0, store.Current.State.LineageCharges.ActiveElapsedMilliseconds.ConservativeUnobserved);
        if (dimension == "cost")
        {
            Assert.Equal(overrun ? 101 : 100, store.Current.State.LineageCharges.CostMicrounits.Observed);
            Assert.Equal(0, store.Current.State.LineageCharges.CostMicrounits.ConservativeUnobserved);
            Assert.False(store.Current.State.LineageCharges.HasUnpricedCostHistory);
        }
        var retry = paused.RetryProgress;
        Assert.Equal(1, retry.LastSettledDispatchOrdinal);
        Assert.Equal(CampaignProviderDispatchDisposition.Success, retry.LastDisposition);
        Assert.Equal(0, retry.RetryableFailureCount);

        var before = store.Current.ExactUtf8Json;
        var fencedClock = new LifetimeAdmissionClock(() => store.Current.State, 0);
        await DocumentationCampaignProposalExecutor.ExecuteAsync(StopCauseInput(fixture, campaign, store,
            request, StopCauseAllowance(fixture, fencedClock, output: 100), fencedClock, exchange: null,
            deferred: () => { credentials++; return exchange; }));
        Assert.Equal(1, credentials);
        Assert.Equal(1, exchange.Sends);
        Assert.True(before.AsSpan().SequenceEqual(store.Current.ExactUtf8Json.AsSpan()));

        var budget = campaign.PlanningInput.ExecutionPolicy.CampaignBudget;
        var cap = clear ? (long?)null : elapsed ? 3000 : 10000;
        budget = dimension switch
        {
            "output" => budget with { MaximumOutputTokens = cap },
            "cost" => budget with { MaximumCostMicrounits = cap },
            _ => budget with { MaximumElapsedMilliseconds = cap },
        };
        var planning = campaign.PlanningInput with
        { ExecutionPolicy = campaign.PlanningInput.ExecutionPolicy with { CampaignBudget = budget } };
        var plan = CampaignPlanner.Plan(planning);
        var accepted = await CampaignCheckpointAcceptance.AcceptCurrentAsync(store);
        var refreshed = CampaignStateReducer.RefreshLifetimeCaps(store.Current, accepted.AcceptedCheckpoint!,
            campaign.ExecutionCapability, "style.public-api.v1", campaign.StyleProjection,
            fixture.Session.InputIdentity, planning, plan);
        Assert.Equal(CampaignTransitionKind.Applied, refreshed.Kind);
        Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted,
            (await CampaignCheckpointAcceptance.AcceptAsync(store, refreshed)).Kind);
        campaign = campaign with { PlanningInput = planning, Plan = plan };
        var freshClock = new LifetimeAdmissionClock(() => store.Current.State, 0);
        var resumed = await DocumentationCampaignProposalExecutor.ExecuteAsync(StopCauseInput(fixture, campaign, store,
            request, StopCauseAllowance(fixture, freshClock, output: 100), freshClock, exchange: null,
            deferred: () => { credentials++; return exchange; }));
        Assert.Equal(DocumentationCampaignProposalOutcomeKind.ProposalReady, resumed.Kind);
        Assert.Equal(2, credentials);
        Assert.Equal(2, exchange.Sends);
        var ready = store.Current.State.WorkItems.Single(w => w.WorkItemKey == work.WorkItemKey);
        Assert.Equal(paused.AttemptId, ready.TrustedProposal!.HistoricalAttemptId);
        Assert.Equal(1, ready.OuterAttemptCount);
        Assert.Null(ready.PausedProviderAttempt);
        Assert.Null(store.Current.State.ActiveReservation);
        Assert.Equal(2, store.Current.State.LineageCharges.ProviderRequests.Observed);
        Assert.Equal(0, store.Current.State.LineageCharges.ProviderRequests.ConservativeUnobserved);
        Assert.Equal(elapsed ? overrun ? 1001 : 1000 : 0, store.Current.State.LineageCharges.ActiveElapsedMilliseconds.Observed);
        if (dimension == "cost") Assert.Equal(overrun ? 101 : 100, store.Current.State.LineageCharges.CostMicrounits.Observed);
    }

    [Theory]
    [InlineData("prepare")]
    [InlineData("host-timeout")]
    public async Task Campaign_genuine_non_lifetime_failure_keeps_closed_authority(string cause)
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await EndToEndFixture.CreateAsync(additionalSources: AdditionalPatchSources());
        var campaign = StopCauseCampaign(fixture, null);
        var store = new PatchMemoryStore(CampaignStateJson.CreateArtifact(campaign.InitialState));
        var clock = new CompletionPrecedenceClock(() => store.Current.State)
        { FirePostflightTimer = cause == "host-timeout" };
        var work = campaign.Plan.WorkItems.First(w => w.Disposition.Kind == CampaignPlanningDispositionKind.Executable);
        var request = campaign.Requests[work.Targets[0].SymbolRef];
        var sends = 0;
        var exchange = cause == "prepare"
            ? (IDocumentationScribeModelExchange)new LifetimePreparationFailureExchange(() => sends++)
            : new StopCauseExchange(request.Request, () => sends++);
        await DocumentationCampaignProposalExecutor.ExecuteAsync(StopCauseInput(fixture, campaign, store, request,
            StopCauseAllowance(fixture, clock, hostElapsed: 500), clock, exchange));
        var closed = store.Current.State.WorkItems.Single(w => w.WorkItemKey == work.WorkItemKey);
        Assert.Equal(CampaignWorkStatus.Closed, closed.Status);
        Assert.Null(closed.PausedProviderAttempt);
        Assert.Equal(cause == "prepare" ? CampaignWorkOutcomeCode.ValidationFailure : CampaignWorkOutcomeCode.Timeout,
            closed.ClosedOutcome!.Code);
        Assert.Null(store.Current.State.ActiveReservation);
        Assert.Equal(1, sends);
        if (cause == "host-timeout") Assert.Equal(CampaignTerminalReason.Deadline, store.Current.State.TerminalOutcome!.Reason);
        campaign = await ChangeStopCauseCapAsync(fixture, campaign, store, 2000);
        var before = store.Current.ExactUtf8Json;
        await DocumentationCampaignProposalExecutor.ExecuteAsync(StopCauseInput(fixture, campaign, store, request,
            StopCauseAllowance(fixture, clock), clock, exchange));
        Assert.Equal(1, sends);
        Assert.True(before.AsSpan().SequenceEqual(store.Current.ExactUtf8Json.AsSpan()));
    }

    private static PatchCampaign LifetimeAdmissionCampaign(EndToEndFixture fixture, string dimension, long cap)
    {
        var original = StopCauseCampaign(fixture, dimension.StartsWith("elapsed", StringComparison.Ordinal) ? cap : null,
            dimension == "output" ? cap : null);
        if (dimension != "cost") return original;
        var b = original.PlanningInput.ExecutionPolicy.CampaignBudget;
        var rates = new CampaignCostRates(0, 0, 1_000_000, 1_000_000);
        var budget = new CampaignPlanningBudgetPolicy(b.MaximumBlocks, b.MaximumChangedFiles, b.MaximumPatchBytes,
            b.MaximumProviderRequests, b.MaximumAttemptsPerTarget, b.MaximumInputTokens, b.MaximumUncachedInputTokens,
            b.MaximumOutputTokens, cap, b.MaximumElapsedMilliseconds, b.MaximumCandidatesPerBlock, true,
            "currency.synthetic", rates.CreateAuthority("currency.synthetic", "rate.synthetic"), rates);
        var planning = original.PlanningInput with
        { ExecutionPolicy = original.PlanningInput.ExecutionPolicy with { CampaignBudget = budget } };
        var plan = CampaignPlanner.Plan(planning);
        var state = CampaignStateFactory.CreateInitial("style.public-api.v1", original.StyleProjection,
            original.ExecutionCapability, fixture.Session.InputIdentity, planning, plan);
        return original with { PlanningInput = planning, Plan = plan, InitialState = state };
    }

    private sealed class LifetimeAdmissionExchange(DocumentationScribeRequest request, string dimension, bool overrun)
        : IDocumentationScribeModelExchange
    {
        internal int Sends { get; private set; }
        public ValueTask<DocumentationScribeModelResponse> SendAsync(DocumentationScribeModelRequest modelRequest,
            CancellationToken cancellationToken)
        {
            Sends++;
            if (Sends > 1) return ValueTask.FromResult(new DocumentationScribeModelResponse([],
                [new(CampaignProposalTerminal(request))], usage: new(0, 0, 0, 0),
                cost: dimension == "cost" ? new("currency.synthetic", 0) : null));
            var calls = ImmutableArray.CreateBuilder<DocumentationScribeModelToolCall>();
            var count = dimension == "elapsed-host" ? 2 : 1;
            for (var index = 0; index < count; index++)
                calls.Add(new(index, "call.admission." + index, DocumentationScribeSemanticToolSelection.OperationId,
                    JsonSerializer.SerializeToUtf8Bytes(new { pageSize = 1 })));
            return ValueTask.FromResult(new DocumentationScribeModelResponse(calls.ToImmutable(), [],
                usage: new(0, dimension == "output" ? overrun ? 101 : 100 : 1, 0, 0),
                cost: dimension == "cost" ? new("currency.synthetic", overrun ? 101 : 100) : null));
        }
    }

    private sealed class LifetimePreparationFailureExchange(Action onSend) : IDocumentationScribeModelExchange
    {
        public ValueTask<DocumentationScribeModelResponse> SendAsync(DocumentationScribeModelRequest modelRequest,
            CancellationToken cancellationToken)
        {
            onSend();
            return ValueTask.FromResult(new DocumentationScribeModelResponse([], [],
                new(DocumentationScribeModelFailureCode.Unsupported, origin: DocumentationScribeModelFailureOrigin.RequestPreparation),
                usage: new(0, 0, 0, 0)));
        }
    }

    private sealed class LifetimeAdmissionClock(Func<CampaignCheckpointState> current, int elapsed) : TimeProvider
    {
        private long timestamp;
        internal int ToolCompletions { get; private set; }
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => timestamp;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            current().ActiveReservation is CampaignProviderReservation { HostPhase: CampaignProviderHostPhase.SemanticTool } claim
                && dueTime.TotalMilliseconds == claim.Exposure.ElapsedMilliseconds
                ? new LifetimeToolCompletionTimer(() => { ToolCompletions++; if (ToolCompletions == 1) timestamp += elapsed; })
                : new NoopTimer();
    }

    private sealed class LifetimeToolCompletionTimer(Action onDispose) : ITimer
    {
        private int disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) onDispose(); }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
