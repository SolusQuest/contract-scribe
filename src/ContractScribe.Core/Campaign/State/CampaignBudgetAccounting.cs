namespace ContractScribe.Core;

public enum CampaignBudgetDecisionKind
{
    Admitted,
    Exhausted,
    Invalid,
}

public sealed class CampaignProviderBudgetDecision
{
    internal CampaignProviderBudgetDecision(
        CampaignBudgetDecisionKind kind,
        CampaignLineageCharges? charges,
        CampaignProviderReservationExposure? exposure)
    {
        Kind = kind;
        Charges = charges;
        Exposure = exposure;
    }

    public CampaignBudgetDecisionKind Kind { get; }
    public CampaignLineageCharges? Charges { get; }
    public CampaignProviderReservationExposure? Exposure { get; }
}

public sealed class CampaignSettlementDecision
{
    internal CampaignSettlementDecision(
        CampaignBudgetDecisionKind kind,
        CampaignLineageCharges? charges)
    {
        Kind = kind;
        Charges = charges;
    }

    public CampaignBudgetDecisionKind Kind { get; }
    public CampaignLineageCharges? Charges { get; }
}

/// <summary>
/// The sole checked arithmetic authority for durable campaign charges.
/// Active reservations are exposure, while <see cref="CampaignLineageCharges"/>
/// contains settled history.
/// </summary>
public static class CampaignBudgetAccounting
{
    public static CampaignProviderBudgetDecision ReserveProviderInvocation(
        CampaignCheckpointState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        try
        {
            var budget = state.ConfiguredCeilings.CampaignBudget;
            var limits = state.ConfiguredCeilings.ScribeRunLimits;
            var exposure = new CampaignProviderReservationExposure(
                limits.MaximumProviderRequests,
                limits.MaximumInputTokens,
                limits.MaximumUncachedInputTokens,
                limits.MaximumOutputTokens,
                ProviderCostExposure(budget, limits),
                limits.MaximumElapsedMilliseconds);
            var charges = state.LineageCharges with
            {
                OuterInvocations = checked(state.LineageCharges.OuterInvocations + 1),
            };

            return FitsProviderBudget(charges, exposure, budget)
                ? new CampaignProviderBudgetDecision(CampaignBudgetDecisionKind.Admitted, charges, exposure)
                : new CampaignProviderBudgetDecision(CampaignBudgetDecisionKind.Exhausted, null, null);
        }
        catch (OverflowException)
        {
            return new CampaignProviderBudgetDecision(CampaignBudgetDecisionKind.Invalid, null, null);
        }
    }

