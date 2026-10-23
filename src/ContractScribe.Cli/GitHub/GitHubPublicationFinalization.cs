namespace ContractScribe.Cli;

// One host window after model work. Its owner outlives checkpoint disposal so
// independent mutation readback and claim cleanup share the same deadline.
internal sealed class GitHubPublicationFinalization(
    CancellationToken caller, TimeProvider? clock = null) : IDisposable
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);
    private CancellationTokenSource? deadline;
    private CancellationTokenSource? execution;
    private CancellationTokenRegistration ownerStop, callerStop, deadlineStop;
    private int firstStop; // caller=1, owner=2, deadline=3; immutable once selected.

    internal CancellationToken Begin(CancellationToken ownedExecution)
    {
        if (execution is not null) return execution.Token;
        if (caller.IsCancellationRequested) firstStop = 1;
        else if (ownedExecution.IsCancellationRequested) firstStop = 2;
        deadline = new CancellationTokenSource(DefaultTimeout, clock ?? TimeProvider.System);
        execution = CancellationTokenSource.CreateLinkedTokenSource(caller, ownedExecution, deadline.Token);
        // Register after the execution link so the source is selected before
        // propagating cancellation to IO; later stop signals cannot replace it.
        ownerStop = ownedExecution.Register(() => Interlocked.CompareExchange(ref firstStop, 2, 0));
        callerStop = caller.Register(() => Interlocked.CompareExchange(ref firstStop, 1, 0));
        deadlineStop = deadline.Token.Register(() => Interlocked.CompareExchange(ref firstStop, 3, 0));
        return execution.Token;
    }

    internal CancellationToken DeadlineToken => deadline?.Token ?? default;
    internal bool TimedOut => Volatile.Read(ref firstStop) == 3;

    public void Dispose()
    {
        execution?.Dispose();
        ownerStop.Dispose();
        callerStop.Dispose();
        deadlineStop.Dispose();
        deadline?.Dispose();
    }
}
