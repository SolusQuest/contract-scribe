using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Agent.Runtime;
using ContractScribe.Cli;
using ContractScribe.Core;
using ContractScribe.Patching;
using ContractScribe.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ContractScribe.Tests;

// CampaignProcessBoundaryHooks has one process-wide observer; any parallel campaign
// execution would otherwise be indistinguishable from the registering test's flow.
[CollectionDefinition("Campaign process boundary hook", DisableParallelization = true)]
public sealed class CampaignProcessBoundaryHookCollection;

[Collection("Campaign process boundary hook")]
public sealed partial class DocumentationScribeCompositionTests
{
    [Fact]
    public async Task Production_prepare_and_consume_preserve_the_exact_bound_outcome()
    {
        await using var fixture = await CompositionFixture.CreateAsync();
        var prepared = await PrepareAsync(fixture, new ProposalExchange(fixture.Request));
        Assert.True(prepared.IsProposalReady);
        var bound = Assert.IsType<DocumentationScribeValidatedRunOutcome>(prepared.M3Outcome);
        Assert.Same(bound.Request, prepared.M3Outcome!.Request);
        Assert.Same(bound.RunResult, prepared.M3Outcome.RunResult);
        Assert.DoesNotContain(
            typeof(IDocumentationScribePreparedOutcome).GetProperties(),
            property => property.PropertyType == typeof(DocumentationPatchRequest));
        Assert.True(prepared.GetType().IsNestedPrivate);

        var consumed = DocumentationScribeComposition.ConsumePreparedOutcome(fixture.SelectedAudit, prepared);
        Assert.Same(bound, consumed.M3Outcome);
        Assert.Same(bound.RunResult, consumed.RunResult);
        var patchRequest = Assert.IsType<DocumentationPatchRequest>(consumed.PatchRequest);
        Assert.NotNull(consumed.PatchOutcome);
        Assert.Equal(bound.Request.Context.RepositoryContextRef, patchRequest.Context.RepositoryContextRef);
        Assert.Equal(bound.Request.Context.InputIdentity, patchRequest.Context.InputIdentity);
        Assert.Equal(bound.Request.Context.TargetProfile, patchRequest.Context.TargetProfile);
        var block = Assert.Single(patchRequest.Blocks);
        Assert.Equal(bound.Request.Target.SymbolRef, block.SymbolRef);
        var boundLocator = Assert.IsType<RepositoryEvidenceLocator>(bound.Request.Target.SourceLocator);
        var patchLocator = Assert.IsType<DocumentationPatchRepositoryLocator>(block.Locator);
        Assert.Equal(boundLocator.Path, patchLocator.Path);
        Assert.Equal(boundLocator.Span, patchLocator.DeclarationSpan);
        Assert.Equal(bound.Request.Target.SourceSha256, patchLocator.OriginalFileSha256);
        Assert.Equal(DocumentationPatchEditKind.Insert, block.EditKind);
        Assert.Equal(
            bound.Request.Target.ApplicableComponents.Select(component => component.Identity),
            block.ApplicableComponents.Select(component => component.Identity));
        Assert.Equal(new[] { "evidence.source" }, patchRequest.ProvenanceCatalog.ToArray());
        var content = Assert.IsType<DocumentationPatchStructuredContent>(block.Content);
        Assert.Equal(new[] { "Runs the selected operation." }, content.SummaryLines.ToArray());

        var consumedAgain = DocumentationScribeComposition.ConsumePreparedOutcome(fixture.SelectedAudit, prepared);
        Assert.Equal(DocumentationScribeCompositionStatus.ProposalRejected, consumedAgain.Status);
        Assert.Equal("scribe.proposal.already-consumed", consumedAgain.Code);
        Assert.Same(bound, consumedAgain.M3Outcome);
        Assert.Null(consumedAgain.PatchRequest);
        Assert.Null(consumedAgain.PatchOutcome);

        foreach (var scenario in new (IDocumentationScribeModelExchange Exchange, DocumentationScribeCompositionStatus Status)[]
                 {
                     (new SkipExchange(), DocumentationScribeCompositionStatus.ProposalSkipped),
                     (new ProviderFailureExchange(), DocumentationScribeCompositionStatus.ProviderFailure),
                     (new ProtocolFailureExchange(), DocumentationScribeCompositionStatus.RuntimeFailure),
                 })
        {
            var closed = await PrepareAsync(fixture, scenario.Exchange);
            Assert.Equal(scenario.Status, closed.Status);
            var closedBound = Assert.IsType<DocumentationScribeValidatedRunOutcome>(closed.M3Outcome);
            var closedOutcome = DocumentationScribeComposition.ConsumePreparedOutcome(fixture.SelectedAudit, closed);
            Assert.Same(closedBound, closedOutcome.M3Outcome);
            Assert.Same(closedBound.Request, closedOutcome.M3Outcome!.Request);
            Assert.Same(closedBound.RunResult, closedOutcome.RunResult);
            Assert.Null(closedOutcome.PatchRequest);
            Assert.Null(closedOutcome.PatchOutcome);
        }

        var budgetBytes = WithLimit(fixture.RequestBytes, "maximumOutputTokens", 1);
        var budget = await DocumentationScribeComposition.PrepareAsync(
            fixture.SelectedAudit,
            budgetBytes,
            fixture.AttemptId,
            configuredAgentEntrypoint: null,
            RuntimeOptions(),
            new ObservedSkipExchange(new DocumentationScribeModelUsage(outputTokens: 2)));
        Assert.Equal(DocumentationScribeCompositionStatus.BudgetExhausted, budget.Status);
        Assert.NotNull(budget.M3Outcome);
        Assert.Null(DocumentationScribeComposition.ConsumePreparedOutcome(
            fixture.SelectedAudit, budget).PatchRequest);

        var timeoutBytes = WithLimit(fixture.RequestBytes, "maximumElapsedMilliseconds", 1);
        var timeout = await DocumentationScribeComposition.PrepareAsync(
            fixture.SelectedAudit,
            timeoutBytes,
            fixture.AttemptId,
            configuredAgentEntrypoint: null,
            RuntimeOptions(),
            new DelayedSkipExchange());
        Assert.Equal(DocumentationScribeCompositionStatus.Timeout, timeout.Status);
        Assert.NotNull(timeout.M3Outcome);
        Assert.Null(DocumentationScribeComposition.ConsumePreparedOutcome(
            fixture.SelectedAudit, timeout).PatchRequest);

        using var callerCancellation = new CancellationTokenSource();
        var cancelled = await PrepareAsync(
            fixture,
            new CancellingExchange(callerCancellation),
            callerCancellation.Token);
        Assert.Equal(DocumentationScribeCompositionStatus.Cancelled, cancelled.Status);
        Assert.NotNull(cancelled.M3Outcome);
        Assert.Null(DocumentationScribeComposition.ConsumePreparedOutcome(
            fixture.SelectedAudit, cancelled).PatchRequest);

        var execute = await DocumentationScribeComposition.ExecuteAsync(
            fixture.SelectedAudit,
            fixture.RequestBytes,
            fixture.AttemptId,
            configuredAgentEntrypoint: null,
            RuntimeOptions(),
            new SkipExchange());
        Assert.Equal(DocumentationScribeCompositionStatus.ProposalSkipped, execute.Status);
        Assert.NotNull(execute.M3Outcome);
        Assert.Null(execute.PatchRequest);
        Assert.Null(execute.PatchOutcome);
    }

    [Fact]
    public async Task Pre_agent_and_post_bind_closures_never_mint_patch_authority()
    {
        await using var fixture = await CompositionFixture.CreateAsync();
        var counting = new CountingExchange();
        var invalid = await DocumentationScribeComposition.PrepareAsync(
            fixture.SelectedAudit,
            "{}"u8.ToArray(),
            fixture.AttemptId,
            configuredAgentEntrypoint: null,
            RuntimeOptions(),
            counting);
        Assert.Equal(DocumentationScribeCompositionStatus.PreflightRejected, invalid.Status);
        Assert.Null(invalid.M3Outcome);
        Assert.False(invalid.IsProposalReady);
        Assert.Equal(0, counting.RequestCount);

        var original = await File.ReadAllTextAsync(fixture.SourcePath);
        var stalePrepared = await PrepareAsync(
            fixture,
            new MutatingProposalExchange(fixture.Request, fixture.SourcePath));
        var staleBound = Assert.IsType<DocumentationScribeValidatedRunOutcome>(stalePrepared.M3Outcome);
        Assert.Equal(DocumentationScribeCompositionStatus.PatchStale, stalePrepared.Status);
        var staleOutcome = DocumentationScribeComposition.ConsumePreparedOutcome(fixture.SelectedAudit, stalePrepared);
        Assert.Same(staleBound, staleOutcome.M3Outcome);
        Assert.Null(staleOutcome.PatchRequest);
        Assert.Null(staleOutcome.PatchOutcome);
        await File.WriteAllTextAsync(fixture.SourcePath, original, new UTF8Encoding(false));

        var sourceSubstitution = await PrepareAsync(fixture, new ProposalExchange(fixture.Request));
        var sourceBound = Assert.IsType<DocumentationScribeValidatedRunOutcome>(sourceSubstitution.M3Outcome);
        await File.AppendAllTextAsync(fixture.SourcePath, Environment.NewLine, new UTF8Encoding(false));
        var sourceRejected = DocumentationScribeComposition.ConsumePreparedOutcome(
            fixture.SelectedAudit,
            sourceSubstitution);
        Assert.Equal(DocumentationScribeCompositionStatus.PatchStale, sourceRejected.Status);
        Assert.Equal("scribe.patch.prepared-authority-mismatch", sourceRejected.Code);
        Assert.Same(sourceBound, sourceRejected.M3Outcome);
        Assert.Null(sourceRejected.PatchRequest);
        Assert.Null(sourceRejected.PatchOutcome);
        await File.WriteAllTextAsync(fixture.SourcePath, original, new UTF8Encoding(false));

        var selectionSubstitution = await PrepareAsync(fixture, new ProposalExchange(fixture.Request));
        var selectionBound = Assert.IsType<DocumentationScribeValidatedRunOutcome>(selectionSubstitution.M3Outcome);
        var selectionRejected = DocumentationScribeComposition.ConsumePreparedOutcome(
            fixture.NonMethodSelectedAudit!,
            selectionSubstitution);
        Assert.Equal(DocumentationScribeCompositionStatus.PatchStale, selectionRejected.Status);
        Assert.Same(selectionBound, selectionRejected.M3Outcome);
        Assert.Null(selectionRejected.PatchRequest);
        Assert.Null(selectionRejected.PatchOutcome);

        var cancelledPrepared = await PrepareAsync(fixture, new ProposalExchange(fixture.Request));
        var cancelledBound = Assert.IsType<DocumentationScribeValidatedRunOutcome>(cancelledPrepared.M3Outcome);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = DocumentationScribeComposition.ConsumePreparedOutcome(
            fixture.SelectedAudit,
            cancelledPrepared,
            cancellation.Token);
        Assert.Equal(DocumentationScribeCompositionStatus.Cancelled, cancelled.Status);
        Assert.Same(cancelledBound, cancelled.M3Outcome);
        Assert.Null(cancelled.PatchRequest);
        Assert.Null(cancelled.PatchOutcome);
    }

