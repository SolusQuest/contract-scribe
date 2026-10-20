using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using ContractScribe.Agent.Prompting;
using ContractScribe.Cli;
using ContractScribe.Core;

namespace ContractScribe.Tests;

public sealed partial class CampaignStateContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_fresh_shared_retry_restarts_all_failed_members_and_fences_a_foreign_durable_target(bool foreignTarget)
    {
        var scenario = CreateProposalScenario(workItemCount: 3, targetLimit: 3, maximumElapsedMilliseconds: null);
        var exchanges = scenario.Plan.WorkItems.Select(work => CreateScribeExchange(work, scenario: scenario)).ToArray();
        var request = SharedRetryRequest(exchanges);
        var initial = CampaignStateJson.CreateArtifact(scenario.InitialState);
        var allowance = CampaignInvocationTestPolicy.Create(request.Limits);
        var admitted = CampaignStateReducer.AdmitProviderInvocation(initial, scenario.ExecutionAuthority, "style.synthetic",
            scenario.StyleProjection, scenario.Input, scenario.Plan, scenario.Plan.WorkItems[0].WorkItemKey, request,
            CampaignStateFactory.CreateInvocationTargetAllowance(initial.State, new(3)), allowance);
        Assert.Equal(CampaignTransitionKind.Applied, admitted.Kind);
        var invocation = CampaignStateReducer.CreateProviderInvocationAuthority(AcceptForTest(initial, admitted), scenario.ExecutionAuthority,
            "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan, request);
        var store = new TransitionCheckpointStore(invocation.AcceptedCheckpoint.Artifact);
        var coordinator = new CampaignScribeExecutionCoordinator(invocation, allowance, store, default, null, 0);
        Assert.True(invocation.TryBeginDispatch(out var attempt));
        var durable = SharedRetrySubmission(request, exchanges[2], 2);
        var oldReceipt = DocumentationScribeValidation.AcknowledgeSubmission(durable,
            new(request.ArtifactSha256, durable.TargetId, durable.ContentSha256, DocumentationScribeAcknowledgementStage.DurablyRecorded));
        var failed = CreateScribeExchange(scenario.Plan.WorkItems[0], attemptId: attempt.Value,
            resultFixture: "retryable-failure-result.json", scenario: scenario).Result;
        var execution = scenario.ExecutionAuthority.Projection;
        var invalidMembers = request.ExecutableTargetIds.Take(2)
            .Select(id => new DocumentationScribeTargetFailure(id, "scribe.result.invalid-content")).ToImmutableArray();
        var failedResult = DocumentationScribeValidation.CreateSessionResult(request, attempt, failed.Stop,
            new(execution.ProviderConfigurationId, execution.ModelConfigurationId, execution.ScribeProtocolId,
                3, 4, 0, 1, 0, new(0, 0, 0, 0, 0), null, null, []), [oldReceipt], invalidMembers, 1, 2);
        var failedOutcome = DocumentationScribeValidation.BindValidatedRunOutcome(request, attempt, failedResult);
        SettleSyntheticDispatches(invocation, failedOutcome);
        var dispatched = invocation.AcceptedCheckpoint.Artifact;
        Assert.Equal(CampaignCheckpointWriteKind.Written, (await store.ReplaceIfCurrentAsync(admitted.Artifact.CheckpointRevision,
            admitted.Artifact.Sha256, dispatched.ExactUtf8Json.AsMemory(), dispatched.CheckpointRevision, dispatched.Sha256, default)).Kind);
        var permit = await coordinator.Scope.BeginHostAsync(DocumentationScribeHostOperation.TerminalSubmission, default);
        Assert.NotNull(permit); Assert.True(permit.TryBeginOperation());
        var accepted = CampaignStateReducer.AcceptScribeSubmission(invocation, durable);
        Assert.Equal(CampaignTransitionKind.Applied, accepted.Kind);
        Assert.True(await coordinator.AcceptHostProgressAsync(accepted, default));
        var progress = DocumentationScribeValidation.CreateProgressNotification(request, attempt, 1, 2,
            request.ExecutableTargetIds, [oldReceipt], invalidMembers, true);
        Assert.True(await coordinator.AcceptHostProgressAsync(CampaignStateReducer.AcceptScribeProgress(invocation, progress), default));
        Assert.True(await coordinator.Scope.CompleteHostAsync(permit, default));
        var registrar = invocation.TryCreateCompletionRegistrar();
        Assert.NotNull(registrar);
        Assert.True(registrar.TryRegister(CampaignProviderCompletionKind.Ordinary, failedOutcome, 0, out var completion));
        var closed = CampaignStateReducer.CompleteProviderInvocation(invocation.AcceptedCheckpoint.Artifact, completion!,
            scenario.ExecutionAuthority, "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan);
        Assert.Equal(CampaignTransitionKind.Applied, closed.Kind);
        Assert.All(closed.Artifact.State.WorkItems.Take(2), work =>
        {
            Assert.Equal(CampaignWorkStatus.Closed, work.Status);
            Assert.Equal(CampaignProviderFinalDisposition.Retryable, work.ClosedOutcome!.ProviderDisposition);
            Assert.Equal(1, work.OuterAttemptCount);
        });
        Assert.Equal(CampaignWorkStatus.ProposalComplete, closed.Artifact.State.WorkItems[2].Status);
        Assert.True(closed.Artifact.State.ScribeProgress!.Session!.Ended);
        var predecessor = closed.Artifact;
        if (foreignTarget)
        {
            var savedProgress = predecessor.State.ScribeProgress!;
            predecessor = CampaignStateJson.CreateArtifact(predecessor.State with
            {
                ScribeProgress = savedProgress with
                {
                    Members = savedProgress.Members.Select(member => member.WorkItemKey == scenario.Plan.WorkItems[2].WorkItemKey
                        ? member with { TargetId = "target.foreign" } : member).ToImmutableArray(),
                },
            });
            Assert.True(CampaignStateJson.Parse(predecessor.ExactUtf8Json.AsMemory()).IsValid);
        }
        exchanges = scenario.Plan.WorkItems.Select(work => CreateFreshContextExchange(work, scenario: scenario)).ToArray();
        var fresh = SharedRetryRequest(exchanges);
        Assert.NotEqual(request.Context.RepositoryContextRef, fresh.Context.RepositoryContextRef);
        Assert.Equal(request.ExecutableTargetIds.ToArray(), fresh.ExecutableTargetIds.ToArray());
        allowance = CampaignInvocationTestPolicy.Create(fresh.Limits);
        var retry = CampaignStateReducer.RetryProviderInvocation(predecessor, null, scenario.ExecutionAuthority, "style.synthetic",
            scenario.StyleProjection, scenario.Input, scenario.Plan, scenario.Plan.WorkItems[0].WorkItemKey, fresh,
            CampaignStateFactory.CreateInvocationTargetAllowance(predecessor.State, new(0)), allowance);
        Assert.Equal(CampaignTransitionKind.Applied, retry.Kind);
        CampaignStateFactory.ValidateSuccessorHistory(predecessor, retry.Artifact);
        var checkpoint = AcceptForTest(predecessor, retry);
        if (foreignTarget)
        {
            Assert.Throws<ArgumentException>(() => CampaignStateReducer.CreateProviderInvocationAuthority(checkpoint,
                scenario.ExecutionAuthority, "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan, fresh));
            Assert.Equal(predecessor.State.LineageCharges.ProviderRequests, retry.Artifact.State.LineageCharges.ProviderRequests);
            Assert.Equal(predecessor.State.DispatchObservations.ToArray(), retry.Artifact.State.DispatchObservations.ToArray());
            return;
        }
        invocation = CampaignStateReducer.CreateProviderInvocationAuthority(checkpoint, scenario.ExecutionAuthority,
            "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan, fresh);
        var evidence = exchanges.SelectMany(exchange => CurrentEvidence(exchange)).GroupBy(row => row.EvidenceReferenceId, StringComparer.Ordinal)
            .Select(group => group.First()).OrderBy(row => row.EvidenceReferenceId, StringComparer.Ordinal).ToImmutableArray();
        var restored = CampaignStateFactory.RestoreScribeProgress(invocation, evidence);
        Assert.NotNull(restored);
        Assert.Equal(fresh.Manifest[2].TargetId, Assert.Single(restored.Submissions).TargetId);
        Assert.Empty(restored.TargetFailures);
        Assert.Equal(1, restored.RepairCount);
        Assert.Equal(0, restored.RunEnvelope.ProviderRequestCount);
        Assert.Equal(0, allowance.ProviderRequestCount);
        store = new TransitionCheckpointStore(invocation.AcceptedCheckpoint.Artifact);
        coordinator = new(invocation, allowance, store, default, null, 0);
        Assert.True(invocation.TryBeginDispatch(out var freshAttempt));
        Assert.NotEqual(attempt, freshAttempt);
        var physical = await coordinator.Scope.ReserveProviderAsync(new(Sha256("fresh shared retry"), 1, 1, 7), default);
        Assert.NotNull(physical); Assert.True(coordinator.Scope.BeginPhysicalDispatch(physical));
        Assert.True(await coordinator.Scope.SettleProviderAsync(physical, new(DocumentationScribeDispatchDisposition.Success,
            new(0, 0, 0, 0, 0)), default));
        permit = await coordinator.Scope.BeginHostAsync(DocumentationScribeHostOperation.TerminalSubmission, default);
        Assert.NotNull(permit); Assert.True(permit.TryBeginOperation());
        var receipts = restored.Submissions.ToBuilder();
        foreach (var index in new[] { 1, 0 })
        {
            var submission = SharedRetrySubmission(fresh, exchanges[index], index);
            var transition = CampaignStateReducer.AcceptScribeSubmission(invocation, submission);
            Assert.Equal(CampaignTransitionKind.Applied, transition.Kind);
            Assert.True(await coordinator.AcceptHostProgressAsync(transition, default));
            receipts.Add(DocumentationScribeValidation.AcknowledgeSubmission(submission,
                new(fresh.ArtifactSha256, submission.TargetId, submission.ContentSha256, DocumentationScribeAcknowledgementStage.DurablyRecorded)));
        }
        var finished = DocumentationScribeValidation.CreateProgressNotification(fresh, freshAttempt, 1, 3,
            fresh.ExecutableTargetIds.Take(2), receipts, [], true, finished: true);
        Assert.True(await coordinator.AcceptHostProgressAsync(CampaignStateReducer.AcceptScribeProgress(invocation, finished), default));
        Assert.True(await coordinator.Scope.CompleteHostAsync(permit, default));
        var result = DocumentationScribeValidation.CreateSessionResult(fresh, freshAttempt,
            new DocumentationScribeFinishedTerminal(DocumentationScribeFinishReason.Completed),
            new(execution.ProviderConfigurationId, execution.ModelConfigurationId, execution.ScribeProtocolId,
                1, 1, 0, 1, 0, new(0, 0, 0, 0, 0), null, null, []), receipts.ToImmutable(), [], 1, 3);
        var outcome = DocumentationScribeValidation.BindValidatedRunOutcome(fresh, freshAttempt, result);
        registrar = invocation.TryCreateCompletionRegistrar();
        Assert.NotNull(registrar); Assert.True(registrar.TryRegister(CampaignProviderCompletionKind.Ordinary, outcome, 0, out completion));
        var completed = CampaignStateReducer.CompleteProviderInvocation(invocation.AcceptedCheckpoint.Artifact, completion!,
            scenario.ExecutionAuthority, "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan);
        Assert.Equal(CampaignTransitionKind.Applied, completed.Kind);
        Assert.Equal(new[] { 2, 2, 1 }, completed.Artifact.State.WorkItems.Select(work => work.OuterAttemptCount));
        Assert.All(completed.Artifact.State.WorkItems, work => Assert.Equal(CampaignWorkStatus.ProposalComplete, work.Status));
        Assert.Equal(2, completed.Artifact.State.LineageCharges.OuterInvocations);
        Assert.Equal(predecessor.State.LineageCharges.ProviderRequests.TotalCharged + 1, completed.Artifact.State.LineageCharges.ProviderRequests.TotalCharged);
        Assert.Equal(predecessor.State.DispatchObservations.ToArray(), completed.Artifact.State.DispatchObservations.Take(predecessor.State.DispatchObservations.Length).ToArray());
        Assert.True(completed.Artifact.State.ScribeProgress!.Session!.Ended);
        Assert.Null(completed.Artifact.State.ActiveReservation);
        Assert.True(CampaignStateJson.Parse(completed.Artifact.ExactUtf8Json.AsMemory()).IsValid);
    }

    private static DocumentationScribeValidatedSubmission SharedRetrySubmission(DocumentationScribeRequest request, ScribeExchange exchange, int index)
    {
        var source = exchange.Result.Submissions.Single().Validation;
        return DocumentationScribeValidation.ValidateTargetSubmission(request, request.Manifest[index].TargetId,
            source.DynamicEvidenceReferences, source.ResultUtf8Json.AsMemory()).Submission!;
    }

    private static DocumentationScribeRequest SharedRetryRequest(ScribeExchange[] exchanges)
    {
        var first = exchanges[0].Request;
        var node = JsonNode.Parse(CanonicalJson.Serialize(new
        {
            scribeRequestVersion = 1,
            first.Context,
            manifest = exchanges.Select((exchange, index) =>
            {
                var member = exchange.Request.Manifest.Single();
                return new DocumentationScribeManifestTarget("target." + index, member.Target, member.AuditOutcome, member.StyleProfile,
                    member.ContextReferenceIds, member.EvidenceReferenceIds, member.Grouping);
            }),
            executableTargetIds = Enumerable.Range(0, exchanges.Length).Select(index => "target." + index),
            first.ContextReferences,
            evidenceReferences = exchanges.SelectMany(exchange => exchange.Request.EvidenceReferences).OrderBy(row => row.EvidenceReferenceId, StringComparer.Ordinal),
            first.EvidenceConflicts,
            first.ToolPolicyId,
            first.Limits,
        }).AsSpan())!;
        foreach (var member in node["manifest"]!.AsArray())
            foreach (var component in member!["target"]!["applicableComponents"]!.AsArray().Where(component => component!["name"] is null))
                component!.AsObject().Remove("name");
        var parsed = DocumentationScribeValidation.ParseRequest(Encoding.UTF8.GetBytes(node.ToJsonString()));
        Assert.True(parsed.IsValid, parsed.Failure?.Code);
        return parsed.Request!;
    }
}
