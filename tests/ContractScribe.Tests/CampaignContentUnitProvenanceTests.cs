using System.Collections.Immutable;
using ContractScribe.Core;

namespace ContractScribe.Tests;

public sealed partial class CampaignStateContractTests
{
    [Fact]
    public void Fresh_request_reconstruction_preserves_each_original_field_claim_and_exact_evidence_association()
    {
        var scenario = CreateProposalScenario(costCurrency: "currency.usd");
        var work = scenario.Plan.WorkItems[0];
        var exchange = CreateScribeExchange(work, scenario: scenario);
        var initial = CampaignStateJson.CreateArtifact(scenario.InitialState);
        var admitted = CampaignStateReducer.AdmitProviderInvocation(initial, scenario.ExecutionAuthority,
            "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan, work.WorkItemKey, exchange.Request,
            CampaignStateFactory.CreateInvocationTargetAllowance(initial.State, new(100)), CampaignInvocationTestPolicy.Create(exchange.Request.Limits));
        var invocation = CampaignStateReducer.CreateProviderInvocationAuthority(AcceptForTest(initial, admitted),
            scenario.ExecutionAuthority, "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan, exchange.Request);
        Assert.True(invocation.BindInvocationAllowance(CampaignInvocationTestPolicy.Create(exchange.Request.Limits)));
        Assert.True(invocation.TryBeginDispatch(out _));
        var outcome = DocumentationScribeValidation.BindValidatedRunOutcome(exchange.Request, invocation.AttemptId, exchange.Result);
        var completion = OrdinaryCompletion(invocation, outcome, exchange.Result.RunEnvelope.ElapsedMilliseconds);
        var completed = CampaignStateReducer.CompleteProviderInvocation(invocation.AcceptedCheckpoint.Artifact, completion,
            scenario.ExecutionAuthority, "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan);
        Assert.Equal(CampaignTransitionKind.Applied, completed.Kind);
        var parsed = CampaignStateJson.Parse(completed.Artifact.ExactUtf8Json.AsMemory());
        Assert.True(parsed.IsValid, parsed.FailureCode?.ToString());
        var state = parsed.Artifact!.State;
        var proposal = state.WorkItems.Single(item => item.WorkItemKey == work.WorkItemKey).TrustedProposal!;
        Assert.Equal(3, proposal.ContentUnitProvenance.Length);
        Assert.Equal(2, proposal.ContentUnitProvenance.Select(unit => unit.ClaimCategoryId).Distinct().Count());
        Assert.Equal(3, proposal.ContentUnitProvenance.SelectMany(unit => unit.EvidenceReferenceIds).Distinct().Count());
        var fresh = CreateFreshContextExchange(work, scenario: scenario);
        Assert.NotEqual(exchange.Request.ArtifactSha256, fresh.Request.ArtifactSha256);
        var restored = CampaignStateFactory.RestoreTrustedSubmission(state, scenario.ExecutionAuthority,
            "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan, fresh.Request,
            fresh.Request.ExecutableTargetIds.Single(), CurrentEvidence(fresh));
        var original = Assert.IsType<DocumentationScribeProposalTerminal>(Assert.Single(exchange.Result.Submissions).Result);
        var rebound = Assert.IsType<DocumentationScribeProposalTerminal>(restored.Result);
        Assert.Equal(fresh.Request.Context.RepositoryContextRef, rebound.Target.RepositoryContextRef);
        Assert.NotEqual(original.Target.RepositoryContextRef, rebound.Target.RepositoryContextRef);
        Assert.Equal(original.ContentUnits.Select(unit => unit.ClaimCategoryId), rebound.ContentUnits.Select(unit => unit.ClaimCategoryId));
        foreach (var pair in original.ContentUnits.Zip(rebound.ContentUnits))
        {
            Assert.Equal(pair.First.Kind, pair.Second.Kind);
            Assert.Equal(pair.First.Lines.ToArray(), pair.Second.Lines.ToArray());
            Assert.Equal(pair.First.EvidenceReferenceIds.ToArray(), pair.Second.EvidenceReferenceIds.ToArray());
        }
        Assert.Equal(completed.Artifact.State.LineageCharges, state.LineageCharges);
        var withoutParameterEvidence = CurrentEvidence(fresh).Where(reference => !reference.EvidenceReferenceId.StartsWith("evidence.parameter.", StringComparison.Ordinal)).ToImmutableArray();
        Assert.Throws<CampaignStateValidationException>(() => CampaignStateFactory.RestoreTrustedSubmission(state, scenario.ExecutionAuthority,
            "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan, fresh.Request,
            fresh.Request.ExecutableTargetIds.Single(), withoutParameterEvidence));
        var changed = proposal with
        {
            ContentUnitProvenance = proposal.ContentUnitProvenance.SetItem(0,
                proposal.ContentUnitProvenance[0] with { EvidenceReferenceIds = proposal.ContentUnitProvenance[1].EvidenceReferenceIds }),
        };
        Assert.Throws<CampaignStateValidationException>(() => CampaignStateFactory.CreateValidated(state.ProductRevision, state.CampaignLineage,
            state.Snapshot, state.CheckpointRevision, state.ConfiguredCeilings, state.LineageCharges,
            state.WorkItems.Select(item => item.WorkItemKey == work.WorkItemKey ? item with { TrustedProposal = changed } : item),
            state.Batch, dispatchObservations: state.DispatchObservations, scribeProgress: state.ScribeProgress));
    }
}
