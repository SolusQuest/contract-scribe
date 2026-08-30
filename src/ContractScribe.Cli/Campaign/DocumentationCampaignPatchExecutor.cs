using System.Collections.Immutable;
using System.Text.Json;
using ContractScribe.Core;
using ContractScribe.Patching;
using ContractScribe.Roslyn;

namespace ContractScribe.Cli;

internal sealed record DocumentationCampaignPatchInput(
    ClassifiedRepositorySession Session,
    ObservedRepositorySession Observations,
    PolicyDocumentV1 AcceptedPolicy,
    ImmutableArray<AuditRecordInput> AcceptedAuditInputs,
    AuditDocument AcceptedAuditDocument,
    CampaignPlanningInput PlanningInput,
    CampaignWorkPlan AcceptedPlan,
    CampaignScribeExecutionCapability ExecutionCapability,
    string StyleConfigurationId,
    JsonElement StyleConfigurationProjection,
    JsonElement M2PolicyProjection,
    ICampaignCheckpointStore Store,
    CancellationToken ExecutionToken,
    CancellationToken SettlementToken,
    DocumentationPatchEngine? PatchEngine = null,
    TimeProvider? TimeProvider = null,
    Action? AfterPatchExecutionObserver = null);

internal static class DocumentationCampaignPatchExecutor
{
    private const int MaximumStageIterations = CampaignStateContract.MaximumActivePatchBlocks + 1;

    internal static async Task<DocumentationCampaignOutcome> ExecuteAsync(
        DocumentationCampaignPatchInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!CampaignM2ExecutionPolicy.TryCreate(
                input.M2PolicyProjection,
                input.PlanningInput.ExecutionPolicy,
                out var m2Policy)
            || m2Policy is null)
        {
            return Outcome(DocumentationCampaignOutcomeKind.HostContractError, "campaign.patch.policy-invalid");
        }

        DocumentationScribeAuditAuthority auditAuthority;
        try
        {
            if (!ReferenceEquals(input.Session.Classification.ClassificationSet, input.PlanningInput.Classifications)
                || !ReferenceEquals(input.Observations.ObservationSet, input.PlanningInput.Observations)
                || !input.Observations.IsBoundToObservationSession(input.Session))
            {
                return Outcome(DocumentationCampaignOutcomeKind.HostContractError, "campaign.patch.session-mismatch");
            }

            auditAuthority = DocumentationScribeAuditAuthority.Create(
                input.Session,
                input.Observations,
                input.AcceptedPolicy,
                input.AcceptedAuditInputs,
                input.AcceptedAuditDocument);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            return Outcome(DocumentationCampaignOutcomeKind.HostContractError, "campaign.patch.session-invalid");
        }

