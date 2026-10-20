using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ContractScribe.Core;

public enum CampaignDispatchObservationState
{
    Reserved, Settled, NotSent, Unknown,
}

/// <summary>Charges from one reserved physical exchange, separate from reported measurements.</summary>
public sealed record CampaignDispatchCharges(
    CampaignChargeObservation ProviderRequests,
    CampaignChargeObservation InputTokens,
    CampaignChargeObservation CachedInputTokens,
    CampaignChargeObservation UncachedInputTokens,
    CampaignChargeObservation OutputTokens,
    CampaignChargeObservation ReasoningTokens,
    CampaignChargeObservation CostMicrounits,
    CampaignChargeObservation ActiveElapsedMilliseconds);

/// <summary>Source-free request history bounded by the existing checkpoint byte ceiling.</summary>
public sealed record CampaignDispatchObservation(
    string ExecutionCommitmentSha256, string BatchIdentity, string ConfigurationCommitmentSha256,
    string WorkItemKey, DocumentationScribeAttemptId AttemptId, string ScribeRequestSha256,
    long ExecutionStartRevision, long DispatchOrdinal, int LogicalAttemptNumber, int ProviderRequestNumber,
    string RequestCommitmentSha256, CampaignProviderReservationExposure Exposure,
    CampaignDispatchObservationState State, DocumentationScribeDispatchDisposition? Disposition,
    DocumentationScribeDispatchUsage? Usage, DocumentationScribeCacheObservation Cache,
    int? ElapsedMilliseconds, int RetryAfterMilliseconds, CampaignDispatchCharges? Charges,
    string? SettlementCommitmentSha256);

public static partial class CampaignStateFactory
{
    /// <summary>Rejects loss or replacement of retained history at an immediate remote checkpoint edge.</summary>
    public static void ValidateSuccessorHistory(CampaignCheckpointArtifact predecessor, CampaignCheckpointArtifact successor)
    {
        ArgumentNullException.ThrowIfNull(predecessor);
        ArgumentNullException.ThrowIfNull(successor);
        var before = predecessor.State;
        var next = successor.State;
        Require(next.CampaignLineage == before.CampaignLineage && next.ProductRevision == before.ProductRevision
            && successor.CheckpointRevision == checked(predecessor.CheckpointRevision + 1),
            CampaignStateValidationCode.InvalidCorrelation);
        var oldCharges = before.LineageCharges;
        var charges = next.LineageCharges;
        Require(charges.OuterInvocations >= oldCharges.OuterInvocations
            && charges.PatchValidationInvocations >= oldCharges.PatchValidationInvocations
            && charges.ProviderRequests.TotalCharged >= oldCharges.ProviderRequests.TotalCharged
            && charges.InputTokens.TotalCharged >= oldCharges.InputTokens.TotalCharged
            && charges.CachedInputTokens.TotalCharged >= oldCharges.CachedInputTokens.TotalCharged
            && charges.UncachedInputTokens.TotalCharged >= oldCharges.UncachedInputTokens.TotalCharged
            && charges.OutputTokens.TotalCharged >= oldCharges.OutputTokens.TotalCharged
            && charges.ReasoningTokens.TotalCharged >= oldCharges.ReasoningTokens.TotalCharged
            && charges.CostMicrounits.TotalCharged >= oldCharges.CostMicrounits.TotalCharged
            && charges.ActiveElapsedMilliseconds.TotalCharged >= oldCharges.ActiveElapsedMilliseconds.TotalCharged
            && (!oldCharges.HasUnpricedCostHistory || charges.HasUnpricedCostHistory),
            CampaignStateValidationCode.InvalidCorrelation);
        if (next.ArchivedDispatchPrefix != before.ArchivedDispatchPrefix)
        {
            _ = CampaignDispatchArchive.VerifyTransition(predecessor, successor);
            return;
        }
        Require(next.DispatchObservations.Length >= before.DispatchObservations.Length,
            CampaignStateValidationCode.InvalidCorrelation);
        for (var index = 0; index < before.DispatchObservations.Length; index++)
        {
            var previous = before.DispatchObservations[index];
            var current = next.DispatchObservations[index];
            if (previous.State == CampaignDispatchObservationState.Reserved)
                current = current with
                {
                    State = previous.State,
                    Disposition = previous.Disposition,
                    Usage = previous.Usage,
                    Cache = previous.Cache,
                    ElapsedMilliseconds = previous.ElapsedMilliseconds,
                    RetryAfterMilliseconds = previous.RetryAfterMilliseconds,
                    Charges = previous.Charges,
                    SettlementCommitmentSha256 = previous.SettlementCommitmentSha256,
                };
            Require(current == previous, CampaignStateValidationCode.InvalidCorrelation);
        }
    }

