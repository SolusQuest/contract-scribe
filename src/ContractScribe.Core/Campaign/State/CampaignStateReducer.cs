using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ContractScribe.Core;

public enum CampaignTransitionKind
{
    Applied,
    Unchanged,
    Rejected,
}

public enum CampaignTransitionFailure
{
    None,
    InvalidPredecessor,
    InvalidCorrelation,
    InvalidAuthority,
    BudgetExhausted,
    ProjectionCapacityUnavailable,
    RevisionOverflow,
    ConflictingReplay,
}

public sealed class CampaignTransitionResult
{
    internal CampaignTransitionResult(
        CampaignTransitionKind kind,
        CampaignCheckpointArtifact predecessor,
        CampaignCheckpointArtifact artifact,
        CampaignTransitionFailure failure,
        DocumentationScribeAttemptId? attemptId = null)
    {
        Kind = kind;
        Predecessor = predecessor;
        Artifact = artifact;
        Failure = failure;
        AttemptId = attemptId;
    }

    public CampaignTransitionKind Kind { get; }
    internal CampaignCheckpointArtifact Predecessor { get; }
    public CampaignCheckpointArtifact Artifact { get; }
    public CampaignTransitionFailure Failure { get; }
    internal DocumentationScribeAttemptId? AttemptId { get; }
}

public sealed class CampaignPatchInvocationAuthority
{
    internal CampaignPatchInvocationAuthority(
        CampaignAcceptedCheckpoint acceptedCheckpoint,
        string patchRequestSha256,
        long expectedCheckpointRevision)
    {
        AcceptedCheckpoint = acceptedCheckpoint;
        PatchRequestSha256 = patchRequestSha256;
        ExpectedCheckpointRevision = expectedCheckpointRevision;
    }

    internal CampaignAcceptedCheckpoint AcceptedCheckpoint { get; }
    internal string PatchRequestSha256 { get; }
    internal long ExpectedCheckpointRevision { get; }
    internal bool DispatchStarted => AcceptedCheckpoint.ReservationLifecycle.IsDispatchStarted;

    public bool TryBeginDispatch() =>
        AcceptedCheckpoint.ReservationLifecycle.TryBeginDispatch();

    public override string ToString() => nameof(CampaignPatchInvocationAuthority);
}

public sealed class CampaignProviderInvocationAuthority
{
    private int registrarGrant = 1;

    internal CampaignProviderInvocationAuthority(
        CampaignAcceptedCheckpoint acceptedCheckpoint,
        string scribeRequestSha256,
        DocumentationScribeAttemptId attemptId,
        CampaignScribeExecutionCapability executionCapability,
        string styleConfigurationId,
        JsonElement validatedStyleConfigurationProjection,
        CampaignPlanningInput planningInput,
        CampaignWorkPlan acceptedPlan,
        DocumentationScribeRequest request)
    {
        AcceptedCheckpoint = acceptedCheckpoint;
        ScribeRequestSha256 = scribeRequestSha256;
        AttemptId = attemptId;
        ExecutionCapability = executionCapability;
        StyleConfigurationId = styleConfigurationId;
        ValidatedStyleConfigurationProjection = validatedStyleConfigurationProjection.Clone();
        PlanningInput = planningInput;
        AcceptedPlan = acceptedPlan;
        Request = request;
    }

    internal CampaignAcceptedCheckpoint AcceptedCheckpoint { get; }
    internal string ScribeRequestSha256 { get; }
    internal DocumentationScribeAttemptId AttemptId { get; }
    internal CampaignScribeExecutionCapability ExecutionCapability { get; }
    internal string StyleConfigurationId { get; }
    internal JsonElement ValidatedStyleConfigurationProjection { get; }
    internal CampaignPlanningInput PlanningInput { get; }
    internal CampaignWorkPlan AcceptedPlan { get; }
    internal DocumentationScribeRequest Request { get; }
    internal bool DispatchStarted => AcceptedCheckpoint.ReservationLifecycle.IsDispatchStarted;
    internal bool LifecycleAvailable => AcceptedCheckpoint.ReservationLifecycle.IsAvailable;

    public bool TryBeginDispatch(out DocumentationScribeAttemptId attemptId)
    {
        if (!AcceptedCheckpoint.ReservationLifecycle.TryBeginDispatch())
        {
            attemptId = default;
            return false;
        }

        attemptId = AttemptId;
        return true;
    }

    public CampaignProviderCompletionRegistrar? TryCreateCompletionRegistrar() =>
        Interlocked.CompareExchange(ref registrarGrant, 0, 1) == 1
            ? new CampaignProviderCompletionRegistrar(this)
            : null;

    internal bool ValidatePreparation(
        DocumentationScribeRequest request,
        string providerConfigurationId,
        string modelConfigurationId,
        string scribeProtocolId)
    {
        var projection = ExecutionCapability.Projection;
        if (!string.Equals(request.ArtifactSha256, ScribeRequestSha256, StringComparison.Ordinal)
            || !string.Equals(providerConfigurationId, projection.ProviderConfigurationId, StringComparison.Ordinal)
            || !string.Equals(modelConfigurationId, projection.ModelConfigurationId, StringComparison.Ordinal)
            || !string.Equals(scribeProtocolId, projection.ScribeProtocolId, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            _ = CampaignStateFactory.ValidateProviderRequestAuthority(
                AcceptedCheckpoint.Artifact.State,
                ExecutionCapability,
                StyleConfigurationId,
                ValidatedStyleConfigurationProjection,
                PlanningInput,
                AcceptedPlan,
                AcceptedCheckpoint.Artifact.State.ActiveReservation is CampaignProviderReservation reservation
                    ? reservation.WorkItemKey
                    : string.Empty,
                request);
            return true;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            return false;
        }
    }

    internal bool ValidateOutcome(DocumentationScribeValidatedRunOutcome outcome) =>
        string.Equals(outcome.Request.ArtifactSha256, ScribeRequestSha256, StringComparison.Ordinal)
        && outcome.RunResult.AttemptId == AttemptId
        && ValidatePreparation(
            outcome.Request,
            outcome.RunResult.RunEnvelope.ProviderConfigurationId,
            outcome.RunResult.RunEnvelope.ModelConfigurationId,
            outcome.RunResult.RunEnvelope.ScribeProtocolId);

    internal bool TryCompleteLifecycle(bool expectedDispatchStarted) =>
        AcceptedCheckpoint.ReservationLifecycle.TryComplete(expectedDispatchStarted);

    public override string ToString() => nameof(CampaignProviderInvocationAuthority);
}

/// <summary>
/// Pure deterministic Campaign State v1 transitions. This type never performs
/// I/O and never accepts independently re-projected provider or Patch facts.
/// </summary>
public static class CampaignStateReducer
{
    public static CampaignTransitionResult ApplyTransition(
        CampaignCheckpointArtifact current,
        CampaignTransitionResult transition)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(transition);
        if (!IsExactArtifact(current)
            || transition.Kind == CampaignTransitionKind.Rejected
            || !IsExactArtifact(transition.Predecessor)
            || !IsExactArtifact(transition.Artifact))
        {
            return Reject(current, CampaignTransitionFailure.InvalidPredecessor);
        }

        if (ArtifactsEqual(current, transition.Artifact))
        {
            return new CampaignTransitionResult(
                CampaignTransitionKind.Unchanged,
                current,
                current,
                CampaignTransitionFailure.None,
                transition.AttemptId);
        }

        return ArtifactsEqual(current, transition.Predecessor)
            ? transition
            : Reject(current, CampaignTransitionFailure.ConflictingReplay);
    }

    public static CampaignProviderInvocationAuthority CreateProviderInvocationAuthority(
        CampaignAcceptedCheckpoint acceptedCheckpoint,
        CampaignScribeExecutionCapability executionCapability,
        string styleConfigurationId,
        JsonElement validatedStyleConfigurationProjection,
        CampaignPlanningInput planningInput,
        CampaignWorkPlan acceptedPlan,
        DocumentationScribeRequest request)
    {
        ArgumentNullException.ThrowIfNull(acceptedCheckpoint);
        ArgumentNullException.ThrowIfNull(executionCapability);
        ArgumentNullException.ThrowIfNull(planningInput);
        ArgumentNullException.ThrowIfNull(acceptedPlan);
        ArgumentNullException.ThrowIfNull(request);
        var artifact = acceptedCheckpoint.Artifact;
        if (!IsExactArtifact(artifact)
            || artifact.State.ActiveReservation is not CampaignProviderReservation reservation
            || !string.Equals(reservation.ScribeRequestSha256, request.ArtifactSha256, StringComparison.Ordinal))
        {
            throw new ArgumentException("The accepted provider reservation does not authorize this invocation.");
        }

        _ = CampaignStateFactory.ValidateProviderRequestAuthority(
            artifact.State,
            executionCapability,
            styleConfigurationId,
            validatedStyleConfigurationProjection,
            planningInput,
            acceptedPlan,
            reservation.WorkItemKey,
            request);
        var work = artifact.State.WorkItems.Single(item =>
            string.Equals(item.WorkItemKey, reservation.WorkItemKey, StringComparison.Ordinal));
        if (CreateAttemptId(
                artifact.State.Snapshot.ExecutionCommitmentSha256,
                executionCapability.Projection,
                reservation.WorkItemKey,
                work.OuterAttemptCount) != reservation.AttemptId
            || !acceptedCheckpoint.TryIssueInvocation())
        {
            throw new ArgumentException("The accepted provider reservation does not grant this dispatch.");
        }

        return new CampaignProviderInvocationAuthority(
            acceptedCheckpoint,
            reservation.ScribeRequestSha256,
            reservation.AttemptId,
            executionCapability,
            styleConfigurationId,
            validatedStyleConfigurationProjection,
            planningInput,
            acceptedPlan,
            request);
    }

