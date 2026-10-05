using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ContractScribe.Core;

public static partial class CampaignStateReducer
{
    internal static CampaignTransitionResult ReserveProviderDispatch(CampaignProviderInvocationAuthority invocation,
        DocumentationScribeDispatchDescriptor descriptor, DocumentationScribeInvocationProviderPermit permit,
        int precedingHostElapsed)
    {
        var predecessor = invocation.AcceptedCheckpoint.Artifact;
        try
        {
            var claim = CurrentClaim(invocation);
            var allowance = invocation.InvocationAllowance!;
            if (claim.OperationKind != CampaignProviderOperationKind.Host || !permit.OwnedBy(allowance)
                || permit.DispatchStarted || !HasProviderCompletionRevisionHeadroom(predecessor.State)
                || descriptor.ProviderRequestNumber != checked(claim.CurrentExecutionSettledProviderRequests + 1)
                || descriptor.LogicalAttemptNumber != checked(claim.RetryProgress.RetryableFailureCount + 1)
                || claim.RetryProgress.RetryableFailureCount >= invocation.Request.Limits.MaximumAttempts
                || claim.RetryProgress.LastDisposition == CampaignProviderDispatchDisposition.TerminalFailure
                || descriptor.MaximumOutputTokens > permit.Exposure.OutputTokens)
                return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
            var state = predecessor.State;
            var charges = CampaignBudgetAccounting.SettleHostInterval(state.LineageCharges,
                precedingHostElapsed, claim.Exposure.ElapsedMilliseconds);
            var lifetime = CampaignBudgetAccounting.RemainingLifetimeElapsed(charges, state.ConfiguredCeilings.CampaignBudget);
            if (lifetime <= 0) return Reject(predecessor, CampaignTransitionFailure.BudgetExhausted);
            if (state.ConfiguredCeilings.CampaignBudget.MaximumElapsedMilliseconds is not null
                && lifetime <= permit.Exposure.ElapsedMilliseconds)
                allowance.ShortenForLifetime(permit, lifetime);
            var exposure = CampaignBudgetAccounting.ProviderExposure(state.ConfiguredCeilings.CampaignBudget, permit.Exposure);
            if (!CampaignBudgetAccounting.FitsProviderBudget(charges, exposure, state.ConfiguredCeilings.CampaignBudget))
                return Reject(predecessor, CampaignTransitionFailure.BudgetExhausted);
            var next = claim with
            {
                CurrentOperationOrdinal = checked(claim.CurrentOperationOrdinal + 1),
                LastDispatchOrdinal = checked(claim.LastDispatchOrdinal + 1),
                OperationKind = CampaignProviderOperationKind.Dispatch,
                HostPhase = null,
                RequestCommitmentSha256 = descriptor.RequestCommitmentSha256,
                Exposure = exposure,
            };
            return Progress(predecessor, charges, next);
        }
        catch (Exception exception) when (IsBoundedContractFailure(exception) || exception is OverflowException)
        { return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority); }
    }

    internal static CampaignTransitionResult SettleProviderDispatch(CampaignProviderInvocationAuthority invocation,
        DocumentationScribeInvocationProviderPermit permit, DocumentationScribeDispatchSettlement settlement, int elapsed)
    {
        var predecessor = invocation.AcceptedCheckpoint.Artifact;
        try
        {
            var claim = CurrentClaim(invocation);
            if (claim.OperationKind != CampaignProviderOperationKind.Dispatch
                || !permit.OwnedBy(invocation.InvocationAllowance!)
                || claim.Exposure != CampaignBudgetAccounting.ProviderExposure(predecessor.State.ConfiguredCeilings.CampaignBudget, permit.Exposure)
                || !Enum.IsDefined(settlement.Disposition) || settlement.RetryAfterMilliseconds is < 0 or > 300_000)
                return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
            var sent = permit.DispatchStarted;
            var decision = CampaignBudgetAccounting.SettleProviderDispatch(predecessor.State, sent, settlement.Usage, elapsed);
            if (decision.Charges is null) return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
            var retry = claim.RetryProgress;
            if (sent)
            {
                var disposition = settlement.Disposition switch
                {
                    DocumentationScribeDispatchDisposition.Success => CampaignProviderDispatchDisposition.Success,
                    DocumentationScribeDispatchDisposition.RetryableFailure => CampaignProviderDispatchDisposition.RetryableFailure,
                    DocumentationScribeDispatchDisposition.TerminalFailure => CampaignProviderDispatchDisposition.TerminalFailure,
                    _ => CampaignProviderDispatchDisposition.Interrupted,
                };
                var failures = checked(retry.RetryableFailureCount +
                    (disposition == CampaignProviderDispatchDisposition.RetryableFailure ? 1 : 0));
                if (failures > invocation.Request.Limits.MaximumAttempts)
                    return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
                var proof = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    "contract-scribe/provider-settlement/v1\n" + claim.ScribeRequestSha256 + "\n" + claim.AttemptId.Value
                    + "\n" + claim.LastDispatchOrdinal + "\n" + claim.RequestCommitmentSha256 + "\n"
                    + JsonSerializer.Serialize(new { settlement, elapsed, sent })))).ToLowerInvariant();
                retry = new(failures, disposition, claim.LastDispatchOrdinal, claim.RequestCommitmentSha256, proof,
                    disposition == CampaignProviderDispatchDisposition.RetryableFailure ? settlement.RetryAfterMilliseconds : 0);
            }
            var next = Continuation(claim with
            {
                CurrentExecutionSettledProviderRequests = checked(claim.CurrentExecutionSettledProviderRequests + (sent ? 1 : 0)),
                RetryProgress = retry,
            }, invocation.InvocationAllowance!, decision.Charges, predecessor.State.ConfiguredCeilings.CampaignBudget);
            return Progress(predecessor, decision.Charges, next);
        }
        catch (Exception exception) when (IsBoundedContractFailure(exception) || exception is OverflowException)
        { return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority); }
    }

    internal static CampaignTransitionResult AdvanceHostOperation(CampaignProviderInvocationAuthority invocation,
        DocumentationScribeHostOperation phase, int elapsed, int maximumMilliseconds, bool completing)
    {
        var predecessor = invocation.AcceptedCheckpoint.Artifact;
        try
        {
            var claim = CurrentClaim(invocation);
            if (claim.OperationKind != CampaignProviderOperationKind.Host || !Enum.IsDefined(phase)
                || elapsed < 0 || maximumMilliseconds < 0
                || completing && claim.HostPhase != (CampaignProviderHostPhase)phase)
                return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
            var charges = CampaignBudgetAccounting.SettleHostInterval(predecessor.State.LineageCharges,
                elapsed, claim.Exposure.ElapsedMilliseconds);
            var next = Continuation(claim, invocation.InvocationAllowance!, charges, predecessor.State.ConfiguredCeilings.CampaignBudget);
            if (!completing)
            {
                var lifetime = CampaignBudgetAccounting.RemainingLifetimeElapsed(charges, predecessor.State.ConfiguredCeilings.CampaignBudget);
                var exposure = Math.Min(maximumMilliseconds, Math.Min(lifetime, invocation.InvocationAllowance!.RemainingMilliseconds));
                if (exposure <= 0 || !HasProviderCompletionRevisionHeadroom(predecessor.State))
                    return Reject(predecessor, CampaignTransitionFailure.BudgetExhausted);
                next = next with
                {
                    HostPhase = (CampaignProviderHostPhase)phase,
                    Exposure = CampaignBudgetAccounting.HostExposure(exposure)
                };
            }
            else if (phase == DocumentationScribeHostOperation.RetryWait)
                next = next with
                {
                    RetryProgress = next.RetryProgress with
                    { PendingRetryAfterMilliseconds = Math.Max(0, next.RetryProgress.PendingRetryAfterMilliseconds - elapsed) }
                };
            return Progress(predecessor, charges, next);
        }
        catch (Exception exception) when (IsBoundedContractFailure(exception) || exception is OverflowException)
        { return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority); }
    }

    internal static CampaignTransitionResult StopProviderBeforePhysicalDispatch(
        CampaignProviderInvocationAuthority invocation, CampaignTerminalKind kind, int elapsed)
    {
        var predecessor = invocation.AcceptedCheckpoint.Artifact;
        try
        {
            var claim = CurrentClaim(invocation);
            if (claim.OperationKind != CampaignProviderOperationKind.Host || claim.CurrentExecutionSettledProviderRequests != 0
                || kind is not (CampaignTerminalKind.Cancelled or CampaignTerminalKind.Timeout))
                return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
            var state = predecessor.State;
            var charges = CampaignBudgetAccounting.SettleHostInterval(state.LineageCharges, elapsed, claim.Exposure.ElapsedMilliseconds);
            var reason = kind == CampaignTerminalKind.Cancelled ? CampaignTerminalReason.Caller : CampaignTerminalReason.Deadline;
            var transition = Applied(predecessor, CreateState(state, NextRevision(state.CheckpointRevision), charges,
                state.WorkItems, null, state.CandidateObservation, state.CumulativeOutcome,
                new CampaignTerminalOutcome(kind, reason), state.Predecessor));
            return invocation.TryCompleteLifecycle(invocation.DispatchStarted) ? transition
                : Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
        }
        catch (Exception exception) when (IsBoundedContractFailure(exception) || exception is OverflowException)
        { return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority); }
    }

    internal static CampaignTransitionResult PauseProviderInvocation(CampaignProviderInvocationAuthority invocation,
        int elapsed, bool lifetimeStop)
    {
        var predecessor = invocation.AcceptedCheckpoint.Artifact;
        try
        {
            var claim = CurrentClaim(invocation);
            if (claim.OperationKind != CampaignProviderOperationKind.Host
                || !lifetimeStop && invocation.InvocationAllowance?.HasCheckedStop != true)
                return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
            var state = predecessor.State;
            var charges = CampaignBudgetAccounting.SettleHostInterval(state.LineageCharges, elapsed, claim.Exposure.ElapsedMilliseconds);
            var items = PreservePausedAttempt(state.WorkItems, claim);
            var terminal = lifetimeStop || !CampaignBudgetAccounting.FitsSettledBudget(charges, state.ConfiguredCeilings.CampaignBudget)
                ? new CampaignTerminalOutcome(CampaignTerminalKind.Exhausted, CampaignTerminalReason.LifetimeCap) : state.TerminalOutcome;
            var transition = Applied(predecessor, CreateState(state, NextRevision(state.CheckpointRevision), charges,
                items, null, state.CandidateObservation, state.CumulativeOutcome, terminal, state.Predecessor));
            return invocation.TryCompleteLifecycle(invocation.DispatchStarted) ? transition
                : Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
        }
        catch (Exception exception) when (IsBoundedContractFailure(exception) || exception is OverflowException)
        { return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority); }
    }

    internal static CampaignTransitionResult RetireInterruptedProviderAttempt(CampaignAcceptedCheckpoint accepted)
    {
        var predecessor = accepted.Artifact;
        if (!IsExactArtifact(predecessor) || predecessor.State.ActiveReservation is not CampaignProviderReservation claim)
            return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
        try
        {
            var state = predecessor.State;
            var charges = CampaignBudgetAccounting.SettleActiveConservatively(state);
            var terminal = CampaignBudgetAccounting.FitsSettledBudget(charges, state.ConfiguredCeilings.CampaignBudget)
                ? state.TerminalOutcome : new CampaignTerminalOutcome(CampaignTerminalKind.Exhausted, CampaignTerminalReason.LifetimeCap);
            var transition = Applied(predecessor, CreateState(state, NextRevision(state.CheckpointRevision), charges,
                PreservePausedAttempt(state.WorkItems, claim), null, state.CandidateObservation, state.CumulativeOutcome,
                terminal, state.Predecessor));
            return RetireReservationBeforeApply(predecessor, accepted, transition);
        }
        catch (Exception exception) when (IsBoundedContractFailure(exception) || exception is OverflowException)
        { return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority); }
    }

    internal static CampaignTransitionResult CompleteRecoveredProviderFailure(CampaignAcceptedCheckpoint accepted, string workItemKey)
    {
        var predecessor = accepted.Artifact;
        try
        {
            var state = predecessor.State;
            var work = state.WorkItems.Single(item => item.WorkItemKey == workItemKey);
            var paused = work.PausedProviderAttempt;
            if (!IsExactArtifact(predecessor) || state.ActiveReservation is not null || paused is null
                || paused.RetryProgress.LastDisposition != CampaignProviderDispatchDisposition.TerminalFailure
                    && paused.RetryProgress.RetryableFailureCount < state.ConfiguredCeilings.ScribeRunLimits.MaximumAttempts
                || paused.RetryProgress.LastSettlementCommitmentSha256 is null)
                return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
            var disposition = paused.RetryProgress.LastDisposition == CampaignProviderDispatchDisposition.TerminalFailure
                ? CampaignProviderFinalDisposition.Terminal : CampaignProviderFinalDisposition.Retryable;
            var closed = new CampaignWorkClosedOutcome(CampaignWorkOutcomeStage.Scribe, CampaignWorkOutcomeCode.ProviderFailure,
                disposition, paused.ScribeRequestSha256, paused.AttemptId, null, null, null, workItemKey)
            {
                ScribeCompletionSource = CampaignScribeCompletionSource.RecoveredDispatchFailure,
                AcceptedDispatchFailureCommitmentSha256 = paused.RetryProgress.LastSettlementCommitmentSha256
            };
            var items = ReplaceWork(state.WorkItems, workItemKey, CampaignWorkStatus.Closed, null, closed);
            if (disposition == CampaignProviderFinalDisposition.Retryable
                && work.OuterAttemptCount >= state.ConfiguredCeilings.CampaignBudget.MaximumAttemptsPerTarget)
                items = MarkAttemptSuppressed(items, workItemKey);
            var transition = Applied(predecessor, CreateState(state, NextRevision(state.CheckpointRevision), state.LineageCharges,
                items, null, state.CandidateObservation, state.CumulativeOutcome,
                state.TerminalOutcome ?? CompleteWhenResolved(state.Batch, items), state.Predecessor));
            return accepted.TryRetireReservation() ? transition : Reject(predecessor, CampaignTransitionFailure.InvalidAuthority);
        }
        catch (Exception exception) when (IsBoundedContractFailure(exception) || exception is OverflowException)
        { return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority); }
    }

    private static CampaignTransitionResult ResumeProviderAttempt(CampaignCheckpointArtifact predecessor,
        CampaignScribeExecutionCapability capability, string styleId, JsonElement style, CampaignPlanningInput planning,
        CampaignWorkPlan plan, string key, DocumentationScribeRequest request, CampaignInvocationTargetAllowance targets,
        DocumentationScribeInvocationAllowance allowance)
    {
        try
        {
            var state = predecessor.State;
            var work = state.WorkItems.Single(item => item.WorkItemKey == key);
            var paused = work.PausedProviderAttempt!;
            _ = CampaignStateFactory.ValidateProviderRequestAuthority(state, capability, styleId, style, planning, plan, key, request);
            if (state.ActiveReservation is not null || state.TerminalOutcome is not null
                || paused.RetryProgress.RetryableFailureCount >= request.Limits.MaximumAttempts
                || paused.RetryProgress.LastDisposition == CampaignProviderDispatchDisposition.TerminalFailure
                || !CampaignStateFactory.AllowsTarget(state, key, targets) || !allowance.CanBeginProviderWork())
                return Reject(predecessor, CampaignTransitionFailure.InvalidCorrelation);
            var budget = CampaignBudgetAccounting.ReserveProviderResume(state, allowance);
            if (budget.Kind != CampaignBudgetDecisionKind.Admitted || budget.Exposure is null)
                return Exhausted(predecessor, CampaignTerminalReason.LifetimeCap);
            var resumed = CreateResumedProviderReservationState(state, work, budget.Exposure, request.ArtifactSha256);
            ValidateProviderSettlementCapacity(resumed);
            return Applied(predecessor, resumed);
        }
        catch (CampaignStateValidationException exception) when (exception.Code == CampaignStateValidationCode.DocumentTooLarge)
        { return Reject(predecessor, CampaignTransitionFailure.CheckpointCapacity); }
        catch (Exception exception) when (IsBoundedContractFailure(exception) || exception is OverflowException)
        { return Reject(predecessor, CampaignTransitionFailure.InvalidAuthority); }
    }

    private static ImmutableArray<CampaignWorkItemState> PreservePausedAttempt(ImmutableArray<CampaignWorkItemState> items,
        CampaignProviderReservation claim) => items.Select(item => item.WorkItemKey == claim.WorkItemKey ? item with
        {
            PausedProviderAttempt = new(claim.ScribeRequestSha256, claim.AttemptId, claim.LastDispatchOrdinal, claim.RetryProgress),
        } : item).ToImmutableArray();

    private static CampaignProviderReservation CurrentClaim(CampaignProviderInvocationAuthority invocation)
    {
        var state = invocation.AcceptedCheckpoint.Artifact.State;
        if (state.ActiveReservation is not CampaignProviderReservation claim || !(invocation.LifecycleAvailable || invocation.DispatchStarted)
            || claim.ExecutionStartRevision != invocation.ExecutionStartRevision
            || claim.ScribeRequestSha256 != invocation.ScribeRequestSha256 || claim.AttemptId != invocation.AttemptId
            || invocation.InvocationAllowance is null) throw new InvalidOperationException("campaign.progress.invalid-owner");
        return claim;
    }

    private static CampaignProviderReservation Continuation(CampaignProviderReservation claim,
        DocumentationScribeInvocationAllowance allowance, CampaignLineageCharges charges, CampaignStateCampaignBudget budget)
    {
        var duration = Math.Min(allowance.Limits.MaximumRequestElapsedMilliseconds, Math.Min(allowance.RemainingMilliseconds,
            CampaignBudgetAccounting.RemainingLifetimeElapsed(charges, budget)));
        return claim with
        {
            CurrentOperationOrdinal = checked(claim.CurrentOperationOrdinal + 1),
            OperationKind = CampaignProviderOperationKind.Host,
            RequestCommitmentSha256 = null,
            HostPhase = duration > 0 ? CampaignProviderHostPhase.Continuation : CampaignProviderHostPhase.Retirement,
            Exposure = CampaignBudgetAccounting.HostExposure(duration),
        };
    }

    private static CampaignTransitionResult Progress(CampaignCheckpointArtifact predecessor, CampaignLineageCharges charges,
        CampaignProviderReservation claim, ImmutableArray<CampaignWorkItemState>? items = null)
    {
        var state = predecessor.State;
        var transition = Applied(predecessor, CreateState(state, NextRevision(state.CheckpointRevision), charges,
            items ?? state.WorkItems, claim, state.CandidateObservation, state.CumulativeOutcome, state.TerminalOutcome, state.Predecessor), claim.AttemptId);
        ValidateProviderSettlementCapacity(transition.Artifact.State);
        return transition;
    }
}
