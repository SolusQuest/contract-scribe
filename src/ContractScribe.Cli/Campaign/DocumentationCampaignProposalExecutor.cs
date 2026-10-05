using System.Collections.Immutable;
using System.Text.Json;
using ContractScribe.Agent.Runtime;
using ContractScribe.Core;
using ContractScribe.Roslyn;

namespace ContractScribe.Cli;

internal sealed record DocumentationCampaignProposalInput(
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
    ReadOnlyMemory<byte> RequestUtf8Json,
    ICampaignCheckpointStore Store,
    DocumentationScribeRuntimeOptions RuntimeOptions,
    IDocumentationScribeModelExchange? Exchange,
    string? ConfiguredAgentEntrypoint,
    CancellationToken ExecutionToken,
    CancellationToken SettlementToken,
    TimeProvider? TimeProvider = null,
    Func<IDocumentationScribeModelExchange?>? DeferredExchange = null,
    Func<bool>? DispatchGuard = null,
    CampaignInvocationTargetAllowance? TargetAllowance = null,
    DocumentationScribeInvocationAllowance? InvocationAllowance = null);

internal static class DocumentationCampaignProposalExecutor
{
    internal static async Task<DocumentationCampaignProposalOutcome> ExecuteAsync(
        DocumentationCampaignProposalInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.InvocationAllowance is null) return Outcome(DocumentationCampaignProposalOutcomeKind.HostContractError, "campaign.runtime.mismatch");
        try { _ = input.InvocationAllowance.RemainingMilliseconds; }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        { return Outcome(DocumentationCampaignProposalOutcomeKind.HostContractError, "campaign.preparation.invalid"); }
        var accepted = await CampaignCheckpointAcceptance.AcceptCurrentAsync(
            input.Store, input.SettlementToken).ConfigureAwait(false);
        if (accepted.Kind != CampaignCheckpointAcceptanceKind.Accepted || accepted.AcceptedCheckpoint is null)
        {
            return new DocumentationCampaignProposalOutcome(
                DocumentationCampaignProposalOutcomeKind.StateConflict,
                "campaign.checkpoint.unaccepted",
                checkpointFailure: accepted.Kind);
        }

        var current = accepted.AcceptedCheckpoint;
        DocumentationScribeAuditAuthority auditAuthority;
        try
        {
            if (!ReferenceEquals(input.Session.Classification.ClassificationSet, input.PlanningInput.Classifications)
                || !ReferenceEquals(input.Observations.ObservationSet, input.PlanningInput.Observations))
            {
                return Outcome(DocumentationCampaignProposalOutcomeKind.HostContractError, "campaign.context.session-mismatch");
            }
            CampaignStateFactory.ValidateCurrentContext(
                current.Artifact.State, input.ExecutionCapability, input.StyleConfigurationId,
                input.StyleConfigurationProjection, input.Session.RepositorySession.InputIdentity,
                input.PlanningInput, input.AcceptedPlan);
            auditAuthority = DocumentationScribeAuditAuthority.Create(
                input.Session, input.Observations, input.AcceptedPolicy,
                input.AcceptedAuditInputs, input.AcceptedAuditDocument);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            return Outcome(DocumentationCampaignProposalOutcomeKind.HostContractError, "campaign.context.invalid");
        }

