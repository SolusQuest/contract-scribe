namespace ContractScribe.Core;

public enum GitHubPublicationResultKind
{
    LocalInvalid,
    ReplayNoOp,
    Admitted,
    RecoveredContentPartial,
    RecoveredRefPartial,
    Published,
    AwaitingReview,
    Merged,
    ClosedUnmerged,
    StaleBaseAfterCreate,
    Stale,
    HumanChange,
    Conflict,
    Permission,
    RateLimit,
    Cancelled,
    Timeout,
    HostFailure,
}

public enum GitHubPublicationResourceKind
{
    Blob,
    Tree,
    Commit,
}

public sealed record GitHubPublicationLocalFailure(
    GitHubPublicationValidationCode Code,
    GitHubPublicationFieldId? Field);

public enum GitHubPublicationFieldId
{
    Repository,
    TargetRef,
    ExpectedBaseCommit,
    Campaign,
    Snapshot,
    WorkPlan,
    Checkpoint,
    Candidate,
    Operation,
    Generation,
    Predecessor,
    Policy,
    ChangedFiles,
    Payload,
    Authorization,
    RemoteObservation,
}

public sealed record GitHubPublicationContentResidual(
    GitHubPublicationResourceKind ResourceKind,
    string ExpectedOid,
    string OperationCommitmentSha256);

public sealed record GitHubPublicationRefResidual(
    string RefName,
    string ExpectedPredecessorOid,
    string ObservedOid,
    string OperationCommitmentSha256);

public sealed record GitHubPublicationClaimIdentity(
    string RefName,
    string ClaimOid,
    string OperationId,
    string OperationCommitmentSha256);

public sealed record GitHubPublicationPullRequestIdentity(
    long Number,
    string GenerationId,
    string HeadRef,
    string HeadOid,
    string OperationCommitmentSha256);

public sealed record GitHubPublicationStaleDraftResidual(
    long PullRequestNumber,
    string OwnershipMarkerSha256,
    string OwnedHeadRef,
    string OwnedHeadOid,
    string ExpectedBaseRef,
    string ExpectedBaseOid,
    string ObservedBaseOid,
    string GenerationId,
    string OperationId,
    string OperationCommitmentSha256);

public enum GitHubPublicationRemoteFailureKind
{
    Stale,
    HumanChange,
    Conflict,
    Permission,
    RateLimit,
    Cancelled,
    Timeout,
    HostFailure,
}

public sealed record GitHubPublicationRemoteFailure(GitHubPublicationRemoteFailureKind Kind);

/// <summary>
/// Closed result union. Only the detail property associated with <see cref="Kind"/>
/// can be populated; raw requests, responses, exceptions, credentials, and byte
/// payloads are not representable.
/// </summary>
public sealed class GitHubPublicationResult
{
    private GitHubPublicationResult(
        GitHubPublicationResultKind kind,
        GitHubPublicationLocalFailure? localFailure = null,
        GitHubPublicationContentResidual? contentResidual = null,
        GitHubPublicationRefResidual? refResidual = null,
        GitHubPublicationClaimIdentity? claim = null,
        GitHubPublicationPullRequestIdentity? pullRequest = null,
        GitHubPublicationStaleDraftResidual? staleDraft = null,
        GitHubPublicationRemoteFailure? remoteFailure = null)
    {
        Kind = kind;
        LocalFailure = localFailure;
        ContentResidual = contentResidual;
        RefResidual = refResidual;
        Claim = claim;
        PullRequest = pullRequest;
        StaleDraft = staleDraft;
        RemoteFailure = remoteFailure;
    }

    public GitHubPublicationResultKind Kind { get; }
    public GitHubPublicationLocalFailure? LocalFailure { get; }
    public GitHubPublicationContentResidual? ContentResidual { get; }
    public GitHubPublicationRefResidual? RefResidual { get; }
    public GitHubPublicationClaimIdentity? Claim { get; }
    public GitHubPublicationPullRequestIdentity? PullRequest { get; }
    public GitHubPublicationStaleDraftResidual? StaleDraft { get; }
    public GitHubPublicationRemoteFailure? RemoteFailure { get; }