    public static CampaignPatchInvocationAuthority CreatePatchInvocationAuthority(
        CampaignAcceptedCheckpoint acceptedCheckpoint,
        DocumentationPatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(acceptedCheckpoint);
        ArgumentNullException.ThrowIfNull(request);
        var persistedReservedArtifact = acceptedCheckpoint.Artifact;
        if (!IsExactArtifact(persistedReservedArtifact)
            || persistedReservedArtifact.State.ActiveReservation is not CampaignPatchReservation reservation
            || reservation.PatchAttemptCount != 1
            || reservation.ExpectedCheckpointRevision != persistedReservedArtifact.CheckpointRevision
            || !string.Equals(reservation.PatchRequestSha256, request.ArtifactSha256, StringComparison.Ordinal))
        {
            throw new ArgumentException("The persisted Patch reservation does not authorize this invocation.");
        }
        CampaignStateFactory.ValidatePatchRequestAuthority(persistedReservedArtifact.State, request);
        if (CampaignStateFactory.HasKnownCompletedPatchProjection(
                persistedReservedArtifact.State,
                request)
            || !acceptedCheckpoint.TryIssueInvocation())
        {
            throw new ArgumentException("The accepted Patch reservation does not grant this dispatch.");
        }

        return new CampaignPatchInvocationAuthority(
            acceptedCheckpoint,
            request.ArtifactSha256,
            reservation.ExpectedCheckpointRevision);
    }

    public static CampaignTransitionResult AdmitProviderInvocation(
        CampaignCheckpointArtifact predecessor,
        CampaignScribeExecutionCapability executionCapability,
        string styleConfigurationId,
        JsonElement validatedStyleConfigurationProjection,
        CampaignPlanningInput planningInput,
        CampaignWorkPlan acceptedPlan,
        string workItemKey,
        DocumentationScribeRequest request)
    {
        ArgumentNullException.ThrowIfNull(predecessor);
        ArgumentNullException.ThrowIfNull(executionCapability);
        ArgumentNullException.ThrowIfNull(planningInput);
        ArgumentNullException.ThrowIfNull(acceptedPlan);
        ArgumentNullException.ThrowIfNull(request);
        if (!IsExactArtifact(predecessor))
        {
            return Reject(predecessor, CampaignTransitionFailure.InvalidPredecessor);
        }

        var state = predecessor.State;
        try
        {
            _ = CampaignStateFactory.ValidateProviderRequestAuthority(
                state,
                executionCapability,
                styleConfigurationId,
                validatedStyleConfigurationProjection,
                planningInput,
                acceptedPlan,
                workItemKey,
                request);
            var work = state.WorkItems.SingleOrDefault(item =>
                string.Equals(item.WorkItemKey, workItemKey, StringComparison.Ordinal));
            if (state.TerminalOutcome is not null
                || state.ActiveReservation is not null
                || work is not { Status: CampaignWorkStatus.Planned }
                || !ValidExecutionCapability(executionCapability))
            {
                return Reject(predecessor, CampaignTransitionFailure.InvalidCorrelation);
            }

            var patchContext = new DocumentationPatchContext(
                request.Context.RepositoryContextRef,
                request.Context.InputIdentity,
                request.Context.TargetProfile);
            var projectionAvailability = CampaignStateFactory.EvaluateProviderProjectionAvailability(
                state,
                patchContext);
            if (projectionAvailability == CampaignTrustedProposalAdmissionKind.Invalid)
            {
                return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
            }
            if (projectionAvailability == CampaignTrustedProposalAdmissionKind.OverBound)
            {
                return Reject(predecessor, CampaignTransitionFailure.ProjectionCapacityUnavailable);
            }

            if (work.OuterAttemptCount >= state.ConfiguredCeilings.CampaignBudget.MaximumAttemptsPerTarget)
            {
                return Exhausted(predecessor);
            }

            if (work.CandidateAttemptCount >= state.ConfiguredCeilings.CampaignBudget.MaximumCandidatesPerBlock)
            {
                return Exhausted(predecessor);
            }

            if (!HasProviderCompletionRevisionHeadroom(state))
            {
                return Exhausted(predecessor);
            }

            var budget = CampaignBudgetAccounting.ReserveProviderInvocation(state);
            if (budget.Kind != CampaignBudgetDecisionKind.Admitted)
            {
                return budget.Kind == CampaignBudgetDecisionKind.Exhausted
                    ? Exhausted(predecessor)
                    : Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
            }

            var nextRevision = NextRevision(state.CheckpointRevision);
            var nextOrdinal = checked(work.OuterAttemptCount + 1);
            var attemptId = CreateAttemptId(
                state.Snapshot.ExecutionCommitmentSha256,
                executionCapability.Projection,
                workItemKey,
                nextOrdinal);
            var workItems = state.WorkItems.Select(item =>
                string.Equals(item.WorkItemKey, workItemKey, StringComparison.Ordinal)
                    ? item with { OuterAttemptCount = nextOrdinal }
                    : item).ToImmutableArray();
            var reservation = new CampaignProviderReservation(
                workItemKey,
                request.ArtifactSha256,
                attemptId,
                budget.Exposure!);
            return Applied(predecessor, CreateState(
                state,
                nextRevision,
                budget.Charges!,
                workItems,
                reservation,
                state.CandidateObservation,
                state.CumulativeOutcome,
                state.TerminalOutcome,
                state.Predecessor), attemptId);
        }
        catch (OverflowException)
        {
            return Reject(predecessor, CampaignTransitionFailure.RevisionOverflow);
        }
        catch (Exception exception) when (IsBoundedContractFailure(exception))
        {
            return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
        }
    }

