using System.Collections.Immutable;
using System.Text.Json;
using ContractScribe.Agent.Runtime;
using ContractScribe.Agent.Prompting;
using ContractScribe.Core.Hosting;
using ContractScribe.Roslyn;
using ContractScribe.Cli;
using ContractScribe.Core;
using System.Text;
using System.Text.Json.Nodes;

namespace ContractScribe.Tests;

public sealed partial class DocumentationScribeCompositionTests
{
    [Theory]
    [InlineData("clean")]
    [InlineData("operation90")]
    [InlineData("caller")]
    [InlineData("owner-loss")]
    [InlineData("publication-then-cancel")]
    [InlineData("readback-then-cancel")]
    [InlineData("checkpoint30")]
    [InlineData("checkpoint30-then90")]
    [InlineData("caller-collision")]
    [InlineData("owner-collision")]
    public async Task Four_member_progress_preserves_original_cancellation_source_and_checkpoint_authority(string mode)
    {
        foreach (var outerExecutor in new[] { false, true })
        {
            const string source = "namespace EndToEnd;\n/// <summary>Represents the fixture.</summary>\n"
                + "public class Fixture { public void Run() {} public void M00() {} public void M01() {} public void M02() {} }";
            await using var fixture = await CompositionFixture.CreateBatchGroupingAsync(source);
            var campaign = fixture.CreateCampaign(maximumCampaignElapsedMilliseconds: 7_200_000, includeAllOwners: true);
            var selected = CampaignScribeRequestBuilder.SelectedMembers(campaign.InitialState, campaign.Plan);
            var configuration = JsonNode.Parse(File.ReadAllBytes(Path.Join(FindRepositoryRoot(),
                "tests", "fixtures", "campaign", "cli", "configuration-valid.json")))!;
            var template = JsonNode.Parse(fixture.RequestBytes.Span)!;
            var style = configuration["scribeRequest"]!["styleProfileTemplate"]!;
            foreach (var property in template["manifest"]![0]!["styleProfile"]!.AsObject())
                if (style.AsObject().ContainsKey(property.Key)) style[property.Key] = property.Value!.DeepClone();
            configuration["scribeRequest"]!["toolPolicyId"] = template["toolPolicyId"]!.DeepClone();
            // These original failure-source cases deliberately retain a 90s
            // terminal ceiling independently of the unchanged provider limit.
            configuration["timeouts"]!["terminalSubmissionMilliseconds"] = 90_000;
            var config = CampaignConfiguration.Parse(JsonSerializer.SerializeToUtf8Bytes(configuration));
            var bundle = new ProductionRepositorySessionBundle(new(fixture.Root, fixture.Root,
                fixture.Session.InputIdentity, fixture.Session.InputIdentity, []), fixture.Policy, fixture.Session,
                fixture.Classified, fixture.Classified.Classification.ClassificationSet!, fixture.Observed,
                fixture.Extraction, fixture.AuditInputs, fixture.AuditDocument, [], HostToolchainFact.NotSelected, null);
            var executable = selected.Where(target => target.M3Eligible).Select(target => target.SymbolRef).ToImmutableArray();
            var built = CampaignScribeRequestBuilder.BuildManifest(bundle, selected, executable,
                campaign.PlanningInput.ExecutionPolicy, config, default);
            var node = JsonNode.Parse(built.RequestUtf8Json.Span)!;
            var evidence = new Dictionary<string, JsonNode>();
            // Full startup evidence is generated from real source through the existing builder.
            // This fixture does not replay the live conversation's lazy semantic queries.
            foreach (var symbol in executable)
            {
                var seed = CampaignScribeRequestBuilder.BuildManifest(bundle, selected, executable,
                    campaign.PlanningInput.ExecutionPolicy, config, default, symbol);
                var seeded = JsonNode.Parse(seed.RequestUtf8Json.Span)!;
                var member = seeded["manifest"]!.AsArray().Single(row => row!["evidenceReferenceIds"]!.AsArray().Count > 0)!;
                node["manifest"]!.AsArray().Single(row => row!["targetId"]!.GetValue<string>()
                    == member["targetId"]!.GetValue<string>())!["evidenceReferenceIds"] = member["evidenceReferenceIds"]!.DeepClone();
                foreach (var row in seeded["evidenceReferences"]!.AsArray())
                    evidence[row!["evidenceReferenceId"]!.GetValue<string>()] = row.DeepClone();
            }
            node["evidenceReferences"] = new JsonArray(evidence.OrderBy(row => row.Key, StringComparer.Ordinal).Select(row => row.Value).ToArray());
            ReadOnlyMemory<byte> bytes = JsonSerializer.SerializeToUtf8Bytes(node);
            var request = DocumentationScribeValidation.ParseRequest(bytes).Request!;
            Assert.Equal(4, request.ExecutableTargetIds.Length);
            var clock = new CampaignStateContractTests.SolusDeadlineClock();
            using var caller = new CancellationTokenSource();
            using var owner = new CancellationTokenSource();
            using var execution = CancellationTokenSource.CreateLinkedTokenSource(caller.Token, owner.Token);
            var initial = CampaignStateJson.CreateArtifact(campaign.InitialState);
            var store = new BatchAcknowledgementStore(new MemoryCampaignStore(initial), initial, clock, mode, caller, owner);
            var allowance = new DocumentationScribeInvocationAllowance(DocumentationScribeInvocationLimits.Create(
                maximumTargets: 4, maximumProviderRequests: 8, maximumToolCalls: 16,
                maximumCachedInputTokens: 32768, maximumUncachedInputTokens: 32768, maximumOutputTokens: 8192,
                maximumRequestOutputTokens: 4096, maximumRequestElapsedMilliseconds: 90000, maximumElapsedMilliseconds: 600000), clock);
            var exchange = new FourMemberProposalExchange(request);
            DocumentationCampaignProposalOutcome? outcome = null;
            CampaignScribeExecutionCoordinator? coordinator = null;
            DocumentationScribeRunResult? run = null;
            if (outerExecutor)
            {
                outcome = await DocumentationCampaignProposalExecutor.ExecuteAsync(new(fixture.Classified, fixture.Observed,
                    fixture.Policy, fixture.AuditInputs, fixture.AuditDocument, campaign.PlanningInput, campaign.Plan,
                    campaign.ExecutionCapability, "style.public-api.v1", campaign.StyleProjection, bytes, store,
                    RuntimeOptions(), exchange, null, execution.Token, owner.Token, clock,
                    DispatchGuard: () => !owner.IsCancellationRequested, InvocationAllowance: allowance,
                    OperationalTimeouts: config.Timeouts))
                    .WaitAsync(TimeSpan.FromSeconds(10));
            }
            else
            {
                var work = campaign.Plan.WorkItems.Single(item => item.Targets.Any(target => target.SymbolRef == request.ExecutionTarget.Target.SymbolRef));
                var admission = CampaignStateReducer.AdmitProviderInvocation(initial, campaign.ExecutionCapability,
                    "style.public-api.v1", campaign.StyleProjection, campaign.PlanningInput, campaign.Plan, work.WorkItemKey,
                    request, CampaignStateFactory.CreateInvocationTargetAllowance(initial.State, new(4)), allowance);
                Assert.Equal(CampaignTransitionKind.Applied, admission.Kind);
                var accepted = await CampaignCheckpointAcceptance.AcceptAsync(store, admission);
                var authority = CampaignStateReducer.CreateProviderInvocationAuthority(accepted.AcceptedCheckpoint!,
                    campaign.ExecutionCapability, "style.public-api.v1", campaign.StyleProjection, campaign.PlanningInput, campaign.Plan, request);
                coordinator = new(authority, allowance, store, owner.Token, () => !owner.IsCancellationRequested, clock.GetTimestamp(), config.Timeouts.Host);
                var prepared = await DocumentationScribeComposition.PrepareCampaignAsync(built.Selection, bytes, authority, null,
                    RuntimeOptions(), exchange, clock, execution.Token, () => !owner.IsCancellationRequested, coordinator)
                    .WaitAsync(TimeSpan.FromSeconds(10));
                run = prepared.CompletionAuthority?.Outcome?.RunResult;
                Assert.NotNull(run);
                Assert.Equal(4, run.Submissions.Length);
            }
            // A failure before the fourth combined replacement leaves its
            // member unacknowledged. The first three durable siblings remain;
            // unknown/incorrect ACK after an applied write retains all four.
            var persistedMembers = mode is "clean" or "publication-then-cancel" or "readback-then-cancel" ? 4 : 3;
            Assert.Equal(persistedMembers, store.Current.State.WorkItems.Count(item => item.TrustedProposal is not null));
            Assert.Equal(persistedMembers, store.MemberWrites);
            Assert.Equal(0, store.DuplicateMemberWrites);
            Assert.Equal(mode == "clean" ? 2 : 1, exchange.Calls);
            Assert.Equal(exchange.Calls, allowance.ProviderRequestCount);
            Assert.Equal(exchange.Calls, allowance.ToolCallCount);
            Assert.Equal(DocumentationScribeInvocationStopReason.None, allowance.StopReason);
            var expected = mode switch
            {
                "clean" => (CampaignCheckpointAcceptanceKind?)null,
                "publication-then-cancel" => CampaignCheckpointAcceptanceKind.PublicationFailure,
                "readback-then-cancel" => CampaignCheckpointAcceptanceKind.ReadbackMismatch,
                _ => CampaignCheckpointAcceptanceKind.Cancelled,
            };
            Assert.Equal(expected, outerExecutor ? outcome!.CheckpointFailure : coordinator!.CheckpointFailure);
            if (outerExecutor && mode != "clean")
                Assert.Equal(mode == "operation90" ? "campaign.timeout"
                    : mode is "publication-then-cancel" or "readback-then-cancel" ? "campaign.state-publication-failure"
                    : "campaign.cancelled", CampaignCommandRunner.ProposalResultOutcome(outcome!));
            if (!outerExecutor) Assert.Equal(mode == "operation90", coordinator!.CheckpointCancelledByHostDeadline);
            var actual = store.Current;
            var acknowledged = outerExecutor ? outcome!.Artifact! : coordinator!.LatestAcknowledgedArtifact;
            if (mode is "publication-then-cancel" or "readback-then-cancel")
                Assert.Equal(acknowledged.CheckpointRevision + 1, actual.CheckpointRevision);
            else if (mode != "clean" || !outerExecutor)
                Assert.Equal(acknowledged.Sha256, actual.Sha256);
            if (mode != "clean")
            {
                Assert.NotNull(actual.State.ActiveReservation);
                Assert.Null(actual.State.CandidateObservation);
                Assert.Null(actual.State.TerminalOutcome);
            }
            if (outerExecutor)
            {
                if (mode == "operation90")
                {
                    Assert.Equal(DocumentationCampaignProposalOutcomeKind.TimedOut, outcome!.Kind);
                    Assert.Equal("campaign.timeout", CampaignCommandRunner.ProposalResultOutcome(outcome));
                }
                else if (mode != "clean")
                    Assert.Equal(DocumentationCampaignProposalOutcomeKind.StateConflict, outcome!.Kind);
            }
            else if (mode == "operation90")
                Assert.Equal(DocumentationScribeFailureCode.Timeout, Assert.IsType<DocumentationScribeFailureTerminal>(run!.Stop).Code);
            else if (mode == "clean") Assert.IsType<DocumentationScribeFinishedTerminal>(run!.Stop);
            else if (mode is "checkpoint30" or "checkpoint30-then90") Assert.Equal(mode == "checkpoint30" ? DocumentationScribeFailureCode.Internal : DocumentationScribeFailureCode.Timeout, Assert.IsType<DocumentationScribeFailureTerminal>(run!.Stop).Code);
            else Assert.IsType<DocumentationScribeCancelledTerminal>(run!.Stop);
        }
    }

