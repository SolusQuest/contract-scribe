using System.Text.Json.Nodes;
using ContractScribe.Cli;
using ContractScribe.Core;

namespace ContractScribe.Tests;

public sealed partial class CampaignStateContractTests
{
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
    }

    private static async Task<CampaignCheckpointArtifact> RecordDispatchHistoryAsync(bool swapped)
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
