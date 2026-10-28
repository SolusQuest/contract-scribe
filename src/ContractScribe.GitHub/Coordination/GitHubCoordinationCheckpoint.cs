using System.Collections.Immutable;
using System.Security.Cryptography;
using ContractScribe.Core;
using ContractScribe.GitHub.Transport;

namespace ContractScribe.GitHub.Coordination;

internal sealed partial class GitHubCoordinationStore : ICampaignSemanticCheckpointStore, ICampaignRecoveryPlanStore, IAsyncDisposable
{
    private CampaignSemanticCheckpointWindow? semanticWindow;
    private RemoteCheckpoint? lastObservedCheckpoint;
    public object SemanticCheckpointIdentity => this;
    public CampaignSemanticCheckpointStoreKind SemanticCheckpointKind => CampaignSemanticCheckpointStoreKind.RemoteLeased;
    public IDisposable EnterSemanticCheckpointWindow(CampaignSemanticCheckpointWindow window)
    {
        if (Interlocked.CompareExchange(ref semanticWindow, window, null) is not null)
            throw new InvalidOperationException("campaign.semantic.window-overlap");
        return new SemanticBinding(this, window);
    }
    private sealed class SemanticBinding(GitHubCoordinationStore store, CampaignSemanticCheckpointWindow window) : IDisposable
    {
        public void Dispose() => Interlocked.CompareExchange(ref store.semanticWindow, null, window);
    }

    private void ObserveSemanticReadback(RemoteCheckpoint remote, CampaignCheckpointReadResult readback)
    {
        if (semanticWindow is not { } window || remote.Document.Claim is not { } claim
            || client.ServerTime is not { } now || !Owns(remote.Document)) return;
        var remaining = (int)Math.Clamp(Math.Floor((DateTimeOffset.FromUnixTimeSeconds(claim.ExpiresUnixSeconds)
            - now - TimeSpan.FromSeconds(1)).TotalMilliseconds), 0, int.MaxValue);
        window.ObserveRemoteReadback(this, readback, remaining, CheckpointToken,
            () => !checkpointStopped && lastObservedCheckpoint is { } latest && Owns(latest.Document)
                && (window.IsClosingObservation || latest.Artifact?.Sha256 == readback.Sha256));
    }
    private readonly SemaphoreSlim checkpointGate = new(1, 1);
    private readonly Dictionary<string, GitHubCoordinationDocument> documents = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource checkpointLifetime = new();
    private readonly CancellationTokenSource checkpointExpiry = new();
    private readonly CancellationTokenRegistration checkpointExpiryRegistration;
    private string? invocationOwner;
    private long invocationEpoch;
    private volatile bool checkpointStopped;
    private Task? renewal;
    private CampaignRecoveryPlan? boundPlan;
    private readonly HashSet<GitHubEventPointer> verifiedCheckpointPublications = [];
    private const int ClaimSeconds = 300;

    internal CancellationToken CheckpointToken => checkpointLifetime.Token;
    internal GitHubFailure? CheckpointFailure { get; private set; }

