using System.Collections.Immutable;

namespace ContractScribe.Core;

public enum CampaignAttemptDisposition
{
    Open,
    SuppressedAtAttemptLimit,
}

public enum CampaignTargetProgressKind
{
    Pending,
    Deferred,
    Active,
    Retryable,
    ProposalComplete,
    AcceptedProposal,
    Skipped,
    Failed,
    Suppressed,
    InfrastructureBlocked,
    UnsupportedCurrentExecutor,
    Excluded,
}

public sealed record CampaignTargetProgress(CampaignBatchTarget Target, CampaignTargetProgressKind Kind);

/// <summary>A host-owned, run-local immutable subset; it cannot expand persisted membership.</summary>
public sealed class CampaignInvocationTargetAllowance
{
    internal CampaignInvocationTargetAllowance(CampaignCheckpointState state, ImmutableArray<string> workItemKeys)
    {
        Snapshot = state.Snapshot;
        BatchIdentity = state.Batch.Identity;
        WorkItemKeys = workItemKeys;
        RecoveryWorkItemKey = (state.ActiveReservation as CampaignProviderReservation)?.WorkItemKey;
    }

    internal CampaignStateSnapshotAuthority Snapshot { get; }
    public string BatchIdentity { get; }
    public ImmutableArray<string> WorkItemKeys { get; }
    public string? RecoveryWorkItemKey { get; }
}

public static partial class CampaignStateFactory
{
    private static void ValidateBatchStyle(CampaignWorkPlan plan, string styleCommitment)
    {
        var eligible = plan.Batch.CompleteTargets.Where(target => target.BatchEligible)
            .Select(target => target.SymbolRef).ToHashSet();
        Require(plan.WorkItems.SelectMany(work => work.Targets).Where(target => eligible.Contains(target.SymbolRef))
            .All(target => target.GroupingAuthority?.StyleConfigurationSha256 == styleCommitment),
            CampaignStateValidationCode.InvalidCorrelation);
    }

    public static CampaignInvocationTargetAllowance CreateInvocationTargetAllowance(
        CampaignCheckpointState state, CampaignInvocationTargetLimit limit)
    {
        Validate(state);
        var progress = ReadTargetProgress(state);
        var byKey = progress.ToDictionary(item => item.Target.TargetKey, StringComparer.Ordinal);
        var workByKey = state.WorkItems.ToDictionary(item => item.WorkItemKey, StringComparer.Ordinal);
        var remaining = state.Batch.SelectedTargetKeys.Select(key => byKey[key])
            .Where(item => item.Target.Dispatchable && item.Kind is
                CampaignTargetProgressKind.Pending or CampaignTargetProgressKind.Retryable)
            .ToImmutableArray();
        var newTargets = remaining.Where(item => workByKey[item.Target.WorkItemKey].OuterAttemptCount == 0)
            .Take(limit.MaximumTargets).Select(item => item.Target.TargetKey).ToHashSet(StringComparer.Ordinal);
        // Persisted starts already consumed their distinct-target slot, including across invocations.
        var keys = remaining.Where(item => workByKey[item.Target.WorkItemKey].OuterAttemptCount > 0
                || newTargets.Contains(item.Target.TargetKey))
            .Select(item => item.Target.WorkItemKey).ToImmutableArray();
        return new CampaignInvocationTargetAllowance(state, keys);
    }

    internal static bool AllowsTarget(CampaignCheckpointState state, string workItemKey,
        CampaignInvocationTargetAllowance? allowance)
    {
        var selected = state.Batch.SelectedTargetKeys.ToHashSet(StringComparer.Ordinal);
        return state.Batch.CompleteTargets.Any(target => target.WorkItemKey == workItemKey
                && target.Dispatchable && selected.Contains(target.TargetKey))
            && allowance is not null && allowance.Snapshot == state.Snapshot
            && allowance.BatchIdentity == state.Batch.Identity
            && (allowance.WorkItemKeys.Contains(workItemKey, StringComparer.Ordinal)
                || allowance.RecoveryWorkItemKey == workItemKey);
    }

