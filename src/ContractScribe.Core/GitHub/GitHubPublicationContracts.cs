using System.Collections.Immutable;

namespace ContractScribe.Core;

public static class GitHubPublicationContract
{
    public const int Version = 1;
    public const string Revision = "github-publication-v1";
    public const int MaximumChangedFiles = CampaignStateContract.MaximumChangedFiles;
    public const int MaximumIdentifierScalars = CampaignStateContract.MaximumIdentifierScalars;
    public const int MaximumPathScalars = CampaignStateContract.MaximumPathScalars;
    public const int MaximumPayloadBytesPerFile = 16_777_216;
    public const long MaximumAggregatePayloadBytes = 67_108_864;
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

public sealed record GitHubPublicationM4Ceilings(
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
    int CandidateDocumentationLineCount);

public sealed record GitHubPrecedingChangedFileAuthority(
    string Path,
    string CandidateFileSha256);

public enum GitHubPublicationPredecessorDisposition
{
    Merged,
    ClosedUnmerged,
}

public sealed record GitHubPublicationPredecessorAuthority(
    string LogicalPredecessorId,
    long PullRequestNumber,
    string GenerationId,
    string HeadOid,
    GitHubPublicationPredecessorDisposition Disposition);

public sealed record GitHubClosedUnmergedSuccessorAuthorization(
    string AuthorizationId,
    string LogicalPredecessorId,
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
    string? PrecedingOperationId,
    string? PrecedingAuthorityCommitmentSha256,
    string? PrecedingCandidateCommitmentSha256,
    GitHubPublicationPredecessorAuthority? TerminalPredecessor,
    GitHubPublicationTransitionKind Transition,
    GitHubPublicationM4Ceilings AcceptedM4Ceilings,
    GitHubPublicationPolicy Policy,
    IEnumerable<GitHubChangedFileAuthority> ChangedFiles,
    IEnumerable<GitHubPrecedingChangedFileAuthority> PrecedingChangedFiles,
    GitHubClosedUnmergedSuccessorAuthorization? ClosedUnmergedSuccessorAuthorization = null);

public sealed class ValidatedGitHubPublicationAuthority
{
    internal ValidatedGitHubPublicationAuthority(
        GitHubPublicationAuthorityInput input,
        ImmutableArray<GitHubChangedFileAuthority> changedFiles,
        ImmutableArray<GitHubPrecedingChangedFileAuthority> precedingChangedFiles,
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
        PrecedingOperationId = input.PrecedingOperationId;
        PrecedingAuthorityCommitmentSha256 = input.PrecedingAuthorityCommitmentSha256;
        PrecedingCandidateCommitmentSha256 = input.PrecedingCandidateCommitmentSha256;
        TerminalPredecessor = input.TerminalPredecessor;
        Transition = input.Transition;
        AcceptedM4Ceilings = input.AcceptedM4Ceilings;
        Policy = input.Policy;
        ChangedFiles = changedFiles;
        PrecedingChangedFiles = precedingChangedFiles;
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
    public string? PrecedingOperationId { get; }
    public string? PrecedingAuthorityCommitmentSha256 { get; }
    public string? PrecedingCandidateCommitmentSha256 { get; }
    public GitHubPublicationPredecessorAuthority? TerminalPredecessor { get; }
    public GitHubPublicationTransitionKind Transition { get; }
    public GitHubPublicationM4Ceilings AcceptedM4Ceilings { get; }
    public GitHubPublicationPolicy Policy { get; }
    public ImmutableArray<GitHubChangedFileAuthority> ChangedFiles { get; }
    public ImmutableArray<GitHubPrecedingChangedFileAuthority> PrecedingChangedFiles { get; }
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

public interface IGitHubPublicationPort
{
    ValueTask<GitHubPublicationResult> PublishAsync(
        ValidatedGitHubPublicationAuthority authority,
        ValidatedGitHubChangedFilePayload payload,
        CancellationToken cancellationToken);
}
