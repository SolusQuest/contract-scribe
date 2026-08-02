using ContractScribe.Core.Hosting;

namespace ContractScribe.Roslyn;

internal sealed class TemporaryDiskMeter
{
    private readonly IReadOnlyList<(string Role, string Root)> roots;
    private long highWater;

    public TemporaryDiskMeter(string? temporaryRoot, string? outputStagingRoot)
    {
        roots = new[]
            {
                (Role: "temporary", Root: temporaryRoot),
                (Role: "staging", Root: outputStagingRoot),
            }
            .Where(item => !string.IsNullOrWhiteSpace(item.Root))
            .Select(item => (item.Role, Path.GetFullPath(item.Root!)))
            .ToArray();
    }

    public long HighWater => Interlocked.Read(ref highWater);

    public long Reconcile()
    {
        long total = 0;
        foreach (var (role, root) in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }
            var entries = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var path in EnumerateRegularFiles(root))
            {
                var relative = Path.GetRelativePath(root, path)
                    .Replace(Path.DirectorySeparatorChar, '/');
                var name = Path.GetFileName(relative);
                if (name.StartsWith(".contractscribe-hv-freeze-", StringComparison.Ordinal)
                    || name.StartsWith(".contractscribe-hv-release-", StringComparison.Ordinal))
                {
                    continue;
                }
                entries.Add(role + "\0" + relative, new FileInfo(path).Length);
            }
            total = checked(total + entries.Values.Sum());
        }
        UpdateHighWater(total);
        return total;
    }

    private static IEnumerable<string> EnumerateRegularFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            var directoryAttributes = File.GetAttributes(directory);
            if ((directoryAttributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("A governed temporary directory is a reparse point.");
            }
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("A governed temporary entry is a reparse point.");
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
                else
                {
                    yield return entry;
                }
            }
        }
    }

    public HostMeasuredBound ToFact() => new(
        "temporary-disk-bytes",
        "bytes",
        HighWater,
        HostContractResources.RequireBound("temporary-disk-bytes"),
        HostEnforcementClass.InternallyEnforceable);

    private void UpdateHighWater(long candidate)
    {
        while (true)
        {
            var current = Interlocked.Read(ref highWater);
            if (candidate <= current
                || Interlocked.CompareExchange(ref highWater, candidate, current) == current)
            {
                return;
            }
        }
    }
}
