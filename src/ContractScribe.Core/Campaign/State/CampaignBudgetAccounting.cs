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
                budget.CostEnforced ? limits.MaximumCostMicrounits : 0,
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
            || activeElapsedMilliseconds > reservation.Exposure.ElapsedMilliseconds)
        {
            return new CampaignSettlementDecision(CampaignBudgetDecisionKind.Invalid, null);
        }

        var envelope = outcome.RunResult.RunEnvelope;
        var budget = state.ConfiguredCeilings.CampaignBudget;
        if (envelope.ProviderRequestCount > reservation.Exposure.ProviderRequests
            || envelope.Usage?.InputTokens > reservation.Exposure.InputTokens
            || envelope.Usage?.UncachedInputTokens > reservation.Exposure.UncachedInputTokens
            || envelope.Usage?.OutputTokens > reservation.Exposure.OutputTokens
            || envelope.Cost?.AmountMicrounits > reservation.Exposure.CostMicrounits
            || envelope.ElapsedMilliseconds > reservation.Exposure.ElapsedMilliseconds
            || activeElapsedMilliseconds is { } hostElapsed
                && hostElapsed < envelope.ElapsedMilliseconds
            || budget.CostEnforced && envelope.Cost is not null
                && !string.Equals(envelope.Cost.CurrencyId, budget.CostCurrency, StringComparison.Ordinal)
            || !budget.CostEnforced && envelope.Cost is not null)
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
                CostMicrounits = budget.CostEnforced
                    ? AddObservation(
                        state.LineageCharges.CostMicrounits,
                        envelope.Cost?.AmountMicrounits,
                        reservation.Exposure.CostMicrounits)
                    : state.LineageCharges.CostMicrounits,
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
        if (elapsedMilliseconds < 0
            || elapsedMilliseconds > CampaignStateContract.MaximumObservation)
        {
            return new CampaignSettlementDecision(CampaignBudgetDecisionKind.Invalid, null);
        }

        try
        {
            var charges = state.LineageCharges with
            {
                PatchValidationInvocations = checked(state.LineageCharges.PatchValidationInvocations + 1),
            };
            var budget = state.ConfiguredCeilings.CampaignBudget;
            return charges.ActiveElapsedMilliseconds.TotalCharged + elapsedMilliseconds <= budget.MaximumElapsedMilliseconds
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
            || activeElapsedMilliseconds > reservation.ElapsedMilliseconds)
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

    internal static bool FitsSettledBudget(
        CampaignLineageCharges charges,
        CampaignStateCampaignBudget budget) =>
        charges.ProviderRequests.TotalCharged <= budget.MaximumProviderRequests
        && charges.InputTokens.TotalCharged <= budget.MaximumInputTokens
        && charges.UncachedInputTokens.TotalCharged <= budget.MaximumUncachedInputTokens
        && charges.OutputTokens.TotalCharged <= budget.MaximumOutputTokens
        && (!budget.CostEnforced || charges.CostMicrounits.TotalCharged <= budget.MaximumCostMicrounits)
        && charges.ActiveElapsedMilliseconds.TotalCharged <= budget.MaximumElapsedMilliseconds;

    private static bool FitsProviderBudget(
        CampaignLineageCharges charges,
        CampaignProviderReservationExposure exposure,
        CampaignStateCampaignBudget budget) =>
        charges.ProviderRequests.TotalCharged + exposure.ProviderRequests <= budget.MaximumProviderRequests
        && charges.InputTokens.TotalCharged + exposure.InputTokens <= budget.MaximumInputTokens
        && charges.UncachedInputTokens.TotalCharged + exposure.UncachedInputTokens <= budget.MaximumUncachedInputTokens
        && charges.OutputTokens.TotalCharged + exposure.OutputTokens <= budget.MaximumOutputTokens
        && (!budget.CostEnforced
            || charges.CostMicrounits.TotalCharged + exposure.CostMicrounits <= budget.MaximumCostMicrounits)
        && charges.ActiveElapsedMilliseconds.TotalCharged + exposure.ElapsedMilliseconds <= budget.MaximumElapsedMilliseconds;

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
