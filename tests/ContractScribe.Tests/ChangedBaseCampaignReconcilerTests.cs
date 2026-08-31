using System.Text;
using ContractScribe.Cli;
using ContractScribe.Core;

namespace ContractScribe.Tests;

public sealed class ChangedBaseCampaignReconcilerTests
{
    [Fact]
    public async Task Valid_successor_is_reduced_replaced_and_read_back_once()
    {
        var scenario = CreateScenario("snapshot.first", '1', '2');
        var successor = CreateScenario("snapshot.second", '3', '4');
        var store = new MemoryStore(scenario.Artifact);

        var result = await ReconcileAsync(scenario, successor, store);

        Assert.Equal(ChangedBaseCampaignReconciliationKind.Accepted, result.Kind);
        var accepted = Assert.IsType<CampaignAcceptedCheckpoint>(result.AcceptedCheckpoint);
        Assert.Equal(1, accepted.Artifact.CheckpointRevision);
        Assert.Equal("snapshot.second", accepted.Artifact.State.Snapshot.OpaqueSnapshotBinding);
        Assert.Equal(scenario.Artifact.Sha256, accepted.Artifact.State.Predecessor!.FinalCheckpointSha256);
        Assert.Equal(1, store.ReplaceCalls);
        Assert.Equal(2, store.ReadCalls);
        AssertArtifact(accepted.Artifact, store.Artifact!);
    }

    [Fact]
    public async Task Configuration_revalidation_and_same_snapshot_rejection_never_touch_the_store()
    {
        var scenario = CreateScenario("snapshot.first", '1', '2');
        var successor = CreateScenario("snapshot.second", '3', '4');
        var guarded = new MemoryStore(scenario.Artifact);

        var invalidConfiguration = await ReconcileAsync(
            scenario,
            successor,
            guarded,
            configurationRevalidator: () => false);
        var sameSnapshot = await ReconcileAsync(scenario, scenario, guarded);

        Assert.Equal(ChangedBaseCampaignReconciliationKind.InvalidConfiguration, invalidConfiguration.Kind);
        Assert.Equal(ChangedBaseCampaignReconciliationKind.Incompatible, sameSnapshot.Kind);
        Assert.Equal(0, guarded.ReadCalls);
        Assert.Equal(0, guarded.ReplaceCalls);
        AssertArtifact(scenario.Artifact, guarded.Artifact!);
    }

    [Fact]
    public async Task Cancellation_before_publication_reports_the_exact_predecessor()
    {
        var scenario = CreateScenario("snapshot.first", '1', '2');
        var successor = CreateScenario("snapshot.second", '3', '4');
        using var cancellation = new CancellationTokenSource();
        var store = new MemoryStore(scenario.Artifact)
        {
            Cancellation = cancellation,
            CancellationPoint = StoreCancellationPoint.BeforeReplacement,
        };

        var result = await ReconcileAsync(scenario, successor, store, cancellationToken: cancellation.Token);

        Assert.Equal(ChangedBaseCampaignReconciliationKind.Cancelled, result.Kind);
        AssertArtifact(scenario.Artifact, result.AcceptedCheckpoint!.Artifact);
        AssertArtifact(scenario.Artifact, store.Artifact!);
        Assert.Equal(1, store.ReplaceCalls);
        Assert.Equal(2, store.ReadCalls);
    }

    [Fact]
    public async Task Cancellation_after_publication_reports_the_exact_successor()
    {
        var scenario = CreateScenario("snapshot.first", '1', '2');
        var successor = CreateScenario("snapshot.second", '3', '4');
        using var cancellation = new CancellationTokenSource();
        var store = new MemoryStore(scenario.Artifact)
        {
            Cancellation = cancellation,
            CancellationPoint = StoreCancellationPoint.AfterReplacement,
        };

        var result = await ReconcileAsync(scenario, successor, store, cancellationToken: cancellation.Token);

        Assert.Equal(ChangedBaseCampaignReconciliationKind.Cancelled, result.Kind);
        Assert.Equal(1, result.AcceptedCheckpoint!.Artifact.CheckpointRevision);
        Assert.Equal("snapshot.second", result.AcceptedCheckpoint.Artifact.State.Snapshot.OpaqueSnapshotBinding);
        AssertArtifact(store.Artifact!, result.AcceptedCheckpoint.Artifact);
        Assert.Equal(1, store.ReplaceCalls);
        Assert.Equal(2, store.ReadCalls);
    }

