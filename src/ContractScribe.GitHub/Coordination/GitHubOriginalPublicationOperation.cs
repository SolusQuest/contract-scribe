using ContractScribe.Core;
using ContractScribe.GitHub.GitData;
using ContractScribe.GitHub.PullRequests;
using System.Text.Json;

namespace ContractScribe.GitHub.Coordination;

internal sealed partial class GitHubCoordinationStore
{
    private async ValueTask<bool> PublicationAllowsCheckpointAsync(RemoteCheckpoint remote,
        CampaignCheckpointArtifact next, ReadProof proof, CancellationToken token)
    {
        if (remote.Document.Publication is not { } pointer)
        {
            if (remote.Artifact is { } before && !RequiresCheckpointAdmission(before.State, next.State)) return true;
            var first = await GitHubCampaignObserver.ObserveCheckpointAsync(client, this, token).ConfigureAwait(false);
            return AllowsCheckpointWork(first, remote);
        }
        // ReadRemoteCheckpointAsync has already authenticated this immutable event
        // and its actual containing tree, parents, original checkpoint and plan.
        var blob = await ReadBlobAsync(pointer.Oid, token, proof, publicationPayload: true).ConfigureAwait(false);
        var intent = GitHubPublicationEvent.Decode(blob.Bytes.AsMemory()).State;
        if (next.State.Batch.Identity != intent.BatchIdentity)
        {
            if (intent.Stage is not (GitHubCoordinationStage.Published or GitHubCoordinationStage.AwaitingReview
                or GitHubCoordinationStage.StaleDraft or GitHubCoordinationStage.Merged
                or GitHubCoordinationStage.ClosedUnmerged or GitHubCoordinationStage.Stale)) return false;
            // Completing, settling and acknowledging already admitted work is
            // not another batch admission. Its exact predecessor, retained
            // history, owner, lease and actual successor are checked by the
            // caller. Never reuse this shortcut to start a new operation.
            if (remote.Artifact is { } admitted && !RequiresCheckpointAdmission(admitted.State, next.State))
                return true;
            var observed = await GitHubCampaignObserver.ObserveCheckpointAsync(client, this, token).ConfigureAwait(false);
            return AllowsCheckpointWork(observed, remote)
                && (intent.Stage == GitHubCoordinationStage.Stale
                    ? !observed.PullRequests.Any(pr => pr.GenerationId == intent.GenerationId)
                    : observed.PullRequests.Any(pr => pr.OwnershipVerified && pr.GenerationId == intent.GenerationId
                    && (pr.Kind == GitHubCampaignPullRequestKind.Merged
                        || pr.Kind == GitHubCampaignPullRequestKind.ClosedUnmerged && pr.OriginalBaseOid != observed.TargetOid)))
                && remote.Artifact is { } before
                && (next.State.Batch.Identity == before.State.Batch.Identity
                    ? next.State.Snapshot == before.State.Snapshot
                        && JsonElement.DeepEquals(JsonSerializer.SerializeToElement(next.State.Predecessor),
                            JsonSerializer.SerializeToElement(before.State.Predecessor))
                    : CampaignStateFactory.IsNoResultBatchSuccessor(before, next)
                        || next.State.Predecessor?.FinalCheckpointSha256 == before.Sha256
                            && next.State.Snapshot != before.State.Snapshot);
        }
        return FrozenCheckpointMatches(intent, next);
    }

    private bool AllowsCheckpointWork(GitHubCampaignObservation observed, RemoteCheckpoint remote) =>
        observed.Failure is null && observed.CollectionExhausted
        && observed.Repository == client.AuthenticatedRepository
        && observed.TargetOid == client.CheckpointAuthority!.ExpectedBaseOid
        && observed.CoordinationOid == remote.Head
        && !observed.PullRequests.Any(pr => pr.Kind is GitHubCampaignPullRequestKind.OpenDraft or GitHubCampaignPullRequestKind.OpenReady)
        && !observed.PullRequests.Any(pr => pr.Kind == GitHubCampaignPullRequestKind.ClosedUnmerged
            && pr.OriginalBaseOid == observed.TargetOid);

    private static bool RequiresCheckpointAdmission(CampaignCheckpointState before, CampaignCheckpointState next)
    {
        if (before.Batch.Identity != next.Batch.Identity || before.Snapshot != next.Snapshot) return true;
        // Host preflight, local tools, settlement and deterministic patching
        // consume already admitted work. A physical provider dispatch is a new
        // external effect and must recheck the complete mutable admission facts.
        if (next.ActiveReservation is CampaignProviderReservation { OperationKind: CampaignProviderOperationKind.Dispatch } provider)
            return before.ActiveReservation is not CampaignProviderReservation prior
                || provider.WorkItemKey != prior.WorkItemKey || provider.AttemptId != prior.AttemptId
                || provider.ScribeRequestSha256 != prior.ScribeRequestSha256
                || provider.ExecutionStartRevision != prior.ExecutionStartRevision
                || provider.CurrentOperationOrdinal != prior.CurrentOperationOrdinal
                || provider.LastDispatchOrdinal != prior.LastDispatchOrdinal
                || provider.OperationKind != prior.OperationKind
                || provider.RequestCommitmentSha256 != prior.RequestCommitmentSha256;
        return false;
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