    public static GitHubPublicationResult LocalInvalid(
        GitHubPublicationValidationCode code,
        GitHubPublicationFieldId? field = null)
    {
        if (!Enum.IsDefined(code)
            || (field is not null && !Enum.IsDefined(field.Value)))
        {
            throw new ArgumentException("Local failure detail is invalid.", nameof(field));
        }
        return new(GitHubPublicationResultKind.LocalInvalid, localFailure: new(code, field));
    }

    public static GitHubPublicationResult ReplayNoOp(GitHubPublicationClaimIdentity claim) =>
        new(GitHubPublicationResultKind.ReplayNoOp, claim: ValidateClaim(claim));

    public static GitHubPublicationResult Admitted(GitHubPublicationClaimIdentity claim) =>
        new(GitHubPublicationResultKind.Admitted, claim: ValidateClaim(claim));

    public static GitHubPublicationResult RecoveredContentPartial(
        GitHubPublicationContentResidual residual) =>
        new(GitHubPublicationResultKind.RecoveredContentPartial,
            contentResidual: ValidateContentResidual(residual));

    public static GitHubPublicationResult RecoveredRefPartial(
        GitHubPublicationRefResidual residual) =>
        new(GitHubPublicationResultKind.RecoveredRefPartial,
            refResidual: ValidateRefResidual(residual));

    public static GitHubPublicationResult Published(GitHubPublicationPullRequestIdentity pullRequest) =>
        new(GitHubPublicationResultKind.Published, pullRequest: ValidatePullRequest(pullRequest));

    public static GitHubPublicationResult AwaitingReview(GitHubPublicationPullRequestIdentity pullRequest) =>
        new(GitHubPublicationResultKind.AwaitingReview, pullRequest: ValidatePullRequest(pullRequest));

    public static GitHubPublicationResult Merged(GitHubPublicationPullRequestIdentity pullRequest) =>
        new(GitHubPublicationResultKind.Merged, pullRequest: ValidatePullRequest(pullRequest));

    public static GitHubPublicationResult ClosedUnmerged(GitHubPublicationPullRequestIdentity pullRequest) =>
        new(GitHubPublicationResultKind.ClosedUnmerged, pullRequest: ValidatePullRequest(pullRequest));

    public static GitHubPublicationResult StaleBaseAfterCreate(
        GitHubPublicationStaleDraftResidual residual) =>
        new(GitHubPublicationResultKind.StaleBaseAfterCreate,
            staleDraft: ValidateStaleDraft(residual));

