using System.Collections.Immutable;
using ContractScribe.GitHub.PullRequests;
using ContractScribe.GitHub.Transport;

namespace ContractScribe.GitHub.Coordination;

internal enum GitHubCampaignLedgerKind { Missing, Current, Corrupt, Incompatible, Unverifiable }
internal enum GitHubCampaignKind { FirstRun, Unpublished, Active, Terminal, Unverifiable }
internal enum GitHubCampaignPullRequestKind { OpenDraft, OpenReady, ClosedUnmerged, Merged, Unknown }
internal enum GitHubCampaignFailureKind
{
    Transport, Corrupt, Incompatible, MissingRef, RefChanged, Ownership,
    Ambiguous, MultipleActive, MissingPullRequest, StaleLedger, TargetMoved, Bounds,
}

// Projections contain exact identities and closed facts, never bodies or mutation capabilities.
internal sealed record GitHubCampaignFailure(GitHubCampaignFailureKind Kind,
    GitHubFailure? Transport = null, string? Ref = null, int? PullRequestNumber = null) : GitHubValue;

internal sealed record GitHubCampaignLedger(string HeadOid, GitHubCoordinationStage Stage,
    string OriginalBaseOid, string GenerationId, string SnapshotCommitmentSha256,
    string PolicyCommitmentSha256, string OperationId, string OperationCommitmentSha256,
    string CandidateCommitmentSha256, string? ProposalRef, int? PullRequestNumber) : GitHubValue;

internal sealed record GitHubCampaignPullRequest(long Id, string NodeId, int Number,
    GitHubCampaignPullRequestKind Kind, GitHubPullRequestHead Head,
    GitHubRepositoryIdentity BaseRepository, string BaseRef, string BaseOid,
    GitHubActor? Author, DateTimeOffset CreatedAt, DateTimeOffset? ClosedAt,
    DateTimeOffset? MergedAt, bool OwnershipVerified) : GitHubValue;

internal sealed record GitHubCampaignObservation(GitHubCampaignKind Kind,
    GitHubCampaignLedgerKind LedgerKind, GitHubRepositoryIdentity? Repository,
    string TargetRef, string? TargetOid, string CoordinationRef, string? CoordinationOid,
    GitHubCampaignLedger? Ledger, ImmutableArray<GitHubCampaignPullRequest> PullRequests,
    bool CollectionExhausted, int CollectionPages, GitHubCampaignFailure? Failure = null) : GitHubValue;

// Derived only during the already bounded complete history proof; not exported in the observation.
internal sealed record GitHubCampaignGeneration(string Ref, string HeadOid, string TreeOid,
    string OriginalBaseOid, string ObservedBaseOid, int? Number, GitHubCoordinationStage Stage,
    GitHubProposalMetadata Metadata) : GitHubValue;
