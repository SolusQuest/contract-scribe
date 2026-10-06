using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Agent.Providers;
using ContractScribe.Cli;
using ContractScribe.Core;

namespace ContractScribe.Tests;

public sealed partial class LayeredConfigurationTests
{
    [Theory]
    [InlineData("maximumProviderRequests")]
    [InlineData("maximumInputTokens")]
    [InlineData("maximumUncachedInputTokens")]
    [InlineData("maximumOutputTokens")]
    [InlineData("maximumCostMicrounits")]
    [InlineData("maximumElapsedMilliseconds")]
    public void Lifetime_caps_distinguish_default_null_inherited_finite_explicit_clear_and_zero(string field)
    {
        var defaults = Defaults();
        Assert.Equal(JsonValueKind.Null, Resolve(defaults).Document.ExactProjection
            .GetProperty("budgets").GetProperty("campaign").GetProperty(field).ValueKind);
        var finite = Layer("budgets", new JsonObject { ["campaign"] = new JsonObject { [field] = 128 } });
        var omitted = Layer("budgets", new JsonObject { ["campaign"] = new JsonObject() });
        var clear = Layer("budgets", new JsonObject { ["campaign"] = new JsonObject { [field] = null } });
        var zero = Layer("budgets", new JsonObject { ["campaign"] = new JsonObject { [field] = 0 } });
        Assert.Equal(128, Value(Resolve(defaults, finite, omitted)).GetInt32());
        Assert.Equal(JsonValueKind.Null, Value(Resolve(defaults, finite, clear)).ValueKind);
        Assert.Equal(0, Value(Resolve(defaults, finite, zero)).GetInt32());
        foreach (var malformed in new[] { "-1", "1.5", "\"unlimited\"", "1e100" })
        {
            var layer = Write("invalid-" + Guid.NewGuid().ToString("N") + ".json",
                "{\"consumerConfigurationVersion\":1,\"budgets\":{\"campaign\":{\"" + field + "\":" + malformed + "}}}");
            AssertInvalid(defaults, layer);
        }
        JsonElement Value(CampaignResolvedConfigurationSnapshot snapshot) => snapshot.Document.ExactProjection
            .GetProperty("budgets").GetProperty("campaign").GetProperty(field);
    }

    [Fact]
    public void Lifetime_elapsed_is_independent_of_the_finite_patch_deadline_and_mixed_input_caps()
    {
        var zero = Resolve(Defaults(), Layer("budgets", new JsonObject
        {
            ["campaign"] = new JsonObject { ["maximumElapsedMilliseconds"] = 0, ["maximumInputTokens"] = 0 },
        }));
        Assert.Equal(0, zero.Document.Budgets.Campaign.MaximumElapsedMilliseconds);
        Assert.Null(zero.Document.Budgets.Campaign.MaximumUncachedInputTokens);
        Assert.True(zero.Document.Planning.MaximumPatchElapsedMilliseconds > 0);
        Assert.True(CampaignM2ExecutionPolicy.TryCreate(JsonSerializer.SerializeToElement(new
        {
            m2ProjectionVersion = 1,
            maximumPatchElapsedMilliseconds = zero.Document.Planning.MaximumPatchElapsedMilliseconds,
        }), zero.Document.CreateExecutionPolicy(), out _));
        AssertInvalid(Defaults(), Layer("budgets", new JsonObject
        {
            ["scribe"] = new JsonObject { ["maximumProviderRequests"] = null },
        }));
    }
}

public sealed partial class DocumentationScribeProviderTransportTests
{
    [Theory]
    [InlineData(1_000_000L, 2_000_000L, 196_608L)]
    [InlineData(2_000_000L, 1_000_000L, 196_608L)]
    public void Cost_bound_covers_independent_partial_partitions_admitted_by_the_actual_codec(
        long cachedRate, long uncachedRate, long expected)
    {
        var prepared = OpenAiCompatibleChatCompletionsCodec.Prepare(Request([]), "model", RequiredContinuationProfile());
        var response = OpenAiCompatibleChatCompletionsCodec.ParseResponse(Encoding.UTF8.GetBytes(
            ThinkingToolResponseWithUsage("{\"prompt_cache_hit_tokens\":65536,\"prompt_cache_miss_tokens\":65536}")), prepared);
        Assert.Null(response.Cost);
        Assert.Null(response.Usage!.InputTokens);
        Assert.Equal(65_536, response.Usage.CachedInputTokens);
        Assert.Equal(65_536, response.Usage.UncachedInputTokens);
        var rates = new CampaignCostRates(cachedRate, uncachedRate, 0, 0);
        Assert.Equal(expected, rates.ConservativeCost(65_536, 65_536, 0, 1));
        Assert.True(rates.ConservativeCost(65_536, 65_536, 0, 1)
            >= (65_536L * cachedRate + 65_536L * uncachedRate) / 1_000_000);
    }
}