    internal static string DispatchSettlementCommitment(CampaignDispatchObservation row) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "contract-scribe/campaign-dispatch-observation/v1\n"
            + JsonSerializer.Serialize(row with { SettlementCommitmentSha256 = null })))).ToLowerInvariant();

    private static void ValidateDispatchObservations(CampaignCheckpointState state)
    {
        Require(!state.DispatchObservations.IsDefault, CampaignStateValidationCode.InvalidShape);
        var ordinals = new Dictionary<(string Execution, string Work, DocumentationScribeAttemptId Attempt), long>();
        var reconstructed = state.ArchivedDispatchPrefix?.Charges ?? EmptyCharges();
        if (state.ArchivedDispatchPrefix is { } prefix)
        {
            RequireSha(prefix.WitnessSha256);
            Require(prefix.Charges is not null && prefix.Charges.OuterInvocations == 0
                && prefix.Charges.PatchValidationInvocations == 0, CampaignStateValidationCode.InvalidCorrelation);
            ValidateCharge(reconstructed.ProviderRequests);
            ValidateCharge(reconstructed.InputTokens);
            ValidateCharge(reconstructed.CachedInputTokens);
            ValidateCharge(reconstructed.UncachedInputTokens);
            ValidateCharge(reconstructed.OutputTokens);
            ValidateCharge(reconstructed.ReasoningTokens);
            ValidateCharge(reconstructed.CostMicrounits);
            ValidateCharge(reconstructed.ActiveElapsedMilliseconds);
        }
        var budget = state.ConfiguredCeilings.CampaignBudget;
        for (var index = 0; index < state.DispatchObservations.Length; index++)
        {
            var row = state.DispatchObservations[index];
            Require(row is not null, CampaignStateValidationCode.InvalidShape);
            RequireSha(row.ExecutionCommitmentSha256);
            RequireSha(row.ConfigurationCommitmentSha256);
            RequireSha(row.BatchIdentity);
            RequireSha(row.ScribeRequestSha256);
            RequireSha(row.RequestCommitmentSha256);
            Require(IsOpaqueId(row.BatchIdentity, CampaignStateContract.MaximumIdentifierScalars)
                && IsWorkItemKey(row.WorkItemKey)
                && DocumentationScribeAttemptId.TryParse(row.AttemptId.Value, out _)
                && row.ExecutionStartRevision > 0 && row.ExecutionStartRevision <= state.CheckpointRevision
                && row.DispatchOrdinal is > 0 and <= CampaignStateContract.MaximumObservation
                && row.LogicalAttemptNumber is > 0 and <= DocumentationScribeContract.MaximumAttempts
                && row.ProviderRequestNumber is > 0 and <= 128
                && Enum.IsDefined(row.State) && Enum.IsDefined(row.Cache)
                && row.RetryAfterMilliseconds is >= 0 and <= 300_000,
                CampaignStateValidationCode.InvalidBound);
            var key = (row.ExecutionCommitmentSha256, row.WorkItemKey, row.AttemptId);
            Require(row.DispatchOrdinal == ordinals.GetValueOrDefault(key) + 1,
                CampaignStateValidationCode.InvalidCorrelation);
            ordinals[key] = row.DispatchOrdinal;
            var exposure = row.Exposure;
            Require(exposure is not null && exposure.ProviderRequests == 1
                && exposure.InputTokens > 0 && exposure.InputTokens <= DocumentationScribeContract.MaximumConfiguredInputTokens
                && exposure.CachedInputTokens > 0 && exposure.UncachedInputTokens > 0
                && exposure.InputTokens == checked((long)exposure.CachedInputTokens + exposure.UncachedInputTokens)
                && exposure.OutputTokens is > 0 and <= DocumentationScribeContract.MaximumConfiguredOutputTokens
                && exposure.ElapsedMilliseconds is > 0 and <= DocumentationScribeContract.MaximumConfiguredElapsedMilliseconds
                && exposure.CostMicrounits is >= 0 and <= CampaignStateContract.MaximumObservation,
                CampaignStateValidationCode.InvalidBound);
            Require(DocumentationScribeInvocationAllowance.ValidUsage(row.Usage), CampaignStateValidationCode.InvalidBound);
            Require(row.ConfigurationCommitmentSha256 == state.ConfiguredCeilings.CampaignConfigurationCommitmentSha256
                && exposure.CostMicrounits == (budget.CostEnforced
                    ? budget.CostRates!.ConservativeDispatchCost(exposure.CachedInputTokens, exposure.UncachedInputTokens, exposure.OutputTokens) : 0)
                && (!budget.CostEnforced || row.Usage?.CurrencyId is not { } currency || currency == budget.CostCurrency),
                CampaignStateValidationCode.InvalidCorrelation);
            if (row.State == CampaignDispatchObservationState.Reserved)
            {
                Require(index == state.DispatchObservations.Length - 1
                    && row.Disposition is null && row.Usage is null && row.Charges is null
                    && row.ElapsedMilliseconds is null && row.SettlementCommitmentSha256 is null
                    && row.Cache == DocumentationScribeCacheObservation.NotReported && row.RetryAfterMilliseconds == 0
                    && state.ActiveReservation is CampaignProviderReservation active
                    && active.OperationKind == CampaignProviderOperationKind.Dispatch
                    && MatchesDispatch(row, state, active), CampaignStateValidationCode.InvalidCorrelation);
            }
            else
            {
                Require(row.Charges is not null
                    && IsSha256(row.SettlementCommitmentSha256)
                    && row.SettlementCommitmentSha256 == DispatchSettlementCommitment(row)
                    && (row.State == CampaignDispatchObservationState.Unknown
                        ? row.Disposition == DocumentationScribeDispatchDisposition.Interrupted && row.ElapsedMilliseconds is null
                        : row.Disposition is { } disposition && Enum.IsDefined(disposition)
                            && row.ElapsedMilliseconds is >= 0 and <= int.MaxValue)
                    && (row.State == CampaignDispatchObservationState.Settled
                        || row.Usage is null && row.Cache == DocumentationScribeCacheObservation.NotReported)
                    && (row.Disposition == DocumentationScribeDispatchDisposition.RetryableFailure
                        || row.RetryAfterMilliseconds == 0), CampaignStateValidationCode.InvalidCorrelation);
                ValidateCharge(row.Charges!.ProviderRequests);
                ValidateCharge(row.Charges.InputTokens);
                ValidateCharge(row.Charges.CachedInputTokens);
                ValidateCharge(row.Charges.UncachedInputTokens);
                ValidateCharge(row.Charges.OutputTokens);
                ValidateCharge(row.Charges.ReasoningTokens);
                ValidateCharge(row.Charges.CostMicrounits);
                ValidateCharge(row.Charges.ActiveElapsedMilliseconds);
                var next = CampaignBudgetAccounting.SettleDispatchCharges(reconstructed, budget, exposure,
                    row.State == CampaignDispatchObservationState.Unknown ? null : row.State == CampaignDispatchObservationState.Settled,
                    row.Usage, row.ElapsedMilliseconds);
                var expected = CampaignStateReducer.DispatchChargeDelta(reconstructed, next);
                Require(SameDispatchCharges(row.Charges, expected), CampaignStateValidationCode.InvalidCorrelation);
                reconstructed = next;
            }
        }
        foreach (var work in state.WorkItems.Where(work => work.PausedProviderAttempt is not null))
        {
            var paused = work.PausedProviderAttempt!;
            Require(ordinals.GetValueOrDefault((state.Snapshot.ExecutionCommitmentSha256, work.WorkItemKey, paused.AttemptId))
                == paused.LastDispatchOrdinal, CampaignStateValidationCode.InvalidCorrelation);
        }
        if (state.ActiveReservation is CampaignProviderReservation claim)
            Require(ordinals.GetValueOrDefault((state.Snapshot.ExecutionCommitmentSha256, claim.WorkItemKey, claim.AttemptId))
                == claim.LastDispatchOrdinal, CampaignStateValidationCode.InvalidCorrelation);
        var lineage = state.LineageCharges;
        Require(SameProviderCharges(CampaignStateReducer.DispatchChargeDelta(EmptyCharges(), reconstructed),
                CampaignStateReducer.DispatchChargeDelta(EmptyCharges(), lineage))
            && reconstructed.HasUnpricedCostHistory == lineage.HasUnpricedCostHistory
            // Host and M2 own additional independent intervals; their elapsed
            // charges are not reconstructible from provider history alone.
            && reconstructed.ActiveElapsedMilliseconds.TotalCharged <= lineage.ActiveElapsedMilliseconds.TotalCharged
            && (reconstructed.ActiveElapsedMilliseconds.Observed ?? 0) <= (lineage.ActiveElapsedMilliseconds.Observed ?? 0)
            && reconstructed.ActiveElapsedMilliseconds.ConservativeUnobserved <= lineage.ActiveElapsedMilliseconds.ConservativeUnobserved,
            CampaignStateValidationCode.InvalidCorrelation);
        if (state.ActiveReservation is CampaignProviderReservation { OperationKind: CampaignProviderOperationKind.Dispatch } dispatch)
            Require(state.DispatchObservations.Length > 0
                && state.DispatchObservations[^1].State == CampaignDispatchObservationState.Reserved
                && MatchesDispatch(state.DispatchObservations[^1], state, dispatch), CampaignStateValidationCode.InvalidCorrelation);
    }

    private static bool SameCharge(CampaignChargeObservation left, CampaignChargeObservation right) =>
        left.Observed == right.Observed
        && left.ConservativeUnobserved == right.ConservativeUnobserved && left.TotalCharged == right.TotalCharged;

    private static bool SameProviderCharges(CampaignDispatchCharges left, CampaignDispatchCharges right) =>
        SameCharge(left.ProviderRequests, right.ProviderRequests) && SameCharge(left.InputTokens, right.InputTokens)
        && SameCharge(left.CachedInputTokens, right.CachedInputTokens) && SameCharge(left.UncachedInputTokens, right.UncachedInputTokens)
        && SameCharge(left.OutputTokens, right.OutputTokens) && SameCharge(left.ReasoningTokens, right.ReasoningTokens)
        && SameCharge(left.CostMicrounits, right.CostMicrounits);

    private static bool SameDispatchCharges(CampaignDispatchCharges left, CampaignDispatchCharges right) =>
        SameProviderCharges(left, right) && SameCharge(left.ActiveElapsedMilliseconds, right.ActiveElapsedMilliseconds);

    private static bool MatchesDispatch(CampaignDispatchObservation row, CampaignCheckpointState state,
        CampaignProviderReservation claim) => row.ExecutionCommitmentSha256 == state.Snapshot.ExecutionCommitmentSha256
        && row.BatchIdentity == state.Batch.Identity
        && row.ConfigurationCommitmentSha256 == state.ConfiguredCeilings.CampaignConfigurationCommitmentSha256
        && row.WorkItemKey == claim.WorkItemKey && row.AttemptId == claim.AttemptId
        && row.ScribeRequestSha256 == claim.ScribeRequestSha256 && row.ExecutionStartRevision == claim.ExecutionStartRevision
        && row.DispatchOrdinal == claim.LastDispatchOrdinal && row.RequestCommitmentSha256 == claim.RequestCommitmentSha256
        && row.Exposure == claim.Exposure;
}

