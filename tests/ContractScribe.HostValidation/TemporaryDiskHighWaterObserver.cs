namespace ContractScribe.HostValidation;

public sealed class TemporaryDiskHighWaterObserver : IDisposable
{
    private const string BarrierPrefix = ".contractscribe-hv-observer-barrier-";

    private readonly object sync = new();
    private readonly string temporaryWorkRoot;
    private readonly string outputStagingRoot;
    private readonly string freezeSentinelName =
        $".contractscribe-hv-freeze-{Guid.NewGuid():N}";
    private readonly string releaseSentinelName =
        $".contractscribe-hv-release-{Guid.NewGuid():N}";
    private readonly Dictionary<string, long> currentLengths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ManualResetEventSlim> barrierAcknowledgements =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, ManualResetEventSlim> freezeAcknowledgements =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, ManualResetEventSlim> releaseAcknowledgements =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> frozenRoles = new(StringComparer.Ordinal);
    private readonly HashSet<string> releasedRoles = new(StringComparer.Ordinal);
    private readonly object callbackSync = new();
    private readonly FileSystemWatcher[] watchers;
    private int activeCallbacks;
    private bool callbacksAccepting = true;
    private bool observerComplete = true;
    private bool retentionBreach;
    private bool releaseIssued;
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
        freezeAcknowledgements["temporary-work"] = new(false);
        freezeAcknowledgements["output-staging"] = new(false);
        releaseAcknowledgements["temporary-work"] = new(false);
        releaseAcknowledgements["output-staging"] = new(false);
        watchers =
        [
            CreateWatcher(this.temporaryWorkRoot, "temporary-work"),
            CreateWatcher(this.outputStagingRoot, "output-staging")
        ];
        Synchronize();
    }

    public TemporaryDiskGateContract GateContract =>
        new(
            temporaryWorkRoot,
            outputStagingRoot,
            freezeSentinelName,
            releaseSentinelName);

    public TemporaryDiskHighWaterEvidence CaptureAndRelease(
        Action release,
        MonotonicDeadline deadline)
    {
        WaitForBoundary(freezeAcknowledgements, deadline);
        Synchronize(deadline);
        TemporaryDiskHighWaterEvidence evidence;
        lock (sync)
        {
            if (frozenRoles.Count != 2)
            {
                observerComplete = false;
            }
            evidence = new(
                "peak-concurrent-logical-file-bytes",
                "contractscribe-temporary-work-and-output-staging.v1",
                "pre-subject-to-temporary-disk-high-water.v1",
                peakTemporaryBytes,
                peakStagingBytes,
                peakTotalBytes,
                observerComplete,
                retentionBreach);
            releaseIssued = true;
        }
        release();
        WaitForBoundary(releaseAcknowledgements, deadline);
        Synchronize(deadline);
        lock (sync)
        {
            if (releasedRoles.Count != 2)
            {
                observerComplete = false;
            }
            observationClosed = true;
            return evidence with
            {
                ObserverComplete = evidence.ObserverComplete && observerComplete,
                RetentionBreach = evidence.RetentionBreach || retentionBreach
            };
        }
    }

    public void Synchronize()
    {
        Synchronize(MonotonicDeadline.Start(TimeSpan.FromSeconds(10)));
    }

    private void Synchronize(MonotonicDeadline deadline)
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
            if (!WaitForSignal(barrier.Acknowledgement, deadline))
            {
                lock (sync)
                {
                    observerComplete = false;
                }
            }
        }
        if (!WaitForCallbacksToDrain(deadline))
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
        _ = WaitForCallbacksToDrain(
            MonotonicDeadline.Start(TimeSpan.FromSeconds(10)));
        foreach (var watcher in watchers)
        {
            watcher.Dispose();
        }
        foreach (var acknowledgement in barrierAcknowledgements.Values)
        {
            acknowledgement.Dispose();
        }
        foreach (var acknowledgement in freezeAcknowledgements.Values)
        {
            acknowledgement.Dispose();
        }
        foreach (var acknowledgement in releaseAcknowledgements.Values)
        {
            acknowledgement.Dispose();
        }
        DeleteSentinel(temporaryWorkRoot, freezeSentinelName);
        DeleteSentinel(temporaryWorkRoot, releaseSentinelName);
        DeleteSentinel(outputStagingRoot, freezeSentinelName);
        DeleteSentinel(outputStagingRoot, releaseSentinelName);
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
            ExecuteCallback(() => ObserveLength(
                role,
                root,
                args.FullPath,
                isMutationEvent: true));
        watcher.Changed += (_, args) =>
            ExecuteCallback(() => ObserveLength(
                role,
                root,
                args.FullPath,
                isMutationEvent: true));
        watcher.Deleted += (_, args) =>
            ExecuteCallback(() => ObserveRemoval(role, root, args.FullPath));
        watcher.Renamed += (_, args) =>
            ExecuteCallback(() =>
            {
                ObserveRemoval(role, root, args.OldFullPath);
                ObserveLength(
                    role,
                    root,
                    args.FullPath,
                    isMutationEvent: true);
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

    private bool WaitForCallbacksToDrain(MonotonicDeadline deadline)
    {
        lock (callbackSync)
        {
            while (activeCallbacks != 0)
            {
                var remaining = deadline.Remaining;
                if (remaining == TimeSpan.Zero
                    || !Monitor.Wait(callbackSync, remaining))
                {
                    return false;
                }
            }
            return true;
        }
    }

    private void WaitForBoundary(
        IReadOnlyDictionary<string, ManualResetEventSlim> acknowledgements,
        MonotonicDeadline deadline)
    {
        foreach (var acknowledgement in acknowledgements.Values)
        {
            if (!WaitForSignal(acknowledgement, deadline))
            {
                lock (sync)
                {
                    observerComplete = false;
                }
            }
        }
    }

    private static bool WaitForSignal(
        ManualResetEventSlim signal,
        MonotonicDeadline deadline)
    {
        var remaining = deadline.Remaining;
        return remaining != TimeSpan.Zero && signal.Wait(remaining);
    }

    private void ObserveFreezeSentinel(string role, string fullPath)
    {
        if (releaseIssued || !IsZeroLengthFile(fullPath))
        {
            observerComplete = false;
            retentionBreach = true;
            return;
        }
        if (frozenRoles.Add(role))
        {
            freezeAcknowledgements[role].Set();
        }
    }

    private void ObserveReleaseSentinel(string role, string fullPath)
    {
        if (!releaseIssued
            || !frozenRoles.Contains(role)
            || !IsZeroLengthFile(fullPath))
        {
            observerComplete = false;
            retentionBreach = true;
            return;
        }
        if (releasedRoles.Add(role))
        {
            releaseAcknowledgements[role].Set();
        }
    }

    private static bool IsZeroLengthFile(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length == 0;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            return false;
        }
    }

    private void ObserveLength(
        string role,
        string root,
        string fullPath,
        bool isMutationEvent)
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
            if (key == ToKey(role, root, Path.Join(root, freezeSentinelName)))
            {
                ObserveFreezeSentinel(role, fullPath);
                return;
            }
            if (key == ToKey(role, root, Path.Join(root, releaseSentinelName)))
            {
                ObserveReleaseSentinel(role, fullPath);
                return;
            }
            if (releasedRoles.Contains(role))
            {
                return;
            }
            if (frozenRoles.Contains(role) && isMutationEvent)
            {
                retentionBreach = true;
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
            if (key == ToKey(role, root, Path.Join(root, freezeSentinelName))
                || key == ToKey(role, root, Path.Join(root, releaseSentinelName)))
            {
                observerComplete = false;
                retentionBreach = true;
                return;
            }
            if (releasedRoles.Contains(role))
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
                         !barrierAcknowledgements.ContainsKey(candidate.Key)
                         && candidate.Key != ToKey(
                             role,
                             root,
                             Path.Join(root, freezeSentinelName))
                         && candidate.Key != ToKey(
                             role,
                             root,
                             Path.Join(root, releaseSentinelName))))
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
                     .Where(key => !current.ContainsKey(key)
                         && !releasedRoles.Contains(RoleFromKey(key)))
                     .ToArray())
        {
            retentionBreach = true;
            RemoveLength(missing);
        }
        foreach (var pair in current)
        {
            var role = RoleFromKey(pair.Key);
            if (releasedRoles.Contains(role))
            {
                continue;
            }
            var existed = currentLengths.TryGetValue(pair.Key, out var previous);
            if (frozenRoles.Contains(role)
                && (!existed || previous != pair.Value))
            {
                retentionBreach = true;
            }
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

    private static string RoleFromKey(string key)
    {
        var separator = key.IndexOf('/');
        return separator < 0 ? string.Empty : key[..separator];
    }

    private static void DeleteSentinel(string root, string name)
    {
        try
        {
            File.Delete(Path.Join(root, name));
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {
        }
    }

    private sealed record BarrierRegistration(
        string Path,
        ManualResetEventSlim Acknowledgement);
}