        for (var iteration = 0; iteration < MaximumStageIterations; iteration++)
        {
            var accepted = await CampaignCheckpointAcceptance.AcceptCurrentAsync(
                input.Store,
                input.SettlementToken).ConfigureAwait(false);
            if (accepted.Kind != CampaignCheckpointAcceptanceKind.Accepted
                || accepted.AcceptedCheckpoint is null)
            {
                return AcceptanceFailure(accepted.Kind, "campaign.patch.checkpoint");
            }

            var checkpoint = accepted.AcceptedCheckpoint;
            var current = checkpoint.Artifact;
            var state = current.State;
            try
            {
                CampaignStateFactory.ValidateCurrentContext(
                    state,
                    input.ExecutionCapability,
                    input.StyleConfigurationId,
                    input.StyleConfigurationProjection,
                    input.Session.RepositorySession.InputIdentity,
                    input.PlanningInput,
                    input.AcceptedPlan);
            }
            catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
            {
                return Outcome(DocumentationCampaignOutcomeKind.HostContractError, "campaign.patch.context-invalid");
            }

            var action = Classify(state);
            if (action == PatchStateAction.Replay)
            {
                return FromArtifact(current);
            }
            if (action == PatchStateAction.NoWork)
            {
                return new DocumentationCampaignOutcome(
                    DocumentationCampaignOutcomeKind.NoWork,
                    "campaign.patch.no-work",
                    current);
            }
            if (action == PatchStateAction.Conflict)
            {
                return Outcome(DocumentationCampaignOutcomeKind.StateConflict, "campaign.patch.state-conflict");
            }

            var needsMutation = action is PatchStateAction.Active
                or PatchStateAction.AcceptedReconstruction
                or PatchStateAction.Retry;
            if (needsMutation && state.CheckpointRevision >= CampaignStateContract.MaximumObservation)
            {
                return Outcome(DocumentationCampaignOutcomeKind.StateConflict, "campaign.patch.revision-conflict");
            }
            if (needsMutation && state.CheckpointRevision == CampaignStateContract.MaximumObservation - 1)
            {
                if (action == PatchStateAction.AcceptedReconstruction)
                {
                    return Outcome(
                        DocumentationCampaignOutcomeKind.StateConflict,
                        "campaign.patch.reconstruction-headroom-conflict");
                }

                var exhausted = state.ActiveReservation is CampaignPatchReservation
                    ? CampaignStateReducer.StopActiveInvocation(
                        current,
                        checkpoint,
                        CampaignTerminalKind.Exhausted)
                    : CampaignStateReducer.Stop(current, CampaignTerminalKind.Exhausted);
                return await AcceptTransitionAsync(
                    input.Store,
                    exhausted,
                    input.SettlementToken,
                    "campaign.patch.revision-exhausted").ConfigureAwait(false);
            }

            CumulativeDocumentationPatchComposition composition;
            try
            {
                composition = CumulativeDocumentationPatchComposer.Compose(
                    input.Session,
                    input.PlanningInput,
                    input.AcceptedPlan,
                    auditAuthority,
                    state,
                    action == PatchStateAction.AcceptedReconstruction,
                    input.ExecutionToken);
            }
            catch (OperationCanceledException) when (input.ExecutionToken.IsCancellationRequested)
            {
                return Outcome(DocumentationCampaignOutcomeKind.Cancelled, "campaign.patch.cancelled-before-admission");
            }
            catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
            {
                return Outcome(DocumentationCampaignOutcomeKind.HostContractError, "campaign.patch.composition-invalid");
            }

            var reservation = action == PatchStateAction.Retry
                ? CampaignStateReducer.RetryPatchInvocation(
                    current,
                    checkpoint,
                    composition.Request,
                    m2Policy.MaximumPatchElapsedMilliseconds)
                : CampaignStateReducer.ReservePatchInvocation(
                    current,
                    composition.Request,
                    m2Policy.MaximumPatchElapsedMilliseconds);
            if (reservation.Kind == CampaignTransitionKind.Rejected)
            {
                return reservation.Failure == CampaignTransitionFailure.BudgetExhausted
                    ? new DocumentationCampaignOutcome(
                        DocumentationCampaignOutcomeKind.BudgetExhausted,
                        "campaign.patch.budget-exhausted",
                        current)
                    : Outcome(DocumentationCampaignOutcomeKind.HostContractError, "campaign.patch.reservation-invalid");
            }

            CampaignProcessBoundaryHooks.Reach(CampaignProcessBoundaryHooks.PatchBeforeReservationCommit);
            CampaignCheckpointAcceptanceResult reserved;
            using (CampaignProcessBoundaryHooks.EnterReplacementScope(
                       CampaignProcessBoundaryHooks.PatchReservationReplacementScope))
            {
                reserved = await CampaignCheckpointAcceptance.AcceptAsync(
                    input.Store,
                    reservation,
                    input.SettlementToken).ConfigureAwait(false);
            }
            if (reserved.Kind != CampaignCheckpointAcceptanceKind.Accepted
                || reserved.AcceptedCheckpoint is null)
            {
                return AcceptanceFailure(reserved.Kind, "campaign.patch.reservation");
            }
            if (reserved.Artifact?.State.ActiveReservation is not CampaignPatchReservation)
            {
                return reserved.Artifact is null
                    ? Outcome(DocumentationCampaignOutcomeKind.AmbiguousDispatch, "campaign.patch.reservation-unconfirmed")
                    : FromArtifact(reserved.Artifact);
            }

            CampaignProcessBoundaryHooks.Reach(CampaignProcessBoundaryHooks.PatchAfterReservationReadback);

            CampaignPatchInvocationAuthority invocation;
            try
            {
                invocation = CampaignStateReducer.CreatePatchInvocationAuthority(
                    reserved.AcceptedCheckpoint,
                    composition.Request);
            }
            catch (ArgumentException)
            {
                return Outcome(DocumentationCampaignOutcomeKind.AmbiguousDispatch, "campaign.patch.reservation-observer");
            }

            var engine = input.PatchEngine ?? new DocumentationPatchEngine();
            var clock = input.TimeProvider ?? TimeProvider.System;
            PatchExecution execution;
            try
            {
                CampaignProcessBoundaryHooks.Reach(CampaignProcessBoundaryHooks.PatchBeforeDispatch);
                execution = ExecutePatch(
                    engine,
                    invocation,
                    input.Session,
                    composition.Request,
                    m2Policy.MaximumPatchElapsedMilliseconds,
                    clock,
                    input.ExecutionToken);
            }
            catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
            {
                return Outcome(DocumentationCampaignOutcomeKind.AmbiguousDispatch, "campaign.patch.dispatch-unconfirmed");
            }
            if (!execution.DispatchStarted)
            {
                return Outcome(DocumentationCampaignOutcomeKind.AmbiguousDispatch, "campaign.patch.dispatch-unavailable");
            }
            input.AfterPatchExecutionObserver?.Invoke();
            CampaignProcessBoundaryHooks.Reach(CampaignProcessBoundaryHooks.PatchAfterDispatchBeforeResultTransition);

            CampaignTransitionResult completion;
            var appliedReduction = false;
            if (execution.Outcome?.Result is { Outcome: DocumentationPatchOutcome.Rejected } rejected)
            {
                var reduction = FindReduction(input, reserved.Artifact!, composition.Request, rejected);
                appliedReduction = reduction is not null;
                completion = reduction is null
                    ? CampaignStateReducer.CompletePatchInvocation(
                        reserved.Artifact!, invocation, composition.Request, rejected,
                        execution.ElapsedMilliseconds, execution.SimultaneousStop)
                    : CampaignStateReducer.ApplyPatchRejection(
                        reserved.Artifact!, invocation, reduction, execution.ElapsedMilliseconds);
            }
            else if (execution.Outcome?.Result is { } result)
            {
                completion = CampaignStateReducer.CompletePatchInvocation(
                    reserved.Artifact!, invocation, composition.Request, result,
                    execution.ElapsedMilliseconds, execution.SimultaneousStop);
            }
            else
            {
                completion = CampaignStateReducer.CompletePatchHostInvocation(
                    reserved.Artifact!, invocation, composition.Request,
                    execution.HostKind, execution.ElapsedMilliseconds);
            }

            if (completion.Kind == CampaignTransitionKind.Rejected)
            {
                return Outcome(DocumentationCampaignOutcomeKind.HostContractError, "campaign.patch.settlement-invalid");
            }

            var acceptedResult = !appliedReduction
                && execution.Outcome?.Result?.Outcome == DocumentationPatchOutcome.Accepted;
            CampaignProcessBoundaryHooks.Reach(appliedReduction
                ? CampaignProcessBoundaryHooks.PatchAfterExecutionBeforeReductionTransition
                : acceptedResult
                    ? CampaignProcessBoundaryHooks.PatchAfterExecutionBeforeAcceptedTransition
                    : CampaignProcessBoundaryHooks.PatchAfterExecutionBeforeClosedTransition);
            var replacementScope = appliedReduction
                ? CampaignProcessBoundaryHooks.PatchReductionReplacementScope
                : acceptedResult
                    ? CampaignProcessBoundaryHooks.PatchAcceptedReplacementScope
                    : CampaignProcessBoundaryHooks.PatchClosedReplacementScope;
            CampaignCheckpointAcceptanceResult settled;
            using (CampaignProcessBoundaryHooks.EnterReplacementScope(replacementScope))
            {
                settled = await CampaignCheckpointAcceptance.AcceptAsync(
                    input.Store,
                    completion,
                    input.SettlementToken).ConfigureAwait(false);
            }
            if (settled.Kind != CampaignCheckpointAcceptanceKind.Accepted || settled.Artifact is null)
            {
                return AcceptanceFailure(settled.Kind, "campaign.patch.settlement");
            }

            CampaignProcessBoundaryHooks.Reach(CampaignProcessBoundaryHooks.PatchAfterResultReadback);
            CampaignProcessBoundaryHooks.Reach(appliedReduction
                ? CampaignProcessBoundaryHooks.PatchAfterReductionReadback
                : acceptedResult
                    ? CampaignProcessBoundaryHooks.PatchAfterAcceptedReadback
                    : CampaignProcessBoundaryHooks.PatchAfterClosedReadback);

            var artifact = settled.Artifact;
            if (artifact.State.CumulativeOutcome?.Kind == CampaignCumulativeOutcomeKind.Accepted
                && execution.Outcome?.AcceptedCandidate is { } candidate
                && CandidateMatches(candidate, composition.Request, artifact.State))
            {
                return new DocumentationCampaignOutcome(
                    composition.AcceptedOnly
                        ? DocumentationCampaignOutcomeKind.Reconstructed
                        : DocumentationCampaignOutcomeKind.Accepted,
                    composition.AcceptedOnly
                        ? "campaign.patch.reconstructed"
                        : "campaign.patch.accepted",
                    artifact,
                    candidate);
            }
            if (artifact.State.CumulativeOutcome?.Kind == CampaignCumulativeOutcomeKind.OverBound)
            {
                return new DocumentationCampaignOutcome(
                    DocumentationCampaignOutcomeKind.BudgetExhausted,
                    "campaign.patch.over-bound",
                    artifact);
            }
            if (appliedReduction
                && artifact.State.WorkItems.Any(item => item.Status is
                    CampaignWorkStatus.ProposalComplete or CampaignWorkStatus.Accepted))
            {
                continue;
            }

            return FromArtifact(artifact);
        }

