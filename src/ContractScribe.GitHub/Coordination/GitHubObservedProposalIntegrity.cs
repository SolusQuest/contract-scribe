using System.Collections.Immutable;
using ContractScribe.GitHub.GitData;
using ContractScribe.GitHub.Transport;

namespace ContractScribe.GitHub.Coordination;

// Checkpoint-backed preflight authenticates the recorded publication's objects
// without reconstructing a candidate, creating publication authority or loading Roslyn.
internal static class GitHubObservedProposalIntegrity
{
    internal static async ValueTask<bool> VerifyAsync(GitHubCampaignGeneration generation, CancellationToken token,
        Func<string, CancellationToken, ValueTask<GitHubCommit?>> readCommit,
        Func<string, CancellationToken, ValueTask<GitHubTree?>> readTree,
        Func<string, CancellationToken, ValueTask<GitHubBlob?>> readBlob)
    {
        var original = await readCommit(generation.OriginalBaseOid, token).ConfigureAwait(false);
        var published = await readCommit(generation.HeadOid, token).ConfigureAwait(false);
        if (original is null || published is null || original.Oid != generation.OriginalBaseOid || published.Oid != generation.HeadOid
            || published.TreeOid != generation.TreeOid) return false;
        var basis = await ReadTreeAsync(original.TreeOid, token, readTree).ConfigureAwait(false);
        var proposal = await ReadTreeAsync(published.TreeOid, token, readTree).ConfigureAwait(false);
        if (basis is null || proposal is null) return false;
        foreach (var file in generation.ChangedFiles)
        {
            if (!basis.TryGetValue(file.Path, out var before) || !proposal.TryGetValue(file.Path, out var after)
                || before.Mode is not (GitHubTreeMode.File or GitHubTreeMode.Executable) || after.Mode != before.Mode)
                return false;
            var oldBlob = await readBlob(before.Oid, token).ConfigureAwait(false);
            var newBlob = await readBlob(after.Oid, token).ConfigureAwait(false);
            if (oldBlob is null || newBlob is null
                || GitHubProposalObjects.ObjectOid("blob", oldBlob.Bytes.AsSpan()) != before.Oid
                || GitHubProposalObjects.ObjectOid("blob", newBlob.Bytes.AsSpan()) != after.Oid
                || GitHubProposalObjects.Sha256(newBlob.Bytes.AsSpan()) != file.CandidateSha256) return false;
        }
        return true;
    }

    // The owned R4 plan supplies the exact deterministic commit and candidate
    // commitments. This proves content only; current refs and send rights are
    // checked by the caller, independently of these immutable payloads.
    internal static async ValueTask<GitHubProposalFailureKind?> VerifyExpectedAsync(
        GitHubCreateCommit expected, ImmutableArray<GitHubCoordinationChangedFile> files, CancellationToken token,
        Func<string, CancellationToken, ValueTask<GitHubCommit?>> readCommit,
        Func<string, CancellationToken, ValueTask<GitHubTree?>> readTree,
        Func<string, CancellationToken, ValueTask<GitHubBlob?>> readBlob)
    {
        try
        {
            if (GitHubProposalObjects.ObjectOid("commit", GitHubProposalObjects.CommitBytes(expected)) != expected.ExpectedOid)
                return GitHubProposalFailureKind.Integrity;
            var commit = await readCommit(expected.ExpectedOid, token).ConfigureAwait(false);
            if (commit is null || !GitHubProposalObjects.ExactCommit(commit, expected))
                return GitHubProposalFailureKind.Integrity;
            var verified = await ReadExpectedTreeAsync(commit.TreeOid, files, token, readTree, readBlob).ConfigureAwait(false);
            return verified.Failure;
        }
        catch (ContentBoundsException) { return GitHubProposalFailureKind.Bounds; }
    }