        if (current.Artifact.State.ActiveReservation is CampaignProviderReservation)
        {
            var recovery = CampaignStateReducer.RetireInterruptedProviderAttempt(current);
            var retired = await CampaignCheckpointAcceptance.AcceptAsync(input.Store, recovery, input.SettlementToken).ConfigureAwait(false);
            if (retired.Kind != CampaignCheckpointAcceptanceKind.Accepted || retired.AcceptedCheckpoint is null)
                return Outcome(DocumentationCampaignProposalOutcomeKind.StateConflict, "campaign.reservation.conflict");
            current = retired.AcceptedCheckpoint;
        }
        var state = current.Artifact.State;
        foreach (var pausedWork in state.WorkItems.Where(work => work.PausedProviderAttempt is { } paused
            && (paused.RetryProgress.RetryableFailureCount >= state.ConfiguredCeilings.ScribeRunLimits.MaximumAttempts
                || paused.RetryProgress.LastDisposition == CampaignProviderDispatchDisposition.TerminalFailure)))
        {
            var fresh = await CampaignCheckpointAcceptance.AcceptCurrentAsync(input.Store, input.SettlementToken).ConfigureAwait(false);
            if (fresh.AcceptedCheckpoint is null) return Outcome(DocumentationCampaignProposalOutcomeKind.StateConflict, "campaign.reservation.conflict");
            var closure = CampaignStateReducer.CompleteRecoveredProviderFailure(fresh.AcceptedCheckpoint, pausedWork.WorkItemKey);
            var closed = await CampaignCheckpointAcceptance.AcceptAsync(input.Store, closure, input.SettlementToken).ConfigureAwait(false);
            if (closed.Kind != CampaignCheckpointAcceptanceKind.Accepted || closed.AcceptedCheckpoint is null)
                return Outcome(DocumentationCampaignProposalOutcomeKind.StateConflict, "campaign.settlement.conflict");
            current = closed.AcceptedCheckpoint;
        }
        state = current.Artifact.State;
        var targetAllowance = input.TargetAllowance
            ?? CampaignStateFactory.CreateInvocationTargetAllowance(state, input.PlanningInput.TargetLimit);
        if (state.ActiveReservation is CampaignPatchReservation)
        {
            return Outcome(DocumentationCampaignProposalOutcomeKind.StateConflict, "campaign.reservation.patch-active");
        }
        CampaignWorkItemState? selectedState = null;
        CampaignPlanningWorkItem? selectedPlan = null;
        foreach (var pair in state.WorkItems.Zip(input.AcceptedPlan.WorkItems).Where(pair =>
                     pair.First.Status == CampaignWorkStatus.ProposalComplete))
        {
            var work = pair.First;
            if (work.Status == CampaignWorkStatus.ProposalComplete)
            {
                if (!TrySelectAudit(auditAuthority, input.PlanningInput, pair.Second, out _))
                {
                    return Outcome(
                        DocumentationCampaignProposalOutcomeKind.HostContractError,
                        "campaign.context.audit-invalid");
                }

                return new(DocumentationCampaignProposalOutcomeKind.ProposalReady,
                    "campaign.proposal.replay", work.WorkItemKey, current.Artifact);
            }
        }
        var orderedKeys = (targetAllowance.RecoveryWorkItemKey is { } recoveryKey
                ? new[] { recoveryKey } : [])
            .Concat(targetAllowance.WorkItemKeys).Distinct(StringComparer.Ordinal);
        foreach (var key in orderedKeys)
        {
            var work = state.WorkItems.SingleOrDefault(item => item.WorkItemKey == key);
            var planWork = input.AcceptedPlan.WorkItems.SingleOrDefault(item => item.WorkItemKey == key);
            if (work is null || planWork is null || work.AttemptDisposition != CampaignAttemptDisposition.Open
                || !CampaignStateFactory.AllowsTarget(state, key, targetAllowance)) continue;
            if (work.Status is CampaignWorkStatus.Accepted
                || work.Status == CampaignWorkStatus.Closed
                    && work.ClosedOutcome is not
                    {
                        Code: CampaignWorkOutcomeCode.ProviderFailure,
                        ProviderDisposition: CampaignProviderFinalDisposition.Retryable
                    })
            {
                continue;
            }
            if (state.TerminalOutcome is null)
            {
                selectedState = work;
                selectedPlan = planWork;
                break;
            }
        }

        if (selectedState is null || selectedPlan is null)
        {
            return state.TerminalOutcome is null
                ? new(DocumentationCampaignProposalOutcomeKind.TargetLimit, "campaign.target-limit", artifact: current.Artifact)
                : FromTerminal(current.Artifact);
        }
        if (state.ActiveReservation is CampaignProviderReservation active
            && active.WorkItemKey != selectedState.WorkItemKey)
        {
            return Outcome(DocumentationCampaignProposalOutcomeKind.HostContractError, "campaign.reservation.foreign");
        }