    public static CampaignSettlementDecision SettleProviderInvocation(
        CampaignCheckpointState state,
        DocumentationScribeValidatedRunOutcome outcome,
        long? activeElapsedMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(outcome);
        if (state.ActiveReservation is not CampaignProviderReservation reservation
            || !string.Equals(reservation.ScribeRequestSha256, outcome.Request.ArtifactSha256, StringComparison.Ordinal)
            || reservation.AttemptId != outcome.RunResult.AttemptId
            || outcome.RunResult.RunEnvelope.AttemptId != reservation.AttemptId
            || activeElapsedMilliseconds is < 0
            || activeElapsedMilliseconds > CampaignStateContract.MaximumObservation)
        {
            return new CampaignSettlementDecision(CampaignBudgetDecisionKind.Invalid, null);
        }

        var envelope = outcome.RunResult.RunEnvelope;
        var budget = state.ConfiguredCeilings.CampaignBudget;
        if (activeElapsedMilliseconds is { } hostElapsed
                && hostElapsed < envelope.ElapsedMilliseconds
            || budget.CostEnforced && envelope.Cost is not null
                && !string.Equals(envelope.Cost.CurrencyId, budget.CostCurrency, StringComparison.Ordinal))
        {
            return new CampaignSettlementDecision(CampaignBudgetDecisionKind.Invalid, null);
        }

        try
        {
            var usage = envelope.Usage;
            var charges = state.LineageCharges with
            {
                ProviderRequests = AddExact(
                    state.LineageCharges.ProviderRequests,
                    envelope.ProviderRequestCount),
                InputTokens = AddObservation(
                    state.LineageCharges.InputTokens,
                    usage?.InputTokens,
                    reservation.Exposure.InputTokens),
                CachedInputTokens = AddObservation(
                    state.LineageCharges.CachedInputTokens,
                    usage?.CachedInputTokens,
                    reservation.Exposure.InputTokens),
                UncachedInputTokens = AddObservation(
                    state.LineageCharges.UncachedInputTokens,
                    usage?.UncachedInputTokens,
                    reservation.Exposure.UncachedInputTokens),
                OutputTokens = AddObservation(
                    state.LineageCharges.OutputTokens,
                    usage?.OutputTokens,
                    reservation.Exposure.OutputTokens),
                ReasoningTokens = AddObservation(
                    state.LineageCharges.ReasoningTokens,
                    usage?.ReasoningTokens,
                    reservation.Exposure.OutputTokens),
                CostMicrounits = SettleCost(state, reservation, envelope),
                HasUnpricedCostHistory = state.LineageCharges.HasUnpricedCostHistory
                    || !budget.CostEnforced && envelope.ProviderRequestCount > 0,
                ActiveElapsedMilliseconds = AddObservation(
                    state.LineageCharges.ActiveElapsedMilliseconds,
                    activeElapsedMilliseconds,
                    reservation.Exposure.ElapsedMilliseconds),
            };
            return FitsSettledBudget(charges, budget)
                ? new CampaignSettlementDecision(CampaignBudgetDecisionKind.Admitted, charges)
                : new CampaignSettlementDecision(CampaignBudgetDecisionKind.Exhausted, charges);
        }
        catch (OverflowException)
        {
            return new CampaignSettlementDecision(CampaignBudgetDecisionKind.Invalid, null);
        }
    }

    public static CampaignSettlementDecision ReservePatchInvocation(
        CampaignCheckpointState state,
        long elapsedMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(state);
        return ReservePatchInvocation(
            state.LineageCharges,
            state.ConfiguredCeilings.CampaignBudget,
            elapsedMilliseconds);
    }

    internal static CampaignSettlementDecision ReservePatchInvocation(
        CampaignLineageCharges settledCharges,
        CampaignStateCampaignBudget budget,
        long elapsedMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(settledCharges);
        ArgumentNullException.ThrowIfNull(budget);
        if (elapsedMilliseconds <= 0
            || elapsedMilliseconds > CampaignStateContract.MaximumObservation)
        {
            return new CampaignSettlementDecision(CampaignBudgetDecisionKind.Invalid, null);
        }

        try
        {
            var charges = settledCharges with
            {
                PatchValidationInvocations = checked(settledCharges.PatchValidationInvocations + 1),
            };
            var totalElapsed = checked(charges.ActiveElapsedMilliseconds.TotalCharged + elapsedMilliseconds);
            return FitsSettledBudget(charges, budget) && Within(totalElapsed, budget.MaximumElapsedMilliseconds)
                ? new CampaignSettlementDecision(CampaignBudgetDecisionKind.Admitted, charges)
                : new CampaignSettlementDecision(CampaignBudgetDecisionKind.Exhausted, null);
        }
        catch (OverflowException)
        {
            return new CampaignSettlementDecision(CampaignBudgetDecisionKind.Invalid, null);
        }
    }

    public static CampaignSettlementDecision SettlePatchInvocation(
        CampaignCheckpointState state,
        long? activeElapsedMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.ActiveReservation is not CampaignPatchReservation reservation
            || reservation.PatchAttemptCount != 1
            || activeElapsedMilliseconds is < 0
            || activeElapsedMilliseconds > CampaignStateContract.MaximumObservation)
        {
            return new CampaignSettlementDecision(CampaignBudgetDecisionKind.Invalid, null);
        }