    [Fact]
    public async Task Conditional_conflict_accepts_only_the_exact_successor()
    {
        var scenario = CreateScenario("snapshot.first", '1', '2');
        var successor = CreateScenario("snapshot.second", '3', '4');
        var exact = new MemoryStore(scenario.Artifact)
        {
            ReplacementResult = CampaignCheckpointWriteKind.CurrentMismatch,
            AdoptIntendedOnRejectedReplacement = true,
        };
        var competing = new MemoryStore(scenario.Artifact)
        {
            ReplacementResult = CampaignCheckpointWriteKind.CurrentMismatch,
        };

        var accepted = await ReconcileAsync(scenario, successor, exact);
        var rejected = await ReconcileAsync(scenario, successor, competing);

        Assert.Equal(ChangedBaseCampaignReconciliationKind.Accepted, accepted.Kind);
        Assert.Equal(ChangedBaseCampaignReconciliationKind.CheckpointFailure, rejected.Kind);
        Assert.Equal(CampaignCheckpointAcceptanceKind.Conflict, rejected.CheckpointFailure);
    }

    [Fact]
    public async Task Active_provider_reservation_is_summarized_charged_and_cleared()
    {
        var predecessor = WithProviderReservation(CreateScenario("snapshot.first", '1', '2'));
        var successor = CreateScenario("snapshot.second", '3', '4');
        var store = new MemoryStore(predecessor.Artifact);
        var reservation = Assert.IsType<CampaignProviderReservation>(
            predecessor.Artifact.State.ActiveReservation);

        var result = await ReconcileAsync(predecessor, successor, store);
        var accepted = Assert.IsType<CampaignAcceptedCheckpoint>(result.AcceptedCheckpoint);
        var state = accepted.Artifact.State;

        Assert.Equal(ChangedBaseCampaignReconciliationKind.Accepted, result.Kind);
        Assert.Null(state.ActiveReservation);
        Assert.Empty(state.WorkItems);
        Assert.Equal("provider", state.Predecessor!.Reservation!.Kind);
        Assert.Equal(reservation.ScribeRequestSha256, state.Predecessor.Reservation.CorrelationSha256);
        Assert.Equal(16, state.Predecessor.Reservation.ConservativeCharge);
        Assert.Equal(1, state.LineageCharges.ProviderRequests.ConservativeUnobserved);
        Assert.Equal(3, state.LineageCharges.InputTokens.ConservativeUnobserved);
        Assert.Equal(3, state.LineageCharges.CachedInputTokens.ConservativeUnobserved);
        Assert.Equal(2, state.LineageCharges.UncachedInputTokens.ConservativeUnobserved);
        Assert.Equal(4, state.LineageCharges.OutputTokens.ConservativeUnobserved);
        Assert.Equal(0, state.LineageCharges.CostMicrounits.ConservativeUnobserved);
        Assert.Equal(6, state.LineageCharges.ActiveElapsedMilliseconds.ConservativeUnobserved);

    }

    [Fact]
    public async Task Cancelled_acceptance_rejects_missing_authority()
    {
        var scenario = CreateScenario("snapshot.first", '1', '2');
        var successor = CreateScenario("snapshot.second", '3', '4');
        using var cancellation = new CancellationTokenSource();
        var missing = new MemoryStore(scenario.Artifact)
        {
            Cancellation = cancellation,
            CancellationPoint = StoreCancellationPoint.AfterRemoval,
        };

        var result = await ReconcileAsync(scenario, successor, missing, cancellationToken: cancellation.Token);

        Assert.Equal(ChangedBaseCampaignReconciliationKind.CheckpointFailure, result.Kind);
        Assert.Equal(CampaignCheckpointAcceptanceKind.Conflict, result.CheckpointFailure);
    }

