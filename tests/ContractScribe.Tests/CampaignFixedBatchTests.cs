using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using ContractScribe.Core;

namespace ContractScribe.Tests;

public sealed partial class CampaignStateContractTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(12)]
    public void Zero_creation_quota_keeps_deferred_targets_without_unresolved_work(int targetCount)
    {
        var scenario = CreateProposalScenario(workItemCount: targetCount, targetLimit: 0);
        var initial = CampaignStateJson.CreateArtifact(scenario.InitialState);
        Assert.Equal(0, initial.State.Batch.CreationQuota);
        Assert.Empty(initial.State.Batch.SelectedTargetKeys);
        Assert.Equal(targetCount, initial.State.Batch.CompleteTargets.Length);
        Assert.All(initial.State.TargetProgress, item => Assert.Equal(CampaignTargetProgressKind.Deferred, item.Kind));
        Assert.Equal(new CampaignTerminalOutcome(CampaignTerminalKind.Complete, CampaignTerminalReason.AllWorkClosed),
            initial.State.TerminalOutcome);
        using var document = System.Text.Json.JsonDocument.Parse(initial.ExactUtf8Json.AsMemory());
        Assert.True(EvaluateCampaignSchema(document.RootElement).IsValid);
        var restored = CampaignStateJson.Parse(initial.ExactUtf8Json.AsMemory()).Artifact!;
        Assert.Equal(initial.ExactUtf8Json.ToArray(), restored.ExactUtf8Json.ToArray());
        AssertMutationFailure(initial.State, root => root["terminalOutcome"]!["reason"] = "unresolved",
            CampaignStateValidationCode.InvalidShape);
        foreach (var limit in new[] { 0, 1, 4096 })
        {
            var input = scenario.Input with { TargetLimit = new(limit) };
            CampaignStateFactory.ValidateCurrentContext(restored.State, scenario.ExecutionAuthority, "style.synthetic",
                scenario.StyleProjection, "samples/Synthetic.csproj", input, scenario.Plan);
            var allowance = CampaignStateFactory.CreateInvocationTargetAllowance(restored.State, new(limit));
            Assert.Empty(allowance.WorkItemKeys);
            var work = scenario.Plan.WorkItems[0];
            var rejected = CampaignStateReducer.AdmitProviderInvocation(restored, scenario.ExecutionAuthority,
                "style.synthetic", scenario.StyleProjection, input, scenario.Plan, work.WorkItemKey,
                CreateScribeExchange(work, scenario: scenario).Request, allowance, CampaignInvocationTestPolicy.Create(CreateScribeExchange(work, scenario: scenario).Request.Limits));
            Assert.Equal(CampaignTransitionKind.Rejected, rejected.Kind);
            Assert.Equal(initial.ExactUtf8Json.ToArray(), rejected.Artifact.ExactUtf8Json.ToArray());
            Assert.Equal(0, rejected.Artifact.State.LineageCharges.OuterInvocations);
            Assert.Null(rejected.Artifact.State.ActiveReservation);
        }
    }

    [Fact]
    public void Empty_selected_batch_still_preserves_excluded_unresolved_work()
    {
        var state = CreateCapacityState(excludedOwners: 1, deferredOwners: 0);
        Assert.Empty(state.Batch.SelectedTargetKeys);
        Assert.All(state.TargetProgress, item => Assert.Equal(CampaignTargetProgressKind.Excluded, item.Kind));
        Assert.Equal(CampaignTerminalReason.Unresolved, state.TerminalOutcome!.Reason);
        Assert.Equal(CampaignTerminalReason.Unresolved,
            CampaignStateJson.Parse(CampaignStateJson.Write(state)).Artifact!.State.TerminalOutcome!.Reason);
    }

    [Fact]
    public void Batch_grouping_is_independent_of_owner_input_permutation_and_current_culture()
    {
        var scenario = CreateProposalScenario(workItemCount: 107,
            containingType: index => index < 95 ? "T:Synthetic.First" : "T:Synthetic.Second");
        var originalCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            foreach (var culture in new[] { "en-US", "tr-TR" })
            {
                System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo(culture);
                var reordered = CampaignPlanner.Plan(scenario.Input with
                {
                    OwnerAuthority = new(scenario.Input.OwnerAuthority.Owners.Reverse().ToImmutableArray()),
                });
                Assert.Equal(scenario.Plan.ExecutionCommitment, reordered.ExecutionCommitment);
                Assert.Equal(scenario.Plan.Batch.Identity, reordered.Batch.Identity);
                Assert.True(scenario.Plan.Batch.CompleteTargets.SequenceEqual(reordered.Batch.CompleteTargets));
                Assert.True(scenario.Plan.Batch.SelectedTargetKeys.SequenceEqual(reordered.Batch.SelectedTargetKeys, StringComparer.Ordinal));
            }
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = originalCulture; }
    }

    [Fact]
    public void Whole_groups_stop_at_95_plus_11_without_skipping_a_later_small_group()
    {
        var scenario = CreateProposalScenario(workItemCount: 107,
            containingType: index => index < 95 ? "T:Synthetic.First" : index < 106 ? "T:Synthetic.Second" : "T:Synthetic.Third");
        Assert.Equal(107, scenario.Plan.WorkItems.Length);
        Assert.Equal(95, scenario.Plan.Batch.SelectedTargetKeys.Length);
        Assert.Equal(95, scenario.InitialState.TargetProgress.Count(item => item.Kind == CampaignTargetProgressKind.Pending));
        Assert.Equal(12, scenario.InitialState.TargetProgress.Count(item => item.Kind == CampaignTargetProgressKind.Deferred));
        Assert.Single(scenario.Plan.Batch.CompleteTargets.Where(target =>
            scenario.Plan.Batch.SelectedTargetKeys.Contains(target.TargetKey)).Select(target => target.GroupIdentity).Distinct());
    }

    [Fact]
    public void Oversized_group_splits_families_then_stable_chunks()
    {
        var whole = CreateProposalScenario(workItemCount: 230, compactPolicyContributions: true);
        Assert.Equal(230, whole.Plan.Batch.CompleteTargets.Length);
        Assert.Equal(100, whole.Plan.Batch.SelectedTargetKeys.Length);
        Assert.Equal(whole.Plan.Batch.CompleteTargets.Take(100).Select(target => target.TargetKey),
            whole.Plan.Batch.SelectedTargetKeys);
        var families = CreateProposalScenario(workItemCount: 230,
            memberFamily: index => index < 95 ? "family.first" : "family.second", compactPolicyContributions: true);
        Assert.Equal(95, families.Plan.Batch.SelectedTargetKeys.Length);
        Assert.Equal(230, families.Plan.Batch.CompleteTargets.Length);
    }

    [Fact]
    public void Invocation_limits_preserve_snapshot_plan_and_original_batch_and_admission_enforces_the_subset()
    {
        var scenario = CreateProposalScenario(workItemCount: 105);
        var lowerInput = scenario.Input with { TargetLimit = new(10) };
        var lowerPlan = CampaignPlanner.Plan(lowerInput);
        Assert.Equal(scenario.Plan.ExecutionCommitment, lowerPlan.ExecutionCommitment);
        Assert.Equal(scenario.Plan.WorkItems.Select(item => item.WorkItemKey), lowerPlan.WorkItems.Select(item => item.WorkItemKey));
        Assert.NotEqual(scenario.Plan.Batch.Identity, lowerPlan.Batch.Identity);
        var restored = CampaignStateJson.Parse(CampaignStateJson.Write(scenario.InitialState)).Artifact!.State;
        CampaignStateFactory.ValidateCurrentContext(restored, scenario.ExecutionAuthority, "style.synthetic",
            scenario.StyleProjection, "samples/Synthetic.csproj", lowerInput, scenario.Plan);
        var allowance = CampaignStateFactory.CreateInvocationTargetAllowance(restored, new(10));
        Assert.Equal(10, allowance.WorkItemKeys.Length);
        Assert.Equal(100, CampaignStateFactory.CreateInvocationTargetAllowance(restored, new(4096)).WorkItemKeys.Length);
        Assert.Equal(scenario.Plan.Batch.Identity, restored.Batch.Identity);
        var blocked = scenario.Plan.WorkItems.Single(item => item.WorkItemKey == restored.Batch.CompleteTargets[10].WorkItemKey);
        var rejected = CampaignStateReducer.AdmitProviderInvocation(CampaignStateJson.CreateArtifact(restored),
            scenario.ExecutionAuthority, "style.synthetic", scenario.StyleProjection, lowerInput, scenario.Plan,
            blocked.WorkItemKey, CreateScribeExchange(blocked, scenario: scenario).Request, allowance, CampaignInvocationTestPolicy.Create(CreateScribeExchange(blocked, scenario: scenario).Request.Limits));
        Assert.Equal(CampaignTransitionKind.Rejected, rejected.Kind);
        Assert.Equal(0, rejected.Artifact.State.LineageCharges.OuterInvocations);
        var work = scenario.Plan.WorkItems.Single(item => item.WorkItemKey == allowance.WorkItemKeys[0]);
        var admitted = CampaignStateReducer.AdmitProviderInvocation(CampaignStateJson.CreateArtifact(restored),
            scenario.ExecutionAuthority, "style.synthetic", scenario.StyleProjection, lowerInput, scenario.Plan,
            work.WorkItemKey, CreateScribeExchange(work, scenario: scenario).Request, allowance, CampaignInvocationTestPolicy.Create(CreateScribeExchange(work, scenario: scenario).Request.Limits));
        Assert.Equal(CampaignTransitionKind.Applied, admitted.Kind);
        var recovery = CampaignStateFactory.CreateInvocationTargetAllowance(admitted.Artifact.State, new(0));
        Assert.Empty(recovery.WorkItemKeys);
        Assert.Equal(work.WorkItemKey, recovery.RecoveryWorkItemKey);
        Assert.True(CampaignStateFactory.AllowsTarget(admitted.Artifact.State, work.WorkItemKey, recovery));
        Assert.False(CampaignStateFactory.AllowsTarget(admitted.Artifact.State, blocked.WorkItemKey, recovery));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public void Restored_retryable_target_reuses_its_distinct_slot_beside_the_current_new_target_subset(int limit)
    {
        var scenario = CreateProposalScenario(workItemCount: 12, costEnforced: false, maximumElapsedMilliseconds: 300_000);
        var work = scenario.Plan.WorkItems[0];
        var predecessor = CampaignStateJson.CreateArtifact(scenario.InitialState);
        var first = CreateScribeExchange(work, scenario: scenario);
        var admitted = CampaignStateReducer.AdmitProviderInvocation(predecessor, scenario.ExecutionAuthority,
            "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan, work.WorkItemKey, first.Request,
            CampaignStateFactory.CreateInvocationTargetAllowance(predecessor.State, new(1)), CampaignInvocationTestPolicy.Create(first.Request.Limits));
        Assert.Equal(CampaignTransitionKind.Applied, admitted.Kind);
        var attempt = Assert.IsType<CampaignProviderReservation>(admitted.Artifact.State.ActiveReservation).AttemptId;
        var failure = CreateScribeExchange(work, attemptId: attempt.Value, resultFixture: "retryable-failure-result.json", scenario: scenario);
        var invocation = CampaignStateReducer.CreateProviderInvocationAuthority(AcceptForTest(predecessor, admitted),
            scenario.ExecutionAuthority, "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan, failure.Request);
        Assert.True(invocation.BindInvocationAllowance(CampaignInvocationTestPolicy.Create(invocation.Request.Limits)));
        Assert.True(invocation.TryBeginDispatch(out _));
        var completion = OrdinaryCompletion(invocation,
            DocumentationScribeValidation.BindValidatedRunOutcome(failure.Request, attempt, failure.Result),
            failure.Result.RunEnvelope.ElapsedMilliseconds);
        var completed = CampaignStateReducer.CompleteProviderInvocation(completion.Invocation.AcceptedCheckpoint.Artifact, completion,
            scenario.ExecutionAuthority, "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan);
        Assert.Equal(CampaignTransitionKind.Applied, completed.Kind);
        var restored = CampaignStateJson.Parse(completed.Artifact.ExactUtf8Json.AsMemory()).Artifact!;
        var allowance = CampaignStateFactory.CreateInvocationTargetAllowance(restored.State, new(limit));
        Assert.Contains(work.WorkItemKey, allowance.WorkItemKeys);
        Assert.Equal(limit + 1, allowance.WorkItemKeys.Length);
        Assert.Equal(limit, restored.State.WorkItems.Count(item => item.OuterAttemptCount == 0
            && allowance.WorkItemKeys.Contains(item.WorkItemKey, StringComparer.Ordinal)));
        Assert.True(restored.State.Batch.SelectedTargetKeys.Select(key => restored.State.Batch.CompleteTargets.Single(target => target.TargetKey == key).WorkItemKey)
            .Where(key => allowance.WorkItemKeys.Contains(key, StringComparer.Ordinal)).SequenceEqual(allowance.WorkItemKeys));
        var blocked = scenario.Plan.WorkItems.First(item => !allowance.WorkItemKeys.Contains(item.WorkItemKey, StringComparer.Ordinal));
        var rejected = CampaignStateReducer.AdmitProviderInvocation(restored, scenario.ExecutionAuthority,
            "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan, blocked.WorkItemKey,
            CreateScribeExchange(blocked, scenario: scenario).Request, allowance, CampaignInvocationTestPolicy.Create(CreateScribeExchange(blocked, scenario: scenario).Request.Limits));
        Assert.Equal(CampaignTransitionKind.Rejected, rejected.Kind);
        var retry = CampaignStateReducer.RetryProviderInvocation(restored, null, scenario.ExecutionAuthority,
            "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan, work.WorkItemKey,
            CreateFreshContextExchange(work).Request, allowance, CampaignInvocationTestPolicy.Create(CreateFreshContextExchange(work).Request.Limits));
        Assert.Equal(CampaignTransitionKind.Applied, retry.Kind);
        Assert.Equal(work.WorkItemKey, Assert.IsType<CampaignProviderReservation>(retry.Artifact.State.ActiveReservation).WorkItemKey);
        Assert.Equal(2, retry.Artifact.State.WorkItems.Single(item => item.WorkItemKey == work.WorkItemKey).OuterAttemptCount);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("project")]
    [InlineData("instruction")]
    [InlineData("style")]
    public void Eligible_target_grouping_requires_complete_bound_authority(string mutation)
    {
        var scenario = CreateProposalScenario();
        var first = scenario.Input.OwnerAuthority.Owners[0];
        var target = first.Targets[0];
        var authority = mutation switch
        {
            "missing" => null,
            "project" => target.GroupingAuthority! with { ProjectIdentity = "project.foreign" },
            "instruction" => target.GroupingAuthority! with { InstructionsSha256 = null },
            _ => target.GroupingAuthority! with { StyleConfigurationSha256 = null },
        };
        var changed = scenario.Input with
        {
            OwnerAuthority = new(scenario.Input.OwnerAuthority.Owners.SetItem(0,
                first with { Targets = [target with { GroupingAuthority = authority }] })),
        };
        Assert.Throws<CampaignPlanningValidationException>(() => CampaignPlanner.Plan(changed));
    }

    [Theory]
    [InlineData("batch")]
    [InlineData("targetProgress")]
    [InlineData("attemptDisposition")]
    public void Current_v1_rejects_obsolete_checkpoint_shapes(string property)
    {
        var state = CreateProposalScenario().InitialState;
        var root = JsonNode.Parse(CampaignStateJson.Write(state))!.AsObject();
        if (property == "attemptDisposition") root["workItems"]![0]!.AsObject().Remove(property);
        else root.Remove(property);
        Assert.False(CampaignStateJson.Parse(Encoding.UTF8.GetBytes(root.ToJsonString() + "\n")).IsValid);
    }

    [Fact]
    public void Batch_manifest_and_visible_progress_reject_substitution_and_membership_growth()
    {
        var scenario = CreateProposalScenario(workItemCount: 105);
        AssertMutationFailure(scenario.InitialState, root => root["batch"]!["creationQuota"] = 101,
            CampaignStateValidationCode.InvalidCorrelation);
        AssertMutationFailure(scenario.InitialState, root => root["targetProgress"]![0]!["status"] = "accepted-proposal",
            CampaignStateValidationCode.InvalidCanonicalBytes);
        AssertMutationFailure(scenario.InitialState, root => root["targetProgress"]![0]!["symbolRef"]!["compilationContextRef"] = "INVALID",
            CampaignStateValidationCode.InvalidVocabulary);
        AssertMutationFailure(scenario.InitialState, root => root["targetProgress"]![0]!["symbolRef"]!["documentationCommentId"] = "M:Invalid\u0001",
            CampaignStateValidationCode.InvalidVocabulary);
        AssertMutationFailure(scenario.InitialState, root => root["batch"]!["selectedTargetKeys"]!.AsArray()
            .Add(root["targetProgress"]![100]!["targetKey"]!.GetValue<string>()), CampaignStateValidationCode.InvalidBound);
        var deferred = scenario.Plan.Batch.CompleteTargets[100];
        AssertInvalidCorrelation(() => CampaignStateFactory.CreateValidated(scenario.InitialState.ProductRevision,
            scenario.InitialState.CampaignLineage, scenario.InitialState.Snapshot, 0,
            scenario.InitialState.ConfiguredCeilings, scenario.InitialState.LineageCharges,
            scenario.InitialState.WorkItems.Select(work => work.WorkItemKey == deferred.WorkItemKey
                ? work with { OuterAttemptCount = 1 } : work), scenario.InitialState.Batch));
    }

    [Fact]
    public void Grouping_context_drift_invalidates_saved_authority_and_style_must_match_the_current_configuration()
    {
        var scenario = CreateProposalScenario();
        foreach (var styleChange in new[] { false, true })
        {
            var first = scenario.Input.OwnerAuthority.Owners[0];
            var target = first.Targets[0];
            var grouping = styleChange
                ? target.GroupingAuthority! with { StyleConfigurationSha256 = new('f', 64) }
                : target.GroupingAuthority! with { InstructionsSha256 = new('f', 64) };
            var changed = scenario.Input with
            {
                OwnerAuthority = new(scenario.Input.OwnerAuthority.Owners.SetItem(0,
                    first with { Targets = [target with { GroupingAuthority = grouping }] })),
            };
            var newPlan = CampaignPlanner.Plan(changed);
            Assert.NotEqual(scenario.Plan.ExecutionCommitment, newPlan.ExecutionCommitment);
            Assert.Equal(2, newPlan.Batch.CompleteTargets.Select(item => item.GroupIdentity).Distinct().Count());
            AssertInvalidCorrelation(() => CampaignStateFactory.ValidateCurrentContext(scenario.InitialState,
                scenario.ExecutionAuthority, "style.synthetic", scenario.StyleProjection, "samples/Synthetic.csproj", changed, newPlan));
            if (styleChange)
                AssertInvalidCorrelation(() => CampaignStateFactory.CreateInitial("style.synthetic", scenario.StyleProjection,
                    scenario.ExecutionAuthority, "samples/Synthetic.csproj", changed, newPlan));
        }
    }
}