    public static CampaignTransitionResult CompleteProviderInvocation(
        CampaignCheckpointArtifact predecessor,
        CampaignProviderCompletionAuthority completionAuthority,
        CampaignScribeExecutionCapability executionCapability,
        string styleConfigurationId,
        JsonElement validatedStyleConfigurationProjection,
        CampaignPlanningInput planningInput,
        CampaignWorkPlan acceptedPlan,
        CampaignTerminalKind? simultaneousStop = null)
    {
        ArgumentNullException.ThrowIfNull(predecessor);
        ArgumentNullException.ThrowIfNull(completionAuthority);
        ArgumentNullException.ThrowIfNull(executionCapability);
        ArgumentNullException.ThrowIfNull(planningInput);
        ArgumentNullException.ThrowIfNull(acceptedPlan);
        if (!IsExactArtifact(predecessor))
        {
            return Reject(predecessor, CampaignTransitionFailure.InvalidPredecessor);
        }

        if (!ValidSimultaneousStop(simultaneousStop))
        {
            return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
        }

        var state = predecessor.State;
        var invocationAuthority = completionAuthority.Invocation;
        var outcome = completionAuthority.Outcome;
        var ordinary = completionAuthority.Kind == CampaignProviderCompletionKind.Ordinary;
        if (ordinary && outcome is null
            || !ReferenceEquals(invocationAuthority.ExecutionCapability, executionCapability)
            || !ArtifactsEqual(predecessor, invocationAuthority.AcceptedCheckpoint.Artifact)
            || state.ActiveReservation is not CampaignProviderReservation reservation
            || outcome is not null && !string.Equals(reservation.ScribeRequestSha256, outcome.Request.ArtifactSha256, StringComparison.Ordinal)
            || outcome is not null && reservation.AttemptId != outcome.RunResult.AttemptId
            || reservation.AttemptId != invocationAuthority.AttemptId
            || !string.Equals(reservation.ScribeRequestSha256, invocationAuthority.ScribeRequestSha256, StringComparison.Ordinal)
            || !string.Equals(reservation.WorkItemKey,
                state.WorkItems.SingleOrDefault(item => item.Status == CampaignWorkStatus.Planned
                    && string.Equals(item.WorkItemKey, reservation.WorkItemKey, StringComparison.Ordinal))?.WorkItemKey,
                StringComparison.Ordinal))
        {
            return Reject(predecessor, CampaignTransitionFailure.InvalidCorrelation);
        }

        try
        {
            _ = CampaignStateFactory.ValidateProviderRequestAuthority(
                state,
                executionCapability,
                styleConfigurationId,
                validatedStyleConfigurationProjection,
                planningInput,
                acceptedPlan,
                reservation.WorkItemKey,
                outcome?.Request ?? invocationAuthority.Request);
            if (!ordinary)
            {
                return CompleteX1ProviderInvocation(
                    predecessor,
                    completionAuthority,
                    reservation,
                    simultaneousStop);
            }

            var ordinaryOutcome = outcome!;
            var envelope = ordinaryOutcome.RunResult.RunEnvelope;
            var reservedWork = state.WorkItems.Single(item =>
                string.Equals(item.WorkItemKey, reservation.WorkItemKey, StringComparison.Ordinal));
            var executionAuthority = executionCapability.Projection;
            if (!string.Equals(envelope.ProviderConfigurationId, executionAuthority.ProviderConfigurationId, StringComparison.Ordinal)
                || !string.Equals(envelope.ModelConfigurationId, executionAuthority.ModelConfigurationId, StringComparison.Ordinal)
                || !string.Equals(envelope.ScribeProtocolId, executionAuthority.ScribeProtocolId, StringComparison.Ordinal)
                || !string.Equals(envelope.ToolPolicyId, executionAuthority.ToolPolicyId, StringComparison.Ordinal)
                || CreateAttemptId(
                    state.Snapshot.ExecutionCommitmentSha256,
                    executionAuthority,
                    reservation.WorkItemKey,
                    reservedWork.OuterAttemptCount) != reservation.AttemptId)
            {
                return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
            }

            var terminal = ordinaryOutcome.RunResult.Terminal;
            CampaignTrustedProposal? trustedProposal = null;
            var proposalAdmission = CampaignTrustedProposalAdmissionKind.Admitted;
            if (terminal is DocumentationScribeProposalTerminal)
            {
                trustedProposal = CampaignStateFactory.CreateTrustedProposal(
                    state,
                    executionCapability,
                    styleConfigurationId,
                    validatedStyleConfigurationProjection,
                    planningInput,
                    acceptedPlan,
                    reservation.WorkItemKey,
                    ordinaryOutcome.Request,
                    ordinaryOutcome.RunResult);
                var patchContext = new DocumentationPatchContext(
                    ordinaryOutcome.Request.Context.RepositoryContextRef,
                    ordinaryOutcome.Request.Context.InputIdentity,
                    ordinaryOutcome.Request.Context.TargetProfile);
                proposalAdmission = CampaignStateFactory.EvaluateTrustedProposalAdmission(
                    state,
                    reservation.WorkItemKey,
                    trustedProposal,
                    patchContext);
                if (proposalAdmission == CampaignTrustedProposalAdmissionKind.Invalid)
                {
                    return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
                }
            }

            var settlement = CampaignBudgetAccounting.SettleProviderInvocation(
                state,
                ordinaryOutcome,
                completionAuthority.ActiveElapsedMilliseconds);
            if (settlement.Kind == CampaignBudgetDecisionKind.Invalid)
            {
                return Reject(predecessor, CampaignTransitionFailure.InvalidCorrelation);
            }

            var workItems = state.WorkItems;
            CampaignTerminalOutcome? campaignTerminal = null;
            if (terminal is DocumentationScribeProposalTerminal)
            {
                var proposal = trustedProposal!;
                if (settlement.Kind == CampaignBudgetDecisionKind.Exhausted)
                {
                    workItems = ReplaceWork(
                        workItems,
                        reservation.WorkItemKey,
                        CampaignWorkStatus.Closed,
                        null,
                        CreateClosedScribeOverboundOutcome(
                            ordinaryOutcome,
                            reservation.WorkItemKey,
                            proposal.ProposalCommitmentSha256));
                    campaignTerminal = new CampaignTerminalOutcome(
                        CampaignTerminalKind.Exhausted,
                        CampaignTerminalReason.Budget);
                }
                else
                {
                    if (proposalAdmission == CampaignTrustedProposalAdmissionKind.OverBound)
                    {
                        workItems = ReplaceWork(
                            workItems,
                            reservation.WorkItemKey,
                            CampaignWorkStatus.Closed,
                            null,
                            CreateClosedScribeOverboundOutcome(
                                ordinaryOutcome,
                                reservation.WorkItemKey,
                                proposal.ProposalCommitmentSha256));
                        campaignTerminal = new CampaignTerminalOutcome(
                            CampaignTerminalKind.Exhausted,
                            CampaignTerminalReason.Budget);
                    }
                    else
                    {
                        workItems = ReplaceWork(
                            workItems,
                            reservation.WorkItemKey,
                            CampaignWorkStatus.ProposalComplete,
                            proposal,
                            null);
                    }
                }
            }
            else
            {
                var closed = CreateClosedScribeOutcome(ordinaryOutcome, reservation.WorkItemKey);
                workItems = ReplaceWork(
                    workItems,
                    reservation.WorkItemKey,
                    CampaignWorkStatus.Closed,
                    null,
                    closed);
                campaignTerminal = terminal switch
                {
                    DocumentationScribeCancelledTerminal { Code: DocumentationScribeCancellationCode.Caller } =>
                        new CampaignTerminalOutcome(CampaignTerminalKind.Cancelled, CampaignTerminalReason.Caller),
                    DocumentationScribeCancelledTerminal =>
                        new CampaignTerminalOutcome(CampaignTerminalKind.Failed, CampaignTerminalReason.Host),
                    DocumentationScribeFailureTerminal { Code: DocumentationScribeFailureCode.Timeout } =>
                        new CampaignTerminalOutcome(CampaignTerminalKind.Timeout, CampaignTerminalReason.Deadline),
                    DocumentationScribeFailureTerminal { Code: DocumentationScribeFailureCode.Budget } =>
                        new CampaignTerminalOutcome(CampaignTerminalKind.Exhausted, CampaignTerminalReason.Budget),
                    DocumentationScribeFailureTerminal
                    {
                        Code: DocumentationScribeFailureCode.Provider,
                        ProviderFinalDisposition: DocumentationScribeProviderFinalDisposition.Retryable,
                    } => null,
                    _ => CompleteWhenResolved(workItems),
                };
                if (campaignTerminal is null
                    && settlement.Kind == CampaignBudgetDecisionKind.Exhausted)
                {
                    campaignTerminal = new CampaignTerminalOutcome(
                        CampaignTerminalKind.Exhausted,
                        CampaignTerminalReason.Budget);
                }
            }

            var transition = Applied(predecessor, CreateState(
                state,
                NextRevision(state.CheckpointRevision),
                settlement.Charges!,
                workItems,
                null,
                state.CandidateObservation,
                state.CumulativeOutcome,
                campaignTerminal,
                state.Predecessor));
            if (!completionAuthority.TryConsume()
                || !invocationAuthority.TryCompleteLifecycle(invocationAuthority.DispatchStarted))
            {
                return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
            }

            return transition;
        }
        catch (OverflowException)
        {
            return Reject(predecessor, CampaignTransitionFailure.RevisionOverflow);
        }
        catch (Exception exception) when (IsBoundedContractFailure(exception))
        {
            return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
        }
    }

    private static CampaignTransitionResult CompleteX1ProviderInvocation(
        CampaignCheckpointArtifact predecessor,
        CampaignProviderCompletionAuthority completionAuthority,
        CampaignProviderReservation reservation,
        CampaignTerminalKind? simultaneousStop)
    {
        var state = predecessor.State;
        var invocation = completionAuthority.Invocation;
        var dispatched = invocation.DispatchStarted;
        if (!dispatched && completionAuthority.Kind is not (
                CampaignProviderCompletionKind.ProposalInvalid
                or CampaignProviderCompletionKind.HostFailure))
        {
            return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
        }

        var outcome = completionAuthority.Outcome;
        CampaignLineageCharges charges;
        var settlementExhausted = false;
        if (outcome is null)
        {
            charges = CampaignBudgetAccounting.SettleActiveConservatively(state);
        }
        else
        {
            if (outcome.RunResult.Terminal is not DocumentationScribeProposalTerminal)
            {
                return Reject(predecessor, CampaignTransitionFailure.InvalidCorrelation);
            }

            var settlement = CampaignBudgetAccounting.SettleProviderInvocation(
                state,
                outcome,
                completionAuthority.ActiveElapsedMilliseconds);
            if (settlement.Kind == CampaignBudgetDecisionKind.Invalid)
            {
                return Reject(predecessor, CampaignTransitionFailure.InvalidCorrelation);
            }

            charges = settlement.Charges!;
            settlementExhausted = settlement.Kind == CampaignBudgetDecisionKind.Exhausted;
        }

        var code = completionAuthority.Kind switch
        {
            CampaignProviderCompletionKind.ProposalInvalid => CampaignWorkOutcomeCode.ValidationFailure,
            CampaignProviderCompletionKind.HostFailure => CampaignWorkOutcomeCode.InternalFailure,
            CampaignProviderCompletionKind.CallerCancelled => CampaignWorkOutcomeCode.CancelledByCaller,
            CampaignProviderCompletionKind.ShutdownCancelled => CampaignWorkOutcomeCode.CancelledByShutdown,
            CampaignProviderCompletionKind.Timeout => CampaignWorkOutcomeCode.Timeout,
            CampaignProviderCompletionKind.BudgetExhausted => CampaignWorkOutcomeCode.BudgetExhausted,
            _ => throw new InvalidOperationException("Unsupported X1 completion kind."),
        };
        var closed = new CampaignWorkClosedOutcome(
            CampaignWorkOutcomeStage.Scribe,
            code,
            null,
            reservation.ScribeRequestSha256,
            reservation.AttemptId,
            null,
            null,
            null,
            reservation.WorkItemKey);
        var workItems = ReplaceWork(
            state.WorkItems,
            reservation.WorkItemKey,
            CampaignWorkStatus.Closed,
            null,
            closed);
        CampaignTerminalOutcome? terminal = completionAuthority.Kind switch
        {
            CampaignProviderCompletionKind.HostFailure or CampaignProviderCompletionKind.ShutdownCancelled =>
                new CampaignTerminalOutcome(CampaignTerminalKind.Failed, CampaignTerminalReason.Host),
            CampaignProviderCompletionKind.CallerCancelled =>
                new CampaignTerminalOutcome(CampaignTerminalKind.Cancelled, CampaignTerminalReason.Caller),
            CampaignProviderCompletionKind.Timeout =>
                new CampaignTerminalOutcome(CampaignTerminalKind.Timeout, CampaignTerminalReason.Deadline),
            CampaignProviderCompletionKind.BudgetExhausted =>
                new CampaignTerminalOutcome(CampaignTerminalKind.Exhausted, CampaignTerminalReason.Budget),
            _ => CompleteWhenResolved(workItems),
        };
        if (terminal is null && settlementExhausted)
        {
            terminal = new CampaignTerminalOutcome(CampaignTerminalKind.Exhausted, CampaignTerminalReason.Budget);
        }
        var transition = Applied(predecessor, CreateState(
            state,
            NextRevision(state.CheckpointRevision),
            charges,
            workItems,
            null,
            state.CandidateObservation,
            state.CumulativeOutcome,
            terminal,
            state.Predecessor));
        if (!completionAuthority.TryConsume()
            || !invocation.TryCompleteLifecycle(dispatched))
        {
            return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
        }

        return transition;
    }

