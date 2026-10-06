using ContractScribe.Agent.Runtime;
using ContractScribe.Cli;
using ContractScribe.Core;

namespace ContractScribe.IntegrationTests;

public sealed partial class DocumentationScribeEndToEndIntegrationTests
{
    [Theory]
    [InlineData("stale", "lifetime", true)]
    [InlineData("stale", "lifetime", false)]
    [InlineData("stale", "lifetime-over", true)]
    [InlineData("stale", "lifetime-over", false)]
    [InlineData("budget", "lifetime", true)]
    [InlineData("budget", "lifetime", false)]
    [InlineData("timeout", "lifetime", true)]
    [InlineData("host", "lifetime", true)]
    [InlineData("caller", "lifetime", true)]
    [InlineData("stale", "action", true)]
    [InlineData("budget", "action", true)]
    [InlineData("timeout", "action", true)]
    [InlineData("host", "action", true)]
    public async Task Campaign_registered_postflight_completion_keeps_closed_fact_when_resource_also_stops(
        string completion, string resource, bool clear)
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await EndToEndFixture.CreateAsync(additionalSources: AdditionalPatchSources());
        var lifetime = resource.StartsWith("lifetime", StringComparison.Ordinal);
        var elapsed = resource == "lifetime-over" ? 1001 : 1000;
        var campaign = StopCauseCampaign(fixture, lifetime ? 1000 : null);
        var store = new PatchMemoryStore(CampaignStateJson.CreateArtifact(campaign.InitialState));
        var clock = new CompletionPrecedenceClock(() => store.Current.State)
        {
            PostflightElapsed = completion == "timeout" ? 0 : elapsed,
            PostflightHostFault = completion == "host",
            FirePostflightTimer = completion == "timeout",
        };
        var allowance = StopCauseAllowance(fixture, clock, elapsed: lifetime ? 900000 : 1000,
            hostElapsed: completion == "timeout" ? 500 : null);
        var work = campaign.Plan.WorkItems.First(w => w.Disposition.Kind == CampaignPlanningDispositionKind.Executable);
        var request = campaign.Requests[work.Targets[0].SymbolRef];
        var source = Path.Join(fixture.Root, ((RepositoryEvidenceLocator)request.Request.Target.SourceLocator).Path);
        var original = await File.ReadAllBytesAsync(source);
        using var caller = new CancellationTokenSource();
        clock.OnPostflight = () =>
        {
            if (completion == "budget")
            {
                using var file = File.OpenWrite(source);
                file.SetLength(DocumentationScribeContextValidation.CreateProductionLimits().MaximumSourceFileUtf8Bytes + 1L);
            }
            if (completion == "stale") File.AppendAllText(source, "\n// stale after admitted postflight\n");
            if (completion == "caller") caller.Cancel();
            if (!lifetime && completion != "timeout") Assert.False(allowance.CanBeginProviderWork());
        };
        clock.OnPostflightTimer = () => { if (!lifetime) Assert.False(allowance.CanBeginProviderWork()); };
        var sends = 0;
        var credentials = 0;
        var exchange = new StopCauseExchange(request.Request, () => sends++);
        var result = await DocumentationCampaignProposalExecutor.ExecuteAsync(StopCauseInput(fixture, campaign, store,
            request, allowance, clock, exchange: null, deferred: () => { credentials++; return exchange; }, cancellationToken: caller.Token));
        Assert.True(clock.SawPostflight);
        Assert.Equal(1, sends);
        Assert.Equal(1, credentials);
        Assert.Null(store.Current.State.ActiveReservation);
        Assert.Equal(1, store.Current.State.LineageCharges.ProviderRequests.Observed);
        Assert.Equal(0, store.Current.State.LineageCharges.ProviderRequests.ConservativeUnobserved);
        Assert.Equal(1, store.Current.State.LineageCharges.OutputTokens.Observed);
        Assert.Equal(0, store.Current.State.LineageCharges.OutputTokens.ConservativeUnobserved);
        Assert.Equal(elapsed, store.Current.State.LineageCharges.ActiveElapsedMilliseconds.Observed);
        Assert.Equal(0, store.Current.State.LineageCharges.ActiveElapsedMilliseconds.ConservativeUnobserved);
        var closed = store.Current.State.WorkItems.Single(w => w.WorkItemKey == work.WorkItemKey);
        Assert.Equal(CampaignWorkStatus.Closed, closed.Status);
        Assert.Null(closed.PausedProviderAttempt);
        Assert.Null(closed.TrustedProposal);
        Assert.Equal(completion switch
        {
            "stale" => CampaignWorkOutcomeCode.ValidationFailure,
            "budget" => CampaignWorkOutcomeCode.BudgetExhausted,
            "timeout" => CampaignWorkOutcomeCode.Timeout,
            "host" => CampaignWorkOutcomeCode.InternalFailure,
            _ => CampaignWorkOutcomeCode.CancelledByCaller,
        }, closed.ClosedOutcome!.Code);
        if (completion != "stale" || resource == "lifetime-over")
        {
            Assert.Equal(completion switch
            {
                "stale" => CampaignTerminalReason.LifetimeCap,
                "budget" => CampaignTerminalReason.Budget,
                "timeout" => CampaignTerminalReason.Deadline,
                "host" => CampaignTerminalReason.Host,
                _ => CampaignTerminalReason.Caller,
            }, store.Current.State.TerminalOutcome!.Reason);
        }
        else
        {
            // Core accepts exact equality; a typed closed failure is retained while
            // the spent lifetime still fences a different target before credentials.
            Assert.Null(store.Current.State.TerminalOutcome);
            if (lifetime)
            {
                var next = campaign.Plan.WorkItems.First(w => w.Disposition.Kind == CampaignPlanningDispositionKind.Executable
                    && w.WorkItemKey != work.WorkItemKey);
                var nextRequest = campaign.Requests[next.Targets[0].SymbolRef];
                var nextClock = new CompletionPrecedenceClock(() => store.Current.State);
                var fenced = await DocumentationCampaignProposalExecutor.ExecuteAsync(StopCauseInput(fixture, campaign, store,
                    nextRequest, StopCauseAllowance(fixture, nextClock), nextClock, exchange: null,
                    deferred: () => { credentials++; return exchange; }));
                Assert.Equal(DocumentationCampaignProposalOutcomeKind.BudgetExhausted, fenced.Kind);
                Assert.Equal(1, sends);
                Assert.Equal(1, credentials);
            }
        }
        Assert.NotEqual(DocumentationCampaignProposalOutcomeKind.HostContractError, result.Kind);
        await File.WriteAllBytesAsync(source, original);
        campaign = await ChangeStopCauseCapAsync(fixture, campaign, store, clear ? null : 2000);
        var before = store.Current.ExactUtf8Json;
        var freshClock = new CompletionPrecedenceClock(() => store.Current.State);
        await DocumentationCampaignProposalExecutor.ExecuteAsync(StopCauseInput(fixture, campaign, store,
            request, StopCauseAllowance(fixture, freshClock), freshClock, exchange: null,
            deferred: () => { credentials++; return exchange; }));
        Assert.Equal(1, sends);
        Assert.Equal(1, credentials);
        Assert.True(before.AsSpan().SequenceEqual(store.Current.ExactUtf8Json.AsSpan()));
        Assert.Equal(CampaignWorkStatus.Closed, store.Current.State.WorkItems.Single(w => w.WorkItemKey == work.WorkItemKey).Status);
    }

    [Theory]
    [InlineData("runtime")]
    [InlineData("postflight")]
    public async Task Campaign_resource_induced_budget_before_authoritative_completion_keeps_same_attempt(string boundary)
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await EndToEndFixture.CreateAsync(additionalSources: AdditionalPatchSources());
        var campaign = StopCauseCampaign(fixture, null);
        var store = new PatchMemoryStore(CampaignStateJson.CreateArtifact(campaign.InitialState));
        var clock = new CompletionPrecedenceClock(() => store.Current.State)
        { PostflightElapsed = boundary == "postflight" ? 1000 : 0 };
        var allowance = StopCauseAllowance(fixture, clock, elapsed: 1000);
        var work = campaign.Plan.WorkItems.First(w => w.Disposition.Kind == CampaignPlanningDispositionKind.Executable);
        var request = campaign.Requests[work.Targets[0].SymbolRef];
        var sends = 0;
        var exchange = new StopCauseExchange(request.Request, () =>
        { sends++; if (boundary == "runtime") clock.Advance(1000); });
        var stopped = await DocumentationCampaignProposalExecutor.ExecuteAsync(StopCauseInput(fixture, campaign, store,
            request, allowance, clock, exchange));
        Assert.Equal(DocumentationCampaignProposalOutcomeKind.InvocationBudgetExhausted, stopped.Kind);
        Assert.Equal(boundary == "postflight", clock.SawPostflight);
        Assert.Null(store.Current.State.TerminalOutcome);
        Assert.Null(store.Current.State.ActiveReservation);
        var paused = Assert.IsType<CampaignPausedProviderAttempt>(store.Current.State.WorkItems.Single(w => w.WorkItemKey == work.WorkItemKey).PausedProviderAttempt);
        Assert.Equal(1, store.Current.State.LineageCharges.ProviderRequests.Observed);
        Assert.Equal(1000, store.Current.State.LineageCharges.ActiveElapsedMilliseconds.Observed);
        var freshClock = new CompletionPrecedenceClock(() => store.Current.State);
        var resumed = await DocumentationCampaignProposalExecutor.ExecuteAsync(StopCauseInput(fixture, campaign, store,
            request, StopCauseAllowance(fixture, freshClock), freshClock, exchange));
        Assert.Equal(DocumentationCampaignProposalOutcomeKind.ProposalReady, resumed.Kind);
        Assert.Equal(paused.AttemptId, store.Current.State.WorkItems.Single(w => w.WorkItemKey == work.WorkItemKey).TrustedProposal!.HistoricalAttemptId);
        Assert.Equal(2, sends);
        Assert.Equal(2, store.Current.State.LineageCharges.ProviderRequests.Observed);
        Assert.Equal(1000, store.Current.State.LineageCharges.ActiveElapsedMilliseconds.Observed);
    }

    private sealed class CompletionPrecedenceClock(Func<CampaignCheckpointState> current) : TimeProvider
    {
        private long timestamp;
        private bool postflightStarted;
        internal int PostflightElapsed { get; init; }
        internal bool PostflightHostFault { get; init; }
        internal bool FirePostflightTimer { get; set; }
        internal Action? OnPostflight { get; set; }
        internal Action? OnPostflightTimer { get; set; }
        internal bool SawPostflight { get; private set; }
        public override long TimestampFrequency => 1000;
        internal void Advance(int value) => timestamp += value;
        public override long GetTimestamp()
        {
            if (!postflightStarted && current().ActiveReservation is CampaignProviderReservation { HostPhase: CampaignProviderHostPhase.Postflight })
            {
                postflightStarted = true;
                SawPostflight = true;
                timestamp += PostflightElapsed;
                OnPostflight?.Invoke();
                if (PostflightHostFault) throw new InvalidOperationException("synthetic postflight clock failure");
            }
            return timestamp;
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (FirePostflightTimer && current().ActiveReservation is CampaignProviderReservation { HostPhase: CampaignProviderHostPhase.Postflight })
            {
                FirePostflightTimer = false;
                timestamp += 1000;
                OnPostflightTimer?.Invoke();
                callback(state);
            }
            return new NoopTimer();
        }
    }
}
