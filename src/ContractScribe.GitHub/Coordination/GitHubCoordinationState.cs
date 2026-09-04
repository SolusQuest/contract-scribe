using System.Collections.Immutable;

namespace ContractScribe.GitHub.Coordination;

internal enum GitHubCoordinationStage
{
    Claimed,
    ContentCreated,
    ProposalRefAdvanced,
    PullRequestCreated,
    Published,
    AwaitingReview,
    StaleDraft,
    Stale,
    Merged,
    ClosedUnmerged,
}

internal sealed record GitHubCoordinationChangedFile(string Path, string CandidateSha256)
{
    public override string ToString() => nameof(GitHubCoordinationChangedFile);
}

internal sealed class GitHubCoordinationState
{
    internal GitHubCoordinationState(
        GitHubCoordinationStage stage,
        string repositoryId,
        string targetRef,
        string targetCommitOid,
        string snapshotCommitmentSha256,
        string authorityCommitmentSha256,
        string policyCommitmentSha256,
        string operationId,
        string operationCommitmentSha256,
        string currentCandidateCommitmentSha256,
        string? precedingOperationId,
        string? precedingAuthorityCommitmentSha256,
        string? precedingCandidateCommitmentSha256,
        string generationId,
        string transition,
        string coordinationPredecessorOid,
        string? contentCommitOid,
        string? proposalRefOid,
        string? proposalCommitOid,
        string? proposalTreeOid,
        string? pullRequestCreationOperationCommitmentSha256,
        int? pullRequestNumber,
        string? expectedBaseOid,
        string? observedBaseOid,
        string? ownershipMarkerSha256,
        int cumulativeDocumentationBlocks,
        long cumulativePatchBytes,
        ImmutableArray<GitHubCoordinationChangedFile> cumulativeChangedFiles)
    {
        Stage = stage;
        RepositoryId = repositoryId;
        TargetRef = targetRef;
        TargetCommitOid = targetCommitOid;
        SnapshotCommitmentSha256 = snapshotCommitmentSha256;
        AuthorityCommitmentSha256 = authorityCommitmentSha256;
        PolicyCommitmentSha256 = policyCommitmentSha256;
        OperationId = operationId;
        OperationCommitmentSha256 = operationCommitmentSha256;
        CurrentCandidateCommitmentSha256 = currentCandidateCommitmentSha256;
        PrecedingOperationId = precedingOperationId;
        PrecedingAuthorityCommitmentSha256 = precedingAuthorityCommitmentSha256;
        PrecedingCandidateCommitmentSha256 = precedingCandidateCommitmentSha256;
        GenerationId = generationId;
        Transition = transition;
        CoordinationPredecessorOid = coordinationPredecessorOid;
        ContentCommitOid = contentCommitOid;
        ProposalRefOid = proposalRefOid;
        ProposalCommitOid = proposalCommitOid;
        ProposalTreeOid = proposalTreeOid;
        PullRequestCreationOperationCommitmentSha256 = pullRequestCreationOperationCommitmentSha256;
        PullRequestNumber = pullRequestNumber;
        ExpectedBaseOid = expectedBaseOid;
        ObservedBaseOid = observedBaseOid;
        OwnershipMarkerSha256 = ownershipMarkerSha256;
        CumulativeDocumentationBlocks = cumulativeDocumentationBlocks;
        CumulativePatchBytes = cumulativePatchBytes;
        CumulativeChangedFiles = cumulativeChangedFiles;
    }

    internal GitHubCoordinationStage Stage { get; }
    internal string RepositoryId { get; }
    internal string TargetRef { get; }
    internal string TargetCommitOid { get; }
    internal string SnapshotCommitmentSha256 { get; }
    internal string AuthorityCommitmentSha256 { get; }
    internal string PolicyCommitmentSha256 { get; }
    internal string OperationId { get; }
    internal string OperationCommitmentSha256 { get; }
    internal string CurrentCandidateCommitmentSha256 { get; }
    internal string? PrecedingOperationId { get; }
    internal string? PrecedingAuthorityCommitmentSha256 { get; }
    internal string? PrecedingCandidateCommitmentSha256 { get; }
    internal string GenerationId { get; }
    internal string Transition { get; }
    internal string CoordinationPredecessorOid { get; }
    internal string? ContentCommitOid { get; }
    internal string? ProposalRefOid { get; }
    internal string? ProposalCommitOid { get; }
    internal string? ProposalTreeOid { get; }
    internal string? PullRequestCreationOperationCommitmentSha256 { get; }
    internal int? PullRequestNumber { get; }
    internal string? ExpectedBaseOid { get; }
    internal string? ObservedBaseOid { get; }
    internal string? OwnershipMarkerSha256 { get; }
    internal int CumulativeDocumentationBlocks { get; }
    internal long CumulativePatchBytes { get; }
    internal ImmutableArray<GitHubCoordinationChangedFile> CumulativeChangedFiles { get; }

    public override string ToString() => nameof(GitHubCoordinationState);
}
