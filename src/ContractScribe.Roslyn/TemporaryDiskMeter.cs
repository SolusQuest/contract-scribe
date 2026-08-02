using ContractScribe.Core.Hosting;

namespace ContractScribe.Roslyn;

internal sealed class TemporaryDiskMeter : IDisposable
{
    private readonly object gate = new();
    private readonly IReadOnlyList<string> roots;
    private readonly List<FileSystemWatcher> watchers = [];
    private Exception? observerFailure;
    private long highWater;
    private bool disposed;

    public TemporaryDiskMeter(string? temporaryRoot, string? outputStagingRoot)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        roots = new[] { temporaryRoot, outputStagingRoot }
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root!)))
            .Distinct(comparison)
            .OrderBy(root => root.Length)
            .Aggregate(
                new List<string>(),
                (selected, candidate) =>
                {
                    if (!selected.Any(root => IsContained(root, candidate)))
                    {
                        selected.Add(candidate);
                    }
                    return selected;
                });
        var watched = new HashSet<string>(comparison);
        foreach (var root in roots)
        {
            var watchRoot = FindExistingAncestor(root);
            if (watchRoot is null || !watched.Add(watchRoot))
            {
                continue;
            }
            var watcher = new FileSystemWatcher(watchRoot)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName
                    | NotifyFilters.DirectoryName
                    | NotifyFilters.Size
                    | NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };
            watcher.Created += ObserveChange;
            watcher.Changed += ObserveChange;
            watcher.Deleted += ObserveChange;
            watcher.Renamed += ObserveRename;
            watcher.Error += ObserveError;
            watchers.Add(watcher);
        }
        _ = Reconcile();
    }

    public long HighWater => Interlocked.Read(ref highWater);

    public long Reconcile()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (observerFailure is not null)
            {
                throw new IOException("The temporary-disk observer lost continuity.", observerFailure);
            }
            long total = 0;
            foreach (var root in roots)
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }
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
                    total = checked(total + new FileInfo(path).Length);
                }
            }
            UpdateHighWater(total);
            return total;
        }
    }

    public HostMeasuredBound ToFact() => new(
        "temporary-disk-bytes",
        "bytes",
        HighWater,
        HostContractResources.RequireBound("temporary-disk-bytes"),
        HostEnforcementClass.InternallyEnforceable);

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            foreach (var watcher in watchers)
            {
                watcher.Dispose();
            }
            watchers.Clear();
        }
    }

    private void ObserveChange(object sender, FileSystemEventArgs args) => ReconcileFromEvent();

    private void ObserveRename(object sender, RenamedEventArgs args) => ReconcileFromEvent();

    private void ObserveError(object sender, ErrorEventArgs args)
    {
        lock (gate)
        {
            observerFailure ??= args.GetException();
        }
    }

    private void ReconcileFromEvent()
    {
        try
        {
            _ = Reconcile();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            lock (gate)
            {
                if (!disposed)
                {
                    observerFailure ??= exception;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateRegularFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
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

    private static bool IsContained(string root, string candidate) =>
        candidate.StartsWith(
            root + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static string? FindExistingAncestor(string path)
    {
        var current = new DirectoryInfo(path);
        while (current is not null && !current.Exists)
        {
            current = current.Parent;
        }
        return current?.FullName;
    }
}
