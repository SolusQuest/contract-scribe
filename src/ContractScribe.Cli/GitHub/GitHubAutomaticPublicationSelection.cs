using System.Security.Cryptography;
using System.Text.Json;
using ContractScribe.Core;
using ContractScribe.GitHub.Coordination;

namespace ContractScribe.Cli;

// Host composition over authenticated observations. Neither caller configuration
// nor a runner identity can choose a publication operation or replace its fence.
internal sealed class GitHubAutomaticPublicationSelection(
    GitHubCampaignConfiguration campaign, GitHubCampaignObservation observed)
{
    internal GitHubPublicationConfiguration ForState(CampaignCheckpointState state)
    {
        if (observed.Failure is not null || !observed.CollectionExhausted || observed.TargetOid is null
            || state.CampaignLineage != campaign.CampaignLineage)
            throw new ArgumentException("Invalid automatic publication observation.");
        var ledger = observed.Ledger;
        var original = ledger?.BatchIdentity == state.Batch.Identity;
        var snapshot = GitHubPublicationRequestFactory.CreateSnapshotCommitment(state.Snapshot);
        if (original && ledger!.SnapshotCommitmentSha256 != snapshot)
            throw new ArgumentException("The original publication snapshot does not match.");
        var predecessor = observed.PullRequests
            .Where(pr => pr.OwnershipVerified
                && pr.Kind is GitHubCampaignPullRequestKind.Merged or GitHubCampaignPullRequestKind.ClosedUnmerged
                && (!original || pr.GenerationId != ledger!.GenerationId))
            .OrderByDescending(pr => pr.Number).FirstOrDefault();
        // A stale operation without a PR uses the existing initial-after-stale
        // transition, even when older terminal generations remain in history.
        if (!original && ledger?.Stage == GitHubCoordinationStage.Stale) predecessor = null;
        var transition = original ? ledger!.Transition switch
        {
            "initial" => GitHubPublicationTransitionKind.Initial,
            "successor-after-merge" => GitHubPublicationTransitionKind.SuccessorAfterMerge,
            "successor-after-closed-unmerged" => GitHubPublicationTransitionKind.SuccessorAfterClosedUnmerged,
            _ => throw new ArgumentException("Invalid original publication transition."),
        } : predecessor?.Kind switch
        {
            GitHubCampaignPullRequestKind.Merged => GitHubPublicationTransitionKind.SuccessorAfterMerge,
            GitHubCampaignPullRequestKind.ClosedUnmerged => GitHubPublicationTransitionKind.SuccessorAfterClosedUnmerged,
            _ => GitHubPublicationTransitionKind.Initial,
        };
        if (transition == GitHubPublicationTransitionKind.Initial) predecessor = null;
        if (transition != GitHubPublicationTransitionKind.Initial && predecessor is null
            || !original && ledger is not null && ledger.Stage != GitHubCoordinationStage.Stale
                && predecessor?.GenerationId != ledger.GenerationId)
            throw new ArgumentException("Missing authenticated publication predecessor.");
        var seed = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            domain = "github-automatic-publication-v1",
            campaign,
            snapshot,
            batch = state.Batch.Identity,
        })));
        return new(campaign.GitHub.RepositoryOwner, campaign.GitHub.RepositoryName, campaign.GitHub.TargetRef,
            observed.TargetOid, original ? ledger!.OperationId : "operation." + seed,
            original ? ledger!.GenerationId : "generation." + seed, transition,
            predecessor is null ? null : new("predecessor." + predecessor.GenerationId,
                predecessor.Number, predecessor.GenerationId!, predecessor.Head.Oid
                    ?? throw new ArgumentException("Missing authenticated predecessor head."),
                predecessor.Kind == GitHubCampaignPullRequestKind.Merged
                    ? GitHubPublicationPredecessorDisposition.Merged : GitHubPublicationPredecessorDisposition.ClosedUnmerged));
    }
}
