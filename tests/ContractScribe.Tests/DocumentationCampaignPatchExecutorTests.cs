using System.Collections.Immutable;
using System.Text.Json;
using ContractScribe.Cli;
using ContractScribe.Core;
using ContractScribe.Patching;

namespace ContractScribe.Tests;

public sealed class DocumentationCampaignPatchExecutorTests
{
    [Fact]
    public void M2_policy_accepts_only_the_exact_committed_closed_projection()
    {
        var projection = JsonSerializer.SerializeToElement(new
        {
            m2ProjectionVersion = 1,
            maximumPatchElapsedMilliseconds = 12_345,
        });
        var execution = ExecutionPolicy(projection, maximumCampaignElapsedMilliseconds: 20_000);

        Assert.True(CampaignM2ExecutionPolicy.TryCreate(projection, execution, out var policy));
        Assert.Equal(12_345, policy!.MaximumPatchElapsedMilliseconds);

        var reordered = Parse("""
            {"maximumPatchElapsedMilliseconds":12345,"m2ProjectionVersion":1}
            """);
        Assert.True(CampaignM2ExecutionPolicy.TryCreate(reordered, execution, out var reorderedPolicy));
        Assert.Equal(policy, reorderedPolicy);

        var substituted = JsonSerializer.SerializeToElement(new
        {
            m2ProjectionVersion = 1,
            maximumPatchElapsedMilliseconds = 12_346,
        });
        Assert.False(CampaignM2ExecutionPolicy.TryCreate(substituted, execution, out _));
    }

    [Theory]
    [InlineData("{\"m2ProjectionVersion\":1,\"maximumPatchElapsedMilliseconds\":12345,\"other\":0}")]
    [InlineData("{\"m2ProjectionVersion\":1,\"m2ProjectionVersion\":1,\"maximumPatchElapsedMilliseconds\":12345}")]
    [InlineData("{\"m2ProjectionVersion\":2,\"maximumPatchElapsedMilliseconds\":12345}")]
    [InlineData("{\"m2ProjectionVersion\":1,\"maximumPatchElapsedMilliseconds\":0}")]
    [InlineData("{\"m2ProjectionVersion\":1,\"maximumPatchElapsedMilliseconds\":20001}")]
    [InlineData("{\"m2ProjectionVersion\":1,\"maximumPatchElapsedMilliseconds\":1.5}")]
    [InlineData("{\"m2ProjectionVersion\":1}")]
    public void M2_policy_rejects_malformed_duplicate_unknown_and_over_bound_input(string json)
    {
        var acceptedProjection = JsonSerializer.SerializeToElement(new
        {
            m2ProjectionVersion = 1,
            maximumPatchElapsedMilliseconds = 12_345,
        });
        var execution = ExecutionPolicy(acceptedProjection, maximumCampaignElapsedMilliseconds: 20_000);

        Assert.False(CampaignM2ExecutionPolicy.TryCreate(Parse(json), execution, out _));
    }

    [Fact]
    public async Task Executor_rejects_uncommitted_policy_before_reading_state_or_dispatching_M2()
    {
        var acceptedProjection = JsonSerializer.SerializeToElement(new
        {
            m2ProjectionVersion = 1,
            maximumPatchElapsedMilliseconds = 12_345,
        });
        var execution = ExecutionPolicy(acceptedProjection, maximumCampaignElapsedMilliseconds: 20_000);
        var planning = new CampaignPlanningInput(
            null!, execution, null!, null!, [], null!, null!);
        var store = new FailingIfCalledStore();
        var substituted = JsonSerializer.SerializeToElement(new
        {
            m2ProjectionVersion = 1,
            maximumPatchElapsedMilliseconds = 12_346,
        });
        var input = new DocumentationCampaignPatchInput(
            null!, null!, null!, [], null!, planning, null!, null!, "style", default,
            substituted, store, CancellationToken.None, CancellationToken.None);

        var outcome = await DocumentationCampaignPatchExecutor.ExecuteAsync(input);

        Assert.Equal(DocumentationCampaignOutcomeKind.HostContractError, outcome.Kind);
        Assert.Equal("campaign.patch.policy-invalid", outcome.Code);
        Assert.Equal(0, store.CallCount);
        Assert.Null(outcome.AcceptedCandidate);
    }

