using ContractScribe.GitHub.Transport;
using ContractScribe.GitHub.PullRequests;

namespace ContractScribe.GitHub.Coordination;

internal sealed partial class GitHubCoordinationStore
{
    // Finish only the already-created PR of the current recorded operation.
    // Re-observe inside the owned checkpoint claim; no caller projection,
    // candidate, publication authority or PR-create entitlement authorizes this CAS.
    internal async ValueTask<GitHubCampaignObservation> RecoverObservedPublicationAsync(CancellationToken token)
    {
        await checkpointGate.WaitAsync(token).ConfigureAwait(false);
        GitHubCampaignObservation? observed = null;
        try
        {
            observed = await GitHubCampaignObserver.ObserveCheckpointAsync(client, this, token).ConfigureAwait(false);
            if (observed.Failure is not null) return observed;
            var remote = await ReadRemoteCheckpointAsync(token).ConfigureAwait(false);
            if (remote is null || !Owns(remote.Document) || remote.Head != observed.CoordinationOid
                || observed.Repository is null || observed.TargetOid is null)
                throw new GitHubCoordinationException();
            var target = GitHubObservedProposalIntegrity.Read(await client.GetRefAsync(client.ReadAuthority.TargetRef, token).ConfigureAwait(false));
            if (target?.Oid != observed.TargetOid) throw new GitHubCoordinationException();
            var read = await ReadObservedStateAsync(observed.Repository, target, remote.Head, token).ConfigureAwait(false);
            if (read.State is not StateCapability current)
                throw new GitHubCoordinationException(transportFailure: read.Failure?.TransportFailure);
            if (current.Stage is not (GitHubCoordinationStage.PullRequestCreateStarted or GitHubCoordinationStage.PullRequestCreated)) return observed;
            var generation = ObservedCurrentGeneration(current) ?? throw new GitHubCoordinationException();
            var pr = observed.PullRequests.SingleOrDefault(row => row.OwnershipVerified
                && row.GenerationId == generation.GenerationId && "refs/heads/" + row.Head.Ref == generation.Ref)
                ?? throw new GitHubCoordinationException();
            var stage = pr.Kind switch
            {
                GitHubCampaignPullRequestKind.OpenDraft => GitHubCoordinationStage.Published,
                GitHubCampaignPullRequestKind.OpenReady => GitHubCoordinationStage.AwaitingReview,
                GitHubCampaignPullRequestKind.Merged => GitHubCoordinationStage.Merged,
                GitHubCampaignPullRequestKind.ClosedUnmerged => GitHubCoordinationStage.ClosedUnmerged,
                _ => throw new GitHubCoordinationException(),
            };
            var update = GitHubCoordinationStageUpdate.PullRequestResult(stage, generation.HeadOid,
                generation.TreeOid, generation.Metadata.CreationCommitment, pr.Number,
                current.TargetCommitOid, pr.BaseOid, generation.Metadata.MarkerHash);
            var next = Apply(current.State, update, current.HeadOid);
            ValidateSuccessor(current.State, next);
            var prepared = Prepare(next, current.Creation?.State ?? current.State);
            if (await WriteDocumentAsync(prepared.Document, [], token,
                livePredecessor: remote.Document, publication: prepared.Event,
                attempt: BeginCheckpointOperation(GitHubCheckpointOperation.Publication)).ConfigureAwait(false) is { } failed)
            {
                StopCheckpoint();
                throw new GitHubCoordinationException(failure: failed.Failure);
            }
            return await GitHubCampaignObserver.ObserveCheckpointAsync(client, this, token).ConfigureAwait(false);
        }
        catch (GitHubCoordinationException exception) when (observed is not null)
        {
            return observed with
            {
                Kind = GitHubCampaignKind.Unverifiable,
                Failure = exception.TransportFailure is { } failure
                ? new(GitHubCampaignFailureKind.Transport, failure) : new(GitHubCampaignFailureKind.StaleLedger)
            };
        }
        finally { checkpointGate.Release(); }
    }