    [Fact]
    public async Task Proposal_executor_persists_reservation_before_send_and_returns_only_exact_readback()
    {
        await using var fixture = await CompositionFixture.CreateProposalStageAsync();
        var campaign = fixture.CreateCampaign();
        var initial = CampaignStateJson.CreateArtifact(campaign.InitialState);
        var store = new MemoryCampaignStore(initial);
        var observing = new ReservationObservingExchange(store, new ProposalExchange(fixture.Request));
        var original = await File.ReadAllBytesAsync(fixture.SourcePath);
        var planned = Assert.Single(campaign.InitialState.WorkItems,
            item => item.Status == CampaignWorkStatus.Planned);
        var planWork = Assert.Single(campaign.Plan.WorkItems,
            item => item.WorkItemKey == planned.WorkItemKey);
        var planTarget = Assert.Single(planWork.Targets);
        var planSource = Assert.IsType<CampaignPlanningRepositorySourceAuthority>(planTarget.Source);
        var requestSource = Assert.IsType<RepositoryEvidenceLocator>(fixture.Request.Target.SourceLocator);
        Assert.Equal(planTarget.SymbolRef, fixture.Request.Target.SymbolRef);
        Assert.Equal(planTarget.AuditOutcome, fixture.Request.Context.AuditOutcome);
        Assert.Equal(planSource.Path, requestSource.Path);
        Assert.Equal(planSource.RequestedDeclarationSpan, requestSource.Span);
        Assert.Equal(planSource.ContentSha256, fixture.Request.Target.SourceSha256);
        Assert.Equal(campaign.PlanningInput.ExecutionPolicy.ScribeRunLimits, fixture.Request.Limits);
        Assert.Equal(planTarget.ApplicableComponents.Length, fixture.Request.Target.ApplicableComponents.Length);
        CampaignStateFactory.ValidateCurrentContext(
            campaign.InitialState,
            campaign.ExecutionCapability,
            "style.public-api.v1",
            campaign.StyleProjection,
            fixture.Request.Context.InputIdentity,
            campaign.PlanningInput,
            campaign.Plan);
        var directAdmission = CampaignStateReducer.AdmitProviderInvocation(
            initial,
            campaign.ExecutionCapability,
            "style.public-api.v1",
            campaign.StyleProjection,
            campaign.PlanningInput,
            campaign.Plan,
            planned.WorkItemKey,
            fixture.Request);
        Assert.True(directAdmission.Kind == CampaignTransitionKind.Applied,
            directAdmission.Failure.ToString());

        var outcome = await DocumentationCampaignProposalExecutor.ExecuteAsync(
            campaign.Input(fixture, store, observing, RuntimeOptions()));

        Assert.True(outcome.Kind == DocumentationCampaignProposalOutcomeKind.ProposalReady,
            outcome.Code + " writes=" + store.SuccessfulReplaceCount + " sends=" + observing.RequestCount);
        Assert.Equal("campaign.proposal.ready", outcome.Code);
        Assert.NotNull(outcome.TrustedProposal);
        Assert.Equal(1, observing.RequestCount);
        Assert.True(observing.SawPersistedReservation);
        Assert.Equal(2, store.SuccessfulReplaceCount);
        Assert.Equal(outcome.Artifact!.Sha256, store.Current!.Sha256);
        Assert.True(outcome.Artifact.ExactUtf8Json.AsSpan().SequenceEqual(
            store.Current.ExactUtf8Json.AsSpan()));
        Assert.Equal(original, await File.ReadAllBytesAsync(fixture.SourcePath));

        var replayExchange = new CountingExchange();
        var replay = await DocumentationCampaignProposalExecutor.ExecuteAsync(
            campaign.Input(fixture, store, replayExchange, RuntimeOptions()));
        Assert.Equal(DocumentationCampaignProposalOutcomeKind.ProposalReady, replay.Kind);
        Assert.Equal("campaign.proposal.replay", replay.Code);
        Assert.Equal(0, replayExchange.RequestCount);
        Assert.Equal(2, store.SuccessfulReplaceCount);
    }

    [Fact]
    public async Task Proposal_executor_rejects_runtime_substitution_before_write_or_send()
    {
        await using var fixture = await CompositionFixture.CreateProposalStageAsync();
        var campaign = fixture.CreateCampaign();
        var initial = CampaignStateJson.CreateArtifact(campaign.InitialState);
        var store = new MemoryCampaignStore(initial);
        var exchange = new CountingExchange();

        var outcome = await DocumentationCampaignProposalExecutor.ExecuteAsync(
            campaign.Input(
                fixture,
                store,
                exchange,
                new DocumentationScribeRuntimeOptions(
                    "provider.substituted.v1",
                    "model.synthetic.v1",
                    "scribe-protocol.v1")));

        Assert.Equal(DocumentationCampaignProposalOutcomeKind.HostContractError, outcome.Kind);
        Assert.Equal("campaign.runtime.mismatch", outcome.Code);
        Assert.Equal(0, exchange.RequestCount);
        Assert.Equal(0, store.SuccessfulReplaceCount);
        Assert.Equal(initial.ExactUtf8Json, store.Current!.ExactUtf8Json);
    }

    [Fact]
    public async Task Proposal_executor_routes_skip_completion_through_closed_result_hooks()
    {
        await using var fixture = await CompositionFixture.CreateProposalStageAsync();
        var campaign = fixture.CreateCampaign();
        var store = new MemoryCampaignStore(CampaignStateJson.CreateArtifact(campaign.InitialState));
        var observed = new List<string>();
        using var registration = CampaignProcessBoundaryHooks.Register(observed.Add);

        var outcome = await DocumentationCampaignProposalExecutor.ExecuteAsync(
            campaign.Input(fixture, store, new SkipExchange(), RuntimeOptions()));

        Assert.Equal(DocumentationCampaignProposalOutcomeKind.TerminalStop, outcome.Kind);
        Assert.Contains(CampaignProcessBoundaryHooks.ProposalAfterProviderBeforeResultTransition, observed);
        Assert.Contains(CampaignProcessBoundaryHooks.ProposalAfterProviderBeforeClosedTransition, observed);
        Assert.Contains(CampaignProcessBoundaryHooks.ProposalAfterClosedReadback, observed);
        Assert.DoesNotContain(CampaignProcessBoundaryHooks.ProposalAfterProviderBeforeProposalTransition, observed);
        Assert.DoesNotContain(CampaignProcessBoundaryHooks.ProposalAfterProposalReadback, observed);
    }

    [Fact]
    public async Task Proposal_executor_binds_deferred_exchange_once_after_preview_and_before_reservation_commit()
    {
        await using var fixture = await CompositionFixture.CreateProposalStageAsync();
        var campaign = fixture.CreateCampaign();
        var store = new MemoryCampaignStore(CampaignStateJson.CreateArtifact(campaign.InitialState));
        var binds = 0;
        var input = campaign.Input(
            fixture,
            store,
            new CountingExchange(),
            RuntimeOptions()) with
        {
            Exchange = null,
            DeferredExchange = () =>
            {
                Assert.Equal(0, store.SuccessfulReplaceCount);
                binds++;
                return new ProposalExchange(fixture.Request);
            },
        };

        var outcome = await DocumentationCampaignProposalExecutor.ExecuteAsync(input);

        Assert.Equal(DocumentationCampaignProposalOutcomeKind.ProposalReady, outcome.Kind);
        Assert.Equal(1, binds);
        Assert.Equal(2, store.SuccessfulReplaceCount);
    }

    [Fact]
    public async Task Proposal_executor_does_not_bind_deferred_exchange_when_higher_precedence_authority_is_invalid()
    {
        await using var fixture = await CompositionFixture.CreateProposalStageAsync();
        var campaign = fixture.CreateCampaign();
        var store = new MemoryCampaignStore(CampaignStateJson.CreateArtifact(campaign.InitialState));
        var binds = 0;
        var input = campaign.Input(
            fixture,
            store,
            new CountingExchange(),
            RuntimeOptions()) with
        {
            AcceptedAuditInputs = ImmutableArray<AuditRecordInput>.Empty,
            Exchange = null,
            DeferredExchange = () =>
            {
                binds++;
                return new CountingExchange();
            },
        };

        var outcome = await DocumentationCampaignProposalExecutor.ExecuteAsync(input);

        Assert.Equal(DocumentationCampaignProposalOutcomeKind.HostContractError, outcome.Kind);
        Assert.Equal(0, binds);
        Assert.Equal(0, store.SuccessfulReplaceCount);
    }

    [Fact]
    public async Task Proposal_executor_validates_current_audit_authority_before_initial_or_replay_work()
    {
        await using var fixture = await CompositionFixture.CreateProposalStageAsync();
        var campaign = fixture.CreateCampaign();
        var initial = CampaignStateJson.CreateArtifact(campaign.InitialState);
        var invalidStore = new MemoryCampaignStore(initial);
        var invalidExchange = new CountingExchange();
        var invalidInput = campaign.Input(fixture, invalidStore, invalidExchange, RuntimeOptions()) with
        {
            AcceptedAuditInputs = ImmutableArray<AuditRecordInput>.Empty,
        };

        var invalid = await DocumentationCampaignProposalExecutor.ExecuteAsync(invalidInput);

        Assert.Equal(DocumentationCampaignProposalOutcomeKind.HostContractError, invalid.Kind);
        Assert.Equal("campaign.context.invalid", invalid.Code);
        Assert.Equal(0, invalidExchange.RequestCount);
        Assert.Equal(0, invalidStore.SuccessfulReplaceCount);

        var replayStore = new MemoryCampaignStore(initial);
        var completed = await DocumentationCampaignProposalExecutor.ExecuteAsync(
            campaign.Input(fixture, replayStore, new ProposalExchange(fixture.Request), RuntimeOptions()));
        Assert.Equal(DocumentationCampaignProposalOutcomeKind.ProposalReady, completed.Kind);
        var writesBeforeReplay = replayStore.SuccessfulReplaceCount;
        var replayExchange = new CountingExchange();

        var replay = await DocumentationCampaignProposalExecutor.ExecuteAsync(
            campaign.Input(fixture, replayStore, replayExchange, RuntimeOptions()) with
            {
                AcceptedAuditInputs = ImmutableArray<AuditRecordInput>.Empty,
            });

        Assert.Equal(DocumentationCampaignProposalOutcomeKind.HostContractError, replay.Kind);
        Assert.Equal("campaign.context.invalid", replay.Code);
        Assert.Equal(0, replayExchange.RequestCount);
        Assert.Equal(writesBeforeReplay, replayStore.SuccessfulReplaceCount);
    }