    private sealed class FourMemberProposalExchange(DocumentationScribeRequest request) : IDocumentationScribeModelExchange
    {
        internal int Calls { get; private set; }
        public ValueTask<DocumentationScribeModelResponse> SendAsync(DocumentationScribeModelRequest model, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls++;
            var payload = Calls == 1 ? JsonSerializer.SerializeToUtf8Bytes(new
            {
                kind = "submit",
                scribeRequestSha256 = request.ArtifactSha256,
                sequence = 1,
                items = request.ExecutableTargetIds.Select(id =>
                {
                    var member = request.Manifest.Single(row => row.TargetId == id);
                    var reference = request.EvidenceReferences.Single(row => row.EvidenceReferenceId == member.EvidenceReferenceIds[0]);
                    return new
                    {
                        targetId = id,
                        result = JsonNode.Parse(CanonicalJson.Serialize(new
                        {
                            kind = "proposal",
                            target = new
                            {
                                request.Context.RepositoryContextRef,
                                member.Target.SymbolRef,
                                sourceCommitment = new { locator = member.Target.SourceLocator, contentSha256 = member.Target.SourceSha256 }
                            },
                            contentUnits = new[] { new { kind = "content.summary", lines = new[] { "Runs the selected operation." },
                            claimCategoryId = reference.ClaimCategoryIds[0], evidenceReferenceIds = member.EvidenceReferenceIds } },
                        }).AsSpan())
                    };
                }),
            }) : ScribeResultFixture.Finish(model);
            return ValueTask.FromResult(new DocumentationScribeModelResponse([], [new(payload, "call.batch." + Calls)], usage: new(1, 1, 0, 1, 0)));
        }
    }

