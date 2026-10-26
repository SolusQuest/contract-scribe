using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Core;

namespace ContractScribe.Tests;

public sealed partial class CampaignStateContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Host_admission_cannot_begin_a_checkpoint_write_after_original_deadline_or_caller_stop(bool caller)
    {
        var clock = new SolusDeadlineClock();
        using var cancellation = new CancellationTokenSource();
        var trial = await SolusTrialAsync(clock: clock, targetMilliseconds: 10000, invocationMilliseconds: 10000,
            hostTimeouts: DocumentationScribeHostTimeouts.Create(terminalSubmissionMilliseconds: 100),
            checkpointAcceptanceMilliseconds: 1000);
        trial.Coordinator.Scope.BindRuntimeWindow(trial.Request, clock, clock.GetTimestamp());
        var predecessor = trial.Store.Current.ExactUtf8Json.ToArray();
        trial.Store.BeforeNextRead = () =>
        {
            if (caller) cancellation.Cancel();
            else clock.Advance(200);
        };
        var permit = await trial.Coordinator.Scope.BeginHostAsync(DocumentationScribeHostOperation.TerminalSubmission, cancellation.Token);
        Assert.DoesNotContain(CampaignProviderHostPhase.TerminalSubmission, trial.Store.AdmittedHostPhases);
        Assert.Equal(predecessor, trial.Store.Current.ExactUtf8Json.ToArray());
        Assert.Null(permit);
        Assert.Equal(0, trial.Coordinator.Scope.Allowance.ToolCallCount);
        Assert.Equal(CampaignCheckpointAcceptanceKind.Cancelled, trial.Coordinator.CheckpointFailure);
    }

    [Theory]
    [InlineData(600000, 600000, 300000)]
    [InlineData(120000, 600000, 120000)]
    [InlineData(600000, 110000, 110000)]
    public async Task Terminal_policy_is_independent_of_provider_and_keeps_original_parent_remainders(
        int target, int invocation, int expected)
    {
        var clock = new SolusDeadlineClock();
        var trial = await SolusTrialAsync(clock: clock, targetMilliseconds: target, invocationMilliseconds: invocation,
            hostMilliseconds: 90000, hostTimeouts: DocumentationScribeHostTimeouts.Default);
        var scope = trial.Coordinator.Scope;
        scope.BindRuntimeWindow(trial.Request, clock, clock.GetTimestamp());
        clock.Advance(1000);
        var permit = await scope.BeginHostAsync(DocumentationScribeHostOperation.TerminalSubmission, default);
        Assert.NotNull(permit);
        var maximum = expected == 300000 ? expected : expected - 1000;
        Assert.Equal(maximum, permit.MaximumMilliseconds);
        Assert.Equal(maximum, Assert.IsType<CampaignProviderReservation>(trial.Store.Current.State.ActiveReservation).Exposure.ElapsedMilliseconds);
        Assert.Equal(90000, scope.Allowance.Limits.MaximumRequestElapsedMilliseconds);
        clock.Advance(90001);
        Assert.True(permit.TryBeginOperation());
        Assert.False(permit.TryBeginOperation());
        Assert.Equal(maximum - 90001, permit.RemainingMilliseconds);
        Assert.Equal(1, scope.Allowance.ToolCallCount);
        clock.Advance(maximum - 90001);
        Assert.Equal(0, permit.RemainingMilliseconds);
        Assert.False(permit.TryBeginOperation());
        Assert.Null(await scope.BeginHostAsync(DocumentationScribeHostOperation.TerminalSubmission, default));
        Assert.True(await scope.CompleteHostAsync(permit, default));
        Assert.Equal(maximum + 1000, trial.Store.Current.State.LineageCharges.ActiveElapsedMilliseconds.Observed);
    }

    [Fact]
    public async Task Smaller_semantic_policy_roundtrips_and_is_cleared_when_the_original_query_closes()
    {
        var clock = new SolusDeadlineClock();
        var policy = CampaignSemanticDurations.Create(10000, 5000, 10000);
        var trial = await SolusTrialAsync(clock: clock, targetMilliseconds: 120000, invocationMilliseconds: 120000,
            hostTimeouts: DocumentationScribeHostTimeouts.Create(semantic: policy));
        trial.Coordinator.Scope.BindRuntimeWindow(trial.Request, clock, clock.GetTimestamp());
        trial.Store.DelayedReadbackPhase = CampaignProviderHostPhase.SemanticTool;
        trial.Store.DelayedReadback = () => clock.Advance(4000);
        var permit = await trial.Coordinator.Scope.BeginHostAsync(DocumentationScribeHostOperation.SemanticTool, default);
        Assert.NotNull(permit);
        var saved = trial.Store.Current;
        var parsed = CampaignStateJson.Parse(saved.ExactUtf8Json.AsMemory());
        Assert.True(parsed.IsValid);
        var claim = Assert.IsType<CampaignProviderReservation>(parsed.Artifact!.State.ActiveReservation);
        Assert.Equal(policy, claim.SemanticDurations);
        Assert.Equal(25000, claim.Exposure.ElapsedMilliseconds);
        Assert.True(EvaluateCampaignSchema(JsonSerializer.SerializeToElement(JsonNode.Parse(saved.ExactUtf8Json.AsSpan()))).IsValid);
        Assert.Equal(5000, permit.RemainingMilliseconds);
        Assert.True(permit.TryBeginOperation());
        clock.Advance(4999);
        Assert.Equal(1, permit.RemainingMilliseconds);
        Assert.True(await trial.Coordinator.Scope.CompleteHostAsync(permit, default));
        Assert.Null(Assert.IsType<CampaignProviderReservation>(trial.Store.Current.State.ActiveReservation).SemanticDurations);
        Assert.True(CampaignStateJson.Parse(trial.Store.Current.ExactUtf8Json.AsMemory()).IsValid);
        Assert.True(EvaluateCampaignSchema(JsonSerializer.SerializeToElement(JsonNode.Parse(trial.Store.Current.ExactUtf8Json.AsSpan()))).IsValid);
    }

    [Fact]
    public async Task Interrupted_semantic_retirement_uses_saved_exposure_without_reconstructing_a_configured_query()
    {
        var clock = new SolusDeadlineClock();
        var originalPolicy = CampaignSemanticDurations.Create(10000, 5000, 10000);
        var trial = await SolusTrialAsync(clock: clock,
            hostTimeouts: DocumentationScribeHostTimeouts.Create(semantic: originalPolicy));
        trial.Coordinator.Scope.BindRuntimeWindow(trial.Request, clock, clock.GetTimestamp());
        var permit = await trial.Coordinator.Scope.BeginHostAsync(DocumentationScribeHostOperation.SemanticTool, default);
        Assert.NotNull(permit);
        var bytes = trial.Store.Current.ExactUtf8Json.ToArray();
        var changedPolicy = DocumentationScribeHostTimeouts.Default;
        Assert.NotEqual(originalPolicy, changedPolicy.Semantic);
        var accepted = await CampaignCheckpointAcceptance.AcceptCurrentAsync(trial.Store);
        var retired = CampaignStateReducer.RetireInterruptedProviderAttempt(accepted.AcceptedCheckpoint!);
        Assert.Equal(CampaignTransitionKind.Applied, retired.Kind);
        Assert.Null(retired.Artifact.State.ActiveReservation);
        Assert.Equal(25000, retired.Artifact.State.LineageCharges.ActiveElapsedMilliseconds.ConservativeUnobserved);
        Assert.Equal(bytes, trial.Store.Current.ExactUtf8Json.ToArray());
        Assert.Equal(originalPolicy, Assert.IsType<CampaignProviderReservation>(
            CampaignStateJson.Parse(bytes).Artifact!.State.ActiveReservation).SemanticDurations);
        Assert.Equal(CampaignTransitionFailure.InvalidAuthority,
            CampaignStateReducer.RetireInterruptedProviderAttempt(accepted.AcceptedCheckpoint!).Failure);
        var invalid = JsonNode.Parse(bytes)!;
        invalid["activeReservation"]!["hostPhase"] = "terminal-submission";
        var invalidBytes = Encoding.UTF8.GetBytes(invalid.ToJsonString() + "\n");
        Assert.False(CampaignStateJson.Parse(invalidBytes).IsValid);
        Assert.False(EvaluateCampaignSchema(JsonSerializer.SerializeToElement(invalid)).IsValid);
        permit.SemanticWindow!.Dispose();
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Checkpoint_configuration_controls_both_phase_and_progress_acceptance_without_resetting_the_terminal(
        bool progress, bool extended)
    {
        var clock = new SolusDeadlineClock();
        var trial = await SolusTrialAsync(clock: clock, targetMilliseconds: 600000, invocationMilliseconds: 600000,
            checkpointAcceptanceMilliseconds: extended ? 40000 : 30000);
        trial.Coordinator.Scope.BindRuntimeWindow(trial.Request, clock, clock.GetTimestamp());
        DocumentationScribeHostPermit? permit = null;
        if (progress) permit = await trial.Coordinator.Scope.BeginHostAsync(DocumentationScribeHostOperation.TerminalSubmission, default);
        trial.Store.DelayedReadbackPhase = CampaignProviderHostPhase.TerminalSubmission;
        trial.Store.DelayedReadback = () => clock.Advance(35000);
        bool accepted;
        if (progress)
        {
            Assert.NotNull(permit);
            var current = trial.Authority.AcceptedCheckpoint.Artifact;
            accepted = await trial.Coordinator.AcceptHostProgressAsync(new(CampaignTransitionKind.Unchanged,
                current, current, CampaignTransitionFailure.None, trial.Attempt), default);
        }
        else
        {
            permit = await trial.Coordinator.Scope.BeginHostAsync(DocumentationScribeHostOperation.TerminalSubmission, default);
            accepted = permit is not null;
        }
        Assert.Equal(extended, accepted);
        Assert.Equal(0, trial.Coordinator.Scope.Allowance.ToolCallCount);
        if (extended)
        {
            Assert.Null(trial.Coordinator.CheckpointFailure);
            Assert.Equal(265000, permit!.RemainingMilliseconds);
            Assert.True(permit.TryBeginOperation());
            Assert.True(await trial.Coordinator.Scope.CompleteHostAsync(permit, default));
        }
        else
        {
            Assert.Equal(CampaignCheckpointAcceptanceKind.Cancelled, trial.Coordinator.CheckpointFailure);
            Assert.False(trial.Coordinator.CheckpointCancelledByHostDeadline);
            Assert.Equal(DocumentationScribeInvocationStopReason.None, trial.Coordinator.Scope.Allowance.StopReason);
        }
    }

    [Fact]
    public async Task Configured_settlement_tail_only_bounds_observation_and_cannot_grant_late_execution()
    {
        var configurationNode = ReadJsonFixture("campaign", "cli", "configuration-valid.json");
        configurationNode["timeouts"]!["settlementTailMilliseconds"] = 5000;
        configurationNode["timeouts"]!["checkpointAcceptanceMilliseconds"] = 800000;
        configurationNode["budgets"]!["invocation"]!["maximumElapsedMilliseconds"] = 600000;
        var configuration = ContractScribe.Cli.CampaignConfiguration.Parse(Encoding.UTF8.GetBytes(configurationNode.ToJsonString()));
        var clock = new SolusDeadlineClock();
        using var settlement = new CancellationTokenSource(TimeSpan.FromMilliseconds(
            configuration.Budgets.Invocation.MaximumElapsedMilliseconds + configuration.Timeouts.SettlementTailMilliseconds), clock);
        var trial = await SolusTrialAsync(clock: clock, targetMilliseconds: 600000, invocationMilliseconds: 600000,
            hostTimeouts: configuration.Timeouts.Host,
            checkpointAcceptanceMilliseconds: configuration.Timeouts.CheckpointAcceptanceMilliseconds,
            settlementToken: settlement.Token);
        trial.Coordinator.Scope.BindRuntimeWindow(trial.Request, clock, clock.GetTimestamp());
        trial.Store.DelayedReadbackPhase = CampaignProviderHostPhase.TerminalSubmission;
        trial.Store.DelayedReadback = () => clock.Advance(605001);
        var permit = await trial.Coordinator.Scope.BeginHostAsync(DocumentationScribeHostOperation.TerminalSubmission, default);
        Assert.True(settlement.IsCancellationRequested);
        Assert.Null(permit);
        Assert.Equal(CampaignCheckpointAcceptanceKind.Cancelled, trial.Coordinator.CheckpointFailure);
        Assert.False(trial.Coordinator.CheckpointCancelledByHostDeadline);
        Assert.Equal(0, trial.Coordinator.Scope.Allowance.ToolCallCount);
        Assert.Equal(0, trial.Coordinator.Scope.PhysicalProviderRequestCount);
        Assert.Null(await trial.Coordinator.Scope.BeginHostAsync(DocumentationScribeHostOperation.TerminalSubmission, default));
        Assert.Equal(0, trial.Store.Current.State.LineageCharges.PatchValidationInvocations);
    }

}
