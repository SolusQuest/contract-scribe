using ContractScribe.Core;

namespace ContractScribe.Cli;

internal sealed class CampaignScribeExecutionCoordinator : IDocumentationScribeExecutionOperations
{
    private readonly CampaignProviderInvocationAuthority invocation;
    private readonly ICampaignCheckpointStore store;
    private readonly TimeProvider clock;
    private readonly CancellationToken settlementToken;
    private readonly Func<bool>? guard;
    private long operationStartedAt;
    internal CampaignScribeExecutionCoordinator(CampaignProviderInvocationAuthority invocation,
        DocumentationScribeInvocationAllowance allowance, ICampaignCheckpointStore store,
        CancellationToken settlementToken, Func<bool>? guard, long operationStartedAt)
    {
        this.invocation = invocation; this.store = store; this.settlementToken = settlementToken;
        this.guard = guard; clock = allowance.Clock; this.operationStartedAt = operationStartedAt;
        Scope = DocumentationScribeExecutionScope.ForCampaign(invocation, allowance, this);
    }
    internal DocumentationScribeExecutionScope Scope { get; }
    internal bool LifetimeStop { get; private set; }
    internal bool Conflict { get; private set; }
    internal bool LifetimeElapsedExhausted => CampaignBudgetAccounting.RemainingLifetimeElapsed(
        invocation.AcceptedCheckpoint.Artifact.State.LineageCharges,
        invocation.AcceptedCheckpoint.Artifact.State.ConfiguredCeilings.CampaignBudget) <= CurrentElapsed;
    internal int CurrentElapsed => ElapsedAt(clock.GetTimestamp());
    private int ElapsedAt(long boundary) => checked((int)Math.Ceiling(clock.GetElapsedTime(operationStartedAt, boundary).TotalMilliseconds));
    internal int CurrentRemaining => invocation.AcceptedCheckpoint.Artifact.State.ActiveReservation is CampaignProviderReservation claim
        ? Math.Max(0, claim.Exposure.ElapsedMilliseconds - CurrentElapsed) : 0;
    internal DocumentationScribeDeadlineOwner CurrentDeadlineOwner
    {
        get
        {
            var state = invocation.AcceptedCheckpoint.Artifact.State;
            var remaining = CurrentRemaining;
            if (state.ConfiguredCeilings.CampaignBudget.MaximumElapsedMilliseconds is not null
                && CampaignBudgetAccounting.RemainingLifetimeElapsed(state.LineageCharges, state.ConfiguredCeilings.CampaignBudget) - CurrentElapsed <= remaining)
                return DocumentationScribeDeadlineOwner.Lifetime;
            return Scope.Allowance.RemainingMilliseconds <= remaining
                ? DocumentationScribeDeadlineOwner.Invocation : DocumentationScribeDeadlineOwner.HostOperation;
        }
    }
    public async ValueTask<bool> ReserveProviderAsync(DocumentationScribeDispatchDescriptor descriptor,
        DocumentationScribeInvocationProviderPermit permit, CancellationToken cancellationToken)
    {
        if (guard is not null && !guard()) { Conflict = true; return false; }
        var preceding = ElapsedAt(permit.StartedAt);
        var transition = CampaignStateReducer.ReserveProviderDispatch(invocation, descriptor, permit, preceding);
        // This producer rejects BudgetExhausted only for lifetime exposure admission.
        if (transition.Failure == CampaignTransitionFailure.BudgetExhausted) Scope.ObserveLifetimeAdmissionDenial();
        using var boundaryScope = CampaignProcessBoundaryHooks.EnterReplacementScope(CampaignProcessBoundaryHooks.PhysicalReservationReplacementScope);
        var accepted = await AcceptProgressAsync(transition, permit.StartedAt).ConfigureAwait(false);
        if (accepted) CampaignProcessBoundaryHooks.Reach(CampaignProcessBoundaryHooks.PhysicalReservationAfterReadback);
        return accepted;
    }
    public async ValueTask<bool> SettleProviderAsync(DocumentationScribeInvocationProviderPermit permit,
        DocumentationScribeDispatchSettlement settlement, CancellationToken cancellationToken)
    {
        var boundary = clock.GetTimestamp();
        var transition = CampaignStateReducer.SettleProviderDispatch(invocation, permit, settlement, ElapsedAt(boundary));
        using var boundaryScope = CampaignProcessBoundaryHooks.EnterReplacementScope(CampaignProcessBoundaryHooks.PhysicalSettlementReplacementScope);
        var accepted = await AcceptProgressAsync(transition, boundary).ConfigureAwait(false);
        if (accepted) CampaignProcessBoundaryHooks.Reach(CampaignProcessBoundaryHooks.PhysicalSettlementAfterReadback);
        return accepted;
    }
    public async ValueTask<DocumentationScribeHostAdmission?> BeginHostAsync(DocumentationScribeHostOperation operation,
        int maximumMilliseconds, CancellationToken cancellationToken)
    {
        if (guard is not null && !guard()) { Conflict = true; return null; }
        var boundary = clock.GetTimestamp();
        var elapsed = ElapsedAt(boundary);
        var transition = CampaignStateReducer.AdvanceHostOperation(invocation, operation,
            elapsed, maximumMilliseconds, completing: false);
        if (transition.Failure == CampaignTransitionFailure.BudgetExhausted)
        {
            var state = invocation.AcceptedCheckpoint.Artifact.State;
            var claim = (CampaignProviderReservation)state.ActiveReservation!;
            var charges = CampaignBudgetAccounting.SettleHostInterval(state.LineageCharges, elapsed, claim.Exposure.ElapsedMilliseconds);
            var budget = state.ConfiguredCeilings.CampaignBudget;
            // Host rejection also covers Action duration and completion headroom.
            // Preserve only the lifetime cause of this specific denied operation.
            if (CampaignBudgetAccounting.RemainingLifetimeElapsed(charges, budget) <= 0
                || !CampaignBudgetAccounting.FitsSettledBudget(charges, budget)
                    && operation is not (DocumentationScribeHostOperation.TerminalSubmission or DocumentationScribeHostOperation.Postflight))
                Scope.ObserveLifetimeAdmissionDenial();
            else if (Scope.Allowance.RemainingMilliseconds <= 0)
                Scope.Allowance.CanBeginProviderWork();
        }
        using var boundaryScope = operation == DocumentationScribeHostOperation.RetryWait
            ? CampaignProcessBoundaryHooks.EnterReplacementScope(CampaignProcessBoundaryHooks.RetryWaitReplacementScope) : null;
        if (!await AcceptProgressAsync(transition, boundary).ConfigureAwait(false)) return null;
        if (operation == DocumentationScribeHostOperation.RetryWait)
            CampaignProcessBoundaryHooks.Reach(CampaignProcessBoundaryHooks.RetryWaitAfterReadback);
        return new(((CampaignProviderReservation)invocation.AcceptedCheckpoint.Artifact.State.ActiveReservation!).Exposure.ElapsedMilliseconds, CurrentDeadlineOwner);
    }
    public async ValueTask<bool> CompleteHostAsync(DocumentationScribeHostOperation operation,
        int elapsedMilliseconds, CancellationToken cancellationToken)
    {
        var boundary = clock.GetTimestamp();
        return await AcceptProgressAsync(CampaignStateReducer.AdvanceHostOperation(invocation, operation,
            ElapsedAt(boundary), 0, completing: true), boundary).ConfigureAwait(false);
    }
    private async ValueTask<bool> AcceptProgressAsync(CampaignTransitionResult transition, long boundary)
    {
        if (transition.Kind != CampaignTransitionKind.Applied)
        {
            LifetimeStop |= Scope.LifetimeAdmissionDenied;
            Conflict |= transition.Failure != CampaignTransitionFailure.BudgetExhausted
                || !(Scope.LifetimeAdmissionDenied || Scope.Allowance.HasCheckedStop);
            return false;
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), clock);
        using var boundedSettlement = CancellationTokenSource.CreateLinkedTokenSource(settlementToken, deadline.Token);
        var accepted = await CampaignCheckpointAcceptance.AcceptAsync(store, transition, boundedSettlement.Token).ConfigureAwait(false);
        if (accepted.Kind != CampaignCheckpointAcceptanceKind.Accepted || accepted.AcceptedCheckpoint is null
            || !invocation.AdvanceAcceptedProgress(transition, accepted.AcceptedCheckpoint))
        { Conflict = true; return false; }
        operationStartedAt = boundary;
        var state = accepted.Artifact!.State;
        var exceeded = !CampaignBudgetAccounting.FitsSettledBudget(state.LineageCharges, state.ConfiguredCeilings.CampaignBudget);
        if (exceeded) Scope.ObserveSettledLifetimeBudgetExhaustion();
        LifetimeStop |= exceeded || CampaignBudgetAccounting.RemainingLifetimeElapsed(state.LineageCharges,
            state.ConfiguredCeilings.CampaignBudget) <= 0;
        return true;
    }
}
