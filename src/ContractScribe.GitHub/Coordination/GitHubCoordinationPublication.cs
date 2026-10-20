using ContractScribe.GitHub.Transport;

namespace ContractScribe.GitHub.Coordination;

internal sealed partial class GitHubCoordinationStore
{
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
            var proposalRef = ProposalRef(current.State);
            var update = await client.UpdatePublicationRefsAsync(
                new(coordinationRef, current.HeadOid, prepared.CommitOid, false),
                new(proposalRef, beforeProposalOid, contentOid, beforeProposalOid == ZeroOid), token).ConfigureAwait(false);
            if (update.Delivery == GitHubDelivery.NotDispatched) return Failed(update);
            using var recovery = new CancellationTokenSource(RecoveryTimeout);
            var head = await client.GetRefAsync(coordinationRef, recovery.Token).ConfigureAwait(false);
            var proposal = await client.GetRefAsync(proposalRef, recovery.Token).ConfigureAwait(false);
            if (head.Value is null || proposal.Value is null)
                return DomainFailure(GitHubCoordinationFailureKind.Transport, update.Failure, update.Delivery,
                    update.Context, update.RequiredPermissions, head.Value is null ? head.Failure : proposal.Failure);
            if (head.Value.Oid != prepared.CommitOid || proposal.Value.Oid != contentOid)
                return DomainFailure(head.Value.Oid == prepared.CommitOid ? GitHubCoordinationFailureKind.ObjectMismatch
                    : GitHubCoordinationFailureKind.Conflict, update.Failure, update.Delivery, update.Context, update.RequiredPermissions);
            if (publicationActive) publicationHead = head.Value.Oid;
            var read = await ReadPreparedAsync(current.Repository, current.Target, prepared, recovery.Token).ConfigureAwait(false);
            if (read.State is not StateCapability accepted) return read;
            var acknowledged = update.Value is not null && update.Failure is null && update.Delivery == GitHubDelivery.NeedsReadback;
            return new(GitHubCoordinationOutcome.Advanced, state: accepted,
                pullRequestEntitlement: acknowledged && next.Transition != "same-snapshot-append"
                    ? new PullRequestEntitlement(this, accepted) : null, publicationMutation: update);
        }
        catch (GitHubCoordinationException) { return DomainFailure(GitHubCoordinationFailureKind.ObjectMismatch); }
        catch (OperationCanceledException) { return DomainFailure(GitHubCoordinationFailureKind.Transport, new(GitHubFailureCode.Cancelled)); }
    }
}
