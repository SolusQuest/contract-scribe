using System.Collections.Immutable;

namespace ContractScribe.GitHub.Transport;

// Wire observations are not R3-R6 ownership, recovery, or publication capabilities.
internal abstract record GitHubValue
{
    public sealed override string ToString() => GetType().Name;
}

internal enum GitHubFailureCode
{
    InvalidRequest, Authentication, Permission, NotFound, Conflict, Validation,
    RateLimit, Cancelled, Timeout, ResponseLost, InvalidResponse, HostFailure,
}

internal enum GitHubDelivery { NotDispatched, Read, NeedsReadback, Ambiguous }
internal enum GitHubObjectKind { Blob, Tree, Commit }
internal enum GitHubPermission { Metadata, Contents, PullRequests }
internal enum GitHubPermissionLevel { Read, Write }
internal enum GitHubActorKind { User, Bot, Organization, Mannequin }
internal enum GitHubTreeMode { File, Executable, Directory, SymbolicLink, Submodule }

internal sealed record GitHubRepositoryIdentity(long Id, string NodeId, string Owner, string Name) : GitHubValue;
internal sealed record GitHubRolePermissions(bool Admin, bool Push, bool Pull, bool? Maintain, bool? Triage) : GitHubValue;
internal sealed record GitHubRepository(GitHubRepositoryIdentity Identity, bool Private, bool Archived,
    bool Disabled, GitHubRolePermissions? RolePermissions) : GitHubValue;
internal sealed record GitHubActor(long Id, string NodeId, string Login, GitHubActorKind Kind) : GitHubValue;
internal sealed record GitHubPermissionRequirement(GitHubPermission Permission, GitHubPermissionLevel Level) : GitHubValue;
internal sealed record GitHubPermissionAlternatives(
    ImmutableArray<ImmutableArray<GitHubPermissionRequirement>> Alternatives,
    bool HasUnrepresentedAlternatives) : GitHubValue;
internal sealed record GitHubRetryObservation(int? RetryAfterSeconds, long? ResetUnixSeconds, long? Remaining) : GitHubValue;
internal sealed record GitHubFailure(GitHubFailureCode Code, int? HttpStatus = null,
    GitHubRetryObservation? Retry = null) : GitHubValue;

internal abstract record GitHubMutationContext(GitHubRepositoryIdentity Repository, string OperationCommitment) : GitHubValue;
internal sealed record GitHubObjectContext(GitHubRepositoryIdentity Repository, string OperationCommitment,
    GitHubObjectKind Kind, string ExpectedOid) : GitHubMutationContext(Repository, OperationCommitment);
internal sealed record GitHubRefContext(GitHubRepositoryIdentity Repository, string OperationCommitment,
    string Ref, string BeforeOid, string AfterOid, string ClientMutationId) : GitHubMutationContext(Repository, OperationCommitment);
internal sealed record GitHubPullRequestContext(GitHubRepositoryIdentity Repository, string OperationCommitment,
    string CreationCommitment, string HeadRef, string HeadOid, string BaseRef, string ExpectedBaseOid,
    string TitleSha256, string BodySha256) : GitHubMutationContext(Repository, OperationCommitment)
{
    internal bool Draft => true;
    internal bool MaintainerCanModify => false;
}

internal sealed record GitHubApiResult<T>(T? Value, GitHubFailure? Failure, GitHubDelivery Delivery,
    GitHubMutationContext? Context = null, GitHubPermissionAlternatives? RequiredPermissions = null) : GitHubValue
    where T : class
{
    internal static GitHubApiResult<T> Failed(GitHubFailureCode code, GitHubMutationContext? context = null,
        bool dispatched = false, int? status = null, GitHubRetryObservation? retry = null,
        GitHubPermissionAlternatives? permissions = null) =>
        new(null, new(code, status, retry), dispatched && context is not null ? GitHubDelivery.Ambiguous
            : dispatched ? GitHubDelivery.Read : GitHubDelivery.NotDispatched, context, permissions);
}

internal sealed record GitHubObjectIdentity(string Oid) : GitHubValue;
internal sealed record GitHubRef(string Name, string NodeId, string Oid) : GitHubValue;
internal sealed record GitHubBlob(string Oid, ImmutableArray<byte> Bytes) : GitHubValue;
internal sealed record GitHubTreeEntry(string Path, GitHubTreeMode Mode, string Oid, long? Size) : GitHubValue;
internal sealed record GitHubTree(string Oid, ImmutableArray<GitHubTreeEntry> Entries) : GitHubValue;
internal sealed record GitHubCommitActor(string Name, string Email, DateTimeOffset Date) : GitHubValue;
internal sealed record GitHubCommit(string Oid, string TreeOid, ImmutableArray<string> Parents,
    string Message, GitHubCommitActor Author, GitHubCommitActor Committer) : GitHubValue;
internal sealed record GitHubPullRequestHead(GitHubRepositoryIdentity? Repository, string? Ref, string? Oid) : GitHubValue;
internal sealed record GitHubPullRequest(long Id, string NodeId, int Number, bool Open, bool Draft,
    bool? Merged, DateTimeOffset? MergedAt, DateTimeOffset? ClosedAt, DateTimeOffset CreatedAt,
    string Title, string? Body, GitHubActor? Author, GitHubPullRequestHead Head,
    GitHubRepositoryIdentity BaseRepository, string BaseRef, string BaseOid, bool? MaintainerCanModify) : GitHubValue;
internal sealed record GitHubPullRequestSet(ImmutableArray<GitHubPullRequest> Items,
    int Pages, int ObservedItems, long BodyBytes, bool Exhausted) : GitHubValue;
internal sealed record GitHubAcknowledgement : GitHubValue;

// These are the resource owner's frozen inputs, not arbitrary JSON or endpoint selectors.
internal sealed record GitHubCreateCommit(string ExpectedOid, string TreeOid, string ParentOid,
    string Message, GitHubCommitActor Author, GitHubCommitActor Committer) : GitHubValue;
internal sealed record GitHubCreatePullRequest(string CreationCommitment, string HeadRef, string HeadOid,
    string BaseRef, string ExpectedBaseOid, string Title, string Body) : GitHubValue;
internal sealed record GitHubUpdateRef(string Ref, string BeforeOid, string AfterOid, bool ExpectedAbsence) : GitHubValue;

internal sealed class GitHubProtocolException(GitHubFailureCode code = GitHubFailureCode.InvalidResponse)
    : Exception("The GitHub transport boundary rejected the operation.")
{
    internal GitHubFailureCode Code { get; } = code;
    public override string ToString() => Message;
}
