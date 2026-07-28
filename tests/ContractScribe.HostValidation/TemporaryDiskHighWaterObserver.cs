namespace ContractScribe.HostValidation;

public sealed class TemporaryDiskHighWaterObserver : IDisposable
{
    private const string BarrierPrefix = ".contractscribe-hv-observer-barrier-";

    private readonly object sync = new();
    private readonly string temporaryWorkRoot;
    private readonly string outputStagingRoot;
    private readonly Dictionary<string, long> currentLengths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ManualResetEventSlim> barrierAcknowledgements =
        new(StringComparer.Ordinal);
    private readonly object callbackSync = new();
    private readonly FileSystemWatcher[] watchers;
    private int activeCallbacks;
    private bool callbacksAccepting = true;
    private bool observerComplete = true;
    private bool retentionBreach;
    private bool observationClosed;
    private long currentTemporaryBytes;
    private long currentStagingBytes;
    private long peakTemporaryBytes;
    private long peakStagingBytes;
    private long peakTotalBytes;

    public TemporaryDiskHighWaterObserver(
        string temporaryWorkRoot,
        string outputStagingRoot)
    {
        this.temporaryWorkRoot = Path.GetFullPath(temporaryWorkRoot);
        this.outputStagingRoot = Path.GetFullPath(outputStagingRoot);
        Directory.CreateDirectory(this.temporaryWorkRoot);
        Directory.CreateDirectory(this.outputStagingRoot);
        var prestate = CaptureLengths();
        if (prestate.Count != 0)
        {
            throw new ProtocolException("HV242_TEMPORARY_DISK_CONTRACT");
        }
        watchers =
        [
            CreateWatcher(this.temporaryWorkRoot, "temporary-work"),
            CreateWatcher(this.outputStagingRoot, "output-staging")
        ];
        Synchronize();
    }

    public TemporaryDiskHighWaterEvidence CaptureGate()
    {
        Synchronize();
        lock (sync)
        {
            observationClosed = true;
            return new(
                "peak-concurrent-logical-file-bytes",
                "contractscribe-temporary-work-and-output-staging.v1",
                "pre-subject-to-temporary-disk-high-water.v1",
                peakTemporaryBytes,
                peakStagingBytes,
                peakTotalBytes,
                observerComplete,
                retentionBreach);
        }
    }

    public void Synchronize()
    {
        lock (sync)
        {
            if (observationClosed)
            {
                throw new InvalidOperationException("The observation interval is closed.");
            }
        }

        var barriers = new List<BarrierRegistration>(2);
        TryCreateBarrier(barriers, "temporary-work", temporaryWorkRoot);
        TryCreateBarrier(barriers, "output-staging", outputStagingRoot);
        foreach (var barrier in barriers)
        {
            if (!barrier.Acknowledgement.Wait(TimeSpan.FromSeconds(10)))
            {
                lock (sync)
                {
                    observerComplete = false;
                }
            }
        }
        if (!WaitForCallbacksToDrain(TimeSpan.FromSeconds(10)))
        {
            lock (sync)
            {
                observerComplete = false;
            }
        }

        lock (sync)
        {
            Reconcile(CaptureLengths());
        }
        foreach (var barrier in barriers)
        {
            try
            {
                File.Delete(barrier.Path);
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or NotSupportedException)
            {
                lock (sync)
                {
                    observerComplete = false;
                }
            }
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
        }
        lock (callbackSync)
        {
            callbacksAccepting = false;
        }
        _ = WaitForCallbacksToDrain(TimeSpan.FromSeconds(10));
        foreach (var watcher in watchers)
        {
            watcher.Dispose();
        }
        foreach (var acknowledgement in barrierAcknowledgements.Values)
        {
            acknowledgement.Dispose();
        }
    }

    private FileSystemWatcher CreateWatcher(string root, string role)
    {
        var watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName
                | NotifyFilters.DirectoryName
                | NotifyFilters.Size
                | NotifyFilters.LastWrite
        };
        watcher.Created += (_, args) =>
            ExecuteCallback(() => ObserveLength(role, root, args.FullPath));
        watcher.Changed += (_, args) =>
            ExecuteCallback(() => ObserveLength(role, root, args.FullPath));
        watcher.Deleted += (_, args) =>
            ExecuteCallback(() => ObserveRemoval(role, root, args.FullPath));
        watcher.Renamed += (_, args) =>
            ExecuteCallback(() =>
            {
                ObserveRemoval(role, root, args.OldFullPath);
                ObserveLength(role, root, args.FullPath);
            });
        watcher.Error += (_, _) =>
            ExecuteCallback(() =>
            {
                lock (sync)
                {
                    observerComplete = false;
                }
            });
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private void ExecuteCallback(Action callback)
    {
        lock (callbackSync)
        {
            if (!callbacksAccepting)
            {
                return;
            }
            activeCallbacks++;
        }
        try
        {
            callback();
        }
        finally
        {
            lock (callbackSync)
            {
                activeCallbacks--;
                if (activeCallbacks == 0)
                {
                    Monitor.PulseAll(callbackSync);
                }
            }
        }
    }

