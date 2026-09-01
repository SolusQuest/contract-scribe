using System.Collections.Immutable;

namespace ContractScribe.Core;

public static class GitHubPublicationContract
{
    public const int Version = 1;
    public const string Revision = "github-publication-v1";
    public const int MaximumChangedFiles = CampaignStateContract.MaximumChangedFiles;
    public const int MaximumIdentifierScalars = CampaignStateContract.MaximumIdentifierScalars;
    public const int MaximumPathScalars = CampaignStateContract.MaximumPathScalars;
    public const string MissingGitObjectId = "0000000000000000000000000000000000000000";
}

public enum GitHubPublicationTransitionKind
{
    Initial,
    SameSnapshotAppend,
    SuccessorAfterMerge,
    SuccessorAfterClosedUnmerged,
}

public enum GitHubPublicationValidationCode
{
    InvalidVocabulary,
    InvalidBound,
    InvalidPath,
    InvalidHash,
    InvalidPolicy,
    InvalidTransition,
    InvalidAuthorization,
    InvalidCorrelation,
    DuplicatePath,
    CaseCollidingPath,
    PayloadMismatch,
    ArithmeticOverflow,
}

public sealed class GitHubPublicationValidationException : FormatException
{
    internal GitHubPublicationValidationException(
        GitHubPublicationValidationCode code,
        string message)
        : base(message) => Code = code;

    public GitHubPublicationValidationCode Code { get; }
}

public sealed record GitHubPublicationPolicy(
    int MaximumDocumentationBlocks,
    int MaximumDistinctChangedFiles,
    long MaximumCumulativePatchBytes);

public sealed record GitHubChangedFileAuthority(
    string Path,
    string OriginalFileSha256,
    string CandidateFileSha256,
    int ChangedDocumentationBlockCount,
    int OriginalDocumentationByteCount,
    int CandidateDocumentationByteCount,
    int OriginalDocumentationLineCount,
    int CandidateDocumentationLineCount,
    string? PrecedingCandidateFileSha256 = null);

public sealed record GitHubClosedUnmergedSuccessorAuthorization(
    string AuthorizationId,
    long ClosedPullRequestNumber,
    string ClosedGenerationId,
    string ClosedHeadOid,
    string FreshSnapshotCommitmentSha256,
    string FreshWorkPlanCommitmentSha256,
    string FreshCandidateCommitmentSha256,
    string NewGenerationId,
    string OperationId);

/// <summary>
/// Caller-supplied facts available before credentials, ambient Git discovery,
/// filesystem access, or a network request. Authenticated remote observations
/// deliberately have no place in this type.
/// </summary>
public sealed record GitHubPublicationAuthorityInput(
    string RepositoryOwner,
    string RepositoryName,
    string TargetRef,
    string ExpectedBaseCommitOid,
    string CampaignLineage,
    string SnapshotCommitmentSha256,
    string ExecutionCommitmentSha256,
    string WorkPlanCommitmentSha256,
    long CheckpointRevision,
    string CheckpointSha256,
    string CandidateCommitmentSha256,
    string PatchRequestSha256,
    string PatchResultCommitmentSha256,
    string AcceptedProjectionCommitmentSha256,
    string OperationId,
    string GenerationId,
    string? LogicalPredecessorId,
    string? PrecedingCandidateCommitmentSha256,
    GitHubPublicationTransitionKind Transition,
    GitHubPublicationPolicy Policy,
    IEnumerable<GitHubChangedFileAuthority> ChangedFiles,
    GitHubClosedUnmergedSuccessorAuthorization? ClosedUnmergedSuccessorAuthorization = null);

public sealed class ValidatedGitHubPublicationAuthority
{
    internal ValidatedGitHubPublicationAuthority(
        GitHubPublicationAuthorityInput input,
        ImmutableArray<GitHubChangedFileAuthority> changedFiles,
        int cumulativeDocumentationBlocks,
        long cumulativePatchBytes,
        string policyCommitmentSha256,
        string authorityCommitmentSha256,
        string operationCommitmentSha256)
    {
        RepositoryOwner = input.RepositoryOwner;
        RepositoryName = input.RepositoryName;
        TargetRef = input.TargetRef;
        ExpectedBaseCommitOid = input.ExpectedBaseCommitOid;
        CampaignLineage = input.CampaignLineage;
        SnapshotCommitmentSha256 = input.SnapshotCommitmentSha256;
        ExecutionCommitmentSha256 = input.ExecutionCommitmentSha256;
        WorkPlanCommitmentSha256 = input.WorkPlanCommitmentSha256;
        CheckpointRevision = input.CheckpointRevision;
        CheckpointSha256 = input.CheckpointSha256;
        CandidateCommitmentSha256 = input.CandidateCommitmentSha256;
        PatchRequestSha256 = input.PatchRequestSha256;
        PatchResultCommitmentSha256 = input.PatchResultCommitmentSha256;
        AcceptedProjectionCommitmentSha256 = input.AcceptedProjectionCommitmentSha256;
        OperationId = input.OperationId;
        GenerationId = input.GenerationId;
        LogicalPredecessorId = input.LogicalPredecessorId;
        PrecedingCandidateCommitmentSha256 = input.PrecedingCandidateCommitmentSha256;
        Transition = input.Transition;
        Policy = input.Policy;
        ChangedFiles = changedFiles;
        ClosedUnmergedSuccessorAuthorization = input.ClosedUnmergedSuccessorAuthorization;
        CumulativeDocumentationBlocks = cumulativeDocumentationBlocks;
        CumulativePatchBytes = cumulativePatchBytes;
        PolicyCommitmentSha256 = policyCommitmentSha256;
        AuthorityCommitmentSha256 = authorityCommitmentSha256;
        OperationCommitmentSha256 = operationCommitmentSha256;
    }

