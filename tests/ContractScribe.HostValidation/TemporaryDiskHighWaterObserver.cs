namespace ContractScribe.HostValidation;

public sealed class TemporaryDiskHighWaterObserver : IDisposable
{
    private readonly object sync = new();
    private readonly string temporaryWorkRoot;
    private readonly string outputStagingRoot;
    private readonly IReadOnlyDictionary<string, long> before;
    private readonly Dictionary<string, long> observedLengths;
    private readonly FileSystemWatcher[] watchers;
    private bool observerComplete = true;
    private bool retentionBreach;
    private bool gateCaptured;

    public TemporaryDiskHighWaterObserver(
        string temporaryWorkRoot,
        string outputStagingRoot)
    {
        this.temporaryWorkRoot = Path.GetFullPath(temporaryWorkRoot);
        this.outputStagingRoot = Path.GetFullPath(outputStagingRoot);
        Directory.CreateDirectory(this.temporaryWorkRoot);
        Directory.CreateDirectory(this.outputStagingRoot);
        before = CaptureLengths();
        observedLengths = new Dictionary<string, long>(before, StringComparer.Ordinal);
        watchers =
        [
            CreateWatcher(this.temporaryWorkRoot, "temporary-work"),
            CreateWatcher(this.outputStagingRoot, "output-staging")
        ];
    }

    public TemporaryDiskHighWaterEvidence CaptureGate()
    {
        lock (sync)
        {
            var current = CaptureLengths();
            var temporaryBytes = MeasureChangedBytes(current, before, "temporary-work/");
            var stagingBytes = MeasureChangedBytes(current, before, "output-staging/");
            gateCaptured = true;
            return new(
                "peak-concurrent-logical-file-bytes",
                "contractscribe-temporary-work-and-output-staging.v1",
                "pre-subject-to-temporary-disk-high-water.v1",
                temporaryBytes,
                stagingBytes,
                checked(temporaryBytes + stagingBytes),
                observerComplete,
                retentionBreach);
        }
    }

    public TemporaryDiskHighWaterEvidence Complete(
        TemporaryDiskHighWaterEvidence evidence)
    {
        lock (sync)
        {
            return evidence with
            {
                ObserverComplete = evidence.ObserverComplete && observerComplete,
                RetentionBreach = evidence.RetentionBreach || retentionBreach
            };
        }
    }

    public void Dispose()
    {
        foreach (var watcher in watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
    }

    private FileSystemWatcher CreateWatcher(string root, string role)
    {
        var watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName
                | NotifyFilters.DirectoryName
                | NotifyFilters.Size
                | NotifyFilters.LastWrite
        };
        watcher.Created += (_, args) => ObserveLength(role, root, args.FullPath);
        watcher.Changed += (_, args) => ObserveLength(role, root, args.FullPath);
        watcher.Deleted += (_, args) => ObserveRemoval(role, root, args.FullPath);
        watcher.Renamed += (_, args) =>
        {
            ObserveRemoval(role, root, args.OldFullPath);
            ObserveLength(role, root, args.FullPath);
        };
        watcher.Error += (_, _) =>
        {
            lock (sync)
            {
                observerComplete = false;
            }
        };
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private void ObserveLength(string role, string root, string fullPath)
    {
        lock (sync)
        {
            if (gateCaptured || Directory.Exists(fullPath))
            {
                return;
            }
            try
            {
                if (!File.Exists(fullPath))
                {
                    return;
                }
                var key = ToKey(role, root, fullPath);
                var length = new FileInfo(fullPath).Length;
                if (observedLengths.TryGetValue(key, out var previous)
                    && length < previous)
                {
                    retentionBreach = true;
                }
                observedLengths[key] = length;
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or NotSupportedException)
            {
                observerComplete = false;
            }
        }
    }

    private void ObserveRemoval(string role, string root, string fullPath)
    {
        lock (sync)
        {
            if (gateCaptured)
            {
                return;
            }
            var key = ToKey(role, root, fullPath);
            if (observedLengths.ContainsKey(key))
            {
                retentionBreach = true;
            }
        }
    }

    private IReadOnlyDictionary<string, long> CaptureLengths()
    {
        try
        {
            var result = new Dictionary<string, long>(StringComparer.Ordinal);
            CaptureRoot(result, "temporary-work", temporaryWorkRoot);
            CaptureRoot(result, "output-staging", outputStagingRoot);
            return result;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            observerComplete = false;
            return new Dictionary<string, long>(StringComparer.Ordinal);
        }
    }

    private static void CaptureRoot(
        Dictionary<string, long> result,
        string role,
        string root)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false
        };
        foreach (var path in Directory.EnumerateFiles(root, "*", options))
        {
            result[ToKey(role, root, path)] = new FileInfo(path).Length;
        }
    }

    private static long MeasureChangedBytes(
        IReadOnlyDictionary<string, long> current,
        IReadOnlyDictionary<string, long> baseline,
        string prefix)
    {
        long total = 0;
        foreach (var pair in current.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal)))
        {
            if (!baseline.TryGetValue(pair.Key, out var original) || original != pair.Value)
            {
                total = checked(total + pair.Value);
            }
        }
        return total;
    }

    private static string ToKey(string role, string root, string fullPath) =>
        $"{role}/{Path.GetRelativePath(root, fullPath).Replace('\\', '/')}";
}