    private static Task<ChangedBaseCampaignReconciliation> ReconcileAsync(
        Scenario predecessor,
        Scenario successor,
        ICampaignCheckpointStore store,
        Func<bool>? configurationRevalidator = null,
        CancellationToken cancellationToken = default) =>
        ChangedBaseCampaignReconciler.ReconcileAsync(
            predecessor.Accepted,
            store,
            successor.Execution,
            successor.Configuration.ScribeRequest.StyleProfileTemplate.StyleProfileId,
            successor.Configuration.ScribeRequest.StyleProfileTemplate.ExactProjection,
            InputIdentity,
            successor.Input,
            successor.Plan,
            configurationRevalidator ?? (() => true),
            cancellationToken);

    private static Scenario CreateScenario(string snapshotBinding, char repositoryHash, char inputHash)
    {
        var configuration = CampaignConfiguration.Parse(File.ReadAllBytes(Fixture("configuration-valid.json")));
        var executionPolicy = configuration.CreateExecutionPolicy();
        var execution = configuration.CreateExecutionCapability(executionPolicy);
        var classifications = Assert.IsType<ClassificationSet>(
            new ClassificationCandidateBuffer().Normalize(TargetProfile.ExternalApi).ClassificationSet);
        var observations = Assert.IsType<DocumentationObservationSet>(
            new DocumentationObservationCandidateBuffer(classifications).Normalize().ObservationSet);
        const string policyJson =
            "{\"schemaVersion\":1,\"targetProfile\":\"profile.external-api\",\"defaultDecision\":\"required\"}";
        var policy = Assert.IsType<PolicyDocumentV1>(
            PolicyConfigurationEvaluator.Parse(Encoding.UTF8.GetBytes(policyJson)).Document);
        var audit = AuditAggregator.Aggregate(
            TargetProfile.ExternalApi,
            classifications,
            policy,
            []);
        var input = new CampaignPlanningInput(
            new CampaignPlanningSnapshot(
                configuration.Planning.CampaignLineage,
                snapshotBinding,
                Hash(repositoryHash),
                Hash(inputHash),
                Hash('9'),
                TargetProfile.ExternalApi),
            executionPolicy,
            classifications,
            observations,
            [],
            audit,
            new CampaignPlanningOwnerAuthoritySet([]));
        var plan = CampaignPlanner.Plan(input);
        Assert.Empty(plan.WorkItems);
        var artifact = CampaignStateJson.CreateArtifact(CampaignStateFactory.CreateInitial(
            configuration.ScribeRequest.StyleProfileTemplate.StyleProfileId,
            configuration.ScribeRequest.StyleProfileTemplate.ExactProjection,
            execution,
            InputIdentity,
            input,
            plan));
        var accepted = CampaignCheckpointAcceptance.AcceptCurrent(CampaignCheckpointReadResult.Found(
            artifact.ExactUtf8Json.AsSpan(),
            artifact.CheckpointRevision,
            artifact.Sha256));
        return new(configuration, execution, input, plan, artifact, accepted.AcceptedCheckpoint!);
    }