    public string RepositoryOwner { get; }
    public string RepositoryName { get; }
    public string TargetRef { get; }
    public string ExpectedBaseCommitOid { get; }
    public string CampaignLineage { get; }
    public string SnapshotCommitmentSha256 { get; }
    public string ExecutionCommitmentSha256 { get; }
    public string WorkPlanCommitmentSha256 { get; }
    public long CheckpointRevision { get; }
    public string CheckpointSha256 { get; }
    public string CandidateCommitmentSha256 { get; }
    public string PatchRequestSha256 { get; }
    public string PatchResultCommitmentSha256 { get; }
    public string AcceptedProjectionCommitmentSha256 { get; }
    public string OperationId { get; }
    public string GenerationId { get; }
    public string? LogicalPredecessorId { get; }
    public string? PrecedingCandidateCommitmentSha256 { get; }
    public GitHubPublicationTransitionKind Transition { get; }
    public GitHubPublicationPolicy Policy { get; }
    public ImmutableArray<GitHubChangedFileAuthority> ChangedFiles { get; }
    public GitHubClosedUnmergedSuccessorAuthorization? ClosedUnmergedSuccessorAuthorization { get; }
    public int CumulativeDocumentationBlocks { get; }
    public long CumulativePatchBytes { get; }
    public string PolicyCommitmentSha256 { get; }
    public string AuthorityCommitmentSha256 { get; }
    public string OperationCommitmentSha256 { get; }

    public override string ToString() => nameof(ValidatedGitHubPublicationAuthority);
}

public sealed record GitHubChangedFilePayloadInput(string Path, ReadOnlyMemory<byte> CandidateBytes);

public sealed class GitHubValidatedChangedFilePayload
{
    internal GitHubValidatedChangedFilePayload(string path, ImmutableArray<byte> candidateBytes)
    {
        Path = path;
        CandidateBytes = candidateBytes;
    }

    public string Path { get; }
    public ImmutableArray<byte> CandidateBytes { get; }
}

/// <summary>
/// Closed, defensive-copy, nonpersistent byte payload. It is deliberately not
/// part of authority, commitment, result, or diagnostic text.
/// </summary>
public sealed class ValidatedGitHubChangedFilePayload
{
    internal ValidatedGitHubChangedFilePayload(
        ImmutableArray<GitHubValidatedChangedFilePayload> files,
        string authorityCommitmentSha256)
    {
        Files = files;
        AuthorityCommitmentSha256 = authorityCommitmentSha256;
    }

    public ImmutableArray<GitHubValidatedChangedFilePayload> Files { get; }
    public string AuthorityCommitmentSha256 { get; }
    public override string ToString() => nameof(ValidatedGitHubChangedFilePayload);
}

public enum GitHubRemoteEntryKind
{
    Blob,
    Tree,
    SymbolicLink,
    Submodule,
}

public sealed record GitHubRemoteEntryObservation(
    string Path,
    string ObjectOid,
    GitHubRemoteEntryKind Kind,
    string Mode,
    string FullFileSha256,
    bool WasPreviouslyPublished);

public sealed record GitHubAuthenticatedRemoteObservation(
    string CanonicalRepositoryId,
    string ObservedTargetCommitOid,
    string ObservedBaseTreeOid,
    string CoordinationRefOid,
    string? ProposalRefOid,
    string? ProposalCommitOid,
    string? ProposalParentOid,
    string? ProposalTreeOid,
    long? ActivePullRequestNumber,
    string? ActivePullRequestState,
    IEnumerable<GitHubRemoteEntryObservation> Entries);

public sealed record GitHubDeterministicCommitPayload(
    string TreeLayoutCommitmentSha256,
    string MessageSha256,
    string ParentOid,
    string AuthorName,
    string AuthorEmail,
    string AuthorTimestamp,
    string CommitterName,
    string CommitterEmail,
    string CommitterTimestamp,
    string OwnershipMarkerSha256,
    string ExpectedCommitOid);

public sealed record GitHubDeterministicPullRequestPayload(
    string HeadRef,
    string BaseRef,
    string TitleSha256,
    string BodyMarkerSha256,
    bool Draft,
    bool MaintainerCanModify);

public sealed class ValidatedGitHubPreparedRemoteOperation
{
    internal ValidatedGitHubPreparedRemoteOperation(
        GitHubAuthenticatedRemoteObservation observation,
        ImmutableArray<GitHubRemoteEntryObservation> entries,
        GitHubDeterministicCommitPayload coordinationCommit,
        GitHubDeterministicCommitPayload proposalCommit,
        GitHubDeterministicPullRequestPayload pullRequest,
        string commitmentSha256)
    {
        Observation = observation with { Entries = entries };
        Entries = entries;
        CoordinationCommit = coordinationCommit;
        ProposalCommit = proposalCommit;
        PullRequest = pullRequest;
        CommitmentSha256 = commitmentSha256;
    }

    public GitHubAuthenticatedRemoteObservation Observation { get; }
    public ImmutableArray<GitHubRemoteEntryObservation> Entries { get; }
    public GitHubDeterministicCommitPayload CoordinationCommit { get; }
    public GitHubDeterministicCommitPayload ProposalCommit { get; }
    public GitHubDeterministicPullRequestPayload PullRequest { get; }
    public string CommitmentSha256 { get; }
    public override string ToString() => nameof(ValidatedGitHubPreparedRemoteOperation);
}

public interface IGitHubPublicationPort
{
    ValueTask<GitHubPublicationResult> PublishAsync(
        ValidatedGitHubPublicationAuthority authority,
        ValidatedGitHubChangedFilePayload payload,
        CancellationToken cancellationToken);
}
