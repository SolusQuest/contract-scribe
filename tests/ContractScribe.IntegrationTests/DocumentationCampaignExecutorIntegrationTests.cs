using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContractScribe.Agent.Runtime;
using ContractScribe.Cli;
using ContractScribe.Core;
using ContractScribe.Patching;
using ContractScribe.Roslyn;

namespace ContractScribe.IntegrationTests;

[Collection("Integration process lane 2")]
public sealed class DocumentationCampaignExecutorIntegrationTests
{
    [Fact]
    public Task Real_M2_accepts_and_reconstructs_a_complete_campaign_candidate_on_Linux() =>
        DocumentationScribeEndToEndIntegrationTests.RunCampaignPatchAcceptedIntegrationAsync();

    [Fact]
    public Task Real_M2_closes_stop_causes_and_preserves_ambiguous_reservations_on_Linux() =>
        DocumentationScribeEndToEndIntegrationTests.RunCampaignPatchFailureIntegrationAsync();
}

public sealed partial class DocumentationScribeEndToEndIntegrationTests
{
    internal static async Task RunCampaignPatchAcceptedIntegrationAsync()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        await using var fixture = await EndToEndFixture.CreateAsync(
            additionalSources: AdditionalPatchSources());
        var campaign = CreatePatchCampaign(fixture);
        var store = new PatchMemoryStore(CampaignStateJson.CreateArtifact(campaign.InitialState));
        var sourceBefore = await File.ReadAllBytesAsync(fixture.SourcePath);
        var otherPath = Path.Join(fixture.Root, "Other.cs");
        var otherBefore = await File.ReadAllBytesAsync(otherPath);
        await PopulatePatchProposalsAsync(fixture, campaign, store);

        var input = PatchInput(fixture, campaign, store);
        var accepted = await DocumentationCampaignPatchExecutor.ExecuteAsync(input);

        Assert.True(accepted.Kind == DocumentationCampaignOutcomeKind.Accepted, accepted.Code);
        Assert.NotNull(accepted.AcceptedCandidate);
        Assert.Equal(3, accepted.AcceptedCandidate!.Result.Targets.Length);
        Assert.Equal(2, accepted.AcceptedCandidate.Files.Length);
        Assert.Equal(3, accepted.Artifact!.State.WorkItems.Count(item =>
            item.Status == CampaignWorkStatus.Accepted));
        Assert.Equal(CampaignCumulativeOutcomeKind.Accepted, accepted.Artifact.State.CumulativeOutcome!.Kind);
        Assert.Null(accepted.Artifact.State.ActiveReservation);
        Assert.Equal(sourceBefore, await File.ReadAllBytesAsync(fixture.SourcePath));
        Assert.Equal(otherBefore, await File.ReadAllBytesAsync(otherPath));

        var writesAfterAcceptance = store.SuccessfulReplaceCount;
        var reconstructed = await DocumentationCampaignPatchExecutor.ExecuteAsync(input);

