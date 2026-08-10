namespace ContractScribe.Roslyn.IntegrationTests;

internal sealed record LoaderFixtureTemplate(
    string Root,
    string PreparationId,
    string ShapeKey,
    string Category);

internal sealed class LoaderFixtureCache : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly HashSet<string> disabled = new(StringComparer.Ordinal);
    private readonly Func<CancellationToken, Task>? beforePreparation;
    private readonly TimeSpan preparationTimeout;
    private bool disposed;
    private int preparationCount;

    public LoaderFixtureCache(
        TimeSpan? preparationTimeout = null,
        Func<CancellationToken, Task>? beforePreparation = null)
    {
        this.preparationTimeout = preparationTimeout ?? TimeSpan.FromMinutes(5);
        this.beforePreparation = beforePreparation;
    }

    public int PreparationCount => Volatile.Read(ref preparationCount);

    public bool IsDisabled(string shapeKey)
    {
        lock (gate)
        {
            return disabled.Contains(shapeKey);
        }
    }

    public async Task<LoaderFixtureTemplate> GetOrPrepareAsync(
        string shapeKey,
        Func<CancellationToken, Task<LoaderFixtureTemplate>> prepare,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            Entry? entry = null;
            Task? cleanupBarrier = null;
            var startPreparation = false;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (disabled.Contains(shapeKey))
                {
                    throw new LoaderFixtureCacheDisabledException(shapeKey);
                }

                if (!entries.TryGetValue(shapeKey, out entry))
                {
                    entry = new Entry(shapeKey);
                    entries.Add(shapeKey, entry);
                    entry.WaiterCount = 1;
                    startPreparation = true;
                }
                else
                {
                    switch (entry.State)
                    {
                        case EntryState.Published:
                            return entry.Template!;
                        case EntryState.Abandoning:
                        case EntryState.TerminalFailure:
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
                return await entry!.Completion.Task
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                DetachWaiter(entry!);
            }
        }
    }

    public void Disable(string shapeKey)
    {
        lock (gate)
        {
            disabled.Add(shapeKey);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Entry[] snapshot;
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
                if (entry.State is EntryState.Preparing or EntryState.Publishing)
                {
                    entry.State = EntryState.Abandoning;
                    entry.PreparationCancellation.Cancel();
                }
            }
        }

        var unfinished = snapshot
            .Where(entry => entry.State is not EntryState.Published)
            .Select(entry => entry.TerminalCleanup.Task)
            .ToArray();
        if (unfinished.Length > 0)
        {
            await Task.WhenAll(unfinished).ConfigureAwait(false);
        }

        foreach (var entry in snapshot)
        {
            if (entry.Template is not null)
            {
                DeleteDirectory(entry.Template.Root);
            }
            entry.PreparationCancellation.Dispose();
            entry.TerminalCleanup.TrySetResult();
        }
        lock (gate)
        {
            entries.Clear();
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
            if (entry.Template is not null)
            {
                DeleteDirectory(entry.Template.Root);
            }
        }
    }

    private async Task PrepareEntryAsync(
        Entry entry,
        Func<CancellationToken, Task<LoaderFixtureTemplate>> prepare)
    {
        Interlocked.Increment(ref preparationCount);
        using var timeout = new CancellationTokenSource(preparationTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            entry.PreparationCancellation.Token,
            timeout.Token);
        try
        {
            if (beforePreparation is not null)
            {
                await beforePreparation(linked.Token).ConfigureAwait(false);
            }

            var template = await prepare(linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (entry.State == EntryState.Abandoning)
                {
                    throw new OperationCanceledException(linked.Token);
                }
                entry.State = EntryState.Publishing;
                entry.Template = template;
                entry.State = EntryState.Published;
            }
            entry.Completion.TrySetResult(template);
        }
        catch (Exception exception)
        {
            var reported = timeout.IsCancellationRequested
                && !entry.PreparationCancellation.IsCancellationRequested
                ? new TimeoutException(
                    $"Fixture preparation exceeded {preparationTimeout}.",
                    exception)
                : exception;
            if (entry.Template is not null)
            {
                DeleteDirectory(entry.Template.Root);
            }
            lock (gate)
            {
                entry.State = EntryState.TerminalFailure;
                entries.Remove(entry.ShapeKey);
            }
            entry.Completion.TrySetException(reported);
            entry.TerminalCleanup.TrySetResult();
        }
    }

    private void DetachWaiter(Entry entry)
    {
        lock (gate)
        {
            if (entry.WaiterCount > 0)
            {
                entry.WaiterCount--;
            }
            if (entry.WaiterCount == 0
                && entry.State is EntryState.Preparing or EntryState.Publishing)
            {
                entry.State = EntryState.Abandoning;
                entry.PreparationCancellation.Cancel();
            }
        }
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
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
        public Entry(string shapeKey)
        {
            ShapeKey = shapeKey;
            _ = Completion.Task.ContinueWith(
                task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        public string ShapeKey { get; }

        public CancellationTokenSource PreparationCancellation { get; } = new();

        public TaskCompletionSource<LoaderFixtureTemplate> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource TerminalCleanup { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public EntryState State { get; set; } = EntryState.Preparing;

        public int WaiterCount { get; set; }

        public LoaderFixtureTemplate? Template { get; set; }
    }

    private enum EntryState
    {
        Preparing,
        Publishing,
        Published,
        Abandoning,
        TerminalFailure,
    }
}

internal sealed class LoaderFixtureCacheDisabledException(string shapeKey)
    : InvalidOperationException($"Fixture cache reuse is disabled for shape {shapeKey}.");
