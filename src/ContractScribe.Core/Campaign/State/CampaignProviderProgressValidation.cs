namespace ContractScribe.Core;

public static partial class CampaignStateFactory
{
    private static void ValidateProviderProgress(CampaignCheckpointState state, CampaignProviderReservation provider)
    {
        var limits = state.ConfiguredCeilings.ScribeRunLimits;
        var exposure = provider.Exposure;
        Require(IsWorkItemKey(provider.WorkItemKey) && IsSha256(provider.ScribeRequestSha256)
            && DocumentationScribeAttemptId.TryParse(provider.AttemptId.Value, out _)
            && provider.ExecutionStartRevision > 0 && provider.ExecutionStartRevision <= state.CheckpointRevision
            && provider.CurrentOperationOrdinal > 0 && provider.CurrentOperationOrdinal <= CampaignStateContract.MaximumObservation
            && provider.LastDispatchOrdinal >= 0 && provider.LastDispatchOrdinal <= CampaignStateContract.MaximumObservation
            && provider.CurrentExecutionSettledProviderRequests >= 0
            && provider.CurrentExecutionSettledProviderRequests <= limits.MaximumProviderRequests
            && provider.CurrentExecutionSettledProviderRequests <= provider.LastDispatchOrdinal
            && provider.RestoredRetryableProviderFailures >= 0
            && provider.RestoredRetryableProviderFailures < limits.MaximumAttempts
            && Enum.IsDefined(provider.OperationKind) && exposure is not null,
            CampaignStateValidationCode.InvalidCorrelation);
        Require(exposure!.ElapsedMilliseconds >= 0 && exposure.ElapsedMilliseconds <= limits.MaximumElapsedMilliseconds,
            CampaignStateValidationCode.InvalidBound);
        var work = state.WorkItems.SingleOrDefault(item => item.WorkItemKey == provider.WorkItemKey);
        Require(work is { Status: CampaignWorkStatus.Planned, PausedProviderAttempt: null }
            && work.OuterAttemptCount > 0 && work.AttemptDisposition == CampaignAttemptDisposition.Open
            && CreateScribeAttemptId(state.Snapshot.ExecutionCommitmentSha256,
                state.ConfiguredCeilings.ScribeExecutionAuthority, provider.WorkItemKey, work.OuterAttemptCount) == provider.AttemptId,
            CampaignStateValidationCode.InvalidCorrelation);
        ValidateRetryProgress(provider.RetryProgress, provider.LastDispatchOrdinal, limits.MaximumAttempts);
        Require(provider.RetryProgress.RetryableFailureCount >= provider.RestoredRetryableProviderFailures
            && provider.RetryProgress.RetryableFailureCount - provider.RestoredRetryableProviderFailures
                <= provider.CurrentExecutionSettledProviderRequests,
            CampaignStateValidationCode.InvalidCorrelation);
        if (provider.OperationKind == CampaignProviderOperationKind.Host)
        {
            Require(provider.HostPhase is { } phase && Enum.IsDefined(phase)
                && provider.RequestCommitmentSha256 is null && exposure.ProviderRequests == 0
                && exposure.InputTokens == 0 && exposure.CachedInputTokens == 0 && exposure.UncachedInputTokens == 0
                && exposure.OutputTokens == 0 && exposure.CostMicrounits == 0
                && (exposure.ElapsedMilliseconds > 0 || provider.HostPhase == CampaignProviderHostPhase.Retirement),
                CampaignStateValidationCode.InvalidShape);
        }
        else
        {
            Require(provider.HostPhase is null && IsSha256(provider.RequestCommitmentSha256)
                && provider.LastDispatchOrdinal > provider.RetryProgress.LastSettledDispatchOrdinal
                && exposure.ProviderRequests == 1
                && exposure.CachedInputTokens > 0 && exposure.UncachedInputTokens > 0
                && exposure.InputTokens == checked((long)exposure.CachedInputTokens + exposure.UncachedInputTokens)
                && exposure.InputTokens <= limits.MaximumInputTokens
                && exposure.UncachedInputTokens <= limits.MaximumUncachedInputTokens
                && exposure.OutputTokens > 0 && exposure.OutputTokens <= limits.MaximumOutputTokens
                && exposure.ElapsedMilliseconds > 0 && exposure.CostMicrounits >= 0
                && exposure.CostMicrounits <= CampaignStateContract.MaximumObservation
                && (!state.ConfiguredCeilings.CampaignBudget.CostEnforced
                    ? exposure.CostMicrounits == 0
                    : exposure.CostMicrounits >= state.ConfiguredCeilings.CampaignBudget.CostRates!
                        .ConservativeDispatchCost(exposure.CachedInputTokens, exposure.UncachedInputTokens, exposure.OutputTokens)),
                CampaignStateValidationCode.InvalidBound);
        }
    }

    private static void ValidatePausedProviderAttempt(CampaignCheckpointState state,
        CampaignWorkItemState work, CampaignPausedProviderAttempt paused)
    {
        Require(work.Status == CampaignWorkStatus.Planned && work.OuterAttemptCount > 0
            && work.AttemptDisposition == CampaignAttemptDisposition.Open
            && (state.ActiveReservation is not CampaignProviderReservation active || active.WorkItemKey != work.WorkItemKey)
            && IsSha256(paused.ScribeRequestSha256) && paused.LastDispatchOrdinal >= 0
            && paused.LastDispatchOrdinal <= CampaignStateContract.MaximumObservation
            && CreateScribeAttemptId(state.Snapshot.ExecutionCommitmentSha256,
                state.ConfiguredCeilings.ScribeExecutionAuthority, work.WorkItemKey, work.OuterAttemptCount) == paused.AttemptId,
            CampaignStateValidationCode.InvalidCorrelation);
        ValidateRetryProgress(paused.RetryProgress, paused.LastDispatchOrdinal,
            state.ConfiguredCeilings.ScribeRunLimits.MaximumAttempts);
    }

    private static void ValidateRetryProgress(CampaignProviderRetryProgress progress, long lastOrdinal, int maximumAttempts)
    {
        Require(progress is not null && progress.RetryableFailureCount >= 0
            && progress.RetryableFailureCount <= maximumAttempts
            && progress.RetryableFailureCount <= progress.LastSettledDispatchOrdinal
            && progress.LastSettledDispatchOrdinal >= 0 && progress.LastSettledDispatchOrdinal <= lastOrdinal
            && Enum.IsDefined(progress.LastDisposition)
            && progress.PendingRetryAfterMilliseconds is >= 0 and <= 300_000,
            CampaignStateValidationCode.InvalidBound);
        Require(progress.LastDisposition == CampaignProviderDispatchDisposition.None
            ? progress.LastSettledDispatchOrdinal == 0 && progress.RetryableFailureCount == 0
                && progress.LastRequestCommitmentSha256 is null && progress.LastSettlementCommitmentSha256 is null
                && progress.PendingRetryAfterMilliseconds == 0
            : progress.LastSettledDispatchOrdinal > 0 && IsSha256(progress.LastRequestCommitmentSha256)
                && IsSha256(progress.LastSettlementCommitmentSha256)
                && (progress.LastDisposition == CampaignProviderDispatchDisposition.RetryableFailure
                    || progress.PendingRetryAfterMilliseconds == 0),
            CampaignStateValidationCode.InvalidCorrelation);
    }
}