        if (!TrySelectAudit(auditAuthority, input.PlanningInput, selectedPlan, out var selectedAudit))
        {
            return Outcome(
                DocumentationCampaignProposalOutcomeKind.HostContractError,
                "campaign.context.audit-invalid");
        }

        if (input.RequestUtf8Json.Length > DocumentationScribeContract.MaximumArtifactUtf8Bytes)
        {
            return Outcome(DocumentationCampaignProposalOutcomeKind.HostContractError, "campaign.request.invalid");
        }

        var ownedRequestUtf8Json = input.RequestUtf8Json.ToArray().AsMemory();
        var parsed = DocumentationScribeValidation.ParseRequest(ownedRequestUtf8Json);
        if (!parsed.IsValid || parsed.Request is not { } request)
        {
            return Outcome(DocumentationCampaignProposalOutcomeKind.HostContractError, "campaign.request.invalid");
        }
        var projection = input.ExecutionCapability.PersistedProjection;
        if (input.RuntimeOptions.ProviderConfigurationId != projection.ProviderConfigurationId
            || input.RuntimeOptions.ModelConfigurationId != projection.ModelConfigurationId
            || input.RuntimeOptions.ScribeProtocolId != projection.ScribeProtocolId)
        {
            return Outcome(DocumentationCampaignProposalOutcomeKind.HostContractError, "campaign.runtime.mismatch");
        }

        var transition = selectedState.Status == CampaignWorkStatus.Planned && state.ActiveReservation is null && selectedState.PausedProviderAttempt is null
            ? CampaignStateReducer.AdmitProviderInvocation(current.Artifact, input.ExecutionCapability,
                input.StyleConfigurationId, input.StyleConfigurationProjection, input.PlanningInput,
                input.AcceptedPlan, selectedState.WorkItemKey, request, targetAllowance, input.InvocationAllowance)
            : CampaignStateReducer.RetryProviderInvocation(current.Artifact,
                state.ActiveReservation is null ? null : current, input.ExecutionCapability,
                input.StyleConfigurationId, input.StyleConfigurationProjection, input.PlanningInput,
                input.AcceptedPlan, selectedState.WorkItemKey, request, targetAllowance, input.InvocationAllowance);
        if (transition.Kind == CampaignTransitionKind.Rejected)
        {
            if (transition.Failure == CampaignTransitionFailure.InvocationBudgetExhausted)
                return new(DocumentationCampaignProposalOutcomeKind.InvocationBudgetExhausted,
                    "campaign.invocation-budget-exhausted", selectedState.WorkItemKey, current.Artifact);
            if (transition.Failure == CampaignTransitionFailure.CheckpointCapacity)
            {
                return new(DocumentationCampaignProposalOutcomeKind.CheckpointCapacity,
                    "campaign.checkpoint-too-large", selectedState.WorkItemKey, current.Artifact);
            }
            return Outcome(DocumentationCampaignProposalOutcomeKind.HostContractError,
                "campaign.reservation.invalid");
        }

