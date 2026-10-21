using ContractScribe.Core;
using ContractScribe.GitHub.GitData;
using ContractScribe.GitHub.PullRequests;
using System.Text.Json;

namespace ContractScribe.GitHub.Coordination;

internal sealed partial class GitHubCoordinationStore
{
    private async ValueTask<bool> PublicationAllowsCheckpointAsync(RemoteCheckpoint remote,
        CampaignCheckpointArtifact next, CancellationToken token)
    {
        if (remote.Document.Publication is not { } pointer) return true;
        // ReadRemoteCheckpointAsync has already authenticated this immutable event
        // and its actual containing tree, parents, original checkpoint and plan.
        var blob = await ReadBlobAsync(pointer.Oid, token).ConfigureAwait(false);
        var intent = GitHubPublicationEvent.Decode(blob.Bytes.AsMemory()).State;
        if (next.State.Batch.Identity != intent.BatchIdentity)
        {
            if (intent.Stage is not (GitHubCoordinationStage.Published or GitHubCoordinationStage.AwaitingReview
                or GitHubCoordinationStage.StaleDraft or GitHubCoordinationStage.Merged
                or GitHubCoordinationStage.ClosedUnmerged or GitHubCoordinationStage.Stale)) return false;
            var observed = await GitHubCampaignObserver.ObserveCheckpointAsync(client, this, token).ConfigureAwait(false);
            return observed.Failure is null && observed.CollectionExhausted
                && !observed.PullRequests.Any(pr => pr.Kind is GitHubCampaignPullRequestKind.OpenDraft or GitHubCampaignPullRequestKind.OpenReady)
                && (intent.Stage == GitHubCoordinationStage.Stale
                    ? !observed.PullRequests.Any(pr => pr.GenerationId == intent.GenerationId)
                    : observed.PullRequests.Any(pr => pr.OwnershipVerified && pr.GenerationId == intent.GenerationId
                    && (pr.Kind == GitHubCampaignPullRequestKind.Merged
                        || pr.Kind == GitHubCampaignPullRequestKind.ClosedUnmerged && pr.OriginalBaseOid != observed.TargetOid)))
                && !observed.PullRequests.Any(pr => pr.Kind == GitHubCampaignPullRequestKind.ClosedUnmerged
                    && pr.OriginalBaseOid == observed.TargetOid)
                && remote.Artifact is { } before
                && (next.State.Batch.Identity == before.State.Batch.Identity
                    ? next.State.Snapshot == before.State.Snapshot
                        && JsonElement.DeepEquals(JsonSerializer.SerializeToElement(next.State.Predecessor),
                            JsonSerializer.SerializeToElement(before.State.Predecessor))
                    : next.State.Predecessor?.FinalCheckpointSha256 == before.Sha256
                        && next.State.Snapshot != before.State.Snapshot);
        }
        return FrozenCheckpointMatches(intent, next);
    }

    private static bool FrozenCheckpointMatches(GitHubCoordinationState intent, CampaignCheckpointArtifact next)
    {
        if (next.State.Batch.Identity != intent.BatchIdentity
            || next.State.ActiveReservation is CampaignProviderReservation
            || next.State.CandidateObservation is not { } candidate
            || !GitHubPublicationFactory.MatchesFrozenResult(next.State, intent.BatchIdentity,
                intent.ResultSetCommitmentSha256, intent.CurrentCandidateCommitmentSha256)) return false;
        return candidate.ChangedFiles.Length == intent.CumulativeChangedFiles.Length
            && candidate.ChangedFiles.Zip(intent.CumulativeChangedFiles)
                .All(pair => pair.First.Path == pair.Second.Path
                    && pair.First.CandidateFileSha256 == pair.Second.CandidateSha256);
    }

    // Only fresh, store-owned content and exhaustive absence proofs can restore
    // the next ref step. The current authority must still be the frozen operation.
    internal async ValueTask<GitHubCoordinationResult> RecoverOriginalProposalRefAsync(
        IGitHubCoordinationStateCapability state, IGitHubInspectedProposal content,
        IGitHubProposalAbsence absence, CancellationToken token)
    {
        if (state is not StateCapability current || !ReferenceEquals(current.Owner, this)
            || current.Stage != GitHubCoordinationStage.ContentCreated || !MatchesAuthority(current.State)
            || !GitHubProposalStore.Authenticates(content, this, current) || !content.ContentExists
            || content.CommitOid != current.ContentCommitOid || content.BeforeOid != ZeroOid
            || content.ObservedRefOid != ZeroOid
            || !GitHubProposalPullRequestStore.AuthenticatesAbsence(absence, this, current))
            return DomainFailure(GitHubCoordinationFailureKind.InvalidInput);
        var guard = await ReadClaimAsync(current, token).ConfigureAwait(false);
        return guard.State is not StateCapability fresh || guard.Guard is null ? guard
            : new(GitHubCoordinationOutcome.Guarded, state: fresh,
                proposalRefEntitlement: new ProposalRefEntitlement(this, fresh, absence));
    }

    // This durable fence is the only issuer of the non-idempotent send right.
    // Replay, restart and lost CAS acknowledgement may discover it, never own it.
    internal async ValueTask<GitHubCoordinationResult> BeginOriginalPullRequestAsync(
        IGitHubCoordinationStateCapability state, IGitHubInspectedProposal content,
        IGitHubProposalAbsence absence, CancellationToken token)
    {
        if (state is not StateCapability current || !ReferenceEquals(current.Owner, this)
            || current.Stage != GitHubCoordinationStage.ProposalRefAdvanced || !MatchesAuthority(current.State)
            || !GitHubProposalStore.Authenticates(content, this, current) || !content.ContentExists
            || content.CommitOid != current.ProposalCommitOid || content.TreeOid != current.ProposalTreeOid
            || content.ObservedRefOid != current.ProposalCommitOid
            || !GitHubProposalPullRequestStore.AuthenticatesAbsence(absence, this, current))
            return DomainFailure(GitHubCoordinationFailureKind.InvalidInput);
        var guard = await ReadClaimAsync(current, token).ConfigureAwait(false);
        if (guard.State is not StateCapability fresh || guard.Guard is null) return guard;
        var creation = GitHubCoordinationCodec.PullRequestCreationCommitment(fresh.State, content.Ref);
        return await AdvanceCoreAsync(fresh, GitHubCoordinationStageUpdate.PullRequestCreateStarted(
            content.CommitOid, content.TreeOid, creation, fresh.TargetCommitOid,
            GitHubCoordinationCodec.MarkerHash(creation)), token, checkTarget: true, requireExpectedTarget: true,
            mutationGuard: guardToken => GitHubProposalPullRequestStore.RevalidateAbsenceAsync(
                absence, this, fresh, guardToken)).ConfigureAwait(false);
    }
}
