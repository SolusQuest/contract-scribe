using ContractScribe.Core;

namespace ContractScribe.Tests;

public sealed class CampaignStateTransitionTests
{
    [Fact]
    public void Provider_admission_reserves_the_complete_persisted_run_bound()
    {
        var state = CreateOpenState();

        var decision = CampaignBudgetAccounting.ReserveProviderInvocation(state);

        Assert.Equal(CampaignBudgetDecisionKind.Admitted, decision.Kind);
        Assert.Equal(1, decision.Charges!.OuterInvocations);
        Assert.Equal(state.ConfiguredCeilings.ScribeRunLimits.MaximumProviderRequests,
            decision.Exposure!.ProviderRequests);
        Assert.Equal(state.ConfiguredCeilings.ScribeRunLimits.MaximumInputTokens,
            decision.Exposure.InputTokens);
        Assert.Equal(state.ConfiguredCeilings.ScribeRunLimits.MaximumUncachedInputTokens,
            decision.Exposure.UncachedInputTokens);
        Assert.Equal(state.ConfiguredCeilings.ScribeRunLimits.MaximumOutputTokens,
            decision.Exposure.OutputTokens);
        Assert.Equal(state.ConfiguredCeilings.ScribeRunLimits.MaximumElapsedMilliseconds,
            decision.Exposure.ElapsedMilliseconds);
        Assert.Equal(0, decision.Exposure.CostMicrounits);
        Assert.Equal(0, decision.Charges.ProviderRequests.TotalCharged);
    }

    [Fact]
    public void Cancellation_settles_unknown_provider_exposure_once_and_clears_authority()
    {
        var state = CreateOpenState();
        var budget = CampaignBudgetAccounting.ReserveProviderInvocation(state);
        Assert.True(DocumentationScribeAttemptId.TryParse(
            "scribe-attempt.0123456789abcdef0123456789abcdef",
            out var attemptId));
        var work = state.WorkItems[0] with { OuterAttemptCount = 1 };
        var reserved = CampaignStateFactory.CreateValidated(
            state.ProductRevision,
            state.CampaignLineage,
            state.Snapshot,
            state.CheckpointRevision,
            state.ConfiguredCeilings,
            budget.Charges!,
            [work],
            new CampaignProviderReservation(
                work.WorkItemKey,
                new string('a', 64),
                attemptId,
                budget.Exposure!));
        var predecessor = CampaignStateJson.CreateArtifact(reserved);

        var stopped = CampaignStateReducer.Stop(predecessor, CampaignTerminalKind.Cancelled);

        Assert.Equal(CampaignTransitionKind.Applied, stopped.Kind);
        Assert.Null(stopped.Artifact.State.ActiveReservation);
        Assert.Equal(predecessor.CheckpointRevision + 1, stopped.Artifact.CheckpointRevision);
        Assert.Equal(
            budget.Exposure!.ProviderRequests,
            stopped.Artifact.State.LineageCharges.ProviderRequests.ConservativeUnobserved);
        Assert.Equal(
            budget.Exposure.InputTokens,
            stopped.Artifact.State.LineageCharges.InputTokens.ConservativeUnobserved);
        Assert.Equal(
            budget.Exposure.ElapsedMilliseconds,
            stopped.Artifact.State.LineageCharges.ActiveElapsedMilliseconds.ConservativeUnobserved);
        Assert.Equal(1, stopped.Artifact.State.LineageCharges.OuterInvocations);

        var replay = CampaignStateReducer.Stop(stopped.Artifact, CampaignTerminalKind.Cancelled);
        Assert.Equal(CampaignTransitionKind.Unchanged, replay.Kind);
        Assert.Equal(
            stopped.Artifact.State.LineageCharges,
            replay.Artifact.State.LineageCharges);
    }

    [Fact]
    public void Exact_bound_admits_one_patch_call_and_one_over_fails_closed()
    {
        var state = CreateOpenState();
        var exact = CampaignBudgetAccounting.ReservePatchInvocation(
            state,
            state.ConfiguredCeilings.CampaignBudget.MaximumElapsedMilliseconds);
        var over = CampaignBudgetAccounting.ReservePatchInvocation(
            state,
            state.ConfiguredCeilings.CampaignBudget.MaximumElapsedMilliseconds + 1);

        Assert.Equal(CampaignBudgetDecisionKind.Admitted, exact.Kind);
        Assert.Equal(1, exact.Charges!.PatchValidationInvocations);
        Assert.Equal(CampaignBudgetDecisionKind.Exhausted, over.Kind);
        Assert.Null(over.Charges);
    }

    [Fact]
    public void Revision_overflow_is_bounded_and_byte_preserving()
    {
        var state = CreateOpenState();
        var max = CampaignStateFactory.CreateValidated(
            state.ProductRevision,
            state.CampaignLineage,
            state.Snapshot,
            CampaignStateContract.MaximumObservation,
            state.ConfiguredCeilings,
            state.LineageCharges,
            state.WorkItems);
        var predecessor = CampaignStateJson.CreateArtifact(max);

        var result = CampaignStateReducer.Stop(predecessor, CampaignTerminalKind.Timeout);

        Assert.Equal(CampaignTransitionKind.Rejected, result.Kind);
        Assert.Equal(CampaignTransitionFailure.RevisionOverflow, result.Failure);
        Assert.True(predecessor.ExactUtf8Json.AsSpan().SequenceEqual(
            result.Artifact.ExactUtf8Json.AsSpan()));
    }

    private static CampaignCheckpointState CreateOpenState()
    {
        var path = Path.GetFullPath(Path.Join(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "fixtures", "campaign", "state", "empty-terminal.json"));
        var parsed = CampaignStateJson.Parse(File.ReadAllBytes(path));
        var basis = Assert.IsType<CampaignCheckpointArtifact>(parsed.Artifact).State;
        var work = new CampaignWorkItemState(
            "campaign-work." + new string('a', 64),
            0,
            0,
            CampaignWorkStatus.Planned,
            null,
            null);
        return CampaignStateFactory.CreateValidated(
            basis.ProductRevision,
            basis.CampaignLineage,
            basis.Snapshot,
            basis.CheckpointRevision,
            basis.ConfiguredCeilings,
            basis.LineageCharges,
            [work]);
    }
}