    internal async ValueTask<GitHubCoordinationResult> AdvanceProposalRefsAsync(
        IGitHubCoordinationStateCapability state, IGitHubProposalRefEntitlement entitlement,
        string contentOid, string treeOid, string beforeProposalOid, CancellationToken token)
    {
        if (state is not StateCapability current || !ReferenceEquals(current.Owner, this)
            || entitlement is not ProposalRefEntitlement right || !ReferenceEquals(right.Owner, this)
            || !SameState(right.State, current) || right.BeforeOid != beforeProposalOid
            || current.Stage != GitHubCoordinationStage.ContentCreated || current.ContentCommitOid != contentOid
            || Interlocked.CompareExchange(ref right.Consumed, 2, 1) != 1)
            return DomainFailure(GitHubCoordinationFailureKind.InvalidInput);
        try
        {
            var guard = await ReadClaimAsync(current, token).ConfigureAwait(false);
            if (guard.Guard is null) return guard;
            var next = Apply(current.State, GitHubCoordinationStageUpdate.ProposalRefAdvanced(contentOid, treeOid), current.HeadOid);
            ValidateSuccessor(current.State, next);
            var prepared = Prepare(next, current.Creation?.State ?? current.State);
            var created = await CreateObjectsAsync(prepared, token).ConfigureAwait(false);
            if (created is not null) return created;
            // Keep the prepared physical parent stable through the paired CAS.
            guard = await ReadClaimAsync(current, token, renewClaim: false).ConfigureAwait(false);
            if (guard.Guard is null) return guard;
            if (right.Absence is not null && await GitHubProposalPullRequestStore.RevalidateAbsenceAsync(
                right.Absence, this, current, token).ConfigureAwait(false) is { } absenceFailure)
                return new(GitHubCoordinationOutcome.Failed, failure: absenceFailure);
            var proposalRef = ProposalRef(current.State);
            var update = await client.UpdatePublicationRefsAsync(
                new(coordinationRef, current.HeadOid, prepared.CommitOid, false),
                new(proposalRef, beforeProposalOid, contentOid, beforeProposalOid == ZeroOid), token).ConfigureAwait(false);
            if (update.Delivery == GitHubDelivery.NotDispatched) return Failed(update);
            using var recovery = client.CreateRecoveryScope(RecoveryTimeout);
            GitHubRef? head, proposal;
            GitHubFailure? readFailure;
            if (nativeCoordination is not null)
            {
                var observed = await client.ReadPublicationRefsAsync(recovery.Token).ConfigureAwait(false);
                head = observed.Value?.Coordination;
                proposal = observed.Value?.Proposal;
                readFailure = observed.Failure;
            }
            else
            {
                var headResult = await client.GetRefAsync(coordinationRef, recovery.Token).ConfigureAwait(false);
                var proposalResult = await client.GetRefAsync(proposalRef, recovery.Token).ConfigureAwait(false);
                head = headResult.Value;
                proposal = proposalResult.Value;
                readFailure = head is null ? headResult.Failure : proposalResult.Failure;
            }
            if (head is null || proposal is null)
                return DomainFailure(GitHubCoordinationFailureKind.Transport, update.Failure, update.Delivery,
                    update.Context, update.RequiredPermissions, readFailure);
            if (head.Oid != prepared.CommitOid || proposal.Oid != contentOid)
                return DomainFailure(head.Oid == prepared.CommitOid ? GitHubCoordinationFailureKind.ObjectMismatch
                    : GitHubCoordinationFailureKind.Conflict, update.Failure, update.Delivery, update.Context, update.RequiredPermissions);
            if (publicationActive) publicationHead = head.Oid;
            var read = await ReadPreparedAsync(current.Repository, current.Target, prepared, recovery.Token).ConfigureAwait(false);
            if (read.State is not StateCapability accepted) return read;
            var acknowledged = update.Value is not null && update.Failure is null && update.Delivery == GitHubDelivery.NeedsReadback;
            return new(GitHubCoordinationOutcome.Advanced, state: accepted,
                publicationMutation: update);
        }
        catch (GitHubCoordinationException) { return DomainFailure(GitHubCoordinationFailureKind.ObjectMismatch); }
        catch (OperationCanceledException) { return DomainFailure(GitHubCoordinationFailureKind.Transport, client.CancellationFailure); }
    }
}