    public static CampaignTransitionResult RetryProviderInvocation(
        CampaignCheckpointArtifact predecessor,
        CampaignAcceptedCheckpoint? acceptedCheckpoint,
        CampaignScribeExecutionCapability executionCapability,
        string styleConfigurationId,
        JsonElement validatedStyleConfigurationProjection,
        CampaignPlanningInput planningInput,
        CampaignWorkPlan acceptedPlan,
        string workItemKey,
        DocumentationScribeRequest request)
    {
        ArgumentNullException.ThrowIfNull(predecessor);
        ArgumentNullException.ThrowIfNull(executionCapability);
        ArgumentNullException.ThrowIfNull(planningInput);
        ArgumentNullException.ThrowIfNull(acceptedPlan);
        ArgumentNullException.ThrowIfNull(request);
        if (!IsExactArtifact(predecessor))
        {
            return Reject(predecessor, CampaignTransitionFailure.InvalidPredecessor);
        }

        var state = predecessor.State;
        var activeRetry = state.ActiveReservation as CampaignProviderReservation;
        var work = state.WorkItems.SingleOrDefault(item =>
            string.Equals(item.WorkItemKey, workItemKey, StringComparison.Ordinal));
        var closedRetry = state.ActiveReservation is null
            && work is
            {
                Status: CampaignWorkStatus.Closed,
                ClosedOutcome.Stage: CampaignWorkOutcomeStage.Scribe,
                ClosedOutcome.Code: CampaignWorkOutcomeCode.ProviderFailure,
                ClosedOutcome.ProviderDisposition: CampaignProviderFinalDisposition.Retryable,
            };
        CampaignTransitionResult Finish(CampaignTransitionResult transition) =>
            activeRetry is null
                ? transition
                : RetireReservationBeforeApply(predecessor, acceptedCheckpoint!, transition);
        if (state.TerminalOutcome is not null
            || work is null
            || activeRetry is not null && !string.Equals(activeRetry.WorkItemKey, workItemKey, StringComparison.Ordinal)
            || activeRetry is null && !closedRetry
            || !ValidExecutionCapability(executionCapability)
            || activeRetry is not null && (acceptedCheckpoint is null
                || !ArtifactsEqual(predecessor, acceptedCheckpoint.Artifact)))
        {
            return Reject(predecessor, CampaignTransitionFailure.InvalidCorrelation);
        }

        try
        {
            _ = CampaignStateFactory.ValidateProviderRequestAuthority(
                state,
                executionCapability,
                styleConfigurationId,
                validatedStyleConfigurationProjection,
                planningInput,
                acceptedPlan,
                workItemKey,
                request);

            var historicalAttempt = activeRetry?.AttemptId ?? work.ClosedOutcome!.AttemptId!.Value;
            if (CreateAttemptId(
                    state.Snapshot.ExecutionCommitmentSha256,
                    executionCapability.Projection,
                    workItemKey,
                    work.OuterAttemptCount) != historicalAttempt)
            {
                return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
            }

            if (!HasProviderCompletionRevisionHeadroom(state))
            {
                return Finish(Exhausted(predecessor));
            }

            var settledCharges = activeRetry is null
                ? state.LineageCharges
                : CampaignBudgetAccounting.SettleActiveConservatively(state);
            var patchContext = new DocumentationPatchContext(
                request.Context.RepositoryContextRef,
                request.Context.InputIdentity,
                request.Context.TargetProfile);
            var projectionAvailability = CampaignStateFactory.EvaluateProviderProjectionAvailability(
                state,
                patchContext);
            if (projectionAvailability == CampaignTrustedProposalAdmissionKind.Invalid)
            {
                return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
            }
            if (projectionAvailability == CampaignTrustedProposalAdmissionKind.OverBound)
            {
                if (activeRetry is null)
                {
                    return Reject(
                        predecessor,
                        CampaignTransitionFailure.ProjectionCapacityUnavailable);
                }

                return Finish(Applied(predecessor, CreateState(
                    state,
                    NextRevision(state.CheckpointRevision),
                    settledCharges,
                    state.WorkItems,
                    null,
                    state.CandidateObservation,
                    state.CumulativeOutcome,
                    state.TerminalOutcome,
                    state.Predecessor)));
            }

            if (work.CandidateAttemptCount >= state.ConfiguredCeilings.CampaignBudget.MaximumCandidatesPerBlock)
            {
                return Finish(Applied(predecessor, CreateState(
                    state,
                    NextRevision(state.CheckpointRevision),
                    settledCharges,
                    state.WorkItems,
                    null,
                    state.CandidateObservation,
                    state.CumulativeOutcome,
                    new CampaignTerminalOutcome(CampaignTerminalKind.Exhausted, CampaignTerminalReason.Budget),
                    state.Predecessor)));
            }

            var retryableWorkItems = state.WorkItems.Select(item =>
                string.Equals(item.WorkItemKey, workItemKey, StringComparison.Ordinal)
                    ? item with
                    {
                        Status = CampaignWorkStatus.Planned,
                        TrustedProposal = null,
                        ClosedOutcome = null,
                    }
                    : item).ToImmutableArray();
            var settled = CreateState(
                state,
                state.CheckpointRevision,
                settledCharges,
                retryableWorkItems,
                null,
                state.CandidateObservation,
                state.CumulativeOutcome,
                state.TerminalOutcome,
                state.Predecessor);
            var budget = CampaignBudgetAccounting.ReserveProviderInvocation(settled);
            if (budget.Kind == CampaignBudgetDecisionKind.Invalid)
            {
                return Reject(predecessor, CampaignTransitionFailure.RevisionOverflow);
            }

            if (budget.Kind == CampaignBudgetDecisionKind.Exhausted)
            {
                var exhausted = CreateState(
                    state,
                    NextRevision(state.CheckpointRevision),
                    settledCharges,
                    state.WorkItems,
                    null,
                    state.CandidateObservation,
                    state.CumulativeOutcome,
                    new CampaignTerminalOutcome(CampaignTerminalKind.Exhausted, CampaignTerminalReason.Budget),
                    state.Predecessor);
                return Finish(Applied(predecessor, exhausted));
            }

            if (work.OuterAttemptCount >= state.ConfiguredCeilings.CampaignBudget.MaximumAttemptsPerTarget)
            {
                return Finish(Applied(predecessor, CreateState(
                    state,
                    NextRevision(state.CheckpointRevision),
                    settledCharges,
                    state.WorkItems,
                    null,
                    state.CandidateObservation,
                    state.CumulativeOutcome,
                    new CampaignTerminalOutcome(CampaignTerminalKind.Exhausted, CampaignTerminalReason.Budget),
                    state.Predecessor)));
            }

            var ordinal = checked(work.OuterAttemptCount + 1);
            var nextRevision = NextRevision(state.CheckpointRevision);
            var attemptId = CreateAttemptId(
                state.Snapshot.ExecutionCommitmentSha256,
                executionCapability.Projection,
                workItemKey,
                ordinal);
            var workItems = retryableWorkItems.Select(item =>
                string.Equals(item.WorkItemKey, workItemKey, StringComparison.Ordinal)
                    ? item with
                    {
                        OuterAttemptCount = ordinal,
                        Status = CampaignWorkStatus.Planned,
                        TrustedProposal = null,
                        ClosedOutcome = null,
                    }
                    : item).ToImmutableArray();
            return Finish(Applied(predecessor, CreateState(
                state,
                nextRevision,
                budget.Charges!,
                workItems,
                new CampaignProviderReservation(
                    workItemKey,
                    request.ArtifactSha256,
                    attemptId,
                    budget.Exposure!),
                state.CandidateObservation,
                state.CumulativeOutcome,
                state.TerminalOutcome,
                state.Predecessor), attemptId));
        }
        catch (OverflowException)
        {
            return Reject(predecessor, CampaignTransitionFailure.RevisionOverflow);
        }
        catch (Exception exception) when (IsBoundedContractFailure(exception))
        {
            return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
        }
    }

    public static CampaignTransitionResult ReservePatchInvocation(
        CampaignCheckpointArtifact predecessor,
        DocumentationPatchRequest request,
        long elapsedMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(predecessor);
        ArgumentNullException.ThrowIfNull(request);
        if (!IsExactArtifact(predecessor))
        {
            return Reject(predecessor, CampaignTransitionFailure.InvalidPredecessor);
        }

        var state = predecessor.State;
        try
        {
            CampaignStateFactory.ValidatePatchRequestAuthority(state, request);
            if (state.ActiveReservation is null
                && CampaignStateFactory.HasKnownCompletedPatchProjection(state, request))
            {
                return Reject(predecessor, CampaignTransitionFailure.BudgetExhausted);
            }

            if (state.ActiveReservation is not null
                || !CanReservePatchFromTerminal(state))
            {
                return Reject(predecessor, CampaignTransitionFailure.InvalidCorrelation);
            }

            var budget = CampaignBudgetAccounting.ReservePatchInvocation(state, elapsedMilliseconds);
            if (budget.Kind != CampaignBudgetDecisionKind.Admitted)
            {
                if (state.TerminalOutcome is not null
                    && budget.Kind == CampaignBudgetDecisionKind.Exhausted)
                {
                    return Reject(predecessor, CampaignTransitionFailure.BudgetExhausted);
                }

                return budget.Kind == CampaignBudgetDecisionKind.Exhausted
                    ? Exhausted(predecessor)
                    : Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
            }

            var nextRevision = NextRevision(state.CheckpointRevision);
            var blockIds = request.Blocks.Select(block => block.BlockId).ToHashSet(StringComparer.Ordinal);
            if (state.WorkItems.Any(item =>
                blockIds.Contains(item.WorkItemKey)
                && item.Status is CampaignWorkStatus.ProposalComplete or CampaignWorkStatus.Accepted
                && item.CandidateAttemptCount >= state.ConfiguredCeilings.CampaignBudget.MaximumCandidatesPerBlock))
            {
                return Exhausted(predecessor);
            }

            var workItems = state.WorkItems.Select(item =>
                blockIds.Contains(item.WorkItemKey)
                    && item.Status is CampaignWorkStatus.ProposalComplete or CampaignWorkStatus.Accepted
                    ? item with { CandidateAttemptCount = checked(item.CandidateAttemptCount + 1) }
                    : item).ToImmutableArray();
            var reservable = CreateState(
                state,
                nextRevision,
                budget.Charges!,
                workItems,
                null,
                state.CandidateObservation,
                state.CumulativeOutcome,
                HasDurableKnownCompletion(state.WorkItems, state.KnownCompletedOperations)
                    ? new CampaignTerminalOutcome(CampaignTerminalKind.Exhausted, CampaignTerminalReason.Budget)
                    : state.TerminalOutcome,
                state.Predecessor);
            var reservation = CampaignStateFactory.CreatePatchReservation(
                reservable,
                request,
                patchAttemptCount: 1,
                elapsedMilliseconds);
            return Applied(predecessor, CreateState(
                reservable,
                nextRevision,
                budget.Charges!,
                workItems,
                reservation,
                state.CandidateObservation,
                state.CumulativeOutcome,
                null,
                state.Predecessor));
        }
        catch (OverflowException)
        {
            return Reject(predecessor, CampaignTransitionFailure.RevisionOverflow);
        }
        catch (CampaignStateValidationException exception)
            when (exception.Code == CampaignStateValidationCode.InvalidCorrelation)
        {
            return Reject(predecessor, CampaignTransitionFailure.InvalidCorrelation);
        }
        catch (Exception exception) when (IsBoundedContractFailure(exception))
        {
            return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
        }
    }