    [Fact]
    public void Nonaccepted_outcomes_cannot_carry_candidate_bytes_and_print_only_bounded_metadata()
    {
        var outcome = new DocumentationCampaignOutcome(
            DocumentationCampaignOutcomeKind.StateConflict,
            "campaign.patch.state-conflict");

        var printed = outcome.ToString();
        Assert.Equal(
            "DocumentationCampaignOutcome { Kind = StateConflict, Code = campaign.patch.state-conflict, HasArtifact = False, HasCandidate = False }",
            printed);
        Assert.DoesNotContain("source", printed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("path", printed, StringComparison.OrdinalIgnoreCase);
        Assert.Null(outcome.AcceptedCandidate);
        Assert.Equal("{}", JsonSerializer.Serialize(outcome));
    }

    [Fact]
    public Task Executor_runs_real_M2_and_reconstructs_the_accepted_candidate() =>
        DocumentationScribeCompositionTests.RunCampaignPatchExecutorRealM2Async();

    [Fact]
    public Task Executor_fails_closed_before_dispatch_for_stale_source_and_store_conflict() =>
        DocumentationScribeCompositionTests.RunCampaignPatchPreDispatchFailuresAsync();

    [Fact]
    public Task Executor_preserves_the_charged_reservation_after_an_abrupt_dispatch_exit() =>
        DocumentationScribeCompositionTests.RunCampaignPatchAbruptExitAsync();

    [Fact]
    public Task Executor_preserves_settlement_revision_headroom() =>
        DocumentationScribeCompositionTests.RunCampaignPatchRevisionHeadroomAsync();

    [Fact]
    public Task Executor_settles_stale_cancellation_and_timeout_as_distinct_closed_outcomes() =>
        DocumentationScribeCompositionTests.RunCampaignPatchStopOutcomesAsync();

    [Fact]
    public Task Composer_rebinds_every_trusted_proposal_field_to_exact_current_C1_work() =>
        DocumentationScribeCompositionTests.RunCampaignPatchCurrentAuthoritySubstitutionsAsync();

    [Fact]
    public Task Executor_preserves_post_M2_pre_transition_crashes_for_conservative_retry() =>
        DocumentationScribeCompositionTests.RunCampaignPatchPostExecutionCrashAsync();

    [Fact]
    public Task Executor_settles_exact_elapsed_above_reservation_and_retains_unknown_exposure() =>
        DocumentationScribeCompositionTests.RunCampaignPatchElapsedAuthorityAsync();

    private static CampaignPlanningExecutionPolicy ExecutionPolicy(
        JsonElement m2Projection,
        long maximumCampaignElapsedMilliseconds)
    {
        var limits = new DocumentationScribeRunLimits(
            maximumContextReferences: 8,
            maximumContextUtf8Bytes: 65_536,
            maximumEvidenceReferences: 32,
            maximumEvidenceUtf8Bytes: 65_536,
            maximumProviderRequests: 8,
            maximumToolRounds: 4,
            maximumToolCalls: 16,
            maximumAttempts: 2,
            maximumInputTokens: 65_536,
            maximumUncachedInputTokens: 32_768,
            maximumOutputTokens: 8_192,
            maximumCostMicrounits: 5_000_000,
            maximumElapsedMilliseconds: 120_000);
        var budget = new CampaignPlanningBudgetPolicy(
            maximumBlocks: 32,
            maximumChangedFiles: 8,
            maximumPatchBytes: 1_000_000,
            maximumProviderRequests: 64,
            maximumAttemptsPerTarget: 3,
            maximumInputTokens: 1_000_000,
            maximumUncachedInputTokens: 500_000,
            maximumOutputTokens: 100_000,
            maximumCostMicrounits: 5_000_000,
            maximumElapsedMilliseconds: maximumCampaignElapsedMilliseconds,
            maximumCandidatesPerBlock: 8,
            costEnforced: false,
            costCurrency: null,
            costRatePolicy: null);
        return new CampaignPlanningExecutionPolicy(
            limits,
            budget,
            Content(CampaignPlanningContentFamily.ProposalContract, "proposal"),
            Content(CampaignPlanningContentFamily.AgentProtocol, "agent"),
            Content(CampaignPlanningContentFamily.ContextSelectionPolicy, "context"),
            Content(CampaignPlanningContentFamily.ToolPolicyAndRegistry, "tools"),
            Content(CampaignPlanningContentFamily.ProviderModelRequestProfile, "provider"),
            Content(CampaignPlanningContentFamily.RetryPolicy, "retry"),
            CampaignPlanningContentAuthority.CreateValidatedJsonProjection(
                CampaignPlanningContentFamily.M2ProjectionPolicy,
                "m2",
                m2Projection),
            Content(CampaignPlanningContentFamily.ProductContractRevision, "product"));
    }

    private static CampaignPlanningContentAuthority Content(
        CampaignPlanningContentFamily family,
        string id) => CampaignPlanningContentAuthority.CreateValidatedJsonProjection(
            family,
            id,
            JsonSerializer.SerializeToElement(new { value = id }));

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class FailingIfCalledStore : ICampaignCheckpointStore
    {
        internal int CallCount { get; private set; }

        public ValueTask<CampaignCheckpointReadResult> ReadAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException("Store must not be called.");
        }

        public ValueTask<CampaignCheckpointWriteResult> CreateIfAbsentAsync(
            ReadOnlyMemory<byte> exactUtf8Json,
            long checkpointRevision,
            string sha256,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException("Store must not be called.");
        }

        public ValueTask<CampaignCheckpointWriteResult> ReplaceIfCurrentAsync(
            long expectedCheckpointRevision,
            string expectedSha256,
            ReadOnlyMemory<byte> exactUtf8Json,
            long checkpointRevision,
            string sha256,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException("Store must not be called.");
        }
    }
}