        if (transition.Artifact.State.ActiveReservation is not CampaignProviderReservation)
        {
            var stopped = await CampaignCheckpointAcceptance.AcceptAsync(input.Store, transition, input.SettlementToken)
                .ConfigureAwait(false);
            return stopped.Artifact is { } artifact && stopped.Kind == CampaignCheckpointAcceptanceKind.Accepted
                ? FromArtifact(artifact, selectedState.WorkItemKey)
                : Outcome(DocumentationCampaignProposalOutcomeKind.StateConflict, "campaign.reservation.conflict");
        }
        var exchange = input.Exchange ?? input.DeferredExchange?.Invoke();
        if (exchange is null)
        {
            return Outcome(DocumentationCampaignProposalOutcomeKind.HostContractError,
                "campaign.credential.invalid");
        }
        using var deferredExchange = input.Exchange is null
            ? exchange as IDisposable
            : null;
        var executionStartedAt = input.InvocationAllowance.Clock.GetTimestamp();
        CampaignProcessBoundaryHooks.Reach(CampaignProcessBoundaryHooks.ProposalBeforeReservationCommit);
        CampaignCheckpointAcceptanceResult reserved;
        using (CampaignProcessBoundaryHooks.EnterReplacementScope(
                   CampaignProcessBoundaryHooks.ProposalReservationReplacementScope))
        {
            reserved = await CampaignCheckpointAcceptance.AcceptAsync(
                input.Store, transition, input.SettlementToken).ConfigureAwait(false);
        }
        if (reserved.Kind != CampaignCheckpointAcceptanceKind.Accepted || reserved.AcceptedCheckpoint is null)
        {
            var failure = reserved.Kind is CampaignCheckpointAcceptanceKind.Conflict
                or CampaignCheckpointAcceptanceKind.InvalidRead
                or CampaignCheckpointAcceptanceKind.Unreadable
                ? Outcome(DocumentationCampaignProposalOutcomeKind.StateConflict, "campaign.reservation.conflict")
                : Outcome(DocumentationCampaignProposalOutcomeKind.AmbiguousDispatch, "campaign.reservation.unconfirmed");
            return new DocumentationCampaignProposalOutcome(
                failure.Kind, failure.Code, checkpointFailure: reserved.Kind);
        }
        if (reserved.Artifact?.State.ActiveReservation is not CampaignProviderReservation)
        {
            return reserved.Artifact is null
                ? Outcome(DocumentationCampaignProposalOutcomeKind.AmbiguousDispatch, "campaign.reservation.unconfirmed")
                : FromArtifact(reserved.Artifact, selectedState.WorkItemKey);
        }

        CampaignProcessBoundaryHooks.Reach(CampaignProcessBoundaryHooks.ProposalAfterReservationReadback);

        CampaignProviderInvocationAuthority invocation;
        try
        {
            invocation = CampaignStateReducer.CreateProviderInvocationAuthority(
                reserved.AcceptedCheckpoint, input.ExecutionCapability, input.StyleConfigurationId,
                input.StyleConfigurationProjection, input.PlanningInput, input.AcceptedPlan, request);
        }
        catch (ArgumentException)
        {
            return Outcome(DocumentationCampaignProposalOutcomeKind.AmbiguousDispatch, "campaign.reservation.observer");
        }