    public static CampaignTransitionResult CompletePatchInvocation(
        CampaignCheckpointArtifact predecessor,
        CampaignPatchInvocationAuthority invocationAuthority,
        DocumentationPatchRequest request,
        DocumentationPatchValidationResult result,
        long? activeElapsedMilliseconds,
        CampaignTerminalKind? simultaneousStop = null)
    {
        ArgumentNullException.ThrowIfNull(predecessor);
        ArgumentNullException.ThrowIfNull(invocationAuthority);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        if (!IsExactArtifact(predecessor))
        {
            return Reject(predecessor, CampaignTransitionFailure.InvalidPredecessor);
        }

        if (!ValidSimultaneousStop(simultaneousStop))
        {
            return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
        }

        var state = predecessor.State;
        if (!invocationAuthority.DispatchStarted
            || !ArtifactsEqual(predecessor, invocationAuthority.AcceptedCheckpoint.Artifact)
            || state.ActiveReservation is not CampaignPatchReservation correlatedReservation
            || correlatedReservation.ExpectedCheckpointRevision != invocationAuthority.ExpectedCheckpointRevision
            || !string.Equals(
                correlatedReservation.PatchRequestSha256,
                invocationAuthority.PatchRequestSha256,
                StringComparison.Ordinal)
            || !string.Equals(
                invocationAuthority.PatchRequestSha256,
                request.ArtifactSha256,
                StringComparison.Ordinal))
        {
            return Reject(predecessor, CampaignTransitionFailure.InvalidCorrelation);
        }

        try
        {
            var settlement = CampaignBudgetAccounting.SettlePatchInvocation(state, activeElapsedMilliseconds);
            if (settlement.Kind == CampaignBudgetDecisionKind.Invalid)
            {
                return Reject(predecessor, CampaignTransitionFailure.InvalidCorrelation);
            }

            var completion = CampaignStateFactory.CreatePatchCompletion(state, request, result);
            var workItems = state.WorkItems;
            var candidate = state.CandidateObservation;
            var cumulative = completion.CumulativeOutcome;
            var knownCompletedOperations = state.KnownCompletedOperations;
            CampaignTerminalOutcome? terminal = state.TerminalOutcome;
            if (completion.CandidateObservation is { } proposed)
            {
                if (settlement.Kind == CampaignBudgetDecisionKind.Exhausted
                    || !FitsCandidate(proposed, state.ConfiguredCeilings.CampaignBudget))
                {
                    terminal = new CampaignTerminalOutcome(
                        CampaignTerminalKind.Exhausted,
                        CampaignTerminalReason.Budget);
                    cumulative = completion.CumulativeOutcome with
                    {
                        Kind = CampaignCumulativeOutcomeKind.OverBound,
                    };
                    knownCompletedOperations = AddKnownPatchCompletion(
                        knownCompletedOperations,
                        cumulative);
                }
                else
                {
                    var acceptedKeys = proposed.AcceptedWorkItemKeys.ToHashSet(StringComparer.Ordinal);
                    workItems = state.WorkItems.Select(item =>
                        acceptedKeys.Contains(item.WorkItemKey)
                            && item.Status is CampaignWorkStatus.ProposalComplete or CampaignWorkStatus.Accepted
                            ? item with { Status = CampaignWorkStatus.Accepted }
                            : item).ToImmutableArray();
                    candidate = proposed;
                    terminal = CompleteWhenResolved(workItems);
                }
            }
            else
            {
                terminal = completion.CumulativeOutcome.Kind switch
                {
                    CampaignCumulativeOutcomeKind.Cancelled =>
                        new CampaignTerminalOutcome(CampaignTerminalKind.Cancelled, CampaignTerminalReason.Caller),
                    CampaignCumulativeOutcomeKind.Timeout =>
                        new CampaignTerminalOutcome(CampaignTerminalKind.Timeout, CampaignTerminalReason.Deadline),
                    _ => new CampaignTerminalOutcome(CampaignTerminalKind.Failed, CampaignTerminalReason.Host),
                };
            }

            if (HasDurableKnownCompletion(workItems, knownCompletedOperations))
            {
                terminal = new CampaignTerminalOutcome(
                    CampaignTerminalKind.Exhausted,
                    CampaignTerminalReason.Budget);
            }

            return Applied(predecessor, CreateState(
                state,
                NextRevision(state.CheckpointRevision),
                settlement.Charges!,
                workItems,
                null,
                candidate,
                cumulative,
                terminal,
                state.Predecessor,
                knownCompletedOperations));
        }
        catch (OverflowException)
        {
            return Reject(predecessor, CampaignTransitionFailure.RevisionOverflow);
        }
        catch (Exception exception) when (IsBoundedContractFailure(exception))
        {
            return Reject(predecessor, CampaignTransitionFailure.InvalidCorrelation);
        }
    }

    public static CampaignTransitionResult RetryPatchInvocation(
        CampaignCheckpointArtifact predecessor,
        CampaignAcceptedCheckpoint acceptedCheckpoint,
        DocumentationPatchRequest request,
        long elapsedMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(predecessor);
        ArgumentNullException.ThrowIfNull(acceptedCheckpoint);
        ArgumentNullException.ThrowIfNull(request);
        if (!IsExactArtifact(predecessor))
        {
            return Reject(predecessor, CampaignTransitionFailure.InvalidPredecessor);
        }

        var state = predecessor.State;
        if (state.TerminalOutcome is not null
            || state.ActiveReservation is not CampaignPatchReservation old
            || old.PatchAttemptCount != 1
            || !ArtifactsEqual(predecessor, acceptedCheckpoint.Artifact))
        {
            return Reject(predecessor, CampaignTransitionFailure.InvalidCorrelation);
        }

        try
        {
            CampaignStateFactory.ValidatePatchRequestAuthority(state, request);
            if (CampaignStateFactory.HasKnownCompletedPatchProjection(state, request))
            {
                return Reject(predecessor, CampaignTransitionFailure.BudgetExhausted);
            }

            var settledCharges = CampaignBudgetAccounting.SettleActiveConservatively(state);
            var budget = CampaignBudgetAccounting.ReservePatchInvocation(
                settledCharges,
                state.ConfiguredCeilings.CampaignBudget,
                elapsedMilliseconds);
            if (budget.Kind == CampaignBudgetDecisionKind.Invalid)
            {
                return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
            }

            if (budget.Kind == CampaignBudgetDecisionKind.Exhausted)
            {
                return RetireReservationBeforeApply(predecessor, acceptedCheckpoint, Applied(predecessor, CreateState(
                    state,
                    NextRevision(state.CheckpointRevision),
                    settledCharges,
                    state.WorkItems,
                    null,
                    state.CandidateObservation,
                    state.CumulativeOutcome,
                    new CampaignTerminalOutcome(CampaignTerminalKind.Exhausted, CampaignTerminalReason.Budget),
                    state.Predecessor)));
            }

            var nextRevision = NextRevision(state.CheckpointRevision);
            var blockIds = request.Blocks.Select(block => block.BlockId).ToHashSet(StringComparer.Ordinal);
            if (state.WorkItems.Any(item =>
                blockIds.Contains(item.WorkItemKey)
                && item.Status is CampaignWorkStatus.ProposalComplete or CampaignWorkStatus.Accepted
                && item.CandidateAttemptCount >= state.ConfiguredCeilings.CampaignBudget.MaximumCandidatesPerBlock))
            {
                return RetireReservationBeforeApply(predecessor, acceptedCheckpoint, Applied(predecessor, CreateState(
                    state,
                    nextRevision,
                    settledCharges,
                    state.WorkItems,
                    null,
                    state.CandidateObservation,
                    state.CumulativeOutcome,
                    new CampaignTerminalOutcome(CampaignTerminalKind.Exhausted, CampaignTerminalReason.Budget),
                    state.Predecessor)));
            }

            var workItems = state.WorkItems.Select(item =>
                blockIds.Contains(item.WorkItemKey)
                    && item.Status is CampaignWorkStatus.ProposalComplete or CampaignWorkStatus.Accepted
                    ? item with { CandidateAttemptCount = checked(item.CandidateAttemptCount + 1) }
                    : item).ToImmutableArray();
            var reservable = CreateState(
                state,
                nextRevision,
                budget.Charges!,
                workItems,
                null,
                state.CandidateObservation,
                state.CumulativeOutcome,
                HasDurableKnownCompletion(state.WorkItems, state.KnownCompletedOperations)
                    ? new CampaignTerminalOutcome(CampaignTerminalKind.Exhausted, CampaignTerminalReason.Budget)
                    : state.TerminalOutcome,
                state.Predecessor);
            var reservation = CampaignStateFactory.CreatePatchReservation(
                reservable,
                request,
                patchAttemptCount: 1,
                elapsedMilliseconds);
            return RetireReservationBeforeApply(predecessor, acceptedCheckpoint, Applied(predecessor, CreateState(
                reservable,
                nextRevision,
                budget.Charges!,
                workItems,
                reservation,
                state.CandidateObservation,
                state.CumulativeOutcome,
                state.TerminalOutcome,
                state.Predecessor)));
        }
        catch (OverflowException)
        {
            return Reject(predecessor, CampaignTransitionFailure.RevisionOverflow);
        }
        catch (CampaignStateValidationException exception)
            when (exception.Code == CampaignStateValidationCode.InvalidCorrelation)
        {
            return Reject(predecessor, CampaignTransitionFailure.InvalidCorrelation);
        }
        catch (Exception exception) when (IsBoundedContractFailure(exception))
        {
            return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
        }
    }