public sealed partial class DocumentationScribeCompositionTests
{
    internal static async Task RunCampaignPatchExecutorRealM2Async()
    {
        await using var fixture = await CompositionFixture.CreateProposalStageAsync();
        var campaign = fixture.CreateCampaign();
        var initial = CampaignStateJson.CreateArtifact(campaign.InitialState);
        var store = new MemoryCampaignStore(initial);
        var proposal = await DocumentationCampaignProposalExecutor.ExecuteAsync(
            campaign.Input(fixture, store, new ProposalExchange(fixture.Request), RuntimeOptions()));
        Assert.Equal(DocumentationCampaignProposalOutcomeKind.ProposalReady, proposal.Kind);
        var sourceBefore = await File.ReadAllBytesAsync(fixture.SourcePath);
        _ = CumulativeDocumentationPatchComposer.Compose(
            fixture.Classified,
            campaign.PlanningInput,
            campaign.Plan,
            DocumentationScribeAuditAuthority.Create(
                fixture.Classified,
                fixture.Observed,
                fixture.Policy,
                fixture.AuditInputs,
                fixture.AuditDocument),
            proposal.Artifact!.State,
            acceptedOnly: false,
            CancellationToken.None);
        var input = new DocumentationCampaignPatchInput(
            fixture.Classified,
            fixture.Observed,
            fixture.Policy,
            fixture.AuditInputs,
            fixture.AuditDocument,
            campaign.PlanningInput,
            campaign.Plan,
            campaign.ExecutionCapability,
            "style.public-api.v1",
            campaign.StyleProjection,
            campaign.M2Projection,
            store,
            CancellationToken.None,
            CancellationToken.None);

        var accepted = await DocumentationCampaignPatchExecutor.ExecuteAsync(input);

        if (!OperatingSystem.IsLinux())
        {
            Assert.Equal(DocumentationCampaignOutcomeKind.HostFailure, accepted.Kind);
            Assert.Null(accepted.AcceptedCandidate);
            Assert.Null(accepted.Artifact?.State.ActiveReservation);
            Assert.Equal(sourceBefore, await File.ReadAllBytesAsync(fixture.SourcePath));
            return;
        }

        Assert.True(accepted.Kind == DocumentationCampaignOutcomeKind.Accepted, accepted.Code);
        Assert.NotNull(accepted.AcceptedCandidate);
        Assert.NotNull(accepted.Artifact?.State.CandidateObservation);
        Assert.Equal(CampaignCumulativeOutcomeKind.Accepted,
            accepted.Artifact!.State.CumulativeOutcome!.Kind);
        Assert.Null(accepted.Artifact.State.ActiveReservation);
        Assert.Equal(sourceBefore, await File.ReadAllBytesAsync(fixture.SourcePath));

        var reconstructed = await DocumentationCampaignPatchExecutor.ExecuteAsync(input);

        Assert.Equal(DocumentationCampaignOutcomeKind.Reconstructed, reconstructed.Kind);
        Assert.NotNull(reconstructed.AcceptedCandidate);
        Assert.Equal(accepted.Artifact.State.CandidateObservation!.AcceptedProjectionCommitmentSha256,
            reconstructed.Artifact!.State.CandidateObservation!.AcceptedProjectionCommitmentSha256);
        Assert.Equal(sourceBefore, await File.ReadAllBytesAsync(fixture.SourcePath));
    }

    internal static async Task RunCampaignPatchPreDispatchFailuresAsync()
    {
        await using var staleFixture = await CompositionFixture.CreateProposalStageAsync();
        var staleCampaign = staleFixture.CreateCampaign();
        var staleStore = new MemoryCampaignStore(
            CampaignStateJson.CreateArtifact(staleCampaign.InitialState));
        var proposal = await DocumentationCampaignProposalExecutor.ExecuteAsync(
            staleCampaign.Input(
                staleFixture,
                staleStore,
                new ProposalExchange(staleFixture.Request),
                RuntimeOptions()));
        Assert.Equal(DocumentationCampaignProposalOutcomeKind.ProposalReady, proposal.Kind);
        var replacementsBeforePatch = staleStore.SuccessfulReplaceCount;
        await File.AppendAllTextAsync(staleFixture.SourcePath, "// stale\n");
        var staleDispatches = 0;
        var staleEngine = new DocumentationPatchEngine(
            stagingParentFactory: null,
            (_, _) => staleDispatches++,
            observer: null);

        var stale = await DocumentationCampaignPatchExecutor.ExecuteAsync(
            PatchInput(staleFixture, staleCampaign, staleStore, patchEngine: staleEngine));

        Assert.Equal(DocumentationCampaignOutcomeKind.HostContractError, stale.Kind);
        Assert.Equal(0, staleDispatches);
        Assert.Equal(replacementsBeforePatch, staleStore.SuccessfulReplaceCount);
        Assert.Null(stale.AcceptedCandidate);

        await using var conflictFixture = await CompositionFixture.CreateProposalStageAsync();
        var conflictCampaign = conflictFixture.CreateCampaign();
        var conflictStore = new MemoryCampaignStore(
            CampaignStateJson.CreateArtifact(conflictCampaign.InitialState))
        {
            ReportedReplaceAttempt = 3,
            ReportedReplaceKind = CampaignCheckpointWriteKind.CurrentMismatch,
        };
        proposal = await DocumentationCampaignProposalExecutor.ExecuteAsync(
            conflictCampaign.Input(
                conflictFixture,
                conflictStore,
                new ProposalExchange(conflictFixture.Request),
                RuntimeOptions()));
        Assert.Equal(DocumentationCampaignProposalOutcomeKind.ProposalReady, proposal.Kind);
        var conflictDispatches = 0;
        var conflictEngine = new DocumentationPatchEngine(
            stagingParentFactory: null,
            (_, _) => conflictDispatches++,
            observer: null);

        var conflict = await DocumentationCampaignPatchExecutor.ExecuteAsync(
            PatchInput(conflictFixture, conflictCampaign, conflictStore, patchEngine: conflictEngine));

        Assert.Equal(DocumentationCampaignOutcomeKind.StateConflict, conflict.Kind);
        Assert.Equal(0, conflictDispatches);
        Assert.Equal(2, conflictStore.SuccessfulReplaceCount);
        Assert.Null(conflict.AcceptedCandidate);
    }

