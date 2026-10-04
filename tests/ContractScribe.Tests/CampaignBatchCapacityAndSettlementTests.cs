using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Core;

namespace ContractScribe.Tests;

public sealed partial class CampaignStateContractTests
{
    [Fact]
    public void Large_complete_manifest_retains_excluded_and_deferred_targets_through_schema_parse_and_restore()
    {
        var state = CreateCapacityState(excludedOwners: 1024, deferredOwners: 1024);
        var artifact = CampaignStateJson.CreateArtifact(state);
        Assert.True(artifact.ExactUtf8Json.Length < CampaignStateContract.MaximumArtifactUtf8Bytes);
        Assert.Equal(2048, state.WorkItems.Length);
        Assert.Equal(5120, state.Batch.CompleteTargets.Length);
        Assert.Equal(4096, state.TargetProgress.Count(row => row.Kind == CampaignTargetProgressKind.Excluded));
        Assert.Equal(1023, state.TargetProgress.Count(row => row.Kind == CampaignTargetProgressKind.Deferred));
        using var document = JsonDocument.Parse(artifact.ExactUtf8Json.AsMemory());
        Assert.True(EvaluateCampaignSchema(document.RootElement).IsValid);
        var restored = CampaignStateJson.Parse(artifact.ExactUtf8Json.AsMemory());
        Assert.True(restored.IsValid, restored.FailureCode?.ToString());
        Assert.Equal(state.Batch.Identity, restored.Artifact!.State.Batch.Identity);
        Assert.True(state.Batch.CompleteTargets.SequenceEqual(restored.Artifact.State.Batch.CompleteTargets));
        Assert.True(state.TargetProgress.SequenceEqual(restored.Artifact.State.TargetProgress));
        Assert.Equal(artifact.ExactUtf8Json, CampaignStateJson.Write(restored.Artifact.State));
        Assert.Empty(CampaignStateFactory.CreateInvocationTargetAllowance(restored.Artifact.State, new(0)).WorkItemKeys);
        Assert.Single(CampaignStateFactory.CreateInvocationTargetAllowance(restored.Artifact.State, new(4096)).WorkItemKeys);
    }

    [Theory]
    [InlineData(4096, false)]
    [InlineData(1024, true)]
    public void Complete_manifest_count_and_valid_large_binding_fields_do_not_waive_canonical_byte_admission(
        int owners, bool largeBindings)
    {
        var exception = Assert.Throws<CampaignStateValidationException>(() =>
            CreateCapacityState(owners, 0, largeBindings));
        Assert.Equal(CampaignStateValidationCode.DocumentTooLarge, exception.Code);
        Assert.Equal("Campaign checkpoint exceeds its byte bound.", exception.Message);
        Assert.Equal(4_194_304, CampaignStateContract.MaximumArtifactUtf8Bytes);
        Assert.Equal(16_384, CampaignStateContract.MaximumCompleteTargets);
    }