    public static CampaignTransitionResult CompletePatchHostInvocation(
        CampaignCheckpointArtifact predecessor,
        CampaignPatchInvocationAuthority invocationAuthority,
        DocumentationPatchRequest request,
        CampaignCumulativeOutcomeKind kind,
        long? activeElapsedMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(predecessor);
        ArgumentNullException.ThrowIfNull(invocationAuthority);
        ArgumentNullException.ThrowIfNull(request);
        if (!IsExactArtifact(predecessor)
            || !invocationAuthority.DispatchStarted
            || !ArtifactsEqual(predecessor, invocationAuthority.AcceptedCheckpoint.Artifact)
            || predecessor.State.ActiveReservation is not CampaignPatchReservation reservation
            || reservation.ExpectedCheckpointRevision != invocationAuthority.ExpectedCheckpointRevision
            || !string.Equals(reservation.PatchRequestSha256, invocationAuthority.PatchRequestSha256, StringComparison.Ordinal)
            || !string.Equals(request.ArtifactSha256, invocationAuthority.PatchRequestSha256, StringComparison.Ordinal))
        {
            return Reject(predecessor, CampaignTransitionFailure.InvalidCorrelation);
        }

        try
        {
            var settlement = CampaignBudgetAccounting.SettlePatchInvocation(
                predecessor.State,
                activeElapsedMilliseconds);
            if (settlement.Kind == CampaignBudgetDecisionKind.Invalid)
            {
                return Reject(predecessor, CampaignTransitionFailure.InvalidCorrelation);
            }

            var outcome = CampaignStateFactory.CreateHostPatchOutcome(predecessor.State, request, kind);
            var terminal = kind switch
            {
                CampaignCumulativeOutcomeKind.Cancelled =>
                    new CampaignTerminalOutcome(CampaignTerminalKind.Cancelled, CampaignTerminalReason.Caller),
                CampaignCumulativeOutcomeKind.Timeout =>
                    new CampaignTerminalOutcome(CampaignTerminalKind.Timeout, CampaignTerminalReason.Deadline),
                CampaignCumulativeOutcomeKind.HostFailure =>
                    new CampaignTerminalOutcome(CampaignTerminalKind.Failed, CampaignTerminalReason.Host),
                _ => throw new ArgumentException("Unsupported Patch host outcome.", nameof(kind)),
            };
            if (HasDurableKnownCompletion(
                predecessor.State.WorkItems,
                predecessor.State.KnownCompletedOperations))
            {
                terminal = new CampaignTerminalOutcome(
                    CampaignTerminalKind.Exhausted,
                    CampaignTerminalReason.Budget);
            }
            return Applied(predecessor, CreateState(
                predecessor.State,
                NextRevision(predecessor.CheckpointRevision),
                settlement.Charges!,
                predecessor.State.WorkItems,
                null,
                predecessor.State.CandidateObservation,
                outcome,
                terminal,
                predecessor.State.Predecessor));
        }
        catch (OverflowException)
        {
            return Reject(predecessor, CampaignTransitionFailure.RevisionOverflow);
        }
        catch (Exception exception) when (IsBoundedContractFailure(exception))
        {
            return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
        }
    }

    public static CampaignTransitionResult ApplyPatchRejection(
        CampaignCheckpointArtifact current,
        CampaignPatchInvocationAuthority invocationAuthority,
        CampaignPatchRejectionReduction reduction,
        long? activeElapsedMilliseconds = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(invocationAuthority);
        ArgumentNullException.ThrowIfNull(reduction);
        if (!IsExactArtifact(current) || !IsExactArtifact(reduction.Predecessor))
        {
            return Reject(current, CampaignTransitionFailure.InvalidPredecessor);
        }

        try
        {
            if (!invocationAuthority.DispatchStarted
                || !ArtifactsEqual(reduction.Predecessor, invocationAuthority.AcceptedCheckpoint.Artifact)
                || !string.Equals(reduction.PatchRequestSha256, invocationAuthority.PatchRequestSha256, StringComparison.Ordinal)
                || reduction.Predecessor.CheckpointRevision != invocationAuthority.ExpectedCheckpointRevision)
            {
                return Reject(current, CampaignTransitionFailure.InvalidAuthority);
            }

            var intended = BuildPatchRejectionSuccessor(reduction, activeElapsedMilliseconds);
            if (ArtifactsEqual(current, intended))
            {
                return new CampaignTransitionResult(
                    CampaignTransitionKind.Unchanged,
                    current,
                    current,
                    CampaignTransitionFailure.None);
            }

            if (!ArtifactsEqual(current, reduction.Predecessor))
            {
                return Reject(current, CampaignTransitionFailure.ConflictingReplay);
            }

            return new CampaignTransitionResult(
                CampaignTransitionKind.Applied,
                current,
                intended,
                CampaignTransitionFailure.None);
        }
        catch (OverflowException)
        {
            return Reject(current, CampaignTransitionFailure.RevisionOverflow);
        }
        catch (Exception exception) when (IsBoundedContractFailure(exception))
        {
            return Reject(current, CampaignTransitionFailure.InvalidAuthority);
        }
    }

    public static CampaignTransitionResult Stop(
        CampaignCheckpointArtifact predecessor,
        CampaignTerminalKind kind)
    {
        ArgumentNullException.ThrowIfNull(predecessor);
        if (!IsExactArtifact(predecessor))
        {
            return Reject(predecessor, CampaignTransitionFailure.InvalidPredecessor);
        }

        if (predecessor.State.ActiveReservation is not null)
        {
            return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
        }

        return StopCore(predecessor, kind);
    }

    private static CampaignTransitionResult StopCore(
        CampaignCheckpointArtifact predecessor,
        CampaignTerminalKind kind)
    {
        var reason = kind switch
        {
            CampaignTerminalKind.Cancelled => CampaignTerminalReason.Caller,
            CampaignTerminalKind.Timeout => CampaignTerminalReason.Deadline,
            CampaignTerminalKind.Exhausted => CampaignTerminalReason.Budget,
            _ => (CampaignTerminalReason?)null,
        };
        if (reason is null)
        {
            return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
        }

        try
        {
            var state = predecessor.State;
            if (state.TerminalOutcome is { Kind: var existing } && existing == kind)
            {
                return new CampaignTransitionResult(
                    CampaignTransitionKind.Unchanged,
                    predecessor,
                    predecessor,
                    CampaignTransitionFailure.None);
            }

            if (state.TerminalOutcome is not null)
            {
                return Reject(predecessor, CampaignTransitionFailure.ConflictingReplay);
            }

            var charges = CampaignBudgetAccounting.SettleActiveConservatively(state);
            var terminal = HasDurableKnownCompletion(
                    state.WorkItems,
                    state.KnownCompletedOperations)
                ? new CampaignTerminalOutcome(
                    CampaignTerminalKind.Exhausted,
                    CampaignTerminalReason.Budget)
                : new CampaignTerminalOutcome(kind, reason.Value);
            return Applied(predecessor, CreateState(
                state,
                NextRevision(state.CheckpointRevision),
                charges,
                state.WorkItems,
                null,
                state.CandidateObservation,
                state.CumulativeOutcome,
                terminal,
                state.Predecessor));
        }
        catch (OverflowException)
        {
            return Reject(predecessor, CampaignTransitionFailure.RevisionOverflow);
        }
        catch (Exception exception) when (IsBoundedContractFailure(exception))
        {
            return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
        }
    }

    public static CampaignTransitionResult StopActiveInvocation(
        CampaignCheckpointArtifact predecessor,
        CampaignAcceptedCheckpoint acceptedCheckpoint,
        CampaignTerminalKind kind)
    {
        ArgumentNullException.ThrowIfNull(predecessor);
        ArgumentNullException.ThrowIfNull(acceptedCheckpoint);
        if (!IsExactArtifact(predecessor)
            || !ArtifactsEqual(predecessor, acceptedCheckpoint.Artifact)
            || predecessor.State.ActiveReservation is null)
        {
            return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
        }

        return RetireReservationBeforeApply(
            predecessor,
            acceptedCheckpoint,
            StopCore(predecessor, kind));
    }

