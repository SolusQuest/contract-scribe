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
        CampaignCheckpointArtifact artifact,
        CampaignTransitionFailure failure,
        DocumentationScribeAttemptId? attemptId = null)
    {
        Kind = kind;
        Artifact = artifact;
        Failure = failure;
        AttemptId = attemptId;
    }

    public CampaignTransitionKind Kind { get; }
    public CampaignCheckpointArtifact Artifact { get; }
    public CampaignTransitionFailure Failure { get; }
    public DocumentationScribeAttemptId? AttemptId { get; }
}

public sealed class CampaignPatchInvocationAuthority
{
    internal CampaignPatchInvocationAuthority(
        CampaignCheckpointArtifact reservedArtifact,
        string patchRequestSha256,
        long expectedCheckpointRevision)
    {
        ReservedArtifact = reservedArtifact;
        PatchRequestSha256 = patchRequestSha256;
        ExpectedCheckpointRevision = expectedCheckpointRevision;
    }

    internal CampaignCheckpointArtifact ReservedArtifact { get; }
    internal string PatchRequestSha256 { get; }
    internal long ExpectedCheckpointRevision { get; }

    public override string ToString() => nameof(CampaignPatchInvocationAuthority);
}

/// <summary>
/// Pure deterministic Campaign State v1 transitions. This type never performs
/// I/O and never accepts independently re-projected provider or Patch facts.
/// </summary>
public static class CampaignStateReducer
{
    public static CampaignPatchInvocationAuthority CreatePatchInvocationAuthority(
        CampaignCheckpointArtifact persistedReservedArtifact,
        DocumentationPatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(persistedReservedArtifact);
        ArgumentNullException.ThrowIfNull(request);
        if (!IsExactArtifact(persistedReservedArtifact)
            || persistedReservedArtifact.State.ActiveReservation is not CampaignPatchReservation reservation
            || reservation.PatchAttemptCount != 1
            || reservation.ExpectedCheckpointRevision != persistedReservedArtifact.CheckpointRevision
            || !string.Equals(reservation.PatchRequestSha256, request.ArtifactSha256, StringComparison.Ordinal))
        {
            throw new ArgumentException("The persisted Patch reservation does not authorize this invocation.");
        }

        return new CampaignPatchInvocationAuthority(
            persistedReservedArtifact,
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
            return Applied(CreateState(
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
        CampaignScribeExecutionAuthority executionAuthority,
        string styleConfigurationId,
        JsonElement validatedStyleConfigurationProjection,
        string inputIdentity,
        CampaignPlanningInput planningInput,
        CampaignWorkPlan acceptedPlan,
        DocumentationScribeValidatedRunOutcome outcome,
        long? activeElapsedMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(predecessor);
        ArgumentNullException.ThrowIfNull(executionAuthority);
        ArgumentNullException.ThrowIfNull(planningInput);
        ArgumentNullException.ThrowIfNull(acceptedPlan);
        ArgumentNullException.ThrowIfNull(outcome);
        if (!IsExactArtifact(predecessor))
        {
            return Reject(predecessor, CampaignTransitionFailure.InvalidPredecessor);
        }

        var state = predecessor.State;
        if (state.ActiveReservation is not CampaignProviderReservation reservation
            || !string.Equals(reservation.ScribeRequestSha256, outcome.Request.ArtifactSha256, StringComparison.Ordinal)
            || reservation.AttemptId != outcome.RunResult.AttemptId
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
            if (!string.Equals(envelope.ProviderConfigurationId, executionAuthority.ProviderConfigurationId, StringComparison.Ordinal)
                || !string.Equals(envelope.ModelConfigurationId, executionAuthority.ModelConfigurationId, StringComparison.Ordinal)
                || !string.Equals(envelope.ScribeProtocolId, executionAuthority.ScribeProtocolId, StringComparison.Ordinal)
                || !string.Equals(envelope.ToolPolicyId, executionAuthority.ToolPolicyId, StringComparison.Ordinal))
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
            else
            {
                var closed = CreateClosedScribeOutcome(outcome);
                workItems = ReplaceWork(
                    workItems,
                    reservation.WorkItemKey,
                    CampaignWorkStatus.Closed,
                    null,
                    closed);
                campaignTerminal ??= CompleteWhenAllClosed(workItems);
            }

            return Applied(CreateState(
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
        string workItemKey,
        DocumentationScribeRequest request)
    {
        ArgumentNullException.ThrowIfNull(predecessor);
        ArgumentNullException.ThrowIfNull(executionAuthority);
        ArgumentNullException.ThrowIfNull(request);
        if (!IsExactArtifact(predecessor))
        {
            return Reject(predecessor, CampaignTransitionFailure.InvalidPredecessor);
        }

        var state = predecessor.State;
        if (state.TerminalOutcome is not null
            || state.ActiveReservation is not CampaignProviderReservation old
            || !string.Equals(old.WorkItemKey, workItemKey, StringComparison.Ordinal)
            || !string.Equals(old.ScribeRequestSha256, request.ArtifactSha256, StringComparison.Ordinal)
            || !ValidExecutionAuthority(executionAuthority))
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
            var budget = CampaignBudgetAccounting.ReserveProviderInvocation(settled);
            if (budget.Kind != CampaignBudgetDecisionKind.Admitted)
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
                return Applied(exhausted);
            }

            var work = state.WorkItems.Single(item =>
                string.Equals(item.WorkItemKey, workItemKey, StringComparison.Ordinal));
            if (work.Status != CampaignWorkStatus.Planned)
            {
                return Reject(predecessor, CampaignTransitionFailure.InvalidCorrelation);
            }

            if (work.OuterAttemptCount >= state.ConfiguredCeilings.CampaignBudget.MaximumAttemptsPerTarget)
            {
                return Applied(CreateState(
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
            var workItems = state.WorkItems.Select(item =>
                string.Equals(item.WorkItemKey, workItemKey, StringComparison.Ordinal)
                    ? item with { OuterAttemptCount = ordinal }
                    : item).ToImmutableArray();
            return Applied(CreateState(
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
                && item.Status == CampaignWorkStatus.ProposalComplete
                && item.CandidateAttemptCount >= state.ConfiguredCeilings.CampaignBudget.MaximumCandidatesPerBlock))
            {
                return Exhausted(predecessor);
            }

            var workItems = state.WorkItems.Select(item =>
                blockIds.Contains(item.WorkItemKey) && item.Status == CampaignWorkStatus.ProposalComplete
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
            return Applied(CreateState(
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
        long? activeElapsedMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(predecessor);
        ArgumentNullException.ThrowIfNull(invocationAuthority);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        if (!IsExactArtifact(predecessor))
        {
            return Reject(predecessor, CampaignTransitionFailure.InvalidPredecessor);
        }

        var state = predecessor.State;
        if (!ArtifactsEqual(predecessor, invocationAuthority.ReservedArtifact)
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
            CampaignTerminalOutcome? terminal = state.TerminalOutcome;
            if (settlement.Kind == CampaignBudgetDecisionKind.Exhausted
                || completion.CandidateObservation is { } proposed
                    && !FitsCandidate(proposed, state.ConfiguredCeilings.CampaignBudget))
            {
                terminal = new CampaignTerminalOutcome(CampaignTerminalKind.Exhausted, CampaignTerminalReason.Budget);
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
            }

            return Applied(CreateState(
                state,
                NextRevision(state.CheckpointRevision),
                settlement.Charges!,
                workItems,
                null,
                candidate,
                completion.CumulativeOutcome,
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
            || old.PatchAttemptCount != 1
            || !string.Equals(old.PatchRequestSha256, request.ArtifactSha256, StringComparison.Ordinal))
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
            if (budget.Kind != CampaignBudgetDecisionKind.Admitted)
            {
                return Applied(CreateState(
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
                && item.Status == CampaignWorkStatus.ProposalComplete
                && item.CandidateAttemptCount >= state.ConfiguredCeilings.CampaignBudget.MaximumCandidatesPerBlock))
            {
                return Applied(CreateState(
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
                blockIds.Contains(item.WorkItemKey) && item.Status == CampaignWorkStatus.ProposalComplete
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
            return Applied(CreateState(
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

    public static CampaignTransitionResult ApplyPatchRejection(
        CampaignCheckpointArtifact current,
        CampaignPatchRejectionReduction reduction)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(reduction);
        if (!IsExactArtifact(current) || !IsExactArtifact(reduction.Predecessor))
        {
            return Reject(current, CampaignTransitionFailure.InvalidPredecessor);
        }

        try
        {
            var intended = BuildPatchRejectionSuccessor(reduction);
            if (ArtifactsEqual(current, intended))
            {
                return new CampaignTransitionResult(
                    CampaignTransitionKind.Unchanged,
                    current,
                    CampaignTransitionFailure.None);
            }

            if (!ArtifactsEqual(current, reduction.Predecessor))
            {
                return Reject(current, CampaignTransitionFailure.ConflictingReplay);
            }

            return new CampaignTransitionResult(
                CampaignTransitionKind.Applied,
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
                    CampaignTransitionFailure.None);
            }

            if (state.TerminalOutcome is not null)
            {
                return Reject(predecessor, CampaignTransitionFailure.ConflictingReplay);
            }

            var charges = CampaignBudgetAccounting.SettleActiveConservatively(state);
            return Applied(CreateState(
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

    public static CampaignTransitionResult Supersede(
        CampaignCheckpointArtifact current,
        CampaignCheckpointArtifact successorTemplate,
        string styleConfigurationId,
        JsonElement validatedStyleConfigurationProjection,
        string inputIdentity,
        CampaignPlanningInput successorPlanningInput,
        CampaignWorkPlan successorPlan)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(successorTemplate);
        ArgumentNullException.ThrowIfNull(successorPlanningInput);
        ArgumentNullException.ThrowIfNull(successorPlan);
        if (!IsExactArtifact(current) || !IsExactArtifact(successorTemplate))
        {
            return Reject(current, CampaignTransitionFailure.InvalidPredecessor);
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
                || template.Predecessor is not null)
            {
                return Reject(current, CampaignTransitionFailure.InvalidAuthority);
            }

            var state = current.State;
            if (state.Predecessor is { } prior
                && state.CheckpointRevision == prior.FinalCheckpointRevision + 1
                && SnapshotEquals(state.Snapshot, template.Snapshot)
                && WorkTemplateEquals(state.WorkItems, template.WorkItems)
                && state.ActiveReservation is null
                && state.CandidateObservation is null
                && state.CumulativeOutcome is null
                && state.TerminalOutcome is null)
            {
                return new CampaignTransitionResult(
                    CampaignTransitionKind.Unchanged,
                    current,
                    CampaignTransitionFailure.None);
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
            return Applied(CampaignStateFactory.CreateValidated(
                template.ProductRevision,
                template.CampaignLineage,
                template.Snapshot,
                NextRevision(state.CheckpointRevision),
                template.ConfiguredCeilings,
                charges,
                template.WorkItems,
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
        CampaignPatchRejectionReduction reduction)
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
        var charges = CampaignBudgetAccounting.SettleActiveConservatively(state);
        var terminal = state.TerminalOutcome ?? CompleteWhenAllClosed(workItems);
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
        DocumentationScribeValidatedRunOutcome outcome)
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
            null);
    }

    private static CampaignTerminalOutcome? CompleteWhenAllClosed(
        ImmutableArray<CampaignWorkItemState> workItems) =>
        workItems.All(item => item.Status == CampaignWorkStatus.Closed)
            ? new CampaignTerminalOutcome(CampaignTerminalKind.Complete, CampaignTerminalReason.AllWorkClosed)
            : null;

    private static CampaignTransitionResult Exhausted(CampaignCheckpointArtifact predecessor)
    {
        try
        {
            var charges = CampaignBudgetAccounting.SettleActiveConservatively(predecessor.State);
            return Applied(CreateState(
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

    private static bool SnapshotEquals(
        CampaignStateSnapshotAuthority left,
        CampaignStateSnapshotAuthority right) => left == right;

    private static bool WorkTemplateEquals(
        ImmutableArray<CampaignWorkItemState> current,
        ImmutableArray<CampaignWorkItemState> template) =>
        current.Length == template.Length
        && current.Zip(template).All(pair =>
            string.Equals(pair.First.WorkItemKey, pair.Second.WorkItemKey, StringComparison.Ordinal)
            && pair.First.Status == pair.Second.Status
            && pair.First.OuterAttemptCount == pair.Second.OuterAttemptCount
            && pair.First.CandidateAttemptCount == pair.Second.CandidateAttemptCount
            && pair.First.TrustedProposal == pair.Second.TrustedProposal
            && pair.First.ClosedOutcome == pair.Second.ClosedOutcome);

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

    private static long NextRevision(long revision)
    {
        if (revision >= CampaignStateContract.MaximumObservation)
        {
            throw new OverflowException();
        }

        return checked(revision + 1);
    }

    private static CampaignTransitionResult Applied(
        CampaignCheckpointState state,
        DocumentationScribeAttemptId? attemptId = null) => new(
            CampaignTransitionKind.Applied,
            CampaignStateJson.CreateArtifact(state),
            CampaignTransitionFailure.None,
            attemptId);

    private static CampaignTransitionResult Reject(
        CampaignCheckpointArtifact predecessor,
        CampaignTransitionFailure failure) => new(
            CampaignTransitionKind.Rejected,
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
