using ContractScribe.Core.Hosting;

namespace ContractScribe.Roslyn;

internal sealed class TemporaryDiskMeter : IDisposable
{
    private readonly object gate = new();
    private readonly IReadOnlyList<string> roots;
    private readonly StringComparer pathComparer;
    private readonly Dictionary<string, long> currentLengths;
    private readonly HashSet<string> retainedPaths;
    private readonly List<FileSystemWatcher> watchers = [];
    private Exception? observerFailure;
    private long highWater;
    private bool disposed;

    public TemporaryDiskMeter(string? temporaryRoot, string? outputStagingRoot)
    {
        pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        currentLengths = new Dictionary<string, long>(pathComparer);
        retainedPaths = new HashSet<string>(pathComparer);
        roots = new[] { temporaryRoot, outputStagingRoot }
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root!)))
            .Distinct(pathComparer)
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
        var watched = new HashSet<string>(pathComparer);
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
            watcher.Deleted += ObserveDelete;
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
                return HighWater;
            }
            var observed = new Dictionary<string, long>(pathComparer);
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
                    var length = new FileInfo(path).Length;
                    observed[Path.GetFullPath(path)] = length;
                    total = checked(total + length);
                }
            }
            currentLengths.Clear();
            foreach (var item in observed)
            {
                currentLengths.Add(item.Key, item.Value);
                retainedPaths.Add(item.Key);
            }
            UpdateHighWater(total);
            return total;
        }
    }

    public void ObserveHostAllocation(
        string path,
        long currentBytes,
        long allocatedBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (currentBytes < 0 || allocatedBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(allocatedBytes));
        }
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var fullPath = Path.GetFullPath(path);
            if (!IsGovernedPath(fullPath) || IsIgnoredSentinel(fullPath))
            {
                throw new ArgumentException(
                    "The direct Host allocation must belong to a governed root.",
                    nameof(path));
            }
            currentLengths[fullPath] = allocatedBytes;
            retainedPaths.Add(fullPath);
            UpdateHighWater(checked(currentBytes + allocatedBytes));
        }
    }

    public bool TryCreateFactWithinThreshold(out HostMeasuredBound? fact)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var measured = highWater;
            var threshold = HostContractResources.RequireBound("temporary-disk-bytes");
            if (measured > threshold)
            {
                fact = null;
                return false;
            }
            fact = new HostMeasuredBound(
                "temporary-disk-bytes",
                "bytes",
                measured,
                threshold,
                HostEnforcementClass.InternallyEnforceable);
            return true;
        }
    }

    public HostMeasuredBound ToFact() =>
        TryCreateFactWithinThreshold(out var fact)
            ? fact!
            : throw new InvalidOperationException(
                "The temporary-disk measurement exceeds its protected threshold.");

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

    private void ObserveChange(object sender, FileSystemEventArgs args) => ObservePath(args.FullPath);

    private void ObserveDelete(object sender, FileSystemEventArgs args)
    {
        if (!IsGovernedPath(args.FullPath) || IsIgnoredSentinel(args.FullPath))
        {
            return;
        }
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            var fullPath = Path.GetFullPath(args.FullPath);
            if (!currentLengths.Remove(fullPath) && !retainedPaths.Contains(fullPath))
            {
                FailClosed(new IOException(
                    "A governed temporary entry disappeared before its event-time size was retained."));
            }
        }
    }

    private void ObserveRename(object sender, RenamedEventArgs args)
    {
        var oldGoverned = IsGovernedPath(args.OldFullPath)
            && !IsIgnoredSentinel(args.OldFullPath);
        var newGoverned = IsGovernedPath(args.FullPath)
            && !IsIgnoredSentinel(args.FullPath);
        if (!oldGoverned && !newGoverned)
        {
            return;
        }
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            if (oldGoverned)
            {
                var oldPath = Path.GetFullPath(args.OldFullPath);
                if (!currentLengths.Remove(oldPath) && !retainedPaths.Contains(oldPath))
                {
                    FailClosed(new IOException(
                        "A governed temporary entry was renamed before its event-time size was retained."));
                }
            }
        }
        if (newGoverned)
        {
            ObservePath(args.FullPath);
        }
    }

    private void ObserveError(object sender, ErrorEventArgs args)
    {
        lock (gate)
        {
            if (!disposed)
            {
                FailClosed(args.GetException());
            }
        }
    }

    private void ObservePath(string path)
    {
        if (!IsGovernedPath(path) || IsIgnoredSentinel(path))
        {
            return;
        }
        try
        {
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }
                var fullPath = Path.GetFullPath(path);
                if (Directory.Exists(fullPath))
                {
                    _ = Reconcile();
                    return;
                }
                if (!File.Exists(fullPath))
                {
                    FailClosed(new IOException(
                        "A governed temporary entry disappeared before its event-time size was retained."));
                    return;
                }
                var attributes = File.GetAttributes(fullPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    FailClosed(new IOException("A governed temporary entry is a reparse point."));
                    return;
                }
                currentLengths[fullPath] = new FileInfo(fullPath).Length;
                retainedPaths.Add(fullPath);
                UpdateHighWater(SumCurrentLengths());
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            lock (gate)
            {
                if (!disposed)
                {
                    FailClosed(exception);
                }
            }
        }
    }

    private long SumCurrentLengths()
    {
        long total = 0;
        foreach (var length in currentLengths.Values)
        {
            total = checked(total + length);
        }
        return total;
    }

    private void FailClosed(Exception exception)
    {
        observerFailure ??= exception;
        UpdateHighWater(checked(HostContractResources.RequireBound("temporary-disk-bytes") + 1));
    }

    private bool IsGovernedPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return roots.Any(root => pathComparer.Equals(root, fullPath) || IsContained(root, fullPath));
    }

    private static bool IsIgnoredSentinel(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith(".contractscribe-hv-freeze-", StringComparison.Ordinal)
            || name.StartsWith(".contractscribe-hv-release-", StringComparison.Ordinal);
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
