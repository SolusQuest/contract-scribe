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
    SuccessorAfterMerge,
    SuccessorAfterClosedUnmerged,
}

public enum GitHubPublicationValidationCode
{
    InvalidVocabulary,
    InvalidBound,
    InvalidPath,
    InvalidHash,
    InvalidTransition,
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

public sealed record GitHubPublicationBatchTarget(string TargetKey, string WorkItemKey);

/// <summary>Exact selected manifest and accepted subset, without proposal or source bytes.</summary>
public sealed record GitHubPublicationBatchInput(
    string BatchIdentity,
    int CreationQuota,
    IEnumerable<GitHubPublicationBatchTarget> SelectedTargets,
    IEnumerable<string> AcceptedWorkItemKeys);

public sealed class ValidatedGitHubPublicationBatch
{
    internal ValidatedGitHubPublicationBatch(string identity, int creationQuota,
        ImmutableArray<GitHubPublicationBatchTarget> selectedTargets,
        ImmutableArray<string> acceptedWorkItemKeys, string resultSetCommitmentSha256)
    {
        Identity = identity;
        CreationQuota = creationQuota;
        SelectedTargets = selectedTargets;
        AcceptedWorkItemKeys = acceptedWorkItemKeys;
        ResultSetCommitmentSha256 = resultSetCommitmentSha256;
    }

    public string Identity { get; }
    public int CreationQuota { get; }
    public ImmutableArray<GitHubPublicationBatchTarget> SelectedTargets { get; }
    public ImmutableArray<string> AcceptedWorkItemKeys { get; }
    public string ResultSetCommitmentSha256 { get; }
    public override string ToString() => nameof(ValidatedGitHubPublicationBatch);
}

public sealed record GitHubChangedFileAuthority(
    string Path,
    string OriginalFileSha256,
    string CandidateFileSha256,
    int ChangedDocumentationBlockCount,
    int OriginalDocumentationByteCount,
    int CandidateDocumentationByteCount,
    int OriginalDocumentationLineCount,
    int CandidateDocumentationLineCount);

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
    GitHubPublicationTransitionKind Transition,
    GitHubPublicationBatchInput Batch,
    IEnumerable<GitHubChangedFileAuthority> ChangedFiles,
    GitHubPublicationPredecessorAuthority? TerminalPredecessor = null);

public sealed class ValidatedGitHubPublicationAuthority
{
    internal ValidatedGitHubPublicationAuthority(
        GitHubPublicationAuthorityInput input,
        ImmutableArray<GitHubChangedFileAuthority> changedFiles,
        ValidatedGitHubPublicationBatch batch,
        int cumulativeDocumentationBlocks,
        long cumulativePatchBytes,
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
        TerminalPredecessor = input.TerminalPredecessor;
        Transition = input.Transition;
        ChangedFiles = changedFiles;
        Batch = batch;
        CumulativeDocumentationBlocks = cumulativeDocumentationBlocks;
        CumulativePatchBytes = cumulativePatchBytes;
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
    public GitHubPublicationPredecessorAuthority? TerminalPredecessor { get; }
    public GitHubPublicationTransitionKind Transition { get; }
    public ImmutableArray<GitHubChangedFileAuthority> ChangedFiles { get; }
    public ValidatedGitHubPublicationBatch Batch { get; }
    public int CumulativeDocumentationBlocks { get; }
    public long CumulativePatchBytes { get; }
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
