using ContractScribe.GitHub.NativeGit;
using ContractScribe.GitHub.Transport;

namespace ContractScribe.GitHub.Coordination;

internal sealed partial class GitHubCoordinationStore
{
    // A retained platform sample, never a native observation or a cached right.
    // True platform-sensitive effects below still read actual ref identities.
    private GitHubRef? nativePublicationTargetIdentity;
    internal bool UsesNativeData => nativeCoordination is not null;

    internal async ValueTask<Dictionary<string, GitHubTreeEntry>?> ReadSourceFilesAsync(string commitOid, CancellationToken token)
    {
        string tree;
        if (nativeCoordination is not null)
            tree = await nativeCoordination.AcquireObservedBaseTreeAsync(commitOid, token).ConfigureAwait(false);
        else
        {
            var commit = GitHubObservedProposalIntegrity.Read(await client.GetCommitAsync(commitOid, token).ConfigureAwait(false));
            if (commit is null || commit.Oid != commitOid) return null;
            tree = commit.TreeOid;
        }
        return await GitHubObservedProposalIntegrity.ReadTreeAsync(tree, token, async (oid, ct) =>
            GitHubObservedProposalIntegrity.Read(await ReadSourceTreeAsync(oid, ct).ConfigureAwait(false))).ConfigureAwait(false);
    }