    internal static async Task RunCampaignPatchAbruptExitAsync()
    {
        await using var fixture = await CompositionFixture.CreateProposalStageAsync();
        var campaign = fixture.CreateCampaign();
        var store = new MemoryCampaignStore(CampaignStateJson.CreateArtifact(campaign.InitialState));
        var proposal = await DocumentationCampaignProposalExecutor.ExecuteAsync(
            campaign.Input(fixture, store, new ProposalExchange(fixture.Request), RuntimeOptions()));
        Assert.Equal(DocumentationCampaignProposalOutcomeKind.ProposalReady, proposal.Kind);
        var engine = new DocumentationPatchEngine(
            stagingParentFactory: null,
            (_, _) => throw new SimulatedPatchProcessExitException(),
            observer: null);

        var outcome = await DocumentationCampaignPatchExecutor.ExecuteAsync(
            PatchInput(fixture, campaign, store, patchEngine: engine));

        Assert.Equal(DocumentationCampaignOutcomeKind.AmbiguousDispatch, outcome.Kind);
        Assert.Equal("campaign.patch.dispatch-unconfirmed", outcome.Code);
        Assert.IsType<CampaignPatchReservation>(store.Current!.State.ActiveReservation);
        Assert.Equal(3, store.SuccessfulReplaceCount);
        Assert.Null(outcome.AcceptedCandidate);
    }

    internal static async Task RunCampaignPatchRevisionHeadroomAsync()
    {
        await using var fixture = await CompositionFixture.CreateProposalStageAsync();
        var campaign = fixture.CreateCampaign();
        var proposalStore = new MemoryCampaignStore(CampaignStateJson.CreateArtifact(campaign.InitialState));
        var proposal = await DocumentationCampaignProposalExecutor.ExecuteAsync(
            campaign.Input(fixture, proposalStore, new ProposalExchange(fixture.Request), RuntimeOptions()));
        Assert.Equal(DocumentationCampaignProposalOutcomeKind.ProposalReady, proposal.Kind);
        var nearMaximum = WithCheckpointRevision(
            proposal.Artifact!,
            CampaignStateContract.MaximumObservation - 1);
        var store = new MemoryCampaignStore(nearMaximum);
        var dispatches = 0;
        var engine = new DocumentationPatchEngine(
            stagingParentFactory: null,
            (_, _) => dispatches++,
            observer: null);

        var exhausted = await DocumentationCampaignPatchExecutor.ExecuteAsync(
            PatchInput(fixture, campaign, store, patchEngine: engine));

        Assert.Equal(DocumentationCampaignOutcomeKind.BudgetExhausted, exhausted.Kind);
        Assert.Equal(CampaignStateContract.MaximumObservation, store.Current!.CheckpointRevision);
        Assert.Equal(1, store.SuccessfulReplaceCount);
        Assert.Equal(0, dispatches);
        Assert.Null(exhausted.AcceptedCandidate);

        var maximumStore = new MemoryCampaignStore(WithCheckpointRevision(
            proposal.Artifact!,
            CampaignStateContract.MaximumObservation));
        var maximum = await DocumentationCampaignPatchExecutor.ExecuteAsync(
            PatchInput(fixture, campaign, maximumStore, patchEngine: engine));
        Assert.Equal(DocumentationCampaignOutcomeKind.StateConflict, maximum.Kind);
        Assert.Equal(0, maximumStore.SuccessfulReplaceCount);
        Assert.Equal(0, dispatches);
    }

