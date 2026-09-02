namespace ContractScribe.Roslyn;

internal interface ICausalInterruptionSignal
{
    CancellationToken Token { get; }

    long? OccurrenceSequence { get; }

    void ObserveIfSourceRequested();

    void EnsureCauseAccepted();
}

internal sealed class CausalInterruptionArbiter
{
    private readonly object gate = new();
    private readonly List<Source> sources = [];
    private long nextSequence;
    private Source? accepted;

    public Source RegisterSource(
        Func<bool> isRequested,
        Action acceptCause,
        int tiePriority)
    {
        var source = new Source(this, isRequested, acceptCause, tiePriority);
        lock (gate)
        {
            sources.Add(source);
        }
        return source;
    }

    public void AcceptEarliest()
    {
        lock (gate)
        {
            ReserveRequestedSourcesLocked();
            if (accepted is not null)
            {
                return;
            }

            var winner = sources
                .Where(source => !source.Disposed && source.ReservedSequence is not null)
                .OrderBy(source => source.ReservedSequence)
                .ThenBy(source => source.TiePriority)
                .FirstOrDefault();
            if (winner is null)
            {
                return;
            }

            accepted = winner;
            winner.AcceptCause();
        }
    }

    private bool Reserve(Source source)
    {
        lock (gate)
        {
            var wasReserved = source.ReservedSequence is not null;
            ReserveRequestedSourcesLocked();
            if (!source.Disposed && source.ReservedSequence is null)
            {
                source.ReservedSequence = ++nextSequence;
            }
            return !wasReserved && source.ReservedSequence is not null;
        }
    }

    private void Unregister(Source source)
    {
        lock (gate)
        {
            source.Disposed = true;
            sources.Remove(source);
        }
    }

    private void ReserveRequestedSourcesLocked()
    {
        foreach (var source in sources
                     .Where(source => !source.Disposed && source.ReservedSequence is null && source.IsRequested())
                     .OrderBy(source => source.TiePriority))
        {
            source.ReservedSequence = ++nextSequence;
        }
    }

    private long? GetSequence(Source source)
    {
        lock (gate)
        {
            return source.ReservedSequence;
        }
    }

    internal sealed class Source : IDisposable
    {
        private readonly CausalInterruptionArbiter owner;
        private readonly Func<bool> isRequested;
        private readonly Action acceptCause;

        public Source(
            CausalInterruptionArbiter owner,
            Func<bool> isRequested,
            Action acceptCause,
            int tiePriority)
        {
            this.owner = owner;
            this.isRequested = isRequested;
            this.acceptCause = acceptCause;
            TiePriority = tiePriority;
        }

        internal long? ReservedSequence { get; set; }

        public long? Sequence => owner.GetSequence(this);

        public int TiePriority { get; }

        public bool Disposed { get; set; }

        public bool Reserve() => owner.Reserve(this);

        public bool IsRequested() => isRequested();

        public void AcceptCause() => acceptCause();

        public void Dispose() => owner.Unregister(this);
    }
}

internal sealed class ProductionDeadlineSource : IDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource scheduler = new();
    private readonly CancellationTokenRegistration registration;
    private CausalInterruptionArbiter? arbiter;
    private CausalInterruptionArbiter.Source? causalSource;
    private Action? observeParent;
    private Action? signal;
    private Action? afterOccurrenceReserved;
    private bool armed;
    private bool retired;
    private bool disposed;

    public ProductionDeadlineSource()
    {
        registration = scheduler.Token.Register(SignalFromScheduler);
    }

    public long? OccurrenceSequence => causalSource?.Sequence;

    public void Configure(
        CausalInterruptionArbiter arbiter,
        Action acceptDeadlineCause,
        Action observeParent,
        Action signal,
        Action? afterOccurrenceReserved)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (causalSource is not null)
            {
                throw new InvalidOperationException("A production deadline source can be configured only once.");
            }

            this.arbiter = arbiter ?? throw new ArgumentNullException(nameof(arbiter));
            causalSource = arbiter.RegisterSource(
                IsActiveAndRequested,
                acceptDeadlineCause ?? throw new ArgumentNullException(nameof(acceptDeadlineCause)),
                tiePriority: 1);
            this.observeParent = observeParent
                ?? throw new ArgumentNullException(nameof(observeParent));
            this.signal = signal
                ?? throw new ArgumentNullException(nameof(signal));
            this.afterOccurrenceReserved = afterOccurrenceReserved;
        }
    }

    public void Arm(TimeSpan deadline)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (causalSource is null)
            {
                throw new InvalidOperationException("The production deadline source must be configured before it is armed.");
            }
            if (armed)
            {
                throw new InvalidOperationException("A production deadline source can be armed only once.");
            }

            armed = true;
            scheduler.CancelAfter(deadline);
        }
    }

    public void Trigger() => scheduler.Cancel();

    public void ObserveIfSourceRequested()
    {
        if (scheduler.IsCancellationRequested)
        {
            ObserveOccurrence(invokeReservationHook: false);
        }
    }

    public void Retire()
    {
        lock (gate)
        {
            if (disposed || retired || OccurrenceSequence is not null)
            {
                return;
            }

            retired = true;
            scheduler.CancelAfter(Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        CausalInterruptionArbiter.Source? source;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            source = causalSource;
        }

        registration.Dispose();
        source?.Dispose();
        scheduler.Dispose();
    }

    private void SignalFromScheduler() => ObserveOccurrence(invokeReservationHook: true);

    private bool IsActiveAndRequested()
    {
        lock (gate)
        {
            return !disposed && !retired && scheduler.IsCancellationRequested;
        }
    }

    private void ObserveOccurrence(bool invokeReservationHook)
    {
        Action parentObserver;
        CausalInterruptionArbiter.Source source;
        CausalInterruptionArbiter currentArbiter;
        Action occurrenceSignal;
        lock (gate)
        {
            if (disposed || retired)
            {
                return;
            }
            parentObserver = observeParent
                ?? throw new InvalidOperationException("The production deadline source was not configured.");
            source = causalSource
                ?? throw new InvalidOperationException("The production deadline source was not configured.");
            currentArbiter = arbiter
                ?? throw new InvalidOperationException("The production deadline source was not configured.");
            occurrenceSignal = signal
                ?? throw new InvalidOperationException("The production deadline source was not configured.");
        }

        // An already-requested parent is observed before this deadline reserves
        // its position. The arbiter also observes every registered requested
        // source while holding one gate, with caller priority for a genuine tie.
        parentObserver();
        var reserved = source.Reserve();
        if (reserved && invokeReservationHook)
        {
            afterOccurrenceReserved?.Invoke();
        }
        currentArbiter.AcceptEarliest();
        occurrenceSignal();
    }
}

