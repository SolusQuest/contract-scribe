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
    internal static string DispatchSettlementCommitment(CampaignDispatchObservation row) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "contract-scribe/campaign-dispatch-observation/v1\n"
            + JsonSerializer.Serialize(row with { SettlementCommitmentSha256 = null })))).ToLowerInvariant();

    private static void ValidateDispatchObservations(CampaignCheckpointState state)
    {
        Require(!state.DispatchObservations.IsDefault, CampaignStateValidationCode.InvalidShape);
        var ordinals = new Dictionary<(string Execution, string Work, DocumentationScribeAttemptId Attempt), long>();
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
                Require(row.Charges.ProviderRequests.TotalCharged == (row.State == CampaignDispatchObservationState.NotSent ? 0 : 1),
                    CampaignStateValidationCode.InvalidCorrelation);
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
        var settled = state.DispatchObservations.Where(row => row.Charges is not null).Select(row => row.Charges!).ToArray();
        Require(settled.Sum(row => row.ProviderRequests.TotalCharged) == state.LineageCharges.ProviderRequests.TotalCharged
            && settled.Sum(row => row.ProviderRequests.ConservativeUnobserved) == state.LineageCharges.ProviderRequests.ConservativeUnobserved
            && settled.Sum(row => row.ProviderRequests.Observed ?? 0) == (state.LineageCharges.ProviderRequests.Observed ?? 0),
            CampaignStateValidationCode.InvalidCorrelation);
        if (state.ActiveReservation is CampaignProviderReservation { OperationKind: CampaignProviderOperationKind.Dispatch } dispatch)
            Require(state.DispatchObservations.Length > 0
                && state.DispatchObservations[^1].State == CampaignDispatchObservationState.Reserved
                && MatchesDispatch(state.DispatchObservations[^1], state, dispatch), CampaignStateValidationCode.InvalidCorrelation);
    }

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
    private static CampaignDispatchCharges DispatchChargeDelta(CampaignLineageCharges before, CampaignLineageCharges after)
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
