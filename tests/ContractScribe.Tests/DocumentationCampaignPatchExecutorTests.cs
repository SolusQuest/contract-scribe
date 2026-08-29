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
        var timeoutEngine = new DocumentationPatchEngine(
            stagingParentFactory: null,
            (stage, _) =>
            {
                if (stage == DocumentationPatchApplicationStage.BaselineCaptured)
                {
                    Thread.Sleep(20);
                }
            },
            observer: null);
        var timedOut = await DocumentationCampaignPatchExecutor.ExecuteAsync(
            PatchInput(timeoutFixture, timeoutCampaign, timeoutStore, patchEngine: timeoutEngine));
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
        TimeProvider? timeProvider = null) => new(
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
            timeProvider);

    private sealed class SimulatedPatchProcessExitException : Exception;
}