    [Theory]
    [InlineData("none", CampaignTerminalKind.Complete)]
    [InlineData("requests", CampaignTerminalKind.Complete)]
    [InlineData("input", CampaignTerminalKind.Complete)]
    [InlineData("uncached", CampaignTerminalKind.Complete)]
    [InlineData("output", CampaignTerminalKind.Complete)]
    [InlineData("cost", CampaignTerminalKind.Complete)]
    [InlineData("elapsed-equal", CampaignTerminalKind.Complete)]
    [InlineData("elapsed-overrun", CampaignTerminalKind.Exhausted)]
    public void Last_retry_suppression_preserves_authoritative_settlement_and_exact_budget_boundary(
        string dimension, CampaignTerminalKind expected)
    {
        var template = ReadScribeRequest();
        var scenario = CreateProposalScenario(targetLimit: 1, costCurrency: "currency.usd",
            maximumAttemptsPerTarget: 1,
            maximumProviderRequests: dimension == "requests" ? template.Limits.MaximumProviderRequests : 100,
            maximumInputTokens: dimension == "input" ? 2L * template.Limits.MaximumInputTokens : 1_000_000,
            maximumUncachedInputTokens: dimension == "uncached" ? 2L * template.Limits.MaximumUncachedInputTokens
                : dimension == "input" ? template.Limits.MaximumUncachedInputTokens : 500_000,
            maximumOutputTokens: dimension == "output" ? 2L * template.Limits.MaximumOutputTokens : 100_000,
            maximumCostMicrounits: dimension == "cost" ? 2 * template.Limits.MaximumCostMicrounits : 5_000_000,
            maximumElapsedMilliseconds: dimension.StartsWith("elapsed", StringComparison.Ordinal)
                ? template.Limits.MaximumElapsedMilliseconds : 300_000);
        var work = scenario.Plan.WorkItems[0];
        var predecessor = CampaignStateJson.CreateArtifact(scenario.InitialState);
        var admitted = CampaignStateReducer.AdmitProviderInvocation(predecessor, scenario.ExecutionAuthority,
            "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan, work.WorkItemKey,
            CreateScribeExchange(work).Request, CampaignStateFactory.CreateInvocationTargetAllowance(predecessor.State, new(1)));
        Assert.Equal(CampaignTransitionKind.Applied, admitted.Kind);
        var boundedCompletionBytes = CampaignStateReducer.ValidateProviderSettlementCapacity(admitted.Artifact.State);
        var attempt = Assert.IsType<CampaignProviderReservation>(admitted.Artifact.State.ActiveReservation).AttemptId;
        var failure = CreateScribeExchange(work, attemptId: attempt.Value, resultFixture: "retryable-failure-result.json",
            resultMutation: root =>
            {
                if (dimension == "requests") root["runEnvelope"]!["providerRequestCount"] = template.Limits.MaximumProviderRequests;
                var field = dimension switch { "input" => "inputTokens", "uncached" => "uncachedInputTokens", "output" => "outputTokens", _ => null };
                if (field is not null)
                {
                    var value = dimension switch { "input" => template.Limits.MaximumInputTokens, "uncached" => template.Limits.MaximumUncachedInputTokens, _ => template.Limits.MaximumOutputTokens };
                    root["runEnvelope"]!["usage"] = new JsonObject { [field] = value };
                }
                if (dimension == "cost") root["runEnvelope"]!["cost"] = new JsonObject
                {
                    ["currencyId"] = "currency.usd",
                    ["amountMicrounits"] = template.Limits.MaximumCostMicrounits,
                };
            });
        var invocation = CampaignStateReducer.CreateProviderInvocationAuthority(AcceptForTest(predecessor, admitted),
            scenario.ExecutionAuthority, "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan, failure.Request);
        Assert.True(invocation.TryBeginDispatch(out _));
        var elapsed = dimension.StartsWith("elapsed", StringComparison.Ordinal)
            ? template.Limits.MaximumElapsedMilliseconds + (dimension == "elapsed-overrun" ? 1 : 0)
            : failure.Result.RunEnvelope.ElapsedMilliseconds;
        var outcome = DocumentationScribeValidation.BindValidatedRunOutcome(failure.Request, attempt, failure.Result);
        var settlement = CampaignBudgetAccounting.SettleProviderInvocation(admitted.Artifact.State, outcome, elapsed);
        Assert.Equal(expected == CampaignTerminalKind.Exhausted ? CampaignBudgetDecisionKind.Exhausted : CampaignBudgetDecisionKind.Admitted, settlement.Kind);
        if (dimension is "input" or "uncached" or "output" or "cost")
        {
            var charge = dimension switch
            {
                "input" => settlement.Charges!.InputTokens,
                "uncached" => settlement.Charges!.UncachedInputTokens,
                "output" => settlement.Charges!.OutputTokens,
                _ => settlement.Charges!.CostMicrounits,
            };
            // Exact equality includes both the observed aggregate and independent unknown exposure.
            Assert.Equal(charge.Observed, charge.ConservativeUnobserved);
            Assert.Equal(2 * charge.Observed, charge.TotalCharged);
        }
        var completed = CampaignStateReducer.CompleteProviderInvocation(admitted.Artifact,
            OrdinaryCompletion(invocation, outcome, elapsed), scenario.ExecutionAuthority,
            "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan);
        Assert.Equal(CampaignTransitionKind.Applied, completed.Kind);
        Assert.True(completed.Artifact.ExactUtf8Json.Length <= boundedCompletionBytes);
        var restored = CampaignStateJson.Parse(completed.Artifact.ExactUtf8Json.AsMemory()).Artifact!.State;
        Assert.Equal(settlement.Charges, restored.LineageCharges);
        Assert.Null(restored.ActiveReservation);
        Assert.Equal(expected, restored.TerminalOutcome!.Kind);
        Assert.Equal(expected == CampaignTerminalKind.Exhausted ? CampaignTerminalReason.LifetimeCap : CampaignTerminalReason.Unresolved, restored.TerminalOutcome.Reason);
        Assert.Equal(CampaignAttemptDisposition.SuppressedAtAttemptLimit, restored.WorkItems[0].AttemptDisposition);
        Assert.Equal(CampaignTargetProgressKind.Suppressed, restored.TargetProgress[0].Kind);
        Assert.Empty(CampaignStateFactory.CreateInvocationTargetAllowance(restored, new(4096)).WorkItemKeys);
    }

    [Theory]
    [InlineData((int)CampaignProviderCompletionKind.ProposalInvalid, CampaignTerminalKind.Exhausted)]
    [InlineData((int)CampaignProviderCompletionKind.HostFailure, CampaignTerminalKind.Failed)]
    [InlineData((int)CampaignProviderCompletionKind.ShutdownCancelled, CampaignTerminalKind.Failed)]
    [InlineData((int)CampaignProviderCompletionKind.CallerCancelled, CampaignTerminalKind.Cancelled)]
    [InlineData((int)CampaignProviderCompletionKind.Timeout, CampaignTerminalKind.Timeout)]
    [InlineData((int)CampaignProviderCompletionKind.BudgetExhausted, CampaignTerminalKind.Exhausted)]
    public void Postflight_rejected_proposal_settlement_preserves_explicit_stops_after_restore(
        int completionKind, CampaignTerminalKind expected)
    {
        var kind = (CampaignProviderCompletionKind)completionKind;
        var scenario = CreateProposalScenario(targetLimit: 1, maximumAttemptsPerTarget: 1, costCurrency: "currency.usd");
        var work = scenario.Plan.WorkItems[0];
        var predecessor = CampaignStateJson.CreateArtifact(scenario.InitialState);
        var exchange = CreateScribeExchange(work);
        var admitted = CampaignStateReducer.AdmitProviderInvocation(predecessor, scenario.ExecutionAuthority,
            "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan, work.WorkItemKey,
            exchange.Request, CampaignStateFactory.CreateInvocationTargetAllowance(predecessor.State, new(1)));
        var boundedCompletionBytes = CampaignStateReducer.ValidateProviderSettlementCapacity(admitted.Artifact.State);
        var attempt = Assert.IsType<CampaignProviderReservation>(admitted.Artifact.State.ActiveReservation).AttemptId;
        exchange = CreateScribeExchange(work, attemptId: attempt.Value);
        var outcome = DocumentationScribeValidation.BindValidatedRunOutcome(exchange.Request, attempt, exchange.Result);
        var invocation = CampaignStateReducer.CreateProviderInvocationAuthority(AcceptForTest(predecessor, admitted),
            scenario.ExecutionAuthority, "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan, exchange.Request);
        Assert.True(invocation.TryBeginDispatch(out _));
        var elapsed = scenario.InitialState.ConfiguredCeilings.CampaignBudget.MaximumElapsedMilliseconds + 1;
        Assert.Equal(CampaignBudgetDecisionKind.Exhausted,
            CampaignBudgetAccounting.SettleProviderInvocation(admitted.Artifact.State, outcome, elapsed).Kind);
        var registrar = Assert.IsType<CampaignProviderCompletionRegistrar>(invocation.TryCreateCompletionRegistrar());
        Assert.True(registrar.TryRegister(kind, outcome, elapsed, out var completion));
        var completed = CampaignStateReducer.CompleteProviderInvocation(admitted.Artifact, completion!, scenario.ExecutionAuthority,
            "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan);
        Assert.Equal(CampaignTransitionKind.Applied, completed.Kind);
        Assert.True(completed.Artifact.ExactUtf8Json.Length <= boundedCompletionBytes);
        var restored = CampaignStateJson.Parse(completed.Artifact.ExactUtf8Json.AsMemory()).Artifact!.State;
        var reason = expected switch
        {
            CampaignTerminalKind.Failed => CampaignTerminalReason.Host,
            CampaignTerminalKind.Cancelled => CampaignTerminalReason.Caller,
            CampaignTerminalKind.Timeout => CampaignTerminalReason.Deadline,
            _ => kind == CampaignProviderCompletionKind.ProposalInvalid
                ? CampaignTerminalReason.LifetimeCap : CampaignTerminalReason.Budget
        };
        Assert.Equal(new CampaignTerminalOutcome(expected, reason), restored.TerminalOutcome);
        if (kind == CampaignProviderCompletionKind.ProposalInvalid)
            Assert.Equal(CampaignWorkOutcomeCode.ValidationFailure, restored.WorkItems[0].ClosedOutcome!.Code);
        Assert.Null(restored.WorkItems[0].TrustedProposal);
        Assert.Null(restored.ActiveReservation);
        Assert.Equal(elapsed, restored.LineageCharges.ActiveElapsedMilliseconds.TotalCharged);
    }

    private static CampaignCheckpointState CreateCapacityState(int excludedOwners, int deferredOwners, bool largeBindings = false)
    {
        var basis = CreateState();
        var works = ImmutableArray.CreateBuilder<CampaignWorkItemState>();
        var targets = ImmutableArray.CreateBuilder<CampaignBatchTarget>();
        for (var owner = 0; owner < excludedOwners + deferredOwners; owner++)
        {
            var key = "campaign-work." + owner.ToString("x64", System.Globalization.CultureInfo.InvariantCulture);
            var excluded = owner < excludedOwners;
            works.Add(new(key, 0, 0, excluded ? CampaignWorkStatus.Closed : CampaignWorkStatus.Planned, null,
                excluded ? new(CampaignWorkOutcomeStage.Planning, CampaignWorkOutcomeCode.PlanningTerminal,
                    null, null, null, null, null, null, key) : null));
            for (var member = 0; member < (excluded ? 4 : 1); member++)
            {
                var suffix = owner + "_" + member;
                var symbol = new SymbolRef(largeBindings ? new('c', 128) : "context.synthetic",
                    (excluded ? "F:" : "M:") + "Synthetic.Widget." + (largeBindings ? new string('\u754c', 900) : "") + "F" + suffix);
                targets.Add(new(CampaignPlanner.ComputeTargetKey(basis.Snapshot.ExecutionCommitmentSha256, key, symbol),
                    key, symbol, !excluded, !excluded, excluded ? null : Hash('a'), excluded ? null : Hash('b')));
            }
        }
        var selected = targets.Where(target => target.BatchEligible).Take(1).Select(target => target.TargetKey).ToImmutableArray();
        var batch = new CampaignFixedBatch(basis.Snapshot.ExecutionCommitmentSha256, 1, targets.ToImmutable(), selected);
        return CampaignStateFactory.CreateValidated(basis.ProductRevision, basis.CampaignLineage, basis.Snapshot,
            0, basis.ConfiguredCeilings, basis.LineageCharges, works, batch,
            terminalOutcome: deferredOwners == 0 ? new(CampaignTerminalKind.Complete, CampaignTerminalReason.Unresolved) : null);
    }
}
