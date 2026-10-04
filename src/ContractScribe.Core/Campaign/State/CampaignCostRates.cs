using System.Text.Json;

namespace ContractScribe.Core;

/// <summary>The configured rates used for bounded campaign estimates, not provider billing.</summary>
public sealed record CampaignCostRates(
    long CachedInputMicrounitsPerMillion,
    long UncachedInputMicrounitsPerMillion,
    long OutputMicrounitsPerMillion,
    long ReasoningMicrounitsPerMillion)
{
    internal bool IsValid =>
        CachedInputMicrounitsPerMillion is >= 0 and <= CampaignStateContract.MaximumObservation
        && UncachedInputMicrounitsPerMillion is >= 0 and <= CampaignStateContract.MaximumObservation
        && OutputMicrounitsPerMillion is >= 0 and <= CampaignStateContract.MaximumObservation
        && ReasoningMicrounitsPerMillion is >= 0 and <= CampaignStateContract.MaximumObservation;

    public CampaignPlanningContentAuthority CreateAuthority(string currencyId, string ratePolicyId) =>
        CampaignPlanningContentAuthority.CreateValidatedJsonProjection(
            CampaignPlanningContentFamily.CostRatePolicy,
            ratePolicyId,
            JsonSerializer.SerializeToElement(new
            {
                currencyId,
                ratePolicyId,
                cachedInputMicrounitsPerMillion = CachedInputMicrounitsPerMillion,
                uncachedInputMicrounitsPerMillion = UncachedInputMicrounitsPerMillion,
                outputMicrounitsPerMillion = OutputMicrounitsPerMillion,
                reasoningMicrounitsPerMillion = ReasoningMicrounitsPerMillion,
            }));

    internal long ConservativeCost(long input, long uncached, long output, int requests)
    {
        if (!IsValid || input < 0 || uncached < 0 || output < 0 || requests < 1)
        {
            throw new OverflowException();
        }
        checked
        {
            // The codec admits independent cache partitions when total input is absent.
            var totalInput = (Int128)input * Math.Max(
                CachedInputMicrounitsPerMillion, UncachedInputMicrounitsPerMillion);
            var independentInput = (Int128)input * CachedInputMicrounitsPerMillion
                + (Int128)uncached * UncachedInputMicrounitsPerMillion;
            var numerator = Int128.Max(totalInput, independentInput)
                + (Int128)output * Math.Max(OutputMicrounitsPerMillion, ReasoningMicrounitsPerMillion);
            // Reasoning is an output subset. Rounding per exchange can add at most N-1.
            var rounded = numerator == 0 ? 0 : (numerator + 999_999) / 1_000_000 + requests - 1;
            if (rounded > CampaignStateContract.MaximumObservation) throw new OverflowException();
            return (long)rounded;
        }
    }
}
