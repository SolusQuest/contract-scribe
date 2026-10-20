using System.Collections.Immutable;
using ContractScribe.GitHub.GitData;
using ContractScribe.GitHub.Transport;

namespace ContractScribe.GitHub.Coordination;

// Checkpoint-backed preflight authenticates the recorded publication's objects
// without reconstructing a candidate, creating publication authority or loading Roslyn.
internal static class GitHubObservedProposalIntegrity
{
    internal static async ValueTask<bool> VerifyAsync(GitHubApiClient client, GitHubCampaignGeneration generation, CancellationToken token)
    {
        var original = Read(await client.GetCommitAsync(generation.OriginalBaseOid, token).ConfigureAwait(false));
        var published = Read(await client.GetCommitAsync(generation.HeadOid, token).ConfigureAwait(false));
        if (original?.Oid != generation.OriginalBaseOid || published?.Oid != generation.HeadOid
            || published.TreeOid != generation.TreeOid) return false;
        var basis = await ReadTreeAsync(client, original.TreeOid, token).ConfigureAwait(false);
        var proposal = await ReadTreeAsync(client, published.TreeOid, token).ConfigureAwait(false);
        if (basis is null || proposal is null) return false;
        foreach (var file in generation.ChangedFiles)
        {
            if (!basis.TryGetValue(file.Path, out var before) || !proposal.TryGetValue(file.Path, out var after)
                || before.Mode is not (GitHubTreeMode.File or GitHubTreeMode.Executable) || after.Mode != before.Mode)
                return false;
            var oldBlob = Read(await client.GetBlobAsync(before.Oid, token).ConfigureAwait(false));
            var newBlob = Read(await client.GetBlobAsync(after.Oid, token).ConfigureAwait(false));
            if (oldBlob is null || newBlob is null
                || GitHubProposalObjects.ObjectOid("blob", oldBlob.Bytes.AsSpan()) != before.Oid
                || GitHubProposalObjects.ObjectOid("blob", newBlob.Bytes.AsSpan()) != after.Oid
                || GitHubProposalObjects.Sha256(newBlob.Bytes.AsSpan()) != file.CandidateSha256) return false;
        }
        return true;
    }

    // Missing authenticated objects are integrity failures. Service failures keep
    // their existing transport category instead of masquerading as changed bytes.
    internal static T? Read<T>(GitHubApiResult<T> result) where T : class =>
        result.Value ?? (result.Failure?.Code == GitHubFailureCode.NotFound ? null
            : throw new GitHubCoordinationException(transportFailure:
                result.Failure ?? new(GitHubFailureCode.InvalidResponse)));

    internal static async ValueTask<Dictionary<string, GitHubTreeEntry>?> ReadTreeAsync(
        GitHubApiClient client, string rootOid, CancellationToken token)
    {
        var files = new Dictionary<string, GitHubTreeEntry>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<(string Path, string Oid)>();
        pending.Push(("", rootOid));
        var count = 0;
        long metadata = 0;
        while (pending.TryPop(out var node))
        {
            token.ThrowIfCancellationRequested();
            if (node.Path.Count(c => c == '/') > 128) return null;
            var tree = Read(await client.GetTreeAsync(node.Oid, token).ConfigureAwait(false));
            if (tree is null || tree.Oid != node.Oid || GitHubProposalObjects.TreeOid(tree.Entries) != node.Oid) return null;
            foreach (var entry in tree.Entries)
            {
                var path = node.Path.Length == 0 ? entry.Path : node.Path + "/" + entry.Path;
                metadata = checked(metadata + (long)path.Length * 6 + 128);
                if (++count > 100_000 || metadata > 64 * 1024 * 1024 || path.Length > 1024 || !names.Add(path)) return null;
                if (entry.Mode == GitHubTreeMode.Directory) pending.Push((path, entry.Oid));
                else files.Add(path, entry);
            }
        }
        return files;
    }
}
