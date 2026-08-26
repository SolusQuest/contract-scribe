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

    public override string ToString() => nameof(CampaignPatchInvocationAuthority);
}

public sealed class CampaignProviderInvocationAuthority
{
    internal CampaignProviderInvocationAuthority(
        CampaignAcceptedCheckpoint acceptedCheckpoint,
        string scribeRequestSha256,
        DocumentationScribeAttemptId attemptId)
    {
        AcceptedCheckpoint = acceptedCheckpoint;
        ScribeRequestSha256 = scribeRequestSha256;
        AttemptId = attemptId;
    }

    internal CampaignAcceptedCheckpoint AcceptedCheckpoint { get; }
    internal string ScribeRequestSha256 { get; }
    public DocumentationScribeAttemptId AttemptId { get; }

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
        DocumentationScribeRequest request)
    {
        ArgumentNullException.ThrowIfNull(acceptedCheckpoint);
        ArgumentNullException.ThrowIfNull(request);
        var artifact = acceptedCheckpoint.Artifact;
        if (!IsExactArtifact(artifact)
            || artifact.State.ActiveReservation is not CampaignProviderReservation reservation
            || !string.Equals(reservation.ScribeRequestSha256, request.ArtifactSha256, StringComparison.Ordinal))
        {
            throw new ArgumentException("The accepted provider reservation does not authorize this invocation.");
        }

        return new CampaignProviderInvocationAuthority(
            acceptedCheckpoint,
            reservation.ScribeRequestSha256,
            reservation.AttemptId);
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

        return new CampaignPatchInvocationAuthority(
            acceptedCheckpoint,
            request.ArtifactSha256,
            reservation.ExpectedCheckpointRevision);
    }

    public static CampaignTransitionResult AdmitProviderInvocation(
        CampaignCheckpointArtifact predecessor,
        CampaignScribeExecutionAuthority executionAuthority,
        string styleConfigurationId,
        JsonElement validatedStyleConfigurationProjection,
        CampaignPlanningInput planningInput,
        CampaignWorkPlan acceptedPlan,
        string workItemKey,
        DocumentationScribeRequest request)
    {
        ArgumentNullException.ThrowIfNull(predecessor);
        ArgumentNullException.ThrowIfNull(executionAuthority);
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
            CampaignStateFactory.ValidateCurrentContext(
                state,
                styleConfigurationId,
                validatedStyleConfigurationProjection,
                request.Context.InputIdentity,
                planningInput,
                acceptedPlan);
            var work = state.WorkItems.SingleOrDefault(item =>
                string.Equals(item.WorkItemKey, workItemKey, StringComparison.Ordinal));
            var planWork = acceptedPlan.WorkItems.SingleOrDefault(item =>
                string.Equals(item.WorkItemKey, workItemKey, StringComparison.Ordinal));
            if (state.TerminalOutcome is not null
                || state.ActiveReservation is not null
                || work is not { Status: CampaignWorkStatus.Planned }
                || planWork is null
                || planWork.Disposition.Kind != CampaignPlanningDispositionKind.Executable
                || planWork.Targets.Length != 1
                || !ValidExecutionAuthority(executionAuthority)
                || request.Target.SymbolRef != planWork.Targets[0].SymbolRef
                || !string.Equals(request.Target.SourceSha256, planWork.Targets[0].Source.ContentSha256, StringComparison.Ordinal)
                || request.Context.TargetProfile != state.Snapshot.TargetProfile
                || request.Limits != planningInput.ExecutionPolicy.ScribeRunLimits
                || !string.Equals(request.ToolPolicyId, executionAuthority.ToolPolicyId, StringComparison.Ordinal))
            {
                return Reject(predecessor, CampaignTransitionFailure.InvalidCorrelation);
            }

            if (work.OuterAttemptCount >= state.ConfiguredCeilings.CampaignBudget.MaximumAttemptsPerTarget)
            {
                return Exhausted(predecessor);
            }

            if (work.CandidateAttemptCount >= state.ConfiguredCeilings.CampaignBudget.MaximumCandidatesPerBlock)
            {
                return Exhausted(predecessor);
            }

            if (ActiveProjectionAtCapacity(state))
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
                executionAuthority,
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
        CampaignProviderInvocationAuthority invocationAuthority,
        CampaignScribeExecutionAuthority executionAuthority,
        string styleConfigurationId,
        JsonElement validatedStyleConfigurationProjection,
        string inputIdentity,
        CampaignPlanningInput planningInput,
        CampaignWorkPlan acceptedPlan,
        DocumentationScribeValidatedRunOutcome outcome,
        long? activeElapsedMilliseconds,
        CampaignTerminalKind? simultaneousStop = null)
    {
        ArgumentNullException.ThrowIfNull(predecessor);
        ArgumentNullException.ThrowIfNull(invocationAuthority);
        ArgumentNullException.ThrowIfNull(executionAuthority);
        ArgumentNullException.ThrowIfNull(planningInput);
        ArgumentNullException.ThrowIfNull(acceptedPlan);
        ArgumentNullException.ThrowIfNull(outcome);
        if (!IsExactArtifact(predecessor))
        {
            return Reject(predecessor, CampaignTransitionFailure.InvalidPredecessor);
        }

        if (!ValidSimultaneousStop(simultaneousStop))
        {
            return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
        }

        var state = predecessor.State;
        if (!ArtifactsEqual(predecessor, invocationAuthority.AcceptedCheckpoint.Artifact)
            || state.ActiveReservation is not CampaignProviderReservation reservation
            || !string.Equals(reservation.ScribeRequestSha256, outcome.Request.ArtifactSha256, StringComparison.Ordinal)
            || reservation.AttemptId != outcome.RunResult.AttemptId
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
            CampaignStateFactory.ValidateCurrentContext(
                state,
                styleConfigurationId,
                validatedStyleConfigurationProjection,
                inputIdentity,
                planningInput,
                acceptedPlan);
            var envelope = outcome.RunResult.RunEnvelope;
            var reservedWork = state.WorkItems.Single(item =>
                string.Equals(item.WorkItemKey, reservation.WorkItemKey, StringComparison.Ordinal));
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

            var settlement = CampaignBudgetAccounting.SettleProviderInvocation(
                state,
                outcome,
                activeElapsedMilliseconds);
            if (settlement.Kind == CampaignBudgetDecisionKind.Invalid)
            {
                return Reject(predecessor, CampaignTransitionFailure.InvalidCorrelation);
            }

            var terminal = outcome.RunResult.Terminal;
            var workItems = state.WorkItems;
            CampaignTerminalOutcome? campaignTerminal = state.TerminalOutcome;
            if (settlement.Kind == CampaignBudgetDecisionKind.Exhausted)
            {
                campaignTerminal = new CampaignTerminalOutcome(CampaignTerminalKind.Exhausted, CampaignTerminalReason.Budget);
            }
            else if (terminal is DocumentationScribeProposalTerminal)
            {
                if (ActiveProjectionAtCapacity(state))
                {
                    campaignTerminal = new CampaignTerminalOutcome(
                        CampaignTerminalKind.Exhausted,
                        CampaignTerminalReason.Budget);
                }
                else
                {
                    var proposal = CampaignStateFactory.CreateTrustedProposal(
                        state,
                        executionAuthority,
                        styleConfigurationId,
                        validatedStyleConfigurationProjection,
                        planningInput,
                        acceptedPlan,
                        reservation.WorkItemKey,
                        outcome.Request,
                        outcome.RunResult);
                    workItems = ReplaceWork(
                        workItems,
                        reservation.WorkItemKey,
                        CampaignWorkStatus.ProposalComplete,
                        proposal,
                        null);
                }
            }
            else
            {
                var closed = CreateClosedScribeOutcome(outcome, reservation.WorkItemKey);
                workItems = ReplaceWork(
                    workItems,
                    reservation.WorkItemKey,
                    CampaignWorkStatus.Closed,
                    null,
                    closed);
                campaignTerminal ??= terminal switch
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
            }

            return Applied(predecessor, CreateState(
                state,
                NextRevision(state.CheckpointRevision),
                settlement.Charges!,
                workItems,
                null,
                state.CandidateObservation,
                state.CumulativeOutcome,
                campaignTerminal,
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

    public static CampaignTransitionResult RetryProviderInvocation(
        CampaignCheckpointArtifact predecessor,
        CampaignScribeExecutionAuthority executionAuthority,
        string styleConfigurationId,
        JsonElement validatedStyleConfigurationProjection,
        string inputIdentity,
        CampaignPlanningInput planningInput,
        CampaignWorkPlan acceptedPlan,
        string workItemKey,
        DocumentationScribeRequest request)
    {
        ArgumentNullException.ThrowIfNull(predecessor);
        ArgumentNullException.ThrowIfNull(executionAuthority);
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
        if (state.TerminalOutcome is not null
            || work is null
            || activeRetry is not null && !string.Equals(activeRetry.WorkItemKey, workItemKey, StringComparison.Ordinal)
            || activeRetry is null && !closedRetry
            || !ValidExecutionAuthority(executionAuthority))
        {
            return Reject(predecessor, CampaignTransitionFailure.InvalidCorrelation);
        }

        try
        {
            CampaignStateFactory.ValidateCurrentContext(
                state,
                styleConfigurationId,
                validatedStyleConfigurationProjection,
                inputIdentity,
                planningInput,
                acceptedPlan);
            var planWork = acceptedPlan.WorkItems.SingleOrDefault(item =>
                string.Equals(item.WorkItemKey, workItemKey, StringComparison.Ordinal));
            if (planWork is null
                || planWork.Disposition.Kind != CampaignPlanningDispositionKind.Executable
                || planWork.Targets.Length != 1
                || request.Target.SymbolRef != planWork.Targets[0].SymbolRef
                || !string.Equals(request.Target.SourceSha256, planWork.Targets[0].Source.ContentSha256, StringComparison.Ordinal)
                || request.Context.TargetProfile != state.Snapshot.TargetProfile
                || request.Limits != planningInput.ExecutionPolicy.ScribeRunLimits
                || !string.Equals(request.ToolPolicyId, executionAuthority.ToolPolicyId, StringComparison.Ordinal))
            {
                return Reject(predecessor, CampaignTransitionFailure.InvalidCorrelation);
            }

            var historicalAttempt = activeRetry?.AttemptId ?? work.ClosedOutcome!.AttemptId!.Value;
            if (CreateAttemptId(
                    state.Snapshot.ExecutionCommitmentSha256,
                    executionAuthority,
                    workItemKey,
                    work.OuterAttemptCount) != historicalAttempt)
            {
                return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
            }

            var settledCharges = activeRetry is null
                ? state.LineageCharges
                : CampaignBudgetAccounting.SettleActiveConservatively(state);
            if (work.CandidateAttemptCount >= state.ConfiguredCeilings.CampaignBudget.MaximumCandidatesPerBlock
                || ActiveProjectionAtCapacity(state))
            {
                return Applied(predecessor, CreateState(
                    state,
                    NextRevision(state.CheckpointRevision),
                    settledCharges,
                    state.WorkItems,
                    null,
                    state.CandidateObservation,
                    state.CumulativeOutcome,
                    new CampaignTerminalOutcome(CampaignTerminalKind.Exhausted, CampaignTerminalReason.Budget),
                    state.Predecessor));
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
                return Applied(predecessor, exhausted);
            }

            if (work.OuterAttemptCount >= state.ConfiguredCeilings.CampaignBudget.MaximumAttemptsPerTarget)
            {
                return Applied(predecessor, CreateState(
                    state,
                    NextRevision(state.CheckpointRevision),
                    settledCharges,
                    state.WorkItems,
                    null,
                    state.CandidateObservation,
                    state.CumulativeOutcome,
                    new CampaignTerminalOutcome(CampaignTerminalKind.Exhausted, CampaignTerminalReason.Budget),
                    state.Predecessor));
            }

            var ordinal = checked(work.OuterAttemptCount + 1);
            var nextRevision = NextRevision(state.CheckpointRevision);
            var attemptId = CreateAttemptId(
                state.Snapshot.ExecutionCommitmentSha256,
                executionAuthority,
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
            return Applied(predecessor, CreateState(
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
            if (state.TerminalOutcome is not null || state.ActiveReservation is not null)
            {
                return Reject(predecessor, CampaignTransitionFailure.InvalidCorrelation);
            }

            var budget = CampaignBudgetAccounting.ReservePatchInvocation(state, elapsedMilliseconds);
            if (budget.Kind != CampaignBudgetDecisionKind.Admitted)
            {
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
                state.TerminalOutcome,
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
                state.TerminalOutcome,
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
        if (!ArtifactsEqual(predecessor, invocationAuthority.AcceptedCheckpoint.Artifact)
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
            CampaignTerminalOutcome? terminal = state.TerminalOutcome;
            if (settlement.Kind == CampaignBudgetDecisionKind.Exhausted
                || completion.CandidateObservation is { } proposed
                    && !FitsCandidate(proposed, state.ConfiguredCeilings.CampaignBudget))
            {
                terminal = new CampaignTerminalOutcome(CampaignTerminalKind.Exhausted, CampaignTerminalReason.Budget);
                if (completion.CandidateObservation is not null)
                {
                    cumulative = state.CumulativeOutcome;
                }
            }
            else if (completion.CandidateObservation is { } accepted)
            {
                var acceptedKeys = accepted.AcceptedWorkItemKeys.ToHashSet(StringComparer.Ordinal);
                workItems = state.WorkItems.Select(item =>
                    acceptedKeys.Contains(item.WorkItemKey)
                        && item.Status is CampaignWorkStatus.ProposalComplete or CampaignWorkStatus.Accepted
                        ? item with { Status = CampaignWorkStatus.Accepted }
                        : item).ToImmutableArray();
                candidate = accepted;
                terminal ??= CompleteWhenResolved(workItems);
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

            return Applied(predecessor, CreateState(
                state,
                NextRevision(state.CheckpointRevision),
                settlement.Charges!,
                workItems,
                null,
                candidate,
                cumulative,
                terminal,
                state.Predecessor));
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
        if (state.TerminalOutcome is not null
            || state.ActiveReservation is not CampaignPatchReservation old
            || old.PatchAttemptCount != 1)
        {
            return Reject(predecessor, CampaignTransitionFailure.InvalidCorrelation);
        }

        try
        {
            var settledCharges = CampaignBudgetAccounting.SettleActiveConservatively(state);
            var settled = CreateState(
                state,
                state.CheckpointRevision,
                settledCharges,
                state.WorkItems,
                null,
                state.CandidateObservation,
                state.CumulativeOutcome,
                state.TerminalOutcome,
                state.Predecessor);
            var budget = CampaignBudgetAccounting.ReservePatchInvocation(settled, elapsedMilliseconds);
            if (budget.Kind == CampaignBudgetDecisionKind.Invalid)
            {
                return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
            }

            if (budget.Kind == CampaignBudgetDecisionKind.Exhausted)
            {
                return Applied(predecessor, CreateState(
                    state,
                    NextRevision(state.CheckpointRevision),
                    settledCharges,
                    state.WorkItems,
                    null,
                    state.CandidateObservation,
                    state.CumulativeOutcome,
                    new CampaignTerminalOutcome(CampaignTerminalKind.Exhausted, CampaignTerminalReason.Budget),
                    state.Predecessor));
            }

            var nextRevision = NextRevision(state.CheckpointRevision);
            var blockIds = request.Blocks.Select(block => block.BlockId).ToHashSet(StringComparer.Ordinal);
            if (state.WorkItems.Any(item =>
                blockIds.Contains(item.WorkItemKey)
                && item.Status is CampaignWorkStatus.ProposalComplete or CampaignWorkStatus.Accepted
                && item.CandidateAttemptCount >= state.ConfiguredCeilings.CampaignBudget.MaximumCandidatesPerBlock))
            {
                return Applied(predecessor, CreateState(
                    state,
                    nextRevision,
                    settledCharges,
                    state.WorkItems,
                    null,
                    state.CandidateObservation,
                    state.CumulativeOutcome,
                    new CampaignTerminalOutcome(CampaignTerminalKind.Exhausted, CampaignTerminalReason.Budget),
                    state.Predecessor));
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
                state.TerminalOutcome,
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
                state.TerminalOutcome,
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
            var terminal = settlement.Kind == CampaignBudgetDecisionKind.Exhausted
                ? new CampaignTerminalOutcome(CampaignTerminalKind.Exhausted, CampaignTerminalReason.Budget)
                : kind switch
                {
                    CampaignCumulativeOutcomeKind.Cancelled =>
                        new CampaignTerminalOutcome(CampaignTerminalKind.Cancelled, CampaignTerminalReason.Caller),
                    CampaignCumulativeOutcomeKind.Timeout =>
                        new CampaignTerminalOutcome(CampaignTerminalKind.Timeout, CampaignTerminalReason.Deadline),
                    CampaignCumulativeOutcomeKind.HostFailure =>
                        new CampaignTerminalOutcome(CampaignTerminalKind.Failed, CampaignTerminalReason.Host),
                    _ => throw new ArgumentException("Unsupported Patch host outcome.", nameof(kind)),
                };
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
            if (!ArtifactsEqual(reduction.Predecessor, invocationAuthority.AcceptedCheckpoint.Artifact)
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
            return Applied(predecessor, CreateState(
                state,
                NextRevision(state.CheckpointRevision),
                charges,
                state.WorkItems,
                null,
                state.CandidateObservation,
                state.CumulativeOutcome,
                new CampaignTerminalOutcome(kind, reason.Value),
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

        return StopCore(predecessor, kind);
    }

    public static CampaignTransitionResult Supersede(
        CampaignCheckpointArtifact current,
        CampaignCheckpointArtifact successorTemplate,
        string styleConfigurationId,
        JsonElement validatedStyleConfigurationProjection,
        string inputIdentity,
        CampaignPlanningInput successorPlanningInput,
        CampaignWorkPlan successorPlan,
        CampaignTransitionResult? simultaneousOldSnapshotTransition = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(successorTemplate);
        ArgumentNullException.ThrowIfNull(successorPlanningInput);
        ArgumentNullException.ThrowIfNull(successorPlan);
        if (!IsExactArtifact(current) || !IsExactArtifact(successorTemplate))
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
                styleConfigurationId,
                validatedStyleConfigurationProjection,
                inputIdentity,
                successorPlanningInput,
                successorPlan);
            if (template.CheckpointRevision != 0
                || template.ActiveReservation is not null
                || template.CandidateObservation is not null
                || template.CumulativeOutcome is not null
                || template.Predecessor is not null
                || template.LineageCharges != CampaignStateFactory.EmptyChargesForAcceptance()
                || template.WorkItems.Any(item => item.OuterAttemptCount != 0 || item.CandidateAttemptCount != 0)
                || template.TerminalOutcome != InitialTerminal(template.WorkItems))
            {
                return Reject(current, CampaignTransitionFailure.InvalidAuthority);
            }

            var state = current.State;
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
            return Applied(current, CampaignStateFactory.CreateValidated(
                template.ProductRevision,
                template.CampaignLineage,
                template.Snapshot,
                NextRevision(state.CheckpointRevision),
                template.ConfiguredCeilings,
                charges,
                template.WorkItems,
                terminalOutcome: template.TerminalOutcome,
                predecessor: summary));
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
        var cumulative = new CampaignCumulativeOutcome(
            CampaignCumulativeOutcomeKind.Rejected,
            reduction.PatchRequestSha256,
            reduction.PatchResultCommitmentSha256,
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
        return new CampaignPredecessorSummary(
            state.ProductRevision,
            state.Snapshot,
            state.ConfiguredCeilings.CampaignConfigurationCommitmentSha256,
            state.CheckpointRevision,
            artifact.Sha256,
            state.TerminalOutcome?.Kind ?? CampaignTerminalKind.Superseded,
            reservation,
            candidateSummary);
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
            workItemKey);
    }

    private static CampaignTerminalOutcome? CompleteWhenResolved(
        ImmutableArray<CampaignWorkItemState> workItems) =>
        workItems.All(item => item.Status is CampaignWorkStatus.Closed or CampaignWorkStatus.Accepted)
            ? new CampaignTerminalOutcome(CampaignTerminalKind.Complete, CampaignTerminalReason.AllWorkClosed)
            : null;

    private static CampaignTerminalOutcome? InitialTerminal(
        ImmutableArray<CampaignWorkItemState> workItems) => workItems.IsEmpty
            ? new CampaignTerminalOutcome(CampaignTerminalKind.Complete, CampaignTerminalReason.NoWork)
            : workItems.All(item => item.Status == CampaignWorkStatus.Closed)
                ? new CampaignTerminalOutcome(CampaignTerminalKind.Complete, CampaignTerminalReason.AllWorkClosed)
                : null;

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
        CampaignPredecessorSummary? predecessor) =>
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

    private static bool ActiveProjectionAtCapacity(CampaignCheckpointState state) =>
        state.WorkItems.Count(item =>
            item.Status is CampaignWorkStatus.ProposalComplete or CampaignWorkStatus.Accepted)
        >= Math.Min(
            CampaignStateContract.MaximumActivePatchBlocks,
            state.ConfiguredCeilings.CampaignBudget.MaximumBlocks);

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

    private static bool ValidExecutionAuthority(CampaignScribeExecutionAuthority authority) =>
        CampaignStateFactory.IsOpaqueId(authority.ProviderConfigurationId, DocumentationScribeContract.MaximumIdentifierScalars)
        && CampaignStateFactory.IsOpaqueId(authority.ModelConfigurationId, DocumentationScribeContract.MaximumIdentifierScalars)
        && CampaignStateFactory.IsOpaqueId(authority.ScribeProtocolId, DocumentationScribeContract.MaximumIdentifierScalars)
        && CampaignStateFactory.IsOpaqueId(authority.ToolPolicyId, DocumentationScribeContract.MaximumIdentifierScalars);

    private static bool ValidSimultaneousStop(CampaignTerminalKind? kind) => kind is null
        or CampaignTerminalKind.Cancelled
        or CampaignTerminalKind.Timeout
        or CampaignTerminalKind.Exhausted;

    private static long NextRevision(long revision)
    {
        if (revision >= CampaignStateContract.MaximumObservation)
        {
            throw new OverflowException();
        }

        return checked(revision + 1);
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