    internal static async Task RunCampaignPatchStopOutcomesAsync()
    {
        await using var staleFixture = await CompositionFixture.CreateProposalStageAsync();
        var staleCampaign = staleFixture.CreateCampaign();
        var staleStore = await ProposalReadyStore(staleFixture, staleCampaign);
        var staleEngine = new DocumentationPatchEngine(
            stagingParentFactory: null,
            (stage, _) =>
            {
                if (stage == DocumentationPatchApplicationStage.BaselineCaptured)
                {
                    File.AppendAllText(staleFixture.SourcePath, "// changed after admission\n");
                }
            },
            observer: null);
        var stale = await DocumentationCampaignPatchExecutor.ExecuteAsync(
            PatchInput(staleFixture, staleCampaign, staleStore, patchEngine: staleEngine));
        Assert.Equal(
            OperatingSystem.IsLinux()
                ? DocumentationCampaignOutcomeKind.Stale
                : DocumentationCampaignOutcomeKind.HostFailure,
            stale.Kind);
        Assert.Null(staleStore.Current!.State.ActiveReservation);
        Assert.Null(stale.AcceptedCandidate);

        await using var cancelledFixture = await CompositionFixture.CreateProposalStageAsync();
        var cancelledCampaign = cancelledFixture.CreateCampaign();
        var cancelledStore = await ProposalReadyStore(cancelledFixture, cancelledCampaign);
        using var caller = new CancellationTokenSource();
        var cancelledEngine = new DocumentationPatchEngine(
            stagingParentFactory: null,
            (stage, _) =>
            {
                if (stage == DocumentationPatchApplicationStage.BaselineCaptured)
                {
                    caller.Cancel();
                }
            },
            observer: null);
        var cancelled = await DocumentationCampaignPatchExecutor.ExecuteAsync(
            PatchInput(
                cancelledFixture,
                cancelledCampaign,
                cancelledStore,
                executionToken: caller.Token,
                patchEngine: cancelledEngine));
        Assert.Equal(DocumentationCampaignOutcomeKind.Cancelled, cancelled.Kind);
        Assert.Null(cancelledStore.Current!.State.ActiveReservation);
        Assert.Null(cancelled.AcceptedCandidate);

        await using var timeoutFixture = await CompositionFixture.CreateProposalStageAsync();
        var timeoutCampaign = timeoutFixture.CreateCampaign(maximumPatchElapsedMilliseconds: 1);
        var timeoutStore = await ProposalReadyStore(timeoutFixture, timeoutCampaign);
        var timedOut = await DocumentationCampaignPatchExecutor.ExecuteAsync(
            PatchInput(
                timeoutFixture,
                timeoutCampaign,
                timeoutStore,
                timeProvider: new ImmediateDeadlineTimeProvider()));
        Assert.Equal(DocumentationCampaignOutcomeKind.TimedOut, timedOut.Kind);
        Assert.Null(timeoutStore.Current!.State.ActiveReservation);
        Assert.Null(timedOut.AcceptedCandidate);

        await using var longDeadlineFixture = await CompositionFixture.CreateProposalStageAsync();
        const long longDeadline = (long)int.MaxValue + 1_000;
        var longDeadlineCampaign = longDeadlineFixture.CreateCampaign(
            longDeadline,
            maximumCampaignElapsedMilliseconds: longDeadline);
        var longDeadlineStore = await ProposalReadyStore(longDeadlineFixture, longDeadlineCampaign);
        var longDeadlineOutcome = await DocumentationCampaignPatchExecutor.ExecuteAsync(
            PatchInput(longDeadlineFixture, longDeadlineCampaign, longDeadlineStore));
        Assert.NotEqual(DocumentationCampaignOutcomeKind.AmbiguousDispatch, longDeadlineOutcome.Kind);
        Assert.Null(longDeadlineStore.Current!.State.ActiveReservation);
    }

