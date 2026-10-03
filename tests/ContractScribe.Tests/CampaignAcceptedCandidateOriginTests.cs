using System.Text.Json.Nodes;
using ContractScribe.Cli;
using ContractScribe.Core;

namespace ContractScribe.Tests;

public sealed partial class CampaignStateContractTests
{
    [Theory]
    [InlineData(CampaignProviderFinalDisposition.Retryable, false)]
    [InlineData(CampaignProviderFinalDisposition.Terminal, true)]
    public void Append_selection_distinguishes_retryable_closed_work_from_completed_work(
        CampaignProviderFinalDisposition disposition, bool noWork)
    {
        var basis = CreateAcceptedCandidateScenario().State;
        var key = basis.WorkItems.Single(item => item.Status == CampaignWorkStatus.Planned).WorkItemKey;
        var closed = new CampaignWorkItemState(key, 1, 0, CampaignWorkStatus.Closed, null,
            new(CampaignWorkOutcomeStage.Scribe, CampaignWorkOutcomeCode.ProviderFailure, disposition,
                Hash('a'), null, null, null, Hash('b'), key));
        // Selection uses the existing batch's work keys; this does not manufacture a checkpoint or admission.
        var state = new CampaignCheckpointState(basis.ProductRevision, basis.CampaignLineage, basis.Snapshot,
            basis.CheckpointRevision, basis.ConfiguredCeilings, basis.LineageCharges,
            [basis.WorkItems.Single(item => item.Status == CampaignWorkStatus.Accepted), closed],
            basis.Batch,
            null, basis.CandidateObservation, basis.CumulativeOutcome, basis.KnownCompletedOperations, null, null)
        { AcceptedCandidateOrigin = basis.AcceptedCandidateOrigin };
        var configuration = new GitHubPublicationConfiguration("Owner", "repo", "refs/heads/main", new('a', 40),
            "append", "generation", new(128, 128, 4194304), GitHubPublicationTransitionKind.SameSnapshotAppend,
            new("previous", Hash('a'), basis.CandidateObservation!.PatchResultCommitmentSha256,
                "generation", Hash('b'), Hash('c'), []));
        var continuation = new CampaignAcceptedCandidateContinuation(null!, null!,
            new("unused", [], new(1, "campaign.unused", "snapshot.unused", "unused", configuration)),
            _ => throw new InvalidOperationException("Selection must not access credentials."));
        Assert.Equal(noWork, continuation.HasNoAppendWork(state));
    }

    [Fact]
    public void Append_selection_stops_on_an_accepted_fixed_batch_with_deferred_full_plan_work()
    {
        var state = CreateAcceptedCandidateScenario(targetLimit: 1).State;
        Assert.Contains(state.TargetProgress, item => item.Kind == CampaignTargetProgressKind.Deferred);
        var configuration = new GitHubPublicationConfiguration("Owner", "repo", "refs/heads/main", new('a', 40),
            "append", "generation", new(128, 128, 4194304), GitHubPublicationTransitionKind.SameSnapshotAppend,
            new("previous", Hash('a'), state.CandidateObservation!.PatchResultCommitmentSha256,
                "generation", Hash('b'), Hash('c'), []));
        var continuation = new CampaignAcceptedCandidateContinuation(null!, null!,
            new("unused", [], new(1, "campaign.unused", "snapshot.unused", "unused", configuration)),
            _ => throw new InvalidOperationException("Selection must not access credentials."));
        Assert.False(continuation.ReconstructAccepted(state));
        Assert.True(continuation.HasNoAppendWork(state));
    }

    [Theory]
    [InlineData(CampaignTerminalKind.Cancelled)]
    [InlineData(CampaignTerminalKind.Timeout)]
    [InlineData(CampaignTerminalKind.Exhausted)]
    public void First_preserving_stop_captures_exact_accepted_predecessor(CampaignTerminalKind kind)
    {
        var accepted = CampaignStateJson.CreateArtifact(CreateAcceptedCandidateScenario().State);
        Assert.Null(accepted.State.AcceptedCandidateOrigin!.CheckpointSha256);
        Assert.Equal(accepted.CheckpointRevision, accepted.State.AcceptedCandidateOrigin.CheckpointRevision);
        var stopped = CampaignStateReducer.Stop(accepted, kind);
        Assert.Equal(CampaignTransitionKind.Applied, stopped.Kind);
        Assert.Equal(accepted.Sha256, stopped.Artifact.State.AcceptedCandidateOrigin!.CheckpointSha256);
        Assert.Equal(accepted.CheckpointRevision, stopped.Artifact.State.AcceptedCandidateOrigin.CheckpointRevision);
        Assert.Equal(accepted.State.CandidateObservation, stopped.Artifact.State.AcceptedCandidateOrigin.CandidateObservation);
        Assert.True(CampaignStateJson.Parse(stopped.Artifact.ExactUtf8Json.AsMemory()).IsValid);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("missing-hash")]
    [InlineData("future")]
    [InlineData("product")]
    [InlineData("lineage")]
    [InlineData("snapshot")]
    [InlineData("configuration")]
    [InlineData("candidate")]
    public void Retained_origin_corruption_is_not_a_new_self_origin(string mutation)
    {
        var accepted = CampaignStateJson.CreateArtifact(CreateAcceptedCandidateScenario().State);
        var retained = CampaignStateReducer.Stop(accepted, CampaignTerminalKind.Cancelled).Artifact;
        var root = JsonNode.Parse(retained.ExactUtf8Json.AsSpan())!.AsObject();
        var origin = root["acceptedCandidateOrigin"]!;
        switch (mutation)
        {
            case "missing": root.Remove("acceptedCandidateOrigin"); break;
            case "null": root["acceptedCandidateOrigin"] = null; break;
            case "missing-hash": origin["checkpointSha256"] = null; break;
            case "future": origin["checkpointRevision"] = retained.CheckpointRevision + 1; break;
            case "product": origin["productRevision"]!["contentSha256"] = Hash('e'); break;
            case "lineage": origin["campaignLineage"] = "different"; break;
            case "snapshot": origin["snapshot"]!["opaqueSnapshotBinding"] = "different"; break;
            case "configuration": origin["campaignConfigurationCommitmentSha256"] = Hash('e'); break;
            case "candidate": origin["candidateObservation"] = null; break;
        }
        Assert.False(CampaignStateJson.Parse(System.Text.Encoding.UTF8.GetBytes(root.ToJsonString() + "\n")).IsValid);
    }

    [Fact]
    public void Accepted_origin_is_bounded_and_uses_the_published_candidate_schema()
    {
        var artifact = CampaignStateJson.CreateArtifact(CreateAcceptedCandidateScenario().State);
        using var document = System.Text.Json.JsonDocument.Parse(artifact.ExactUtf8Json.AsMemory());
        Assert.True(EvaluateCampaignSchema(document.RootElement).IsValid);
        var parsed = CampaignStateJson.Parse(artifact.ExactUtf8Json.AsMemory());
        Assert.True(parsed.IsValid);
        Assert.Equal(artifact.Sha256, parsed.Artifact!.Sha256);
        Assert.Null(CreateState().AcceptedCandidateOrigin);
    }
}