        try
        {
            var charges = state.LineageCharges with
            {
                ActiveElapsedMilliseconds = AddObservation(
                    state.LineageCharges.ActiveElapsedMilliseconds,
                    activeElapsedMilliseconds,
                    reservation.ElapsedMilliseconds),
            };
            return FitsSettledBudget(charges, state.ConfiguredCeilings.CampaignBudget)
                ? new CampaignSettlementDecision(CampaignBudgetDecisionKind.Admitted, charges)
                : new CampaignSettlementDecision(CampaignBudgetDecisionKind.Exhausted, charges);
        }
        catch (OverflowException)
        {
            return new CampaignSettlementDecision(CampaignBudgetDecisionKind.Invalid, null);
        }
    }

    internal static CampaignLineageCharges SettleActiveConservatively(CampaignCheckpointState state)
    {
        return state.ActiveReservation switch
        {
            CampaignProviderReservation provider => state.LineageCharges with
            {
                ProviderRequests = AddUnknown(state.LineageCharges.ProviderRequests, provider.Exposure.ProviderRequests),
                InputTokens = AddUnknown(state.LineageCharges.InputTokens, provider.Exposure.InputTokens),
                CachedInputTokens = AddUnknown(state.LineageCharges.CachedInputTokens, provider.Exposure.InputTokens),
                UncachedInputTokens = AddUnknown(state.LineageCharges.UncachedInputTokens, provider.Exposure.UncachedInputTokens),
                OutputTokens = AddUnknown(state.LineageCharges.OutputTokens, provider.Exposure.OutputTokens),
                ReasoningTokens = AddUnknown(state.LineageCharges.ReasoningTokens, provider.Exposure.OutputTokens),
                HasUnpricedCostHistory = state.LineageCharges.HasUnpricedCostHistory
                    || !state.ConfiguredCeilings.CampaignBudget.CostEnforced,
                CostMicrounits = state.ConfiguredCeilings.CampaignBudget.CostEnforced
                    ? AddUnknown(state.LineageCharges.CostMicrounits, provider.Exposure.CostMicrounits)
                    : state.LineageCharges.CostMicrounits,
                ActiveElapsedMilliseconds = AddUnknown(
                    state.LineageCharges.ActiveElapsedMilliseconds,
                    provider.Exposure.ElapsedMilliseconds),
            },
            CampaignPatchReservation patch => state.LineageCharges with
            {
                ActiveElapsedMilliseconds = AddUnknown(
                    state.LineageCharges.ActiveElapsedMilliseconds,
                    patch.ElapsedMilliseconds),
            },
            _ => state.LineageCharges,
        };
    }

    internal static long ProviderCostExposure(
        CampaignStateCampaignBudget budget,
        CampaignStateScribeLimits limits) => budget.CostEnforced
            ? Math.Max(limits.MaximumCostMicrounits, budget.CostRates!.ConservativeCost(
                limits.MaximumInputTokens, limits.MaximumUncachedInputTokens,
                limits.MaximumOutputTokens, limits.MaximumProviderRequests))
            : 0;

    private static CampaignChargeObservation SettleCost(
        CampaignCheckpointState state,
        CampaignProviderReservation reservation,
        DocumentationScribeRunEnvelope envelope)
    {
        var budget = state.ConfiguredCeilings.CampaignBudget;
        var previous = state.LineageCharges.CostMicrounits;
        if (!budget.CostEnforced) return previous;
        if (envelope.Cost is { } exact && envelope.ProviderRequestCount == 1)
        {
            return AddExact(previous, exact.AmountMicrounits);
        }
        var limits = state.ConfiguredCeilings.ScribeRunLimits;
        var usage = envelope.Usage;
        var bound = Math.Max(reservation.Exposure.CostMicrounits, budget.CostRates!.ConservativeCost(
            Math.Max(limits.MaximumInputTokens, Math.Max(usage?.InputTokens ?? 0, usage?.CachedInputTokens ?? 0)),
            Math.Max(limits.MaximumUncachedInputTokens, usage?.UncachedInputTokens ?? 0),
            Math.Max(limits.MaximumOutputTokens, Math.Max(usage?.OutputTokens ?? 0, usage?.ReasoningTokens ?? 0)),
            Math.Max(limits.MaximumProviderRequests, envelope.ProviderRequestCount)));
        if (envelope.Cost is not { } reported) return AddUnknown(previous, bound);
        // Aggregated present fields do not prove complete monetary observation for every exchange.
        var known = AddExact(previous, reported.AmountMicrounits);
        return AddUnknown(known, Math.Max(0, checked(bound - reported.AmountMicrounits)));
    }

    internal static bool FitsSettledBudget(
        CampaignLineageCharges charges,
        CampaignStateCampaignBudget budget) =>
        Within(charges.ProviderRequests.TotalCharged, budget.MaximumProviderRequests)
        && Within(charges.InputTokens.TotalCharged, budget.MaximumInputTokens)
        && Within(charges.UncachedInputTokens.TotalCharged, budget.MaximumUncachedInputTokens)
        && Within(charges.OutputTokens.TotalCharged, budget.MaximumOutputTokens)
        && FitsCost(charges, charges.CostMicrounits.TotalCharged, budget)
        && Within(charges.ActiveElapsedMilliseconds.TotalCharged, budget.MaximumElapsedMilliseconds);

    private static bool FitsProviderBudget(
        CampaignLineageCharges charges,
        CampaignProviderReservationExposure exposure,
        CampaignStateCampaignBudget budget)
    {
        // Evaluate every checked sum even if its cap is unlimited or an earlier dimension fails.
        var requests = checked(charges.ProviderRequests.TotalCharged + exposure.ProviderRequests);
        var input = checked(charges.InputTokens.TotalCharged + exposure.InputTokens);
        var uncached = checked(charges.UncachedInputTokens.TotalCharged + exposure.UncachedInputTokens);
        var output = checked(charges.OutputTokens.TotalCharged + exposure.OutputTokens);
        var cost = checked(charges.CostMicrounits.TotalCharged + exposure.CostMicrounits);
        var elapsed = checked(charges.ActiveElapsedMilliseconds.TotalCharged + exposure.ElapsedMilliseconds);
        return Within(requests, budget.MaximumProviderRequests)
            && Within(input, budget.MaximumInputTokens)
            && Within(uncached, budget.MaximumUncachedInputTokens)
            && Within(output, budget.MaximumOutputTokens)
            && FitsCost(charges, cost, budget)
            && Within(elapsed, budget.MaximumElapsedMilliseconds);
    }

    private static bool FitsCost(CampaignLineageCharges charges, long cost, CampaignStateCampaignBudget budget) =>
        budget.MaximumCostMicrounits is null
        || budget.CostEnforced && budget.CostRates is not null && !charges.HasUnpricedCostHistory
            && cost <= budget.MaximumCostMicrounits;

    private static bool Within(long amount, long? cap) => cap is null || amount <= cap;

    private static CampaignChargeObservation AddExact(CampaignChargeObservation charge, long value)
    {
        var observed = checked((charge.Observed ?? 0) + value);
        return new CampaignChargeObservation(
            observed,
            charge.ConservativeUnobserved,
            checked(observed + charge.ConservativeUnobserved));
    }

    private static CampaignChargeObservation AddObservation(
        CampaignChargeObservation charge,
        long? observed,
        long conservativeMaximum) => observed is { } exact
            ? AddExact(charge, exact)
            : AddUnknown(charge, conservativeMaximum);

    private static CampaignChargeObservation AddUnknown(CampaignChargeObservation charge, long value)
    {
        var conservative = checked(charge.ConservativeUnobserved + value);
        return new CampaignChargeObservation(
            charge.Observed,
            conservative,
            checked((charge.Observed ?? 0) + conservative));
    }
}
