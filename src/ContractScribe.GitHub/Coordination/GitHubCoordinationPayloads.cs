using System.Text;
using System.Collections.Immutable;
using ContractScribe.GitHub.GitData;
using ContractScribe.GitHub.Transport;

namespace ContractScribe.GitHub.Coordination;

internal sealed partial class GitHubCoordinationStore
{
    private PublicationPayloadPool? publicationPayloads;
    private PublicationPayloadPool? checkpointPayloads;

    private ReadProof PublicationProof(GitHubRepositoryIdentity repository, string? head, bool effect = false)
    {
        var pool = publicationPayloads;
        if (pool is null && invocationOwner is not null && !checkpointStopped
            && !checkpointLifetime.IsCancellationRequested && !client.HasMutationAuthority)
        {
            pool = checkpointPayloads;
            if (pool is null)
            {
                var created = new PublicationPayloadPool(this, repository, checkpointLifetime.Token) { CheckpointScope = true };
                pool = Interlocked.CompareExchange(ref checkpointPayloads, created, null) ?? created;
                if (!ReferenceEquals(pool, created)) created.Dispose();
            }
        }
        return new(pool?.Matches(repository) == true ? pool : null, head, effect);
    }

    private void StopPublicationPayloads() => Interlocked.Exchange(ref publicationPayloads, null)?.Dispose();
    private void StopCheckpointPayloads() => Interlocked.Exchange(ref checkpointPayloads, null)?.Dispose();

    internal async ValueTask<bool> VerifyObservedProposalIntegrityAsync(GitHubCampaignGeneration generation, CancellationToken token)
    {
        // Initial read-only observation precedes checkpoint acquisition. It may
        // acquire immutable Git bytes, without acquiring a claim or write right.
        if (nativeCoordination is null && client.NativeCoordinationFactory is { } nativeFactory)
            nativeCoordination = await nativeFactory(token).ConfigureAwait(false);
        using var proof = PublicationProof(client.AuthenticatedRepository ?? throw new GitHubCoordinationException(), null);
        var verified = await GitHubObservedProposalIntegrity.VerifyAsync(generation, token,
            async (oid, cancellation) => nativeCoordination is not null
                ? RequireRemote(await ReadNativeSourceTreeIdentityAsync(oid, cancellation).ConfigureAwait(false)).Oid
                : (await Read(oid, cancellation, client.GetCommitAsync).ConfigureAwait(false))?.TreeOid,
            (oid, cancellation) => Read(oid, cancellation, ReadSourceTreeAsync),
            (oid, cancellation) => Read(oid, cancellation, ReadSourceBlobAsync)).ConfigureAwait(false);
        // This is only a complete immutable content proof. The observer still
        // physically rechecks PR ownership/lifecycle, pagination and ref fences.
        // No partial, failed or cancelled content proof warms a later gate.
        if (verified) proof.Promote(token);
        return verified;

        async ValueTask<T?> Read<T>(string oid, CancellationToken cancellation,
            Func<string, CancellationToken, ValueTask<GitHubApiResult<T>>> fetch) where T : GitHubValue
        {
            var value = proof.Pool?.Read<T>(oid, cancellation)
                ?? GitHubObservedProposalIntegrity.Read(await fetch(oid, cancellation).ConfigureAwait(false));
            if (value is not null) proof.Stage(value, cancellation);
            return value;
        }
    }