    [Fact]
    public async Task Proposal_executor_rederives_no_work_from_a_valid_empty_current_authority()
    {
        await using var proposalFixture = await CompositionFixture.CreateProposalStageAsync();
        await using var emptyFixture = await EmptyAuditFixture.CreateAsync();
        var template = proposalFixture.CreateCampaign();
        var planningInput = new CampaignPlanningInput(
            template.PlanningInput.Snapshot,
            template.PlanningInput.ExecutionPolicy,
            emptyFixture.Classified.Classification.ClassificationSet!,
            emptyFixture.Observed.ObservationSet!,
            [],
            emptyFixture.AuditDocument,
            new CampaignPlanningOwnerAuthoritySet([]));
        var plan = CampaignPlanner.Plan(planningInput);
        Assert.Empty(plan.WorkItems);
        var initialState = CampaignStateFactory.CreateInitial(
            "style.public-api.v1",
            template.StyleProjection,
            template.ExecutionCapability,
            emptyFixture.Session.InputIdentity,
            planningInput,
            plan);
        Assert.Equal(CampaignTerminalReason.NoWork, initialState.TerminalOutcome!.Reason);
        var initial = CampaignStateJson.CreateArtifact(initialState);
        var store = new MemoryCampaignStore(initial);
        var exchange = new CountingExchange();
        var input = new DocumentationCampaignProposalInput(
            emptyFixture.Classified,
            emptyFixture.Observed,
            emptyFixture.Policy,
            emptyFixture.AuditInputs,
            emptyFixture.AuditDocument,
            planningInput,
            plan,
            template.ExecutionCapability,
            "style.public-api.v1",
            template.StyleProjection,
            proposalFixture.RequestBytes,
            store,
            RuntimeOptions(),
            exchange,
            null,
            CancellationToken.None,
            CancellationToken.None);

        var outcome = await DocumentationCampaignProposalExecutor.ExecuteAsync(input);

        Assert.Equal(DocumentationCampaignProposalOutcomeKind.NoWork, outcome.Kind);
        Assert.Equal("campaign.no-work", outcome.Code);
        Assert.Equal(0, exchange.RequestCount);
        Assert.Equal(0, store.SuccessfulReplaceCount);
        Assert.Equal(initial.Sha256, outcome.Artifact!.Sha256);
        Assert.True(initial.ExactUtf8Json.AsSpan().SequenceEqual(outcome.Artifact.ExactUtf8Json.AsSpan()));
        Assert.Equal(initial.Sha256, store.Current!.Sha256);
    }

    [Fact]
    public async Task Proposal_executor_owns_request_bytes_before_reservation_and_provider_execution()
    {
        await using var fixture = await CompositionFixture.CreateProposalStageAsync();
        var campaign = fixture.CreateCampaign();
        var callerBuffer = fixture.RequestBytes.ToArray();
        var store = new MemoryCampaignStore(CampaignStateJson.CreateArtifact(campaign.InitialState))
        {
            AfterSuccessfulReplace = attempt =>
            {
                if (attempt == 1)
                {
                    callerBuffer.AsSpan().Fill(0x20);
                }
            },
        };
        var exchange = new CountingProposalExchange(fixture.Request);
        var input = campaign.Input(fixture, store, exchange, RuntimeOptions()) with
        {
            RequestUtf8Json = callerBuffer,
        };

        var outcome = await DocumentationCampaignProposalExecutor.ExecuteAsync(input);

        Assert.Equal(DocumentationCampaignProposalOutcomeKind.ProposalReady, outcome.Kind);
        Assert.Equal(1, exchange.RequestCount);
        Assert.Equal(2, store.SuccessfulReplaceCount);
    }

    [Fact]
    public async Task Proposal_executor_separates_pre_send_cancellation_from_authoritative_settlement()
    {
        await using var fixture = await CompositionFixture.CreateProposalStageAsync();
        var campaign = fixture.CreateCampaign();
        var store = new MemoryCampaignStore(CampaignStateJson.CreateArtifact(campaign.InitialState));
        var exchange = new CountingExchange();
        using var execution = new CancellationTokenSource();
        execution.Cancel();

        var outcome = await DocumentationCampaignProposalExecutor.ExecuteAsync(
            campaign.Input(
                fixture,
                store,
                exchange,
                RuntimeOptions(),
                execution.Token,
                CancellationToken.None));

        Assert.Equal(DocumentationCampaignProposalOutcomeKind.Cancelled, outcome.Kind);
        Assert.Equal(0, exchange.RequestCount);
        Assert.Equal(2, store.SuccessfulReplaceCount);
        Assert.Null(store.Current!.State.ActiveReservation);
        Assert.Equal(CampaignTerminalKind.Cancelled, store.Current.State.TerminalOutcome!.Kind);
        Assert.Contains(store.Current.State.WorkItems, item => item.Status == CampaignWorkStatus.Planned);
    }

    [Fact]
    public async Task Proposal_executor_never_promotes_a_postflight_stale_retained_proposal()
    {
        await using var fixture = await CompositionFixture.CreateProposalStageAsync();
        var campaign = fixture.CreateCampaign();
        var store = new MemoryCampaignStore(CampaignStateJson.CreateArtifact(campaign.InitialState));
        var original = await File.ReadAllTextAsync(fixture.SourcePath);
        try
        {
            var outcome = await DocumentationCampaignProposalExecutor.ExecuteAsync(
                campaign.Input(
                    fixture,
                    store,
                    new MutatingProposalExchange(fixture.Request, fixture.SourcePath),
                    RuntimeOptions()));

            Assert.Equal(DocumentationCampaignProposalOutcomeKind.TerminalStop, outcome.Kind);
            Assert.Equal(2, store.SuccessfulReplaceCount);
            Assert.Null(store.Current!.State.ActiveReservation);
            var closed = Assert.Single(store.Current.State.WorkItems,
                item => item.Status == CampaignWorkStatus.Closed
                    && item.ClosedOutcome?.Stage == CampaignWorkOutcomeStage.Scribe);
            Assert.Equal(CampaignWorkOutcomeCode.ValidationFailure, closed.ClosedOutcome!.Code);
            Assert.Null(closed.TrustedProposal);
            Assert.Null(closed.ClosedOutcome.ScribeResultCommitmentSha256);
            Assert.True(store.Current.State.LineageCharges.ProviderRequests.Observed > 0);
        }
        finally
        {
            await File.WriteAllTextAsync(fixture.SourcePath, original, new UTF8Encoding(false));
        }
    }

    [Fact]
    public async Task Proposal_executor_distinguishes_store_conflict_from_lost_reservation_acknowledgement()
    {
        await using var fixture = await CompositionFixture.CreateProposalStageAsync();
        var campaign = fixture.CreateCampaign();
        var initial = CampaignStateJson.CreateArtifact(campaign.InitialState);

        var conflictStore = new MemoryCampaignStore(initial)
        {
            NextReportedReplaceKind = CampaignCheckpointWriteKind.CurrentMismatch,
        };
        var conflictExchange = new CountingExchange();
        var conflict = await DocumentationCampaignProposalExecutor.ExecuteAsync(
            campaign.Input(fixture, conflictStore, conflictExchange, RuntimeOptions()));
        Assert.Equal(DocumentationCampaignProposalOutcomeKind.StateConflict, conflict.Kind);
        Assert.Equal("campaign.reservation.conflict", conflict.Code);
        Assert.Equal(0, conflictExchange.RequestCount);
        Assert.Equal(0, conflictStore.SuccessfulReplaceCount);
        Assert.Equal(initial.Sha256, conflictStore.Current!.Sha256);

        var lostAckStore = new MemoryCampaignStore(initial)
        {
            NextReportedReplaceKind = CampaignCheckpointWriteKind.CurrentMismatch,
            ApplyNextReplaceBeforeReporting = true,
        };
        var lostAckExchange = new CountingExchange();
        var lostAck = await DocumentationCampaignProposalExecutor.ExecuteAsync(
            campaign.Input(fixture, lostAckStore, lostAckExchange, RuntimeOptions()));
        Assert.Equal(DocumentationCampaignProposalOutcomeKind.AmbiguousDispatch, lostAck.Kind);
        Assert.Equal("campaign.reservation.observer", lostAck.Code);
        Assert.Equal(0, lostAckExchange.RequestCount);
        Assert.Equal(1, lostAckStore.SuccessfulReplaceCount);
        Assert.IsType<CampaignProviderReservation>(lostAckStore.Current!.State.ActiveReservation);
    }

    [Fact]
    public async Task Proposal_executor_classifies_deterministic_completion_failure_and_settlement_conflict()
    {
        await using var fixture = await CompositionFixture.CreateProposalStageAsync();
        var campaign = fixture.CreateCampaign();
        var initial = CampaignStateJson.CreateArtifact(campaign.InitialState);
        var invalidStore = new MemoryCampaignStore(initial);
        var invalidExchange = new CountingProposalExchange(fixture.Request);

        var invalid = await DocumentationCampaignProposalExecutor.ExecuteAsync(
            campaign.Input(
                fixture,
                invalidStore,
                invalidExchange,
                RuntimeOptions(),
                timeProvider: new SequencedTimeProvider(0)));

        Assert.Equal(DocumentationCampaignProposalOutcomeKind.HostContractError, invalid.Kind);
        Assert.Equal("campaign.settlement.invalid", invalid.Code);
        Assert.Equal(1, invalidExchange.RequestCount);
        Assert.Equal(1, invalidStore.SuccessfulReplaceCount);
        Assert.IsType<CampaignProviderReservation>(invalidStore.Current!.State.ActiveReservation);

        var conflictStore = new MemoryCampaignStore(initial)
        {
            ReportedReplaceAttempt = 2,
            ReportedReplaceKind = CampaignCheckpointWriteKind.CurrentMismatch,
        };
        var conflictExchange = new CountingProposalExchange(fixture.Request);
        var conflict = await DocumentationCampaignProposalExecutor.ExecuteAsync(
            campaign.Input(fixture, conflictStore, conflictExchange, RuntimeOptions()));

        Assert.Equal(DocumentationCampaignProposalOutcomeKind.StateConflict, conflict.Kind);
        Assert.Equal("campaign.settlement.conflict", conflict.Code);
        Assert.Equal(1, conflictExchange.RequestCount);
        Assert.Equal(1, conflictStore.SuccessfulReplaceCount);
        Assert.IsType<CampaignProviderReservation>(conflictStore.Current!.State.ActiveReservation);
    }