    internal static async Task RunCampaignPatchCurrentAuthoritySubstitutionsAsync()
    {
        await using var fixture = await CompositionFixture.CreateProposalStageAsync();
        var campaign = fixture.CreateCampaign();
        var store = await ProposalReadyStore(fixture, campaign);
        var state = store.Current!.State;
        var stateWork = Assert.Single(state.WorkItems.Where(item =>
            item.Status == CampaignWorkStatus.ProposalComplete));
        var planWork = Assert.Single(campaign.Plan.WorkItems.Where(item =>
            string.Equals(item.WorkItemKey, stateWork.WorkItemKey, StringComparison.Ordinal)));
        var target = Assert.Single(planWork.Targets);

        CampaignStateFactory.ValidateCurrentTrustedProposalAuthority(state, stateWork, planWork);

        var alternateSymbol = new SymbolRef(
            target.SymbolRef.CompilationContextRef,
            "M:Substituted.Authority.Target");
        AssertRejected(Target(target, symbolRef: alternateSymbol));

        var repository = Assert.IsType<CampaignPlanningRepositorySourceAuthority>(target.Source);
        var shiftedSpan = new Utf16Span(
            repository.RequestedDeclarationSpan.Start,
            repository.RequestedDeclarationSpan.End - 1);
        AssertRejected(Target(target, source: new CampaignPlanningRepositorySourceAuthority(
            repository.Path,
            repository.PhysicalSourceCommitmentSha256,
            repository.ObservedSourceTextSha256,
            repository.AuthoritativeDeclarationId,
            repository.ContentSha256,
            repository.Encoding,
            repository.ObservationDeclarationSpan,
            shiftedSpan,
            repository.CanonicalDeclarationSpan,
            repository.OwnerSpan,
            repository.DocumentationSpan,
            repository.BlockState)));
        AssertRejected(Target(target, components: []));
        AssertRejected(Target(target, styleProfile: Style(target.StyleProfile!, "style.substituted.v1")));

        var oppositeCapability = planWork.Disposition.EditCapability == CampaignPlanningEditCapability.Insert
            ? CampaignPlanningEditCapability.Replace
            : CampaignPlanningEditCapability.Insert;
        var changedDisposition = new CampaignPlanningDisposition(
            CampaignPlanningDispositionKind.Executable,
            oppositeCapability,
            null,
            []);
        Assert.Throws<CampaignStateValidationException>(() => CampaignStateFactory.ValidateCurrentTrustedProposalAuthority(
            state,
            stateWork,
            new CampaignPlanningWorkItem(
                planWork.WorkItemKey,
                planWork.OwnerEquivalenceRef,
                planWork.Targets,
                planWork.ViolationCauses,
                changedDisposition)));

        Assert.Throws<CampaignStateValidationException>(() => CampaignStateFactory.ValidateCurrentTrustedProposalAuthority(
            state,
            stateWork with { OuterAttemptCount = checked(stateWork.OuterAttemptCount + 1) },
            planWork));

        var evidence = fixture.Request.EvidenceReferences.First(item =>
            item.Subject is TargetEvidenceSubject);
        var dynamicInput = new DocumentationScribeDynamicEvidenceInput(
            evidence.Subject,
            evidence.Kind,
            evidence.Relation,
            evidence.Authority,
            evidence.Locator,
            evidence.ContentSha256,
            evidence.OriginalUtf8ByteCount,
            evidence.IncludedUtf8ByteCount,
            evidence.IsTruncated,
            evidence.ClaimCategoryIds);
        Assert.True(DocumentationScribeValidation.TryCreateDynamicEvidenceReference(
            fixture.Request,
            dynamicInput,
            out var dynamicReference));
        Assert.NotNull(dynamicReference);
        Assert.True(StableDynamic(dynamicReference!));
        Assert.False(StableDynamic(Evidence(
            dynamicReference!,
            subject: new TargetEvidenceSubject(alternateSymbol))));
        Assert.False(StableDynamic(Evidence(
            dynamicReference!,
            relation: dynamicReference.Relation == EvidenceRelation.Declares
                ? EvidenceRelation.References
                : EvidenceRelation.Declares)));
        Assert.False(StableDynamic(Evidence(
            dynamicReference!,
            kind: EvidenceKind.Test,
            authority: DocumentationScribeEvidenceAuthority.Test)));
        Assert.False(StableDynamic(Evidence(
            dynamicReference!,
            locator: new GeneratedOutputEvidenceLocator(
                GeneratedOutputKind.SourceGenerator,
                "sgp." + new string('a', 64),
                "sgo." + new string('b', 64),
                dynamicReference.ContentSha256,
                null))));
        Assert.False(StableDynamic(Evidence(
            dynamicReference!,
            claimCategoryIds: ["claim.substituted"])));

        void AssertRejected(CampaignPlanningTargetFact changedTarget) =>
            Assert.Throws<CampaignStateValidationException>(() => CampaignStateFactory.ValidateCurrentTrustedProposalAuthority(
                state,
                stateWork,
                new CampaignPlanningWorkItem(
                    planWork.WorkItemKey,
                    planWork.OwnerEquivalenceRef,
                    [changedTarget],
                    planWork.ViolationCauses,
                    planWork.Disposition)));

        static bool StableDynamic(DocumentationScribeEvidenceReference item) =>
            DocumentationScribeValidation.HasStableDynamicEvidenceIdentity(
                item.EvidenceReferenceId,
                item.Subject,
                item.Kind,
                item.Relation,
                item.Authority,
                item.Locator,
                item.ContentSha256,
                item.OriginalUtf8ByteCount,
                item.IncludedUtf8ByteCount,
                item.IsTruncated,
                item.ClaimCategoryIds);
    }

    internal static async Task RunCampaignPatchPostExecutionCrashAsync()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        await using var acceptedFixture = await CompositionFixture.CreateProposalStageAsync();
        var acceptedCampaign = acceptedFixture.CreateCampaign();
        var acceptedStore = await ProposalReadyStore(acceptedFixture, acceptedCampaign);
        var acceptedCrash = PatchInput(
            acceptedFixture,
            acceptedCampaign,
            acceptedStore,
            afterPatchExecutionObserver: () => throw new SimulatedPatchProcessExitException());

        await Assert.ThrowsAsync<SimulatedPatchProcessExitException>(() =>
            DocumentationCampaignPatchExecutor.ExecuteAsync(acceptedCrash));
        var acceptedReservation = Assert.IsType<CampaignPatchReservation>(
            acceptedStore.Current!.State.ActiveReservation);
        Assert.Null(acceptedStore.Current.State.CumulativeOutcome);

