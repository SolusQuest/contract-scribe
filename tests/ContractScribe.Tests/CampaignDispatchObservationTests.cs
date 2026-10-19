using System.Text.Json.Nodes;
using ContractScribe.Cli;
using ContractScribe.Core;

namespace ContractScribe.Tests;

public sealed partial class CampaignStateContractTests
{
    [Theory]
    [InlineData("providerRequests")]
    [InlineData("inputTokens")]
    [InlineData("cachedInputTokens")]
    [InlineData("uncachedInputTokens")]
    [InlineData("outputTokens")]
    [InlineData("reasoningTokens")]
    [InlineData("costMicrounits")]
    [InlineData("activeElapsedMilliseconds")]
    public async Task Rehashed_dispatch_charges_must_match_settlement_and_provider_lineage(string dimension)
    {
        var artifact = await RecordDispatchHistoryAsync(false);
        // Change both row and lineage so a sum-only check cannot detect the defect.
        AssertInvalidDispatchCharge(artifact, dimension, changeRow: true);
        if (dimension != "activeElapsedMilliseconds")
            AssertInvalidDispatchCharge(artifact, dimension, changeRow: false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispatch_history_reconstructs_priced_and_unpriced_actual_producer_charges(bool priced)
    {
        var artifact = await RecordDispatchHistoryAsync(false, priced);
        Assert.Equal(!priced, artifact.State.LineageCharges.HasUnpricedCostHistory);
        var rows = artifact.State.DispatchObservations;
        Assert.Equal(0, rows[0].Usage!.CostMicrounits);
        Assert.Equal(0, rows[0].Charges!.CostMicrounits.Observed);
        Assert.Equal(0, rows[0].Charges!.CostMicrounits.TotalCharged);
        Assert.Equal(priced, rows[1].Charges!.CostMicrounits.ConservativeUnobserved > 0);
        var parsed = CampaignStateJson.Parse(artifact.ExactUtf8Json.AsMemory());
        Assert.True(parsed.IsValid);
        Assert.Equal(0, parsed.Artifact!.State.DispatchObservations[0].Charges!.CostMicrounits.Observed);
    }

    [Theory]
    [InlineData("cachedInputTokens", true)]
    [InlineData("cachedInputTokens", false)]
    [InlineData("reasoningTokens", true)]
    [InlineData("reasoningTokens", false)]
    [InlineData("costMicrounits", true)]
    [InlineData("costMicrounits", false)]
    [InlineData("activeElapsedMilliseconds", true)]
    public async Task Rehashed_known_zero_charge_observation_cannot_be_replaced_by_null(string dimension, bool changeRow)
    {
        var artifact = await RecordDispatchHistoryAsync(false);
        AssertInvalidDispatchCharge(artifact, dimension, changeRow, removeObserved: true);
    }

    private static void AssertInvalidDispatchCharge(CampaignCheckpointArtifact artifact, string dimension, bool changeRow,
        bool removeObserved = false)
    {
        var row = artifact.State.DispatchObservations[0];
        var charges = row.Charges!;
        var property = char.ToUpperInvariant(dimension[0]) + dimension[1..];
        var chargeProperty = typeof(CampaignDispatchCharges).GetProperty(property)!;
        var original = (CampaignChargeObservation)chargeProperty.GetValue(charges)!;
        if (removeObserved) Assert.Equal(0, original.Observed);
        var changed = removeObserved ? original with { Observed = null }
            : original with { Observed = (original.Observed ?? 0) + 1, TotalCharged = original.TotalCharged + 1 };
        var changedCharges = charges with { };
        chargeProperty.SetValue(changedCharges, changed);
        var root = JsonNode.Parse(artifact.ExactUtf8Json.AsSpan())!;
        if (changeRow)
        {
            row = row with { Charges = changedCharges };
            row = row with { SettlementCommitmentSha256 = CampaignStateFactory.DispatchSettlementCommitment(row) };
            var node = root["dispatchObservations"]![0]!;
            node["charges"]![dimension]!["observed"] = changed.Observed;
            node["charges"]![dimension]!["totalCharged"] = changed.TotalCharged;
            node["settlementCommitmentSha256"] = row.SettlementCommitmentSha256;
        }
        var lineage = artifact.State.LineageCharges;
        var lineageProperty = typeof(CampaignLineageCharges).GetProperty(property)!;
        var oldTotal = (CampaignChargeObservation)lineageProperty.GetValue(lineage)!;
        var newTotal = oldTotal;
        if (!removeObserved || !changeRow)
        {
            if (removeObserved) Assert.Equal(0, oldTotal.Observed);
            newTotal = removeObserved ? oldTotal with { Observed = null }
                : oldTotal with { Observed = (oldTotal.Observed ?? 0) + 1, TotalCharged = oldTotal.TotalCharged + 1 };
        }
        var changedLineage = lineage with { };
        lineageProperty.SetValue(changedLineage, newTotal);
        root["lineageCharges"]![dimension]!["observed"] = newTotal.Observed;
        root["lineageCharges"]![dimension]!["totalCharged"] = newTotal.TotalCharged;
        var bytes = System.Text.Encoding.UTF8.GetBytes(root.ToJsonString() + "\n");
        // Parse has no caller-supplied digest. Acceptance additionally receives the
        // recomputed digest, rather than failing merely on stale transport integrity.
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        var parsed = CampaignStateJson.Parse(bytes);
        Assert.False(parsed.IsValid, $"Accepted rehashed {dimension}; changeRow={changeRow}, state={row.State}");
        Assert.Equal(CampaignStateValidationCode.InvalidCorrelation, parsed.FailureCode);
        Assert.NotEqual(CampaignCheckpointAcceptanceKind.Accepted, CampaignCheckpointAcceptance.AcceptCurrent(
            CampaignCheckpointReadResult.Found(bytes, artifact.CheckpointRevision, digest)).Kind);
        var basis = artifact.State;
        var state = new CampaignCheckpointState(basis.ProductRevision, basis.CampaignLineage, basis.Snapshot,
            basis.CheckpointRevision, basis.ConfiguredCeilings, changedLineage, basis.WorkItems, basis.Batch,
            basis.ActiveReservation, basis.CandidateObservation, basis.CumulativeOutcome, basis.KnownCompletedOperations,
            basis.TerminalOutcome, basis.Predecessor)
        {
            AcceptedCandidateOrigin = basis.AcceptedCandidateOrigin,
            ScribeProgress = basis.ScribeProgress,
            DispatchObservations = changeRow ? artifact.State.DispatchObservations.SetItem(0, row) : artifact.State.DispatchObservations,
        };
        Assert.Throws<CampaignStateValidationException>(() => CampaignStateJson.CreateArtifact(state));
    }

    [Fact]
    public async Task Per_request_observations_distinguish_swapped_presence_with_equal_totals_and_survive_roundtrip()
    {
        var first = await RecordDispatchHistoryAsync(false);
        var swapped = await RecordDispatchHistoryAsync(true);
        Assert.Equal(first.State.LineageCharges, swapped.State.LineageCharges);
        Assert.NotEqual(first.Sha256, swapped.Sha256);
        foreach (var artifact in new[] { first, swapped })
        {
            using var document = System.Text.Json.JsonDocument.Parse(artifact.ExactUtf8Json.AsMemory());
            var schema = EvaluateCampaignSchema(document.RootElement);
            Assert.True(schema.IsValid, DescribeSchemaFailures(schema));
            var parsed = CampaignStateJson.Parse(artifact.ExactUtf8Json.AsMemory());
            Assert.True(parsed.IsValid, parsed.FailureCode?.ToString());
            Assert.Equal(artifact.Sha256, parsed.Artifact!.Sha256);
            var rows = parsed.Artifact.State.DispatchObservations;
            Assert.Equal(2, rows.Length);
            Assert.Equal(new long[] { 1, 2 }, rows.Select(row => row.DispatchOrdinal));
            Assert.All(rows, row => Assert.Equal(CampaignDispatchObservationState.Settled, row.State));
            Assert.Equal(0, rows.Single(row => row.Usage!.CostMicrounits is not null).Usage!.CostMicrounits);
            Assert.Null(rows.Single(row => row.Usage!.CostMicrounits is null).Usage!.CurrencyId);
            Assert.Contains(rows, row => row.Cache == DocumentationScribeCacheObservation.Hit);
            Assert.Contains(rows, row => row.Cache == DocumentationScribeCacheObservation.NotReported);
            Assert.NotNull(rows[0].Charges);
            Assert.Equal(1, rows[0].Charges!.ProviderRequests.TotalCharged);
            Assert.True(CampaignStateJson.Write(parsed.Artifact.State).AsSpan().SequenceEqual(artifact.ExactUtf8Json.AsSpan()));
        }
        Assert.Equal(10, first.State.DispatchObservations[0].Usage!.InputTokens);
        Assert.Null(swapped.State.DispatchObservations[0].Usage!.InputTokens);
        Assert.Null(first.State.DispatchObservations[1].Usage!.InputTokens);
        Assert.Equal(10, swapped.State.DispatchObservations[1].Usage!.InputTokens);
    }

    [Fact]
    public async Task Dispatch_history_rejects_duplicate_missing_reordered_and_conflicting_observations()
    {
        var artifact = await RecordDispatchHistoryAsync(false);
        foreach (var mutation in new Action<JsonArray>[]
        {
            rows => rows.Add(rows[0]!.DeepClone()),
            rows => rows.RemoveAt(0),
            rows => { var first = rows[0]!.DeepClone(); rows[0] = rows[1]!.DeepClone(); rows[1] = first; },
            rows => rows[0]!["requestCommitmentSha256"] = new string('e', 64),
            rows => rows[0]!["usage"]!["inputTokens"] = 11,
            rows => rows[0]!["usage"]!["costMicrounits"] = null,
            rows => rows[0]!["cache"] = "cache.miss",
        })
        {
            var root = JsonNode.Parse(artifact.ExactUtf8Json.AsSpan())!;
            mutation(root["dispatchObservations"]!.AsArray());
            var parsed = CampaignStateJson.Parse(System.Text.Encoding.UTF8.GetBytes(root.ToJsonString()));
            Assert.False(parsed.IsValid);
            Assert.Contains(parsed.FailureCode, new CampaignStateValidationCode?[]
                { CampaignStateValidationCode.InvalidCorrelation, CampaignStateValidationCode.InvalidBound });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_physical_reservation_records_not_sent_or_uncertain_takeover_without_fabricating_usage(bool interrupted)
    {
        var scenario = UnlimitedScenario();
        var request = CreateScribeExchange(scenario.Plan.WorkItems[0], scenario: scenario).Request;
        var initial = CampaignStateJson.CreateArtifact(scenario.InitialState);
        var store = new TransitionCheckpointStore(initial);
        var allowance = CampaignInvocationTestPolicy.Create(request.Limits, new C4CampaignClock());
        var admitted = CampaignStateReducer.AdmitProviderInvocation(initial, scenario.ExecutionAuthority,
            "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan, scenario.Plan.WorkItems[0].WorkItemKey,
            request, CampaignStateFactory.CreateInvocationTargetAllowance(initial.State, new(100)), allowance);
        var accepted = await CampaignCheckpointAcceptance.AcceptAsync(store, admitted);
        var parent = CampaignStateReducer.CreateProviderInvocationAuthority(accepted.AcceptedCheckpoint!, scenario.ExecutionAuthority,
            "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan, request);
        var coordinator = new CampaignScribeExecutionCoordinator(parent, allowance, store, default, null, 0);
        Assert.True(parent.TryBeginDispatch(out _));
        var permit = await coordinator.Scope.ReserveProviderAsync(new(Sha256("unknown request"), 1, 1,
            request.Limits.MaximumOutputTokens), default);
        Assert.NotNull(permit);
        var reserved = parent.AcceptedCheckpoint.Artifact;
        Assert.Equal(CampaignDispatchObservationState.Reserved, Assert.Single(reserved.State.DispatchObservations).State);
        CampaignCheckpointArtifact completed;
        if (interrupted)
        {
            var readback = await CampaignCheckpointAcceptance.AcceptCurrentAsync(store);
            var retirementAcceptance = await CampaignCommandRunner.AcceptProviderRetirementAsync(readback.AcceptedCheckpoint!, store, default);
            Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted, retirementAcceptance.Kind);
            var retirement = CampaignStateReducer.RetireInterruptedProviderAttempt(retirementAcceptance.AcceptedCheckpoint!);
            Assert.Equal(CampaignTransitionKind.Applied, retirement.Kind);
            completed = retirement.Artifact;
        }
        else
        {
            Assert.True(await coordinator.Scope.SettleProviderAsync(permit,
                new(DocumentationScribeDispatchDisposition.Interrupted, null), default));
            completed = parent.AcceptedCheckpoint.Artifact;
        }
        var parsed = CampaignStateJson.Parse(completed.ExactUtf8Json.AsMemory());
        Assert.True(parsed.IsValid, parsed.FailureCode?.ToString());
        var row = Assert.Single(parsed.Artifact!.State.DispatchObservations);
        Assert.Equal(interrupted ? CampaignDispatchObservationState.Unknown : CampaignDispatchObservationState.NotSent, row.State);
        Assert.Null(row.Usage);
        Assert.Equal(DocumentationScribeCacheObservation.NotReported, row.Cache);
        Assert.Equal(interrupted ? 1 : 0, row.Charges!.ProviderRequests.TotalCharged);
        Assert.Equal(interrupted ? 1 : 0, row.Charges.ProviderRequests.ConservativeUnobserved);
        Assert.Equal(interrupted, row.ElapsedMilliseconds is null);
        foreach (var dimension in new[] { "providerRequests", "inputTokens", "cachedInputTokens", "uncachedInputTokens",
            "outputTokens", "reasoningTokens", "costMicrounits", "activeElapsedMilliseconds" })
        {
            AssertInvalidDispatchCharge(completed, dimension, changeRow: true);
            AssertInvalidDispatchCharge(completed, dimension, changeRow: true, removeObserved: true);
        }
    }

    private static async Task<CampaignCheckpointArtifact> RecordDispatchHistoryAsync(bool swapped, bool priced = true)
    {
        var scenario = CreateProposalScenario(targetLimit: 1, maximumProviderRequests: null,
            maximumInputTokens: null, maximumUncachedInputTokens: null, maximumOutputTokens: null,
            maximumCostMicrounits: null, maximumElapsedMilliseconds: null, costEnforced: priced,
            costRates: new CampaignCostRates(1_000_000, 2_000_000, 1_000_000, 3_000_000));
        var request = CreateScribeExchange(scenario.Plan.WorkItems[0], scenario: scenario).Request;
        var initial = CampaignStateJson.CreateArtifact(scenario.InitialState);
        var store = new TransitionCheckpointStore(initial);
        var allowance = CampaignInvocationTestPolicy.Create(request.Limits, new C4CampaignClock());
        var admitted = CampaignStateReducer.AdmitProviderInvocation(initial, scenario.ExecutionAuthority,
            "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan, scenario.Plan.WorkItems[0].WorkItemKey,
            request, CampaignStateFactory.CreateInvocationTargetAllowance(initial.State, new(100)), allowance);
        var accepted = await CampaignCheckpointAcceptance.AcceptAsync(store, admitted);
        var parent = CampaignStateReducer.CreateProviderInvocationAuthority(accepted.AcceptedCheckpoint!, scenario.ExecutionAuthority,
            "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan, request);
        var coordinator = new CampaignScribeExecutionCoordinator(parent, allowance, store, default, null, 0);
        Assert.True(parent.TryBeginDispatch(out _));
        for (var index = 1; index <= 2; index++)
        {
            var permit = await coordinator.Scope.ReserveProviderAsync(new(Sha256("physical " + index), 1, index,
                allowance.PreviewProviderExposure().OutputTokens), default);
            Assert.NotNull(permit);
            Assert.True(coordinator.Scope.BeginPhysicalDispatch(permit));
            var knownInput = (index == 1) != swapped;
            var usage = knownInput ? new DocumentationScribeDispatchUsage(10, 0, 10, 20, 0, "USD", 0)
                : new DocumentationScribeDispatchUsage(null, 0, 10, 20, 0);
            var settlement = new DocumentationScribeDispatchSettlement(DocumentationScribeDispatchDisposition.Success, usage,
                Cache: knownInput ? DocumentationScribeCacheObservation.Hit : DocumentationScribeCacheObservation.NotReported);
            var probe = CampaignStateReducer.SettleProviderDispatch(parent, permit, settlement, 0);
            Assert.Equal(CampaignTransitionKind.Applied, probe.Kind);
            var parsed = CampaignStateJson.Parse(probe.Artifact.ExactUtf8Json.AsMemory());
            Assert.True(parsed.IsValid, parsed.FailureCode?.ToString());
            Assert.True(await coordinator.Scope.SettleProviderAsync(permit, settlement, default));
        }
        return parent.AcceptedCheckpoint.Artifact;
    }
}