    [Fact]
    public async Task Proposal_executor_preserves_completion_revision_headroom_without_dispatch()
    {
        await using var fixture = await CompositionFixture.CreateProposalStageAsync();
        var campaign = fixture.CreateCampaign();
        var initial = CampaignStateJson.CreateArtifact(campaign.InitialState);

        var nearMaximumStore = new MemoryCampaignStore(
            WithCheckpointRevision(initial, CampaignStateContract.MaximumObservation - 1));
        var nearMaximumExchange = new CountingExchange();
        var nearMaximum = await DocumentationCampaignProposalExecutor.ExecuteAsync(
            campaign.Input(fixture, nearMaximumStore, nearMaximumExchange, RuntimeOptions()));

        Assert.Equal(DocumentationCampaignProposalOutcomeKind.BudgetExhausted, nearMaximum.Kind);
        Assert.Equal(0, nearMaximumExchange.RequestCount);
        Assert.Equal(1, nearMaximumStore.SuccessfulReplaceCount);
        Assert.Equal(CampaignStateContract.MaximumObservation, nearMaximumStore.Current!.CheckpointRevision);
        Assert.Null(nearMaximumStore.Current.State.ActiveReservation);

        var maximumStore = new MemoryCampaignStore(
            WithCheckpointRevision(initial, CampaignStateContract.MaximumObservation));
        var maximumExchange = new CountingExchange();
        var maximum = await DocumentationCampaignProposalExecutor.ExecuteAsync(
            campaign.Input(fixture, maximumStore, maximumExchange, RuntimeOptions()));

        Assert.Equal(DocumentationCampaignProposalOutcomeKind.HostContractError, maximum.Kind);
        Assert.Equal("campaign.reservation.invalid", maximum.Code);
        Assert.Equal(0, maximumExchange.RequestCount);
        Assert.Equal(0, maximumStore.SuccessfulReplaceCount);
    }

    [Fact]
    public async Task Campaign_binder_uses_bounded_monotonic_host_elapsed_and_fails_closed_on_overflow()
    {
        await using var fixture = await CompositionFixture.CreateProposalStageAsync();
        var campaign = fixture.CreateCampaign();
        var observedStore = new MemoryCampaignStore(CampaignStateJson.CreateArtifact(campaign.InitialState));

        var observed = await DocumentationCampaignProposalExecutor.ExecuteAsync(
            campaign.Input(
                fixture,
                observedStore,
                new ProposalExchange(fixture.Request),
                RuntimeOptions(),
                timeProvider: new SequencedTimeProvider(50_000)));

        Assert.Equal(DocumentationCampaignProposalOutcomeKind.ProposalReady, observed.Kind);
        Assert.Equal(50_000, observedStore.Current!.State.LineageCharges.ActiveElapsedMilliseconds.Observed);

        var overflowStore = new MemoryCampaignStore(CampaignStateJson.CreateArtifact(campaign.InitialState));
        var overflowExchange = new CountingProposalExchange(fixture.Request);
        var overflow = await DocumentationCampaignProposalExecutor.ExecuteAsync(
            campaign.Input(
                fixture,
                overflowStore,
                overflowExchange,
                RuntimeOptions(),
                timeProvider: new ThrowingTimeProvider()));

        Assert.Equal(DocumentationCampaignProposalOutcomeKind.HostContractError, overflow.Kind);
        Assert.Equal("campaign.preparation.invalid", overflow.Code);
        Assert.Equal(1, overflowExchange.RequestCount);
        Assert.Equal(1, overflowStore.SuccessfulReplaceCount);
        Assert.IsType<CampaignProviderReservation>(overflowStore.Current!.State.ActiveReservation);
    }

    [Fact]
    public async Task Proposal_executor_allows_agent_internal_sends_under_one_outer_dispatch()
    {
        await using var fixture = await CompositionFixture.CreateProposalStageAsync();
        var campaign = fixture.CreateCampaign();
        var store = new MemoryCampaignStore(CampaignStateJson.CreateArtifact(campaign.InitialState));
        var exchange = new SemanticThenProposalExchange(fixture.Request);

        var outcome = await DocumentationCampaignProposalExecutor.ExecuteAsync(
            campaign.Input(fixture, store, exchange, RuntimeOptions()));

        Assert.Equal(DocumentationCampaignProposalOutcomeKind.ProposalReady, outcome.Kind);
        Assert.Equal(2, exchange.RequestCount);
        Assert.Equal(2, store.Current!.State.LineageCharges.ProviderRequests.Observed);
        Assert.Equal(1, Assert.Single(store.Current.State.WorkItems,
            item => item.Status == CampaignWorkStatus.ProposalComplete).OuterAttemptCount);
    }

    [Fact]
    public async Task Proposal_executor_recovers_active_lineage_with_a_fresh_outer_attempt()
    {
        await using var fixture = await CompositionFixture.CreateProposalStageAsync();
        var campaign = fixture.CreateCampaign();
        var initial = CampaignStateJson.CreateArtifact(campaign.InitialState);
        var store = new MemoryCampaignStore(initial);
        var admitted = Admit(campaign, fixture, initial);
        var accepted = await CampaignCheckpointAcceptance.AcceptAsync(store, admitted);
        Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted, accepted.Kind);
        Assert.IsType<CampaignProviderReservation>(store.Current!.State.ActiveReservation);

        var outcome = await DocumentationCampaignProposalExecutor.ExecuteAsync(
            campaign.Input(fixture, store, new ProposalExchange(fixture.Request), RuntimeOptions()));