    internal static async ValueTask<(GitHubProposalObjectGraph? Graph, GitHubProposalFailureKind? Failure)> ReadExpectedTreeAsync(
        string rootOid, ImmutableArray<GitHubCoordinationChangedFile> files, CancellationToken token,
        Func<string, CancellationToken, ValueTask<GitHubTree?>> readTree,
        Func<string, CancellationToken, ValueTask<GitHubBlob?>> readBlob)
    {
        try
        {
            var graph = await ReadTreeCoreAsync(rootOid, token, readTree).ConfigureAwait(false);
            if (graph is null) return (null, GitHubProposalFailureKind.Integrity);
            foreach (var file in files)
            {
                if (!graph.Files.TryGetValue(file.Path, out var entry)
                    || entry.Mode is not (GitHubTreeMode.File or GitHubTreeMode.Executable))
                    return (null, GitHubProposalFailureKind.Integrity);
                var blob = await readBlob(entry.Oid, token).ConfigureAwait(false);
                if (blob is null || GitHubProposalObjects.ObjectOid("blob", blob.Bytes.AsSpan()) != entry.Oid
                    || GitHubProposalObjects.Sha256(blob.Bytes.AsSpan()) != file.CandidateSha256
                    || entry.Size is { } size && size != blob.Bytes.Length)
                    return (null, GitHubProposalFailureKind.Integrity);
            }
            return (graph, null);
        }
        catch (ContentBoundsException) { return (null, GitHubProposalFailureKind.Bounds); }
    }

    // Missing authenticated objects are integrity failures. Service failures keep
    // their existing transport category instead of masquerading as changed bytes.
    internal static T? Read<T>(GitHubApiResult<T> result) where T : class =>
        result.Value ?? (result.Failure?.Code == GitHubFailureCode.NotFound ? null
            : throw new GitHubCoordinationException(transportFailure:
                result.Failure ?? new(GitHubFailureCode.InvalidResponse)));

    // Current checkout binding always reads its actual target tree physically.
    internal static ValueTask<Dictionary<string, GitHubTreeEntry>?> ReadTreeAsync(
        GitHubApiClient client, string rootOid, CancellationToken token) =>
        ReadTreeAsync(rootOid, token, async (oid, cancellation) =>
            Read(await client.GetTreeAsync(oid, cancellation).ConfigureAwait(false)));

    private static async ValueTask<Dictionary<string, GitHubTreeEntry>?> ReadTreeAsync(
        string rootOid, CancellationToken token,
        Func<string, CancellationToken, ValueTask<GitHubTree?>> readTree)
    {
        try { return (await ReadTreeCoreAsync(rootOid, token, readTree).ConfigureAwait(false))?.Files; }
        catch (ContentBoundsException) { return null; }
    }

    private sealed class ContentBoundsException : Exception;

    private static async ValueTask<GitHubProposalObjectGraph?> ReadTreeCoreAsync(
        string rootOid, CancellationToken token,
        Func<string, CancellationToken, ValueTask<GitHubTree?>> readTree)
    {
        var files = new Dictionary<string, GitHubTreeEntry>(StringComparer.Ordinal);
        var trees = new Dictionary<string, ImmutableArray<GitHubTreeEntry>>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<(string Path, string Oid)>();
        pending.Push(("", rootOid));
        var count = 0;
        var treeCount = 0;
        long metadata = 0;
        while (pending.TryPop(out var node))
        {
            token.ThrowIfCancellationRequested();
            if ((node.Path.Length == 0 ? 0 : node.Path.Count(c => c == '/') + 1) > 128 || ++treeCount > 100_000)
                throw new ContentBoundsException();
            var tree = await readTree(node.Oid, token).ConfigureAwait(false);
            if (tree is null || tree.Oid != node.Oid || GitHubProposalObjects.TreeOid(tree.Entries) != node.Oid) return null;
            trees.Add(node.Path, tree.Entries);
            foreach (var entry in tree.Entries)
            {
                var path = node.Path.Length == 0 ? entry.Path : node.Path + "/" + entry.Path;
                metadata = checked(metadata + (long)path.Length * 6 + 128);
                if (++count > 100_000 || metadata > 64 * 1024 * 1024) throw new ContentBoundsException();
                if (path.Length > 1024 || !names.Add(path)) return null;
                if (entry.Mode == GitHubTreeMode.Directory)
                {
                    if (entry.Size is not null) return null;
                    pending.Push((path, entry.Oid));
                }
                else files.Add(path, entry);
            }
        }
        return new(rootOid, trees, files);
    }
}