    private bool WaitForCallbacksToDrain(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        lock (callbackSync)
        {
            while (activeCallbacks != 0)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero
                    || !Monitor.Wait(callbackSync, remaining))
                {
                    return false;
                }
            }
            return true;
        }
    }

    private void ObserveLength(string role, string root, string fullPath)
    {
        lock (sync)
        {
            if (observationClosed)
            {
                return;
            }
            var key = ToKey(role, root, fullPath);
            if (barrierAcknowledgements.TryGetValue(key, out var acknowledgement))
            {
                acknowledgement.Set();
                return;
            }
            if (Directory.Exists(fullPath))
            {
                return;
            }
            try
            {
                if (!File.Exists(fullPath))
                {
                    retentionBreach = true;
                    return;
                }
                var length = new FileInfo(fullPath).Length;
                UpdateLength(key, length);
            }
            catch (Exception exception) when (
                exception is FileNotFoundException
                    or DirectoryNotFoundException)
            {
                retentionBreach = true;
                RemoveLength(key);
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
            if (observationClosed)
            {
                return;
            }
            var key = ToKey(role, root, fullPath);
            if (barrierAcknowledgements.ContainsKey(key))
            {
                return;
            }
            retentionBreach = true;
            RemoveLength(key);
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

    private void CaptureRoot(
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
        foreach (var candidate in Directory.EnumerateFiles(root, "*", options)
                     .Select(path => new
                     {
                         Path = path,
                         Key = ToKey(role, root, path)
                     })
                     .Where(candidate =>
                         !barrierAcknowledgements.ContainsKey(candidate.Key)))
        {
            result[candidate.Key] = new FileInfo(candidate.Path).Length;
        }
    }

    private void TryCreateBarrier(
        ICollection<BarrierRegistration> barriers,
        string role,
        string root)
    {
        try
        {
            barriers.Add(CreateBarrier(role, root));
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            lock (sync)
            {
                observerComplete = false;
            }
        }
    }

    private BarrierRegistration CreateBarrier(string role, string root)
    {
        var path = Path.Join(root, $"{BarrierPrefix}{Guid.NewGuid():N}");
        var key = ToKey(role, root, path);
        var acknowledgement = new ManualResetEventSlim(false);
        lock (sync)
        {
            barrierAcknowledgements.Add(key, acknowledgement);
        }
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        stream.WriteByte(0);
        stream.Flush(flushToDisk: true);
        return new(path, acknowledgement);
    }

    private void Reconcile(IReadOnlyDictionary<string, long> current)
    {
        foreach (var missing in currentLengths.Keys
                     .Where(key => !current.ContainsKey(key))
                     .ToArray())
        {
            retentionBreach = true;
            RemoveLength(missing);
        }
        foreach (var pair in current)
        {
            UpdateLength(pair.Key, pair.Value);
        }
    }

    private void UpdateLength(string key, long length)
    {
        var previous = currentLengths.GetValueOrDefault(key);
        if (currentLengths.ContainsKey(key) && length < previous)
        {
            retentionBreach = true;
        }
        currentLengths[key] = length;
        if (key.StartsWith("temporary-work/", StringComparison.Ordinal))
        {
            currentTemporaryBytes = checked(currentTemporaryBytes - previous + length);
        }
        else if (key.StartsWith("output-staging/", StringComparison.Ordinal))
        {
            currentStagingBytes = checked(currentStagingBytes - previous + length);
        }
        else
        {
            observerComplete = false;
            return;
        }
        UpdatePeak();
    }

    private void RemoveLength(string key)
    {
        if (!currentLengths.Remove(key, out var previous))
        {
            return;
        }
        if (key.StartsWith("temporary-work/", StringComparison.Ordinal))
        {
            currentTemporaryBytes = checked(currentTemporaryBytes - previous);
        }
        else if (key.StartsWith("output-staging/", StringComparison.Ordinal))
        {
            currentStagingBytes = checked(currentStagingBytes - previous);
        }
        else
        {
            observerComplete = false;
        }
    }

    private void UpdatePeak()
    {
        var total = checked(currentTemporaryBytes + currentStagingBytes);
        if (total <= peakTotalBytes)
        {
            return;
        }
        peakTemporaryBytes = currentTemporaryBytes;
        peakStagingBytes = currentStagingBytes;
        peakTotalBytes = total;
    }

    private static string ToKey(string role, string root, string fullPath) =>
        $"{role}/{Path.GetRelativePath(root, fullPath).Replace('\\', '/')}";

    private sealed record BarrierRegistration(
        string Path,
        ManualResetEventSlim Acknowledgement);
}