        var acceptedRetry = await DocumentationCampaignPatchExecutor.ExecuteAsync(
            PatchInput(acceptedFixture, acceptedCampaign, acceptedStore));
        Assert.Equal(DocumentationCampaignOutcomeKind.Accepted, acceptedRetry.Kind);
        Assert.NotNull(acceptedRetry.AcceptedCandidate);
        Assert.Null(acceptedRetry.Artifact!.State.ActiveReservation);
        Assert.NotEqual(
            acceptedReservation.ExpectedCheckpointRevision,
            acceptedRetry.Artifact.State.CumulativeOutcome!.CompletedFromCheckpointRevision);

        await using var staleFixture = await CompositionFixture.CreateProposalStageAsync();
        var staleCampaign = staleFixture.CreateCampaign();
        var staleStore = await ProposalReadyStore(staleFixture, staleCampaign);
        var originalBytes = await File.ReadAllBytesAsync(staleFixture.SourcePath);
        var staleEngine = new DocumentationPatchEngine(
            stagingParentFactory: null,
            (stage, _) =>
            {
                if (stage == DocumentationPatchApplicationStage.BaselineCaptured)
                {
                    File.AppendAllText(staleFixture.SourcePath, "// stale after dispatch\n");
                }
            },
            observer: null);
        await Assert.ThrowsAsync<SimulatedPatchProcessExitException>(() =>
            DocumentationCampaignPatchExecutor.ExecuteAsync(PatchInput(
                staleFixture,
                staleCampaign,
                staleStore,
                patchEngine: staleEngine,
                afterPatchExecutionObserver: () => throw new SimulatedPatchProcessExitException())));
        Assert.IsType<CampaignPatchReservation>(staleStore.Current!.State.ActiveReservation);
        Assert.Null(staleStore.Current.State.CumulativeOutcome);
        await File.WriteAllBytesAsync(staleFixture.SourcePath, originalBytes);