    internal static ImmutableArray<CampaignTargetProgress> ReadTargetProgress(CampaignCheckpointState state)
    {
        var selected = state.Batch.SelectedTargetKeys.ToHashSet(StringComparer.Ordinal);
        var workByKey = state.WorkItems.ToDictionary(work => work.WorkItemKey, StringComparer.Ordinal);
        return state.Batch.CompleteTargets.Select(target =>
        {
            var work = workByKey[target.WorkItemKey];
            var kind = !target.BatchEligible ? CampaignTargetProgressKind.Excluded
                : !selected.Contains(target.TargetKey) ? CampaignTargetProgressKind.Deferred
                : !target.Dispatchable ? CampaignTargetProgressKind.UnsupportedCurrentExecutor
                : work.AttemptDisposition == CampaignAttemptDisposition.SuppressedAtAttemptLimit ? CampaignTargetProgressKind.Suppressed
                : state.ActiveReservation is CampaignProviderReservation reservation && reservation.WorkItemKey == work.WorkItemKey
                    ? CampaignTargetProgressKind.Active
                : work.Status switch
                {
                    CampaignWorkStatus.Planned => CampaignTargetProgressKind.Pending,
                    CampaignWorkStatus.ProposalComplete => CampaignTargetProgressKind.ProposalComplete,
                    CampaignWorkStatus.Accepted => CampaignTargetProgressKind.AcceptedProposal,
                    _ => work.ClosedOutcome switch
                    {
                        { Code: CampaignWorkOutcomeCode.ProviderFailure, ProviderDisposition: CampaignProviderFinalDisposition.Retryable }
                            => CampaignTargetProgressKind.Retryable,
                        { Code: CampaignWorkOutcomeCode.InsufficientEvidence or CampaignWorkOutcomeCode.UnsupportedDomain }
                            => CampaignTargetProgressKind.Skipped,
                        {
                            Code: CampaignWorkOutcomeCode.InternalFailure or CampaignWorkOutcomeCode.Timeout
                            or CampaignWorkOutcomeCode.CancelledByCaller or CampaignWorkOutcomeCode.CancelledByShutdown
                        }
                            => CampaignTargetProgressKind.InfrastructureBlocked,
                        _ => CampaignTargetProgressKind.Failed,
                    },
                };
            return new CampaignTargetProgress(target, kind);
        }).ToImmutableArray();
    }

    internal static CampaignTerminalOutcome? SelectBatchTerminal(
        CampaignFixedBatch batch, ImmutableArray<CampaignWorkItemState> workItems)
    {
        if (workItems.IsEmpty) return new(CampaignTerminalKind.Complete, CampaignTerminalReason.NoWork);
        var selected = batch.SelectedTargetKeys.ToHashSet(StringComparer.Ordinal);
        var byKey = workItems.ToDictionary(item => item.WorkItemKey, StringComparer.Ordinal);
        var targets = batch.CompleteTargets.Where(target => selected.Contains(target.TargetKey)).ToImmutableArray();
        if (targets.Any(target => target.Dispatchable
            && byKey[target.WorkItemKey].AttemptDisposition == CampaignAttemptDisposition.Open
            && (byKey[target.WorkItemKey].Status is CampaignWorkStatus.Planned or CampaignWorkStatus.ProposalComplete
                || byKey[target.WorkItemKey].ClosedOutcome is
                { Code: CampaignWorkOutcomeCode.ProviderFailure, ProviderDisposition: CampaignProviderFinalDisposition.Retryable })))
            return null;
        var unresolved = targets.Any(target => !target.Dispatchable
            || byKey[target.WorkItemKey].Status != CampaignWorkStatus.Accepted)
            || batch.CompleteTargets.Any(target => !target.BatchEligible);
        return new(CampaignTerminalKind.Complete, unresolved || targets.IsEmpty
            ? CampaignTerminalReason.Unresolved : CampaignTerminalReason.AllWorkClosed);
    }