    public static GitHubPublicationResult FromRemoteFailure(
        GitHubPublicationRemoteFailureKind kind)
    {
        var resultKind = kind switch
        {
            GitHubPublicationRemoteFailureKind.Stale => GitHubPublicationResultKind.Stale,
            GitHubPublicationRemoteFailureKind.HumanChange => GitHubPublicationResultKind.HumanChange,
            GitHubPublicationRemoteFailureKind.Conflict => GitHubPublicationResultKind.Conflict,
            GitHubPublicationRemoteFailureKind.Permission => GitHubPublicationResultKind.Permission,
            GitHubPublicationRemoteFailureKind.RateLimit => GitHubPublicationResultKind.RateLimit,
            GitHubPublicationRemoteFailureKind.Cancelled => GitHubPublicationResultKind.Cancelled,
            GitHubPublicationRemoteFailureKind.Timeout => GitHubPublicationResultKind.Timeout,
            GitHubPublicationRemoteFailureKind.HostFailure => GitHubPublicationResultKind.HostFailure,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        return new(resultKind, remoteFailure: new(kind));
    }

    public override string ToString() => $"{nameof(GitHubPublicationResult)}:{Kind}";

    private static GitHubPublicationClaimIdentity ValidateClaim(GitHubPublicationClaimIdentity claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        if (!GitHubPublicationFactory.IsRefName(claim.RefName)
            || !GitHubPublicationFactory.IsGitOid(claim.ClaimOid, allowMissing: false)
            || !GitHubPublicationFactory.IsOpaqueIdentifier(claim.OperationId)
            || !GitHubPublicationFactory.IsSha256(claim.OperationCommitmentSha256))
        {
            throw new ArgumentException("Claim identity is invalid.", nameof(claim));
        }
        return claim;
    }

    private static GitHubPublicationContentResidual ValidateContentResidual(
        GitHubPublicationContentResidual residual)
    {
        ArgumentNullException.ThrowIfNull(residual);
        if (!Enum.IsDefined(residual.ResourceKind)
            || !GitHubPublicationFactory.IsGitOid(residual.ExpectedOid, allowMissing: false)
            || !GitHubPublicationFactory.IsSha256(residual.OperationCommitmentSha256))
        {
            throw new ArgumentException("Content residual is invalid.", nameof(residual));
        }
        return residual;
    }

    private static GitHubPublicationRefResidual ValidateRefResidual(
        GitHubPublicationRefResidual residual)
    {
        ArgumentNullException.ThrowIfNull(residual);
        if (!GitHubPublicationFactory.IsRefName(residual.RefName)
            || !GitHubPublicationFactory.IsGitOid(residual.ExpectedPredecessorOid, allowMissing: true)
            || !GitHubPublicationFactory.IsGitOid(residual.ObservedOid, allowMissing: false)
            || !GitHubPublicationFactory.IsSha256(residual.OperationCommitmentSha256))
        {
            throw new ArgumentException("Ref residual is invalid.", nameof(residual));
        }
        return residual;
    }

    private static GitHubPublicationPullRequestIdentity ValidatePullRequest(
        GitHubPublicationPullRequestIdentity pullRequest)
    {
        ArgumentNullException.ThrowIfNull(pullRequest);
        if (pullRequest.Number <= 0
            || !GitHubPublicationFactory.IsOpaqueIdentifier(pullRequest.GenerationId)
            || !GitHubPublicationFactory.IsRefName(pullRequest.HeadRef)
            || !GitHubPublicationFactory.IsGitOid(pullRequest.HeadOid, allowMissing: false)
            || !GitHubPublicationFactory.IsSha256(pullRequest.OperationCommitmentSha256))
        {
            throw new ArgumentException("Pull-request identity is invalid.", nameof(pullRequest));
        }
        return pullRequest;
    }

    private static GitHubPublicationStaleDraftResidual ValidateStaleDraft(
        GitHubPublicationStaleDraftResidual residual)
    {
        ArgumentNullException.ThrowIfNull(residual);
        if (residual.PullRequestNumber <= 0
            || !GitHubPublicationFactory.IsSha256(residual.OwnershipMarkerSha256)
            || !GitHubPublicationFactory.IsRefName(residual.OwnedHeadRef)
            || !GitHubPublicationFactory.IsGitOid(residual.OwnedHeadOid, allowMissing: false)
            || !GitHubPublicationFactory.IsRefName(residual.ExpectedBaseRef)
            || !GitHubPublicationFactory.IsGitOid(residual.ExpectedBaseOid, allowMissing: false)
            || !GitHubPublicationFactory.IsGitOid(residual.ObservedBaseOid, allowMissing: false)
            || string.Equals(residual.ExpectedBaseOid, residual.ObservedBaseOid,
                StringComparison.Ordinal)
            || !GitHubPublicationFactory.IsOpaqueIdentifier(residual.GenerationId)
            || !GitHubPublicationFactory.IsOpaqueIdentifier(residual.OperationId)
            || !GitHubPublicationFactory.IsSha256(residual.OperationCommitmentSha256))
        {
            throw new ArgumentException("Stale-draft residual is invalid.", nameof(residual));
        }
        return residual;
    }
}
