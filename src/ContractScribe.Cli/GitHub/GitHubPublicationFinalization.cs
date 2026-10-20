namespace ContractScribe.Cli;

// One host window after model work. Its owner outlives checkpoint disposal so
// independent mutation readback and claim cleanup share the same deadline.
internal sealed class GitHubPublicationFinalization(
    CancellationToken caller, TimeProvider? clock = null) : IDisposable
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);
    private CancellationTokenSource? deadline;
    private CancellationTokenSource? execution;

    internal CancellationToken Begin(CancellationToken ownedExecution)
    {
        if (execution is not null) return execution.Token;
        deadline = new CancellationTokenSource(DefaultTimeout, clock ?? TimeProvider.System);
        execution = CancellationTokenSource.CreateLinkedTokenSource(caller, ownedExecution, deadline.Token);
        return execution.Token;
    }

    internal CancellationToken DeadlineToken => deadline?.Token ?? default;
    internal bool TimedOut => deadline?.IsCancellationRequested == true && !caller.IsCancellationRequested;

    public void Dispose()
    {
        execution?.Dispose();
        deadline?.Dispose();
    }
}
