using System.Text.Json.Nodes;
using ContractScribe.Agent.Runtime;
using ContractScribe.Core;
using ContractScribe.Cli;

namespace ContractScribe.Tests;

public sealed partial class CampaignStateContractTests
{
    [Theory]
    [InlineData(GitHubCoordinationRefTests.LostReadback.Exact)]
    [InlineData(GitHubCoordinationRefTests.LostReadback.Missing)]
    [InlineData(GitHubCoordinationRefTests.LostReadback.Mismatch)]
    public async Task Remote_checkpoint_cancellation_keeps_unknown_or_mismatched_write_effects_fail_closed(
        GitHubCoordinationRefTests.LostReadback readback)
    {
        var scenario = CreateProposalScenario();
        var artifact = CampaignStateJson.CreateArtifact(scenario.InitialState);
        var remote = new GitHubCoordinationRefTests.CoordinationRemote();
        using var handler = new GitHubCoordinationRefTests.CheckpointInterceptHandler(remote);
        using var client = GitHubCoordinationRefTests.CheckpointClient(remote, artifact.State.CampaignLineage, handler);
        await using var store = ContractScribe.GitHub.Coordination.GitHubCoordinationStore.Create(client);
        Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
        Assert.True(await store.BindPlanAsync(CampaignPlanner.CreateRecoveryProjection(scenario.Plan), default));
        var writes = remote.RefMutationAttempts;
        var objects = remote.ObjectMutationAttempts;
        using var cancellation = new CancellationTokenSource();
        var readbacks = 0;
        handler.BeforeRequest = request =>
        {
            if (cancellation.IsCancellationRequested && request.Method == HttpMethod.Get
                && request.RequestUri!.AbsolutePath.Contains("/git/blobs/", StringComparison.Ordinal)) readbacks++;
            return Task.CompletedTask;
        };
        remote.CancelAfterMutation = GitHubCoordinationRefTests.LostMutation.Blob;
        remote.CancelSource = cancellation;
        remote.LostBlobReadback = readback;
        if (readback != GitHubCoordinationRefTests.LostReadback.Exact)
            remote.LoseResponse = GitHubCoordinationRefTests.LostMutation.Blob;
        async Task Write() => Assert.Equal(CampaignCheckpointWriteKind.PublicationFailure,
            (await store.CreateIfAbsentAsync(artifact.ExactUtf8Json.AsMemory(), artifact.CheckpointRevision,
                artifact.Sha256, cancellation.Token)).Kind);
        if (readback == GitHubCoordinationRefTests.LostReadback.Exact)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(Write);
        else await Write();
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(writes, remote.RefMutationAttempts);
        Assert.Equal(objects + 1, remote.ObjectMutationAttempts);
        Assert.True(readbacks > 0);
    }

