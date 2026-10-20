using ContractScribe.Core;

namespace ContractScribe.Tests;

internal static class GitHubPublicationTestBatch
{
    internal static GitHubPublicationBatchInput ForFiles(IEnumerable<GitHubChangedFileAuthority>? files = null)
    {
        var count = (int)Math.Clamp(files?.Sum(file => (long)file.ChangedDocumentationBlockCount) ?? 1, 1, 512);
        var targets = Enumerable.Range(0, count).Select(index =>
            new GitHubPublicationBatchTarget($"target-{index:D4}", $"work-{index:D4}")).ToArray();
        return new(new('a', 64), Math.Max(100, count), targets, targets.Select(target => target.WorkItemKey).ToArray());
    }

    internal static GitHubPublicationAuthorityInput Copy(ValidatedGitHubPublicationAuthority a) => new(
        a.RepositoryOwner, a.RepositoryName, a.TargetRef, a.ExpectedBaseCommitOid, a.CampaignLineage,
        a.SnapshotCommitmentSha256, a.ExecutionCommitmentSha256, a.WorkPlanCommitmentSha256,
        a.CheckpointRevision, a.CheckpointSha256, a.CandidateCommitmentSha256, a.PatchRequestSha256,
        a.PatchResultCommitmentSha256, a.AcceptedProjectionCommitmentSha256, a.OperationId, a.GenerationId,
        a.Transition, new(a.Batch.Identity, a.Batch.CreationQuota, a.Batch.SelectedTargets, a.Batch.AcceptedWorkItemKeys),
        a.ChangedFiles, a.TerminalPredecessor);

    internal static ValidatedGitHubPublicationAuthority BindCheckpoint(
        ValidatedGitHubPublicationAuthority authority, CampaignCheckpointArtifact artifact)
    {
        var state = artifact.State;
        var candidate = state.CandidateObservation!;
        return GitHubPublicationFactory.CreateAuthority(Copy(authority) with
        {
            SnapshotCommitmentSha256 = ContractScribe.Cli.GitHubPublicationRequestFactory.CreateSnapshotCommitment(state.Snapshot),
            ExecutionCommitmentSha256 = state.Snapshot.ExecutionCommitmentSha256,
            WorkPlanCommitmentSha256 = state.Snapshot.ExecutionCommitmentSha256,
            CheckpointRevision = artifact.CheckpointRevision,
            CheckpointSha256 = artifact.Sha256,
            CandidateCommitmentSha256 = candidate.PatchResultCommitmentSha256,
            PatchRequestSha256 = candidate.PatchRequestSha256,
            PatchResultCommitmentSha256 = candidate.PatchResultCommitmentSha256,
            AcceptedProjectionCommitmentSha256 = candidate.AcceptedProjectionCommitmentSha256,
            Batch = GitHubPublicationFactory.CreateBatchInput(state),
            ChangedFiles = candidate.ChangedFiles.Select(file => new GitHubChangedFileAuthority(file.Path,
                file.OriginalFileSha256, file.CandidateFileSha256, file.ChangedDocumentationBlockCount,
                file.OriginalDocumentationByteCount, file.CandidateDocumentationByteCount,
                file.OriginalDocumentationLineCount, file.CandidateDocumentationLineCount)),
        });
    }
}