public sealed partial class CampaignStateContractTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(200_000L)]
    public void Lifetime_caps_change_canonical_spending_policy_without_changing_correctness(long? cap)
    {
        var scenario = UnlimitedScenario();
        var budget = scenario.Input.ExecutionPolicy.CampaignBudget with
        {
            MaximumProviderRequests = cap is null ? null : (int)cap.Value,
            MaximumInputTokens = cap,
            MaximumUncachedInputTokens = cap,
            MaximumOutputTokens = cap,
            MaximumCostMicrounits = cap,
            MaximumElapsedMilliseconds = cap,
        };
        var input = WithBudget(scenario, budget);
        var plan = CampaignPlanner.Plan(input);
        Assert.Equal(scenario.Plan.ExecutionCommitment, plan.ExecutionCommitment);
        Assert.Equal(scenario.Plan.Batch.Identity, plan.Batch.Identity);
        Assert.Equal(scenario.Plan.WorkItems.Select(work => work.WorkItemKey), plan.WorkItems.Select(work => work.WorkItemKey));
        var predecessor = CampaignStateJson.CreateArtifact(scenario.InitialState);
        var refreshed = RefreshCaps(predecessor, scenario, input);
        Assert.Equal(cap is null ? CampaignTransitionKind.Unchanged : CampaignTransitionKind.Applied, refreshed.Kind);
        var restored = CampaignStateJson.Parse(refreshed.Artifact.ExactUtf8Json.AsMemory()).Artifact!;
        Assert.Equal(predecessor.State.ConfiguredCeilings.CampaignConfigurationCommitmentSha256,
            restored.State.ConfiguredCeilings.CampaignConfigurationCommitmentSha256);
        CampaignStateFactory.ValidateCurrentContext(restored.State, scenario.ExecutionAuthority,
            "style.synthetic", scenario.StyleProjection, "samples/Synthetic.csproj", input, plan);
        Assert.Equal(predecessor.State.LineageCharges, restored.State.LineageCharges);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Success_crossing_lifetime_elapsed_retains_valid_results_and_cap_removal_restores_them(bool patch)
    {
        var scenario = CreateProposalScenario(targetLimit: 1);
        var initial = CampaignStateJson.CreateArtifact(scenario.InitialState);
        var (completed, exchange) = CompleteCapProposal(scenario, initial, patch ? 100 : 120_001);
        CampaignCheckpointArtifact successful;
        if (patch)
        {
            var request = CampaignStateFactory.ReconstructPatchRequest(completed.State,
                PatchContext(exchange.Request), CurrentEvidence(exchange));
            var reserved = CampaignStateReducer.ReservePatchInvocation(completed, request, 1_000);
            var invocation = CampaignStateReducer.CreatePatchInvocationAuthority(AcceptForTest(completed, reserved), request);
            Assert.True(invocation.TryBeginDispatch());
            var result = CampaignStateReducer.CompletePatchInvocation(reserved.Artifact, invocation, request,
                CreateAcceptedPatchResult(request, completed.State.WorkItems[0].TrustedProposal!), 120_001);
            Assert.Equal(CampaignTransitionKind.Applied, result.Kind);
            successful = result.Artifact;
            Assert.Equal(CampaignWorkStatus.Accepted, successful.State.WorkItems[0].Status);
            Assert.NotNull(successful.State.CandidateObservation);
            Assert.Equal(CampaignCumulativeOutcomeKind.Accepted, successful.State.CumulativeOutcome!.Kind);
            Assert.Empty(successful.State.KnownCompletedOperations);
        }
        else
        {
            successful = completed;
            Assert.Equal(CampaignWorkStatus.ProposalComplete, successful.State.WorkItems[0].Status);
        }
        Assert.NotNull(successful.State.WorkItems[0].TrustedProposal);
        Assert.Equal(CampaignTerminalReason.LifetimeCap, successful.State.TerminalOutcome!.Reason);
        var input = WithBudget(scenario, scenario.Input.ExecutionPolicy.CampaignBudget with { MaximumElapsedMilliseconds = null });
        var refreshed = RefreshCaps(successful, scenario, input);
        Assert.Equal(CampaignTransitionKind.Applied, refreshed.Kind);
        var restored = CampaignStateJson.Parse(refreshed.Artifact.ExactUtf8Json.AsMemory()).Artifact!;
        Assert.Equal(successful.State.LineageCharges, restored.State.LineageCharges);
        Assert.Equal(1, restored.State.WorkItems[0].OuterAttemptCount);
        Assert.Equal(successful.State.WorkItems[0].TrustedProposal!.ProposalCommitmentSha256,
            restored.State.WorkItems[0].TrustedProposal!.ProposalCommitmentSha256);
        Assert.NotEqual(CampaignTerminalReason.LifetimeCap, restored.State.TerminalOutcome?.Reason);
        Assert.Equal(successful.State.CandidateObservation?.AcceptedProjectionCommitmentSha256,
            restored.State.CandidateObservation?.AcceptedProjectionCommitmentSha256);
        if (patch)
        {
            Assert.Equal(successful.CheckpointRevision, restored.State.AcceptedCandidateOrigin!.CheckpointRevision);
            Assert.Equal(successful.Sha256, restored.State.AcceptedCandidateOrigin.CheckpointSha256);
        }
        else
        {
            Assert.Single(CampaignStateFactory.ReconstructPatchRequest(restored.State,
                PatchContext(exchange.Request), CurrentEvidence(exchange)).Blocks);
        }
        var repeat = RefreshCaps(restored, scenario, input);
        Assert.Equal(CampaignTransitionKind.Unchanged, repeat.Kind);
        Assert.Equal(restored.ExactUtf8Json, repeat.Artifact.ExactUtf8Json);
    }

    [Fact]
    public void Active_provider_cap_change_settles_old_exposure_retires_lease_and_retains_unknown_history()
    {
        var scenario = UnlimitedScenario();
        var initial = CampaignStateJson.CreateArtifact(scenario.InitialState);
        var exchange = CreateScribeExchange(scenario.Plan.WorkItems[0], scenario: scenario);
        var admitted = AdmitCapProvider(scenario, initial, exchange.Request);
        var accepted = AcceptForTest(initial, admitted);
        var oldInvocation = CampaignStateReducer.CreateProviderInvocationAuthority(accepted, scenario.ExecutionAuthority,
            "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan, exchange.Request);
        Assert.True(oldInvocation.BindInvocationAllowance(CampaignInvocationTestPolicy.Create(oldInvocation.Request.Limits)));
        var input = WithBudget(scenario, scenario.Input.ExecutionPolicy.CampaignBudget with { MaximumProviderRequests = 0 });
        var refreshed = CampaignStateReducer.RefreshLifetimeCaps(admitted.Artifact, accepted, scenario.ExecutionAuthority,
            "style.synthetic", scenario.StyleProjection, "samples/Synthetic.csproj", input, CampaignPlanner.Plan(input));
        Assert.Equal(CampaignTransitionKind.Applied, refreshed.Kind);
        Assert.False(oldInvocation.TryBeginDispatch(out _));
        Assert.Null(refreshed.Artifact.State.ActiveReservation);
        Assert.Null(refreshed.Artifact.State.TerminalOutcome);
        Assert.NotNull(refreshed.Artifact.State.WorkItems[0].PausedProviderAttempt);
        Assert.Equal(0, refreshed.Artifact.State.LineageCharges.ProviderRequests.TotalCharged);
        Assert.True(refreshed.Artifact.State.LineageCharges.ActiveElapsedMilliseconds.ConservativeUnobserved > 0);
        Assert.Equal(1, refreshed.Artifact.State.WorkItems[0].OuterAttemptCount);
        var cleared = RefreshCaps(refreshed.Artifact, scenario, scenario.Input);
        Assert.True(cleared.Kind == CampaignTransitionKind.Applied, "clear caps: " + cleared.Failure);
        Assert.Equal(refreshed.Artifact.State.LineageCharges, cleared.Artifact.State.LineageCharges);
        Assert.Null(cleared.Artifact.State.TerminalOutcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Configuration_source_revalidation_guards_cap_refresh_before_writes_and_active_lease_retirement(bool changed)
    {
        var scenario = UnlimitedScenario();
        var initial = CampaignStateJson.CreateArtifact(scenario.InitialState);
        var exchange = CreateScribeExchange(scenario.Plan.WorkItems[0], scenario: scenario);
        var admitted = AdmitCapProvider(scenario, initial, exchange.Request);
        var accepted = AcceptForTest(initial, admitted);
        var invocation = CampaignStateReducer.CreateProviderInvocationAuthority(accepted, scenario.ExecutionAuthority,
            "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan, exchange.Request);
        Assert.True(invocation.BindInvocationAllowance(CampaignInvocationTestPolicy.Create(invocation.Request.Limits)));
        var sourcePath = Path.Join(Path.GetTempPath(), "c3-admitted-source-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var bytes = File.ReadAllBytes(Path.Join(RepositoryRoot(), "tests", "fixtures", "campaign", "cli", "configuration-valid.json"));
            File.WriteAllBytes(sourcePath, bytes);
            var source = new CampaignConfigurationSource(sourcePath, bytes.Length,
                File.GetLastWriteTimeUtc(sourcePath), Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes);
            var snapshot = new CampaignResolvedConfigurationSnapshot([source], CampaignConfiguration.Parse(bytes));
            Assert.True(snapshot.Revalidate());
            if (changed) File.WriteAllText(sourcePath, "{\"consumerConfigurationVersion\":1,\"budgets\":{\"campaign\":{\"maximumProviderRequests\":null}}}");
            var input = WithBudget(scenario, scenario.Input.ExecutionPolicy.CampaignBudget with { MaximumProviderRequests = 0 });
            var store = new C3MemoryStore(admitted.Artifact);
            var result = await ChangedBaseCampaignReconciler.RefreshLifetimeCapsAsync(accepted, store,
                scenario.ExecutionAuthority, "style.synthetic", scenario.StyleProjection, "samples/Synthetic.csproj",
                input, CampaignPlanner.Plan(input), snapshot.Revalidate, CancellationToken.None);
            if (changed)
            {
                Assert.Equal(ChangedBaseCampaignReconciliationKind.InvalidConfiguration, result.Kind);
                Assert.Equal(0, store.Writes);
                Assert.Equal(admitted.Artifact.ExactUtf8Json, store.Artifact.ExactUtf8Json);
                Assert.True(invocation.TryBeginDispatch(out _));
            }
            else
            {
                Assert.Equal(ChangedBaseCampaignReconciliationKind.Accepted, result.Kind);
                Assert.Equal(1, store.Writes);
                Assert.Null(store.Artifact.State.TerminalOutcome);
                Assert.NotNull(store.Artifact.State.WorkItems[0].PausedProviderAttempt);
                Assert.Equal(0, store.Artifact.State.LineageCharges.ProviderRequests.TotalCharged);
                Assert.False(invocation.TryBeginDispatch(out _));
            }
        }
        finally { File.Delete(sourcePath); }
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    public void Positive_rate_cost_exposure_blocks_zero_and_insufficient_cap_even_with_scribe_cost_zero(long cap)
    {
        var request = ReadScribeRequest(root => root["limits"]!["maximumCostMicrounits"] = 0);
        var scenario = CreateProposalScenario(scribeRequestTemplate: request, maximumCostMicrounits: cap,
            costRates: new CampaignCostRates(1_000_000, 2_000_000, 1_000_000, 3_000_000));
        Assert.Equal(0, request.Limits.MaximumCostMicrounits);
        var initial = CampaignStateJson.CreateArtifact(scenario.InitialState);
        var exchange = CreateScribeExchange(scenario.Plan.WorkItems[0],
            requestMutation: root => root["limits"]!["maximumCostMicrounits"] = 0,
            resultMutation: root => root["runEnvelope"]!.AsObject().Remove("cost"), scenario: scenario);
        var admitted = AdmitCapProvider(scenario, initial, exchange.Request);
        Assert.Equal(CampaignTransitionKind.Applied, admitted.Kind);
        Assert.Null(admitted.Artifact.State.ActiveReservation);
        Assert.Equal(CampaignTerminalReason.LifetimeCap, admitted.Artifact.State.TerminalOutcome!.Reason);
        Assert.Equal(0, admitted.Artifact.State.LineageCharges.OuterInvocations);
    }

    [Fact]
    public void Unlimited_rate_cost_preserves_conservative_history_for_later_finite_caps()
    {
        var scenario = UnlimitedScenario(new CampaignCostRates(1_000_000, 2_000_000, 1_000_000, 3_000_000));
        var initial = CampaignStateJson.CreateArtifact(scenario.InitialState);
        var (completed, _) = CompleteCapProposal(scenario, initial, 100);
        var cost = completed.State.LineageCharges.CostMicrounits;
        Assert.True(cost.ConservativeUnobserved > 0);
        Assert.Equal(0, cost.Observed);
        var input = WithBudget(scenario, scenario.Input.ExecutionPolicy.CampaignBudget with { MaximumCostMicrounits = cost.TotalCharged - 1 });
        var lowered = RefreshCaps(completed, scenario, input);
        Assert.Equal(CampaignTerminalReason.LifetimeCap, lowered.Artifact.State.TerminalOutcome!.Reason);
        Assert.Equal(completed.State.LineageCharges, lowered.Artifact.State.LineageCharges);
        var enough = WithBudget(scenario, scenario.Input.ExecutionPolicy.CampaignBudget with { MaximumCostMicrounits = cost.TotalCharged });
        var raised = RefreshCaps(lowered.Artifact, scenario, enough);
        Assert.Null(raised.Artifact.State.TerminalOutcome);
        Assert.Equal(cost, raised.Artifact.State.LineageCharges.CostMicrounits);
    }

    [Fact]
    public void Unpriced_history_and_correctness_drift_never_authorize_new_finite_cost_as_zero()
    {
        var scenario = CreateProposalScenario(costEnforced: false, maximumProviderRequests: null,
            maximumInputTokens: null, maximumUncachedInputTokens: null, maximumOutputTokens: null,
            maximumElapsedMilliseconds: null, maximumCostMicrounits: null);
        var (completed, _) = CompleteCapProposal(scenario, CampaignStateJson.CreateArtifact(scenario.InitialState), 100);
        Assert.True(completed.State.LineageCharges.HasUnpricedCostHistory);
        var input = WithBudget(scenario, scenario.Input.ExecutionPolicy.CampaignBudget with { MaximumCostMicrounits = 0 });
        var blocked = RefreshCaps(completed, scenario, input);
        Assert.Equal(CampaignTerminalReason.LifetimeCap, blocked.Artifact.State.TerminalOutcome!.Reason);
        Assert.Equal(completed.State.LineageCharges, blocked.Artifact.State.LineageCharges);
        var drift = input with { Snapshot = input.Snapshot with { OpaqueSnapshotBinding = "snapshot.changed" } };
        Assert.Equal(CampaignTransitionKind.Rejected, RefreshCaps(completed, scenario, drift).Kind);
        Assert.Equal(CampaignTransitionKind.Unchanged, RefreshCaps(completed, scenario, scenario.Input).Kind);
    }

    [Theory]
    [InlineData("requests")]
    [InlineData("input")]
    [InlineData("uncached")]
    [InlineData("output")]
    [InlineData("cost")]
    [InlineData("elapsed")]
    public void Each_lifetime_admission_cap_accepts_exact_reservation_and_blocks_one_unit_below(string dimension)
    {
        var scenario = UnlimitedScenario(new CampaignCostRates(1_000_000, 2_000_000, 1_000_000, 3_000_000));
        var initial = CampaignStateJson.CreateArtifact(scenario.InitialState);
        var exchange = CreateScribeExchange(scenario.Plan.WorkItems[0], scenario: scenario);
        var allowance = CampaignInvocationTestPolicy.Create(exchange.Request.Limits, new C4CampaignClock());
        var exposure = CampaignBudgetAccounting.ProviderExposure(initial.State.ConfiguredCeilings.CampaignBudget, allowance.PreviewProviderExposure());
        var bound = dimension switch
        {
            "requests" => exposure.ProviderRequests,
            "input" => exposure.InputTokens,
            "uncached" => exposure.UncachedInputTokens,
            "output" => exposure.OutputTokens,
            "cost" => exposure.CostMicrounits,
            _ => exposure.ElapsedMilliseconds,
        };
        Assert.True(bound > 0);
        foreach (var cap in new[] { bound - 1, bound })
        {
            var budget = scenario.Input.ExecutionPolicy.CampaignBudget;
            budget = dimension switch
            {
                "requests" => budget with { MaximumProviderRequests = (int)cap },
                "input" => budget with { MaximumInputTokens = cap },
                "uncached" => budget with { MaximumUncachedInputTokens = cap },
                "output" => budget with { MaximumOutputTokens = cap },
                "cost" => budget with { MaximumCostMicrounits = cap },
                _ => budget with { MaximumElapsedMilliseconds = cap },
            };
            var input = WithBudget(scenario, budget);
            var refreshed = RefreshCaps(initial, scenario, input).Artifact;
            var admitted = CampaignStateReducer.AdmitProviderInvocation(refreshed, scenario.ExecutionAuthority,
                "style.synthetic", scenario.StyleProjection, input, CampaignPlanner.Plan(input),
                scenario.Plan.WorkItems[0].WorkItemKey, exchange.Request,
                CampaignStateFactory.CreateInvocationTargetAllowance(refreshed.State, new(100)), CampaignInvocationTestPolicy.Create(exchange.Request.Limits, new C4CampaignClock()));
            Assert.Equal(CampaignTransitionKind.Applied, admitted.Kind);
            if (cap == bound || dimension == "elapsed")
            {
                var host = Assert.IsType<CampaignProviderReservation>(admitted.Artifact.State.ActiveReservation);
                Assert.Equal(0, host.Exposure.ProviderRequests);
                if (dimension == "elapsed") Assert.Equal(cap, host.Exposure.ElapsedMilliseconds);
                Assert.Equal(1, admitted.Artifact.State.LineageCharges.OuterInvocations);
            }
            else
            {
                Assert.Null(admitted.Artifact.State.ActiveReservation);
                Assert.Equal(CampaignTerminalReason.LifetimeCap, admitted.Artifact.State.TerminalOutcome!.Reason);
                Assert.Equal(0, admitted.Artifact.State.LineageCharges.OuterInvocations);
            }
        }
    }

    [Fact]
    public void Active_patch_cap_change_settles_elapsed_once_and_retires_old_revision_authority()
    {
        var scenario = UnlimitedScenario();
        var (completed, exchange) = CompleteCapProposal(scenario, CampaignStateJson.CreateArtifact(scenario.InitialState), 100);
        var request = CampaignStateFactory.ReconstructPatchRequest(completed.State, PatchContext(exchange.Request), CurrentEvidence(exchange));
        var reserved = CampaignStateReducer.ReservePatchInvocation(completed, request, 1_000);
        var accepted = AcceptForTest(completed, reserved);
        var invocation = CampaignStateReducer.CreatePatchInvocationAuthority(accepted, request);
        var input = WithBudget(scenario, scenario.Input.ExecutionPolicy.CampaignBudget with { MaximumElapsedMilliseconds = 0 });
        var refresh = CampaignStateReducer.RefreshLifetimeCaps(reserved.Artifact, accepted, scenario.ExecutionAuthority,
            "style.synthetic", scenario.StyleProjection, "samples/Synthetic.csproj", input, CampaignPlanner.Plan(input));
        Assert.Equal(CampaignTransitionKind.Applied, refresh.Kind);
        Assert.False(invocation.TryBeginDispatch());
        Assert.Null(refresh.Artifact.State.ActiveReservation);
        Assert.Equal(CampaignTerminalReason.LifetimeCap, refresh.Artifact.State.TerminalOutcome!.Reason);
        Assert.Equal(1_000, refresh.Artifact.State.LineageCharges.ActiveElapsedMilliseconds.ConservativeUnobserved);
        Assert.Equal(1, refresh.Artifact.State.LineageCharges.PatchValidationInvocations);
        Assert.Equal(CampaignWorkStatus.ProposalComplete, refresh.Artifact.State.WorkItems[0].Status);
        var clear = RefreshCaps(refresh.Artifact, scenario, scenario.Input);
        Assert.Equal(CampaignTransitionKind.Applied, clear.Kind);
        Assert.Equal(refresh.Artifact.State.LineageCharges, clear.Artifact.State.LineageCharges);
        Assert.Null(clear.Artifact.State.TerminalOutcome);
        Assert.Single(CampaignStateFactory.ReconstructPatchRequest(clear.Artifact.State,
            PatchContext(exchange.Request), CurrentEvidence(exchange)).Blocks);
    }

    [Fact]
    public void Structural_budget_terminal_is_not_reopened_by_lifetime_cap_removal()
    {
        var scenario = CreateProposalScenario();
        var basis = scenario.InitialState;
        var state = CampaignStateFactory.CreateValidated(basis.ProductRevision, basis.CampaignLineage, basis.Snapshot,
            basis.CheckpointRevision, basis.ConfiguredCeilings, basis.LineageCharges, basis.WorkItems, basis.Batch,
            null, basis.CandidateObservation, basis.CumulativeOutcome, basis.KnownCompletedOperations,
            new(CampaignTerminalKind.Exhausted, CampaignTerminalReason.Budget), basis.Predecessor);
        var initial = CampaignStateJson.CreateArtifact(state);
        var input = WithBudget(scenario, scenario.Input.ExecutionPolicy.CampaignBudget with { MaximumElapsedMilliseconds = null });
        var result = RefreshCaps(initial, scenario, input);
        Assert.Equal(CampaignTransitionKind.Applied, result.Kind);
        Assert.Equal(state.TerminalOutcome, result.Artifact.State.TerminalOutcome);
        Assert.Equal(state.LineageCharges, result.Artifact.State.LineageCharges);
    }

    [Fact]
    public void Changed_base_with_new_caps_carries_priced_history_before_new_provider_admission()
    {
        var scenario = UnlimitedScenario(new CampaignCostRates(1_000_000, 2_000_000, 1_000_000, 3_000_000));
        var (completed, _) = CompleteCapProposal(scenario, CampaignStateJson.CreateArtifact(scenario.InitialState), 100);
        var input = WithBudget(scenario, scenario.Input.ExecutionPolicy.CampaignBudget with { MaximumCostMicrounits = 0 });
        input = input with { Snapshot = input.Snapshot with { OpaqueSnapshotBinding = "snapshot.next" } };
        var plan = CampaignPlanner.Plan(input);
        var template = CampaignStateJson.CreateArtifact(CampaignStateFactory.CreateSupersessionTemplate(
            "style.synthetic", scenario.StyleProjection, scenario.ExecutionAuthority, "samples/Synthetic.csproj", input, plan));
        var superseded = CampaignStateReducer.Supersede(completed, null,
            CampaignCheckpointAcceptance.CreateInitialAuthority(template), scenario.ExecutionAuthority,
            "style.synthetic", scenario.StyleProjection, "samples/Synthetic.csproj", input, plan);
        Assert.Equal(CampaignTransitionKind.Applied, superseded.Kind);
        Assert.Equal(completed.State.LineageCharges, superseded.Artifact.State.LineageCharges);
        Assert.Equal(0, superseded.Artifact.State.ConfiguredCeilings.CampaignBudget.MaximumCostMicrounits);
        var exchange = CreateScribeExchange(plan.WorkItems[0], scenario: scenario);
        var admission = CampaignStateReducer.AdmitProviderInvocation(superseded.Artifact, scenario.ExecutionAuthority,
            "style.synthetic", scenario.StyleProjection, input, plan, plan.WorkItems[0].WorkItemKey, exchange.Request,
            CampaignStateFactory.CreateInvocationTargetAllowance(superseded.Artifact.State, new(100)), CampaignInvocationTestPolicy.Create(exchange.Request.Limits));
        Assert.Equal(CampaignTransitionKind.Applied, admission.Kind);
        Assert.Null(admission.Artifact.State.ActiveReservation);
        Assert.Equal(CampaignTerminalReason.LifetimeCap, admission.Artifact.State.TerminalOutcome!.Reason);
        Assert.Equal(completed.State.LineageCharges, admission.Artifact.State.LineageCharges);
    }

    private static ProposalScenario UnlimitedScenario(CampaignCostRates? rates = null) =>
        CreateProposalScenario(targetLimit: 1, maximumProviderRequests: null,
            maximumInputTokens: null, maximumUncachedInputTokens: null, maximumOutputTokens: null,
            maximumCostMicrounits: null, maximumElapsedMilliseconds: null, costRates: rates);

    private static CampaignPlanningInput WithBudget(ProposalScenario scenario, CampaignPlanningBudgetPolicy budget) =>
        scenario.Input with { ExecutionPolicy = scenario.Input.ExecutionPolicy with { CampaignBudget = budget } };

    private static CampaignTransitionResult RefreshCaps(CampaignCheckpointArtifact artifact,
        ProposalScenario scenario, CampaignPlanningInput input) => CampaignStateReducer.RefreshLifetimeCaps(artifact,
            CampaignCheckpointAcceptance.AcceptCurrent(CampaignCheckpointReadResult.Found(artifact.ExactUtf8Json.AsSpan(),
                artifact.CheckpointRevision, artifact.Sha256)).AcceptedCheckpoint!, scenario.ExecutionAuthority,
            "style.synthetic", scenario.StyleProjection, "samples/Synthetic.csproj", input, CampaignPlanner.Plan(input));

    private sealed class C4CampaignClock : TimeProvider
    {
        public override long TimestampFrequency => 1_000;
        private long timestamp;
        public override long GetTimestamp() => timestamp;
        internal void Advance(int milliseconds) => timestamp += milliseconds;
    }

    private static CampaignTransitionResult AdmitCapProvider(ProposalScenario scenario,
        CampaignCheckpointArtifact artifact, DocumentationScribeRequest request) => CampaignStateReducer.AdmitProviderInvocation(
            artifact, scenario.ExecutionAuthority, "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan,
            scenario.Plan.WorkItems[0].WorkItemKey, request, CampaignStateFactory.CreateInvocationTargetAllowance(artifact.State, new(100)), CampaignInvocationTestPolicy.Create(request.Limits));

    private static (CampaignCheckpointArtifact Artifact, ScribeExchange Exchange) CompleteCapProposal(
        ProposalScenario scenario, CampaignCheckpointArtifact initial, long elapsed)
    {
        var exchange = CreateScribeExchange(scenario.Plan.WorkItems[0], scenario: scenario);
        var admitted = AdmitCapProvider(scenario, initial, exchange.Request);
        var attempt = Assert.IsType<CampaignProviderReservation>(admitted.Artifact.State.ActiveReservation).AttemptId;
        exchange = CreateScribeExchange(scenario.Plan.WorkItems[0], attemptId: attempt.Value,
            resultMutation: root => root["runEnvelope"]!.AsObject().Remove("cost"), scenario: scenario);
        var invocation = CampaignStateReducer.CreateProviderInvocationAuthority(AcceptForTest(initial, admitted),
            scenario.ExecutionAuthority, "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan, exchange.Request);
        Assert.True(invocation.BindInvocationAllowance(CampaignInvocationTestPolicy.Create(invocation.Request.Limits)));
        Assert.True(invocation.TryBeginDispatch(out _));
        var outcome = DocumentationScribeValidation.BindValidatedRunOutcome(exchange.Request, attempt, exchange.Result);
        var completion = OrdinaryCompletion(invocation, outcome, Math.Max(elapsed, exchange.Result.RunEnvelope.ElapsedMilliseconds));
        var completed = CampaignStateReducer.CompleteProviderInvocation(invocation.AcceptedCheckpoint.Artifact,
            completion, scenario.ExecutionAuthority,
            "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan);
        Assert.Equal(CampaignTransitionKind.Applied, completed.Kind);
        return (completed.Artifact, exchange);
    }

    private sealed class C3MemoryStore(CampaignCheckpointArtifact artifact) : ICampaignCheckpointStore
    {
        public CampaignCheckpointArtifact Artifact { get; private set; } = artifact;
        public int Writes { get; private set; }
        public ValueTask<CampaignCheckpointReadResult> ReadAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(CampaignCheckpointReadResult.Found(Artifact.ExactUtf8Json.AsSpan(), Artifact.CheckpointRevision, Artifact.Sha256));
        public ValueTask<CampaignCheckpointWriteResult> CreateIfAbsentAsync(ReadOnlyMemory<byte> bytes,
            long revision, string sha, CancellationToken token) => throw new InvalidOperationException();
        public ValueTask<CampaignCheckpointWriteResult> ReplaceIfCurrentAsync(long expectedRevision,
            string expectedSha, ReadOnlyMemory<byte> bytes, long revision, string sha, CancellationToken token)
        {
            Writes++;
            if (expectedRevision != Artifact.CheckpointRevision || expectedSha != Artifact.Sha256)
                return ValueTask.FromResult(new CampaignCheckpointWriteResult(CampaignCheckpointWriteKind.CurrentMismatch));
            Artifact = CampaignStateJson.Parse(bytes).Artifact!;
            return ValueTask.FromResult(new CampaignCheckpointWriteResult(CampaignCheckpointWriteKind.Written));
        }
    }
}