    internal async ValueTask<GitHubProposalFailureKind?> VerifyExpectedProposalIntegrityAsync(
        GitHubCreateCommit expected, ImmutableArray<GitHubCoordinationChangedFile> files,
        GitHubCommit? actualCommit, CancellationToken token,
        IReadOnlyDictionary<string, GitHubValue>? observedObjects = null)
    {
        using var proof = PublicationProof(client.AuthenticatedRepository ?? throw new GitHubCoordinationException(), null);
        if (actualCommit is not null) proof.Stage(actualCommit, token);
        var failure = await GitHubObservedProposalIntegrity.VerifyExpectedAsync(expected, files, token,
            (oid, ct) => actualCommit is not null && actualCommit.Oid == oid
                ? ValueTask.FromResult<GitHubCommit?>(actualCommit) : Read(oid, ct, client.GetCommitAsync),
            (oid, ct) => Read<GitHubTree>(oid, ct, (tree, cancellation) => client.GetTreeAsync(tree, cancellation)),
            (oid, ct) => Read(oid, ct, client.GetBlobAsync)).ConfigureAwait(false);
        // Promote only this complete role proof. Actual mutation readback calls
        // bypass this pool, and an incomplete/cancelled prefix is discarded.
        if (failure is null) proof.Promote(token);
        return failure;

        async ValueTask<T?> Read<T>(string oid, CancellationToken cancellation,
            Func<string, CancellationToken, ValueTask<GitHubApiResult<T>>> fetch) where T : GitHubValue
        {
            cancellation.ThrowIfCancellationRequested();
            var value = observedObjects?.GetValueOrDefault(oid) as T ?? proof.Pool?.Read<T>(oid, cancellation);
            if (value is null)
            {
                var result = await fetch(oid, cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                value = result.Value ?? throw new GitHubCoordinationException(failure:
                    new(GitHubCoordinationFailureKind.Transport, result.Failure ?? new(GitHubFailureCode.InvalidResponse),
                        result.Delivery, result.Context, result.RequiredPermissions));
            }
            proof.Stage(value, cancellation);
            return value;
        }
    }

    internal async ValueTask<(GitHubProposalObjectGraph? Graph, GitHubProposalFailureKind? Failure)> ReadVerifiedProposalBasisAsync(
        string rootOid, ImmutableArray<GitHubCoordinationChangedFile> files, CancellationToken token)
    {
        using var proof = PublicationProof(client.AuthenticatedRepository ?? throw new GitHubCoordinationException(), null);
        var result = await GitHubObservedProposalIntegrity.ReadExpectedTreeAsync(rootOid, files, token,
            (oid, ct) => Read(oid, ct, ReadSourceTreeAsync),
            (oid, ct) => Read(oid, ct, ReadSourceBlobAsync)).ConfigureAwait(false);
        if (result.Failure is null) proof.Promote(token);
        return result;

        async ValueTask<T?> Read<T>(string oid, CancellationToken cancellation,
            Func<string, CancellationToken, ValueTask<GitHubApiResult<T>>> fetch) where T : GitHubValue
        {
            var value = proof.Pool?.Read<T>(oid, cancellation);
            if (value is null)
            {
                var observed = await fetch(oid, cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                value = observed.Value ?? throw new GitHubCoordinationException(failure:
                    new(GitHubCoordinationFailureKind.Transport, observed.Failure ?? new(GitHubFailureCode.InvalidResponse),
                        observed.Delivery, observed.Context, observed.RequiredPermissions));
            }
            proof.Stage(value, cancellation);
            return value;
        }
    }

    private async ValueTask<GitHubApiResult<GitHubTree>> ReadSourceTreeAsync(string oid, CancellationToken token) =>
        nativeCoordination is null ? await client.GetTreeAsync(oid, token).ConfigureAwait(false)
            : await ReadNativeSourceAsync(() => nativeCoordination.ReadSourceTreeAsync(oid, token), token).ConfigureAwait(false);

    private async ValueTask<GitHubApiResult<GitHubBlob>> ReadSourceBlobAsync(string oid, CancellationToken token) =>
        nativeCoordination is null ? await client.GetBlobAsync(oid, token).ConfigureAwait(false)
            : await ReadNativeSourceAsync(() => nativeCoordination.ReadSourceBlobAsync(oid, token), token).ConfigureAwait(false);

    private ValueTask<GitHubApiResult<GitHubObjectIdentity>> ReadNativeSourceTreeIdentityAsync(string oid, CancellationToken token) =>
        ReadNativeSourceAsync<GitHubObjectIdentity>(async () =>
            new(await nativeCoordination!.AcquireObservedBaseTreeAsync(oid, token).ConfigureAwait(false)), token);

    // Match the API reader boundary: failed acquisition is transport evidence,
    // never a validated checkout/content mismatch. This performs no retry or
    // fallback, and semantic integrity checks still run only on successful data.
    private async ValueTask<GitHubApiResult<T>> ReadNativeSourceAsync<T>(Func<ValueTask<T>> read, CancellationToken token)
        where T : GitHubValue
    {
        try { return new(await read().ConfigureAwait(false), null, GitHubDelivery.Read); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (GitHubProtocolException exception) { return GitHubApiResult<T>.Failed(exception.Code, dispatched: true); }
        catch (OperationCanceledException) { return GitHubApiResult<T>.Failed(client.CancellationFailure.Code, dispatched: true); }
        catch (Exception exception) when (exception is DecoderFallbackException or FormatException or OverflowException)
        { return GitHubApiResult<T>.Failed(GitHubFailureCode.InvalidResponse, dispatched: true); }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        { return GitHubApiResult<T>.Failed(GitHubFailureCode.HostFailure, dispatched: true); }
    }

    // R4/R5 may consume only raw objects admitted by a complete content proof.
    // Cold single-object observations are not promoted here. Principal metadata
    // outside Git bytes is never carried into the proposal content role.
    internal async ValueTask<GitHubApiResult<GitHubCommit>> ReadVerifiedProposalCommitAsync(
        GitHubCreateCommit expected, CancellationToken token)
    {
        using var proof = PublicationProof(client.AuthenticatedRepository ?? throw new GitHubCoordinationException(), null);
        if (GitHubProposalObjects.ObjectOid("commit", GitHubProposalObjects.CommitBytes(expected)) == expected.ExpectedOid
            && proof.Pool?.Read<GitHubCommit>(expected.ExpectedOid, token) is { } prior
            && GitHubProposalObjects.ExactCommit(prior, expected))
            return new(prior with { CheckpointSignature = null }, null, GitHubDelivery.Read);
        return await client.GetCommitAsync(expected.ExpectedOid, token).ConfigureAwait(false);
    }

    internal async ValueTask<GitHubApiResult<GitHubTree>> ReadVerifiedProposalTreeAsync(string oid, CancellationToken token)
    {
        using var proof = PublicationProof(client.AuthenticatedRepository ?? throw new GitHubCoordinationException(), null);
        if (proof.Pool?.Read<GitHubTree>(oid, token) is { } prior) return new(prior, null, GitHubDelivery.Read);
        return await client.GetTreeAsync(oid, token).ConfigureAwait(false);
    }

    internal async ValueTask<GitHubApiResult<GitHubBlob>> ReadVerifiedProposalBlobAsync(string oid, CancellationToken token)
    {
        using var proof = PublicationProof(client.AuthenticatedRepository ?? throw new GitHubCoordinationException(), null);
        if (proof.Pool?.Read<GitHubBlob>(oid, token) is { } prior) return new(prior, null, GitHubDelivery.Read);
        return await client.GetBlobAsync(oid, token).ConfigureAwait(false);
    }

    // Checkpoint acceptance repeatedly authenticates retained publication history.
    // Reuse its immutable payloads for one confirmed owner, never its mutable
    // claim/checkpoint bodies or any mutation/readback entitlement. Confirmed
    // same-owner publication entry may retain these raw positive payloads.
    // The owner binds the exact store/client, whose origin, credential lifetime
    // and read authority cannot be exchanged. Numeric repository identity is
    // checked again for each proof. This pool contains payloads, never rights.
    private sealed class PublicationPayloadPool(GitHubCoordinationStore owner,
        GitHubRepositoryIdentity repository, CancellationToken execution,
        int maximumEntries = 1024, long maximumBytes = 64 * 1024 * 1024) : IDisposable
    {
        private const int MaximumEntries = 1024;
        private const long MaximumBytes = 64 * 1024 * 1024;
        private readonly int entryLimit = Math.Clamp(maximumEntries, 0, MaximumEntries);
        private readonly long byteLimit = Math.Clamp(maximumBytes, 0, MaximumBytes);
        private readonly object gate = new();
        private readonly Dictionary<(Type Kind, string Oid), GitHubValue> values = [];
        private readonly HashSet<ReadProof> staging = [];
        private long bytes, reservedBytes;
        private int reservedEntries;
        private bool stopped;
        internal bool CheckpointScope { get; set; }

        internal bool EnterPublication(CancellationToken token)
        {
            lock (gate)
            {
                // The checkpoint gate is held across this handoff. No parsed
                // proof or partial staging crosses the phase boundary, and a
                // fresh confirmed owner/ref observation still owns execution.
                if (!CheckpointScope || stopped || execution.IsCancellationRequested || token.IsCancellationRequested
                    || staging.Count != 0 || owner.checkpointStopped || !owner.publicationActive
                    || owner.client.AuthenticatedRepository != repository
                    || owner.lastWrittenCheckpointReadback is not { } current || !owner.Owns(current.Document)
                    || current.Reference?.NodeId != owner.client.CheckpointRefNodeId)
                { Clear(); return false; }
                execution = token;
                CheckpointScope = false;
                return true;
            }
        }

        private bool Active => !stopped && !execution.IsCancellationRequested && !owner.checkpointStopped
            && (CheckpointScope
                ? owner.invocationOwner is not null && !owner.publicationActive && !owner.client.HasMutationAuthority
                    && ReferenceEquals(owner.checkpointPayloads, this) && owner.client.CheckpointAuthority is not null
                : owner.publicationActive && ReferenceEquals(owner.publicationPayloads, this) && owner.client.HasMutationAuthority)
            && owner.client.AuthenticatedRepository == repository;

        internal bool Matches(GitHubRepositoryIdentity current)
        {
            lock (gate)
            {
                if (current == repository && Active) return true;
                Clear();
                return false;
            }
        }

        internal T? Read<T>(string oid, CancellationToken token) where T : GitHubValue
        {
            token.ThrowIfCancellationRequested();
            execution.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (!Active) { Clear(); return null; }
                var value = values.GetValueOrDefault((typeof(T), oid)) as T;
                token.ThrowIfCancellationRequested();
                execution.ThrowIfCancellationRequested();
                return value;
            }
        }

        internal void Stage(ReadProof proof, GitHubValue value, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            execution.ThrowIfCancellationRequested();
            var key = Key(value);
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                execution.ThrowIfCancellationRequested();
                if (!Active) { Clear(); return; }
                if (values.ContainsKey(key) || proof.Staged.ContainsKey(key)) return;
                var size = Size(value);
                if (values.Count + reservedEntries >= entryLimit
                    || size > byteLimit - bytes - reservedBytes) return;
                proof.Staged.Add(key, value);
                staging.Add(proof);
                proof.StagedBytes += size;
                reservedBytes += size;
                reservedEntries++;
            }
        }

        internal void Promote(ReadProof proof, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            execution.ThrowIfCancellationRequested();
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                execution.ThrowIfCancellationRequested();
                if (!Active) { Clear(); return; }
                foreach (var (key, value) in proof.Staged)
                    if (values.TryAdd(key, value)) bytes += Size(value);
                Release(proof);
            }
        }

        internal void Discard(ReadProof proof)
        {
            lock (gate)
            {
                if (stopped) { proof.Staged.Clear(); proof.StagedBytes = 0; }
                else Release(proof);
            }
        }

        private void Release(ReadProof proof)
        {
            reservedBytes -= proof.StagedBytes;
            reservedEntries -= proof.Staged.Count;
            proof.Staged.Clear();
            proof.StagedBytes = 0;
            staging.Remove(proof);
        }

        private static (Type Kind, string Oid) Key(GitHubValue value) => value switch
        {
            GitHubCommit commit => (typeof(GitHubCommit), commit.Oid),
            GitHubTree tree => (typeof(GitHubTree), tree.Oid),
            GitHubBlob blob => (typeof(GitHubBlob), blob.Oid),
            _ => throw new InvalidOperationException(),
        };

        // Encoded payload plus a conservative per-entry metadata allowance.
        // R3 canonical documents and fully verified proposal-content objects
        // retain their transport/tree/actor bounds before positive admission.
        private static long Size(GitHubValue value) => 1024 + (value switch
        {
            GitHubBlob blob => (long)blob.Bytes.Length,
            GitHubTree tree => tree.Entries.Sum(entry => 32L + Encoding.UTF8.GetByteCount(entry.Path)),
            GitHubCommit commit => 512L + Encoding.UTF8.GetByteCount(commit.Message)
                + commit.Parents.Length * 41L
                + Encoding.UTF8.GetByteCount(commit.Author.Name) + Encoding.UTF8.GetByteCount(commit.Author.Email)
                + Encoding.UTF8.GetByteCount(commit.Committer.Name) + Encoding.UTF8.GetByteCount(commit.Committer.Email)
                + (commit.CheckpointSignature is { } signature
                    ? Encoding.UTF8.GetByteCount(signature.Repository.NodeId) + Encoding.UTF8.GetByteCount(signature.Repository.Owner)
                        + Encoding.UTF8.GetByteCount(signature.Repository.Name) + Encoding.UTF8.GetByteCount(signature.RefNodeId)
                        + Encoding.UTF8.GetByteCount(signature.Author.NodeId) + Encoding.UTF8.GetByteCount(signature.Author.Login)
                    : 0),
            _ => throw new InvalidOperationException(),
        });

        private void Clear()
        {
            stopped = true;
            values.Clear();
            foreach (var proof in staging) { proof.Staged.Clear(); proof.StagedBytes = 0; }
            staging.Clear();
            bytes = reservedBytes = 0;
            reservedEntries = 0;
        }

        public void Dispose() { lock (gate) Clear(); }
    }

    private async ValueTask<GitHubCommit> ReadProofCommitAsync(string oid, CancellationToken token, ReadProof? proof, bool fresh)
    {
        if (!fresh && proof?.Pool?.Read<GitHubCommit>(oid, token) is { } prior) return prior;
        return RequireRemote(await client.GetCommitAsync(oid, token).ConfigureAwait(false));
    }

    private async ValueTask<GitHubTree> ReadProofTreeAsync(string oid, CancellationToken token, ReadProof? proof, bool fresh)
    {
        if (!fresh && proof?.Pool?.Read<GitHubTree>(oid, token) is { } prior) return prior;
        return RequireRemote(await client.GetTreeAsync(oid, token).ConfigureAwait(false));
    }
}
