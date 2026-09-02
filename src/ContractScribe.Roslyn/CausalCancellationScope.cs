namespace ContractScribe.Roslyn;

internal sealed class ProductionDeadlineSource : IDisposable
{
    private readonly CancellationTokenSource source = new();

    public CancellationToken Token => source.Token;

    public void Arm(TimeSpan deadline) => source.CancelAfter(deadline);

    public void Trigger() => source.Cancel();

    public void Retire() => source.CancelAfter(Timeout.InfiniteTimeSpan);

    public void Dispose() => source.Dispose();
}

internal sealed class CausalCallerSignal : IDisposable
{
    private readonly object gate = new();
    private readonly CancellationToken sourceToken;
    private readonly Action acceptCause;
    private readonly CancellationTokenSource propagation = new();
    private readonly CancellationTokenRegistration registration;
    private bool signalled;
    private bool disposed;

    public CausalCallerSignal(CancellationToken sourceToken, Action acceptCause)
    {
        this.sourceToken = sourceToken;
        this.acceptCause = acceptCause ?? throw new ArgumentNullException(nameof(acceptCause));
        registration = sourceToken.Register(ObserveCancellation);
    }

    public CancellationToken Token => propagation.Token;

    public void ObserveIfCancellationRequested()
    {
        if (sourceToken.IsCancellationRequested)
        {
            ObserveCancellation();
        }
    }

    public void Dispose()
    {
        registration.Dispose();
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            propagation.Dispose();
        }
    }

    private void ObserveCancellation()
    {
        lock (gate)
        {
            if (signalled || disposed)
            {
                return;
            }

            acceptCause();
            signalled = true;
            propagation.Cancel();
        }
    }
}

internal sealed class CausalDeadlineScope : IDisposable
{
    private readonly object gate = new();
    private readonly Action observeCallerCancellation;
    private readonly Action acceptDeadlineCause;
    private readonly ProductionDeadlineSource deadline;
    private readonly CancellationTokenSource propagation = new();
    private readonly CancellationTokenRegistration parentRegistration;
    private readonly CancellationTokenRegistration deadlineRegistration;
    private bool signalled;
    private bool disposed;

    public CausalDeadlineScope(
        CancellationToken parentToken,
        ProductionDeadlineSource deadline,
        TimeSpan deadlineValue,
        Action observeCallerCancellation,
        Action acceptDeadlineCause)
    {
        this.deadline = deadline ?? throw new ArgumentNullException(nameof(deadline));
        this.observeCallerCancellation = observeCallerCancellation
            ?? throw new ArgumentNullException(nameof(observeCallerCancellation));
        this.acceptDeadlineCause = acceptDeadlineCause
            ?? throw new ArgumentNullException(nameof(acceptDeadlineCause));

        parentRegistration = parentToken.Register(SignalFromParent);
        deadlineRegistration = deadline.Token.Register(SignalFromDeadline);
        deadline.Arm(deadlineValue);
    }

    public CancellationToken Token => propagation.Token;

    public void RetireDeadline()
    {
        deadline.Retire();
        deadlineRegistration.Dispose();
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
        }

        deadlineRegistration.Dispose();
        parentRegistration.Dispose();
        deadline.Dispose();
        propagation.Dispose();
    }

    private void SignalFromParent() => Signal(acceptCause: null);

    private void SignalFromDeadline()
    {
        observeCallerCancellation();
        Signal(acceptDeadlineCause);
    }

    private void Signal(Action? acceptCause)
    {
        lock (gate)
        {
            if (signalled || disposed)
            {
                return;
            }

            acceptCause?.Invoke();
            signalled = true;
            propagation.Cancel();
        }
    }
}