    internal async ValueTask<GitHubApiResult<GitHubDataRef>> ReadDataRefAsync(string name, CancellationToken token)
    {
        if (nativeCoordination is null)
        {
            var read = await client.GetRefAsync(name, token).ConfigureAwait(false);
            return new(read.Value is { } value ? new(value.Name, value.Oid) : null,
                read.Failure, read.Delivery, read.Context, read.RequiredPermissions);
        }
        try
        {
            var head = await nativeCoordination.ReadDataHeadAsync(name, token).ConfigureAwait(false);
            return head is null ? GitHubApiResult<GitHubDataRef>.Failed(GitHubFailureCode.NotFound, dispatched: true)
                : new(new(name, head), null, GitHubDelivery.Read);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (GitHubProtocolException exception) { return GitHubApiResult<GitHubDataRef>.Failed(exception.Code, dispatched: true); }
    }

    internal async ValueTask<GitHubApiResult<GitHubObjectIdentity>> ReadBaseTreeAsync(CancellationToken token)
    {
        var oid = authority.ExpectedBaseCommitOid;
        if (nativeCoordination is not null
            && await nativeCoordination.AcquireObservedBaseTreeAsync(oid, token).ConfigureAwait(false) is { } tree)
            return new(new(tree), null, GitHubDelivery.Read);
        var read = await client.GetCommitAsync(oid, token).ConfigureAwait(false);
        return new(read.Value is { } value ? new(value.TreeOid) : null, read.Failure, read.Delivery, read.Context, read.RequiredPermissions);
    }

    private async ValueTask<GitHubCoordinationResult> ReadNativePublicationAsync(CancellationToken token)
    {
        // This is only a data read under the already admitted client/lease.
        // Its Git request rechecks Contents access; it does not renew identity,
        // time or PR authority. Actual effect/PR/renewal gates sample HTTPS.
        var repository = client.AuthenticatedRepository ?? throw new GitHubCoordinationException();
        var targetHead = await nativeCoordination!.ReadHeadAsync(true, token).ConfigureAwait(false);
        if (nativePublicationTargetIdentity is null || targetHead != nativePublicationTargetIdentity.Oid)
            nativePublicationTargetIdentity = RequireRemote(await client.GetRefAsync(authority.TargetRef, token).ConfigureAwait(false));
        var target = nativePublicationTargetIdentity;
        if (targetHead != target.Oid) return DomainFailure(GitHubCoordinationFailureKind.TargetMoved);
        var native = await nativeCoordination.ObserveAsync(token).ConfigureAwait(false);
        if (native is null) return DomainFailure(GitHubCoordinationFailureKind.MissingPredecessor);
        if (native.Commit is null) throw new GitHubCoordinationException();
        using var proof = PublicationProof(repository, native.HeadOid);
        _ = await AcceptNativeCheckpointAsync(native, repository, proof, token, verifyTarget: false).ConfigureAwait(false);
        var state = await ReadStateAsync(repository, target, native.HeadOid, token, proof: proof).ConfigureAwait(false);
        if (state.Failure is not null) return state;
        if (await nativeCoordination.ReadHeadAsync(false, token).ConfigureAwait(false) != native.HeadOid)
            return DomainFailure(GitHubCoordinationFailureKind.Conflict);
        token.ThrowIfCancellationRequested();
        var capability = state.State as StateCapability;
        if (!checkpointStopped && capability is not null) proof.Promote(token);
        return new(GitHubCoordinationOutcome.Current,
            read: new ReadCapability(this, repository, target, capability, capability is null ? native.HeadOid : null), state: capability);
    }

    private async ValueTask<GitHubCoordinationResult> UpdateNativePublicationAsync(GitHubRepositoryIdentity repository,
        GitHubRef target, GitHubPreparedCoordination prepared, string beforeOid, CancellationToken token)
    {
        if (!publicationActive || prepared.Event is null || prepared.Document.PredecessorOid != beforeOid
            || !documents.TryGetValue(beforeOid, out var predecessor) || !Owns(predecessor)
            || prepared.Document.Claim != predecessor.Claim)
            return DomainFailure(GitHubCoordinationFailureKind.Conflict);
        if (prepared.State.Stage == GitHubCoordinationStage.PullRequestCreateStarted)
        {
            var gate = RequireRemote(await client.ReadPublicationGateAsync(token).ConfigureAwait(false));
            if (gate.Repository != repository)
                return DomainFailure(GitHubCoordinationFailureKind.HumanChange);
            // The non-idempotent send fence retains real platform identities.
            if (gate.Coordination?.Oid != beforeOid || gate.Coordination.NodeId != client.CheckpointRefNodeId || gate.Target != target)
                return DomainFailure(GitHubCoordinationFailureKind.Conflict);
        }
        if (await nativeCoordination!.ReadHeadAsync(true, token).ConfigureAwait(false) != target.Oid)
            return DomainFailure(GitHubCoordinationFailureKind.TargetMoved);
        var write = new GitHubCheckpointWrite(prepared, [Blob(prepared.Event.Encode())]);
        var result = await nativeCoordination.AdvanceAsync(write, token, () =>
        {
            RequirePendingCheckpointLease(predecessor, token, null);
        }).ConfigureAwait(false);
        if (result.Outcome != GitHubGitAdvance.ActualUpdated || result.Observation is not { Commit: not null } actual)
            return DomainFailure(GitHubCoordinationFailureKind.Conflict);
        using var proof = PublicationProof(repository, actual.HeadOid, effect: true);
        _ = await AcceptNativeCheckpointAsync(actual, repository, proof, token, verifyTarget: false).ConfigureAwait(false);
        var read = await ReadStateAsync(repository, target, actual.HeadOid, token, proof: proof).ConfigureAwait(false);
        if (read.State is not StateCapability state || actual.Document != prepared.Document
            || await nativeCoordination.ReadHeadAsync(false, token).ConfigureAwait(false) != actual.HeadOid)
            return DomainFailure(GitHubCoordinationFailureKind.ObjectMismatch);
        RequirePendingCheckpointLease(predecessor, token, null);
        publicationHead = actual.HeadOid;
        proof.Promote(token);
        // This typed native result describes an acknowledged actual CAS. Never
        // manufacture an API ACK, nor grant these rights on an observed replay.
        return new(GitHubCoordinationOutcome.Advanced, state: state,
            proposalRefEntitlement: state.Stage == GitHubCoordinationStage.ContentCreated ? new ProposalRefEntitlement(this, state) : null,
            pullRequestEntitlement: state.Stage == GitHubCoordinationStage.PullRequestCreateStarted ? new PullRequestEntitlement(this, state) : null);
    }

    private GitHubCoordinationDocument StageNativeObservation(GitHubGitCheckpointObservation native,
        ReadProof? proof, CancellationToken token)
    {
        if (native.Commit is not { } commit) throw new GitHubCoordinationException();
        var document = AuthenticateDocument(commit, native.Root, native.Leaf,
            native.Blobs[GitHubCoordinationObjects.StatePath]);
        if (proof is not null)
        {
            proof.Documents[native.HeadOid] = document;
            proof.Stage(commit, token);
            proof.Stage(native.Root, token);
            proof.Stage(native.Leaf, token);
            foreach (var blob in native.Blobs.Values)
            {
                proof.Blobs[blob.Oid] = blob;
                proof.PooledBlobs.Remove(blob.Oid);
                proof.Stage(blob, token);
            }
        }
        documents[native.HeadOid] = document;
        return document;
    }

    private async ValueTask<RemoteCheckpoint> AcceptNativeCheckpointAsync(GitHubGitCheckpointObservation native,
        GitHubRepositoryIdentity repository, ReadProof proof, CancellationToken token, bool verifyTarget)
    {
        proof.CurrentHead = native.HeadOid;
        StageNativeObservation(native, proof, token);
        var document = await ReadAcceptedDocumentAsync(native.HeadOid, token, proof).ConfigureAwait(false);
        if (verifyTarget && await nativeCoordination!.ReadHeadAsync(true, token).ConfigureAwait(false)
            != client.CheckpointAuthority!.ExpectedBaseOid) throw new GitHubCoordinationException();
        // No API Ref identity or principal metadata is synthesised here.
        return (await ReadCheckpointContentAsync(repository, native.HeadOid, document, token, proof).ConfigureAwait(false))
            with
        { Commit = native.Commit };
    }

    private async ValueTask<GitHubCoordinationResult?> WriteNativeCheckpointAsync(GitHubCheckpointWrite write,
        bool requireLiveClaim, GitHubCoordinationDocument? livePredecessor, CancellationToken token,
        CancellationToken? observationToken, CheckpointAttempt? attempt, ReadProof? transactionProof)
    {
        var document = write.Prepared.Document;
        if (attempt?.Operation == GitHubCheckpointOperation.Renew)
        {
            var identity = client.AuthenticatedRepository ?? throw new GitHubCoordinationException();
            var sampled = RequireRemote(await client.GetRepositoryAsync(token).ConfigureAwait(false));
            if (sampled.Identity != identity) throw new GitHubCoordinationException();
            // This actual HTTPS response refreshes the existing trusted Date
            // sample; no push acknowledgement or local wall clock renews it.
        }
        void ValidateOwner()
        {
            token.ThrowIfCancellationRequested();
            if (requireLiveClaim && !Owns(document) || livePredecessor is not null && !Owns(livePredecessor))
                throw new GitHubCoordinationException();
        }
        ValidateOwner();
        var result = await nativeCoordination!.AdvanceAsync(write, token, () =>
        {
            ValidateOwner();
        }).ConfigureAwait(false);
        if (result.Outcome != GitHubGitAdvance.ActualUpdated || result.Observation is not { Commit: not null } actual)
        {
            ObserveCheckpointFailure(attempt, GitHubCheckpointStage.RefMutation, GitHubCheckpointReason.RefMismatch);
            return RetainCheckpointFailure(DomainFailure(GitHubCoordinationFailureKind.Conflict));
        }
        using var observation = observationToken is { } parent
            ? CancellationTokenSource.CreateLinkedTokenSource(token, parent) : null;
        var active = observation?.Token ?? token;
        active.ThrowIfCancellationRequested();
        var repository = client.AuthenticatedRepository ?? throw new GitHubCoordinationException();
        using var ownedProof = transactionProof is null ? PublicationProof(repository, actual.HeadOid) : null;
        var proof = transactionProof ?? ownedProof!;
        var current = await AcceptNativeCheckpointAsync(actual, repository, proof, active,
            verifyTarget: attempt?.Operation == GitHubCheckpointOperation.Write).ConfigureAwait(false);
        if (current.Document != document
            || await nativeCoordination.ReadHeadAsync(false, active).ConfigureAwait(false) != actual.HeadOid)
            throw new GitHubCoordinationException();
        CompleteCheckpointObservation(current, attempt);
        if (requireLiveClaim && !ConfirmCheckpointLease(document, attempt))
            return RetainCheckpointFailure(DomainFailure(GitHubCoordinationFailureKind.Conflict));
        lastWrittenCheckpointHead = actual.HeadOid;
        lastWrittenCheckpointReadback = current;
        if (!checkpointStopped) proof.Promote(active);
        return null;
    }
}