    public static CampaignTransitionResult Supersede(
        CampaignCheckpointArtifact current,
        CampaignAcceptedCheckpoint? acceptedCheckpoint,
        CampaignInitialCheckpointAuthority successorAuthority,
        CampaignScribeExecutionCapability successorExecutionCapability,
        string styleConfigurationId,
        JsonElement validatedStyleConfigurationProjection,
        string inputIdentity,
        CampaignPlanningInput successorPlanningInput,
        CampaignWorkPlan successorPlan,
        CampaignTransitionResult? simultaneousOldSnapshotTransition = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(successorAuthority);
        ArgumentNullException.ThrowIfNull(successorExecutionCapability);
        ArgumentNullException.ThrowIfNull(successorPlanningInput);
        ArgumentNullException.ThrowIfNull(successorPlan);
        var successorTemplate = successorAuthority.Artifact;
        if (!IsExactArtifact(current))
        {
            return Reject(current, CampaignTransitionFailure.InvalidPredecessor);
        }

        if (simultaneousOldSnapshotTransition is not null
            && (simultaneousOldSnapshotTransition.Kind != CampaignTransitionKind.Applied
                || !ArtifactsEqual(current, simultaneousOldSnapshotTransition.Predecessor)))
        {
            return Reject(current, CampaignTransitionFailure.InvalidCorrelation);
        }

        try
        {
            var template = successorTemplate.State;
            CampaignStateFactory.ValidateCurrentContext(
                template,
                successorExecutionCapability,
                styleConfigurationId,
                validatedStyleConfigurationProjection,
                inputIdentity,
                successorPlanningInput,
                successorPlan);
            var state = current.State;
            if (state.ActiveReservation is not null
                && (acceptedCheckpoint is null || !ArtifactsEqual(current, acceptedCheckpoint.Artifact)))
            {
                return Reject(current, CampaignTransitionFailure.InvalidAuthority);
            }
            if (state.ProductRevision != template.ProductRevision
                || !string.Equals(state.CampaignLineage, template.CampaignLineage, StringComparison.Ordinal)
                || state.ConfiguredCeilings != template.ConfiguredCeilings
                || !string.Equals(
                    state.Snapshot.InputIdentityCommitmentSha256,
                    template.Snapshot.InputIdentityCommitmentSha256,
                    StringComparison.Ordinal)
                || !string.Equals(
                    state.Snapshot.PolicyAuthorityCommitmentSha256,
                    template.Snapshot.PolicyAuthorityCommitmentSha256,
                    StringComparison.Ordinal)
                || state.Snapshot.TargetProfile != template.Snapshot.TargetProfile
                || string.Equals(
                    state.Snapshot.OpaqueSnapshotBinding,
                    template.Snapshot.OpaqueSnapshotBinding,
                    StringComparison.Ordinal)
                || string.Equals(
                    state.Snapshot.ExecutionCommitmentSha256,
                    template.Snapshot.ExecutionCommitmentSha256,
                    StringComparison.Ordinal))
            {
                return Reject(current, CampaignTransitionFailure.InvalidAuthority);
            }

            var charges = CampaignBudgetAccounting.SettleActiveConservatively(state);
            var summary = CreatePredecessorSummary(current);
            var transition = Applied(current, CampaignStateFactory.CreateValidated(
                template.ProductRevision,
                template.CampaignLineage,
                template.Snapshot,
                NextRevision(state.CheckpointRevision),
                template.ConfiguredCeilings,
                charges,
                template.WorkItems,
                terminalOutcome: template.TerminalOutcome,
                predecessor: summary));
            return state.ActiveReservation is null
                ? transition
                : RetireReservationBeforeApply(current, acceptedCheckpoint!, transition);
        }
        catch (OverflowException)
        {
            return Reject(current, CampaignTransitionFailure.RevisionOverflow);
        }
        catch (Exception exception) when (IsBoundedContractFailure(exception))
        {
            return Reject(current, CampaignTransitionFailure.InvalidAuthority);
        }
    }