    private static void ValidateBatch(CampaignCheckpointState state)
    {
        var batch = state.Batch;
        Require(batch is not null && !batch.CompleteTargets.IsDefault && !batch.SelectedTargetKeys.IsDefault,
            CampaignStateValidationCode.InvalidShape);
        Require(batch!.CreationQuota is >= 0 and <= CampaignStateContract.MaximumWorkItems
            && batch.CompleteTargets.Length <= CampaignStateContract.MaximumCompleteTargets
            && batch.SelectedTargetKeys.Length <= batch.CreationQuota,
            CampaignStateValidationCode.InvalidBound);
        Require(batch.PlanCommitment == state.Snapshot.ExecutionCommitmentSha256
            && batch.Identity == CampaignPlanner.ComputeBatchIdentity(batch), CampaignStateValidationCode.InvalidCorrelation);
        var workIndices = state.WorkItems.Select((work, index) => (work.WorkItemKey, index))
            .ToDictionary(pair => pair.WorkItemKey, pair => pair.index, StringComparer.Ordinal);
        var targetKeys = new HashSet<string>(StringComparer.Ordinal);
        var targetsByKey = new Dictionary<string, CampaignBatchTarget>(StringComparer.Ordinal);
        var coveredWorks = new HashSet<string>(StringComparer.Ordinal);
        var symbols = new HashSet<SymbolRef>();
        var priorIndex = -1;
        foreach (var target in batch.CompleteTargets)
        {
            Require(target is not null && DocumentationScribeContextValidation.IsValidSymbolRef(target.SymbolRef),
                CampaignStateValidationCode.InvalidVocabulary);
            Require(target is not null && workIndices.TryGetValue(target.WorkItemKey, out var index)
                && index >= priorIndex && targetKeys.Add(target.TargetKey) && symbols.Add(target.SymbolRef)
                && target.TargetKey == CampaignPlanner.ComputeTargetKey(batch.PlanCommitment, target.WorkItemKey, target.SymbolRef),
                CampaignStateValidationCode.InvalidCorrelation);
            priorIndex = workIndices[target!.WorkItemKey];
            targetsByKey.Add(target.TargetKey, target);
            coveredWorks.Add(target.WorkItemKey);
            Require(target.Dispatchable ? target.BatchEligible : true, CampaignStateValidationCode.InvalidCorrelation);
            Require(target.BatchEligible
                ? IsSha256(target.GroupIdentity) && IsSha256(target.MemberFamilyIdentity)
                : target.GroupIdentity is null && target.MemberFamilyIdentity is null,
                CampaignStateValidationCode.InvalidShape);
        }
        Require(state.WorkItems.All(work => coveredWorks.Contains(work.WorkItemKey)),
            CampaignStateValidationCode.InvalidCorrelation);
        Require(batch.SelectedTargetKeys.Distinct(StringComparer.Ordinal).Count() == batch.SelectedTargetKeys.Length
            && batch.SelectedTargetKeys.All(key => targetsByKey.TryGetValue(key, out var target) && target.BatchEligible),
            CampaignStateValidationCode.InvalidCorrelation);
        var selectedTargetKeys = batch.SelectedTargetKeys.ToHashSet(StringComparer.Ordinal);
        var selectedWork = batch.CompleteTargets.Where(target => selectedTargetKeys.Contains(target.TargetKey))
            .Where(target => target.Dispatchable).Select(target => target.WorkItemKey).ToHashSet(StringComparer.Ordinal);
        Require(state.WorkItems.All(work => selectedWork.Contains(work.WorkItemKey)
            || work.OuterAttemptCount == 0 && work.CandidateAttemptCount == 0 && work.TrustedProposal is null),
            CampaignStateValidationCode.InvalidCorrelation);
        Require(state.ActiveReservation is not CampaignProviderReservation reservation || selectedWork.Contains(reservation.WorkItemKey),
            CampaignStateValidationCode.InvalidCorrelation);
    }
}