        CampaignProcessBoundaryHooks.Reach(CampaignProcessBoundaryHooks.ProposalBeforeProviderDispatch);
        var dispatchGuardRejected = false;
        bool GuardDispatch()
        {
            var allowed = DispatchAllowed(input.DispatchGuard);
            dispatchGuardRejected |= !allowed;
            return allowed;
        }
        if (!GuardDispatch())
        {
            return new DocumentationCampaignProposalOutcome(
                DocumentationCampaignProposalOutcomeKind.StateConflict,
                "campaign.reservation.conflict",
                selectedState.WorkItemKey,
                reserved.Artifact);
        }
        var coordinator = new CampaignScribeExecutionCoordinator(invocation, input.InvocationAllowance,
            input.Store, input.SettlementToken, GuardDispatch, executionStartedAt);
        var prepared = await DocumentationScribeComposition.PrepareCampaignAsync(
            selectedAudit!, ownedRequestUtf8Json, invocation, input.ConfiguredAgentEntrypoint,
            input.RuntimeOptions, exchange, input.TimeProvider, input.ExecutionToken,
            GuardDispatch, coordinator).ConfigureAwait(false);
        if (dispatchGuardRejected || coordinator.Conflict)
        {
            return new DocumentationCampaignProposalOutcome(
                DocumentationCampaignProposalOutcomeKind.StateConflict,
                "campaign.reservation.conflict",
                selectedState.WorkItemKey,
                reserved.Artifact);
        }
        CampaignProcessBoundaryHooks.Reach(CampaignProcessBoundaryHooks.ProposalAfterProviderBeforeResultTransition);
        CampaignTransitionResult completed;
        var proposalResult = prepared.CompletionAuthority?.Outcome?.RunResult.Terminal
            is DocumentationScribeProposalTerminal;
        CampaignProcessBoundaryHooks.Reach(proposalResult
            ? CampaignProcessBoundaryHooks.ProposalAfterProviderBeforeProposalTransition
            : CampaignProcessBoundaryHooks.ProposalAfterProviderBeforeClosedTransition);
        var callerCancelled = prepared.Kind == DocumentationCampaignPreparationKind.StopCancelled
            || prepared.CompletionAuthority?.Kind == CampaignProviderCompletionKind.CallerCancelled
            || prepared.CompletionAuthority is
            {
                Kind: CampaignProviderCompletionKind.Ordinary,
                Outcome.RunResult.Terminal: DocumentationScribeCancelledTerminal { Code: DocumentationScribeCancellationCode.Caller }
            };
        if (!callerCancelled && (coordinator.Scope.Allowance.HasCheckedStop || coordinator.Scope.LifetimeDeadlineReached
            || coordinator.LifetimeElapsedExhausted
            || prepared.Kind == DocumentationCampaignPreparationKind.StopLifetimeExhausted
            || coordinator.LifetimeStop && !proposalResult))
        {
            completed = CampaignStateReducer.PauseProviderInvocation(invocation, coordinator.CurrentElapsed,
                coordinator.LifetimeStop || coordinator.Scope.LifetimeDeadlineReached || coordinator.LifetimeElapsedExhausted
                    || prepared.Kind == DocumentationCampaignPreparationKind.StopLifetimeExhausted);
        }
        else if (prepared.Kind == DocumentationCampaignPreparationKind.Completion && prepared.CompletionAuthority is not null)
        {
            completed = CampaignStateReducer.CompleteProviderInvocation(
                invocation.AcceptedCheckpoint.Artifact, prepared.CompletionAuthority,
                input.ExecutionCapability, input.StyleConfigurationId, input.StyleConfigurationProjection,
                input.PlanningInput, input.AcceptedPlan);
        }
        else if (prepared.Kind is DocumentationCampaignPreparationKind.StopCancelled
            or DocumentationCampaignPreparationKind.StopTimedOut
            or DocumentationCampaignPreparationKind.StopBudgetExhausted)
        {
            var stop = prepared.Kind switch
            {
                DocumentationCampaignPreparationKind.StopCancelled => CampaignTerminalKind.Cancelled,
                DocumentationCampaignPreparationKind.StopTimedOut => CampaignTerminalKind.Timeout,
                _ => CampaignTerminalKind.Exhausted,
            };
            completed = CampaignStateReducer.StopProviderBeforePhysicalDispatch(invocation, stop, coordinator.CurrentElapsed);
        }
        else
        {
            return Outcome(DocumentationCampaignProposalOutcomeKind.HostContractError, "campaign.preparation.invalid");
        }
        if (completed.Kind == CampaignTransitionKind.Rejected)
        {
            return Outcome(
                DocumentationCampaignProposalOutcomeKind.HostContractError,
                "campaign.settlement.invalid");
        }

        CampaignCheckpointAcceptanceResult settled;
        using (CampaignProcessBoundaryHooks.EnterReplacementScope(proposalResult
                   ? CampaignProcessBoundaryHooks.ProposalResultReplacementScope
                   : CampaignProcessBoundaryHooks.ProposalClosedReplacementScope))
        {
            settled = await CampaignCheckpointAcceptance.AcceptAsync(
                input.Store, completed, input.SettlementToken).ConfigureAwait(false);
        }
        if (settled.Kind == CampaignCheckpointAcceptanceKind.Accepted && settled.Artifact is not null)
        {
            CampaignProcessBoundaryHooks.Reach(CampaignProcessBoundaryHooks.ProposalAfterResultReadback);
            CampaignProcessBoundaryHooks.Reach(proposalResult
                ? CampaignProcessBoundaryHooks.ProposalAfterProposalReadback
                : CampaignProcessBoundaryHooks.ProposalAfterClosedReadback);
            if (coordinator.Scope.Allowance.HasCheckedStop && settled.Artifact.State.TerminalOutcome is null)
                return new(DocumentationCampaignProposalOutcomeKind.InvocationBudgetExhausted,
                    "campaign.invocation-budget-exhausted", selectedState.WorkItemKey, settled.Artifact);
            return FromArtifact(settled.Artifact, selectedState.WorkItemKey);
        }

