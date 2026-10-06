using ContractScribe.Core;

namespace ContractScribe.Roslyn.IntegrationTests;

internal static class CampaignInvocationTestPolicy
{
    internal static DocumentationScribeInvocationAllowance Create(CampaignStateScribeLimits limits, TimeProvider? clock = null) =>
        Create(new DocumentationScribeRunLimits(limits.MaximumContextReferences, limits.MaximumContextUtf8Bytes,
            limits.MaximumEvidenceReferences, limits.MaximumEvidenceUtf8Bytes, limits.MaximumProviderRequests,
            limits.MaximumToolRounds, limits.MaximumToolCalls, limits.MaximumAttempts, limits.MaximumInputTokens,
            limits.MaximumUncachedInputTokens, limits.MaximumOutputTokens, limits.MaximumCostMicrounits,
            limits.MaximumElapsedMilliseconds), clock);

    internal static DocumentationScribeInvocationAllowance Create(DocumentationScribeRunLimits limits, TimeProvider? clock = null)
    {
        var hit = limits.MaximumInputTokens / 2;
        var miss = Math.Min(limits.MaximumUncachedInputTokens, limits.MaximumInputTokens - hit);
        return new(DocumentationScribeInvocationLimits.Create(maximumProviderRequests: limits.MaximumProviderRequests,
            maximumToolCalls: limits.MaximumToolCalls, maximumCachedInputTokens: hit, maximumUncachedInputTokens: miss,
            maximumOutputTokens: limits.MaximumOutputTokens, maximumElapsedMilliseconds: limits.MaximumElapsedMilliseconds,
            maximumRequestOutputTokens: limits.MaximumOutputTokens,
            maximumRequestElapsedMilliseconds: Math.Min(300_000, limits.MaximumElapsedMilliseconds)), clock);
    }
}