internal sealed class CausalCallerSignal : ICausalInterruptionSignal, IDisposable
{
    private readonly object gate = new();
    private readonly CancellationToken sourceToken;
    private readonly CausalInterruptionArbiter arbiter;
    private readonly CausalInterruptionArbiter.Source causalSource;
    private readonly Action? afterOccurrenceReserved;
    private readonly CancellationTokenSource propagation = new();
    private readonly CancellationTokenRegistration registration;
    private bool propagated;
    private bool disposed;

    public CausalCallerSignal(
        CancellationToken sourceToken,
        CausalInterruptionArbiter arbiter,
        Action acceptCause,
        Action? afterOccurrenceReserved)
    {
        this.sourceToken = sourceToken;
        this.arbiter = arbiter ?? throw new ArgumentNullException(nameof(arbiter));
        causalSource = arbiter.RegisterSource(
            () => sourceToken.IsCancellationRequested,
            acceptCause ?? throw new ArgumentNullException(nameof(acceptCause)),
            tiePriority: 0);
        this.afterOccurrenceReserved = afterOccurrenceReserved;
        registration = sourceToken.Register(SignalFromSource);
    }

    public CancellationToken Token => propagation.Token;

    public long? OccurrenceSequence => causalSource.Sequence;

    public void ObserveIfSourceRequested()
    {
        if (!sourceToken.IsCancellationRequested)
        {
            return;
        }

        causalSource.Reserve();
        arbiter.AcceptEarliest();
        Propagate();
    }

    public void EnsureCauseAccepted() => arbiter.AcceptEarliest();

    public void Dispose()
    {
        registration.Dispose();
        causalSource.Dispose();
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

    private void SignalFromSource()
    {
        var reserved = causalSource.Reserve();
        if (reserved)
        {
            afterOccurrenceReserved?.Invoke();
        }
        arbiter.AcceptEarliest();
        Propagate();
    }

    private void Propagate()
    {
        lock (gate)
        {
            if (propagated || disposed)
            {
                return;
            }

            propagated = true;
            propagation.Cancel();
        }
    }
}

internal sealed class CausalDeadlineScope : ICausalInterruptionSignal, IDisposable
{
    private readonly object gate = new();
    private readonly ICausalInterruptionSignal parent;
    private readonly CausalInterruptionArbiter arbiter;
    private readonly ProductionDeadlineSource deadline;
    private readonly CancellationTokenSource propagation = new();
    private readonly CancellationTokenRegistration parentRegistration;
    private bool propagated;
    private bool disposed;

    public CausalDeadlineScope(
        ICausalInterruptionSignal parent,
        CausalInterruptionArbiter arbiter,
        ProductionDeadlineSource deadline,
        string deadlineName,
        TimeSpan deadlineValue,
        Action acceptDeadlineCause,
        Action<string>? afterOccurrenceReserved)
    {
        this.parent = parent ?? throw new ArgumentNullException(nameof(parent));
        this.arbiter = arbiter ?? throw new ArgumentNullException(nameof(arbiter));
        this.deadline = deadline ?? throw new ArgumentNullException(nameof(deadline));

        deadline.Configure(
            arbiter,
            acceptDeadlineCause,
            parent.ObserveIfSourceRequested,
            SignalFromDeadline,
            afterOccurrenceReserved is null
                ? null
                : () => afterOccurrenceReserved(deadlineName));
        parentRegistration = parent.Token.Register(SignalFromParent);
        deadline.Arm(deadlineValue);
    }

    public CancellationToken Token => propagation.Token;

    public long? OccurrenceSequence
    {
        get
        {
            var parentSequence = parent.OccurrenceSequence;
            var deadlineSequence = deadline.OccurrenceSequence;
            if (parentSequence is null)
            {
                return deadlineSequence;
            }
            if (deadlineSequence is null)
            {
                return parentSequence;
            }
            return Math.Min(parentSequence.Value, deadlineSequence.Value);
        }
    }

    public void ObserveIfSourceRequested()
    {
        parent.ObserveIfSourceRequested();
        deadline.ObserveIfSourceRequested();
        if (OccurrenceSequence is not null)
        {
            Signal();
        }
    }

    public void EnsureCauseAccepted() => arbiter.AcceptEarliest();

    public void RetireDeadline() => deadline.Retire();

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

        parentRegistration.Dispose();
        deadline.Dispose();
        propagation.Dispose();
    }

    private void SignalFromParent()
    {
        deadline.ObserveIfSourceRequested();
        Signal();
    }

    private void SignalFromDeadline() => Signal();

    private void Signal()
    {
        arbiter.AcceptEarliest();
        lock (gate)
        {
            if (propagated || disposed)
            {
                return;
            }

            propagated = true;
            propagation.Cancel();
        }
    }
}