    [Theory]
    [InlineData("read", "caller")]
    [InlineData("plan", "caller")]
    [InlineData("claim", "caller")]
    [InlineData("read", "timeout")]
    [InlineData("plan", "timeout")]
    [InlineData("claim", "timeout")]
    [InlineData("read", "response-loss")]
    [InlineData("plan", "response-loss")]
    [InlineData("claim", "response-loss")]
    public async Task Remote_checkpoint_ports_distinguish_caller_cancellation_from_transport_failure(string operation, string failure)
    {
        var scenario = CreateProposalScenario();
        var remote = new GitHubCoordinationRefTests.CoordinationRemote();
        using var handler = new GitHubCoordinationRefTests.CheckpointInterceptHandler(remote);
        using var client = GitHubCoordinationRefTests.CheckpointClient(remote, scenario.InitialState.CampaignLineage, handler,
            timeoutMilliseconds: failure == "timeout" ? 100 : 30_000);
        await using var store = ContractScribe.GitHub.Coordination.GitHubCoordinationStore.Create(client);
        if (operation != "claim")
            Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
        var writes = remote.RefMutationAttempts;
        using var cancellation = new CancellationTokenSource();
        var callerCancelled = failure == "caller";
        if (failure == "timeout") handler.PauseNextRequest = true;
        else handler.BeforeRequest = _ =>
        {
            if (callerCancelled) cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        };
        async Task Invoke()
        {
            if (operation == "plan")
                Assert.False(await store.BindPlanAsync(CampaignPlanner.CreateRecoveryProjection(scenario.Plan), cancellation.Token));
            else
            {
                var kind = operation == "claim" ? await store.AcquireCheckpointAsync(cancellation.Token)
                    : (await store.ReadAsync(cancellation.Token)).Kind;
                Assert.Equal(CampaignCheckpointReadKind.Unreadable, kind);
                Assert.Equal(callerCancelled ? ContractScribe.GitHub.Transport.GitHubFailureCode.Cancelled
                    : failure == "timeout" ? ContractScribe.GitHub.Transport.GitHubFailureCode.Timeout
                    : ContractScribe.GitHub.Transport.GitHubFailureCode.ResponseLost, store.CheckpointFailure!.Code);
            }
        }
        try
        {
            if (callerCancelled && operation != "claim") await Assert.ThrowsAnyAsync<OperationCanceledException>(Invoke);
            else await Invoke();
            Assert.Equal(writes, remote.RefMutationAttempts);
        }
        finally { handler.BeforeRequest = null; }
    }

