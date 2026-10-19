using System.Collections.Immutable;
using ContractScribe.GitHub.Coordination;

namespace ContractScribe.GitHub.Transport;

/// <summary>A single prepared coordination transaction, never general blob or ref authority.</summary>
internal sealed class GitHubCheckpointWrite
{
    internal GitHubCheckpointWrite(GitHubPreparedCoordination prepared, IEnumerable<GitHubBlob> blobs)
    {
        Prepared = prepared;
        var permitted = GitHubCoordinationObjects.LeafEntries(prepared).Select(entry => entry.Oid).ToHashSet(StringComparer.Ordinal);
        var builder = ImmutableDictionary.CreateBuilder<string, ImmutableArray<byte>>(StringComparer.Ordinal);
        foreach (var blob in blobs)
        {
            if (!permitted.Contains(blob.Oid) || blob.Oid != GitHubCoordinationObjects.ObjectOid("blob", blob.Bytes.AsSpan()))
                throw new GitHubCoordinationException();
            builder.Add(blob.Oid, blob.Bytes);
        }
        builder[prepared.BlobOid] = prepared.StateBytes;
        Blobs = builder.ToImmutable();
    }

    internal GitHubPreparedCoordination Prepared { get; }
    internal ImmutableDictionary<string, ImmutableArray<byte>> Blobs { get; }
    internal bool AllowsBlob(string oid, ReadOnlySpan<byte> bytes) =>
        Blobs.TryGetValue(oid, out var expected) && bytes.SequenceEqual(expected.AsSpan());
    internal bool AllowsTree(string oid, ImmutableArray<GitHubTreeEntry> entries) =>
        oid == Prepared.LeafTreeOid && entries.SequenceEqual(GitHubCoordinationObjects.LeafEntries(Prepared))
        || oid == Prepared.RootTreeOid && entries.SequenceEqual(GitHubCoordinationObjects.RootEntries(Prepared));
    internal bool AllowsCommit(GitHubCreateCommit commit) => commit == GitHubCoordinationObjects.CommitRequest(Prepared);
}