    // Independent proof-local validation; only a publication proof may stage
    // authenticated payloads for a later gate in that same callback.
    private sealed class ReadProof(PublicationPayloadPool? pool = null, string? head = null, bool effect = false) : IDisposable
    {
        internal Dictionary<string, GitHubCoordinationDocument> Documents { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, GitHubBlob> Blobs { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> PooledBlobs { get; } = new(StringComparer.Ordinal);
        internal PublicationPayloadPool? Pool { get; } = pool;
        internal string? CurrentHead { get; set; } = head;
        internal string? EffectHead { get; } = effect ? head : null;
        internal Dictionary<(Type Kind, string Oid), GitHubValue> Staged { get; } = [];
        internal long StagedBytes { get; set; }
        internal void Stage(GitHubValue value, CancellationToken token) => Pool?.Stage(this, value, token);
        internal void Promote(CancellationToken token) => Pool?.Promote(this, token);
        public void Dispose() => Pool?.Discard(this);
    }

    private sealed record RemoteCheckpoint(string Head, GitHubCoordinationDocument Document,
        CampaignCheckpointArtifact? Artifact, CampaignVerifiedArchive? ArchiveEvidence);

    private static T RequireRemote<T>(GitHubApiResult<T> result) where T : class => result.Value
        ?? throw new GitHubCoordinationException(failure: new(GitHubCoordinationFailureKind.Transport,
            result.Failure ?? new(GitHubFailureCode.InvalidResponse), result.Delivery, result.Context, result.RequiredPermissions));

    private async ValueTask<GitHubCoordinationDocument> ReadDocumentAsync(string head, CancellationToken token,
        ReadProof? proof = null)
    {
        token.ThrowIfCancellationRequested();
        // Parsed document acceptance is never carried across independent gates.
        if (proof?.Documents.TryGetValue(head, out var authenticated) == true)
        {
            documents[head] = authenticated;
            return authenticated;
        }
        var effect = proof?.EffectHead == head;
        var commit = await ReadProofCommitAsync(head, token, proof, effect).ConfigureAwait(false);
        var root = await ReadProofTreeAsync(commit.TreeOid, token, proof, effect).ConfigureAwait(false);
        if (root.Entries.Length != 1 || root.Entries[0].Path != GitHubCoordinationObjects.RootPath
            || root.Entries[0].Mode != GitHubTreeMode.Directory) throw new GitHubCoordinationException();
        var leaf = await ReadProofTreeAsync(root.Entries[0].Oid, token, proof, effect).ConfigureAwait(false);
        var stateEntry = leaf.Entries.SingleOrDefault(entry => entry.Path == GitHubCoordinationObjects.StatePath)
            ?? throw new GitHubCoordinationException();
        var blob = await ReadBlobAsync(stateEntry.Oid, token, proof,
            publicationPayload: true, fresh: effect || proof?.CurrentHead == head).ConfigureAwait(false);
        var document = AuthenticateDocument(commit, root, leaf, blob);
        proof?.Stage(commit, token);
        proof?.Stage(root, token);
        proof?.Stage(leaf, token);
        proof?.Stage(blob, token);
        documents[head] = document;
        if (proof is not null) proof.Documents[head] = document;
        return document;
    }

    private GitHubCoordinationDocument AuthenticateDocument(GitHubCommit commit, GitHubTree root, GitHubTree leaf, GitHubBlob blob)
    {
        var document = GitHubCoordinationDocument.Decode(blob.Bytes.AsMemory());
        var prepared = GitHubCoordinationObjects.Prepare(document);
        GitHubCoordinationObjects.Authenticate(prepared, commit, root, leaf, blob);
        if (!document.RepositoryId.Equals(client.ReadAuthority.RepositoryOwner + "/" + client.ReadAuthority.RepositoryName, StringComparison.OrdinalIgnoreCase)
            || document.TargetRef != client.ReadAuthority.TargetRef || document.CampaignLineage != client.ReadAuthority.CampaignLineage)
            throw new GitHubCoordinationException();
        return document;
    }

    // Object hashes authenticate bytes; the immediate parent authenticates which
    // protected facts this operation was allowed to replace. Never walk the
    // checkpoint chain: publication history has its own immutable event graph.
    private async ValueTask<GitHubCoordinationDocument> ReadAcceptedDocumentAsync(string head, CancellationToken token,
        ReadProof? proof = null)
    {
        var document = await ReadDocumentAsync(head, token, proof).ConfigureAwait(false);
        if (document.PredecessorOid == ZeroOid)
        {
            if (document.Checkpoint is not null || document.Plan is not null || document.Archive is not null
                || document.Publication is { Anchor: not GitHubCoordinationDocument.CurrentCommit }
                || document.Claim is { Epoch: not 1 })
                throw new GitHubCoordinationException();
            return document;
        }
        var parent = (await ReadDocumentAsync(document.PredecessorOid, token, proof).ConfigureAwait(false))
            .RetainAt(document.PredecessorOid);
        if (parent.Claim is { } priorClaim)
        {
            if (document.Claim is not { } claim
                || !(claim.Owner == priorClaim.Owner && claim.Epoch == priorClaim.Epoch
                    || claim.Owner != priorClaim.Owner && claim.Epoch == priorClaim.Epoch + 1))
                throw new GitHubCoordinationException();
        }
        else if (document.Claim is { Epoch: not 1 }) throw new GitHubCoordinationException();
        var publicationChanged = document.Publication is { Anchor: GitHubCoordinationDocument.CurrentCommit };
        if (!publicationChanged && document.Publication != parent.Publication)
            throw new GitHubCoordinationException();
        if (document.Checkpoint == parent.Checkpoint)
        {
            if (document.Plan != parent.Plan || document.Archive != parent.Archive)
                throw new GitHubCoordinationException();
        }
        else
        {
            if (publicationChanged || document.Claim != parent.Claim || document.Checkpoint is not { } pointer)
                throw new GitHubCoordinationException();
            var next = await ReadCheckpointBlobAsync(pointer, token, proof).ConfigureAwait(false);
            if (parent.Checkpoint is { } previous)
            {
                var before = await ReadCheckpointBlobAsync(previous, token, proof).ConfigureAwait(false);
                CampaignStateFactory.ValidateSuccessorHistory(before, next);
                if (parent.Publication is { } retained)
                {
                    var publication = GitHubPublicationEvent.Decode((await ReadBlobAsync(retained.Oid, token, proof).ConfigureAwait(false)).Bytes.AsMemory());
                    var intent = publication.State;
                    if (next.State.Batch.Identity == intent.BatchIdentity
                            ? !FrozenCheckpointMatches(intent, next)
                            : intent.Stage is not (GitHubCoordinationStage.Published or GitHubCoordinationStage.AwaitingReview
                                or GitHubCoordinationStage.StaleDraft or GitHubCoordinationStage.Merged
                                or GitHubCoordinationStage.ClosedUnmerged or GitHubCoordinationStage.Stale))
                        throw new GitHubCoordinationException();
                }
                if (document.Plan != parent.Plan
                    && !CampaignStateFactory.IsNoResultBatchSuccessor(before, next)
                    && (next.State.Snapshot.ExecutionCommitmentSha256 == before.State.Snapshot.ExecutionCommitmentSha256
                        || next.State.Predecessor?.FinalCheckpointSha256 != before.Sha256))
                    throw new GitHubCoordinationException();
                if (document.Archive != parent.Archive)
                {
                    if (document.Archive is not { Anchor: GitHubCoordinationDocument.CurrentCommit } archive
                        || archive.Oid != previous.Oid)
                        throw new GitHubCoordinationException();
                    _ = CampaignDispatchArchive.VerifyTransition(before, next);
                }
            }
            else if (document.Archive is not null || next.State.ArchivedDispatchPrefix is not null)
                throw new GitHubCoordinationException();
        }
        if (publicationChanged && document.Claim != parent.Claim)
            throw new GitHubCoordinationException();
        return document;
    }

    private async ValueTask<GitHubBlob> ReadBlobAsync(string oid, CancellationToken token, ReadProof? proof = null,
        bool publicationPayload = false, bool fresh = false)
    {
        token.ThrowIfCancellationRequested();
        if (proof?.Blobs.TryGetValue(oid, out var authenticated) == true
            && (!(fresh || !publicationPayload) || !proof.PooledBlobs.Contains(oid))) return authenticated;
        if (publicationPayload && !fresh && proof?.Pool?.Read<GitHubBlob>(oid, token) is { } prior)
        {
            proof.Blobs[oid] = prior;
            proof.PooledBlobs.Add(oid);
            return prior;
        }
        var blob = RequireRemote(await client.GetBlobAsync(oid, token).ConfigureAwait(false));
        if (blob.Oid != oid || GitHubCoordinationObjects.ObjectOid("blob", blob.Bytes.AsSpan()) != oid)
            throw new GitHubCoordinationException();
        if (proof is not null) { proof.Blobs[oid] = blob; proof.PooledBlobs.Remove(oid); }
        return blob;
    }

    private async ValueTask<CampaignCheckpointArtifact> ReadCheckpointBlobAsync(GitHubCheckpointPointer pointer, CancellationToken token, ReadProof? proof = null)
    {
        var blob = await ReadBlobAsync(pointer.Oid, token, proof).ConfigureAwait(false);
        var parsed = CampaignStateJson.Parse(blob.Bytes.AsMemory());
        if (!parsed.IsValid || parsed.Artifact is not { } artifact || artifact.Sha256 != pointer.Sha256
            || artifact.CheckpointRevision != pointer.Revision || !blob.Bytes.AsSpan().SequenceEqual(artifact.ExactUtf8Json.AsSpan()))
            throw new GitHubCoordinationException();
        return artifact;
    }

    // Authenticate immutable checkpoint/plan/archive/publication bindings at the
    // already observed head. Mutable authority fences remain separate physical reads.
    private async ValueTask<RemoteCheckpoint> ReadCheckpointContentAsync(GitHubRepositoryIdentity repository,
        string head, GitHubCoordinationDocument document, CancellationToken token, ReadProof proof)
    {
        if (document.Publication is { } publication)
        {
            var retained = publication.Anchor == GitHubCoordinationDocument.CurrentCommit
                ? publication with { Anchor = head } : publication;
            // Immutable event provenance can be reused within this invocation;
            // every mutable head still passes the parent-retention check above.
            if (!verifiedCheckpointPublications.Contains(retained))
            {
                var target = RequireRemote(await client.GetRefAsync(client.ReadAuthority.TargetRef, token).ConfigureAwait(false));
                var result = await ReadStateAsync(repository, target, head, token, matchAuthority: false, proof: proof)
                    .ConfigureAwait(false);
                if (result.State is null) throw new GitHubCoordinationException(transportFailure: result.Failure?.TransportFailure);
                verifiedCheckpointPublications.Add(retained);
            }
        }
        CampaignCheckpointArtifact? artifact = null;
        CampaignVerifiedArchive? evidence = null;
        if (document.Checkpoint is { } pointer)
        {
            artifact = await ReadCheckpointBlobAsync(pointer, token, proof).ConfigureAwait(false);
            var plan = document.Plan ?? throw new GitHubCoordinationException();
            var planBlob = await ReadBlobAsync(plan.Oid, token, proof).ConfigureAwait(false);
            if (Convert.ToHexStringLower(SHA256.HashData(planBlob.Bytes.AsSpan())) != plan.Sha256
                || artifact.State.CampaignLineage != document.CampaignLineage
                || artifact.State.Snapshot.ExecutionCommitmentSha256 != plan.ExecutionCommitment
                || artifact.State.Batch.Identity != plan.BatchIdentity) throw new GitHubCoordinationException();
            if (artifact.State.ArchivedDispatchPrefix is { } prefix)
            {
                var archive = document.Archive ?? throw new GitHubCoordinationException();
                var anchor = archive.Anchor == GitHubCoordinationDocument.CurrentCommit ? head : archive.Anchor;
                var archiveDocument = anchor == head ? document : await ReadDocumentAsync(anchor, token, proof).ConfigureAwait(false);
                if (archiveDocument.Archive != archive with { Anchor = GitHubCoordinationDocument.CurrentCommit }
                    || archive.WitnessSha256 != prefix.WitnessSha256
                    || archiveDocument.PredecessorOid == ZeroOid) throw new GitHubCoordinationException();
                var before = await ReadDocumentAsync(archiveDocument.PredecessorOid, token, proof).ConfigureAwait(false);
                if (before.Checkpoint is not { } beforePointer || beforePointer.Oid != archive.Oid)
                    throw new GitHubCoordinationException();
                var predecessor = await ReadCheckpointBlobAsync(beforePointer, token, proof).ConfigureAwait(false);
                if ((predecessor.State.ArchivedDispatchPrefix?.WitnessSha256) != before.Archive?.WitnessSha256)
                    throw new GitHubCoordinationException();
                if (archiveDocument.Checkpoint is not { } transitionPointer) throw new GitHubCoordinationException();
                var transition = anchor == head ? artifact
                    : await ReadCheckpointBlobAsync(transitionPointer, token, proof).ConfigureAwait(false);
                _ = CampaignDispatchArchive.VerifyTransition(predecessor, transition);
                evidence = CampaignDispatchArchive.Verify(predecessor, artifact);
            }
            else if (document.Archive is not null) throw new GitHubCoordinationException();
        }
        return new(head, document, artifact, evidence);
    }

    private async ValueTask<RemoteCheckpoint?> ReadRemoteCheckpointAsync(CancellationToken token, ReadProof? transactionProof = null, bool verifyTarget = false, CheckpointAttempt? attempt = null)
    {
        var repository = RequireRemote(await client.GetRepositoryAsync(token).ConfigureAwait(false));
        var current = await client.GetRefAsync(coordinationRef, token).ConfigureAwait(false);
        if (current.Value is null)
        {
            if (current.Failure?.Code == GitHubFailureCode.NotFound) return null;
            throw new GitHubCoordinationException(transportFailure: current.Failure);
        }
        using var ownedProof = transactionProof is null ? PublicationProof(repository.Identity, current.Value.Oid) : null;
        var proof = transactionProof ?? ownedProof!;
        // Current claim bytes are always fetched at the actual observed head.
        // Only immutable predecessors may come from this owner's bounded pool.
        proof.CurrentHead = current.Value.Oid;
        var document = await ReadAcceptedDocumentAsync(current.Value.Oid, token, proof).ConfigureAwait(false);
        var observed = await ReadCheckpointContentAsync(repository.Identity, current.Value.Oid, document, token, proof).ConfigureAwait(false);
        if (verifyTarget)
        {
            var target = RequireRemote(await client.GetRefAsync(client.ReadAuthority.TargetRef, token).ConfigureAwait(false));
            if (target.Oid != client.CheckpointAuthority!.ExpectedBaseOid) throw new GitHubCoordinationException();
        }
        var reread = await client.GetRefAsync(coordinationRef, token).ConfigureAwait(false);
        if (reread.Value is null && reread.Failure?.Code != GitHubFailureCode.NotFound)
            throw new GitHubCoordinationException(transportFailure: reread.Failure ?? new(GitHubFailureCode.InvalidResponse));
        if (reread.Value?.Oid != current.Value.Oid)
            throw new GitHubCoordinationException(transportFailure: new(GitHubFailureCode.Conflict));
        lastObservedCheckpoint = observed;
        if (invocationOwner is not null && !Owns(document))
        {
            ObserveCheckpointFailure(attempt, GitHubCheckpointStage.OwnerConfirmation,
                document.Claim?.Owner != invocationOwner || document.Claim.Epoch != invocationEpoch
                    ? GitHubCheckpointReason.OwnerMismatch : GitHubCheckpointReason.LeaseUnverifiable);
            StopCheckpoint();
        }
        if (!checkpointStopped) proof.Promote(token);
        return observed;
    }

    private async ValueTask<RemoteCheckpoint?> ReadCallerCheckpointAsync(CancellationToken token, ReadProof? proof = null, bool verifyTarget = false, CheckpointAttempt? attempt = null,
        GitHubCheckpointStage stage = GitHubCheckpointStage.PredecessorRead)
    {
        try { return await ReadRemoteCheckpointAsync(token, proof, verifyTarget, attempt).ConfigureAwait(false); }
        catch (GitHubCoordinationException exception) when (
            exception.TransportFailure?.Code == GitHubFailureCode.Cancelled && token.IsCancellationRequested)
        { ObserveCheckpointException(attempt, stage, exception, token); throw new OperationCanceledException(token); }
    }

    public async ValueTask<CampaignCheckpointReadResult> ReadAsync(CancellationToken cancellationToken)
    {
        var attempt = BeginCheckpointOperation(GitHubCheckpointOperation.Read);
        try { await checkpointGate.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException exception)
        { ObserveCheckpointException(attempt, GitHubCheckpointStage.Gate, exception, cancellationToken); throw; }
        try
        {
            var remote = await ReadCallerCheckpointAsync(cancellationToken, attempt: attempt).ConfigureAwait(false);
            if (remote?.Artifact is not { } artifact) return CampaignCheckpointReadResult.NotFound();
            var readback = CampaignCheckpointReadResult.Found(artifact.ExactUtf8Json.AsSpan(), artifact.CheckpointRevision,
                artifact.Sha256, remote.ArchiveEvidence);
            ObserveSemanticReadback(remote, readback);
            return readback;
        }
        catch (GitHubCoordinationException exception) when (exception.TransportFailure?.Code == GitHubFailureCode.Conflict)
        { ObserveCheckpointException(attempt, GitHubCheckpointStage.PredecessorRead, exception, cancellationToken); return CampaignCheckpointReadResult.LeaseConflict(); }
        catch (GitHubCoordinationException exception) when (exception.TransportFailure is not null)
        { ObserveCheckpointException(attempt, GitHubCheckpointStage.PredecessorRead, exception, cancellationToken); CheckpointFailure = exception.TransportFailure; return CampaignCheckpointReadResult.Unreadable(); }
        catch (Exception exception) when (exception is GitHubCoordinationException or CampaignStateValidationException)
        { ObserveCheckpointException(attempt, GitHubCheckpointStage.PredecessorRead, exception, cancellationToken); return CampaignCheckpointReadResult.Invalid(); }
        catch (OperationCanceledException exception)
        { ObserveCheckpointException(attempt, GitHubCheckpointStage.PredecessorRead, exception, cancellationToken); throw; }
        finally { checkpointGate.Release(); }
    }

    public async ValueTask<bool> BindPlanAsync(CampaignRecoveryPlan plan, CancellationToken cancellationToken)
    {
        await checkpointGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var remote = await ReadRemoteCheckpointAsync(cancellationToken).ConfigureAwait(false);
            if (remote is null || !Owns(remote.Document) || plan.CampaignLineage != remote.Document.CampaignLineage) return false;
            if (remote.Document.Plan is { } previous && previous.ExecutionCommitment == plan.ExecutionCommitmentSha256)
            {
                var saved = await ReadBlobAsync(previous.Oid, cancellationToken).ConfigureAwait(false);
                if (!plan.Matches(saved.Bytes.AsSpan())) return false;
            }
            boundPlan = plan;
            return true;
        }
        catch (GitHubCoordinationException exception) when (
            exception.TransportFailure?.Code == GitHubFailureCode.Cancelled && cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
        catch (Exception exception) when (exception is GitHubCoordinationException or CampaignStateValidationException)
        { return false; }
        finally { checkpointGate.Release(); }
    }

    private bool Owns(GitHubCoordinationDocument document) => !checkpointStopped
        && document.Claim is { } claim && claim.Owner == invocationOwner && claim.Epoch == invocationEpoch
        && client.ServerTime is { } now && now.AddSeconds(1).ToUnixTimeSeconds() < claim.ExpiresUnixSeconds;

    public ValueTask<CampaignCheckpointWriteResult> CreateIfAbsentAsync(ReadOnlyMemory<byte> exactUtf8Json,
        long checkpointRevision, string sha256, CancellationToken cancellationToken) =>
        WriteCheckpointAsync(null, null, exactUtf8Json, checkpointRevision, sha256, cancellationToken);

    public ValueTask<CampaignCheckpointWriteResult> ReplaceIfCurrentAsync(long expectedCheckpointRevision,
        string expectedSha256, ReadOnlyMemory<byte> exactUtf8Json, long checkpointRevision, string sha256,
        CancellationToken cancellationToken, CancellationToken mutationCancellationToken = default) =>
        WriteCheckpointAsync(expectedCheckpointRevision, expectedSha256,
            exactUtf8Json, checkpointRevision, sha256, cancellationToken, mutationCancellationToken);
}
