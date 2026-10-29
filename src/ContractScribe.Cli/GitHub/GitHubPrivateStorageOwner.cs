using ContractScribe.GitHub.NativeGit;

namespace ContractScribe.Cli;

// The composition root owns storage validation and lifetime. No target path or
// consumer option is accepted as a Git workspace.
internal sealed class GitHubPrivateStorageOwner : IDisposable
{
    private readonly string directory;
    private readonly FileCampaignCheckpointStore.GitStorageDirectory retained;
    private readonly CancellationTokenSource lifetime;
    private bool disposed;
    internal GitHubPrivateStorage Storage { get; }

    private GitHubPrivateStorageOwner(string directory, FileCampaignCheckpointStore.GitStorageDirectory retained,
        CancellationToken parent)
    {
        this.directory = directory;
        this.retained = retained;
        parent.ThrowIfCancellationRequested();
        // Invocation cancellation is still passed to each operation. Storage
        // stays held for the existing bounded finalization/recovery interval.
        lifetime = new CancellationTokenSource();
        Storage = new(directory, retained.Validate, lifetime.Token);
    }

    internal static GitHubPrivateStorageOwner Create(string repositoryRoot, CancellationToken parent)
    {
        parent.ThrowIfCancellationRequested();
        var retained = FileCampaignCheckpointStore.CreateGitStorageDirectory(repositoryRoot, Path.GetTempPath());
        try { return new(retained.Path, retained, parent); }
        catch { retained.Dispose(); throw; }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        try
        {
            // Native children have already stopped and released their stores.
            // A substituted/unverifiable path is never recursively removed.
            retained.Validate();
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        { }
        finally { retained.Dispose(); lifetime.Dispose(); }
    }
}
