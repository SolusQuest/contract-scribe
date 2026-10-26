using ContractScribe.Core;

namespace ContractScribe.Cli;

internal sealed class CampaignScribeExecutionCoordinator : IDocumentationScribeExecutionOperations
{
    private readonly CampaignProviderInvocationAuthority invocation;
    private readonly ICampaignCheckpointStore store;
    private readonly TimeProvider clock;
    private readonly CancellationToken settlementToken;
    private readonly int checkpointAcceptanceMilliseconds;
    private readonly Func<bool>? guard;
    private long operationStartedAt;
    private CampaignSemanticCheckpointWindow? semanticWindow;
    private sealed record CheckpointRejection(CampaignCheckpointAcceptanceKind Kind, bool HostDeadline);
    private CheckpointRejection? checkpointFailure;
    internal CampaignScribeExecutionCoordinator(CampaignProviderInvocationAuthority invocation,
        DocumentationScribeInvocationAllowance allowance, ICampaignCheckpointStore store,
        CancellationToken settlementToken, Func<bool>? guard, long operationStartedAt,
        DocumentationScribeHostTimeouts? hostTimeouts = null, int checkpointAcceptanceMilliseconds = 30_000)
    {
        if (checkpointAcceptanceMilliseconds is < 1 or > DocumentationScribeContract.MaximumConfiguredElapsedMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(checkpointAcceptanceMilliseconds));
        this.checkpointAcceptanceMilliseconds = checkpointAcceptanceMilliseconds;
        this.invocation = invocation; this.store = store; this.settlementToken = settlementToken;
        this.guard = guard; clock = allowance.Clock; this.operationStartedAt = operationStartedAt;
        Scope = DocumentationScribeExecutionScope.ForCampaign(invocation, allowance, this, hostTimeouts);
    }
    internal DocumentationScribeExecutionScope Scope { get; }
    internal bool CheckpointCapacityStop { get; private set; }
    internal bool ProjectionCapacityStop { get; private set; }
    internal void ObserveProjectionCapacity() => ProjectionCapacityStop = true;
    internal bool LifetimeStop { get; private set; }
    internal bool Conflict { get; private set; }
    internal CampaignCheckpointAcceptanceKind? CheckpointFailure => Volatile.Read(ref checkpointFailure)?.Kind;
    internal bool CheckpointCancelledByHostDeadline => Volatile.Read(ref checkpointFailure) is
    { Kind: CampaignCheckpointAcceptanceKind.Cancelled, HostDeadline: true };
    internal CampaignCheckpointArtifact LatestAcknowledgedArtifact => invocation.AcceptedCheckpoint.Artifact;
    internal Task LateObservation { get; private set; } = Task.CompletedTask;
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
        if (operation == DocumentationScribeHostOperation.SemanticTool)
            return await BeginSemanticAsync(maximumMilliseconds, cancellationToken).ConfigureAwait(false);
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
        // Admission readback also settles an already-started checkpoint write.
        // Retain its observation scope on execution cancellation; the permit
        // still measures from the original pre-admission boundary and cannot
        // execute after that boundary's applicable deadline.
        if (!await AcceptProgressAsync(transition, boundary).ConfigureAwait(false)) return null;
        if (operation == DocumentationScribeHostOperation.RetryWait)
            CampaignProcessBoundaryHooks.Reach(CampaignProcessBoundaryHooks.RetryWaitAfterReadback);
        return new(((CampaignProviderReservation)invocation.AcceptedCheckpoint.Artifact.State.ActiveReservation!).Exposure.ElapsedMilliseconds, CurrentDeadlineOwner);
    }

    private async ValueTask<DocumentationScribeHostAdmission?> BeginSemanticAsync(int maximumMilliseconds,
        CancellationToken cancellationToken)
    {
        if (store is not ICampaignSemanticCheckpointStore semanticStore || semanticWindow is not null)
        { Conflict = true; return null; }
        var boundary = clock.GetTimestamp();
        // Capture the shorter original target bound at this transition's own
        // boundary, rather than borrowing time spent since Scope's first check.
        maximumMilliseconds = Math.Min(maximumMilliseconds, Scope.RemainingTargetMilliseconds);
        if (maximumMilliseconds <= Scope.HostTimeouts.Semantic.ClosureMilliseconds)
        { Scope.ObserveSemanticHeadroom(); return null; }
        var transition = CampaignStateReducer.AdvanceHostOperation(invocation, DocumentationScribeHostOperation.SemanticTool,
            ElapsedAt(boundary), maximumMilliseconds, completing: false, Scope.HostTimeouts.Semantic);
        if (transition.Kind != CampaignTransitionKind.Applied)
        {
            if (transition.Failure == CampaignTransitionFailure.BudgetExhausted)
            {
                Scope.ObserveLifetimeAdmissionDenial();
                LifetimeStop = true;
            }
            else if (transition.Failure == CampaignTransitionFailure.CheckpointCapacity)
            { CheckpointCapacityStop = true; Scope.ObserveCheckpointCapacity(); }
            else Conflict = true;
            return null;
        }

        var envelope = ((CampaignProviderReservation)transition.Artifact.State.ActiveReservation!).Exposure.ElapsedMilliseconds;
        var window = new CampaignSemanticCheckpointWindow(semanticStore, transition.Artifact, clock, boundary, envelope, guard);
        semanticWindow = window;
        Task<CampaignCheckpointAcceptanceResult>? pending = null;
        var issued = false;
        try
        {
            window.AttachBinding(semanticStore.EnterSemanticCheckpointWindow(window));
            using var boundaryScope = CampaignProcessBoundaryHooks.EnterReplacementScope(
                CampaignProcessBoundaryHooks.SemanticAuthorizationReplacementScope);
            using var authorization = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, settlementToken, window.AuthorizationToken);
            pending = CampaignCheckpointAcceptance.AcceptAsync(store, transition, authorization.Token).AsTask();
            var accepted = await pending.WaitAsync(authorization.Token).ConfigureAwait(false);
            var acceptedAt = clock.GetTimestamp();
            if (!window.AuthorizationOpen || cancellationToken.IsCancellationRequested)
            {
                if (!cancellationToken.IsCancellationRequested) Scope.ObserveSemanticAuthorizationTimeout();
                await ObserveStoppedSemanticAsync(transition, boundary, window, pending).ConfigureAwait(false);
                return null;
            }
            if (accepted.Kind != CampaignCheckpointAcceptanceKind.Accepted || accepted.AcceptedCheckpoint is null
                || !invocation.AdvanceAcceptedProgress(transition, accepted.AcceptedCheckpoint))
            { RejectCheckpoint(accepted.Kind); return null; }
            // This is the transition boundary, never the later readback time.
            operationStartedAt = boundary;
            CampaignProcessBoundaryHooks.Reach(CampaignProcessBoundaryHooks.SemanticAuthorizationAfterReadback);
            if (!window.TryIssue(accepted.Artifact!, acceptedAt, Scope.Allowance.RemainingMilliseconds,
                    Scope.RemainingTargetMilliseconds, out var queryMilliseconds)
                || guard is not null && !guard())
            { Conflict = true; return null; }
            issued = true;
            return new(queryMilliseconds, DocumentationScribeDeadlineOwner.HostOperation) { SemanticWindow = window };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || window.AuthorizationToken.IsCancellationRequested)
        {
            if (!cancellationToken.IsCancellationRequested) Scope.ObserveSemanticAuthorizationTimeout();
            await ObserveStoppedSemanticAsync(transition, boundary, window, pending).ConfigureAwait(false);
            return null;
        }
        finally
        {
            if (!issued)
            {
                window.AbandonQuery();
                window.Dispose();
                semanticWindow = null;
            }
        }
    }

    private async ValueTask ObserveStoppedSemanticAsync(CampaignTransitionResult transition, long boundary,
        CampaignSemanticCheckpointWindow window, Task<CampaignCheckpointAcceptanceResult>? pending)
    {
        window.AbandonQuery();
        window.BeginClosing();
        using var closing = CancellationTokenSource.CreateLinkedTokenSource(settlementToken, window.ObservationToken);
        try
        {
            if (pending is not null) await pending.WaitAsync(closing.Token).ConfigureAwait(false);
            if (!window.ObservationOpen || guard is not null && !guard()) { Conflict = true; return; }
            var read = CampaignCheckpointAcceptance.AcceptCurrentAsync(store, closing.Token).AsTask();
            CampaignCheckpointAcceptanceResult observed;
            try { observed = await read.WaitAsync(closing.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { LateObservation = ObserveLateAcceptanceAsync(read); throw; }
            if (!window.ObservationOpen || guard is not null && !guard()
                || observed.Kind != CampaignCheckpointAcceptanceKind.Accepted || observed.AcceptedCheckpoint is null)
            {
                if (observed.Kind != CampaignCheckpointAcceptanceKind.Accepted) RejectCheckpoint(observed.Kind);
                else Conflict = true;
                return;
            }
            if (!invocation.ObserveStoppedProgress(transition, observed.AcceptedCheckpoint))
            { Conflict = true; return; }
            Scope.ObserveSemanticAuthorizationClosed();
            if (observed.Artifact!.Sha256 == transition.Artifact.Sha256) operationStartedAt = boundary;
        }
        catch (OperationCanceledException) when (closing.IsCancellationRequested)
        {
            Conflict = true; // Unknown durable outcome: fresh-process retirement owns its exposure.
            if (pending is not null) LateObservation = ObserveLateAcceptanceAsync(pending);
        }
    }

    private static async Task ObserveLateAcceptanceAsync(Task<CampaignCheckpointAcceptanceResult> pending)
    {
        try { await pending.ConfigureAwait(false); }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException)) { }
    }
    public async ValueTask<bool> CompleteHostAsync(DocumentationScribeHostOperation operation,
        int elapsedMilliseconds, CancellationToken cancellationToken)
    {
        var boundary = clock.GetTimestamp();
        if (operation == DocumentationScribeHostOperation.SemanticTool && semanticWindow is { } window)
        {
            var pending = AcceptProgressAsync(CampaignStateReducer.AdvanceHostOperation(invocation, operation,
                ElapsedAt(boundary), 0, completing: true), boundary, window.ObservationToken, window).AsTask();
            try { return await pending.WaitAsync(window.ObservationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (window.ObservationToken.IsCancellationRequested)
            { Conflict = true; LateObservation = ObserveLateCompletionAsync(pending); return false; }
            finally { semanticWindow = null; }
        }
        return await AcceptProgressAsync(CampaignStateReducer.AdvanceHostOperation(invocation, operation,
            ElapsedAt(boundary), 0, completing: true), boundary).ConfigureAwait(false);
    }

    private static async Task ObserveLateCompletionAsync(Task<bool> pending)
    {
        try { await pending.ConfigureAwait(false); }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException)) { }
    }
    internal async ValueTask<bool> AcceptHostProgressAsync(CampaignTransitionResult transition, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) { RejectCheckpoint(CampaignCheckpointAcceptanceKind.Cancelled, HostDeadlineCancellation(cancellationToken, default)); return false; }
        if (guard is not null && !guard()) { Conflict = true; return false; }
        if (transition.Kind != CampaignTransitionKind.Unchanged)
            return await AcceptProgressAsync(transition, operationStartedAt, cancellationToken).ConfigureAwait(false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(checkpointAcceptanceMilliseconds), clock);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(settlementToken, cancellationToken, deadline.Token);
        var cancelledByHost = -1;
        using var cancellation = bounded.Token.Register(() =>
            Interlocked.CompareExchange(ref cancelledByHost, HostDeadlineCancellation(cancellationToken, deadline.Token) ? 1 : 0, -1));
        var accepted = await CampaignCheckpointAcceptance.AcceptAsync(store, transition, bounded.Token).ConfigureAwait(false);
        var valid = !bounded.IsCancellationRequested
            && (invocation.LifecycleAvailable || invocation.DispatchStarted)
            && accepted.Kind == CampaignCheckpointAcceptanceKind.Accepted
            && accepted.Artifact?.Sha256 == invocation.AcceptedCheckpoint.Artifact.Sha256;
        if (!valid) RejectCheckpoint(accepted.Kind == CampaignCheckpointAcceptanceKind.Accepted && bounded.IsCancellationRequested
            ? CampaignCheckpointAcceptanceKind.Cancelled : accepted.Kind, Volatile.Read(ref cancelledByHost) == 1);
        return valid;
    }

    private async ValueTask<bool> AcceptProgressAsync(CampaignTransitionResult transition, long boundary,
        CancellationToken cancellationToken = default, CampaignSemanticCheckpointWindow? semanticObservation = null)
    {
        if (transition.Kind != CampaignTransitionKind.Applied)
        {
            if (transition.Failure == CampaignTransitionFailure.CheckpointCapacity)
            {
                CheckpointCapacityStop = true;
                Scope.ObserveCheckpointCapacity();
                return false;
            }
            LifetimeStop |= Scope.LifetimeAdmissionDenied;
            Conflict |= transition.Failure != CampaignTransitionFailure.BudgetExhausted
                || !(Scope.LifetimeAdmissionDenied || Scope.Allowance.HasCheckedStop);
            return false;
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(checkpointAcceptanceMilliseconds), clock);
        using var boundedSettlement = CancellationTokenSource.CreateLinkedTokenSource(settlementToken, deadline.Token, cancellationToken);
        // Snapshot when this acceptance is first cancelled. A later Host timer
        // cannot turn an earlier checkpoint deadline/owner loss into a timeout.
        var cancelledByHost = -1;
        using var cancellation = boundedSettlement.Token.Register(() =>
            Interlocked.CompareExchange(ref cancelledByHost, HostDeadlineCancellation(cancellationToken, deadline.Token) ? 1 : 0, -1));
        var accepted = await CampaignCheckpointAcceptance.AcceptAsync(store, transition, boundedSettlement.Token).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested || semanticObservation is { ObservationOpen: false }
            || accepted.Kind != CampaignCheckpointAcceptanceKind.Accepted || accepted.AcceptedCheckpoint is null
            || !invocation.AdvanceAcceptedProgress(transition, accepted.AcceptedCheckpoint))
        {
            RejectCheckpoint(accepted.Kind == CampaignCheckpointAcceptanceKind.Accepted && cancellationToken.IsCancellationRequested
                ? CampaignCheckpointAcceptanceKind.Cancelled : accepted.Kind, Volatile.Read(ref cancelledByHost) == 1);
            return false;
        }
        operationStartedAt = boundary;
        var state = accepted.Artifact!.State;
        var exceeded = !CampaignBudgetAccounting.FitsSettledBudget(state.LineageCharges, state.ConfiguredCeilings.CampaignBudget);
        if (exceeded) Scope.ObserveSettledLifetimeBudgetExhaustion();
        LifetimeStop |= exceeded || CampaignBudgetAccounting.RemainingLifetimeElapsed(state.LineageCharges,
            state.ConfiguredCeilings.CampaignBudget) <= 0;
        return true;
    }

    private bool HostDeadlineCancellation(CancellationToken token, CancellationToken checkpointDeadline) =>
        !checkpointDeadline.IsCancellationRequested && !settlementToken.IsCancellationRequested && Scope.IsHostDeadlineCancellation(token);

    private void RejectCheckpoint(CampaignCheckpointAcceptanceKind kind, bool hostDeadline = false)
    {
        // An exact observation is not a new Writer grant. Preserve the first
        // failed acknowledgement even if a later stop or observation completes.
        var failure = kind == CampaignCheckpointAcceptanceKind.Accepted ? CampaignCheckpointAcceptanceKind.Conflict : kind;
        Interlocked.CompareExchange(ref checkpointFailure, new(failure, hostDeadline), null);
        Conflict = true;
    }
}