        Assert.Equal(DocumentationCampaignOutcomeKind.Reconstructed, reconstructed.Kind);
        Assert.NotNull(reconstructed.AcceptedCandidate);
        Assert.True(store.SuccessfulReplaceCount >= writesAfterAcceptance + 2);
        Assert.Equal(
            accepted.Artifact.State.CandidateObservation!.AcceptedProjectionCommitmentSha256,
            reconstructed.Artifact!.State.CandidateObservation!.AcceptedProjectionCommitmentSha256);
        Assert.Equal(sourceBefore, await File.ReadAllBytesAsync(fixture.SourcePath));
        Assert.Equal(otherBefore, await File.ReadAllBytesAsync(otherPath));
    }

    internal static async Task RunCampaignPatchFailureIntegrationAsync()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        await using (var staleFixture = await EndToEndFixture.CreateAsync(
                         additionalSources: AdditionalPatchSources()))
        {
            var campaign = CreatePatchCampaign(staleFixture);
            var store = new PatchMemoryStore(CampaignStateJson.CreateArtifact(campaign.InitialState));
            await PopulatePatchProposalsAsync(staleFixture, campaign, store);
            var engine = new DocumentationPatchEngine(
                stagingParentFactory: null,
                applicationObserver: null,
                stage =>
                {
                    if (stage == DocumentationPatchEngineStage.BeforeFinalOriginalRebind)
                    {
                        File.AppendAllText(staleFixture.SourcePath, "// stale\n");
                    }
                });
            var stale = await DocumentationCampaignPatchExecutor.ExecuteAsync(
                PatchInput(staleFixture, campaign, store, patchEngine: engine));
            Assert.Equal(DocumentationCampaignOutcomeKind.Stale, stale.Kind);
            Assert.Null(store.Current.State.ActiveReservation);
            Assert.Null(stale.AcceptedCandidate);
        }

        await using (var cancelledFixture = await EndToEndFixture.CreateAsync(
                         additionalSources: AdditionalPatchSources()))
        {
            var campaign = CreatePatchCampaign(cancelledFixture);
            var store = new PatchMemoryStore(CampaignStateJson.CreateArtifact(campaign.InitialState));
            await PopulatePatchProposalsAsync(cancelledFixture, campaign, store);
            using var caller = new CancellationTokenSource();
            var engine = new DocumentationPatchEngine(
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
                    campaign,
                    store,
                    caller.Token,
                    patchEngine: engine));
            Assert.Equal(DocumentationCampaignOutcomeKind.Cancelled, cancelled.Kind);
            Assert.Null(store.Current.State.ActiveReservation);
            Assert.Null(cancelled.AcceptedCandidate);
        }

        await using (var timeoutFixture = await EndToEndFixture.CreateAsync(
                         additionalSources: AdditionalPatchSources()))
        {
            var campaign = CreatePatchCampaign(timeoutFixture, maximumPatchElapsedMilliseconds: 1);
            var store = new PatchMemoryStore(CampaignStateJson.CreateArtifact(campaign.InitialState));
            await PopulatePatchProposalsAsync(timeoutFixture, campaign, store);
            var timedOut = await DocumentationCampaignPatchExecutor.ExecuteAsync(
                PatchInput(
                    timeoutFixture,
                    campaign,
                    store,
                    timeProvider: new ImmediateDeadlineTimeProvider()));
            Assert.Equal(DocumentationCampaignOutcomeKind.TimedOut, timedOut.Kind);
            Assert.Null(store.Current.State.ActiveReservation);
            Assert.Null(timedOut.AcceptedCandidate);
        }

        await using (var crashFixture = await EndToEndFixture.CreateAsync(
                         additionalSources: AdditionalPatchSources()))
        {
            var campaign = CreatePatchCampaign(crashFixture);
            var store = new PatchMemoryStore(CampaignStateJson.CreateArtifact(campaign.InitialState));
            await PopulatePatchProposalsAsync(crashFixture, campaign, store);
            var engine = new DocumentationPatchEngine(
                stagingParentFactory: null,
                (_, _) => throw new PatchProcessExitException(),
                observer: null);
            var ambiguous = await DocumentationCampaignPatchExecutor.ExecuteAsync(
                PatchInput(crashFixture, campaign, store, patchEngine: engine));
            Assert.Equal(DocumentationCampaignOutcomeKind.AmbiguousDispatch, ambiguous.Kind);
            Assert.IsType<CampaignPatchReservation>(store.Current.State.ActiveReservation);
            Assert.Null(ambiguous.AcceptedCandidate);
        }

        await using (var conflictFixture = await EndToEndFixture.CreateAsync(
                         additionalSources: AdditionalPatchSources()))
        {
            var campaign = CreatePatchCampaign(conflictFixture);
            var store = new PatchMemoryStore(CampaignStateJson.CreateArtifact(campaign.InitialState));
            await PopulatePatchProposalsAsync(conflictFixture, campaign, store);
            store.ReportedReplaceAttempt = store.SuccessfulReplaceCount + 1;
            store.ReportedReplaceKind = CampaignCheckpointWriteKind.CurrentMismatch;
            var dispatches = 0;
            var engine = new DocumentationPatchEngine(
                stagingParentFactory: null,
                (_, _) => dispatches++,
                observer: null);
            var conflict = await DocumentationCampaignPatchExecutor.ExecuteAsync(
                PatchInput(conflictFixture, campaign, store, patchEngine: engine));
            Assert.Equal(DocumentationCampaignOutcomeKind.StateConflict, conflict.Kind);
            Assert.Equal(0, dispatches);
            Assert.Null(conflict.AcceptedCandidate);
        }
    }

    private static async Task PopulatePatchProposalsAsync(
        EndToEndFixture fixture,
        PatchCampaign campaign,
        ICampaignCheckpointStore store)
    {
        foreach (var work in campaign.Plan.WorkItems.Where(item =>
                     item.Disposition.Kind == CampaignPlanningDispositionKind.Executable))
        {
            var target = Assert.Single(work.Targets);
            var request = campaign.Requests[target.SymbolRef];
            var proposal = await DocumentationCampaignProposalExecutor.ExecuteAsync(new(
                fixture.Classified,
                fixture.Observations,
                campaign.Policy,
                campaign.AuditInputs,
                campaign.Audit,
                campaign.PlanningInput,
                campaign.Plan,
                campaign.ExecutionCapability,
                "style.public-api.v1",
                campaign.StyleProjection,
                request.Bytes,
                store,
                new DocumentationScribeRuntimeOptions(
                    "provider.synthetic.v1",
                    "model.synthetic.v1",
                    "scribe-protocol.v1"),
                new ProposalExchange(request.Request),
                ConfiguredAgentEntrypoint: null,
                CancellationToken.None,
                CancellationToken.None));
            Assert.Equal(DocumentationCampaignProposalOutcomeKind.ProposalReady, proposal.Kind);
        }
    }

    private static Dictionary<string, string> AdditionalPatchSources() =>
        new(StringComparer.Ordinal)
        {
            ["Fixture.cs"] = """
                namespace EndToEnd;

                /// <summary>Provides the base fixture used by the patch-stage campaign.</summary>
                public class BaseFixture
                {
                    /// <summary>Runs the documented base operation.</summary>
                    public virtual void Run()
                    {
                    }
                }

                /// <summary>Provides the derived fixture used by the patch-stage campaign.</summary>
                public sealed class Fixture : BaseFixture
                {
                    public override void Run()
                    {
                    }
                }
                """,
            ["Other.cs"] = """
                namespace EndToEnd;

                /// <summary>Provides the second source file used by the patch-stage campaign.</summary>
                public sealed class OtherFixture
                {
                    public void Run()
                    {
                    }

                    public void Stop()
                    {
                    }
                }
                """,
        };

    private static PatchCampaign CreatePatchCampaign(
        EndToEndFixture fixture,
        long maximumPatchElapsedMilliseconds = 120_000)
    {
        var classifications = fixture.Classified.Classification.ClassificationSet!;
        var observations = fixture.Observations.ObservationSet!;
        var policy = PolicyConfigurationEvaluator.Parse(Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":1,\"targetProfile\":\"profile.external-api\",\"defaultDecision\":\"required\"}"))
            .Document ?? throw new InvalidOperationException("policy");
        var extraction = new PolicyEvidenceExtractor().Extract(
            fixture.Classified,
            fixture.Observations,
            policy);
        Assert.Equal(PolicyEvidenceExtractionStatus.Success, extraction.Status);
        var auditInputs = AuditInputAssembler.Assemble(classifications, policy, extraction).ToImmutableArray();
        var audit = AuditAggregator.Aggregate(
            TargetProfile.ExternalApi,
            classifications,
            policy,
            auditInputs);
        var evidenceAuthority = extraction.Bindings.Select(binding =>
            new CampaignPlanningEvidenceAuthority(
                observations.Observations.Single(observation => observation.Subject == binding.Subject),
                binding.Evidence)).ToImmutableArray();
        using var auditJson = JsonDocument.Parse(AuditJson.Write(audit));
        var selectedTargets = classifications.Targets.Where(target =>
                target.SupportStatus == SupportStatus.Supported
                && target.SymbolRef.DocumentationCommentId is
                    "M:EndToEnd.Fixture.Run"
                    or "M:EndToEnd.OtherFixture.Run"
                    or "M:EndToEnd.OtherFixture.Stop")
            .OrderBy(target => target.SymbolRef.DocumentationCommentId, StringComparer.Ordinal)
            .ToImmutableArray();
        Assert.Equal(3, selectedTargets.Length);
        var requests = ImmutableDictionary.CreateBuilder<SymbolRef, PatchCampaignRequest>();
        var targetAuthorities = ImmutableArray.CreateBuilder<CampaignPlanningTargetAuthority>();
        foreach (var target in selectedTargets)
        {
            var request = target.SymbolRef == fixture.Target.SymbolRef
                ? new PatchCampaignRequest(fixture.RequestBytes, fixture.Request)
                : CreatePatchCampaignRequest(fixture, classifications, target, AuditOutcome(target, auditJson));
            requests.Add(target.SymbolRef, request);
            var targetObservation = observations.Observations.Single(item =>
                item.Subject.ParentSymbolRef == target.SymbolRef
                && item.Subject.ComponentKind is null);
            var declaration = Assert.Single(targetObservation.Declarations);
            var repository = Assert.IsType<RepositoryDocumentationSourceIdentity>(declaration.Source);
            var requestLocator = Assert.IsType<RepositoryEvidenceLocator>(request.Request.Target.SourceLocator);
            var sourcePath = Path.Join(fixture.Root, repository.Path);
            targetAuthorities.Add(new CampaignPlanningTargetAuthority(
                target,
                new CampaignPlanningRepositorySourceAuthority(
                    repository.Path,
                    CampaignSha(Encoding.UTF8.GetBytes(
                        new RepositoryPathResolver().PhysicalIdentity(fixture.Root, sourcePath))),
                    repository.SourceSha256,
                    declaration.DeclarationId,
                    repository.SourceSha256,
                    DocumentationPatchRepositoryEncoding.Utf8,
                    declaration.DeclarationSpan,
                    Assert.IsType<Utf16Span>(requestLocator.Span),
                    Assert.IsType<Utf16Span>(requestLocator.Span),
                    declaration.DeclarationSpan,
                    declaration.DocumentationSpan,
                    declaration.BlockState),
                request.Request.Target.ApplicableComponents.Select(component =>
                    new CampaignPlanningApplicableComponent(
                        PatchComponentKind(component.Kind),
                        component.Identity,
                        component.Name)).ToImmutableArray(),
                [target.SymbolRef],
                multiDeclarator: false,
                primaryConstructor: false,
                primaryConstructorAlias: false,
                request.Request.StyleProfile));
        }
        var agentProjection = JsonSerializer.SerializeToElement(new
        {
            scribeProtocolId = "scribe-protocol.v1",
        });
        var toolProjection = JsonSerializer.SerializeToElement(new
        {
            toolPolicyId = fixture.Request.ToolPolicyId,
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
            fixture.Request.Limits,
            new CampaignPlanningBudgetPolicy(
                32, 8, 1_000_000, 64, 3, 1_000_000, 500_000, 100_000,
                5_000_000, 300_000, 8, false, null, null),
            PatchContent(CampaignPlanningContentFamily.ProposalContract, "proposal", new { value = "proposal-v1" }),
            PatchContent(CampaignPlanningContentFamily.AgentProtocol, "agent", agentProjection),
            PatchContent(CampaignPlanningContentFamily.ContextSelectionPolicy, "context", new { value = "context-v1" }),
            PatchContent(CampaignPlanningContentFamily.ToolPolicyAndRegistry, "tools", toolProjection),
            PatchContent(CampaignPlanningContentFamily.ProviderModelRequestProfile, "provider", providerProjection),
            PatchContent(CampaignPlanningContentFamily.RetryPolicy, "retry", new { value = "retry-v1" }),
            PatchContent(CampaignPlanningContentFamily.M2ProjectionPolicy, "m2", m2Projection),
            PatchContent(CampaignPlanningContentFamily.ProductContractRevision, "product", new { value = "product-v1" }));
        var planning = new CampaignPlanningInput(
            new CampaignPlanningSnapshot(
                "campaign.patch-stage.fixture",
                "snapshot.patch-stage.fixture",
                CampaignSha("repository"u8.ToArray()),
                CampaignSha("input"u8.ToArray()),
                CampaignSha("policy"u8.ToArray()),
                TargetProfile.ExternalApi),
            executionPolicy,
            classifications,
            observations,
            evidenceAuthority,
            audit,
            new CampaignPlanningOwnerAuthoritySet(targetAuthorities.Select(target =>
                new CampaignPlanningOwnerAuthority([target])).ToImmutableArray()));
        var plan = CampaignPlanner.Plan(planning);
        var executable = plan.WorkItems.Where(item =>
            item.Disposition.Kind == CampaignPlanningDispositionKind.Executable).ToArray();
        Assert.Equal(3, executable.Length);
        Assert.All(executable, work => Assert.True(Assert.Single(work.Targets).M3Eligible));
        var styleProjection = JsonSerializer.SerializeToElement(new { style = "public-api-v1" });
        var capability = CampaignStateFactory.CreateScribeExecutionCapability(
            executionPolicy,
            agentProjection,
            toolProjection,
            providerProjection);
        var initial = CampaignStateFactory.CreateInitial(
            "style.public-api.v1",
            styleProjection,
            capability,
            fixture.Session.InputIdentity,
            planning,
            plan);
        return new PatchCampaign(
            policy,
            auditInputs,
            audit,
            planning,
            plan,
            capability,
            styleProjection,
            m2Projection,
            initial,
            requests.ToImmutable());
    }

    private static DocumentationCampaignPatchInput PatchInput(
        EndToEndFixture fixture,
        PatchCampaign campaign,
        ICampaignCheckpointStore store,
        CancellationToken executionToken = default,
        CancellationToken settlementToken = default,
        DocumentationPatchEngine? patchEngine = null,
        TimeProvider? timeProvider = null) => new(
            fixture.Classified,
            fixture.Observations,
            campaign.Policy,
            campaign.AuditInputs,
            campaign.Audit,
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

    private static PatchCampaignRequest CreatePatchCampaignRequest(
        EndToEndFixture fixture,
        ClassificationSet classifications,
        TargetClassification target,
        string auditOutcome)
    {
        var project = Assert.Single(fixture.Session.Projects, item =>
            item.CompilationContextRef == target.SymbolRef.CompilationContextRef);
        var symbol = Assert.Single(Microsoft.CodeAnalysis.DocumentationCommentId
            .GetSymbolsForDeclarationId(target.SymbolRef.DocumentationCommentId, project.Compilation));
        var syntaxReference = Assert.Single(symbol.DeclaringSyntaxReferences);
        var loadedSource = project.SourceTrees[syntaxReference.SyntaxTree];
        var repositoryPath = Assert.IsType<string>(loadedSource.RepositoryPath);
        var targetSpan = new Utf16Span(syntaxReference.Span.Start, syntaxReference.Span.End);
        var sourceSha256 = CampaignSha(File.ReadAllBytes(Path.Join(fixture.Root, repositoryPath)));
        var selection = DocumentationScribeContextValidation.CreateBootstrapSelection(
            fixture.Session.RepositoryContextRef,
            fixture.Session.InputIdentity,
            TargetProfile.ExternalApi,
            target.SymbolRef,
            repositoryPath,
            targetSpan.Start,
            targetSpan.End,
            sourceSha256);
        var bootstrap = new DocumentationScribeContextBootstrapper().Bootstrap(
            fixture.Classified,
            selection);
        Assert.True(bootstrap.Status is DocumentationScribeContextBootstrapStatus.Succeeded
            or DocumentationScribeContextBootstrapStatus.Incomplete);
        var context = Assert.IsType<DocumentationScribeLoadedContext>(bootstrap.Context);
        var bytes = EndToEndFixture.CreateRequest(
            fixture.Session,
            classifications,
            target,
            symbol as Microsoft.CodeAnalysis.IMethodSymbol,
            repositoryPath,
            targetSpan,
            sourceSha256,
            context,
            auditOutcome);
        var parsed = DocumentationScribeValidation.ParseRequest(bytes);
        Assert.True(parsed.IsValid, parsed.Failure?.Code + ":" + parsed.Failure?.Pointer);
        return new PatchCampaignRequest(
            bytes,
            Assert.IsType<DocumentationScribeRequest>(parsed.Request));
    }

    private static string AuditOutcome(TargetClassification target, JsonDocument audit) =>
        Assert.Single(
                audit.RootElement.GetProperty("results").EnumerateArray(),
                row => row.GetProperty("classification") is { } classification
                    && classification.TryGetProperty("symbolRef", out var symbolRef)
                    && symbolRef.GetProperty("documentationCommentId").GetString()
                        == target.SymbolRef.DocumentationCommentId)
            .GetProperty("auditOutcome")
            .GetString()!;

    private static CampaignPlanningContentAuthority PatchContent(
        CampaignPlanningContentFamily family,
        string id,
        object projection) => CampaignPlanningContentAuthority.CreateValidatedJsonProjection(
            family,
            id,
            projection is JsonElement json ? json : JsonSerializer.SerializeToElement(projection));

    private static ComponentKind PatchComponentKind(DocumentationPatchComponentKind kind) => kind switch
    {
        DocumentationPatchComponentKind.Parameter => ComponentKind.Parameter,
        DocumentationPatchComponentKind.TypeParameter => ComponentKind.TypeParameter,
        DocumentationPatchComponentKind.Return => ComponentKind.Return,
        DocumentationPatchComponentKind.Value => ComponentKind.Value,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string CampaignSha(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record PatchCampaign(
        PolicyDocumentV1 Policy,
        ImmutableArray<AuditRecordInput> AuditInputs,
        AuditDocument Audit,
        CampaignPlanningInput PlanningInput,
        CampaignWorkPlan Plan,
        CampaignScribeExecutionCapability ExecutionCapability,
        JsonElement StyleProjection,
        JsonElement M2Projection,
        CampaignCheckpointState InitialState,
        ImmutableDictionary<SymbolRef, PatchCampaignRequest> Requests);

    private sealed record PatchCampaignRequest(
        ReadOnlyMemory<byte> Bytes,
        DocumentationScribeRequest Request);

    private sealed class PatchProcessExitException : Exception;

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

    private sealed class NoopTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PatchMemoryStore(CampaignCheckpointArtifact initial) : ICampaignCheckpointStore
    {
        private readonly object gate = new();
        private CampaignCheckpointArtifact current = initial;
        private int replaceAttemptCount;

        internal int SuccessfulReplaceCount { get; private set; }

        internal CampaignCheckpointArtifact Current => current;

        internal int? ReportedReplaceAttempt { get; set; }

        internal CampaignCheckpointWriteKind? ReportedReplaceKind { get; set; }

        internal bool ApplyReportedReplaceBeforeReturning { get; init; }

        public ValueTask<CampaignCheckpointReadResult> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                return ValueTask.FromResult(CampaignCheckpointReadResult.Found(
                    current.ExactUtf8Json.AsSpan(),
                    current.CheckpointRevision,
                    current.Sha256));
            }
        }

        public ValueTask<CampaignCheckpointWriteResult> CreateIfAbsentAsync(
            ReadOnlyMemory<byte> exactUtf8Json,
            long checkpointRevision,
            string sha256,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new CampaignCheckpointWriteResult(
                CampaignCheckpointWriteKind.AlreadyPresent));

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
                if (current.CheckpointRevision != expectedCheckpointRevision
                    || !string.Equals(current.Sha256, expectedSha256, StringComparison.Ordinal))
                {
                    return ValueTask.FromResult(new CampaignCheckpointWriteResult(
                        CampaignCheckpointWriteKind.CurrentMismatch));
                }

                var parsed = CampaignStateJson.Parse(exactUtf8Json);
                var successor = Assert.IsType<CampaignCheckpointArtifact>(parsed.Artifact);
                if (ReportedReplaceAttempt == replaceAttempt
                    && ReportedReplaceKind is { } reported)
                {
                    if (ApplyReportedReplaceBeforeReturning)
                    {
                        current = successor;
                        SuccessfulReplaceCount++;
                    }

                    return ValueTask.FromResult(new CampaignCheckpointWriteResult(reported));
                }

                current = successor;
                SuccessfulReplaceCount++;
                return ValueTask.FromResult(new CampaignCheckpointWriteResult(
                    CampaignCheckpointWriteKind.Written));
            }
        }
    }
}