        return Outcome(DocumentationCampaignOutcomeKind.HostContractError, "campaign.patch.iteration-bound");
    }

    private static PatchExecution ExecutePatch(
        DocumentationPatchEngine engine,
        CampaignPatchInvocationAuthority invocation,
        ClassifiedRepositorySession session,
        DocumentationPatchRequest request,
        long maximumElapsedMilliseconds,
        TimeProvider clock,
        CancellationToken callerToken)
    {
        using var deadline = new CampaignPatchDeadline(clock, maximumElapsedMilliseconds);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, deadline.Token);
        var causeState = new StopCause(0);
        using var currentCallerRegistration = callerToken.Register(
            static state => Interlocked.CompareExchange(ref ((StopCause)state!).Value, 1, 0), causeState);
        using var currentDeadlineRegistration = deadline.Token.Register(
            static state => Interlocked.CompareExchange(ref ((StopCause)state!).Value, 2, 0), causeState);
        var started = clock.GetTimestamp();
        if (!invocation.TryBeginDispatch())
        {
            return PatchExecution.NotDispatched();
        }

        DocumentationPatchExecutionOutcome? outcome = null;
        try
        {
            CampaignProcessBoundaryHooks.Reach(CampaignProcessBoundaryHooks.PatchDuringExecution);
            outcome = engine.Execute(session, request, linked.Token);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            // The independent registrations below retain the authoritative caller/deadline stop cause.
        }

        var observed = ObserveElapsedMilliseconds(clock, started);
        var simultaneousStop = Volatile.Read(ref causeState.Value) switch
        {
            1 => CampaignTerminalKind.Cancelled,
            2 => CampaignTerminalKind.Timeout,
            _ => (CampaignTerminalKind?)null,
        };
        if (outcome?.Result is not null)
        {
            return new PatchExecution(true, outcome, observed, simultaneousStop,
                CampaignCumulativeOutcomeKind.HostFailure);
        }
        if (outcome?.Status == DocumentationPatchExecutionStatus.HostFailure)
        {
            return new PatchExecution(true, outcome, observed, null,
                CampaignCumulativeOutcomeKind.HostFailure);
        }

        var hostKind = simultaneousStop switch
        {
            CampaignTerminalKind.Cancelled => CampaignCumulativeOutcomeKind.Cancelled,
            CampaignTerminalKind.Timeout => CampaignCumulativeOutcomeKind.Timeout,
            _ => CampaignCumulativeOutcomeKind.HostFailure,
        };
        return new PatchExecution(true, outcome, observed, null, hostKind);
    }

    internal static long? ObserveElapsedMilliseconds(TimeProvider clock, long started)
    {
        try
        {
            var elapsed = clock.GetElapsedTime(started, clock.GetTimestamp()).TotalMilliseconds;
            if (double.IsFinite(elapsed) && elapsed >= 0)
            {
                var ceiling = checked((long)Math.Ceiling(elapsed));
                if (ceiling <= CampaignStateContract.MaximumObservation)
                {
                    return ceiling;
                }
            }
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            // Preserve the full conservative reservation when monotonic observation is unavailable.
        }

        return null;
    }

    private static CampaignPatchRejectionReduction? FindReduction(
        DocumentationCampaignPatchInput input,
        CampaignCheckpointArtifact predecessor,
        DocumentationPatchRequest request,
        DocumentationPatchValidationResult result)
    {
        foreach (var target in result.Targets.Where(target => target.Status == DocumentationPatchTargetStatus.Invalid))
        {
            var decision = CampaignStateFactory.CreatePatchRejectionReduction(
                predecessor,
                input.ExecutionCapability,
                input.StyleConfigurationId,
                input.StyleConfigurationProjection,
                input.Session.RepositorySession.InputIdentity,
                input.PlanningInput,
                input.AcceptedPlan,
                request,
                result,
                target.BlockId);
            if (decision.Kind == CampaignPatchRejectionDecisionKind.Removable)
            {
                return decision.Reduction;
            }
        }

        return null;
    }

    private static bool CandidateMatches(
        DocumentationPatchAcceptedCandidate candidate,
        DocumentationPatchRequest request,
        CampaignCheckpointState state)
    {
        var observation = state.CandidateObservation;
        var cumulative = state.CumulativeOutcome;
        if (observation is null
            || cumulative?.Kind != CampaignCumulativeOutcomeKind.Accepted
            || !string.Equals(observation.PatchRequestSha256, request.ArtifactSha256, StringComparison.Ordinal)
            || !string.Equals(cumulative.PatchRequestSha256, request.ArtifactSha256, StringComparison.Ordinal)
            || !string.Equals(
                observation.PatchResultCommitmentSha256,
                CampaignStateFactory.CreatePatchResultCommitment(request, candidate.Result),
                StringComparison.Ordinal)
            || !string.Equals(
                cumulative.PatchResultCommitmentSha256,
                observation.PatchResultCommitmentSha256,
                StringComparison.Ordinal)
            || !observation.AcceptedWorkItemKeys.Order(StringComparer.Ordinal).SequenceEqual(
                request.Blocks.Select(block => block.BlockId).Order(StringComparer.Ordinal),
                StringComparer.Ordinal))
        {
            return false;
        }

        var observed = observation.ChangedFiles.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
        var changedCandidates = candidate.Files
            .Where(file => observed.Any(changed =>
                string.Equals(file.RepositoryPath, changed.Path, StringComparison.Ordinal)))
            .OrderBy(file => file.RepositoryPath, StringComparer.Ordinal)
            .ToArray();
        return changedCandidates.Length == observed.Length
            && changedCandidates.Zip(observed).All(pair =>
                string.Equals(pair.First.RepositoryPath, pair.Second.Path, StringComparison.Ordinal)
                && string.Equals(pair.First.Sha256, pair.Second.CandidateFileSha256, StringComparison.Ordinal));
    }

    private static PatchStateAction Classify(CampaignCheckpointState state)
    {
        if (state.ActiveReservation is not null and not CampaignPatchReservation)
        {
            return PatchStateAction.Conflict;
        }
        if (state.ActiveReservation is CampaignPatchReservation)
        {
            return state.TerminalOutcome is null ? PatchStateAction.Retry : PatchStateAction.Conflict;
        }

        var proposalCount = state.WorkItems.Count(item => item.Status == CampaignWorkStatus.ProposalComplete);
        var acceptedCount = state.WorkItems.Count(item => item.Status == CampaignWorkStatus.Accepted);
        if (state.TerminalOutcome is { } terminal)
        {
            if (terminal.Kind == CampaignTerminalKind.Complete
                && terminal.Reason == CampaignTerminalReason.AllWorkClosed
                && proposalCount == 0
                && acceptedCount > 0)
            {
                return PatchStateAction.AcceptedReconstruction;
            }

            return PatchStateAction.Replay;
        }
        if (proposalCount > 0)
        {
            return PatchStateAction.Active;
        }
        if (acceptedCount > 0)
        {
            return PatchStateAction.AcceptedReconstruction;
        }
        return PatchStateAction.NoWork;
    }

    private static async Task<DocumentationCampaignOutcome> AcceptTransitionAsync(
        ICampaignCheckpointStore store,
        CampaignTransitionResult transition,
        CancellationToken token,
        string code)
    {
        if (transition.Kind == CampaignTransitionKind.Rejected)
        {
            return Outcome(DocumentationCampaignOutcomeKind.StateConflict, code);
        }
        var accepted = await CampaignCheckpointAcceptance.AcceptAsync(store, transition, token).ConfigureAwait(false);
        return accepted.Kind == CampaignCheckpointAcceptanceKind.Accepted && accepted.Artifact is not null
            ? FromArtifact(accepted.Artifact)
            : AcceptanceFailure(accepted.Kind, code);
    }

    private static DocumentationCampaignOutcome FromArtifact(CampaignCheckpointArtifact artifact)
    {
        if (artifact.State.TerminalOutcome is { Kind: CampaignTerminalKind.Exhausted })
        {
            return new DocumentationCampaignOutcome(
                DocumentationCampaignOutcomeKind.BudgetExhausted,
                "campaign.patch.exhausted",
                artifact);
        }

        return artifact.State.CumulativeOutcome?.Kind switch
        {
            CampaignCumulativeOutcomeKind.Accepted => new(
                DocumentationCampaignOutcomeKind.StateConflict,
                "campaign.patch.candidate-reconstruction-required",
                artifact),
            CampaignCumulativeOutcomeKind.OverBound => new(
                DocumentationCampaignOutcomeKind.BudgetExhausted,
                "campaign.patch.over-bound",
                artifact),
            CampaignCumulativeOutcomeKind.Rejected => new(
                DocumentationCampaignOutcomeKind.Rejected,
                "campaign.patch.rejected",
                artifact),
            CampaignCumulativeOutcomeKind.Stale => new(
                DocumentationCampaignOutcomeKind.Stale,
                "campaign.patch.stale",
                artifact),
            CampaignCumulativeOutcomeKind.HostFailure => new(
                DocumentationCampaignOutcomeKind.HostFailure,
                "campaign.patch.host-failure",
                artifact),
            CampaignCumulativeOutcomeKind.Cancelled => new(
                DocumentationCampaignOutcomeKind.Cancelled,
                "campaign.patch.cancelled",
                artifact),
            CampaignCumulativeOutcomeKind.Timeout => new(
                DocumentationCampaignOutcomeKind.TimedOut,
                "campaign.patch.timed-out",
                artifact),
            _ when artifact.State.TerminalOutcome is null => new(
                DocumentationCampaignOutcomeKind.Reduced,
                "campaign.patch.reduced",
                artifact),
            _ => new(
                DocumentationCampaignOutcomeKind.TerminalStop,
                "campaign.patch.terminal",
                artifact),
        };
    }

    private static DocumentationCampaignOutcome AcceptanceFailure(
        CampaignCheckpointAcceptanceKind kind,
        string prefix)
    {
        var outcome = kind switch
        {
            CampaignCheckpointAcceptanceKind.Conflict
                or CampaignCheckpointAcceptanceKind.InvalidRead
                or CampaignCheckpointAcceptanceKind.Unreadable
                or CampaignCheckpointAcceptanceKind.WriteRejected
                or CampaignCheckpointAcceptanceKind.ReadbackMismatch
                or CampaignCheckpointAcceptanceKind.InvalidTransition =>
                Outcome(DocumentationCampaignOutcomeKind.StateConflict, prefix + "-conflict"),
            _ => Outcome(DocumentationCampaignOutcomeKind.AmbiguousDispatch, prefix + "-unconfirmed"),
        };
        return new DocumentationCampaignOutcome(
            outcome.Kind, outcome.Code, checkpointFailure: kind);
    }

    private static DocumentationCampaignOutcome Outcome(
        DocumentationCampaignOutcomeKind kind,
        string code) => new(kind, code);

    private enum PatchStateAction
    {
        Active,
        AcceptedReconstruction,
        Retry,
        Replay,
        NoWork,
        Conflict,
    }

    private sealed class StopCause
    {
        internal StopCause(int value) => Value = value;
        internal int Value;
    }

    private sealed class CampaignPatchDeadline : IDisposable
    {
        private const long MaximumTimerSliceMilliseconds = int.MaxValue - 1L;
        private readonly object gate = new();
        private readonly TimeProvider clock;
        private readonly long maximumElapsedMilliseconds;
        private readonly long started;
        private readonly CancellationTokenSource cancellation = new();
        private readonly ITimer timer;
        private bool disposed;

        internal CampaignPatchDeadline(TimeProvider clock, long maximumElapsedMilliseconds)
        {
            this.clock = clock;
            this.maximumElapsedMilliseconds = maximumElapsedMilliseconds;
            started = clock.GetTimestamp();
            timer = clock.CreateTimer(
                static state => ((CampaignPatchDeadline)state!).OnTimer(),
                this,
                Slice(maximumElapsedMilliseconds),
                Timeout.InfiniteTimeSpan);
        }

        internal CancellationToken Token => cancellation.Token;

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
            }

            timer.Dispose();
            cancellation.Dispose();
        }

        private void OnTimer()
        {
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                long remaining;
                try
                {
                    var elapsed = clock.GetElapsedTime(started, clock.GetTimestamp()).TotalMilliseconds;
                    if (!double.IsFinite(elapsed) || elapsed < 0)
                    {
                        cancellation.Cancel();
                        return;
                    }

                    remaining = elapsed >= maximumElapsedMilliseconds
                        ? 0
                        : checked((long)Math.Ceiling(maximumElapsedMilliseconds - elapsed));
                }
                catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
                {
                    cancellation.Cancel();
                    return;
                }

                if (remaining <= 0)
                {
                    cancellation.Cancel();
                    return;
                }

                _ = timer.Change(Slice(remaining), Timeout.InfiniteTimeSpan);
            }
        }

        private static TimeSpan Slice(long milliseconds) =>
            TimeSpan.FromMilliseconds(Math.Min(milliseconds, MaximumTimerSliceMilliseconds));
    }

    private sealed record PatchExecution(
        bool DispatchStarted,
        DocumentationPatchExecutionOutcome? Outcome,
        long? ElapsedMilliseconds,
        CampaignTerminalKind? SimultaneousStop,
        CampaignCumulativeOutcomeKind HostKind)
    {
        internal static PatchExecution NotDispatched() =>
            new(false, null, null, null, CampaignCumulativeOutcomeKind.HostFailure);
    }
}