        var staleRetry = await DocumentationCampaignPatchExecutor.ExecuteAsync(
            PatchInput(staleFixture, staleCampaign, staleStore));
        Assert.Equal(DocumentationCampaignOutcomeKind.Accepted, staleRetry.Kind);
        Assert.Null(staleRetry.Artifact!.State.ActiveReservation);
    }

    internal static async Task RunCampaignPatchElapsedAuthorityAsync()
    {
        var exactClock = new FixedElapsedTimeProvider(10_001, 10_000);
        Assert.Equal(1_001, DocumentationCampaignPatchExecutor.ObserveElapsedMilliseconds(
            exactClock,
            exactClock.GetTimestamp()));
        var unrepresentableClock = new FixedElapsedTimeProvider(1, 0);
        Assert.Null(DocumentationCampaignPatchExecutor.ObserveElapsedMilliseconds(
            unrepresentableClock,
            unrepresentableClock.GetTimestamp()));

        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        await using var overrunFixture = await CompositionFixture.CreateProposalStageAsync();
        var overrunCampaign = overrunFixture.CreateCampaign(
            maximumPatchElapsedMilliseconds: 1_000,
            maximumCampaignElapsedMilliseconds: 120_000);
        var overrunStore = await ProposalReadyStore(overrunFixture, overrunCampaign);
        var dispatches = 0;
        var overrunEngine = new DocumentationPatchEngine(
            stagingParentFactory: null,
            (_, _) => dispatches++,
            observer: null);
        var overrun = await DocumentationCampaignPatchExecutor.ExecuteAsync(PatchInput(
            overrunFixture,
            overrunCampaign,
            overrunStore,
            patchEngine: overrunEngine,
            timeProvider: new FixedElapsedTimeProvider(1_200_001, 10_000)));

        Assert.Equal(DocumentationCampaignOutcomeKind.BudgetExhausted, overrun.Kind);
        Assert.Equal(CampaignCumulativeOutcomeKind.OverBound, overrun.Artifact!.State.CumulativeOutcome!.Kind);
        Assert.Equal(120_001, overrun.Artifact.State.LineageCharges.ActiveElapsedMilliseconds.Observed);
        Assert.Equal(0, overrun.Artifact.State.LineageCharges.ActiveElapsedMilliseconds.ConservativeUnobserved);
        Assert.True(overrun.Artifact.State.LineageCharges.ActiveElapsedMilliseconds.TotalCharged >= 120_001);
        Assert.Null(overrun.AcceptedCandidate);
        var firstDispatches = dispatches;

        var replay = await DocumentationCampaignPatchExecutor.ExecuteAsync(PatchInput(
            overrunFixture,
            overrunCampaign,
            overrunStore,
            patchEngine: overrunEngine));
        Assert.Equal(DocumentationCampaignOutcomeKind.BudgetExhausted, replay.Kind);
        Assert.Equal(firstDispatches, dispatches);

        await using var unknownFixture = await CompositionFixture.CreateProposalStageAsync();
        var unknownCampaign = unknownFixture.CreateCampaign(
            maximumPatchElapsedMilliseconds: 1_000,
            maximumCampaignElapsedMilliseconds: 120_000);
        var unknownStore = await ProposalReadyStore(unknownFixture, unknownCampaign);
        var unknown = await DocumentationCampaignPatchExecutor.ExecuteAsync(PatchInput(
            unknownFixture,
            unknownCampaign,
            unknownStore,
            timeProvider: new FixedElapsedTimeProvider(1, 0)));

        Assert.Equal(DocumentationCampaignOutcomeKind.Accepted, unknown.Kind);
        Assert.Null(unknown.Artifact!.State.LineageCharges.ActiveElapsedMilliseconds.Observed);
        Assert.Equal(1_000, unknown.Artifact.State.LineageCharges.ActiveElapsedMilliseconds.ConservativeUnobserved);
        Assert.True(unknown.Artifact.State.LineageCharges.ActiveElapsedMilliseconds.TotalCharged >= 1_000);
    }

    private static CampaignPlanningTargetFact Target(
        CampaignPlanningTargetFact basis,
        SymbolRef? symbolRef = null,
        CampaignPlanningSourceAuthority? source = null,
        ImmutableArray<CampaignPlanningApplicableComponent>? components = null,
        DocumentationScribeStyleProfile? styleProfile = null) => new(
            symbolRef ?? basis.SymbolRef,
            basis.PrimaryKind,
            basis.Origin,
            source ?? basis.Source,
            (source ?? basis.Source).AuthoritativeDeclarationId,
            components ?? basis.ApplicableComponents,
            basis.OwnerSymbolRefs,
            basis.AuditOutcome,
            basis.AuditReason,
            basis.AuditRowSha256,
            basis.M3Eligible,
            styleProfile ?? basis.StyleProfile);

    private static DocumentationScribeStyleProfile Style(
        DocumentationScribeStyleProfile basis,
        string id) => new(
            id,
            basis.OutputLanguageId,
            basis.Summary,
            basis.Remarks,
            basis.Exceptions,
            basis.ComponentPolicies,
            basis.InheritDocDisposition,
            basis.AllowedLiterals,
            basis.ForbiddenLiterals,
            basis.ClaimPolicies,
            basis.MaximumContentUnits,
            basis.MaximumEvidenceRefsPerUnit);

    private static DocumentationScribeEvidenceReference Evidence(
        DocumentationScribeEvidenceReference basis,
        EvidenceSubject? subject = null,
        EvidenceKind? kind = null,
        EvidenceRelation? relation = null,
        DocumentationScribeEvidenceAuthority? authority = null,
        EvidenceLocator? locator = null,
        ImmutableArray<string>? claimCategoryIds = null) => new(
            basis.EvidenceReferenceId,
            basis.RepositoryContextRef,
            subject ?? basis.Subject,
            kind ?? basis.Kind,
            relation ?? basis.Relation,
            authority ?? basis.Authority,
            locator ?? basis.Locator,
            basis.ContentSha256,
            basis.OriginalUtf8ByteCount,
            basis.IncludedUtf8ByteCount,
            basis.IsTruncated,
            claimCategoryIds ?? basis.ClaimCategoryIds);

    private static async Task<MemoryCampaignStore> ProposalReadyStore(
        CompositionFixture fixture,
        CampaignExecutionFixture campaign)
    {
        var store = new MemoryCampaignStore(CampaignStateJson.CreateArtifact(campaign.InitialState));
        var proposal = await DocumentationCampaignProposalExecutor.ExecuteAsync(
            campaign.Input(fixture, store, new ProposalExchange(fixture.Request), RuntimeOptions()));
        Assert.Equal(DocumentationCampaignProposalOutcomeKind.ProposalReady, proposal.Kind);
        return store;
    }

    private static DocumentationCampaignPatchInput PatchInput(
        CompositionFixture fixture,
        CampaignExecutionFixture campaign,
        ICampaignCheckpointStore store,
        CancellationToken executionToken = default,
        CancellationToken settlementToken = default,
        DocumentationPatchEngine? patchEngine = null,
        TimeProvider? timeProvider = null,
        Action? afterPatchExecutionObserver = null) => new(
            fixture.Classified,
            fixture.Observed,
            fixture.Policy,
            fixture.AuditInputs,
            fixture.AuditDocument,
            campaign.PlanningInput,
            campaign.Plan,
            campaign.ExecutionCapability,
            "style.public-api.v1",
            campaign.StyleProjection,
            campaign.M2Projection,
            store,
            executionToken,
            settlementToken,
            patchEngine,
            timeProvider,
            afterPatchExecutionObserver);

    private sealed class SimulatedPatchProcessExitException : Exception;

    private sealed class ImmediateDeadlineTimeProvider : TimeProvider
    {
        private int timestampReads;

        public override long TimestampFrequency => 1_000;

        public override long GetTimestamp() => Interlocked.Increment(ref timestampReads) == 1 ? 0 : 1;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            callback(state);
            return new NoopTimer();
        }
    }

    private sealed class FixedElapsedTimeProvider(long elapsedTicks, long frequency) : TimeProvider
    {
        private int timestampReads;

        public override long TimestampFrequency => frequency;

        public override long GetTimestamp() =>
            Interlocked.Increment(ref timestampReads) == 1 ? 0 : elapsedTicks;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) => new NoopTimer();
    }

    private sealed class NoopTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
