using System.Collections.Immutable;
using System.Text.Json;
using ContractScribe.Core;
using ContractScribe.Roslyn;

namespace ContractScribe.Cli;

// The sole protocol is the current R1 contract, including its fixed ref/marker
// derivation and closed-unmerged terminal/no-automatic-retry disposition.
internal sealed record GitHubPublicationConfiguration(
    string RepositoryOwner,
    string RepositoryName,
    string TargetRef,
    string ExpectedBaseCommitOid,
    string OperationId,
    string GenerationId,
    GitHubPublicationPolicy Policy,
    GitHubPublicationTransitionKind Transition,
    GitHubPublicationAppendPredecessor? AppendPredecessor = null,
    GitHubPublicationPredecessorAuthority? TerminalPredecessor = null,
    GitHubClosedUnmergedSuccessorAuthorization? ClosedUnmergedSuccessorAuthorization = null);

// These are caller claims. The adapter must authenticate them against remote
// state; passing local admission does not establish a published predecessor.
internal sealed record GitHubPublicationAppendPredecessor(
    string OperationId,
    string AuthorityCommitmentSha256,
    string CandidateCommitmentSha256,
    string GenerationId,
    string SnapshotCommitmentSha256,
    string PolicyCommitmentSha256,
    ImmutableArray<GitHubPrecedingChangedFileAuthority> ChangedFiles);

internal sealed record GitHubPublicationContext
{
    internal GitHubPublicationContext(
        ClassifiedRepositorySession session,
        ObservedRepositorySession observations,
        PolicyDocumentV1 acceptedPolicy,
        ImmutableArray<AuditRecordInput> acceptedAuditInputs,
        AuditDocument acceptedAuditDocument,
        CampaignPlanningInput planningInput,
        CampaignWorkPlan acceptedPlan,
        CampaignScribeExecutionCapability executionCapability,
        string styleConfigurationId,
        JsonElement styleConfigurationProjection,
        CampaignCheckpointArtifact currentCheckpoint,
        CancellationToken cancellationToken = default)
    {
        Session = session;
        Observations = observations;
        AcceptedPolicy = acceptedPolicy;
        AcceptedAuditInputs = acceptedAuditInputs;
        AcceptedAuditDocument = acceptedAuditDocument;
        PlanningInput = planningInput;
        AcceptedPlan = acceptedPlan;
        ExecutionCapability = executionCapability;
        StyleConfigurationId = styleConfigurationId;
        StyleConfigurationProjection = styleConfigurationProjection;
        CurrentCheckpoint = currentCheckpoint;
        CancellationToken = cancellationToken;
    }

    internal ClassifiedRepositorySession Session { get; init; }
    internal ObservedRepositorySession Observations { get; init; }
    internal PolicyDocumentV1 AcceptedPolicy { get; init; }
    internal ImmutableArray<AuditRecordInput> AcceptedAuditInputs { get; init; }
    internal AuditDocument AcceptedAuditDocument { get; init; }
    internal CampaignPlanningInput PlanningInput { get; init; }
    internal CampaignWorkPlan AcceptedPlan { get; init; }
    internal CampaignScribeExecutionCapability ExecutionCapability { get; init; }
    internal string StyleConfigurationId { get; init; }
    internal JsonElement StyleConfigurationProjection { get; init; }

    // The host supplies its exact current readback immediately before calling
    // this seam. H1 has no store authority and cannot discover a later store head.
    internal CampaignCheckpointArtifact CurrentCheckpoint { get; init; }
    internal CancellationToken CancellationToken { get; init; }

    public override string ToString() => nameof(GitHubPublicationContext);
}