    private sealed class BatchAcknowledgementStore(ICampaignSemanticCheckpointStore inner, CampaignCheckpointArtifact initial,
        CampaignStateContractTests.SolusDeadlineClock clock, string mode, CancellationTokenSource caller, CancellationTokenSource owner)
        : ICampaignSemanticCheckpointStore
    {
        internal CampaignCheckpointArtifact Current { get; private set; } = initial;
        internal int MemberWrites { get; private set; }
        internal int DuplicateMemberWrites { get; private set; }
        private readonly HashSet<string> members = [];
        private bool progressSeen, cancelAfterFailure;
        public object SemanticCheckpointIdentity => inner.SemanticCheckpointIdentity;
        public CampaignSemanticCheckpointStoreKind SemanticCheckpointKind => inner.SemanticCheckpointKind;
        public IDisposable EnterSemanticCheckpointWindow(CampaignSemanticCheckpointWindow window) => inner.EnterSemanticCheckpointWindow(window);
        public ValueTask<CampaignCheckpointReadResult> ReadAsync(CancellationToken token)
        {
            if (cancelAfterFailure) { cancelAfterFailure = false; caller.Cancel(); }
            token.ThrowIfCancellationRequested();
            return owner.IsCancellationRequested ? ValueTask.FromResult(CampaignCheckpointReadResult.LeaseConflict()) : inner.ReadAsync(token);
        }
        public ValueTask<CampaignCheckpointWriteResult> CreateIfAbsentAsync(ReadOnlyMemory<byte> bytes, long revision, string sha, CancellationToken token) =>
            inner.CreateIfAbsentAsync(bytes, revision, sha, token);
        private void Delay(int milliseconds, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            clock.Advance(milliseconds);
            token.ThrowIfCancellationRequested();
        }
        public async ValueTask<CampaignCheckpointWriteResult> ReplaceIfCurrentAsync(long priorRevision, string priorSha,
            ReadOnlyMemory<byte> bytes, long revision, string sha, CancellationToken token, CancellationToken mutationCancellationToken = default)
        {
            mutationCancellationToken.ThrowIfCancellationRequested();
            token.ThrowIfCancellationRequested();
            var next = CampaignStateJson.Parse(bytes).Artifact!;
            var before = Current;
            var added = next.State.WorkItems.Where(item => item.TrustedProposal is not null
                && before.State.WorkItems.All(old => old.WorkItemKey != item.WorkItemKey || old.TrustedProposal is null)).ToArray();
            var progress = next.State.ScribeProgress?.LastProgressSha256 is { } hash
                && hash != before.State.ScribeProgress?.LastProgressSha256;
            if (!mode.StartsWith("checkpoint30", StringComparison.Ordinal))
            {
                if (next.State.ActiveReservation is CampaignProviderReservation { HostPhase: CampaignProviderHostPhase.TerminalSubmission }
                    && before.State.ActiveReservation is CampaignProviderReservation { HostPhase: not CampaignProviderHostPhase.TerminalSubmission }
                    && MemberWrites == 0) Delay(14000, token);
                if (added.Length > 0) Delay(14000, token);
            }
            // Partial member projections now share the member's durable write.
            // Inject the original after-four-members fault at the completed batch,
            // preserving earlier durable siblings and the original parent clocks.
            var failCompletedBatch = progress && !progressSeen && MemberWrites + added.Length == 4;
            if (failCompletedBatch)
            {
                progressSeen = true;
                if (mode is "caller" or "caller-collision") caller.Cancel();
                if (mode is "owner-loss" or "owner-collision") owner.Cancel();
                if (mode is "caller-collision" or "owner-collision")
                {
                    // Both sources are signalled before the failed acknowledgement.
                    clock.Advance(20001);
                    token.ThrowIfCancellationRequested();
                }
                try { Delay(mode switch { "operation90" => 20001, "checkpoint30" or "checkpoint30-then90" => 30001, _ => 1000 }, token); }
                catch (OperationCanceledException) when (mode == "checkpoint30-then90")
                {
                    // No effect follows cancellation. A delayed exception cannot
                    // relabel the earlier30s checkpoint cause when Host90s also fires.
                    clock.Advance(60000);
                    throw;
                }
            }
            var beforeRead = CampaignCheckpointReadResult.Found(before.ExactUtf8Json.AsSpan(), before.CheckpointRevision, before.Sha256);
            var result = await inner.ReplaceIfCurrentAsync(priorRevision, priorSha, bytes, revision, sha, token, mutationCancellationToken);
            if (result.Kind == CampaignCheckpointWriteKind.Written)
            {
                Current = next;
                foreach (var item in added) { if (!members.Add(item.WorkItemKey)) DuplicateMemberWrites++; MemberWrites++; }
            }
            if (failCompletedBatch && mode is "publication-then-cancel" or "readback-then-cancel")
            {
                cancelAfterFailure = true;
                return mode == "publication-then-cancel" ? new(CampaignCheckpointWriteKind.PublicationFailure)
                    : CampaignCheckpointWriteResult.Written(beforeRead);
            }
            return result;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Member_and_progress_acknowledgement_failures_keep_the_first_store_failure(bool progress)
    {
        await using var fixture = await CompositionFixture.CreateProposalStageAsync();
        var campaign = fixture.CreateCampaign();
        using var stop = new CancellationTokenSource();
        var inner = new MemoryCampaignStore(CampaignStateJson.CreateArtifact(campaign.InitialState));
        var store = new CampaignAcknowledgementFaultStore(inner, "publication", stop, (before, after) => progress
            ? after.State.ScribeProgress?.LastProgressSha256 is not null
                && before.State.ScribeProgress?.LastProgressSha256 != after.State.ScribeProgress.LastProgressSha256
            : after.State.WorkItems.Any(work => work.TrustedProposal is not null)
                && before.State.WorkItems.All(work => work.TrustedProposal is null));
        var exchange = new CountingProposalExchange(fixture.Request);

        var outcome = await DocumentationCampaignProposalExecutor.ExecuteAsync(
            campaign.Input(fixture, store, exchange, RuntimeOptions()));

        Assert.Equal(CampaignCheckpointAcceptanceKind.PublicationFailure, outcome.CheckpointFailure);
        Assert.Equal(store.BeforeFault!.Sha256, outcome.Artifact!.Sha256);
        Assert.Equal(store.AfterFault!.Sha256, inner.Current!.Sha256);
        Assert.Equal(1, exchange.RequestCount);
        Assert.Equal(1, inner.Current.State.LineageCharges.ProviderRequests.Observed);
        Assert.NotNull(inner.Current.State.ActiveReservation);
        Assert.Null(inner.Current.State.CandidateObservation);
    }

    [Theory]
    [InlineData("publication", CampaignCheckpointAcceptanceKind.PublicationFailure)]
    [InlineData("readback", CampaignCheckpointAcceptanceKind.ReadbackMismatch)]
    [InlineData("observer", CampaignCheckpointAcceptanceKind.Conflict)]
    [InlineData("cancel", CampaignCheckpointAcceptanceKind.Cancelled)]
    public async Task Terminal_acknowledgement_failure_preserves_its_kind_and_latest_acknowledged_checkpoint(
        string fault, CampaignCheckpointAcceptanceKind expected)
    {
        await using var fixture = await CompositionFixture.CreateProposalStageAsync();
        var campaign = fixture.CreateCampaign();
        using var stop = new CancellationTokenSource();
        var inner = new MemoryCampaignStore(CampaignStateJson.CreateArtifact(campaign.InitialState));
        var store = new CampaignAcknowledgementFaultStore(inner, fault, stop);
        var exchange = new CountingProposalExchange(fixture.Request);
        var input = campaign.Input(fixture, store, exchange, RuntimeOptions(), settlementToken: stop.Token);

        var outcome = await DocumentationCampaignProposalExecutor.ExecuteAsync(input);

        Assert.Equal(DocumentationCampaignProposalOutcomeKind.StateConflict, outcome.Kind);
        Assert.Equal(expected, outcome.CheckpointFailure);
        Assert.Equal(store.BeforeFault!.Sha256, outcome.Artifact!.Sha256);
        Assert.Equal(store.BeforeFault.CheckpointRevision + 1, store.AfterFault!.CheckpointRevision);
        Assert.Equal(store.AfterFault.Sha256, inner.Current!.Sha256);
        Assert.Equal(CampaignProviderHostPhase.TerminalSubmission,
            Assert.IsType<CampaignProviderReservation>(inner.Current.State.ActiveReservation).HostPhase);
        Assert.Equal(1, exchange.RequestCount);
        Assert.Equal(1, input.InvocationAllowance!.ProviderRequestCount);
        Assert.Equal(0, input.InvocationAllowance!.ToolCallCount);
        Assert.All(inner.Current.State.WorkItems, work => Assert.Null(work.TrustedProposal));
    }
}

public sealed partial class CampaignStateContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Host_deadline_classification_requires_the_original_linked_token_and_freezes_the_first_observation(bool original)
    {
        var clock = new SolusDeadlineClock();
        var trial = await SolusTrialAsync(clock: clock, targetMilliseconds: 600000,
            invocationMilliseconds: 600000, hostMilliseconds: 90000);
        trial.Coordinator.Scope.BindRuntimeWindow(trial.Request, clock, clock.GetTimestamp());
        var permit = await trial.Coordinator.Scope.BeginHostAsync(DocumentationScribeHostOperation.TerminalSubmission, default);
        Assert.NotNull(permit);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(90000), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        using var foreign = new CancellationTokenSource();
        permit.ObserveCancellation(deadline.Token, default, default, linked.Token);
        clock.Advance(90001);
        foreign.Cancel();
        var artifact = trial.Coordinator.LatestAcknowledgedArtifact;
        var unchanged = new CampaignTransitionResult(CampaignTransitionKind.Unchanged, artifact, artifact, CampaignTransitionFailure.None);
        Assert.False(await trial.Coordinator.AcceptHostProgressAsync(unchanged, original ? linked.Token : foreign.Token));
        Assert.Equal(CampaignCheckpointAcceptanceKind.Cancelled, trial.Coordinator.CheckpointFailure);
        Assert.Equal(original, trial.Coordinator.CheckpointCancelledByHostDeadline);
        // A different later input cannot rewrite the first classification.
        Assert.False(await trial.Coordinator.AcceptHostProgressAsync(unchanged, original ? foreign.Token : linked.Token));
        Assert.Equal(original, trial.Coordinator.CheckpointCancelledByHostDeadline);
        Assert.Equal(artifact.Sha256, trial.Coordinator.LatestAcknowledgedArtifact.Sha256);
        Assert.True(await trial.Coordinator.Scope.CompleteHostAsync(permit, CancellationToken.None));
        Assert.NotNull(trial.Store.Current.State.ActiveReservation);
    }


    [Theory]
    [InlineData("clean", null)]
    [InlineData("publication", CampaignCheckpointAcceptanceKind.PublicationFailure)]
    [InlineData("readback", CampaignCheckpointAcceptanceKind.ReadbackMismatch)]
    [InlineData("observer", CampaignCheckpointAcceptanceKind.Conflict)]
    [InlineData("cancel", CampaignCheckpointAcceptanceKind.Cancelled)]
    public async Task Terminal_acknowledgement_keeps_three_settled_sends_and_separates_ack13_from_persisted14(
        string fault, CampaignCheckpointAcceptanceKind? expected)
    {
        var templateNode = ReadJsonFixture("documentation-scribe", "v1", "valid", "request.json");
        templateNode["limits"]!["maximumElapsedMilliseconds"] = 600_000;
        var template = DocumentationScribeValidation.ParseRequest(Encoding.UTF8.GetBytes(templateNode.ToJsonString())).Request!;
        var scenario = CreateProposalScenario(workItemCount: 12, targetLimit: 5,
            maximumElapsedMilliseconds: 7_200_000, maximumProviderRequests: 104, scribeRequestTemplate: template);
        var exchange = CreateScribeExchange(scenario.Plan.WorkItems[0], scenario: scenario,
            requestMutation: root => root["limits"]!["maximumElapsedMilliseconds"] = 600_000);
        var request = exchange.Request;
        var initial = CampaignStateJson.CreateArtifact(scenario.InitialState);
        var clock = new C4CampaignClock();
        var allowance = new DocumentationScribeInvocationAllowance(DocumentationScribeInvocationLimits.Create(
            maximumProviderRequests: 8, maximumToolCalls: 16, maximumCachedInputTokens: 32768,
            maximumUncachedInputTokens: 32768, maximumOutputTokens: 8192, maximumRequestOutputTokens: 4096,
            maximumElapsedMilliseconds: 600_000, maximumRequestElapsedMilliseconds: 90_000), clock);
        using var stop = new CancellationTokenSource();
        var inner = new TransitionCheckpointStore(initial);
        var store = new CampaignAcknowledgementFaultStore(inner, fault, stop,
            beforeSemanticWrite: () => clock.Advance(13_164));
        var admitted = CampaignStateReducer.AdmitProviderInvocation(initial, scenario.ExecutionAuthority, "style.synthetic",
            scenario.StyleProjection, scenario.Input, scenario.Plan, scenario.Plan.WorkItems[0].WorkItemKey, request,
            CampaignStateFactory.CreateInvocationTargetAllowance(initial.State, new(5)), allowance);
        var accepted = await CampaignCheckpointAcceptance.AcceptAsync(store, admitted);
        var invocation = CampaignStateReducer.CreateProviderInvocationAuthority(accepted.AcceptedCheckpoint!, scenario.ExecutionAuthority,
            "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan, request);
        var coordinator = new CampaignScribeExecutionCoordinator(invocation, allowance, store, stop.Token, null, clock.GetTimestamp());
        coordinator.Scope.BindRuntimeWindow(request, clock, clock.GetTimestamp());
        Assert.True(invocation.TryBeginDispatch(out _));
        for (var index = 1; index <= 3; index++)
        {
            var provider = await coordinator.Scope.ReserveProviderAsync(new(Sha256("ack-test." + index), 1, index, 4096), default);
            Assert.NotNull(provider);
            Assert.True(coordinator.Scope.BeginPhysicalDispatch(provider));
            clock.Advance(100);
            Assert.True(await coordinator.Scope.SettleProviderAsync(provider,
                new(DocumentationScribeDispatchDisposition.Success, new(100, 50, 50, 10, 5)), default));
            if (index == 3) continue;
            var semantic = await coordinator.Scope.BeginHostAsync(DocumentationScribeHostOperation.SemanticTool, default);
            Assert.NotNull(semantic);
            Assert.True(semantic.TryBeginOperation());
            clock.Advance(1);
            Assert.True(await coordinator.Scope.CompleteHostAsync(semantic, default));
            if (index != 1) continue;
            var registered = await coordinator.Scope.BeginHostAsync(DocumentationScribeHostOperation.RegisteredTool, default);
            Assert.NotNull(registered);
            Assert.True(registered.TryBeginOperation());
            Assert.True(await coordinator.Scope.CompleteHostAsync(registered, default));
        }
        Assert.Equal(13, invocation.AcceptedCheckpoint.Artifact.CheckpointRevision);
        var terminal = await coordinator.Scope.BeginHostAsync(DocumentationScribeHostOperation.TerminalSubmission, default);
        var persisted = CampaignStateJson.Parse((await inner.ReadAsync(default)).ExactUtf8Json.AsMemory()).Artifact!;
        Assert.Equal(14, persisted.CheckpointRevision);
        var claim = Assert.IsType<CampaignProviderReservation>(persisted.State.ActiveReservation);
        Assert.Equal(CampaignProviderHostPhase.TerminalSubmission, claim.HostPhase);
        Assert.Equal(300_000, claim.Exposure.ElapsedMilliseconds);
        Assert.Equal(90_000, allowance.Limits.MaximumRequestElapsedMilliseconds);
        Assert.Equal(3, persisted.State.LineageCharges.ProviderRequests.Observed);
        Assert.Equal(300, persisted.State.LineageCharges.InputTokens.Observed);
        Assert.Equal(30, persisted.State.LineageCharges.OutputTokens.Observed);
        Assert.All(persisted.State.DispatchObservations, observation => Assert.Equal(CampaignDispatchObservationState.Settled, observation.State));
        Assert.Equal(expected, coordinator.CheckpointFailure);
        if (expected is null)
        {
            Assert.NotNull(terminal);
            Assert.True(terminal.TryBeginOperation());
            Assert.Equal(4, allowance.ToolCallCount);
            Assert.Equal(14, coordinator.LatestAcknowledgedArtifact.CheckpointRevision);
            Assert.True(await coordinator.Scope.CompleteHostAsync(terminal, default));
            Assert.False(coordinator.Conflict);
            return;
        }
        Assert.Null(terminal);
        Assert.True(coordinator.Conflict);
        Assert.Equal(3, allowance.ToolCallCount);
        Assert.Equal(13, coordinator.LatestAcknowledgedArtifact.CheckpointRevision);
        using var laterCancellation = new CancellationTokenSource();
        laterCancellation.Cancel();
        Assert.False(await coordinator.AcceptHostProgressAsync(
            CampaignStateReducer.AdvanceHostOperation(invocation, DocumentationScribeHostOperation.TerminalSubmission, 0, 90_000, false),
            laterCancellation.Token));
        Assert.Equal(expected, coordinator.CheckpointFailure);
        Assert.Equal(persisted.Sha256, CampaignStateJson.Parse((await inner.ReadAsync(default)).ExactUtf8Json.AsMemory()).Artifact!.Sha256);
        Assert.Equal(3, allowance.ProviderRequestCount);
        Assert.Equal(3, allowance.ToolCallCount);
        Assert.All(persisted.State.WorkItems, work => Assert.Null(work.TrustedProposal));
    }
}

internal sealed class CampaignAcknowledgementFaultStore(
    ICampaignSemanticCheckpointStore inner, string fault, CancellationTokenSource stop,
    Func<CampaignCheckpointArtifact, CampaignCheckpointArtifact, bool>? select = null,
    Action? beforeSemanticWrite = null) : ICampaignSemanticCheckpointStore
{
    internal CampaignCheckpointArtifact? BeforeFault { get; private set; }
    internal CampaignCheckpointArtifact? AfterFault { get; private set; }
    public object SemanticCheckpointIdentity => inner.SemanticCheckpointIdentity;
    public CampaignSemanticCheckpointStoreKind SemanticCheckpointKind => inner.SemanticCheckpointKind;
    public IDisposable EnterSemanticCheckpointWindow(CampaignSemanticCheckpointWindow window) =>
        inner.EnterSemanticCheckpointWindow(window);
    public ValueTask<CampaignCheckpointReadResult> ReadAsync(CancellationToken token) => inner.ReadAsync(token);
    public ValueTask<CampaignCheckpointWriteResult> CreateIfAbsentAsync(ReadOnlyMemory<byte> bytes,
        long revision, string hash, CancellationToken token) => inner.CreateIfAbsentAsync(bytes, revision, hash, token);
    public async ValueTask<CampaignCheckpointWriteResult> ReplaceIfCurrentAsync(long previousRevision, string previousHash,
        ReadOnlyMemory<byte> bytes, long revision, string hash, CancellationToken token, CancellationToken mutationCancellationToken = default)
    {
        mutationCancellationToken.ThrowIfCancellationRequested();
        var before = await inner.ReadAsync(token);
        var predecessor = CampaignStateJson.Parse(before.ExactUtf8Json.AsMemory()).Artifact!;
        var successor = CampaignStateJson.Parse(bytes).Artifact!;
        if (successor.State.ActiveReservation is CampaignProviderReservation { HostPhase: CampaignProviderHostPhase.SemanticTool })
            beforeSemanticWrite?.Invoke();
        var actual = await inner.ReplaceIfCurrentAsync(previousRevision, previousHash, bytes, revision, hash, token, mutationCancellationToken);
        var selected = select?.Invoke(predecessor, successor) ?? successor.State.ActiveReservation is
            CampaignProviderReservation { HostPhase: CampaignProviderHostPhase.TerminalSubmission };
        if (fault == "clean" || AfterFault is not null || !selected) return actual;
        BeforeFault = predecessor;
        AfterFault = successor;
        switch (fault)
        {
            case "publication": return new(CampaignCheckpointWriteKind.PublicationFailure);
            case "readback": return CampaignCheckpointWriteResult.Written(before);
            case "observer": return new(CampaignCheckpointWriteKind.CurrentMismatch);
            case "cancel": stop.Cancel(); token.ThrowIfCancellationRequested(); break;
        }
        return actual;
    }
}