    private static Scenario WithProviderReservation(Scenario scenario)
    {
        const string WorkItemKey =
            "campaign-work.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        Assert.True(DocumentationScribeAttemptId.TryParse(
            "scribe-attempt.11111111111111111111111111111111",
            out var attemptId));
        var state = scenario.Artifact.State;
        var reserved = CampaignStateFactory.CreateValidated(
            state.ProductRevision,
            state.CampaignLineage,
            state.Snapshot,
            state.CheckpointRevision,
            state.ConfiguredCeilings,
            state.LineageCharges,
            [new CampaignWorkItemState(
                WorkItemKey,
                OuterAttemptCount: 0,
                CandidateAttemptCount: 0,
                CampaignWorkStatus.Planned,
                TrustedProposal: null,
                ClosedOutcome: null)],
            new CampaignProviderReservation(
                WorkItemKey,
                Hash('8'),
                attemptId,
                new CampaignProviderReservationExposure(
                    ProviderRequests: 1,
                    InputTokens: 3,
                    UncachedInputTokens: 2,
                    OutputTokens: 4,
                    CostMicrounits: 0,
                    ElapsedMilliseconds: 6)),
            candidateObservation: null,
            cumulativeOutcome: null,
            state.KnownCompletedOperations,
            terminalOutcome: null,
            state.Predecessor);
        var artifact = CampaignStateJson.CreateArtifact(reserved);
        var accepted = CampaignCheckpointAcceptance.AcceptCurrent(CampaignCheckpointReadResult.Found(
            artifact.ExactUtf8Json.AsSpan(),
            artifact.CheckpointRevision,
            artifact.Sha256));
        return scenario with
        {
            Artifact = artifact,
            Accepted = accepted.AcceptedCheckpoint!,
        };
    }

    private static string Fixture(string name) => Path.GetFullPath(Path.Join(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "fixtures", "campaign", "cli", name));

    private static string Hash(char value) => new(value, 64);

    private static void AssertArtifact(CampaignCheckpointArtifact expected, CampaignCheckpointArtifact actual)
    {
        Assert.Equal(expected.CheckpointRevision, actual.CheckpointRevision);
        Assert.Equal(expected.Sha256, actual.Sha256);
        Assert.True(expected.ExactUtf8Json.AsSpan().SequenceEqual(actual.ExactUtf8Json.AsSpan()));
    }

    private const string InputIdentity = "App/App.csproj";

    private sealed record Scenario(
        CampaignConfigurationDocument Configuration,
        CampaignScribeExecutionCapability Execution,
        CampaignPlanningInput Input,
        CampaignWorkPlan Plan,
        CampaignCheckpointArtifact Artifact,
        CampaignAcceptedCheckpoint Accepted);

    private enum StoreCancellationPoint
    {
        None,
        BeforeReplacement,
        AfterReplacement,
        AfterRemoval,
    }

    private sealed class MemoryStore(CampaignCheckpointArtifact artifact) : ICampaignCheckpointStore
    {
        internal CampaignCheckpointArtifact? Artifact { get; private set; } = artifact;
        internal CampaignCheckpointWriteKind ReplacementResult { get; init; } =
            CampaignCheckpointWriteKind.Written;
        internal bool AdoptIntendedOnRejectedReplacement { get; init; }
        internal CancellationTokenSource? Cancellation { get; init; }
        internal StoreCancellationPoint CancellationPoint { get; init; }
        internal int ReadCalls { get; private set; }
        internal int ReplaceCalls { get; private set; }

        public ValueTask<CampaignCheckpointReadResult> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCalls++;
            return ValueTask.FromResult(Artifact is null
                ? CampaignCheckpointReadResult.NotFound()
                : CampaignCheckpointReadResult.Found(
                    Artifact.ExactUtf8Json.AsSpan(),
                    Artifact.CheckpointRevision,
                    Artifact.Sha256));
        }

        public ValueTask<CampaignCheckpointWriteResult> CreateIfAbsentAsync(
            ReadOnlyMemory<byte> exactUtf8Json,
            long checkpointRevision,
            string sha256,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Changed-base reconciliation cannot create state.");

        public ValueTask<CampaignCheckpointWriteResult> ReplaceIfCurrentAsync(
            long expectedCheckpointRevision,
            string expectedSha256,
            ReadOnlyMemory<byte> exactUtf8Json,
            long checkpointRevision,
            string sha256,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReplaceCalls++;
            if (CancellationPoint == StoreCancellationPoint.BeforeReplacement)
            {
                Cancellation!.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (Artifact is null
                || Artifact.CheckpointRevision != expectedCheckpointRevision
                || !string.Equals(Artifact.Sha256, expectedSha256, StringComparison.Ordinal))
            {
                return ValueTask.FromResult(new CampaignCheckpointWriteResult(
                    CampaignCheckpointWriteKind.CurrentMismatch));
            }

            var intended = Assert.IsType<CampaignCheckpointArtifact>(CampaignStateJson.Parse(exactUtf8Json).Artifact);
            if (ReplacementResult != CampaignCheckpointWriteKind.Written)
            {
                if (AdoptIntendedOnRejectedReplacement)
                {
                    Artifact = intended;
                }
                return ValueTask.FromResult(new CampaignCheckpointWriteResult(ReplacementResult));
            }

            Artifact = intended;
            if (CancellationPoint is StoreCancellationPoint.AfterReplacement or StoreCancellationPoint.AfterRemoval)
            {
                if (CancellationPoint == StoreCancellationPoint.AfterRemoval)
                {
                    Artifact = null;
                }
                Cancellation!.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return ValueTask.FromResult(new CampaignCheckpointWriteResult(CampaignCheckpointWriteKind.Written));
        }
    }
}
