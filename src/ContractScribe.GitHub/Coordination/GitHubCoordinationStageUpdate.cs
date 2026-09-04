using static ContractScribe.GitHub.Transport.GitHubResponseReader;

namespace ContractScribe.GitHub.Coordination;

internal sealed class GitHubCoordinationStageUpdate
{
    private GitHubCoordinationStageUpdate(
        GitHubCoordinationStage stage,
        string? contentCommitOid = null,
        string? proposalRefOid = null,
        string? proposalCommitOid = null,
        string? proposalTreeOid = null,
        string? pullRequestCreationOperationCommitmentSha256 = null,
        int? pullRequestNumber = null,
        string? expectedBaseOid = null,
        string? observedBaseOid = null,
        string? ownershipMarkerSha256 = null)
    {
        Stage = stage;
        ContentCommitOid = contentCommitOid;
        ProposalRefOid = proposalRefOid;
        ProposalCommitOid = proposalCommitOid;
        ProposalTreeOid = proposalTreeOid;
        PullRequestCreationOperationCommitmentSha256 = pullRequestCreationOperationCommitmentSha256;
        PullRequestNumber = pullRequestNumber;
        ExpectedBaseOid = expectedBaseOid;
        ObservedBaseOid = observedBaseOid;
        OwnershipMarkerSha256 = ownershipMarkerSha256;
    }

    internal GitHubCoordinationStage Stage { get; }
    internal string? ContentCommitOid { get; }
    internal string? ProposalRefOid { get; }
    internal string? ProposalCommitOid { get; }
    internal string? ProposalTreeOid { get; }
    internal string? PullRequestCreationOperationCommitmentSha256 { get; }
    internal int? PullRequestNumber { get; }
    internal string? ExpectedBaseOid { get; }
    internal string? ObservedBaseOid { get; }
    internal string? OwnershipMarkerSha256 { get; }

    internal static GitHubCoordinationStageUpdate ContentCreated(string contentCommitOid)
    {
        Require(IsOid(contentCommitOid));
        return new(GitHubCoordinationStage.ContentCreated, contentCommitOid);
    }

    internal static GitHubCoordinationStageUpdate ProposalRefAdvanced(
        string proposalCommitOid,
        string proposalTreeOid)
    {
        Require(IsOid(proposalCommitOid) && IsOid(proposalTreeOid));
        return new(GitHubCoordinationStage.ProposalRefAdvanced,
            proposalCommitOid, proposalCommitOid, proposalCommitOid, proposalTreeOid);
    }

    internal static GitHubCoordinationStageUpdate PullRequestResult(
        GitHubCoordinationStage stage,
        string proposalCommitOid,
        string proposalTreeOid,
        string creationCommitmentSha256,
        int pullRequestNumber,
        string expectedBaseOid,
        string observedBaseOid,
        string ownershipMarkerSha256)
    {
        Require((stage is GitHubCoordinationStage.PullRequestCreated
                or GitHubCoordinationStage.Published
                or GitHubCoordinationStage.StaleDraft)
            && IsOid(proposalCommitOid) && IsOid(proposalTreeOid)
            && Hex(creationCommitmentSha256, 64) && pullRequestNumber > 0
            && IsOid(expectedBaseOid) && IsOid(observedBaseOid)
            && Hex(ownershipMarkerSha256, 64));
        return new(stage, proposalCommitOid, proposalCommitOid, proposalCommitOid,
            proposalTreeOid, creationCommitmentSha256, pullRequestNumber,
            expectedBaseOid, observedBaseOid, ownershipMarkerSha256);
    }

    internal static GitHubCoordinationStageUpdate Stale(
        string expectedBaseOid,
        string observedBaseOid)
    {
        Require(IsOid(expectedBaseOid) && IsOid(observedBaseOid)
            && expectedBaseOid != observedBaseOid);
        return new(GitHubCoordinationStage.Stale,
            expectedBaseOid: expectedBaseOid,
            observedBaseOid: observedBaseOid);
    }

    internal static GitHubCoordinationStageUpdate AwaitingReview() =>
        new(GitHubCoordinationStage.AwaitingReview);

    internal static GitHubCoordinationStageUpdate Terminal(GitHubCoordinationStage stage)
    {
        Require(stage is GitHubCoordinationStage.Merged or GitHubCoordinationStage.ClosedUnmerged);
        return new(stage);
    }

    public override string ToString() => nameof(GitHubCoordinationStageUpdate);

    private static void Require(bool condition)
    {
        if (!condition) throw new GitHubCoordinationException();
    }
}
