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
        CampaignCheckpointState state, DocumentationScribeInvocationAllowance allowance) =>
        ReserveProviderAttempt(state, allowance, chargeOuterInvocation: true);

    internal static CampaignProviderBudgetDecision ReserveProviderResume(
        CampaignCheckpointState state, DocumentationScribeInvocationAllowance allowance) =>
        ReserveProviderAttempt(state, allowance, chargeOuterInvocation: false);

    private static CampaignProviderBudgetDecision ReserveProviderAttempt(
        CampaignCheckpointState state, DocumentationScribeInvocationAllowance allowance, bool chargeOuterInvocation)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(allowance);
        try
        {
            if (!allowance.CanBeginProviderWork())
                return new(CampaignBudgetDecisionKind.Exhausted, null, null);
            var budget = state.ConfiguredCeilings.CampaignBudget;
            var dispatch = allowance.PreviewProviderExposure();
            var lifetime = RemainingLifetimeElapsed(state.LineageCharges, budget);
            var potential = ProviderExposure(budget, dispatch with
            { ElapsedMilliseconds = Math.Min(dispatch.ElapsedMilliseconds, lifetime) });
            var hostElapsed = Math.Min(state.ConfiguredCeilings.ScribeRunLimits.MaximumElapsedMilliseconds,
                Math.Min(allowance.Limits.MaximumRequestElapsedMilliseconds, Math.Min(allowance.RemainingMilliseconds, lifetime)));
            var charges = chargeOuterInvocation ? state.LineageCharges with
            { OuterInvocations = checked(state.LineageCharges.OuterInvocations + 1) } : state.LineageCharges;
            return hostElapsed > 0 && potential.ElapsedMilliseconds > 0
                && FitsProviderBudget(charges, potential, budget)
                ? new(CampaignBudgetDecisionKind.Admitted, charges, HostExposure(hostElapsed))
                : new(CampaignBudgetDecisionKind.Exhausted, null, null);
        }
        catch (OverflowException) { return new(CampaignBudgetDecisionKind.Invalid, null, null); }
    }

    public static CampaignSettlementDecision SettleProviderInvocation(
        CampaignCheckpointState state, DocumentationScribeValidatedRunOutcome outcome,
        long? activeElapsedMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(outcome);
        if (state.ActiveReservation is not CampaignProviderReservation reservation
            || reservation.OperationKind != CampaignProviderOperationKind.Host
            || !string.Equals(reservation.ScribeRequestSha256, outcome.Request.ArtifactSha256, StringComparison.Ordinal)
            || reservation.AttemptId != outcome.RunResult.AttemptId
            || reservation.CurrentExecutionSettledProviderRequests != outcome.RunResult.RunEnvelope.ProviderRequestCount
            || reservation.RestoredRetryableProviderFailures != outcome.RunResult.RunEnvelope.RestoredRetryableProviderFailures
            || activeElapsedMilliseconds is < 0 or > CampaignStateContract.MaximumObservation)
            return new(CampaignBudgetDecisionKind.Invalid, null);
        try
        {
            var charges = SettleHostInterval(state.LineageCharges, activeElapsedMilliseconds,
                reservation.Exposure.ElapsedMilliseconds);
            return new(FitsSettledBudget(charges, state.ConfiguredCeilings.CampaignBudget)
                ? CampaignBudgetDecisionKind.Admitted : CampaignBudgetDecisionKind.Exhausted, charges);
        }
        catch (OverflowException) { return new(CampaignBudgetDecisionKind.Invalid, null); }
    }

    internal static CampaignProviderReservationExposure HostExposure(int elapsed) => new(0, 0, 0, 0, 0, elapsed);

    internal static CampaignProviderReservationExposure ProviderExposure(CampaignStateCampaignBudget budget,
        DocumentationScribeDispatchExposure exposure) => new(1, exposure.InputTokens, exposure.UncachedInputTokens,
            exposure.OutputTokens, budget.CostEnforced
                ? budget.CostRates!.ConservativeDispatchCost(exposure.CachedInputTokens, exposure.UncachedInputTokens, exposure.OutputTokens) : 0,
            exposure.ElapsedMilliseconds)
        { CachedInputTokens = exposure.CachedInputTokens };

    internal static int RemainingLifetimeElapsed(CampaignLineageCharges charges, CampaignStateCampaignBudget budget) =>
        budget.MaximumElapsedMilliseconds is { } cap
            ? (int)Math.Clamp(checked(cap - charges.ActiveElapsedMilliseconds.TotalCharged), 0, int.MaxValue)
            : int.MaxValue;

    internal static CampaignLineageCharges SettleHostInterval(CampaignLineageCharges charges,
        long? elapsed, int exposure) => charges with
        { ActiveElapsedMilliseconds = AddObservation(charges.ActiveElapsedMilliseconds, elapsed, exposure) };

    internal static CampaignSettlementDecision SettleProviderDispatch(CampaignCheckpointState state,
        bool sent, DocumentationScribeDispatchUsage? usage, long elapsed)
    {
        if (state.ActiveReservation is not CampaignProviderReservation reservation
            || reservation.OperationKind != CampaignProviderOperationKind.Dispatch
            || elapsed is < 0 or > CampaignStateContract.MaximumObservation
            || !DocumentationScribeInvocationAllowance.ValidUsage(usage)
            || !sent && usage is not null
            || state.ConfiguredCeilings.CampaignBudget.CostEnforced && usage?.CurrencyId is { } currency
                && !string.Equals(currency, state.ConfiguredCeilings.CampaignBudget.CostCurrency, StringComparison.Ordinal))
            return new(CampaignBudgetDecisionKind.Invalid, null);
        try
        {
            var previous = state.LineageCharges;
            var exposure = reservation.Exposure;
            var charges = sent ? previous with
            {
                ProviderRequests = AddExact(previous.ProviderRequests, 1),
                InputTokens = ChargeDispatchDimension(previous.InputTokens,
                    usage?.InputTokens ?? DocumentationScribeInvocationAllowance.CompleteInput(usage), exposure.InputTokens,
                    checked((long)(usage?.CachedInputTokens ?? 0) + (usage?.UncachedInputTokens ?? 0))),
                CachedInputTokens = ChargeDispatchDimension(previous.CachedInputTokens, usage?.CachedInputTokens, exposure.CachedInputTokens),
                UncachedInputTokens = ChargeDispatchDimension(previous.UncachedInputTokens, usage?.UncachedInputTokens, exposure.UncachedInputTokens),
                OutputTokens = ChargeDispatchDimension(previous.OutputTokens, usage?.OutputTokens, exposure.OutputTokens, usage?.ReasoningTokens ?? 0),
                ReasoningTokens = ChargeDispatchDimension(previous.ReasoningTokens, usage?.ReasoningTokens,
                    Math.Max(exposure.OutputTokens, usage?.OutputTokens ?? 0)),
                CostMicrounits = SettleDispatchCost(state.ConfiguredCeilings.CampaignBudget, previous.CostMicrounits, exposure, usage),
                HasUnpricedCostHistory = previous.HasUnpricedCostHistory || !state.ConfiguredCeilings.CampaignBudget.CostEnforced,
            } : previous;
            charges = SettleHostInterval(charges, elapsed, exposure.ElapsedMilliseconds);
            return new(FitsSettledBudget(charges, state.ConfiguredCeilings.CampaignBudget)
                ? CampaignBudgetDecisionKind.Admitted : CampaignBudgetDecisionKind.Exhausted, charges);
        }
        catch (OverflowException) { return new(CampaignBudgetDecisionKind.Invalid, null); }
    }

    private static CampaignChargeObservation SettleDispatchCost(CampaignStateCampaignBudget budget,
        CampaignChargeObservation previous, CampaignProviderReservationExposure exposure, DocumentationScribeDispatchUsage? usage)
    {
        if (!budget.CostEnforced) return previous;
        if (usage?.CostMicrounits is { } exact) return AddExact(previous, exact);
        var cachedBound = usage?.CachedInputTokens ?? exposure.CachedInputTokens;
        var uncachedBound = usage?.UncachedInputTokens ?? exposure.UncachedInputTokens;
        var outputBound = Math.Max(usage?.OutputTokens ?? exposure.OutputTokens, usage?.ReasoningTokens ?? 0);
        var partitionCost = budget.CostRates!.ConservativeDispatchCost(cachedBound, uncachedBound, outputBound);
        var knownInput = Math.Max(usage?.InputTokens ?? 0,
            checked((long)(usage?.CachedInputTokens ?? 0) + (usage?.UncachedInputTokens ?? 0)));
        var bound = knownInput > checked((long)cachedBound + uncachedBound)
            ? Math.Max(partitionCost, budget.CostRates.ConservativeCost(knownInput, uncachedBound, outputBound, 1))
            : partitionCost;
        return AddUnknown(previous, bound);
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
            CampaignProviderReservation { OperationKind: CampaignProviderOperationKind.Host } host =>
                SettleHostInterval(state.LineageCharges, null, host.Exposure.ElapsedMilliseconds),
            CampaignProviderReservation provider => state.LineageCharges with
            {
                ProviderRequests = AddUnknown(state.LineageCharges.ProviderRequests, 1),
                InputTokens = AddUnknown(state.LineageCharges.InputTokens, provider.Exposure.InputTokens),
                CachedInputTokens = AddUnknown(state.LineageCharges.CachedInputTokens, provider.Exposure.CachedInputTokens),
                UncachedInputTokens = AddUnknown(state.LineageCharges.UncachedInputTokens, provider.Exposure.UncachedInputTokens),
                OutputTokens = AddUnknown(state.LineageCharges.OutputTokens, provider.Exposure.OutputTokens),
                ReasoningTokens = AddUnknown(state.LineageCharges.ReasoningTokens, provider.Exposure.OutputTokens),
                HasUnpricedCostHistory = state.LineageCharges.HasUnpricedCostHistory || !state.ConfiguredCeilings.CampaignBudget.CostEnforced,
                CostMicrounits = state.ConfiguredCeilings.CampaignBudget.CostEnforced
                    ? AddUnknown(state.LineageCharges.CostMicrounits, provider.Exposure.CostMicrounits) : state.LineageCharges.CostMicrounits,
                ActiveElapsedMilliseconds = AddUnknown(state.LineageCharges.ActiveElapsedMilliseconds, provider.Exposure.ElapsedMilliseconds),
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

    internal static bool FitsSettledBudget(
        CampaignLineageCharges charges,
        CampaignStateCampaignBudget budget) =>
        Within(charges.ProviderRequests.TotalCharged, budget.MaximumProviderRequests)
        && Within(charges.InputTokens.TotalCharged, budget.MaximumInputTokens)
        && Within(charges.UncachedInputTokens.TotalCharged, budget.MaximumUncachedInputTokens)
        && Within(charges.OutputTokens.TotalCharged, budget.MaximumOutputTokens)
        && FitsCost(charges, charges.CostMicrounits.TotalCharged, budget)
        && Within(charges.ActiveElapsedMilliseconds.TotalCharged, budget.MaximumElapsedMilliseconds);

    internal static bool FitsProviderBudget(
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

    internal static CampaignChargeObservation ChargeDispatchDimension(
        CampaignChargeObservation charge, long? observed, long exposure, long knownSubset = 0) =>
        AddObservation(charge, observed, Math.Max(exposure, knownSubset));

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
        long conservativeMaximum,
        bool complete = true)
    {
        if (observed is not { } exact) return AddUnknown(charge, conservativeMaximum);
        var known = AddExact(charge, exact);
        // A sum of present fields does not prove that every dispatched exchange reported this dimension.
        return complete ? known : AddUnknown(known, conservativeMaximum);
    }

    private static CampaignChargeObservation AddUnknown(CampaignChargeObservation charge, long value)
    {
        var conservative = checked(charge.ConservativeUnobserved + value);
        return new CampaignChargeObservation(
            charge.Observed,
            conservative,
            checked((charge.Observed ?? 0) + conservative));
    }
}
