namespace ContractScribe.Roslyn.IntegrationTests;

internal sealed record LoaderFixtureTemplate(
    string Root,
    string PreparationId,
    string ShapeKey,
    string Category)
{
    public string OwnershipRoot { get; init; } = Root;
}

internal sealed class LoaderFixtureCache : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly HashSet<string> disabled = new(StringComparer.Ordinal);
    private readonly Func<CancellationToken, Task>? beforePreparation;
    private readonly Func<LoaderFixtureTemplate, CancellationToken, Task>? beforePublication;
    private readonly Action<string> deleteDirectory;
    private readonly TimeSpan preparationTimeout;
    private bool disposed;
    private int preparationCount;

    public LoaderFixtureCache(
        TimeSpan? preparationTimeout = null,
        Func<CancellationToken, Task>? beforePreparation = null,
        Func<LoaderFixtureTemplate, CancellationToken, Task>? beforePublication = null,
        Action<string>? deleteDirectory = null)
    {
        this.preparationTimeout = preparationTimeout ?? TimeSpan.FromMinutes(5);
        this.beforePreparation = beforePreparation;
        this.beforePublication = beforePublication;
        this.deleteDirectory = deleteDirectory ?? DeleteDirectoryStrict;
    }

    public int PreparationCount => Volatile.Read(ref preparationCount);

    public async Task<TResult> GetOrPrepareAndUseAsync<TResult>(
        string shapeKey,
        Func<string, CancellationToken, Task<LoaderFixtureTemplate>> prepare,
        Func<LoaderFixtureTemplate, CancellationToken, Task<TResult>> use,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            Entry? entry = null;
            LoaderFixtureTemplate? published = null;
            Task? cleanupBarrier = null;
            var startPreparation = false;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (disabled.Contains(shapeKey))
                {
                    if (entries.TryGetValue(shapeKey, out entry))
                    {
                        cleanupBarrier = entry.TerminalCleanup.Task;
                    }
                    else
                    {
                        throw new LoaderFixtureCacheDisabledException(shapeKey);
                    }
                }
                else if (!entries.TryGetValue(shapeKey, out entry))
                {
                    entry = new Entry(shapeKey, CreateOwnedRoot());
                    entries.Add(shapeKey, entry);
                    entry.WaiterCount = 1;
                    startPreparation = true;
                }
                else
                {
                    switch (entry.State)
                    {
                        case EntryState.Published:
                            entry.WaiterCount++;
                            published = entry.Template!;
                            break;
                        case EntryState.Abandoning:
                        case EntryState.Disposing:
                        case EntryState.CleanupFailed:
                            cleanupBarrier = entry.TerminalCleanup.Task;
                            break;
                        default:
                            entry.WaiterCount++;
                            break;
                    }
                }
            }

            if (cleanupBarrier is not null)
            {
                await cleanupBarrier.WaitAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (startPreparation)
            {
                _ = PrepareEntryAsync(entry!, prepare);
            }

            try
            {
                var template = published
                    ?? await entry!.Completion.Task
                        .WaitAsync(cancellationToken)
                        .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return await use(template, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                DetachWaiter(entry!);
            }
        }
    }

    public async Task DisableAsync(string shapeKey)
    {
        Entry? cleanup = null;
        Task? terminal = null;
        lock (gate)
        {
            disabled.Add(shapeKey);
            if (!entries.TryGetValue(shapeKey, out var entry))
            {
                return;
            }

            terminal = entry.TerminalCleanup.Task;
            switch (entry.State)
            {
                case EntryState.Preparing:
                case EntryState.Publishing:
                    entry.State = EntryState.Disposing;
                    entry.PreparationCancellation.Cancel();
                    break;
                case EntryState.Published:
                    entry.State = EntryState.Disposing;
                    if (entry.WaiterCount == 0 && !entry.CleanupStarted)
                    {
                        entry.CleanupStarted = true;
                        cleanup = entry;
                    }
                    break;
            }
        }

        if (cleanup is not null)
        {
            _ = CleanupPublishedEntryAsync(cleanup);
        }
        if (terminal is not null)
        {
            await terminal.ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Entry[] snapshot;
        var cleanup = new List<Entry>();
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            snapshot = entries.Values.ToArray();
            foreach (var entry in snapshot)
            {
                switch (entry.State)
                {
                    case EntryState.Preparing:
                    case EntryState.Publishing:
                        entry.State = EntryState.Disposing;
                        entry.PreparationCancellation.Cancel();
                        break;
                    case EntryState.Published:
                        entry.State = EntryState.Disposing;
                        if (entry.WaiterCount == 0 && !entry.CleanupStarted)
                        {
                            entry.CleanupStarted = true;
                            cleanup.Add(entry);
                        }
                        break;
                }
            }
        }

        foreach (var entry in cleanup)
        {
            _ = CleanupPublishedEntryAsync(entry);
        }
        if (snapshot.Length > 0)
        {
            await Task.WhenAll(snapshot.Select(entry => entry.TerminalCleanup.Task))
                .ConfigureAwait(false);
        }
    }

    internal void DisposeTemplatesAtProcessExit()
    {
        Entry[] snapshot;
        lock (gate)
        {
            snapshot = entries.Values.ToArray();
        }
        foreach (var entry in snapshot)
        {
            DeleteDirectoryBestEffort(entry.OwnedRoot);
        }
    }

    private async Task PrepareEntryAsync(
        Entry entry,
        Func<string, CancellationToken, Task<LoaderFixtureTemplate>> prepare)
    {
        Interlocked.Increment(ref preparationCount);
        using var timeout = new CancellationTokenSource(preparationTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            entry.PreparationCancellation.Token,
            timeout.Token);
        LoaderFixtureTemplate? localTemplate = null;
        try
        {
            if (beforePreparation is not null)
            {
                await beforePreparation(linked.Token).ConfigureAwait(false);
            }

            localTemplate = await prepare(entry.OwnedRoot, linked.Token).ConfigureAwait(false);
            if (!PathEquals(localTemplate.OwnershipRoot, entry.OwnedRoot))
            {
                throw new InvalidOperationException(
                    $"Prepared fixture ownership root '{localTemplate.OwnershipRoot}' "
                    + $"did not match the cache-owned root '{entry.OwnedRoot}'.");
            }
            linked.Token.ThrowIfCancellationRequested();
            if (beforePublication is not null)
            {
                await beforePublication(localTemplate, linked.Token).ConfigureAwait(false);
            }
            linked.Token.ThrowIfCancellationRequested();

            LoaderFixtureTemplate published;
            lock (gate)
            {
                if (entry.State is EntryState.Abandoning or EntryState.Disposing)
                {
                    throw new OperationCanceledException(linked.Token);
                }
                entry.State = EntryState.Publishing;
                entry.Template = localTemplate;
                localTemplate = null;
                entry.State = EntryState.Published;
                published = entry.Template;
            }
            entry.Completion.TrySetResult(published);
        }
        catch (Exception exception)
        {
            var reported = timeout.IsCancellationRequested
                && !entry.PreparationCancellation.IsCancellationRequested
                ? new TimeoutException(
                    $"Fixture preparation exceeded {preparationTimeout}.",
                    exception)
                : exception;
            CompleteFailedPreparation(entry, reported);
        }
    }

    private void CompleteFailedPreparation(
        Entry entry,
        Exception failure)
    {
        Exception? cleanupFailure = null;
        try
        {
            deleteDirectory(entry.OwnedRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            cleanupFailure = exception;
        }

        var reported = cleanupFailure is null
            ? failure
            : new AggregateException(
                "Fixture preparation failed and strict cleanup did not complete.",
                failure,
                cleanupFailure);
        lock (gate)
        {
            entry.State = cleanupFailure is null
                ? EntryState.TerminalFailure
                : EntryState.CleanupFailed;
            if (cleanupFailure is null)
            {
                entries.Remove(entry.ShapeKey);
            }
        }
        entry.Completion.TrySetException(reported);
        if (cleanupFailure is null)
        {
            entry.TerminalCleanup.TrySetResult();
        }
        else
        {
            entry.TerminalCleanup.TrySetException(reported);
        }
    }

    private async Task CleanupPublishedEntryAsync(Entry entry)
    {
        Exception? failure = null;
        try
        {
            deleteDirectory(entry.OwnedRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failure = exception;
        }

        lock (gate)
        {
            entry.State = failure is null
                ? EntryState.TerminalFailure
                : EntryState.CleanupFailed;
            if (failure is null)
            {
                entries.Remove(entry.ShapeKey);
            }
        }
        if (failure is null)
        {
            entry.TerminalCleanup.TrySetResult();
        }
        else
        {
            entry.TerminalCleanup.TrySetException(failure);
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private void DetachWaiter(Entry entry)
    {
        Entry? cleanup = null;
        lock (gate)
        {
            if (entry.WaiterCount > 0)
            {
                entry.WaiterCount--;
            }
            if (entry.WaiterCount != 0)
            {
                return;
            }

            if (entry.State is EntryState.Preparing or EntryState.Publishing)
            {
                entry.State = EntryState.Abandoning;
                entry.PreparationCancellation.Cancel();
            }
            else if (entry.State == EntryState.Disposing && !entry.CleanupStarted)
            {
                entry.CleanupStarted = true;
                cleanup = entry;
            }
        }
        if (cleanup is not null)
        {
            _ = CleanupPublishedEntryAsync(cleanup);
        }
    }

    private static void DeleteDirectoryStrict(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        if (Directory.Exists(path))
        {
            throw new IOException($"Fixture directory still exists after cleanup: {path}");
        }
    }

    private static string CreateOwnedRoot() => Path.Combine(
        Path.GetTempPath(),
        "contract-scribe-issue80",
        $"template-owner-{Guid.NewGuid():N}");

    private static bool PathEquals(string left, string right) => string.Equals(
        Path.GetFullPath(left),
        Path.GetFullPath(right),
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);

    private static void DeleteDirectoryBestEffort(string path)
    {
        try
        {
            DeleteDirectoryStrict(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class Entry
    {
        public Entry(string shapeKey, string ownedRoot)
        {
            ShapeKey = shapeKey;
            OwnedRoot = ownedRoot;
            _ = Completion.Task.ContinueWith(
                task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            _ = TerminalCleanup.Task.ContinueWith(
                task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        public string ShapeKey { get; }

        public string OwnedRoot { get; }

        public CancellationTokenSource PreparationCancellation { get; } = new();

        public TaskCompletionSource<LoaderFixtureTemplate> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource TerminalCleanup { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public EntryState State { get; set; } = EntryState.Preparing;

        public int WaiterCount { get; set; }

        public bool CleanupStarted { get; set; }

        public LoaderFixtureTemplate? Template { get; set; }
    }

    private enum EntryState
    {
        Preparing,
        Publishing,
        Published,
        Abandoning,
        Disposing,
        TerminalFailure,
        CleanupFailed,
    }
}

internal sealed class LoaderFixtureCacheDisabledException(string shapeKey)
    : InvalidOperationException($"Fixture cache reuse is disabled for shape {shapeKey}.");
