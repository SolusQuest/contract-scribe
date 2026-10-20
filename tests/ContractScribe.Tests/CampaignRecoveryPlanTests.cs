using System.Text.Json.Nodes;
using ContractScribe.Agent.Runtime;
using ContractScribe.Core;
using ContractScribe.Cli;

namespace ContractScribe.Tests;

public sealed partial class CampaignStateContractTests
{
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
