using ContractScribe.Cli;
using ContractScribe.Core;
using ContractScribe.GitHub.Coordination;
using ContractScribe.Agent.Runtime;
using System.Text.Json;

namespace ContractScribe.Tests;

public sealed partial class CampaignStateContractTests
{
    [Theory]
    [InlineData("deadline", "OperationCheckpointAcceptanceDeadline")]
    [InlineData("settlement", "OperationSettlementToken")]
    [InlineData("both", "Unknown")]
    [InlineData("clean", null)]
    [InlineData("frozen", null)]
    [InlineData("earlier", "RecoveryToken")]
    [InlineData("setup", "OperationToken")]
    public async Task Dispatch_readback_cancellation_reports_its_first_local_parent_without_granting_send(
        string source, string? expected)
    {
        var clock = new SolusDeadlineClock();
        var trial = await SolusTrialAsync(clock: clock, targetMilliseconds: 600000, invocationMilliseconds: 600000);
        var remote = new GitHubCoordinationRefTests.CoordinationRemote();
        using var handler = new GitHubCoordinationRefTests.CheckpointInterceptHandler(remote);
        using var client = GitHubCoordinationRefTests.CheckpointClient(remote, trial.Scenario.InitialState.CampaignLineage, handler);
        var store = GitHubCoordinationStore.Create(client);
        var usage = new InvocationUsage(clock);
        string? frozenDiagnostic = null;
        GitHubCheckpointDiagnostic? originalFailure = null;
        await using (store)
        {
            Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
            Assert.True(await store.BindPlanAsync(CampaignPlanner.CreateRecoveryProjection(trial.Scenario.Plan), default));
            var initial = CampaignStateJson.CreateArtifact(trial.Scenario.InitialState);
            Assert.Equal(CampaignCheckpointWriteKind.Written, (await store.CreateIfAbsentAsync(initial.ExactUtf8Json.AsMemory(),
                initial.CheckpointRevision, initial.Sha256, default)).Kind);
            var allowance = new DocumentationScribeInvocationAllowance(trial.Coordinator.Scope.Allowance.Limits, clock);
            var admission = CampaignStateReducer.AdmitProviderInvocation(initial, trial.Scenario.ExecutionAuthority, "style.synthetic",
                trial.Scenario.StyleProjection, trial.Scenario.Input, trial.Scenario.Plan, trial.Scenario.Plan.WorkItems[0].WorkItemKey,
                trial.Request, CampaignStateFactory.CreateInvocationTargetAllowance(initial.State, new(100)), allowance);
            var accepted = await CampaignCheckpointAcceptance.AcceptAsync(store, admission);
            Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted, accepted.Kind);
            var authority = CampaignStateReducer.CreateProviderInvocationAuthority(accepted.AcceptedCheckpoint!, trial.Scenario.ExecutionAuthority,
                "style.synthetic", trial.Scenario.StyleProjection, trial.Scenario.Input, trial.Scenario.Plan, trial.Request);
            using var settlement = new CancellationTokenSource();
            usage.Bind(allowance);
            var terminal = GitHubProposalPresentation.Campaign(CliBuildIdentity.Current,
                new("execution", CampaignOperation.Resume, "campaign.cancelled", accepted.Artifact!.CheckpointRevision));
            if (source == "frozen") _ = GitHubProposalPresentation.WithUsage(terminal, usage);
            if (source == "earlier") usage.ObserveCheckpoint(new(1, GitHubCheckpointOperation.Read, null,
                GitHubCheckpointStage.Gate, GitHubCheckpointReason.Cancelled, Cancellation: GitHubCheckpointCancellation.RecoveryToken));
            var coordinator = new CampaignScribeExecutionCoordinator(authority, allowance, store, settlement.Token, null,
                clock.GetTimestamp(), checkpointAcceptanceMilliseconds: 1000, usage: usage);
            Assert.True(authority.TryBeginDispatch(out _));
            var predecessor = authority.AcceptedCheckpoint.Artifact;
            var writes = remote.RefMutationAttempts;
            string? dispatchBlob = null;
            var hits = 0;
            handler.BeforeRequestWithCancellation = async (message, token) =>
            {
                if (GitHubCoordinationRefTests.IsRefMutation(message))
                {
                    using var body = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
                    var input = body.RootElement.GetProperty("variables").GetProperty("input");
                    if (input.TryGetProperty("fileChanges", out var changes))
                    {
                        var file = changes.GetProperty("additions").EnumerateArray().SingleOrDefault(item => item.GetProperty("path").GetString() == ".contract-scribe/checkpoint.json");
                        if (file.ValueKind == JsonValueKind.Object)
                        {
                            var bytes = Convert.FromBase64String(file.GetProperty("contents").GetString()!);
                            var parsed = CampaignStateJson.Parse(bytes);
                            if (parsed.Artifact is { } artifact && artifact.CheckpointRevision == predecessor.CheckpointRevision + 1)
                                dispatchBlob = GitHubCoordinationObjects.ObjectOid("blob", bytes);
                        }
                    }
                }
                if (hits != 0 || dispatchBlob is null || remote.RefMutationAttempts != writes + 1
                    || !GitHubCoordinationRefTests.IsCheckpointQuery(message, currentOnly: true)) return;
                Assert.Equal(1, ++hits);
                if (source == "clean") { clock.Advance(10); return; }
                if (source == "both")
                {
                    // Both flags are requested before the linked notification;
                    // there is no registration-order winner to report.
                    using var alsoDeadline = settlement.Token.Register(() => clock.Advance(1001));
                    settlement.Cancel();
                }
                else if (source == "settlement") settlement.Cancel();
                else clock.Advance(1001);
                await Task.Delay(Timeout.Infinite, token);
            };
            var exchange = new ScriptedDocumentationScribeModelExchange([
                ScriptedDocumentationScribeStep.Return(new([], [new(C3Submission(trial.Scenario, trial.Request))], usage: new(0, 0, 0, 0))),
            C3FinishStep(trial.Request)]);
            var session = new ScribeSessionTestDriver(trial.Request, trial.Attempt, trial.Prompt,
                new DocumentationScribeToolRegistryBuilder(trial.Request.ToolPolicyId).Build(), exchange, coordinator.Scope);
            if (source == "setup") settlement.Cancel();
            await session.RunAsync();
            Assert.Equal(source == "setup" ? 0 : 1, hits);
            handler.BeforeRequestWithCancellation = null;
            if (source == "clean")
            {
                Assert.Equal(2, exchange.Requests.Length);
                Assert.Equal(2, coordinator.Scope.PhysicalProviderRequestCount);
                Assert.Null(store.CheckpointDiagnostic);
                Assert.Null(coordinator.CheckpointFailure);
                Assert.All(authority.AcceptedCheckpoint.Artifact.State.DispatchObservations,
                    row => Assert.Equal(CampaignDispatchObservationState.Settled, row.State));
                return;
            }
            Assert.Empty(exchange.Requests);
            Assert.Equal(0, coordinator.Scope.PhysicalProviderRequestCount);
            Assert.Equal(predecessor.Sha256, authority.AcceptedCheckpoint.Artifact.Sha256);
            Assert.Equal(CampaignCheckpointAcceptanceKind.Cancelled, coordinator.CheckpointFailure);
            Assert.False(coordinator.CheckpointCancelledByHostDeadline);
            var failure = store.CheckpointDiagnostic;
            if (source == "setup") Assert.Null(failure); // Cancelled in Core before any remote operation.
            else
            {
                var diagnostic = Assert.IsType<GitHubCheckpointDiagnostic>(failure);
                Assert.Equal(GitHubCheckpointStage.CheckpointReadback, diagnostic.Stage);
                Assert.Equal(GitHubCheckpointReason.Cancelled, diagnostic.Reason);
                Assert.Equal(predecessor.CheckpointRevision + 1, diagnostic.AttemptedRevision);
            }
            var durable = CampaignStateJson.Parse((await store.ReadAsync(default)).ExactUtf8Json.AsMemory()).Artifact!;
            Assert.Equal(predecessor.CheckpointRevision + (source == "setup" ? 0 : 1), durable.CheckpointRevision);
            if (source == "setup") Assert.Empty(durable.State.DispatchObservations);
            else Assert.Equal(CampaignDispatchObservationState.Reserved, Assert.Single(durable.State.DispatchObservations).State);
            Assert.NotNull(durable.State.ActiveReservation);
            Assert.Null(durable.State.TerminalOutcome);
            Assert.Equal(predecessor.State.LineageCharges.PatchValidationInvocations, durable.State.LineageCharges.PatchValidationInvocations);
            usage.ObserveCheckpoint(failure);
            // A later parent cannot revise the local first notification, even
            // before the first serialized Usage freeze.
            settlement.Cancel();
            clock.Advance(300001);
            Assert.Same(failure, store.CheckpointDiagnostic);
            using var json = JsonDocument.Parse(GitHubProposalPresentation.WithUsage(terminal, usage).StandardOutput);
            var reported = json.RootElement.GetProperty("invocationUsage").GetProperty("checkpointFailure");
            if (source is "frozen" or "setup") Assert.Equal(JsonValueKind.Null, reported.ValueKind);
            else
            {
                Assert.Equal(expected, reported.GetProperty("cancellation").GetString());
                Assert.Equal(9, reported.EnumerateObject().Count());
            }
            if (failure is not null) usage.ObserveCheckpoint(failure with { Cancellation = GitHubCheckpointCancellation.RecoveryToken });
            using var later = JsonDocument.Parse(GitHubProposalPresentation.WithUsage(terminal, usage).StandardOutput);
            Assert.Equal(reported.GetRawText(), later.RootElement.GetProperty("invocationUsage").GetProperty("checkpointFailure").GetRawText());
            frozenDiagnostic = reported.GetRawText();
            originalFailure = failure;
        }
        // Actual store disposal/owner cleanup occurs after the frozen report.
        // Neither that cleanup nor another reporting callback can replace it.
        Assert.Same(originalFailure, store.CheckpointDiagnostic);
        usage.ObserveCheckpoint(store.CheckpointDiagnostic);
        var afterCleanup = GitHubProposalPresentation.Campaign(CliBuildIdentity.Current,
            new("execution", CampaignOperation.Resume, "campaign.cancelled", 0));
        using var cleanupJson = JsonDocument.Parse(GitHubProposalPresentation.WithUsage(afterCleanup, usage).StandardOutput);
        Assert.Equal(frozenDiagnostic, cleanupJson.RootElement.GetProperty("invocationUsage").GetProperty("checkpointFailure").GetRawText());
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(0, false)]
    [InlineData(0, true)]
    public Task Remote_host_admission_fences_new_CAS_but_keeps_already_dispatched_readback(
        int stopAtCheckpointRead, bool caller) => RemoteHostAdmissionBoundaryAsync(stopAtCheckpointRead, caller, false);

    [Fact]
    public Task Remote_admission_expired_observation_retains_its_actual_stop_and_never_advances_Writer() =>
        RemoteHostAdmissionBoundaryAsync(0, false, true);

    private static async Task RemoteHostAdmissionBoundaryAsync(int stopAtCheckpointRead, bool caller, bool expiredObservation)
    {
        var clock = new SolusDeadlineClock();
        var trial = await SolusTrialAsync(clock: clock, targetMilliseconds: 10000, invocationMilliseconds: 10000);
        var remote = new GitHubCoordinationRefTests.CoordinationRemote();
        using var handler = new GitHubCoordinationRefTests.CheckpointInterceptHandler(remote);
        using var client = GitHubCoordinationRefTests.CheckpointClient(remote, trial.Scenario.InitialState.CampaignLineage, handler);
        await using var store = GitHubCoordinationStore.Create(client);
        Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
        Assert.True(await store.BindPlanAsync(CampaignPlanner.CreateRecoveryProjection(trial.Scenario.Plan), default));
        var initial = CampaignStateJson.CreateArtifact(trial.Scenario.InitialState);
        Assert.Equal(CampaignCheckpointWriteKind.Written, (await store.CreateIfAbsentAsync(initial.ExactUtf8Json.AsMemory(),
            initial.CheckpointRevision, initial.Sha256, default)).Kind);
        var allowance = new DocumentationScribeInvocationAllowance(trial.Coordinator.Scope.Allowance.Limits, clock);
        var admission = CampaignStateReducer.AdmitProviderInvocation(initial, trial.Scenario.ExecutionAuthority, "style.synthetic",
            trial.Scenario.StyleProjection, trial.Scenario.Input, trial.Scenario.Plan, trial.Scenario.Plan.WorkItems[0].WorkItemKey,
            trial.Request, CampaignStateFactory.CreateInvocationTargetAllowance(initial.State, new(100)), allowance);
        var accepted = await CampaignCheckpointAcceptance.AcceptAsync(store, admission);
        Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted, accepted.Kind);
        var authority = CampaignStateReducer.CreateProviderInvocationAuthority(accepted.AcceptedCheckpoint!, trial.Scenario.ExecutionAuthority,
            "style.synthetic", trial.Scenario.StyleProjection, trial.Scenario.Input, trial.Scenario.Plan, trial.Request);
        var coordinator = new CampaignScribeExecutionCoordinator(authority, allowance, store, default, null, clock.GetTimestamp(),
            DocumentationScribeHostTimeouts.Create(terminalSubmissionMilliseconds: 100), 1000);
        coordinator.Scope.BindRuntimeWindow(trial.Request, clock, clock.GetTimestamp());
        Assert.True(authority.TryBeginDispatch(out _));
        var predecessor = authority.AcceptedCheckpoint.Artifact;
        var predecessorHead = remote.CoordinationHead;
        var checkpointOid = GitHubCoordinationObjects.ObjectOid("blob", predecessor.ExactUtf8Json.AsSpan());
        var writes = remote.RefMutationAttempts;
        var checkpointReads = 0;
        var predecessorRefReads = 0;
        var stopped = false;
        using var cancellation = new CancellationTokenSource();
        void Stop()
        {
            Assert.False(stopped);
            stopped = true;
            if (caller) cancellation.Cancel();
            else clock.Advance(expiredObservation ? 1200 : 200);
        }
        handler.BeforeRequestWithCancellation = (request, token) =>
        {
            if (stopAtCheckpointRead == 1 && GitHubCoordinationRefTests.IsCheckpointQuery(request, currentOnly: true)
                && ++checkpointReads == 1) Stop();
            // The duplicate Core read is gone. Keep the other pre-mutation
            // stop at the conditional owner's final predecessor-ref fence.
            if (stopAtCheckpointRead == 2 && request.Method == HttpMethod.Get
                && request.RequestUri!.AbsolutePath.Contains("/git/ref/heads/contract-scribe/coordination/", StringComparison.Ordinal)
                && ++predecessorRefReads == 1) Stop();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };
        handler.AfterResponse = (request, response) =>
        {
            if (stopAtCheckpointRead == 0 && !stopped && request.RequestUri!.AbsolutePath == "/graphql"
                && remote.RefMutationAttempts > writes) Stop();
            return Task.FromResult(response);
        };
        var permit = await coordinator.Scope.BeginHostAsync(DocumentationScribeHostOperation.TerminalSubmission, cancellation.Token);
        Assert.True(stopped);
        Assert.Equal(0, coordinator.Scope.Allowance.ToolCallCount);
        if (stopAtCheckpointRead != 0)
        {
            Assert.Null(permit);
            Assert.Equal(writes, remote.RefMutationAttempts);
            Assert.Equal(predecessorHead, remote.CoordinationHead);
            Assert.Equal(predecessor.Sha256, authority.AcceptedCheckpoint.Artifact.Sha256);
            Assert.Equal(CampaignCheckpointAcceptanceKind.Cancelled, coordinator.CheckpointFailure);
            Assert.Equal(!caller, coordinator.CheckpointCancelledByHostDeadline);
        }
        else if (expiredObservation)
        {
            Assert.Null(permit);
            Assert.Equal(writes + 1, remote.RefMutationAttempts);
            Assert.Equal(predecessor.Sha256, authority.AcceptedCheckpoint.Artifact.Sha256);
            var failure = Assert.IsType<GitHubCheckpointDiagnostic>(store.CheckpointDiagnostic);
            Assert.Equal(GitHubCheckpointStage.CheckpointReadback, failure.Stage); // Folded aggregate readback was never dispatched after this stop.
            Assert.Equal(GitHubCheckpointReason.Cancelled, failure.Reason);
            Assert.Equal(GitHubCheckpointCancellation.OperationToken, failure.Cancellation);
            Assert.NotEqual(CampaignCheckpointAcceptanceKind.Conflict, coordinator.CheckpointFailure);
        }
        else
        {
            Assert.NotNull(permit);
            Assert.Equal(writes + 1, remote.RefMutationAttempts);
            Assert.NotEqual(predecessorHead, remote.CoordinationHead);
            Assert.Equal(predecessor.CheckpointRevision + 1, authority.AcceptedCheckpoint.Artifact.CheckpointRevision);
            Assert.Null(coordinator.CheckpointFailure);
            // The accepted factual write remains available for mandatory
            // settlement. Runtime's original caller stop still forbids work.
            if (caller) Assert.True(cancellation.IsCancellationRequested);
            else
            {
                Assert.Equal(0, permit.RemainingMilliseconds);
                Assert.False(permit.TryBeginOperation());
            }
        }
    }
}