        Assert.Equal(DocumentationCampaignProposalOutcomeKind.ProposalReady, outcome.Kind);
        var completed = Assert.Single(store.Current!.State.WorkItems,
            item => item.Status == CampaignWorkStatus.ProposalComplete);
        Assert.Equal(2, completed.OuterAttemptCount);
        Assert.True(store.Current.State.LineageCharges.ProviderRequests.ConservativeUnobserved > 0);
        Assert.True(store.Current.State.LineageCharges.ProviderRequests.Observed > 0);
        Assert.Equal(3, store.SuccessfulReplaceCount);
    }

    [Fact]
    public async Task Proposal_executor_never_reopens_provider_work_under_a_persisted_root_terminal()
    {
        await using var fixture = await CompositionFixture.CreateProposalStageAsync();
        var campaign = fixture.CreateCampaign();
        var initial = CampaignStateJson.CreateArtifact(campaign.InitialState);
        var store = new MemoryCampaignStore(initial);
        var admitted = Admit(campaign, fixture, initial);
        var acceptedAdmission = await CampaignCheckpointAcceptance.AcceptAsync(store, admitted);
        var accepted = Assert.IsType<CampaignAcceptedCheckpoint>(acceptedAdmission.AcceptedCheckpoint);
        var stopped = CampaignStateReducer.StopActiveInvocation(
            admitted.Artifact,
            accepted,
            CampaignTerminalKind.Cancelled);
        var acceptedStop = await CampaignCheckpointAcceptance.AcceptAsync(store, stopped);
        Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted, acceptedStop.Kind);
        var writesBefore = store.SuccessfulReplaceCount;
        var exchange = new CountingExchange();

        var outcome = await DocumentationCampaignProposalExecutor.ExecuteAsync(
            campaign.Input(fixture, store, exchange, RuntimeOptions()));

        Assert.Equal(DocumentationCampaignProposalOutcomeKind.Cancelled, outcome.Kind);
        Assert.Equal(0, exchange.RequestCount);
        Assert.Equal(writesBefore, store.SuccessfulReplaceCount);
        Assert.Equal(CampaignTerminalKind.Cancelled, store.Current!.State.TerminalOutcome!.Kind);
    }

    private static CampaignTransitionResult Admit(
        CampaignExecutionFixture campaign,
        CompositionFixture fixture,
        CampaignCheckpointArtifact initial)
    {
        var work = Assert.Single(campaign.InitialState.WorkItems,
            item => item.Status == CampaignWorkStatus.Planned);
        var admitted = CampaignStateReducer.AdmitProviderInvocation(
            initial,
            campaign.ExecutionCapability,
            "style.public-api.v1",
            campaign.StyleProjection,
            campaign.PlanningInput,
            campaign.Plan,
            work.WorkItemKey,
            fixture.Request);
        Assert.Equal(CampaignTransitionKind.Applied, admitted.Kind);
        return admitted;
    }

    private static CampaignCheckpointArtifact WithCheckpointRevision(
        CampaignCheckpointArtifact artifact,
        long checkpointRevision)
    {
        var root = JsonNode.Parse(CampaignStateJson.Write(artifact.State))!.AsObject();
        root["checkpointRevision"] = checkpointRevision;
        var parsed = CampaignStateJson.Parse(Encoding.UTF8.GetBytes(root.ToJsonString() + "\n"));
        Assert.True(parsed.IsValid, parsed.FailureCode?.ToString());
        return Assert.IsType<CampaignCheckpointArtifact>(parsed.Artifact);
    }

    private static Task<IDocumentationScribePreparedOutcome> PrepareAsync(
        CompositionFixture fixture,
        IDocumentationScribeModelExchange exchange,
        CancellationToken cancellationToken = default) =>
        DocumentationScribeComposition.PrepareAsync(
            fixture.SelectedAudit,
            fixture.RequestBytes,
            fixture.AttemptId,
            configuredAgentEntrypoint: null,
            RuntimeOptions(),
            exchange,
            cancellationToken);

    private static DocumentationScribeRuntimeOptions RuntimeOptions() => new(
        "provider.synthetic.v1",
        "model.synthetic.v1",
        "scribe-protocol.v1");

    private sealed class ProposalExchange(DocumentationScribeRequest request) : IDocumentationScribeModelExchange
    {
        public ValueTask<DocumentationScribeModelResponse> SendAsync(
            DocumentationScribeModelRequest modelRequest,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new DocumentationScribeModelResponse(
                [],
                [new DocumentationScribeModelTerminalSubmission(ProposalTerminal(request))]));
        }
    }

    private sealed class SkipExchange : IDocumentationScribeModelExchange
    {
        public ValueTask<DocumentationScribeModelResponse> SendAsync(
            DocumentationScribeModelRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new DocumentationScribeModelResponse(
                [],
                [new DocumentationScribeModelTerminalSubmission(SkipTerminal())]));
    }

    private sealed class ProviderFailureExchange : IDocumentationScribeModelExchange
    {
        public ValueTask<DocumentationScribeModelResponse> SendAsync(
            DocumentationScribeModelRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new DocumentationScribeModelResponse(
                [],
                [],
                new DocumentationScribeModelFailure(DocumentationScribeModelFailureCode.PermanentUnavailable)));
    }

    private sealed class ObservedSkipExchange(DocumentationScribeModelUsage usage)
        : IDocumentationScribeModelExchange
    {
        public ValueTask<DocumentationScribeModelResponse> SendAsync(
            DocumentationScribeModelRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new DocumentationScribeModelResponse(
                [],
                [new DocumentationScribeModelTerminalSubmission(SkipTerminal())],
                usage: usage));
    }

    private sealed class DelayedSkipExchange : IDocumentationScribeModelExchange
    {
        public async ValueTask<DocumentationScribeModelResponse> SendAsync(
            DocumentationScribeModelRequest request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(20, cancellationToken);
            return new DocumentationScribeModelResponse(
                [],
                [new DocumentationScribeModelTerminalSubmission(SkipTerminal())]);
        }
    }

    private sealed class CancellingExchange(CancellationTokenSource cancellation)
        : IDocumentationScribeModelExchange
    {
        public ValueTask<DocumentationScribeModelResponse> SendAsync(
            DocumentationScribeModelRequest request,
            CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            return ValueTask.FromCanceled<DocumentationScribeModelResponse>(cancellationToken);
        }
    }

    private sealed class ProtocolFailureExchange : IDocumentationScribeModelExchange
    {
        public ValueTask<DocumentationScribeModelResponse> SendAsync(
            DocumentationScribeModelRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new DocumentationScribeModelResponse(
                [new DocumentationScribeModelToolCall(
                    0,
                    "call.conflict",
                    DocumentationScribeRepositoryToolOperationIds.SearchText,
                    "{}"u8.ToArray())],
                [new DocumentationScribeModelTerminalSubmission(SkipTerminal())]));
    }

    private sealed class CountingExchange : IDocumentationScribeModelExchange
    {
        internal int RequestCount { get; private set; }

        public ValueTask<DocumentationScribeModelResponse> SendAsync(
            DocumentationScribeModelRequest request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return ValueTask.FromResult(new DocumentationScribeModelResponse(
                [],
                [new DocumentationScribeModelTerminalSubmission(SkipTerminal())]));
        }
    }

    private sealed class ReservationObservingExchange(
        MemoryCampaignStore store,
        IDocumentationScribeModelExchange inner) : IDocumentationScribeModelExchange
    {
        internal int RequestCount { get; private set; }
        internal bool SawPersistedReservation { get; private set; }

        public ValueTask<DocumentationScribeModelResponse> SendAsync(
            DocumentationScribeModelRequest request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            SawPersistedReservation = store.Current?.State.ActiveReservation is CampaignProviderReservation;
            return inner.SendAsync(request, cancellationToken);
        }
    }

    private sealed class CountingProposalExchange(DocumentationScribeRequest request)
        : IDocumentationScribeModelExchange
    {
        internal int RequestCount { get; private set; }

        public ValueTask<DocumentationScribeModelResponse> SendAsync(
            DocumentationScribeModelRequest modelRequest,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return new ProposalExchange(request).SendAsync(modelRequest, cancellationToken);
        }
    }

    private sealed class SemanticThenProposalExchange(DocumentationScribeRequest request)
        : IDocumentationScribeModelExchange
    {
        internal int RequestCount { get; private set; }

        public ValueTask<DocumentationScribeModelResponse> SendAsync(
            DocumentationScribeModelRequest modelRequest,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            if (RequestCount == 1)
            {
                var semantic = Assert.Single(modelRequest.Tools,
                    tool => tool.OperationId == new DocumentationScribeSemanticToolDescriptor().OperationId);
                return ValueTask.FromResult(new DocumentationScribeModelResponse(
                    [new DocumentationScribeModelToolCall(
                        0,
                        "call.semantic",
                        semantic.OperationId,
                        "{}"u8.ToArray())],
                    []));
            }

            Assert.Equal(2, RequestCount);
            Assert.Single(modelRequest.CompletedToolExchanges);
            return ValueTask.FromResult(new DocumentationScribeModelResponse(
                [],
                [new DocumentationScribeModelTerminalSubmission(ProposalTerminal(request))]));
        }
    }

    private sealed class SequencedTimeProvider(long elapsedMilliseconds) : TimeProvider
    {
        private int reads;

        public override long TimestampFrequency => 1_000;

        public override long GetTimestamp() =>
            Interlocked.Increment(ref reads) == 1 ? 0 : elapsedMilliseconds;
    }

    private sealed class ThrowingTimeProvider : TimeProvider
    {
        private int reads;

        public override long GetTimestamp() => Interlocked.Increment(ref reads) == 1
            ? 0
            : throw new OverflowException("synthetic timestamp overflow");
    }

    private sealed class MutatingProposalExchange(
        DocumentationScribeRequest request,
        string sourcePath) : IDocumentationScribeModelExchange
    {
        public async ValueTask<DocumentationScribeModelResponse> SendAsync(
            DocumentationScribeModelRequest modelRequest,
            CancellationToken cancellationToken)
        {
            await File.AppendAllTextAsync(
                sourcePath,
                Environment.NewLine,
                new UTF8Encoding(false),
                cancellationToken);
            return new DocumentationScribeModelResponse(
                [],
                [new DocumentationScribeModelTerminalSubmission(ProposalTerminal(request))]);
        }
    }

    private static ReadOnlyMemory<byte> ProposalTerminal(DocumentationScribeRequest request)
    {
        var locator = Assert.IsType<RepositoryEvidenceLocator>(request.Target.SourceLocator);
        var contentUnits = new JsonArray
        {
            new JsonObject
            {
                ["kind"] = "content.summary",
                ["lines"] = new JsonArray("Runs the selected operation."),
                ["claimCategoryId"] = "claim.purpose",
                ["evidenceReferenceIds"] = new JsonArray("evidence.source"),
            },
        };
        foreach (var component in request.Target.ApplicableComponents)
        {
            var evidenceReferenceId = Assert.Single(request.EvidenceReferences,
                reference => reference.Subject is ComponentEvidenceSubject subject
                    && subject.ComponentKind == CampaignComponentKind(component.Kind)
                    && subject.Identity == component.Identity).EvidenceReferenceId;
            var contentUnit = new JsonObject
            {
                ["kind"] = "content." + ComponentKindId(component.Kind),
                ["componentIdentity"] = component.Identity,
                ["lines"] = new JsonArray(component.Kind == DocumentationPatchComponentKind.Return
                    ? "The operation result."
                    : "The value supplied to the operation."),
                ["claimCategoryId"] = "claim.behavior",
                ["evidenceReferenceIds"] = new JsonArray(evidenceReferenceId),
            };
            if (component.Name is not null)
            {
                contentUnit["name"] = component.Name;
            }
            contentUnits.Add(contentUnit);
        }

        return JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["kind"] = "proposal",
            ["target"] = new JsonObject
            {
                ["repositoryContextRef"] = request.Context.RepositoryContextRef.Value,
                ["symbolRef"] = Symbol(request.Target.SymbolRef),
                ["sourceCommitment"] = new JsonObject
                {
                    ["locator"] = RepositoryLocator(locator.Path, locator.Span!.Value),
                    ["contentSha256"] = request.Target.SourceSha256,
                },
            },
            ["contentUnits"] = contentUnits,
        });
    }

    private static ReadOnlyMemory<byte> SkipTerminal() =>
        "{\"kind\":\"skip\",\"reason\":\"scribe.skip.insufficient-evidence\",\"evidenceReferenceIds\":[]}"u8.ToArray();

    private static ReadOnlyMemory<byte> WithLimit(
        ReadOnlyMemory<byte> requestBytes,
        string name,
        int value)
    {
        var root = JsonNode.Parse(requestBytes.Span)!.AsObject();
        root["limits"]![name] = value;
        return JsonSerializer.SerializeToUtf8Bytes(root);
    }

    private sealed class EmptyAuditFixture : IAsyncDisposable
    {
        private EmptyAuditFixture(
            string root,
            LoadedRepositorySession session,
            ClassifiedRepositorySession classified,
            ObservedRepositorySession observed,
            PolicyDocumentV1 policy,
            ImmutableArray<AuditRecordInput> auditInputs,
            AuditDocument auditDocument)
        {
            Root = root;
            Session = session;
            Classified = classified;
            Observed = observed;
            Policy = policy;
            AuditInputs = auditInputs;
            AuditDocument = auditDocument;
        }

        private string Root { get; }
        internal LoadedRepositorySession Session { get; }
        internal ClassifiedRepositorySession Classified { get; }
        internal ObservedRepositorySession Observed { get; }
        internal PolicyDocumentV1 Policy { get; }
        internal ImmutableArray<AuditRecordInput> AuditInputs { get; }
        internal AuditDocument AuditDocument { get; }

        internal static async Task<EmptyAuditFixture> CreateAsync()
        {
            var tempRoot = Path.GetFullPath(Path.GetTempPath());
            var root = Descendant(tempRoot, "contract-scribe-issue-142-empty-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            LoadedRepositorySession? session = null;
            try
            {
                const string repositoryPath = "Empty.cs";
                var sourcePath = Descendant(root, repositoryPath);
                await File.WriteAllTextAsync(
                    sourcePath,
                    "namespace Empty; internal sealed class Hidden { }\n",
                    new UTF8Encoding(false));
                var sourceText = await File.ReadAllTextAsync(sourcePath);
                var syntaxTree = CSharpSyntaxTree.ParseText(
                    sourceText,
                    CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest),
                    repositoryPath,
                    Encoding.UTF8);
                var compilation = CSharpCompilation.Create(
                    "Empty",
                    [syntaxTree],
                    PlatformReferences,
                    new CSharpCompilationOptions(
                        OutputKind.DynamicallyLinkedLibrary,
                        deterministic: true));
                var workspace = new AdhocWorkspace();
                var project = workspace.AddProject("Empty", LanguageNames.CSharp);
                var loadedProject = new LoadedProject(
                    "Empty.csproj",
                    "net10.0",
                    "empty.net10.0",
                    LoadedProjectRole.AuditRoot,
                    [],
                    project,
                    compilation,
                    new Dictionary<SyntaxTree, LoadedSourceTree>(ReferenceEqualityComparer.Instance)
                    {
                        [syntaxTree] = new(
                            LoadedSourceKind.Repository,
                            repositoryPath,
                            new RepositoryPathResolver().PhysicalIdentity(root, sourcePath),
                            null),
                    });
                Assert.True(RepositoryContextRef.TryParse(
                    "repoctx-fedcba9876543210fedcba9876543210",
                    out var repositoryContextRef));
                session = new LoadedRepositorySession(
                    repositoryContextRef,
                    root,
                    "Empty.csproj",
                    new ToolchainIdentity("test", "test", "test", "test"),
                    [loadedProject],
                    [],
                    workspace,
                    DocumentationScribeContextStableFileReader.ReadDirectoryIdentity(root));
                session.SealDocumentationPatchRepositoryPolicyForTests();
                var classified = new SymbolClassifier().ClassifySession(session, TargetProfile.ExternalApi);
                Assert.Equal(ClassificationRunStatus.Success, classified.Classification.Status);
                Assert.Empty(classified.Classification.ClassificationSet!.Targets);
                var observed = new DocumentationObserver().Observe(classified);
                Assert.Equal(DocumentationObservationRunStatus.Success, observed.Status);
                var policy = PolicyConfigurationEvaluator.Parse(
                    "{\"schemaVersion\":1,\"targetProfile\":\"profile.external-api\",\"defaultDecision\":\"required\"}"u8.ToArray())
                    .Document ?? throw new InvalidOperationException("policy");
                var extracted = new PolicyEvidenceExtractor().Extract(classified, observed, policy);
                Assert.Equal(PolicyEvidenceExtractionStatus.Success, extracted.Status);
                var inputs = AuditInputAssembler.Assemble(
                    classified.Classification.ClassificationSet,
                    policy,
                    extracted).ToImmutableArray();
                Assert.Empty(inputs);
                var audit = AuditAggregator.Aggregate(
                    TargetProfile.ExternalApi,
                    classified.Classification.ClassificationSet,
                    policy,
                    inputs);
                return new EmptyAuditFixture(root, session, classified, observed, policy, inputs, audit);
            }
            catch
            {
                if (session is not null)
                {
                    await session.DisposeAsync();
                }
                Directory.Delete(root, recursive: true);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class CompositionFixture : IAsyncDisposable
    {
        private CompositionFixture(
            string root,
            string sourcePath,
            LoadedRepositorySession session,
            ClassifiedRepositorySession classified,
            ObservedRepositorySession observed,
            PolicyDocumentV1 policy,
            ImmutableArray<AuditRecordInput> auditInputs,
            AuditDocument auditDocument,
            PolicyEvidenceExtractionOutcome extraction,
            TargetClassification target,
            DocumentationScribeSelectedAudit selectedAudit,
            DocumentationScribeSelectedAudit? nonMethodSelectedAudit,
            ReadOnlyMemory<byte> requestBytes,
            DocumentationScribeRequest request,
            DocumentationScribeAttemptId attemptId)
        {
            Root = root;
            SourcePath = sourcePath;
            Session = session;
            Classified = classified;
            Observed = observed;
            Policy = policy;
            AuditInputs = auditInputs;
            AuditDocument = auditDocument;
            Extraction = extraction;
            Target = target;
            SelectedAudit = selectedAudit;
            NonMethodSelectedAudit = nonMethodSelectedAudit;
            RequestBytes = requestBytes;
            Request = request;
            AttemptId = attemptId;
        }

        internal string Root { get; }
        internal string SourcePath { get; }
        internal LoadedRepositorySession Session { get; }
        internal ClassifiedRepositorySession Classified { get; }
        internal ObservedRepositorySession Observed { get; }
        internal PolicyDocumentV1 Policy { get; }
        internal ImmutableArray<AuditRecordInput> AuditInputs { get; }
        internal AuditDocument AuditDocument { get; }
        internal PolicyEvidenceExtractionOutcome Extraction { get; }
        internal TargetClassification Target { get; }
        internal DocumentationScribeSelectedAudit SelectedAudit { get; }
        internal DocumentationScribeSelectedAudit? NonMethodSelectedAudit { get; }
        internal ReadOnlyMemory<byte> RequestBytes { get; }
        internal DocumentationScribeRequest Request { get; }
        internal DocumentationScribeAttemptId AttemptId { get; }

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            Directory.Delete(Root, recursive: true);
        }

        internal static Task<CompositionFixture> CreateAsync() =>
            CreateAsync(
                ["documentation-scribe", "end-to-end"],
                "M:EndToEnd.Fixture.Run",
                "T:EndToEnd.BaseFixture");

        internal static Task<CompositionFixture> CreateProposalStageAsync() =>
            CreateAsync(
                ["campaign", "execution", "proposal-stage"],
                "M:ProposalStage.ProposalFixture.Execute",
                nonMethodDocumentationId: null);

        internal CampaignExecutionFixture CreateCampaign(
            long maximumPatchElapsedMilliseconds = 120_000,
            long maximumCampaignElapsedMilliseconds = 300_000)
        {
            var classifications = Classified.Classification.ClassificationSet!;
            var observations = Observed.Observation.ObservationSet!;
            var evidenceAuthority = Extraction.Bindings.Select(binding =>
                new CampaignPlanningEvidenceAuthority(
                    observations.Observations.Single(observation => observation.Subject == binding.Subject),
                    binding.Evidence)).ToImmutableArray();
            var targetAuthorities = classifications.Targets
                .Where(target => target.SupportStatus == SupportStatus.Supported)
                .Select(target =>
                {
                    var observation = observations.Observations.Single(item =>
                        item.Subject.ParentSymbolRef == target.SymbolRef
                        && item.Subject.ComponentKind is null);
                    var declaration = Assert.Single(observation.Declarations);
                    var repository = Assert.IsType<RepositoryDocumentationSourceIdentity>(declaration.Source);
                    var sourcePath = Descendant(Root, repository.Path);
                    var requestedSpan = target.SymbolRef == Target.SymbolRef
                        ? Assert.IsType<Utf16Span>(
                            Assert.IsType<RepositoryEvidenceLocator>(Request.Target.SourceLocator).Span)
                        : declaration.DeclarationSpan;
                    var source = new CampaignPlanningRepositorySourceAuthority(
                        repository.Path,
                        Sha256(Encoding.UTF8.GetBytes(
                            new RepositoryPathResolver().PhysicalIdentity(Root, sourcePath))),
                        repository.SourceSha256,
                        declaration.DeclarationId,
                        repository.SourceSha256,
                        DocumentationPatchRepositoryEncoding.Utf8,
                        declaration.DeclarationSpan,
                        requestedSpan,
                        requestedSpan,
                        declaration.DeclarationSpan,
                        declaration.DocumentationSpan,
                        declaration.BlockState);
                    var components = target.SymbolRef == Target.SymbolRef
                        ? Request.Target.ApplicableComponents.Select(component =>
                            new CampaignPlanningApplicableComponent(
                                CampaignComponentKind(component.Kind),
                                component.Identity,
                                component.Name)).ToImmutableArray()
                        : [];
                    return new CampaignPlanningTargetAuthority(
                        target,
                        source,
                        components,
                        [target.SymbolRef],
                        multiDeclarator: false,
                        primaryConstructor: false,
                        primaryConstructorAlias: false,
                        target.SymbolRef == Target.SymbolRef ? Request.StyleProfile : null);
                }).ToImmutableArray();
            var agentProjection = JsonSerializer.SerializeToElement(new
            {
                scribeProtocolId = "scribe-protocol.v1",
            });
            var toolProjection = JsonSerializer.SerializeToElement(new
            {
                toolPolicyId = Request.ToolPolicyId,
            });
            var providerProjection = JsonSerializer.SerializeToElement(new
            {
                providerConfigurationId = "provider.synthetic.v1",
                modelConfigurationId = "model.synthetic.v1",
            });
            var m2Projection = JsonSerializer.SerializeToElement(new
            {
                m2ProjectionVersion = 1,
                maximumPatchElapsedMilliseconds,
            });
            var executionPolicy = new CampaignPlanningExecutionPolicy(
                Request.Limits,
                new CampaignPlanningBudgetPolicy(
                    32,
                    8,
                    1_000_000,
                    64,
                    3,
                    1_000_000,
                    500_000,
                    100_000,
                    5_000_000,
                    maximumCampaignElapsedMilliseconds,
                    8,
                    costEnforced: false,
                    costCurrency: null,
                    costRatePolicy: null),
                Content(CampaignPlanningContentFamily.ProposalContract, "proposal", "proposal-v1"),
                Content(CampaignPlanningContentFamily.AgentProtocol, "agent", agentProjection),
                Content(CampaignPlanningContentFamily.ContextSelectionPolicy, "context", "context-v1"),
                Content(CampaignPlanningContentFamily.ToolPolicyAndRegistry, "tools", toolProjection),
                Content(CampaignPlanningContentFamily.ProviderModelRequestProfile, "provider", providerProjection),
                Content(CampaignPlanningContentFamily.RetryPolicy, "retry", "retry-v1"),
                Content(CampaignPlanningContentFamily.M2ProjectionPolicy, "m2", m2Projection),
                Content(CampaignPlanningContentFamily.ProductContractRevision, "product", "product-v1"));
            var planningInput = new CampaignPlanningInput(
                new CampaignPlanningSnapshot(
                    "campaign.proposal-stage.fixture",
                    "snapshot.proposal-stage.fixture",
                    Sha256(Encoding.UTF8.GetBytes("repository")),
                    Sha256(Encoding.UTF8.GetBytes("input")),
                    Sha256(Encoding.UTF8.GetBytes("policy")),
                    TargetProfile.ExternalApi),
                executionPolicy,
                classifications,
                observations,
                evidenceAuthority,
                AuditDocument,
                new CampaignPlanningOwnerAuthoritySet(targetAuthorities
                    .Where(target => target.Target.SymbolRef == Target.SymbolRef)
                    .Select(target =>
                    new CampaignPlanningOwnerAuthority([target])).ToImmutableArray()));
            var plan = CampaignPlanner.Plan(planningInput);
            var styleProjection = JsonSerializer.SerializeToElement(new { style = "public-api-v1" });
            var executionCapability = CampaignStateFactory.CreateScribeExecutionCapability(
                executionPolicy,
                agentProjection,
                toolProjection,
                providerProjection);
            var initial = CampaignStateFactory.CreateInitial(
                "style.public-api.v1",
                styleProjection,
                executionCapability,
                Session.InputIdentity,
                planningInput,
                plan);
            var selectedWork = Assert.Single(plan.WorkItems.Where(item =>
                item.Targets.Any(target => target.SymbolRef == Target.SymbolRef)));
            var selectedTarget = Assert.Single(selectedWork.Targets);
            Assert.True(selectedTarget.M3Eligible,
                selectedWork.Disposition.Kind + " " + selectedWork.Disposition.PrimaryTerminalReason);
            return new CampaignExecutionFixture(
                planningInput,
                plan,
                executionCapability,
                m2Projection,
                styleProjection,
                initial);
        }

        private static async Task<CompositionFixture> CreateAsync(
            string[] fixtureSegments,
            string targetDocumentationId,
            string? nonMethodDocumentationId)
        {
            var tempRoot = Path.GetFullPath(Path.GetTempPath());
            var root = Descendant(tempRoot, "contract-scribe-issue-138-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var fixtureRoot = Descendant(
                FindRepositoryRoot(),
                ["tests", "fixtures", .. fixtureSegments]);
            foreach (var file in Directory.EnumerateFiles(fixtureRoot))
            {
                File.Copy(file, Descendant(root, Path.GetFileName(file)));
            }

            LoadedRepositorySession? session = null;
            try
            {
                const string repositoryPath = "Fixture.cs";
                var sourcePath = Descendant(root, repositoryPath);
                var sourceText = await File.ReadAllTextAsync(sourcePath);
                var syntaxTree = CSharpSyntaxTree.ParseText(
                    sourceText,
                    CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest),
                    repositoryPath,
                    Encoding.UTF8);
                var compilation = CSharpCompilation.Create(
                    "Fixture",
                    [syntaxTree],
                    PlatformReferences,
                    new CSharpCompilationOptions(
                        OutputKind.DynamicallyLinkedLibrary,
                        deterministic: true));
                var workspace = new AdhocWorkspace();
                var project = workspace.AddProject("Fixture", LanguageNames.CSharp);
                var loadedProject = new LoadedProject(
                    "Fixture.csproj",
                    "net10.0",
                    "fixture.net10.0",
                    LoadedProjectRole.AuditRoot,
                    [],
                    project,
                    compilation,
                    new Dictionary<SyntaxTree, LoadedSourceTree>(ReferenceEqualityComparer.Instance)
                    {
                        [syntaxTree] = new(
                            LoadedSourceKind.Repository,
                            repositoryPath,
                            new RepositoryPathResolver().PhysicalIdentity(root, sourcePath),
                            null),
                    });
                Assert.True(RepositoryContextRef.TryParse(
                    "repoctx-0123456789abcdef0123456789abcdef",
                    out var repositoryContextRef));
                session = new LoadedRepositorySession(
                    repositoryContextRef,
                    root,
                    "Fixture.csproj",
                    new ToolchainIdentity("test", "test", "test", "test"),
                    [loadedProject],
                    [],
                    workspace,
                    DocumentationScribeContextStableFileReader.ReadDirectoryIdentity(root));
                session.SealDocumentationPatchRepositoryPolicyForTests();
                var classified = new SymbolClassifier().ClassifySession(session, TargetProfile.ExternalApi);
                Assert.Equal(ClassificationRunStatus.Success, classified.Classification.Status);
                var classifications = classified.Classification.ClassificationSet!;
                var target = Assert.Single(
                    classifications.Targets,
                    candidate => candidate.SymbolRef.DocumentationCommentId.StartsWith(
                            targetDocumentationId, StringComparison.Ordinal)
                        && candidate.SupportStatus == SupportStatus.Supported);
                var nonMethod = nonMethodDocumentationId is null
                    ? null
                    : Assert.Single(
                        classifications.Targets,
                        candidate => candidate.SymbolRef.DocumentationCommentId == nonMethodDocumentationId);
                var observed = new DocumentationObserver().Observe(classified);
                Assert.Equal(DocumentationObservationRunStatus.Success, observed.Status);
                var policy = PolicyConfigurationEvaluator.Parse(
                    "{\"schemaVersion\":1,\"targetProfile\":\"profile.external-api\",\"defaultDecision\":\"required\"}"u8.ToArray())
                    .Document ?? throw new InvalidOperationException("policy");
                var extracted = new PolicyEvidenceExtractor().Extract(classified, observed, policy);
                var inputs = AuditInputAssembler.Assemble(classifications, policy, extracted).ToImmutableArray();
                var audit = AuditAggregator.Aggregate(TargetProfile.ExternalApi, classifications, policy, inputs);
                using var auditJson = JsonDocument.Parse(AuditJson.Write(audit));
                var auditOutcome = Assert.Single(
                    auditJson.RootElement.GetProperty("results").EnumerateArray(),
                    row => row.GetProperty("classification") is { } classification
                        && classification.TryGetProperty("symbolRef", out var symbolRef)
                        && symbolRef.GetProperty("documentationCommentId").GetString()
                            == target.SymbolRef.DocumentationCommentId)
                    .GetProperty("auditOutcome").GetString()!;
                var authority = DocumentationScribeAuditAuthority.Create(
                    classified, observed, policy, inputs, audit);
                var selected = authority.Select(target);
                var nonMethodSelected = nonMethod is null ? null : authority.Select(nonMethod);

                var symbol = Assert.Single(Microsoft.CodeAnalysis.DocumentationCommentId
                    .GetSymbolsForDeclarationId(target.SymbolRef.DocumentationCommentId, compilation));
                var syntaxReference = Assert.Single(symbol.DeclaringSyntaxReferences);
                var sourceSha256 = Sha256(File.ReadAllBytes(sourcePath));
                var bootstrapSelection = DocumentationScribeContextValidation.CreateBootstrapSelection(
                    session.RepositoryContextRef,
                    session.InputIdentity,
                    TargetProfile.ExternalApi,
                    target.SymbolRef,
                    repositoryPath,
                    syntaxReference.Span.Start,
                    syntaxReference.Span.End,
                    sourceSha256);
                var bootstrap = new DocumentationScribeContextBootstrapper().Bootstrap(classified, bootstrapSelection);
                Assert.True(bootstrap.Status is DocumentationScribeContextBootstrapStatus.Succeeded
                    or DocumentationScribeContextBootstrapStatus.Incomplete);
                var context = Assert.IsType<DocumentationScribeLoadedContext>(bootstrap.Context);
                var evidence = Assert.Single(context.Facts.Evidence, item => item.KindId == "source.target-declaration");
                var span = Assert.IsType<Utf16Span>(evidence.Range);
                var requestBytes = CreateRequest(
                    session,
                    classifications,
                    target,
                    repositoryPath,
                    span,
                    sourceSha256,
                    context,
                    evidence,
                    extracted,
                    auditOutcome);
                var requestParse = DocumentationScribeValidation.ParseRequest(requestBytes);
                Assert.True(requestParse.IsValid,
                    requestParse.Failure?.Code + " " + requestParse.Failure?.Pointer);
                var request = Assert.IsType<DocumentationScribeRequest>(requestParse.Request);
                Assert.True(DocumentationScribeAttemptId.TryParse(
                    "scribe-attempt." + Guid.NewGuid().ToString("N"), out var attempt));
                return new CompositionFixture(
                    root,
                    sourcePath,
                    session,
                    classified,
                    observed,
                    policy,
                    inputs,
                    audit,
                    extracted,
                    target,
                    selected,
                    nonMethodSelected,
                    requestBytes,
                    request,
                    attempt);
            }
            catch
            {
                if (session is not null)
                {
                    await session.DisposeAsync();
                }

                Directory.Delete(root, recursive: true);
                throw;
            }
        }

        private static ReadOnlyMemory<byte> CreateRequest(
            LoadedRepositorySession session,
            ClassificationSet classifications,
            TargetClassification target,
            string sourcePath,
            Utf16Span targetSpan,
            string sourceSha256,
            DocumentationScribeLoadedContext context,
            DocumentationScribeEvidenceContextFact evidence,
            PolicyEvidenceExtractionOutcome extraction,
            string auditOutcome)
        {
            var components = classifications.Components
                .Where(component => component.ParentSymbolRef == target.SymbolRef
                    && component.SupportStatus == SupportStatus.Supported)
                .OrderBy(component => component.ComponentKind)
                .ThenBy(component => component.Identity, StringComparer.Ordinal)
                .ToArray();
            var applicableComponents = new JsonArray();
            var componentPolicies = new JsonArray();
            foreach (var component in components)
            {
                var kind = ComponentKindId(component.ComponentKind);
                var applicable = new JsonObject
                {
                    ["kind"] = kind,
                    ["identity"] = component.Identity,
                };
                if (component.ComponentKind == ComponentKind.Parameter)
                {
                    applicable["name"] = "value";
                }
                applicableComponents.Add(applicable);
                componentPolicies.Add(new JsonObject
                {
                    ["componentIdentity"] = component.Identity,
                    ["disposition"] = "required",
                    ["maximumScalars"] = 300,
                });
            }

            var contextReferences = new JsonArray();
            foreach (var instruction in context.Facts.Instructions)
            {
                contextReferences.Add(new JsonObject
                {
                    ["contextReferenceId"] = instruction.InstructionId,
                    ["kind"] = "context.project-instruction",
                    ["repositoryContextRef"] = session.RepositoryContextRef.Value,
                    ["path"] = instruction.Commitment.RepositoryPath,
                    ["contentSha256"] = instruction.Commitment.ContentSha256,
                    ["originalUtf8ByteCount"] = instruction.Commitment.OriginalUtf8ByteCount,
                    ["includedUtf8ByteCount"] = instruction.Commitment.IncludedUtf8ByteCount,
                    ["isTruncated"] = instruction.Commitment.IsTruncated,
                });
            }

            var evidenceReferenceItems = new List<JsonObject>
            {
                EvidenceReference(
                    "evidence.source",
                    session,
                    Symbol(target.SymbolRef),
                    sourcePath,
                    targetSpan,
                    evidence,
                    "claim.purpose"),
            };
            foreach (var component in components)
            {
                var binding = extraction.Bindings.Single(candidate =>
                    candidate.Subject.ParentSymbolRef == target.SymbolRef
                    && candidate.Subject.ComponentKind == component.ComponentKind
                    && candidate.Subject.ComponentIdentity == component.Identity);
                var item = Assert.Single(binding.Evidence.Bundle.Items,
                    candidate => binding.Evidence.EvidenceIds.Contains(candidate.EvidenceId, StringComparer.Ordinal));
                var locator = Assert.IsType<RepositoryEvidenceLocator>(item.Locator);
                evidenceReferenceItems.Add(new JsonObject
                {
                    ["evidenceReferenceId"] = item.EvidenceId,
                    ["repositoryContextRef"] = session.RepositoryContextRef.Value,
                    ["subject"] = new JsonObject
                    {
                        ["parentSymbolRef"] = Symbol(target.SymbolRef),
                        ["componentKind"] = ClassificationVocabulary.GetId(component.ComponentKind),
                        ["identity"] = component.Identity,
                    },
                    ["kind"] = EvidenceVocabulary.GetId(item.Kind),
                    ["relation"] = EvidenceVocabulary.GetId(item.Relation),
                    ["authority"] = "authority.source-declaration",
                    ["locator"] = RepositoryLocator(locator.Path, Assert.IsType<Utf16Span>(locator.Span)),
                    ["contentSha256"] = item.Sha256,
                    ["originalUtf8ByteCount"] = item.OriginalUtf8ByteCount,
                    ["includedUtf8ByteCount"] = item.IncludedUtf8ByteCount,
                    ["isTruncated"] = item.IsTruncated,
                    ["claimCategoryIds"] = new JsonArray("claim.behavior"),
                });
            }
            var evidenceReferences = new JsonArray(evidenceReferenceItems
                .OrderBy(item => item["evidenceReferenceId"]!.GetValue<string>(), StringComparer.Ordinal)
                .Select(item => (JsonNode)item)
                .ToArray());

            var root = new JsonObject
            {
                ["scribeRequestVersion"] = 1,
                ["context"] = new JsonObject
                {
                    ["repositoryContextRef"] = session.RepositoryContextRef.Value,
                    ["inputIdentity"] = session.InputIdentity,
                    ["targetProfile"] = "profile.external-api",
                    ["auditOutcome"] = auditOutcome,
                },
                ["target"] = new JsonObject
                {
                    ["symbolRef"] = Symbol(target.SymbolRef),
                    ["sourceCommitment"] = new JsonObject
                    {
                        ["locator"] = RepositoryLocator(sourcePath, targetSpan),
                        ["contentSha256"] = sourceSha256,
                    },
                    ["applicableComponents"] = applicableComponents,
                },
                ["styleProfile"] = new JsonObject
                {
                    ["styleProfileId"] = "style.public-api.v1",
                    ["outputLanguageId"] = "language.en",
                    ["summary"] = Policy("required", 400),
                    ["remarks"] = Policy("forbidden", 400),
                    ["exceptions"] = Policy("forbidden", 400),
                    ["componentPolicies"] = componentPolicies,
                    ["inheritDocDisposition"] = "forbidden",
                    ["allowedLiterals"] = new JsonArray(),
                    ["forbiddenLiterals"] = new JsonArray(),
                    ["claimPolicies"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["claimCategoryId"] = "claim.behavior",
                            ["completeEvidenceRequired"] = false,
                            ["allowedAuthorities"] = new JsonArray("authority.source-declaration"),
                        },
                        new JsonObject
                        {
                            ["claimCategoryId"] = "claim.purpose",
                            ["completeEvidenceRequired"] = false,
                            ["allowedAuthorities"] = new JsonArray("authority.source-declaration"),
                        },
                    },
                    ["maximumContentUnits"] = 8,
                    ["maximumEvidenceRefsPerUnit"] = 4,
                },
                ["contextReferences"] = contextReferences,
                ["evidenceReferences"] = evidenceReferences,
                ["evidenceConflicts"] = new JsonArray(),
                ["toolPolicyId"] = "tool-policy.read-only.v1",
                ["limits"] = new JsonObject
                {
                    ["maximumAttempts"] = 2,
                    ["maximumContextReferences"] = 8,
                    ["maximumContextUtf8Bytes"] = 65536,
                    ["maximumEvidenceReferences"] = 32,
                    ["maximumEvidenceUtf8Bytes"] = 65536,
                    ["maximumProviderRequests"] = 8,
                    ["maximumToolRounds"] = 4,
                    ["maximumToolCalls"] = 16,
                    ["maximumInputTokens"] = 65536,
                    ["maximumUncachedInputTokens"] = 32768,
                    ["maximumOutputTokens"] = 8192,
                    ["maximumCostMicrounits"] = 5000000,
                    ["maximumElapsedMilliseconds"] = 120000,
                },
            };
            return JsonSerializer.SerializeToUtf8Bytes(root);
        }

        private static JsonObject EvidenceReference(
            string evidenceReferenceId,
            LoadedRepositorySession session,
            JsonObject subject,
            string sourcePath,
            Utf16Span targetSpan,
            DocumentationScribeEvidenceContextFact evidence,
            string claimCategoryId) => new()
            {
                ["evidenceReferenceId"] = evidenceReferenceId,
                ["repositoryContextRef"] = session.RepositoryContextRef.Value,
                ["subject"] = subject.ContainsKey("symbolRef") || subject.ContainsKey("parentSymbolRef")
                ? subject
                : new JsonObject { ["symbolRef"] = subject },
                ["kind"] = "evidence.source.declaration",
                ["relation"] = "evidence.declares",
                ["authority"] = "authority.source-declaration",
                ["locator"] = RepositoryLocator(sourcePath, targetSpan),
                ["contentSha256"] = evidence.Commitment.ContentSha256,
                ["originalUtf8ByteCount"] = evidence.Commitment.OriginalUtf8ByteCount,
                ["includedUtf8ByteCount"] = evidence.Commitment.IncludedUtf8ByteCount,
                ["isTruncated"] = evidence.Commitment.IsTruncated,
                ["claimCategoryIds"] = new JsonArray(claimCategoryId),
            };
    }

    private sealed record CampaignExecutionFixture(
        CampaignPlanningInput PlanningInput,
        CampaignWorkPlan Plan,
        CampaignScribeExecutionCapability ExecutionCapability,
        JsonElement M2Projection,
        JsonElement StyleProjection,
        CampaignCheckpointState InitialState)
    {
        internal DocumentationCampaignProposalInput Input(
            CompositionFixture fixture,
            ICampaignCheckpointStore store,
            IDocumentationScribeModelExchange exchange,
            DocumentationScribeRuntimeOptions runtimeOptions,
            CancellationToken executionToken = default,
            CancellationToken settlementToken = default,
            TimeProvider? timeProvider = null) => new(
                fixture.Classified,
                fixture.Observed,
                fixture.Policy,
                fixture.AuditInputs,
                fixture.AuditDocument,
                PlanningInput,
                Plan,
                ExecutionCapability,
                "style.public-api.v1",
                StyleProjection,
                fixture.RequestBytes,
                store,
                runtimeOptions,
                exchange,
                null,
                executionToken,
                settlementToken,
                timeProvider);
    }

    private sealed class MemoryCampaignStore(CampaignCheckpointArtifact initial) : ICampaignCheckpointStore
    {
        private readonly object gate = new();
        private CampaignCheckpointArtifact? current = initial;
        private int replaceAttemptCount;

        internal CampaignCheckpointArtifact? Current
        {
            get
            {
                lock (gate)
                {
                    return current;
                }
            }
        }

        internal int SuccessfulReplaceCount { get; private set; }
        internal CampaignCheckpointWriteKind? NextReportedReplaceKind { get; init; }
        internal bool ApplyNextReplaceBeforeReporting { get; init; }
        internal int? ReportedReplaceAttempt { get; init; }
        internal CampaignCheckpointWriteKind? ReportedReplaceKind { get; init; }
        internal Action<int>? AfterSuccessfulReplace { get; init; }

        public ValueTask<CampaignCheckpointReadResult> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                return ValueTask.FromResult(current is null
                    ? CampaignCheckpointReadResult.NotFound()
                    : CampaignCheckpointReadResult.Found(
                        current.ExactUtf8Json.AsSpan(),
                        current.CheckpointRevision,
                        current.Sha256));
            }
        }

        public ValueTask<CampaignCheckpointWriteResult> CreateIfAbsentAsync(
            ReadOnlyMemory<byte> exactUtf8Json,
            long checkpointRevision,
            string sha256,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (current is not null)
                {
                    return ValueTask.FromResult(new CampaignCheckpointWriteResult(
                        CampaignCheckpointWriteKind.AlreadyPresent));
                }

                current = Parse(exactUtf8Json);
                return ValueTask.FromResult(new CampaignCheckpointWriteResult(CampaignCheckpointWriteKind.Written));
            }
        }

        public ValueTask<CampaignCheckpointWriteResult> ReplaceIfCurrentAsync(
            long expectedCheckpointRevision,
            string expectedSha256,
            ReadOnlyMemory<byte> exactUtf8Json,
            long checkpointRevision,
            string sha256,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                var replaceAttempt = ++replaceAttemptCount;
                if (current is null)
                {
                    return ValueTask.FromResult(new CampaignCheckpointWriteResult(
                        CampaignCheckpointWriteKind.PredecessorMissing));
                }
                if (current.CheckpointRevision != expectedCheckpointRevision
                    || !string.Equals(current.Sha256, expectedSha256, StringComparison.Ordinal))
                {
                    return ValueTask.FromResult(new CampaignCheckpointWriteResult(
                        CampaignCheckpointWriteKind.CurrentMismatch));
                }

                var reportedAttempt = ReportedReplaceAttempt
                    ?? (NextReportedReplaceKind is null ? null : 1);
                var reportedKind = ReportedReplaceKind ?? NextReportedReplaceKind;
                if (reportedAttempt == replaceAttempt && reportedKind is { } reported)
                {
                    if (ApplyNextReplaceBeforeReporting)
                    {
                        current = Parse(exactUtf8Json);
                        SuccessfulReplaceCount++;
                        AfterSuccessfulReplace?.Invoke(SuccessfulReplaceCount);
                    }
                    return ValueTask.FromResult(new CampaignCheckpointWriteResult(reported));
                }

                current = Parse(exactUtf8Json);
                SuccessfulReplaceCount++;
                AfterSuccessfulReplace?.Invoke(SuccessfulReplaceCount);
                return ValueTask.FromResult(new CampaignCheckpointWriteResult(CampaignCheckpointWriteKind.Written));
            }
        }

        private static CampaignCheckpointArtifact Parse(ReadOnlyMemory<byte> bytes) =>
            Assert.IsType<CampaignCheckpointArtifact>(CampaignStateJson.Parse(bytes).Artifact);
    }

    private static CampaignPlanningContentAuthority Content(
        CampaignPlanningContentFamily family,
        string id,
        string value) => Content(family, id, JsonSerializer.SerializeToElement(new { value }));

    private static CampaignPlanningContentAuthority Content(
        CampaignPlanningContentFamily family,
        string id,
        JsonElement projection) => CampaignPlanningContentAuthority.CreateValidatedJsonProjection(
            family,
            id,
            projection);

    private static ImmutableArray<MetadataReference> PlatformReferences { get; } =
        ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InvalidOperationException("Platform assemblies are unavailable."))
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(path => MetadataReference.CreateFromFile(path))
        .ToImmutableArray<MetadataReference>();

    private static string Descendant(string root, params string[] parts)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        if (parts.Any(Path.IsPathRooted))
        {
            throw new ArgumentException("Path components must be relative.", nameof(parts));
        }

        var candidate = Path.GetFullPath(Path.Join([fullRoot, .. parts]));
        var prefix = fullRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Path escaped its fixture root.");
        }

        return candidate;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "ContractScribe.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static JsonObject Policy(string disposition, int maximumScalars) => new()
    {
        ["disposition"] = disposition,
        ["maximumScalars"] = maximumScalars,
    };

    private static JsonObject Symbol(SymbolRef symbol) => new()
    {
        ["compilationContextRef"] = symbol.CompilationContextRef,
        ["documentationCommentId"] = symbol.DocumentationCommentId,
    };

    private static string ComponentKindId(ComponentKind kind) => kind switch
    {
        ComponentKind.Parameter => "parameter",
        ComponentKind.TypeParameter => "type-parameter",
        ComponentKind.Return => "return",
        ComponentKind.Value => "value",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string ComponentKindId(DocumentationPatchComponentKind kind) => kind switch
    {
        DocumentationPatchComponentKind.Parameter => "parameter",
        DocumentationPatchComponentKind.TypeParameter => "type-parameter",
        DocumentationPatchComponentKind.Return => "return",
        DocumentationPatchComponentKind.Value => "value",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static ComponentKind CampaignComponentKind(DocumentationPatchComponentKind kind) => kind switch
    {
        DocumentationPatchComponentKind.Parameter => ComponentKind.Parameter,
        DocumentationPatchComponentKind.TypeParameter => ComponentKind.TypeParameter,
        DocumentationPatchComponentKind.Return => ComponentKind.Return,
        DocumentationPatchComponentKind.Value => ComponentKind.Value,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static JsonObject RepositoryLocator(string path, Utf16Span span) => new()
    {
        ["repository"] = new JsonObject
        {
            ["path"] = path,
            ["span"] = new JsonObject
            {
                ["start"] = span.Start,
                ["end"] = span.End,
            },
        },
    };
}