public static partial class CampaignStateReducer
{
    internal static CampaignDispatchCharges DispatchChargeDelta(CampaignLineageCharges before, CampaignLineageCharges after)
    {
        static CampaignChargeObservation Delta(CampaignChargeObservation first, CampaignChargeObservation last) => new(
            last.Observed is { } observed ? checked(observed - (first.Observed ?? 0)) : null,
            checked(last.ConservativeUnobserved - first.ConservativeUnobserved), checked(last.TotalCharged - first.TotalCharged));
        return new(Delta(before.ProviderRequests, after.ProviderRequests), Delta(before.InputTokens, after.InputTokens),
            Delta(before.CachedInputTokens, after.CachedInputTokens), Delta(before.UncachedInputTokens, after.UncachedInputTokens),
            Delta(before.OutputTokens, after.OutputTokens), Delta(before.ReasoningTokens, after.ReasoningTokens),
            Delta(before.CostMicrounits, after.CostMicrounits), Delta(before.ActiveElapsedMilliseconds, after.ActiveElapsedMilliseconds));
    }

    private static ImmutableArray<CampaignDispatchObservation> RetireDispatchObservation(
        CampaignCheckpointState state, CampaignLineageCharges charges)
    {
        if (state.ActiveReservation is not CampaignProviderReservation { OperationKind: CampaignProviderOperationKind.Dispatch })
            return state.DispatchObservations;
        var row = state.DispatchObservations[^1] with
        {
            State = CampaignDispatchObservationState.Unknown,
            Disposition = DocumentationScribeDispatchDisposition.Interrupted,
            Charges = DispatchChargeDelta(state.LineageCharges, charges),
        };
        row = row with { SettlementCommitmentSha256 = CampaignStateFactory.DispatchSettlementCommitment(row) };
        return state.DispatchObservations.SetItem(state.DispatchObservations.Length - 1, row);
    }
}
