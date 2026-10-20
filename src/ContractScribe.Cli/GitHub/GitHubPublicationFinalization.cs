namespace ContractScribe.Cli;

// One host window after model work. Its owner outlives checkpoint disposal so
// independent mutation readback and claim cleanup share the same deadline.
internal sealed class GitHubPublicationFinalization(
    CancellationToken caller, TimeProvider? clock = null) : IDisposable
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);
    private CancellationTokenSource? deadline;
    private CancellationTokenSource? execution;
    private CancellationTokenRegistration ownerStop;
    private int ownerStoppedBeforeDeadline;

    internal CancellationToken Begin(CancellationToken ownedExecution)
    {
        if (execution is not null) return execution.Token;
        if (ownedExecution.IsCancellationRequested) ownerStoppedBeforeDeadline = 1;
        deadline = new CancellationTokenSource(DefaultTimeout, clock ?? TimeProvider.System);
        var hostDeadline = deadline;
        execution = CancellationTokenSource.CreateLinkedTokenSource(caller, ownedExecution, deadline.Token);
        // Observe the owner stop before propagating it through our linked token.
        ownerStop = ownedExecution.Register(() =>
        {
            if (!hostDeadline.IsCancellationRequested) Interlocked.Exchange(ref ownerStoppedBeforeDeadline, 1);
        });
        return execution.Token;
    }

    internal CancellationToken DeadlineToken => deadline?.Token ?? default;
    internal bool TimedOut => deadline?.IsCancellationRequested == true
        && !caller.IsCancellationRequested && Volatile.Read(ref ownerStoppedBeforeDeadline) == 0;

    public void Dispose()
    {
        execution?.Dispose();
        ownerStop.Dispose();
        deadline?.Dispose();
    }
}