    private static CampaignCheckpointArtifact BuildPatchRejectionSuccessor(
        CampaignPatchRejectionReduction reduction,
        long? activeElapsedMilliseconds)
    {
        var state = reduction.Predecessor.State;
        if (state.ActiveReservation is not CampaignPatchReservation reservation
            || reservation.PatchAttemptCount != 1
            || !string.Equals(reservation.PatchRequestSha256, reduction.PatchRequestSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException();
        }

        var workItems = ReplaceWork(
            state.WorkItems,
            reduction.WorkItemKey,
            CampaignWorkStatus.Closed,
            null,
            reduction.ClosedOutcome);
        var settlement = CampaignBudgetAccounting.SettlePatchInvocation(state, activeElapsedMilliseconds);
        if (settlement.Kind == CampaignBudgetDecisionKind.Invalid)
        {
            throw new InvalidOperationException();
        }

        var charges = settlement.Charges!;
        var terminal = state.TerminalOutcome ?? CompleteWhenResolved(workItems);
        if (settlement.Kind == CampaignBudgetDecisionKind.Exhausted)
        {
            terminal = new CampaignTerminalOutcome(CampaignTerminalKind.Exhausted, CampaignTerminalReason.Budget);
        }
        if (HasDurableKnownCompletion(workItems, state.KnownCompletedOperations))
        {
            terminal = new CampaignTerminalOutcome(
                CampaignTerminalKind.Exhausted,
                CampaignTerminalReason.Budget);
        }
        var cumulative = new CampaignCumulativeOutcome(
            CampaignCumulativeOutcomeKind.Rejected,
            reduction.PatchRequestSha256,
            reduction.PatchResultCommitmentSha256,
            CampaignStateFactory.CreateActiveProjectionCommitment(state),
            reservation.ExpectedCheckpointRevision);
        return CampaignStateJson.CreateArtifact(CreateState(
            state,
            NextRevision(state.CheckpointRevision),
            charges,
            workItems,
            null,
            state.CandidateObservation,
            cumulative,
            terminal,
            state.Predecessor));
    }

    private static CampaignPredecessorSummary CreatePredecessorSummary(
        CampaignCheckpointArtifact artifact)
    {
        var state = artifact.State;
        CampaignPredecessorReservationSummary? reservation = state.ActiveReservation switch
        {
            CampaignProviderReservation provider => new CampaignPredecessorReservationSummary(
                "provider",
                provider.ScribeRequestSha256,
                checked((long)provider.Exposure.ProviderRequests
                    + provider.Exposure.InputTokens
                    + provider.Exposure.UncachedInputTokens
                    + provider.Exposure.OutputTokens
                    + provider.Exposure.CostMicrounits
                    + provider.Exposure.ElapsedMilliseconds)),
            CampaignPatchReservation patch => new CampaignPredecessorReservationSummary(
                "patch",
                patch.PatchRequestSha256,
                patch.ElapsedMilliseconds),
            _ => null,
        };
        var candidate = state.CandidateObservation;
        var candidateSummary = candidate is null
            ? new CampaignPredecessorCandidateSummary(0, 0, 0, 0, 0, 0, null, null)
            : new CampaignPredecessorCandidateSummary(
                candidate.AcceptedWorkItemKeys.Length,
                candidate.ChangedFiles.Length,
                candidate.ChangedFiles.Sum(file => checked((long)file.OriginalDocumentationByteCount)),
                candidate.ChangedFiles.Sum(file => checked((long)file.CandidateDocumentationByteCount)),
                candidate.ChangedFiles.Sum(file => checked((long)file.OriginalDocumentationLineCount)),
                candidate.ChangedFiles.Sum(file => checked((long)file.CandidateDocumentationLineCount)),
                candidate.PatchRequestSha256,
                candidate.PatchResultCommitmentSha256);
        var completedOperations = state.KnownCompletedOperations
            .Select(operation => new CampaignPredecessorCompletedOperationSummary(
                operation.Kind,
                operation.ProjectionCommitmentSha256,
                operation.ResultCommitmentSha256))
            .Concat(state.WorkItems
                .Select(item => item.ClosedOutcome)
                .Where(outcome => outcome is
                {
                    Stage: CampaignWorkOutcomeStage.Scribe,
                    Code: CampaignWorkOutcomeCode.CompletedOverBound,
                    ScribeRequestSha256: not null,
                    ScribeResultCommitmentSha256: not null,
                })
                .Select(outcome => new CampaignPredecessorCompletedOperationSummary(
                    "scribe-over-bound",
                    outcome!.ScribeRequestSha256!,
                    outcome.ScribeResultCommitmentSha256!)))
            .OrderBy(operation => operation.Kind, StringComparer.Ordinal)
            .ThenBy(operation => operation.ProjectionCommitmentSha256, StringComparer.Ordinal)
            .ThenBy(operation => operation.ResultCommitmentSha256, StringComparer.Ordinal)
            .ToImmutableArray();
        return new CampaignPredecessorSummary(
            state.ProductRevision,
            state.Snapshot,
            state.ConfiguredCeilings.CampaignConfigurationCommitmentSha256,
            state.CheckpointRevision,
            artifact.Sha256,
            state.TerminalOutcome?.Kind ?? CampaignTerminalKind.Superseded,
            reservation,
            candidateSummary,
            completedOperations);
    }

    private static CampaignWorkClosedOutcome CreateClosedScribeOutcome(
        DocumentationScribeValidatedRunOutcome outcome,
        string workItemKey)
    {
        var terminal = outcome.RunResult.Terminal;
        var code = terminal switch
        {
            DocumentationScribeSkipTerminal { Reason: DocumentationScribeSkipReason.InsufficientEvidence } =>
                CampaignWorkOutcomeCode.InsufficientEvidence,
            DocumentationScribeSkipTerminal => CampaignWorkOutcomeCode.UnsupportedDomain,
            DocumentationScribeFailureTerminal { Code: DocumentationScribeFailureCode.Provider } =>
                CampaignWorkOutcomeCode.ProviderFailure,
            DocumentationScribeFailureTerminal { Code: DocumentationScribeFailureCode.ToolProtocol } =>
                CampaignWorkOutcomeCode.ToolProtocolFailure,
            DocumentationScribeFailureTerminal { Code: DocumentationScribeFailureCode.Validation } =>
                CampaignWorkOutcomeCode.ValidationFailure,
            DocumentationScribeFailureTerminal { Code: DocumentationScribeFailureCode.Timeout } =>
                CampaignWorkOutcomeCode.Timeout,
            DocumentationScribeFailureTerminal { Code: DocumentationScribeFailureCode.Budget } =>
                CampaignWorkOutcomeCode.BudgetExhausted,
            DocumentationScribeFailureTerminal => CampaignWorkOutcomeCode.InternalFailure,
            DocumentationScribeCancelledTerminal { Code: DocumentationScribeCancellationCode.Caller } =>
                CampaignWorkOutcomeCode.CancelledByCaller,
            DocumentationScribeCancelledTerminal => CampaignWorkOutcomeCode.CancelledByShutdown,
            _ => throw new InvalidOperationException(),
        };
        CampaignProviderFinalDisposition? disposition = terminal is DocumentationScribeFailureTerminal
        {
            Code: DocumentationScribeFailureCode.Provider,
            ProviderFinalDisposition: { } providerDisposition,
        }
            ? providerDisposition switch
            {
                DocumentationScribeProviderFinalDisposition.Retryable => CampaignProviderFinalDisposition.Retryable,
                DocumentationScribeProviderFinalDisposition.Terminal => CampaignProviderFinalDisposition.Terminal,
                _ => throw new InvalidOperationException(),
            }
            : null;
        return new CampaignWorkClosedOutcome(
            CampaignWorkOutcomeStage.Scribe,
            code,
            disposition,
            outcome.Request.ArtifactSha256,
            outcome.RunResult.AttemptId,
            null,
            null,
            null,
            workItemKey);
    }

    private static CampaignWorkClosedOutcome CreateClosedScribeOverboundOutcome(
        DocumentationScribeValidatedRunOutcome outcome,
        string workItemKey,
        string resultCommitmentSha256) =>
        new(
            CampaignWorkOutcomeStage.Scribe,
            CampaignWorkOutcomeCode.CompletedOverBound,
            null,
            outcome.Request.ArtifactSha256,
            outcome.RunResult.AttemptId,
            null,
            null,
            resultCommitmentSha256,
            workItemKey);

    private static CampaignTerminalOutcome? CompleteWhenResolved(
        ImmutableArray<CampaignWorkItemState> workItems) =>
        workItems.All(item => item.Status is CampaignWorkStatus.Closed or CampaignWorkStatus.Accepted)
            ? new CampaignTerminalOutcome(CampaignTerminalKind.Complete, CampaignTerminalReason.AllWorkClosed)
            : null;

    private static bool HasDurableKnownCompletion(
        ImmutableArray<CampaignWorkItemState> workItems,
        ImmutableArray<CampaignKnownCompletedOperation> knownCompletedOperations) =>
        !knownCompletedOperations.IsEmpty
        || workItems.Any(item => item.ClosedOutcome is
        {
            Stage: CampaignWorkOutcomeStage.Scribe,
            Code: CampaignWorkOutcomeCode.CompletedOverBound,
        });

    private static ImmutableArray<CampaignKnownCompletedOperation> AddKnownPatchCompletion(
        ImmutableArray<CampaignKnownCompletedOperation> existing,
        CampaignCumulativeOutcome cumulative)
    {
        if (cumulative is not
            {
                Kind: CampaignCumulativeOutcomeKind.OverBound,
                PatchResultCommitmentSha256: { } result,
                ProjectionCommitmentSha256: { } projection,
            })
        {
            throw new InvalidOperationException();
        }

        var operation = new CampaignKnownCompletedOperation(
            "patch-over-bound",
            cumulative.PatchRequestSha256,
            projection,
            result,
            CampaignStateFactory.CreateKnownCompletedOperationBinding(
                "patch-over-bound",
                cumulative.PatchRequestSha256,
                projection,
                result));
        return existing
            .Append(operation)
            .Distinct()
            .OrderBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.ProjectionCommitmentSha256, StringComparer.Ordinal)
            .ThenBy(item => item.RequestCommitmentSha256, StringComparer.Ordinal)
            .ThenBy(item => item.ResultCommitmentSha256, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static CampaignTransitionResult Exhausted(CampaignCheckpointArtifact predecessor)
    {
        try
        {
            var charges = CampaignBudgetAccounting.SettleActiveConservatively(predecessor.State);
            return Applied(predecessor, CreateState(
                predecessor.State,
                NextRevision(predecessor.CheckpointRevision),
                charges,
                predecessor.State.WorkItems,
                null,
                predecessor.State.CandidateObservation,
                predecessor.State.CumulativeOutcome,
                new CampaignTerminalOutcome(CampaignTerminalKind.Exhausted, CampaignTerminalReason.Budget),
                predecessor.State.Predecessor));
        }
        catch (OverflowException)
        {
            return Reject(predecessor, CampaignTransitionFailure.RevisionOverflow);
        }
    }

    private static CampaignCheckpointState CreateState(
        CampaignCheckpointState basis,
        long revision,
        CampaignLineageCharges charges,
        ImmutableArray<CampaignWorkItemState> workItems,
        CampaignActiveReservation? reservation,
        CampaignCandidateObservation? candidate,
        CampaignCumulativeOutcome? cumulative,
        CampaignTerminalOutcome? terminal,
        CampaignPredecessorSummary? predecessor,
        ImmutableArray<CampaignKnownCompletedOperation>? knownCompletedOperations = null) =>
        CampaignStateFactory.CreateValidated(
            basis.ProductRevision,
            basis.CampaignLineage,
            basis.Snapshot,
            revision,
            basis.ConfiguredCeilings,
            charges,
            workItems,
            reservation,
            candidate,
            cumulative,
            knownCompletedOperations ?? basis.KnownCompletedOperations,
            terminal,
            predecessor);

    private static ImmutableArray<CampaignWorkItemState> ReplaceWork(
        ImmutableArray<CampaignWorkItemState> workItems,
        string workItemKey,
        CampaignWorkStatus status,
        CampaignTrustedProposal? proposal,
        CampaignWorkClosedOutcome? closed) => workItems.Select(item =>
            string.Equals(item.WorkItemKey, workItemKey, StringComparison.Ordinal)
                ? item with
                {
                    Status = status,
                    TrustedProposal = proposal,
                    ClosedOutcome = closed,
                }
                : item).ToImmutableArray();

    private static bool FitsCandidate(
        CampaignCandidateObservation candidate,
        CampaignStateCampaignBudget budget)
    {
        try
        {
            return candidate.AcceptedWorkItemKeys.Length <= budget.MaximumBlocks
                && candidate.ChangedFiles.Length <= budget.MaximumChangedFiles
                && candidate.ChangedFiles.Sum(file => checked((long)file.CandidateDocumentationByteCount))
                    <= budget.MaximumPatchBytes;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool CanReservePatchFromTerminal(CampaignCheckpointState state)
    {
        if (state.TerminalOutcome is null)
        {
            return true;
        }

        var active = state.WorkItems
            .Where(item => item.Status is CampaignWorkStatus.ProposalComplete or CampaignWorkStatus.Accepted)
            .ToImmutableArray();
        return !active.IsEmpty
            && state.TerminalOutcome switch
            {
                { Kind: CampaignTerminalKind.Complete, Reason: CampaignTerminalReason.AllWorkClosed } =>
                    active.All(item => item.Status == CampaignWorkStatus.Accepted),
                { Kind: CampaignTerminalKind.Exhausted, Reason: CampaignTerminalReason.Budget } => true,
                _ => false,
            };
    }

    private static DocumentationScribeAttemptId CreateAttemptId(
        string executionCommitment,
        CampaignScribeExecutionAuthority authority,
        string workItemKey,
        int ordinal)
    {
        var material = string.Join('\n',
            "contract-scribe/campaign/scribe-attempt/v1",
            executionCommitment,
            authority.ProviderConfigurationId,
            authority.ModelConfigurationId,
            authority.ScribeProtocolId,
            authority.ToolPolicyId,
            workItemKey,
            ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))
            .ToLowerInvariant()[..32];
        if (!DocumentationScribeAttemptId.TryParse("scribe-attempt." + suffix, out var result))
        {
            throw new InvalidOperationException();
        }

        return result;
    }

    private static bool ValidExecutionCapability(CampaignScribeExecutionCapability capability)
    {
        var authority = capability.Projection;
        return
        CampaignStateFactory.IsOpaqueId(authority.ProviderConfigurationId, DocumentationScribeContract.MaximumIdentifierScalars)
        && CampaignStateFactory.IsOpaqueId(authority.ModelConfigurationId, DocumentationScribeContract.MaximumIdentifierScalars)
        && CampaignStateFactory.IsOpaqueId(authority.ScribeProtocolId, DocumentationScribeContract.MaximumIdentifierScalars)
        && CampaignStateFactory.IsOpaqueId(authority.ToolPolicyId, DocumentationScribeContract.MaximumIdentifierScalars);
    }

    private static bool ValidSimultaneousStop(CampaignTerminalKind? kind) => kind is null
        or CampaignTerminalKind.Cancelled
        or CampaignTerminalKind.Timeout
        or CampaignTerminalKind.Exhausted;

    private static bool HasProviderCompletionRevisionHeadroom(CampaignCheckpointState state) =>
        state.CheckpointRevision <= CampaignStateContract.MaximumObservation - 2;

    private static long NextRevision(long revision)
    {
        if (revision >= CampaignStateContract.MaximumObservation)
        {
            throw new OverflowException();
        }

        return checked(revision + 1);
    }

    private static CampaignTransitionResult RetireReservationBeforeApply(
        CampaignCheckpointArtifact predecessor,
        CampaignAcceptedCheckpoint acceptedCheckpoint,
        CampaignTransitionResult transition)
    {
        if (transition.Kind != CampaignTransitionKind.Applied
            || !ArtifactsEqual(predecessor, acceptedCheckpoint.Artifact)
            || !acceptedCheckpoint.TryRetireReservation())
        {
            return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
        }

        return transition;
    }

    private static CampaignTransitionResult Applied(
        CampaignCheckpointArtifact predecessor,
        CampaignCheckpointState state,
        DocumentationScribeAttemptId? attemptId = null) => new(
            CampaignTransitionKind.Applied,
            predecessor,
            CampaignStateJson.CreateArtifact(state),
            CampaignTransitionFailure.None,
            attemptId);

    private static CampaignTransitionResult Reject(
        CampaignCheckpointArtifact predecessor,
        CampaignTransitionFailure failure) => new(
            CampaignTransitionKind.Rejected,
            predecessor,
            predecessor,
            failure);

    private static bool IsExactArtifact(CampaignCheckpointArtifact artifact)
    {
        try
        {
            return ArtifactsEqual(artifact, CampaignStateJson.CreateArtifact(artifact.State));
        }
        catch (Exception exception) when (IsBoundedContractFailure(exception))
        {
            return false;
        }
    }

    private static bool ArtifactsEqual(CampaignCheckpointArtifact left, CampaignCheckpointArtifact right) =>
        left.CheckpointRevision == right.CheckpointRevision
        && string.Equals(left.Sha256, right.Sha256, StringComparison.Ordinal)
        && left.ExactUtf8Json.AsSpan().SequenceEqual(right.ExactUtf8Json.AsSpan());

    private static bool IsBoundedContractFailure(Exception exception) => exception is
        CampaignStateValidationException
        or CampaignPlanningValidationException
        or ArgumentException
        or InvalidOperationException
        or OverflowException;
}
