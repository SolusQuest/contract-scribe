using System.Collections.Immutable;
using System.Text.Json.Nodes;
using ContractScribe.Agent.Prompting;
using ContractScribe.Cli;
using ContractScribe.Core;

namespace ContractScribe.Tests;

public sealed partial class CampaignStateContractTests
{
    [Fact]
    public void Unfinished_shared_session_uses_the_existing_retryable_outcome_category()
    {
        var outcome = new DocumentationCampaignProposalOutcome(
            DocumentationCampaignProposalOutcomeKind.RetryableStop, "campaign.session.pending");
        Assert.Equal(DocumentationCampaignProposalOutcomeKind.RetryableStop, outcome.Kind);
        Assert.Equal("campaign.session.pending", outcome.Code);
        Assert.Throws<ArgumentException>(() => new DocumentationCampaignProposalOutcome(
            DocumentationCampaignProposalOutcomeKind.ProposalReady, "campaign.session.pending"));
    }

    [Theory]
    [InlineData(false, false, false, false, null, null)]
    [InlineData(true, false, false, false, null, null)]
    [InlineData(false, true, false, false, null, null)]
    [InlineData(true, true, false, false, null, null)]
    [InlineData(false, false, true, false, null, null)]
    [InlineData(false, false, false, true, null, null)]
    [InlineData(false, false, true, false, "ProposalInvalid", null)]
    [InlineData(false, false, true, false, "BudgetExhausted", null)]
    [InlineData(false, false, true, false, "Timeout", null)]
    [InlineData(false, false, true, false, "HostFailure", null)]
    [InlineData(false, false, true, false, "CallerCancelled", null)]
    [InlineData(false, false, true, false, "ShutdownCancelled", null)]
    [InlineData(false, false, false, false, null, "equality")]
    [InlineData(false, false, false, false, null, "overrun")]
    [InlineData(false, false, false, false, null, "elapsed")]
    [InlineData(false, false, false, false, null, "finished-subset")]
    [InlineData(false, false, true, false, "TimeoutNoSibling", null)]
    public async Task One_shared_session_durably_accepts_a_non_anchor_member_and_preserves_the_live_grant(bool skipSecond, bool freshResume, bool unconfirmedHost, bool projectionCapacity, string? hostRejection, string? pauseBoundary)
    {
        var noSibling = hostRejection == "TimeoutNoSibling";
        var subset = pauseBoundary == "finished-subset";
        var scenario = CreateProposalScenario(workItemCount: subset ? 3 : 2, targetLimit: subset ? 3 : 2, maximumElapsedMilliseconds: pauseBoundary == "elapsed" || hostRejection is not null ? 1000 : null,
            maximumProviderRequests: pauseBoundary == "equality" ? 1 : 100, maximumOutputTokens: pauseBoundary == "overrun" ? 7 : 100000, maximumBlocks: projectionCapacity ? 1 : 100);
        var exchanges = scenario.Plan.WorkItems.Select(work => skipSecond && work == scenario.Plan.WorkItems[1]
            ? CreateNonProposalExchange(work, scenario) : CreateScribeExchange(work, scenario: scenario)).ToArray();
        var first = exchanges[0].Request;
        var members = exchanges.Select((exchange, index) =>
        {
            var member = exchange.Request.Manifest.Single();
            return new DocumentationScribeManifestTarget("target." + index, member.Target, member.AuditOutcome, member.StyleProfile,
                member.ContextReferenceIds, member.EvidenceReferenceIds, member.Grouping);
        }).ToImmutableArray();
        var bytes = CanonicalJson.Serialize(new
        {
            scribeRequestVersion = 1,
            first.Context,
            manifest = members,
            executableTargetIds = members.Take(2).Select(member => member.TargetId),
            first.ContextReferences,
            evidenceReferences = exchanges.SelectMany(exchange => exchange.Request.EvidenceReferences).OrderBy(reference => reference.EvidenceReferenceId, StringComparer.Ordinal),
            first.EvidenceConflicts,
            first.ToolPolicyId,
            first.Limits,
        });
        var nodeRequest = JsonNode.Parse(bytes.AsSpan())!;
        foreach (var memberNode in nodeRequest["manifest"]!.AsArray())
            foreach (var component in memberNode!["target"]!["applicableComponents"]!.AsArray().Where(component => component!["name"] is null))
                component!.AsObject().Remove("name");
        var parsedRequest = DocumentationScribeValidation.ParseRequest(System.Text.Encoding.UTF8.GetBytes(nodeRequest.ToJsonString()));
        Assert.True(parsedRequest.IsValid, parsedRequest.Failure?.Code + " " + parsedRequest.Failure?.Pointer);
        var request = parsedRequest.Request!;
        var initial = CampaignStateJson.CreateArtifact(scenario.InitialState);
        var store = new TransitionCheckpointStore(initial);
        var allowance = pauseBoundary == "overrun" ? new DocumentationScribeInvocationAllowance(DocumentationScribeInvocationLimits.Create(
            maximumProviderRequests: request.Limits.MaximumProviderRequests, maximumToolCalls: request.Limits.MaximumToolCalls,
            maximumCachedInputTokens: request.Limits.MaximumInputTokens / 2,
            maximumUncachedInputTokens: Math.Min(request.Limits.MaximumUncachedInputTokens, request.Limits.MaximumInputTokens / 2),
            maximumOutputTokens: request.Limits.MaximumOutputTokens, maximumRequestOutputTokens: 7,
            maximumElapsedMilliseconds: request.Limits.MaximumElapsedMilliseconds,
            maximumRequestElapsedMilliseconds: Math.Min(300000, request.Limits.MaximumElapsedMilliseconds)), new C4CampaignClock())
            : CampaignInvocationTestPolicy.Create(request.Limits, new C4CampaignClock());
        var admitted = CampaignStateReducer.AdmitProviderInvocation(initial, scenario.ExecutionAuthority, "style.synthetic", scenario.StyleProjection,
            scenario.Input, scenario.Plan, scenario.Plan.WorkItems[0].WorkItemKey, request,
            CampaignStateFactory.CreateInvocationTargetAllowance(initial.State, new(2)), allowance);
        Assert.Equal(CampaignTransitionKind.Applied, admitted.Kind);
        var accepted = await CampaignCheckpointAcceptance.AcceptAsync(store, admitted);
        var invocation = CampaignStateReducer.CreateProviderInvocationAuthority(accepted.AcceptedCheckpoint!, scenario.ExecutionAuthority,
            "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan, request);
        var coordinator = new CampaignScribeExecutionCoordinator(invocation, allowance, store, default, null, 0);
        Assert.True(invocation.TryBeginDispatch(out var attempt));
        var physical = await coordinator.Scope.ReserveProviderAsync(new(Sha256("one shared physical request"), 1, 1,
            pauseBoundary == "overrun" ? 7 : request.Limits.MaximumOutputTokens), default);
        Assert.NotNull(physical);
        Assert.True(coordinator.Scope.BeginPhysicalDispatch(physical));
        Assert.True(await coordinator.Scope.SettleProviderAsync(physical,
            new(DocumentationScribeDispatchDisposition.Success, new(0, 0, 0, pauseBoundary == "overrun" ? 8 : 0, 0)), default));
        var charges = invocation.AcceptedCheckpoint.Artifact.State.LineageCharges;
        var permit = await coordinator.Scope.BeginHostAsync(DocumentationScribeHostOperation.TerminalSubmission, default);
        Assert.NotNull(permit); Assert.True(permit.TryBeginOperation());
        var receipts = ImmutableArray.CreateBuilder<DocumentationScribeTargetSubmission>();
        foreach (var index in new[] { 1, 0 })
        {
            var source = exchanges[index].Result.Submissions.Single().Validation;
            var validation = DocumentationScribeValidation.ValidateTargetSubmission(request, members[index].TargetId,
                source.DynamicEvidenceReferences, source.ResultUtf8Json.AsMemory());
            Assert.NotNull(validation.Submission);
            if (unconfirmedHost && index == (noSibling ? 1 : 0))
            {
                var rejectedKind = hostRejection is null ? (CampaignProviderCompletionKind?)null
                    : Enum.Parse<CampaignProviderCompletionKind>(noSibling ? "Timeout" : hostRejection);
                if (rejectedKind is { } kind)
                {
                    Assert.True(invocation.TryRecordHostRejection(validation.Submission, kind));
                    Assert.True(invocation.TryRecordHostRejection(validation.Submission, kind));
                    Assert.False(invocation.TryRecordHostRejection(validation.Submission, CampaignProviderCompletionKind.Ordinary));
                    Assert.False(invocation.TryRecordHostRejection(validation.Submission,
                        kind == CampaignProviderCompletionKind.Timeout ? CampaignProviderCompletionKind.HostFailure : CampaignProviderCompletionKind.Timeout));
                }
                receipts.Add(DocumentationScribeValidation.AcknowledgeSubmission(validation.Submission,
                    new(request.ArtifactSha256, members[index].TargetId, validation.Submission.ContentSha256,
                        DocumentationScribeAcknowledgementStage.Received)));
                if (!noSibling)
                {
                    var stoppedProgress = DocumentationScribeValidation.CreateProgressNotification(request, attempt, 0, 1,
                        request.ExecutableTargetIds, receipts, [], true);
                    var observedStop = CampaignStateReducer.AcceptScribeProgress(invocation, stoppedProgress);
                    Assert.Equal(CampaignTransitionKind.Applied, observedStop.Kind);
                    Assert.True(await coordinator.AcceptHostProgressAsync(observedStop, default));
                }
                Assert.Null(invocation.AcceptedCheckpoint.Artifact.State.ScribeProgress!.Members.SingleOrDefault(member => member.TargetId == members[index].TargetId)?.ContentSha256);
                Assert.True(await coordinator.Scope.CompleteHostAsync(permit, default));
                var execution = scenario.ExecutionAuthority.Projection;
                var result = DocumentationScribeValidation.CreateSessionResult(request, attempt,
                    new DocumentationScribeFailureTerminal(DocumentationScribeFailureCode.Internal, null),
                    new(execution.ProviderConfigurationId, execution.ModelConfigurationId, execution.ScribeProtocolId,
                        1, 1, 0, 1, 0, null, null, null, []), receipts.ToImmutable(), [], 0, 1);
                var outcome = DocumentationScribeValidation.BindValidatedRunOutcome(request, attempt, result);
                var registrar = invocation.TryCreateCompletionRegistrar();
                Assert.NotNull(registrar);
                Assert.True(registrar.TryRegister(CampaignProviderCompletionKind.Ordinary, outcome, hostRejection is null ? 0 : 1001, out var completion));
                var completed = CampaignStateReducer.CompleteProviderInvocation(invocation.AcceptedCheckpoint.Artifact, completion!,
                    scenario.ExecutionAuthority, "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan);
                Assert.Equal(CampaignTransitionKind.Applied, completed.Kind);
                if (!noSibling)
                    Assert.Equal(CampaignWorkStatus.ProposalComplete, completed.Artifact.State.WorkItems[1].Status);
                else
                {
                    Assert.All(completed.Artifact.State.WorkItems, work => Assert.Null(work.TrustedProposal));
                    Assert.True(completed.Artifact.State.ScribeProgress!.Session!.Ended);
                    Assert.Null(invocation.AcceptedCheckpoint.Artifact.State.ScribeProgress!.LastProgressSha256);
                }
                Assert.Equal(rejectedKind switch
                {
                    CampaignProviderCompletionKind.ProposalInvalid => CampaignWorkOutcomeCode.ValidationFailure,
                    CampaignProviderCompletionKind.BudgetExhausted => CampaignWorkOutcomeCode.BudgetExhausted,
                    CampaignProviderCompletionKind.Timeout => CampaignWorkOutcomeCode.Timeout,
                    CampaignProviderCompletionKind.CallerCancelled => CampaignWorkOutcomeCode.CancelledByCaller,
                    CampaignProviderCompletionKind.ShutdownCancelled => CampaignWorkOutcomeCode.CancelledByShutdown,
                    _ => CampaignWorkOutcomeCode.InternalFailure,
                }, completed.Artifact.State.WorkItems[index].ClosedOutcome!.Code);
                if (rejectedKind is { } consumedKind)
                {
                    Assert.False(invocation.TryRecordHostRejection(validation.Submission, consumedKind));
                    Assert.Equal(1001, completed.Artifact.State.LineageCharges.ActiveElapsedMilliseconds.Observed);
                    Assert.Equal(consumedKind switch
                    {
                        CampaignProviderCompletionKind.ProposalInvalid => CampaignTerminalReason.LifetimeCap,
                        CampaignProviderCompletionKind.BudgetExhausted => CampaignTerminalReason.Budget,
                        CampaignProviderCompletionKind.Timeout => CampaignTerminalReason.Deadline,
                        CampaignProviderCompletionKind.CallerCancelled => CampaignTerminalReason.Caller,
                        _ => CampaignTerminalReason.Host,
                    }, completed.Artifact.State.TerminalOutcome!.Reason);
                }
                Assert.Null(completed.Artifact.State.WorkItems[index].TrustedProposal);
                Assert.Equal(charges.ProviderRequests, completed.Artifact.State.LineageCharges.ProviderRequests);
                Assert.Equal(charges.InputTokens, completed.Artifact.State.LineageCharges.InputTokens);
                Assert.Null(completed.Artifact.State.ActiveReservation);
                return;
            }
            var transition = CampaignStateReducer.AcceptScribeSubmission(invocation, validation.Submission);
            Assert.Equal(CampaignTransitionKind.Applied, transition.Kind);
            Assert.True(await coordinator.AcceptHostProgressAsync(transition, default));
            var state = invocation.AcceptedCheckpoint.Artifact.State;
            if (pauseBoundary is "equality" or "overrun" or "elapsed")
            {
                Assert.True(await coordinator.Scope.CompleteHostAsync(permit, default));
                var paused = CampaignStateReducer.PauseProviderInvocation(invocation,
                    pauseBoundary == "elapsed" ? 1000 : 0, lifetimeStop: true);
                Assert.Equal(CampaignTransitionKind.Applied, paused.Kind);
                Assert.Equal(CampaignWorkStatus.ProposalComplete, paused.Artifact.State.WorkItems[1].Status);
                Assert.Null(paused.Artifact.State.ActiveReservation);
                Assert.Equal(charges.ProviderRequests, paused.Artifact.State.LineageCharges.ProviderRequests);
                Assert.Equal(charges.OutputTokens, paused.Artifact.State.LineageCharges.OutputTokens);
                if (pauseBoundary == "equality") Assert.Null(paused.Artifact.State.TerminalOutcome);
                else Assert.Equal(CampaignTerminalReason.LifetimeCap, paused.Artifact.State.TerminalOutcome!.Reason);
                Assert.True(CampaignStateJson.Parse(paused.Artifact.ExactUtf8Json.AsMemory()).IsValid);
                return;
            }
            if (projectionCapacity && index == 0)
            {
                Assert.Equal(CampaignWorkOutcomeCode.CompletedOverBound, state.WorkItems[0].ClosedOutcome!.Code);
                Assert.Null(state.WorkItems[0].TrustedProposal);
                Assert.Equal(CampaignWorkStatus.ProposalComplete, state.WorkItems[1].Status);
                Assert.True(await coordinator.Scope.CompleteHostAsync(permit, default));
                var paused = CampaignStateReducer.PauseProviderInvocation(invocation, coordinator.CurrentElapsed,
                    lifetimeStop: false, projectionCapacityStop: true);
                Assert.Equal(CampaignTransitionKind.Applied, paused.Kind);
                Assert.Null(paused.Artifact.State.TerminalOutcome);
                Assert.Null(paused.Artifact.State.ActiveReservation);
                Assert.Equal(CampaignWorkStatus.ProposalComplete, paused.Artifact.State.WorkItems[1].Status);
                Assert.Equal(charges.ProviderRequests, paused.Artifact.State.LineageCharges.ProviderRequests);
                Assert.Equal(charges.InputTokens, paused.Artifact.State.LineageCharges.InputTokens);
                Assert.True(CampaignStateJson.Parse(paused.Artifact.ExactUtf8Json.AsMemory()).IsValid);
                return;
            }
            Assert.IsType<CampaignProviderReservation>(state.ActiveReservation);
            Assert.True(invocation.DispatchStarted);
            Assert.Equal(charges.ProviderRequests, state.LineageCharges.ProviderRequests);
            Assert.Equal(charges.InputTokens, state.LineageCharges.InputTokens);
            Assert.Equal(charges.OutputTokens, state.LineageCharges.OutputTokens);
            var receipt = DocumentationScribeValidation.AcknowledgeSubmission(validation.Submission,
                new(request.ArtifactSha256, members[index].TargetId, validation.Submission.ContentSha256,
                    DocumentationScribeAcknowledgementStage.DurablyRecorded));
            receipts.Add(receipt);
            var replay = CampaignStateReducer.AcceptScribeSubmission(invocation, validation.Submission);
            Assert.Equal(CampaignTransitionKind.Unchanged, replay.Kind);
            Assert.True(await coordinator.AcceptHostProgressAsync(replay, default));
            if (freshResume && index == 1)
            {
                var invalidBatch = DocumentationScribeValidation.CreateProgressNotification(request, attempt, 1, 2,
                    members.Select(member => member.TargetId), receipts, [new(members[0].TargetId, "scribe.result.invalid-content")], false);
                Assert.True(await coordinator.AcceptHostProgressAsync(CampaignStateReducer.AcceptScribeProgress(invocation, invalidBatch), default));
                Assert.True(await coordinator.Scope.CompleteHostAsync(permit, default));
                var current = await CampaignCheckpointAcceptance.AcceptCurrentAsync(store);
                var retired = await CampaignCheckpointAcceptance.AcceptAsync(store,
                    CampaignStateReducer.RetireInterruptedProviderAttempt(current.AcceptedCheckpoint!));
                Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted, retired.Kind);
                var oldRequest = request;
                exchanges = scenario.Plan.WorkItems.Select(work => skipSecond && work == scenario.Plan.WorkItems[1]
                    ? CreateNonProposalExchange(work, scenario: scenario, freshContext: true)
                    : CreateFreshContextExchange(work, scenario: scenario)).ToArray();
                var freshContext = exchanges[0].Request.Context.RepositoryContextRef.Value;
                var freshBytes = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(bytes.AsSpan())
                    .Replace(oldRequest.Context.RepositoryContextRef.Value, freshContext, StringComparison.Ordinal));
                var freshNode = JsonNode.Parse(freshBytes)!;
                foreach (var memberNode in freshNode["manifest"]!.AsArray())
                    foreach (var component in memberNode!["target"]!["applicableComponents"]!.AsArray().Where(component => component!["name"] is null))
                        component!.AsObject().Remove("name");
                request = DocumentationScribeValidation.ParseRequest(System.Text.Encoding.UTF8.GetBytes(freshNode.ToJsonString())).Request!;
                allowance = CampaignInvocationTestPolicy.Create(request.Limits, new C4CampaignClock());
                var resumed = CampaignStateReducer.RetryProviderInvocation(retired.Artifact!, retired.AcceptedCheckpoint!,
                    scenario.ExecutionAuthority, "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan,
                    scenario.Plan.WorkItems[0].WorkItemKey, request,
                    CampaignStateFactory.CreateInvocationTargetAllowance(retired.Artifact!.State, new(0)), allowance);
                Assert.Equal(CampaignTransitionKind.Applied, resumed.Kind);
                var resumeAcceptance = await CampaignCheckpointAcceptance.AcceptAsync(store, resumed);
                Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted, resumeAcceptance.Kind);
                var resumedClaim = Assert.IsType<CampaignProviderReservation>(resumeAcceptance.Artifact!.State.ActiveReservation);
                Assert.Equal(resumeAcceptance.Artifact.CheckpointRevision, resumedClaim.ExecutionStartRevision);
                Assert.Equal(request.ArtifactSha256, resumedClaim.ScribeRequestSha256);
                invocation = CampaignStateReducer.CreateProviderInvocationAuthority(resumeAcceptance.AcceptedCheckpoint!, scenario.ExecutionAuthority,
                    "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan, request);
                coordinator = new(invocation, allowance, store, default, null, 0);
                var currentEvidence = exchanges.SelectMany(exchange => exchange.Request.EvidenceReferences.Concat(exchange.Result.DynamicEvidenceReferences))
                    .GroupBy(reference => reference.EvidenceReferenceId, StringComparer.Ordinal).Select(group => group.First()).ToImmutableArray();
                var restoredProgress = CampaignStateFactory.RestoreScribeProgress(invocation, currentEvidence);
                Assert.NotNull(restoredProgress);
                Assert.Single(restoredProgress.Submissions);
                Assert.Equal(1, restoredProgress.RepairCount);
                Assert.Equal(2, restoredProgress.NextSequence);
                Assert.Equal(members[0].TargetId, Assert.Single(restoredProgress.TargetFailures).TargetId);
                Assert.Equal(members[1].TargetId, restoredProgress.Submissions[0].TargetId);
                Assert.Equal(0, restoredProgress.RunEnvelope.ProviderRequestCount);
                Assert.Equal(0, allowance.ProviderRequestCount);
                Assert.Equal(charges.ProviderRequests, invocation.AcceptedCheckpoint.Artifact.State.LineageCharges.ProviderRequests);
                receipts.Clear(); receipts.AddRange(restoredProgress.Submissions);
                Assert.True(invocation.TryBeginDispatch(out var resumedAttempt));
                Assert.Equal(attempt, resumedAttempt);
                permit = await coordinator.Scope.BeginHostAsync(DocumentationScribeHostOperation.TerminalSubmission, default);
                Assert.NotNull(permit); Assert.True(permit.TryBeginOperation());
            }
        }
        var progress = DocumentationScribeValidation.CreateProgressNotification(request, attempt, 1, 2,
            request.ExecutableTargetIds, receipts, [], false);
        var recorded = CampaignStateReducer.AcceptScribeProgress(invocation, progress);
        Assert.Equal(CampaignTransitionKind.Applied, recorded.Kind);
        Assert.True(await coordinator.AcceptHostProgressAsync(recorded, default));
        Assert.True(await coordinator.Scope.CompleteHostAsync(permit, default));
        if (subset)
        {
            var execution = scenario.ExecutionAuthority.Projection;
            var result = DocumentationScribeValidation.CreateSessionResult(request, attempt,
                new DocumentationScribeFinishedTerminal(DocumentationScribeFinishReason.Unresolved),
                new(execution.ProviderConfigurationId, execution.ModelConfigurationId, execution.ScribeProtocolId,
                    1, 1, 0, 1, 0, null, null, null, []), receipts.ToImmutable(), [], 1, 2);
            var outcome = DocumentationScribeValidation.BindValidatedRunOutcome(request, attempt, result);
            var registrar = invocation.TryCreateCompletionRegistrar();
            Assert.NotNull(registrar);
            Assert.True(registrar.TryRegister(CampaignProviderCompletionKind.Ordinary, outcome, 0, out var completion));
            var completed = CampaignStateReducer.CompleteProviderInvocation(invocation.AcceptedCheckpoint.Artifact, completion!,
                scenario.ExecutionAuthority, "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan);
            Assert.Equal(CampaignTransitionKind.Applied, completed.Kind);
            Assert.True(completed.Artifact.State.ScribeProgress!.Session!.Ended);
            Assert.All(completed.Artifact.State.WorkItems.Take(2), work =>
            {
                Assert.Equal(CampaignWorkStatus.ProposalComplete, work.Status);
                Assert.Null(work.PausedProviderAttempt);
            });
            Assert.Equal(CampaignWorkStatus.Planned, completed.Artifact.State.WorkItems[2].Status);
            Assert.Equal(0, completed.Artifact.State.WorkItems[2].OuterAttemptCount);
            Assert.Null(completed.Artifact.State.TerminalOutcome);
            Assert.Equal(charges.ProviderRequests, completed.Artifact.State.LineageCharges.ProviderRequests);
            Assert.Equal(charges.InputTokens, completed.Artifact.State.LineageCharges.InputTokens);
            Assert.Null(completed.Artifact.State.ActiveReservation);
            Assert.True(CampaignStateJson.Parse(completed.Artifact.ExactUtf8Json.AsMemory()).IsValid);
            return;
        }
        var artifact = invocation.AcceptedCheckpoint.Artifact;
        using var schemaDocument = System.Text.Json.JsonDocument.Parse(artifact.ExactUtf8Json.AsMemory());
        var schemaResult = EvaluateCampaignSchema(schemaDocument.RootElement);
        Assert.True(schemaResult.IsValid, DescribeSchemaFailures(schemaResult));
        var restored = CampaignStateJson.Parse(artifact.ExactUtf8Json.AsMemory());
        Assert.True(restored.IsValid, restored.FailureCode?.ToString());
        var saved = restored.Artifact!.State;
        Assert.Throws<CampaignStateValidationException>(() => CampaignStateFactory.ValidateCurrentTrustedProposalAuthority(saved,
            saved.WorkItems[0] with { OuterAttemptCount = saved.WorkItems[0].OuterAttemptCount + 1 }, scenario.Plan.WorkItems[0]));
        Assert.Equal(1, saved.ScribeProgress!.RepairCount);
        Assert.Equal(2, saved.ScribeProgress.NextSequence);
        Assert.Equal(2, saved.ScribeProgress.Members.Length);
        Assert.Empty(saved.ScribeProgress.TargetFailures);
        Assert.All(saved.ScribeProgress.Members, member => Assert.Equal(attempt, member.AttemptId));
        Assert.All(saved.WorkItems, work => Assert.Equal(1, work.OuterAttemptCount));
        Assert.Equal(1, saved.LineageCharges.OuterInvocations);
        Assert.Equal(1, saved.LineageCharges.ProviderRequests.TotalCharged);
        Assert.Single(saved.DispatchObservations);
        Assert.NotEqual(CampaignStateFactory.CreateScribeAttemptId(saved.Snapshot.ExecutionCommitmentSha256,
            scenario.ExecutionAuthority.Projection, scenario.Plan.WorkItems[1].WorkItemKey, 1), attempt);
        if (!skipSecond)
            CampaignStateFactory.ValidateCurrentTrustedProposalAuthority(saved, saved.WorkItems[1], scenario.Plan.WorkItems[1]);
        else Assert.NotNull(saved.ScribeProgress.Members[1].Skip);
        foreach (var mutation in new Action<JsonNode>[]
        {
            node => node["scribeProgress"]!["members"]![1]!["attemptId"] = CampaignStateFactory.CreateScribeAttemptId(saved.Snapshot.ExecutionCommitmentSha256,
                scenario.ExecutionAuthority.Projection, scenario.Plan.WorkItems[1].WorkItemKey, 1).Value,
            node => node["scribeProgress"]!["members"]![1]!["outerAttemptOrdinal"] = 2,
            node => node["scribeProgress"]!["members"]![1]!["sessionAnchorWorkItemKey"] = scenario.Plan.WorkItems[1].WorkItemKey,
        })
        {
            var node = JsonNode.Parse(artifact.ExactUtf8Json.AsSpan())!;
            mutation(node);
            Assert.False(CampaignStateJson.Parse(System.Text.Encoding.UTF8.GetBytes(node.ToJsonString())).IsValid);
        }
    }
}
