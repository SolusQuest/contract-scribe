using System.Text;
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
        using var proof = PublicationProof(client.AuthenticatedRepository ?? throw new GitHubCoordinationException(), null);
        var verified = await GitHubObservedProposalIntegrity.VerifyAsync(generation, token,
            (oid, cancellation) => Read(oid, cancellation, client.GetCommitAsync),
            (oid, cancellation) => Read<GitHubTree>(oid, cancellation,
                (tree, ct) => client.GetTreeAsync(tree, ct)),
            (oid, cancellation) => Read(oid, cancellation, client.GetBlobAsync)).ConfigureAwait(false);
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

    // Checkpoint acceptance repeatedly authenticates retained publication history.
    // Reuse its immutable payloads for one confirmed owner, never its mutable
    // claim/checkpoint bodies or any mutation/readback entitlement. Publication
    // entry clears this pool before creating its separate callback-local pool.
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
        internal bool CheckpointScope { get; init; }

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
                + commit.Parents.Length * 41L,
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
