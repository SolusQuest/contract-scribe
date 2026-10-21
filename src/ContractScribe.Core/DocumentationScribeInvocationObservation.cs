namespace ContractScribe.Core;

/// <summary>Reported subtotal and coverage; absent measurements never become zero.</summary>
public sealed record DocumentationScribeTokenObservation(long? Observed, int MissingDispatches);

/// <summary>A read-only, process-local projection. It grants no budget or recovery authority.</summary>
public sealed record DocumentationScribeInvocationObservation(
    int PhysicalProviderDispatches, int? LogicalProviderCalls, int? RetryDispatches,
    int UncorrelatedDispatches, int ToolCalls, int ActiveSent, int ActiveReserved,
    int UnsettledDispatches,
    DocumentationScribeTokenObservation Input, DocumentationScribeTokenObservation CachedInput,
    DocumentationScribeTokenObservation UncachedInput, DocumentationScribeTokenObservation Output,
    DocumentationScribeTokenObservation Reasoning,
    CampaignChargeObservation InputCharge, CampaignChargeObservation CachedInputCharge,
    CampaignChargeObservation UncachedInputCharge, CampaignChargeObservation OutputCharge,
    DocumentationScribeDispatchExposure? ActiveExposure, int? ElapsedMilliseconds,
    DocumentationScribeInvocationStopReason StopReason);

public sealed partial class DocumentationScribeInvocationAllowance
{
    // Actual sends are already bounded by MaximumProviderRequests (at most 128).
    private readonly List<DocumentationScribeInvocationProviderPermit> sentPermits = [];

    internal void ObserveProviderUsage(DocumentationScribeInvocationProviderPermit permit,
        DocumentationScribeDispatchUsage? usage)
    {
        lock (gate)
        {
            if (!ReferenceEquals(active, permit) || !permit.OwnedBy(this) || !permit.DispatchStarted
                || permit.MeasurementCaptured || !ValidUsage(usage)) return;
            permit.ObservedUsage = usage;
            permit.MeasurementCaptured = true;
        }
    }

    public DocumentationScribeInvocationObservation Observe()
    {
        lock (gate)
        {
            DocumentationScribeTokenObservation Tokens(Func<DocumentationScribeDispatchUsage?, int?> select)
            {
                var known = sentPermits.Select(permit => select(permit.ObservedUsage)).ToArray();
                var missing = known.Count(value => value is null);
                return new(known.Length == 0 ? 0 : missing == known.Length ? null
                    : known.Sum(value => (long)(value ?? 0)), missing);
            }
            var uncorrelated = sentPermits.Count(permit => permit.LogicalCallId is null || permit.PhysicalAttemptNumber is null);
            int? elapsed;
            try { elapsed = ElapsedMilliseconds; }
            catch (InvalidOperationException) { elapsed = null; }
            return new(requests,
                uncorrelated == 0 ? sentPermits.Select(permit => permit.LogicalCallId).Distinct().Count() : null,
                uncorrelated == 0 ? sentPermits.Count(permit => permit.PhysicalAttemptNumber > 1) : null,
                uncorrelated, tools, active?.DispatchStarted == true ? 1 : 0,
                active is { DispatchStarted: false } ? 1 : 0,
                sentPermits.Count(permit => !permit.ChargeCommitted),
                Tokens(usage => usage?.InputTokens ?? CompleteInput(usage)), Tokens(usage => usage?.CachedInputTokens),
                Tokens(usage => usage?.UncachedInputTokens), Tokens(usage => usage?.OutputTokens),
                Tokens(usage => usage?.ReasoningTokens), input, cached, uncached, output, active?.Exposure, elapsed, stop);
        }
    }
}
