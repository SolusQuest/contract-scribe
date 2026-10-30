using System.Text;
using System.Text.Json.Nodes;
using ContractScribe.Core;

namespace ContractScribe.Tests;

public sealed partial class CampaignStateContractTests
{
    [Theory]
    [InlineData("written")]
    [InlineData("write-failed")]
    [InlineData("unknown-applied")]
    [InlineData("exact-concurrent-winner")]
    [InlineData("crash-before")]
    public async Task Composed_member_and_progress_have_one_physical_acceptance_and_no_observer_send_grant(string fault)
    {
        var trial = await SolusTrialAsync();
        var permit = await trial.Coordinator.Scope.BeginHostAsync(DocumentationScribeHostOperation.TerminalSubmission, default);
        Assert.NotNull(permit);
        Assert.True(permit.TryBeginOperation());
        var before = trial.Authority.AcceptedCheckpoint.Artifact;
        var item = JsonNode.Parse(C3Submission(trial.Scenario, trial.Request))!["items"]![0]!;
        var target = item["targetId"]!.GetValue<string>();
        var validation = DocumentationScribeValidation.ValidateTargetSubmission(trial.Request, target, [],
            Encoding.UTF8.GetBytes(item["result"]!.ToJsonString()));
        Assert.True(validation.Submission is not null, validation.Failure?.Code);
        var submission = validation.Submission!;
        var proposed = DocumentationScribeValidation.AcknowledgeSubmission(submission,
            new(trial.Request.ArtifactSha256, target, submission.ContentSha256, DocumentationScribeAcknowledgementStage.DurablyRecorded));
        var progress = DocumentationScribeValidation.CreateProgressNotification(trial.Request, trial.Attempt,
            0, 2, [target], [proposed], [], false);
        var combined = CampaignStateReducer.AcceptScribeSubmissionWithProgress(trial.Authority, submission, progress);
        Assert.Equal(CampaignTransitionKind.Applied, combined.Kind);
        Assert.Equal(before.CheckpointRevision + 1, combined.Artifact.CheckpointRevision);
        Assert.Equal(before.Sha256, trial.Authority.AcceptedCheckpoint.Artifact.Sha256);
        Assert.Equal(before.Sha256, trial.Store.Current.Sha256); // Pure composition has no effect or ACK.
        if (fault == "crash-before")
        {
            var old = await CampaignCheckpointAcceptance.AcceptCurrentAsync(trial.Store);
            var retired = CampaignStateReducer.RetireInterruptedProviderAttempt(old.AcceptedCheckpoint!);
            Assert.Null(retired.Artifact.State.WorkItems[0].TrustedProposal);
            Assert.Empty(retired.Artifact.State.ScribeProgress!.Members.Where(member => member.ContentSha256 is not null));
            return;
        }
        var store = new TransitionCheckpointStore(before)
        {
            ReplaceResult = fault == "written" ? CampaignCheckpointWriteKind.Written
                : fault == "exact-concurrent-winner" ? CampaignCheckpointWriteKind.CurrentMismatch
                : CampaignCheckpointWriteKind.PublicationFailure,
            WinnerOnRejectedWrite = fault is "unknown-applied" or "exact-concurrent-winner" ? combined.Artifact : null,
        };
        var accepted = await CampaignCheckpointAcceptance.AcceptAsync(store, combined);
        if (fault is "written" or "exact-concurrent-winner")
        {
            Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted, accepted.Kind);
            Assert.Equal(fault == "written", trial.Authority.AdvanceAcceptedProgress(combined, accepted.AcceptedCheckpoint!));
            Assert.False(trial.Authority.AdvanceAcceptedProgress(combined, accepted.AcceptedCheckpoint!));
        }
        else Assert.Equal(CampaignCheckpointAcceptanceKind.PublicationFailure, accepted.Kind);
        var physical = CampaignCheckpointAcceptance.AcceptCurrent(await store.ReadAsync(default));
        Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted, physical.Kind);
        var applied = fault != "write-failed";
        Assert.Equal(applied ? 2 : 1, physical.Artifact!.State.ScribeProgress!.NextSequence);
        Assert.Equal(applied, physical.Artifact.State.WorkItems[0].TrustedProposal is not null);
        if (applied)
        {
            Assert.Equal(progress.ProgressSha256, physical.Artifact.State.ScribeProgress.LastProgressSha256);
            var recovered = CampaignStateReducer.RetireInterruptedProviderAttempt(physical.AcceptedCheckpoint!);
            Assert.NotNull(recovered.Artifact.State.WorkItems[0].TrustedProposal);
            Assert.Equal(2, recovered.Artifact.State.ScribeProgress!.NextSequence);
        }
        Assert.Equal(fault == "written" ? combined.Artifact.Sha256 : before.Sha256,
            trial.Authority.AcceptedCheckpoint.Artifact.Sha256);
    }
}