        var settlementFailure = settled.Kind is CampaignCheckpointAcceptanceKind.Conflict
            or CampaignCheckpointAcceptanceKind.InvalidRead
            or CampaignCheckpointAcceptanceKind.Unreadable
            or CampaignCheckpointAcceptanceKind.WriteRejected
            ? Outcome(DocumentationCampaignProposalOutcomeKind.StateConflict, "campaign.settlement.conflict")
            : Outcome(DocumentationCampaignProposalOutcomeKind.AmbiguousDispatch, "campaign.settlement.unconfirmed");
        return new DocumentationCampaignProposalOutcome(
            settlementFailure.Kind, settlementFailure.Code, checkpointFailure: settled.Kind);
    }

    private static bool TrySelectAudit(
        DocumentationScribeAuditAuthority auditAuthority,
        CampaignPlanningInput planningInput,
        CampaignPlanningWorkItem planWork,
        out DocumentationScribeSelectedAudit? selectedAudit)
    {
        selectedAudit = null;
        try
        {
            var targetFact = planWork.Targets.Single();
            var target = planningInput.Classifications.Targets.Single(candidate =>
                candidate.SymbolRef == targetFact.SymbolRef);
            selectedAudit = auditAuthority.Select(target);
            return true;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            return false;
        }
    }

    private static DocumentationCampaignProposalOutcome FromArtifact(CampaignCheckpointArtifact artifact, string workKey)
    {
        var work = artifact.State.WorkItems.Single(item => item.WorkItemKey == workKey);
        if (work.Status == CampaignWorkStatus.ProposalComplete)
            return new(DocumentationCampaignProposalOutcomeKind.ProposalReady, "campaign.proposal.ready", workKey, artifact);
        if (work.ClosedOutcome is
            {
                Code: CampaignWorkOutcomeCode.ProviderFailure,
                ProviderDisposition: CampaignProviderFinalDisposition.Retryable
            } && work.AttemptDisposition == CampaignAttemptDisposition.Open && artifact.State.TerminalOutcome is null)
            return new(DocumentationCampaignProposalOutcomeKind.RetryableStop, "campaign.provider.retryable", workKey, artifact);
        return FromTerminal(artifact);
    }

    private static DocumentationCampaignProposalOutcome FromTerminal(CampaignCheckpointArtifact artifact) =>
        artifact.State.TerminalOutcome switch
        {
            { Kind: CampaignTerminalKind.Cancelled } => new(DocumentationCampaignProposalOutcomeKind.Cancelled, "campaign.cancelled", artifact: artifact),
            { Kind: CampaignTerminalKind.Timeout } => new(DocumentationCampaignProposalOutcomeKind.TimedOut, "campaign.timed-out", artifact: artifact),
            { Kind: CampaignTerminalKind.Exhausted } => new(DocumentationCampaignProposalOutcomeKind.BudgetExhausted, "campaign.exhausted", artifact: artifact),
            { Reason: CampaignTerminalReason.NoWork } => new(DocumentationCampaignProposalOutcomeKind.NoWork, "campaign.no-work", artifact: artifact),
            { Kind: CampaignTerminalKind.Complete, Reason: CampaignTerminalReason.AllWorkClosed or CampaignTerminalReason.Unresolved }
                when artifact.State.WorkItems.All(item => item.ClosedOutcome?.Stage == CampaignWorkOutcomeStage.Planning) =>
                new(DocumentationCampaignProposalOutcomeKind.UnsupportedOnly, "campaign.unsupported-only", artifact: artifact),
            _ => new(DocumentationCampaignProposalOutcomeKind.TerminalStop, "campaign.terminal", artifact: artifact),
        };

    private static DocumentationCampaignProposalOutcome Outcome(
        DocumentationCampaignProposalOutcomeKind kind, string code) => new(kind, code);

    private static bool DispatchAllowed(Func<bool>? guard)
    {
        try
        {
            return guard?.Invoke() ?? true;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            return false;
        }
    }

}
