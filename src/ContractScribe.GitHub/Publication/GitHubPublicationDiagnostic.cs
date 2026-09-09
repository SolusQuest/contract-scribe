using ContractScribe.Core;
using ContractScribe.GitHub.Coordination;
using ContractScribe.GitHub.GitData;
using ContractScribe.GitHub.Transport;
using PrOutcome = ContractScribe.GitHub.PullRequests.GitHubProposalOutcome;

namespace ContractScribe.GitHub.Publication;

internal enum GitHubPublicationBoundary
{
    Reconcile, Repository, CoordinationRead, CoordinationClaim, CoordinationRecord,
    CoordinationAdvanceStale, CoordinationAdvanceContent, CoordinationAdvanceRef,
    GitInspect, GitInspectPredecessor, GitPrepare, GitCreateContent, GitAdvanceRef,
    PullRequestPreflight, PullRequestObserve, PullRequestCreate, PullRequestRecover,
}

internal enum GitHubPublicationOwner { Reconciler, Transport, Coordination, GitData, PullRequests }

internal enum GitHubPublicationPredicate
{
    InvalidCorrelation, Cancelled, UnhandledException, RepositoryUnavailable, DifferentOperation,
    TargetMoved, SuccessorMismatch, AppendMismatch, TransitionMismatch, UnexpectedProposalRef,
    ClaimMismatch, CurrentMismatch, ClaimedRefPresent, StaleStage, UnexpectedStage,
    AppendCreateForbidden, CompletionHeadChanged, ObservationLimit, LifecycleOutcome,
    MissingOrUnexpectedComponent,
}

// Observations only: no capability, request body, identity, exception or retry instruction.
internal sealed record GitHubPublicationDiagnostic(
    GitHubPublicationBoundary Boundary, GitHubPublicationOwner Owner,
    GitHubCoordinationFailureKind? CoordinationFailure = null,
    GitHubProposalFailureKind? ProposalFailure = null, PrOutcome? PullRequestOutcome = null,
    GitHubFailureCode? TransportCode = null, int? TransportHttpStatus = null,
    GitHubDelivery? Delivery = null, GitHubFailureCode? RecoveryCode = null,
    int? RecoveryHttpStatus = null, GitHubObjectKind? ObjectKind = null,
    GitHubPublicationPredicate? Predicate = null) : GitHubValue
{
    internal bool IsValid => Enum.IsDefined(Boundary) && Enum.IsDefined(Owner)
        && Defined(CoordinationFailure) && Defined(ProposalFailure) && Defined(PullRequestOutcome)
        && Defined(TransportCode) && Defined(Delivery) && Defined(RecoveryCode)
        && Defined(ObjectKind) && Defined(Predicate)
        && Status(TransportHttpStatus) && Status(RecoveryHttpStatus);

    private static bool Defined<T>(T? value) where T : struct, Enum => value is null || Enum.IsDefined(value.Value);
    private static bool Status(int? value) => value is null or >= 100 and <= 599;
}

// Each invocation owns its immutable pair, including while queued behind the same gate.
internal sealed record GitHubPublicationAttempt(GitHubPublicationResult Result,
    GitHubPublicationDiagnostic? Diagnostic = null) : GitHubValue
{
    public static implicit operator GitHubPublicationAttempt(GitHubPublicationResult result) => new(result);
}