    [Fact]
    public async Task Remote_checkpoint_archives_usage_and_restores_from_a_fresh_store()
    {
        var scenario = CreateProposalScenario(targetLimit: 1, maximumProviderRequests: null,
            maximumInputTokens: null, maximumUncachedInputTokens: null, maximumOutputTokens: null,
            maximumCostMicrounits: null, maximumElapsedMilliseconds: null);
        var exchange = new ScriptedDocumentationScribeModelExchange([
            ScriptedDocumentationScribeStep.Return(new([], [new(C3Submission(scenario))]))]);
        var (artifact, _) = await CompleteC3RuntimeAsync(scenario, exchange);
        var remote = new GitHubCoordinationRefTests.CoordinationRemote();
        using var client = GitHubCoordinationRefTests.CheckpointClient(remote, artifact.State.CampaignLineage);
        var projection = CampaignPlanner.CreateRecoveryProjection(scenario.Plan);
        CampaignCheckpointArtifact archived;
        await using (var store = ContractScribe.GitHub.Coordination.GitHubCoordinationStore.Create(client))
        {
            Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
            Assert.True(await store.BindPlanAsync(projection, default));
            Assert.Equal(CampaignCheckpointWriteKind.Written, (await store.CreateIfAbsentAsync(
                artifact.ExactUtf8Json.AsMemory(), artifact.CheckpointRevision, artifact.Sha256, default)).Kind);
            var accepted = CampaignCheckpointAcceptance.AcceptCurrent(await store.ReadAsync(default));
            archived = Assert.IsType<CampaignCheckpointArtifact>(CampaignDispatchArchive.Prepare(accepted.AcceptedCheckpoint!));
            Assert.Equal(CampaignCheckpointWriteKind.Written, (await store.ReplaceIfCurrentAsync(
                artifact.CheckpointRevision, artifact.Sha256, archived.ExactUtf8Json.AsMemory(),
                archived.CheckpointRevision, archived.Sha256, default)).Kind);
            var mutations = remote.RefMutationAttempts;
            Assert.Equal(CampaignCheckpointWriteKind.CurrentMismatch, (await store.ReplaceIfCurrentAsync(
                artifact.CheckpointRevision, artifact.Sha256, archived.ExactUtf8Json.AsMemory(),
                archived.CheckpointRevision, archived.Sha256, default)).Kind);
            Assert.Equal(mutations, remote.RefMutationAttempts);
            // Checkpoint traffic must not consume the publication-event history budget.
            for (var index = 0; index < 1025; index++)
                Assert.Equal(0, await store.WithPublicationAsync(() => Task.FromResult(0), default));
            remote.ServerNow = remote.ServerNow.AddSeconds(301);
            using var takeoverClient = GitHubCoordinationRefTests.CheckpointClient(remote, artifact.State.CampaignLineage);
            await using var takeover = ContractScribe.GitHub.Coordination.GitHubCoordinationStore.Create(takeoverClient);
            Assert.Equal(CampaignCheckpointReadKind.Found, await takeover.AcquireCheckpointAsync(default));
            var takeoverHead = remote.CoordinationHead;
            mutations = remote.RefMutationAttempts;
            Assert.Equal(CampaignCheckpointWriteKind.LeaseConflict, (await store.ReplaceIfCurrentAsync(
                archived.CheckpointRevision, archived.Sha256, archived.ExactUtf8Json.AsMemory(),
                archived.CheckpointRevision, archived.Sha256, default)).Kind);
            Assert.Equal(mutations, remote.RefMutationAttempts);
            Assert.Equal(takeoverHead, remote.CoordinationHead);
        }
        var readsBeforeRestore = remote.BlobReadAttempts;
        using var resumedClient = GitHubCoordinationRefTests.CheckpointClient(remote, artifact.State.CampaignLineage);
        await using var resumed = ContractScribe.GitHub.Coordination.GitHubCoordinationStore.Create(resumedClient);
        Assert.Equal(CampaignCheckpointReadKind.Found, await resumed.AcquireCheckpointAsync(default));
        Assert.True(await resumed.BindPlanAsync(projection, default));
        var restored = CampaignCheckpointAcceptance.AcceptCurrent(await resumed.ReadAsync(default));
        Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted, restored.Kind);
        Assert.Equal(archived.Sha256, restored.AcceptedCheckpoint!.Artifact.Sha256);
        Assert.Equal(artifact.State.LineageCharges, restored.AcceptedCheckpoint.Artifact.State.LineageCharges);
        Assert.InRange(remote.BlobReadAttempts - readsBeforeRestore, 1, 100);
        var input = WithBudget(scenario, scenario.Input.ExecutionPolicy.CampaignBudget with
        {
            MaximumProviderRequests = checked((int)artifact.State.LineageCharges.ProviderRequests.TotalCharged + 1),
        });
        var refresh = await ChangedBaseCampaignReconciler.RefreshLifetimeCapsAsync(restored.AcceptedCheckpoint!, resumed,
            scenario.ExecutionAuthority, "style.synthetic", scenario.StyleProjection, "samples/Synthetic.csproj",
            input, CampaignPlanner.Plan(input), () => true, default);
        Assert.Equal(ChangedBaseCampaignReconciliationKind.Accepted, refresh.Kind);
        var refreshed = CampaignCheckpointAcceptance.AcceptCurrent(await resumed.ReadAsync(default));
        Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted, refreshed.Kind);
        Assert.Equal(archived.State.ArchivedDispatchPrefix, refreshed.AcceptedCheckpoint!.Artifact.State.ArchivedDispatchPrefix);
        Assert.Equal(archived.State.LineageCharges, refreshed.AcceptedCheckpoint.Artifact.State.LineageCharges);
    }

    [Theory]
    [InlineData("drop-checkpoint")]
    [InlineData("replace-plan")]
    [InlineData("reset-history")]
    public async Task Canonical_successor_cannot_remove_checkpoint_or_replace_its_unchanged_plan(string tamper)
    {
        var scenario = UnlimitedScenario();
        var exchange = new ScriptedDocumentationScribeModelExchange([
            ScriptedDocumentationScribeStep.Return(new([], [new(C3Submission(scenario))]))]);
        var (artifact, _) = await CompleteC3RuntimeAsync(scenario, exchange);
        var remote = new GitHubCoordinationRefTests.CoordinationRemote();
        using (var client = GitHubCoordinationRefTests.CheckpointClient(remote, artifact.State.CampaignLineage))
        await using (var store = ContractScribe.GitHub.Coordination.GitHubCoordinationStore.Create(client))
        {
            Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
            Assert.True(await store.BindPlanAsync(CampaignPlanner.CreateRecoveryProjection(scenario.Plan), default));
            Assert.Equal(CampaignCheckpointWriteKind.Written, (await store.CreateIfAbsentAsync(
                artifact.ExactUtf8Json.AsMemory(), artifact.CheckpointRevision, artifact.Sha256, default)).Kind);
        }
        remote.AppendDocument(document =>
        {
            if (tamper == "drop-checkpoint") return document with { Checkpoint = null, Plan = null };
            if (tamper == "reset-history")
            {
                var initial = scenario.InitialState;
                var reset = CampaignStateJson.CreateArtifact(CampaignStateFactory.CreateValidated(initial.ProductRevision,
                    initial.CampaignLineage, initial.Snapshot, artifact.CheckpointRevision + 1, initial.ConfiguredCeilings,
                    initial.LineageCharges, initial.WorkItems, initial.Batch));
                return document with
                {
                    Checkpoint = new(remote.SeedBlob(reset.ExactUtf8Json.ToArray()),
                    reset.Sha256, reset.CheckpointRevision)
                };
            }
            var changed = JsonNode.Parse(CampaignPlanner.CreateRecoveryProjection(scenario.Plan).ExactUtf8Json.AsSpan())!;
            changed["workItems"]![0]!["targets"]![0]!["source"]!["path"] = "substituted.cs";
            var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(changed);
            return document with
            {
                Plan = document.Plan! with
                {
                    Oid = remote.SeedBlob(bytes),
                    Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))
                }
            };
        });
        var writes = remote.RefMutationAttempts;
        using var freshClient = GitHubCoordinationRefTests.CheckpointClient(remote, artifact.State.CampaignLineage);
        await using var fresh = ContractScribe.GitHub.Coordination.GitHubCoordinationStore.Create(freshClient);
        Assert.Equal(CampaignCheckpointReadKind.Invalid, await fresh.AcquireCheckpointAsync(default));
        Assert.Equal(writes, remote.RefMutationAttempts);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Canonical_successor_cannot_reclassify_observed_elapsed_history(bool eraseZero, bool eraseUnknown)
    {
        var scenario = UnlimitedScenario();
        var state = scenario.InitialState;
        var elapsed = new CampaignChargeObservation(eraseZero || eraseUnknown ? 0 : 100, eraseUnknown ? 100 : 0, eraseZero ? 0 : 100);
        var artifact = CampaignStateJson.CreateArtifact(CampaignStateFactory.CreateValidated(
            state.ProductRevision, state.CampaignLineage, state.Snapshot, state.CheckpointRevision,
            state.ConfiguredCeilings, state.LineageCharges with { ActiveElapsedMilliseconds = elapsed },
            state.WorkItems, state.Batch));
        var remote = new GitHubCoordinationRefTests.CoordinationRemote();
        using (var client = GitHubCoordinationRefTests.CheckpointClient(remote, state.CampaignLineage))
        await using (var store = ContractScribe.GitHub.Coordination.GitHubCoordinationStore.Create(client))
        {
            Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
            Assert.True(await store.BindPlanAsync(CampaignPlanner.CreateRecoveryProjection(scenario.Plan), default));
            Assert.Equal(CampaignCheckpointWriteKind.Written, (await store.CreateIfAbsentAsync(
                artifact.ExactUtf8Json.AsMemory(), artifact.CheckpointRevision, artifact.Sha256, default)).Kind);
        }
        var changed = CampaignStateJson.CreateArtifact(CampaignStateFactory.CreateValidated(
            state.ProductRevision, state.CampaignLineage, state.Snapshot, artifact.CheckpointRevision + 1,
            state.ConfiguredCeilings, state.LineageCharges with
            {
                ActiveElapsedMilliseconds = new(eraseZero ? null : eraseUnknown ? 100 : 0, eraseUnknown ? 0 : elapsed.TotalCharged, elapsed.TotalCharged),
            }, state.WorkItems, state.Batch));
        remote.AppendDocument(document => document with
        {
            Checkpoint = new(remote.SeedBlob(changed.ExactUtf8Json.ToArray()), changed.Sha256, changed.CheckpointRevision),
        });
        var writes = remote.RefMutationAttempts;
        using var freshClient = GitHubCoordinationRefTests.CheckpointClient(remote, state.CampaignLineage);
        await using var fresh = ContractScribe.GitHub.Coordination.GitHubCoordinationStore.Create(freshClient);
        Assert.Equal(CampaignCheckpointReadKind.Invalid, await fresh.AcquireCheckpointAsync(default));
        Assert.Equal(writes, remote.RefMutationAttempts);
    }

    [Fact]
    public async Task Canonical_successor_cannot_reopen_a_completed_terminal_provider_failure()
    {
        var scenario = UnlimitedScenario();
        var exchange = new ScriptedDocumentationScribeModelExchange([
            ScriptedDocumentationScribeStep.Return(new([], [],
                failure: new(DocumentationScribeModelFailureCode.Authentication)))]);
        var (artifact, _) = await CompleteC3RuntimeAsync(scenario, exchange);
        var work = Assert.Single(artifact.State.WorkItems.Where(work => work.Status == CampaignWorkStatus.Closed));
        Assert.Equal(CampaignProviderFinalDisposition.Terminal, work.ClosedOutcome!.ProviderDisposition);
        var remote = new GitHubCoordinationRefTests.CoordinationRemote();
        using (var client = GitHubCoordinationRefTests.CheckpointClient(remote, artifact.State.CampaignLineage))
        await using (var store = ContractScribe.GitHub.Coordination.GitHubCoordinationStore.Create(client))
        {
            Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
            Assert.True(await store.BindPlanAsync(CampaignPlanner.CreateRecoveryProjection(scenario.Plan), default));
            Assert.Equal(CampaignCheckpointWriteKind.Written, (await store.CreateIfAbsentAsync(
                artifact.ExactUtf8Json.AsMemory(), artifact.CheckpointRevision, artifact.Sha256, default)).Kind);
        }
        var state = artifact.State;
        var reset = CampaignStateJson.CreateArtifact(CampaignStateFactory.CreateValidated(
            state.ProductRevision, state.CampaignLineage, state.Snapshot, state.CheckpointRevision + 1,
            state.ConfiguredCeilings, state.LineageCharges, state.WorkItems.Select(item => item.WorkItemKey == work.WorkItemKey
                ? item with { Status = CampaignWorkStatus.Planned, ClosedOutcome = null } : item),
            state.Batch, dispatchObservations: state.DispatchObservations));
        Assert.True(CampaignStateJson.Parse(reset.ExactUtf8Json.AsMemory()).IsValid);
        remote.AppendDocument(document => document with
        {
            Checkpoint = new(remote.SeedBlob(reset.ExactUtf8Json.ToArray()), reset.Sha256, reset.CheckpointRevision),
        });
        var writes = remote.RefMutationAttempts;
        using var freshClient = GitHubCoordinationRefTests.CheckpointClient(remote, state.CampaignLineage);
        await using var fresh = ContractScribe.GitHub.Coordination.GitHubCoordinationStore.Create(freshClient);
        Assert.Equal(CampaignCheckpointReadKind.Invalid, await fresh.AcquireCheckpointAsync(default));
        Assert.Equal(writes, remote.RefMutationAttempts);
    }

    [Fact]
    public void Recovery_projection_compares_complete_source_facts_instead_of_claimed_execution_identity()
    {
        var scenario = CreateProposalScenario();
        var projection = CampaignPlanner.CreateRecoveryProjection(scenario.Plan);
        Assert.True(projection.Matches(projection.ExactUtf8Json.AsSpan()));
        var changed = JsonNode.Parse(projection.ExactUtf8Json.AsSpan())!.AsObject();
        changed["workItems"]![0]!["targets"]![0]!["source"]!["path"] = "substituted.cs";
        Assert.Equal(scenario.Plan.ExecutionCommitment, changed["ExecutionCommitment"]!.GetValue<string>());
        Assert.False(projection.Matches(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(changed)));
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("missing")]
    [InlineData("stale")]
    public async Task Conditional_remote_readback_must_bind_archive_evidence_to_the_actual_successor(string fault)
    {
        var scenario = CreateProposalScenario(targetLimit: 1, maximumProviderRequests: null,
            maximumInputTokens: null, maximumUncachedInputTokens: null, maximumOutputTokens: null,
            maximumCostMicrounits: null, maximumElapsedMilliseconds: null);
        var exchange = new ScriptedDocumentationScribeModelExchange([
            ScriptedDocumentationScribeStep.Return(new([], [new(C3Submission(scenario))]))]);
        var (artifact, _) = await CompleteC3RuntimeAsync(scenario, exchange);
        var remote = new GitHubCoordinationRefTests.CoordinationRemote();
        using var client = GitHubCoordinationRefTests.CheckpointClient(remote, artifact.State.CampaignLineage);
        await using var store = ContractScribe.GitHub.Coordination.GitHubCoordinationStore.Create(client);
        Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
        Assert.True(await store.BindPlanAsync(CampaignPlanner.CreateRecoveryProjection(scenario.Plan), default));
        Assert.Equal(CampaignCheckpointWriteKind.Written, (await store.CreateIfAbsentAsync(
            artifact.ExactUtf8Json.AsMemory(), artifact.CheckpointRevision, artifact.Sha256, default)).Kind);
        var accepted = CampaignCheckpointAcceptance.AcceptCurrent(await store.ReadAsync(default));
        var archived = Assert.IsType<CampaignCheckpointArtifact>(CampaignDispatchArchive.Prepare(accepted.AcceptedCheckpoint!));
        var compacted = await store.ReplaceIfCurrentAsync(artifact.CheckpointRevision, artifact.Sha256,
            archived.ExactUtf8Json.AsMemory(), archived.CheckpointRevision, archived.Sha256, default);
        Assert.Equal(CampaignCheckpointWriteKind.Written, compacted.Kind);
        Assert.Equal(archived.Sha256, compacted.Readback!.VerifiedArchive!.CheckpointSha256);
        var transition = CampaignStateReducer.Stop(archived, CampaignTerminalKind.Cancelled);
        Assert.Equal(CampaignTransitionKind.Applied, transition.Kind);
        var filtered = new ArchiveReadbackStore(store, fault, compacted.Readback.VerifiedArchive);
        var result = await CampaignCheckpointAcceptance.AcceptAsync(filtered, transition);
        Assert.Equal(fault == "valid" ? CampaignCheckpointAcceptanceKind.Accepted : CampaignCheckpointAcceptanceKind.ReadbackMismatch,
            result.Kind);
        Assert.Equal(fault == "valid", result.AcceptedCheckpoint is not null);
    }

    private sealed class ArchiveReadbackStore(ICampaignCheckpointStore inner, string fault,
        CampaignVerifiedArchive stale) : ICampaignCheckpointStore
    {
        public ValueTask<CampaignCheckpointReadResult> ReadAsync(CancellationToken token) => inner.ReadAsync(token);
        public ValueTask<CampaignCheckpointWriteResult> CreateIfAbsentAsync(ReadOnlyMemory<byte> bytes, long revision, string sha,
            CancellationToken token) => inner.CreateIfAbsentAsync(bytes, revision, sha, token);
        public async ValueTask<CampaignCheckpointWriteResult> ReplaceIfCurrentAsync(long expectedRevision, string expectedSha,
            ReadOnlyMemory<byte> bytes, long revision, string sha, CancellationToken token, CancellationToken mutationCancellationToken = default)
        {
            mutationCancellationToken.ThrowIfCancellationRequested();
            var result = await inner.ReplaceIfCurrentAsync(expectedRevision, expectedSha, bytes, revision, sha, token, mutationCancellationToken);
            if (fault == "valid" || result.Readback is not { } observed) return result;
            return CampaignCheckpointWriteResult.Written(CampaignCheckpointReadResult.Found(observed.ExactUtf8Json.AsSpan(),
                observed.CheckpointRevision!.Value, observed.Sha256!, fault == "missing" ? null : stale));
        }
    }

    [Fact]
    public async Task Archived_usage_requires_exact_predecessor_evidence_at_checkpoint_acceptance()
    {
        var scenario = CreateProposalScenario(targetLimit: 1, maximumProviderRequests: null,
            maximumInputTokens: null, maximumUncachedInputTokens: null, maximumOutputTokens: null,
            maximumCostMicrounits: null, maximumElapsedMilliseconds: null);
        var exchange = new ScriptedDocumentationScribeModelExchange([
            ScriptedDocumentationScribeStep.Return(new([], [new(C3Submission(scenario))]))]);
        var (predecessor, _) = await CompleteC3RuntimeAsync(scenario, exchange);
        var accepted = CampaignCheckpointAcceptance.AcceptCurrent(CampaignCheckpointReadResult.Found(
            predecessor.ExactUtf8Json.AsSpan(), predecessor.CheckpointRevision, predecessor.Sha256));
        Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted, accepted.Kind);
        var archived = Assert.IsType<CampaignCheckpointArtifact>(CampaignDispatchArchive.Prepare(accepted.AcceptedCheckpoint!));
        Assert.Empty(archived.State.DispatchObservations);
        Assert.Equal(predecessor.State.LineageCharges, archived.State.LineageCharges);
        Assert.Equal(CampaignCheckpointAcceptanceKind.InvalidRead, CampaignCheckpointAcceptance.AcceptCurrent(
            CampaignCheckpointReadResult.Found(archived.ExactUtf8Json.AsSpan(), archived.CheckpointRevision, archived.Sha256)).Kind);
        var proof = CampaignDispatchArchive.VerifyTransition(predecessor, archived);
        var changedJson = System.Text.Encoding.UTF8.GetString(archived.ExactUtf8Json.AsSpan())
            .Replace($"\"checkpointRevision\":{archived.CheckpointRevision},",
                $"\"checkpointRevision\":{archived.CheckpointRevision + 1},", StringComparison.Ordinal);
        var changedTransition = Assert.IsType<CampaignCheckpointArtifact>(
            CampaignStateJson.Parse(System.Text.Encoding.UTF8.GetBytes(changedJson)).Artifact);
        Assert.Throws<CampaignStateValidationException>(() => CampaignDispatchArchive.VerifyTransition(predecessor, changedTransition));
        Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted, CampaignCheckpointAcceptance.AcceptCurrent(
            CampaignCheckpointReadResult.Found(archived.ExactUtf8Json.AsSpan(), archived.CheckpointRevision, archived.Sha256, proof)).Kind);
        var wrong = CampaignStateJson.CreateArtifact(archived.State with
        {
            ArchivedDispatchPrefix = archived.State.ArchivedDispatchPrefix! with { WitnessSha256 = new string('a', 64) },
        });
        Assert.Throws<CampaignStateValidationException>(() => CampaignDispatchArchive.Verify(predecessor, wrong));
        Assert.Equal(CampaignCheckpointAcceptanceKind.InvalidRead, CampaignCheckpointAcceptance.AcceptCurrent(
            CampaignCheckpointReadResult.Found(wrong.ExactUtf8Json.AsSpan(), wrong.CheckpointRevision, wrong.Sha256, proof)).Kind);
    }
}
