using ContractScribe.Agent.Runtime;
using ContractScribe.Cli;
using ContractScribe.Core;

namespace ContractScribe.IntegrationTests;

public sealed partial class DocumentationScribeEndToEndIntegrationTests
{
    [Theory]
    [InlineData("safety")]
    [InlineData("action")]
    [InlineData("lifetime")]
    [InlineData("caller")]
    [InlineData("deadline")]
    [InlineData("action-caller")]
    [InlineData("lifetime-caller")]
    public async Task Campaign_zero_send_preflight_stops_keep_their_authoritative_owner(string owner)
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await EndToEndFixture.CreateAsync(additionalSources: AdditionalPatchSources());
        var campaign = StopCauseCampaign(fixture, owner.StartsWith("lifetime", StringComparison.Ordinal) ? 1000 : null);
        var store = new PatchMemoryStore(CampaignStateJson.CreateArtifact(campaign.InitialState));
        var clock = new StopCauseClock(() => store.Current.State);
        var allowance = StopCauseAllowance(fixture, clock, elapsed: owner.StartsWith("action", StringComparison.Ordinal) ? 1000 : 900000);
        var work = campaign.Plan.WorkItems.First(w => w.Disposition.Kind == CampaignPlanningDispositionKind.Executable);
        var request = campaign.Requests[work.Targets[0].SymbolRef];
        var source = Path.Join(fixture.Root, ((RepositoryEvidenceLocator)request.Request.Target.SourceLocator).Path);
        var original = await File.ReadAllBytesAsync(source);
        var sends = 0;
        var credentialReads = 0;
        using var cancelled = new CancellationTokenSource();
        var mutated = false;
        bool BeforePreparation()
        {
            if (!mutated)
            {
                mutated = true;
                if (owner is "safety" or "action" or "lifetime")
                {
                    using var file = File.OpenWrite(source);
                    file.SetLength(DocumentationScribeContextValidation.CreateProductionLimits().MaximumSourceFileUtf8Bytes + 1L);
                }
                if (owner.StartsWith("action", StringComparison.Ordinal) || owner.StartsWith("lifetime", StringComparison.Ordinal)) clock.Advance(1000);
                if (owner.StartsWith("action", StringComparison.Ordinal)) Assert.False(allowance.CanBeginProviderWork());
                if (owner.EndsWith("caller", StringComparison.Ordinal)) cancelled.Cancel();
                if (owner == "deadline") clock.FireNextTimer = true;
            }
            return true;
        }
        var exchange = new StopCauseExchange(request.Request, () => sends++);
        var result = await DocumentationCampaignProposalExecutor.ExecuteAsync(StopCauseInput(fixture, campaign, store,
            request, allowance, clock, exchange: null, deferred: () => { credentialReads++; return exchange; },
            guard: BeforePreparation, cancellationToken: cancelled.Token));
        Assert.Equal(0, sends);
        Assert.Equal(1, credentialReads);
        Assert.Null(store.Current.State.ActiveReservation);
        Assert.Equal(0, store.Current.State.LineageCharges.ProviderRequests.TotalCharged);
        Assert.Equal(0, store.Current.State.LineageCharges.InputTokens.TotalCharged);
        Assert.Equal(0, store.Current.State.LineageCharges.OutputTokens.TotalCharged);
        var paused = store.Current.State.WorkItems.Single(w => w.WorkItemKey == work.WorkItemKey).PausedProviderAttempt;
        if (owner == "action")
        {
            Assert.Equal(DocumentationCampaignProposalOutcomeKind.InvocationBudgetExhausted, result.Kind);
            Assert.Null(store.Current.State.TerminalOutcome);
            Assert.NotNull(paused);
        }
        else
        {
            var expected = owner switch
            {
                "lifetime" => CampaignTerminalReason.LifetimeCap,
                "caller" or "action-caller" or "lifetime-caller" => CampaignTerminalReason.Caller,
                "deadline" => CampaignTerminalReason.Deadline,
                _ => CampaignTerminalReason.Budget,
            };
            Assert.Equal(expected, store.Current.State.TerminalOutcome!.Reason);
            Assert.Equal(owner == "lifetime", paused is not null);
        }
        await File.WriteAllBytesAsync(source, original);
        if (owner is "action" or "lifetime")
        {
            var attempt = paused!.AttemptId;
            campaign = await ChangeStopCauseCapAsync(fixture, campaign, store, null);
            var freshClock = new StopCauseClock(() => store.Current.State);
            var resumed = await DocumentationCampaignProposalExecutor.ExecuteAsync(StopCauseInput(fixture, campaign, store,
                request, StopCauseAllowance(fixture, freshClock), freshClock, exchange));
            Assert.Equal(DocumentationCampaignProposalOutcomeKind.ProposalReady, resumed.Kind);
            Assert.Equal(attempt, store.Current.State.WorkItems.Single(w => w.WorkItemKey == work.WorkItemKey).TrustedProposal!.HistoricalAttemptId);
            Assert.Equal(1, sends);
            Assert.Equal(1, store.Current.State.LineageCharges.ProviderRequests.Observed);
        }
        else
        {
            campaign = await ChangeStopCauseCapAsync(fixture, campaign, store, 2000);
            Assert.Equal(owner == "safety" ? CampaignTerminalReason.Budget : owner.EndsWith("caller", StringComparison.Ordinal) ? CampaignTerminalReason.Caller : CampaignTerminalReason.Deadline,
                store.Current.State.TerminalOutcome!.Reason);
            Assert.Equal(0, sends);
        }
    }

    [Theory]
    [InlineData("elapsed", true)]
    [InlineData("elapsed", false)]
    [InlineData("remaining", true)]
    [InlineData("elapsed-postflight", true)]
    [InlineData("elapsed-postflight", false)]
    [InlineData("elapsed-deadline", true)]
    [InlineData("host-deadline", true)]
    [InlineData("postflight-budget", true)]
    [InlineData("postflight-stale", true)]
    [InlineData("nonelapsed-budget", true)]
    [InlineData("nonelapsed-stale", true)]
    [InlineData("nonelapsed-proposal", true)]
    public async Task Campaign_returned_proposal_requires_authorized_postflight_and_preserves_stop_cause(string cause, bool clear)
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await EndToEndFixture.CreateAsync(additionalSources: AdditionalPatchSources());
        var nonelapsed = cause.StartsWith("nonelapsed", StringComparison.Ordinal);
        var campaign = StopCauseCampaign(fixture, nonelapsed || cause == "host-deadline" ? null : 1000, nonelapsed ? 7 : null);
        var store = new PatchMemoryStore(CampaignStateJson.CreateArtifact(campaign.InitialState));
        var clock = new StopCauseClock(() => store.Current.State)
        {
            TerminalElapsed = cause == "elapsed" ? 1000 : cause == "remaining" ? 999 : 0,
            PostflightElapsed = cause == "elapsed-postflight" ? 1000 : 0,
            FirePostflightTimer = cause.EndsWith("deadline", StringComparison.Ordinal)
        };
        var allowance = StopCauseAllowance(fixture, clock, output: nonelapsed ? 7 : null);
        var work = campaign.Plan.WorkItems.First(w => w.Disposition.Kind == CampaignPlanningDispositionKind.Executable);
        var request = campaign.Requests[work.Targets[0].SymbolRef];
        var source = Path.Join(fixture.Root, ((RepositoryEvidenceLocator)request.Request.Target.SourceLocator).Path);
        var original = await File.ReadAllBytesAsync(source);
        var sends = 0;
        var exchange = new StopCauseExchange(request.Request, () =>
        {
            sends++;
            if (cause.EndsWith("budget", StringComparison.Ordinal))
            {
                using var file = File.OpenWrite(source);
                file.SetLength(DocumentationScribeContextValidation.CreateProductionLimits().MaximumSourceFileUtf8Bytes + 1L);
            }
            if (cause.EndsWith("stale", StringComparison.Ordinal)) File.AppendAllText(source, "\n// changed after dispatch\n");
        }, nonelapsed ? 8 : 1);
        var result = await DocumentationCampaignProposalExecutor.ExecuteAsync(StopCauseInput(fixture, campaign, store,
            request, allowance, clock, exchange));
        Assert.Null(store.Current.State.ActiveReservation);
        Assert.Equal(1, sends);
        Assert.Equal(1, store.Current.State.LineageCharges.ProviderRequests.Observed);
        Assert.Equal(nonelapsed ? 8 : 1, store.Current.State.LineageCharges.OutputTokens.Observed);
        var finalWork = store.Current.State.WorkItems.Single(w => w.WorkItemKey == work.WorkItemKey);
        if (cause.StartsWith("elapsed", StringComparison.Ordinal))
        {
            Assert.Equal(cause != "elapsed", clock.SawPostflight);
            Assert.Equal(CampaignTerminalReason.LifetimeCap, store.Current.State.TerminalOutcome!.Reason);
            Assert.Equal(1000, store.Current.State.LineageCharges.ActiveElapsedMilliseconds.Observed);
            Assert.Equal(0, store.Current.State.LineageCharges.ActiveElapsedMilliseconds.ConservativeUnobserved);
            Assert.Null(finalWork.TrustedProposal);
            var paused = Assert.IsType<CampaignPausedProviderAttempt>(finalWork.PausedProviderAttempt);
            campaign = await ChangeStopCauseCapAsync(fixture, campaign, store, clear ? null : 2000);
            Assert.Null(store.Current.State.TerminalOutcome);
            var freshClock = new StopCauseClock(() => store.Current.State);
            var resumed = await DocumentationCampaignProposalExecutor.ExecuteAsync(StopCauseInput(fixture, campaign, store,
                request, StopCauseAllowance(fixture, freshClock), freshClock,
                new StopCauseExchange(request.Request, () => sends++)));
            Assert.Equal(DocumentationCampaignProposalOutcomeKind.ProposalReady, resumed.Kind);
            Assert.Equal(paused.AttemptId, store.Current.State.WorkItems.Single(w => w.WorkItemKey == work.WorkItemKey).TrustedProposal!.HistoricalAttemptId);
            Assert.Equal(2, sends);
            Assert.Equal(2, store.Current.State.LineageCharges.ProviderRequests.Observed);
        }
        else
        {
            Assert.True(clock.SawPostflight);
            if (cause == "host-deadline")
            {
                Assert.Equal(CampaignTerminalReason.Deadline, store.Current.State.TerminalOutcome!.Reason);
                Assert.Equal(CampaignWorkOutcomeCode.Timeout, finalWork.ClosedOutcome!.Code);
            }
            else if (cause.EndsWith("budget", StringComparison.Ordinal))
            {
                Assert.Equal(CampaignTerminalReason.Budget, store.Current.State.TerminalOutcome!.Reason);
                Assert.Equal(CampaignWorkOutcomeCode.BudgetExhausted, finalWork.ClosedOutcome!.Code);
            }
            else if (cause.EndsWith("stale", StringComparison.Ordinal))
            {
                Assert.Equal(CampaignWorkOutcomeCode.ValidationFailure, finalWork.ClosedOutcome!.Code);
                Assert.Null(finalWork.TrustedProposal);
            }
            else
            {
                Assert.Equal(DocumentationCampaignProposalOutcomeKind.ProposalReady, result.Kind);
                Assert.NotNull(finalWork.TrustedProposal);
                if (nonelapsed) Assert.Equal(CampaignTerminalReason.LifetimeCap, store.Current.State.TerminalOutcome!.Reason);
                else Assert.Equal(999, store.Current.State.LineageCharges.ActiveElapsedMilliseconds.Observed);
            }
        }
        await File.WriteAllBytesAsync(source, original);
        if (cause.EndsWith("budget", StringComparison.Ordinal) || cause is "remaining" or "nonelapsed-proposal")
        {
            campaign = await ChangeStopCauseCapAsync(fixture, campaign, store, null, clearOutput: true);
            var beforeReplay = store.Current.ExactUtf8Json;
            var freshClock = new StopCauseClock(() => store.Current.State);
            var replay = await DocumentationCampaignProposalExecutor.ExecuteAsync(StopCauseInput(fixture, campaign, store,
                request, StopCauseAllowance(fixture, freshClock), freshClock, exchange));
            Assert.Equal(cause.EndsWith("budget", StringComparison.Ordinal)
                ? DocumentationCampaignProposalOutcomeKind.BudgetExhausted : DocumentationCampaignProposalOutcomeKind.ProposalReady, replay.Kind);
            Assert.Equal(1, sends);
            Assert.True(beforeReplay.AsSpan().SequenceEqual(store.Current.ExactUtf8Json.AsSpan()));
            if (cause.EndsWith("budget", StringComparison.Ordinal))
                Assert.Equal(CampaignTerminalReason.Budget, store.Current.State.TerminalOutcome!.Reason);
        }
    }

    private static DocumentationScribeInvocationAllowance StopCauseAllowance(EndToEndFixture fixture, TimeProvider clock,
        int elapsed = 900000, int? output = null, int? hostElapsed = null) => new(DocumentationScribeInvocationLimits.Create(
            maximumProviderRequests: fixture.Request.Limits.MaximumProviderRequests,
            maximumToolCalls: fixture.Request.Limits.MaximumToolCalls,
            maximumCachedInputTokens: fixture.Request.Limits.MaximumInputTokens - fixture.Request.Limits.MaximumUncachedInputTokens,
            maximumUncachedInputTokens: fixture.Request.Limits.MaximumUncachedInputTokens,
            maximumOutputTokens: fixture.Request.Limits.MaximumOutputTokens,
            maximumRequestOutputTokens: output ?? fixture.Request.Limits.MaximumOutputTokens,
            maximumElapsedMilliseconds: elapsed,
            maximumRequestElapsedMilliseconds: hostElapsed ?? fixture.Request.Limits.MaximumElapsedMilliseconds), clock);

    private static PatchCampaign StopCauseCampaign(EndToEndFixture fixture, long? elapsed, long? output = null)
    {
        var original = CreatePatchCampaign(fixture);
        var planning = original.PlanningInput with
        {
            ExecutionPolicy = original.PlanningInput.ExecutionPolicy with
            {
                CampaignBudget = original.PlanningInput.ExecutionPolicy.CampaignBudget with
                { MaximumElapsedMilliseconds = elapsed, MaximumOutputTokens = output }
            }
        };
        var plan = CampaignPlanner.Plan(planning);
        var state = CampaignStateFactory.CreateInitial("style.public-api.v1", original.StyleProjection,
            original.ExecutionCapability, fixture.Session.InputIdentity, planning, plan);
        return original with { PlanningInput = planning, Plan = plan, InitialState = state };
    }

    private static async Task<PatchCampaign> ChangeStopCauseCapAsync(EndToEndFixture fixture, PatchCampaign campaign,
        PatchMemoryStore store, long? elapsed, bool clearOutput = false)
    {
        var planning = campaign.PlanningInput with
        {
            ExecutionPolicy = campaign.PlanningInput.ExecutionPolicy with
            {
                CampaignBudget = campaign.PlanningInput.ExecutionPolicy.CampaignBudget with
                { MaximumElapsedMilliseconds = elapsed, MaximumOutputTokens = clearOutput ? null : campaign.PlanningInput.ExecutionPolicy.CampaignBudget.MaximumOutputTokens }
            }
        };
        var plan = CampaignPlanner.Plan(planning);
        var accepted = await CampaignCheckpointAcceptance.AcceptCurrentAsync(store);
        var refresh = CampaignStateReducer.RefreshLifetimeCaps(store.Current, accepted.AcceptedCheckpoint!,
            campaign.ExecutionCapability, "style.public-api.v1", campaign.StyleProjection,
            fixture.Session.InputIdentity, planning, plan);
        Assert.True(refresh.Kind is CampaignTransitionKind.Applied or CampaignTransitionKind.Unchanged);
        if (refresh.Kind == CampaignTransitionKind.Applied) Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted,
            (await CampaignCheckpointAcceptance.AcceptAsync(store, refresh)).Kind);
        return campaign with { PlanningInput = planning, Plan = plan };
    }

    private static DocumentationCampaignProposalInput StopCauseInput(EndToEndFixture fixture, PatchCampaign campaign,
        PatchMemoryStore store, PatchCampaignRequest request, DocumentationScribeInvocationAllowance allowance,
        TimeProvider clock, IDocumentationScribeModelExchange? exchange, Func<IDocumentationScribeModelExchange?>? deferred = null,
        Func<bool>? guard = null, CancellationToken cancellationToken = default) =>
        new(fixture.Classified, fixture.Observations, campaign.Policy, campaign.AuditInputs, campaign.Audit,
            campaign.PlanningInput, campaign.Plan, campaign.ExecutionCapability, "style.public-api.v1", campaign.StyleProjection,
            request.Bytes, store, new("provider.synthetic.v1", "model.synthetic.v1", "scribe-protocol.v1"), exchange, null,
            cancellationToken, CancellationToken.None, TimeProvider: clock, DeferredExchange: deferred, DispatchGuard: guard,
            InvocationAllowance: allowance);

    private sealed class StopCauseExchange(DocumentationScribeRequest request, Action onSend, int output = 1)
        : IDocumentationScribeModelExchange
    {
        public ValueTask<DocumentationScribeModelResponse> SendAsync(DocumentationScribeModelRequest modelRequest,
            CancellationToken cancellationToken)
        {
            onSend();
            return ValueTask.FromResult(new DocumentationScribeModelResponse([],
                [new(CampaignProposalTerminal(request))], usage: new(0, output, 0, 0)));
        }
    }

    private sealed class StopCauseClock(Func<CampaignCheckpointState> current) : TimeProvider
    {
        private long timestamp;
        private int terminalReads;
        internal int TerminalElapsed { get; init; }
        internal int PostflightElapsed { get; init; }
        private bool advancedPostflight;
        internal bool FireNextTimer { get; set; }
        internal bool FirePostflightTimer { get; set; }
        internal bool SawPostflight { get; private set; }
        public override long TimestampFrequency => 1000;
        internal void Advance(int value) => timestamp += value;
        public override long GetTimestamp()
        {
            if (current().ActiveReservation is CampaignProviderReservation claim)
            {
                SawPostflight |= claim.HostPhase == CampaignProviderHostPhase.Postflight;
                if (claim.HostPhase == CampaignProviderHostPhase.Postflight && !advancedPostflight)
                { advancedPostflight = true; timestamp += PostflightElapsed; }
                // Advance at the mandatory terminal host settlement, after terminal validation has returned.
                if (claim.HostPhase == CampaignProviderHostPhase.TerminalSubmission && ++terminalReads == 5)
                    timestamp += TerminalElapsed;
            }
            return timestamp;
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (FirePostflightTimer && current().ActiveReservation is CampaignProviderReservation { HostPhase: CampaignProviderHostPhase.Postflight })
            { FirePostflightTimer = false; timestamp += (long)dueTime.TotalMilliseconds; callback(state); }
            if (FireNextTimer) { FireNextTimer = false; callback(state); }
            return new NoopTimer();
        }
    }
}

