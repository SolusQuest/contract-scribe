using System.Security.Cryptography;

namespace ContractScribe.Roslyn;

internal static class RepositoryInventory
{
    public static IReadOnlyDictionary<string, InventoryEntry> Capture(
        string root,
        CancellationToken cancellationToken)
    {
        var entries = new Dictionary<string, InventoryEntry>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (relative.Equals(".git", StringComparison.Ordinal)
                    || relative.StartsWith(".git/", StringComparison.Ordinal))
                {
                    continue;
                }

                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    FileSystemInfo info = Directory.Exists(path)
                        ? new DirectoryInfo(path)
                        : new FileInfo(path);
                    entries[relative] = new InventoryEntry(
                        relative,
                        "reparse",
                        info.LinkTarget ?? string.Empty,
                        0,
                        string.Empty);
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(path);
                    continue;
                }

                var infoFile = new FileInfo(path);
                using var stream = File.OpenRead(path);
                entries[relative] = new InventoryEntry(
                    relative,
                    "file",
                    string.Empty,
                    infoFile.Length,
                    Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
            }
        }

        return entries;
    }

    public static IReadOnlyList<string> ChangedPaths(
        IReadOnlyDictionary<string, InventoryEntry> before,
        IReadOnlyDictionary<string, InventoryEntry> after)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var paths = before.Keys.Concat(after.Keys).Distinct(comparer).Order(StringComparer.Ordinal);
        return paths.Where(path =>
                !before.TryGetValue(path, out var left)
                || !after.TryGetValue(path, out var right)
                || left != right)
            .ToArray();
    }
}

internal sealed record InventoryEntry(
    string Path,
    string Kind,
    string LinkTarget,
    long Length,
    string Sha256);
