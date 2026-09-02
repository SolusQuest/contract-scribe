using System.Diagnostics;

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
    private readonly Action? beforeHostProgress;
    private long nextSequence;
    private Source? accepted;

    public CausalInterruptionArbiter(Action? beforeHostProgress = null)
    {
        this.beforeHostProgress = beforeHostProgress;
    }

    public Source RegisterSource(
        Func<bool> isRequested,
        Action acceptCause,
        Action propagate,
        int tiePriority)
    {
        var source = new Source(this, isRequested, acceptCause, propagate, tiePriority);
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
            var newlyReserved = ReserveRequestedSourcesLocked();
            AcceptEarliestLocked();
            PropagateLocked(newlyReserved);
        }
    }

    public void ExecuteHostProgress(Action progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        beforeHostProgress?.Invoke();
        lock (gate)
        {
            var newlyReserved = ReserveRequestedSourcesLocked();
            AcceptEarliestLocked();
            PropagateLocked(newlyReserved);
            progress();
        }
    }

    public T ExecuteHostProgress<T>(Func<T> progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        beforeHostProgress?.Invoke();
        lock (gate)
        {
            var newlyReserved = ReserveRequestedSourcesLocked();
            AcceptEarliestLocked();
            PropagateLocked(newlyReserved);
            return progress();
        }
    }

    private void ObserveOccurrence(
        Source source,
        Action? afterLinearized)
    {
        lock (gate)
        {
            var wasReserved = source.ReservedSequence is not null;
            var newlyReserved = ReserveRequestedSourcesLocked();
            if (!source.Disposed && !source.Retired && source.ReservedSequence is null)
            {
                source.ReservedSequence = ++nextSequence;
                newlyReserved.Add(source);
            }
            AcceptEarliestLocked();
            PropagateLocked(newlyReserved);
            if (!wasReserved && source.ReservedSequence is not null)
            {
                afterLinearized?.Invoke();
            }
        }
    }

    private bool TryRetire(Source source, Func<bool> hasElapsed)
    {
        lock (gate)
        {
            var newlyReserved = ReserveRequestedSourcesLocked();
            if (!source.Disposed
                && !source.Retired
                && source.ReservedSequence is null
                && hasElapsed())
            {
                source.ReservedSequence = ++nextSequence;
                newlyReserved.Add(source);
            }
            AcceptEarliestLocked();
            PropagateLocked(newlyReserved);
            if (source.Disposed || source.Retired || source.ReservedSequence is not null)
            {
                return false;
            }

            source.Retired = true;
            return true;
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

    private List<Source> ReserveRequestedSourcesLocked()
    {
        var newlyReserved = new List<Source>();
        foreach (var source in sources
                     .Where(source => !source.Disposed && source.ReservedSequence is null && source.IsRequested())
                     .OrderBy(source => source.TiePriority))
        {
            source.ReservedSequence = ++nextSequence;
            newlyReserved.Add(source);
        }
        return newlyReserved;
    }

    private void AcceptEarliestLocked()
    {
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

    private static void PropagateLocked(IEnumerable<Source> newlyReserved)
    {
        foreach (var source in newlyReserved)
        {
            source.Propagate();
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
        private readonly Action propagate;

        public Source(
            CausalInterruptionArbiter owner,
            Func<bool> isRequested,
            Action acceptCause,
            Action propagate,
            int tiePriority)
        {
            this.owner = owner;
            this.isRequested = isRequested;
            this.acceptCause = acceptCause;
            this.propagate = propagate;
            TiePriority = tiePriority;
        }

        internal long? ReservedSequence { get; set; }

        public long? Sequence => owner.GetSequence(this);

        public int TiePriority { get; }

        public bool Disposed { get; set; }

        public bool Retired { get; set; }

        public void ObserveOccurrence(Action? afterLinearized = null) =>
            owner.ObserveOccurrence(this, afterLinearized);

        public bool TryRetire(Func<bool> hasElapsed) => owner.TryRetire(this, hasElapsed);

        public bool IsRequested() => isRequested();

        public void AcceptCause() => acceptCause();

        public void Propagate() => propagate();

        public void Dispose() => owner.Unregister(this);
    }
}

internal sealed class ProductionDeadlineSource : IDisposable
{
    private readonly object gate = new();
    private CausalInterruptionArbiter.Source? causalSource;
    private Action? signal;
    private Action? afterOccurrenceReserved;
    private Action? beforeRetirementLinearized;
    private Timer? timer;
    private long deadlineTimestamp;
    private bool armed;
    private bool retired;
    private bool disposed;

    public long? OccurrenceSequence => causalSource?.Sequence;

    public void Configure(
        CausalInterruptionArbiter arbiter,
        Action acceptDeadlineCause,
        Action signal,
        Action? afterOccurrenceReserved,
        Action? beforeRetirementLinearized)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (causalSource is not null)
            {
                throw new InvalidOperationException("A production deadline source can be configured only once.");
            }

            ArgumentNullException.ThrowIfNull(arbiter);
            causalSource = arbiter.RegisterSource(
                static () => false,
                acceptDeadlineCause ?? throw new ArgumentNullException(nameof(acceptDeadlineCause)),
                signal ?? throw new ArgumentNullException(nameof(signal)),
                tiePriority: 1);
            this.signal = signal;
            this.afterOccurrenceReserved = afterOccurrenceReserved;
            this.beforeRetirementLinearized = beforeRetirementLinearized;
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
            deadlineTimestamp = Stopwatch.GetTimestamp()
                + (long)(deadline.TotalSeconds * Stopwatch.Frequency);
            timer = new Timer(
                static state => ((ProductionDeadlineSource)state!).SignalFromTimer(),
                this,
                deadline,
                Timeout.InfiniteTimeSpan);
        }
    }

    public void Trigger() => ObserveOccurrence(invokeLinearizedHook: true);

    public void ObserveIfSourceRequested()
    {
        if (HasElapsed())
        {
            ObserveOccurrence(invokeLinearizedHook: false);
        }
    }

    public void Retire()
    {
        CausalInterruptionArbiter.Source? source;
        lock (gate)
        {
            if (disposed || retired)
            {
                return;
            }
            source = causalSource;
        }

        beforeRetirementLinearized?.Invoke();
        var retiredNow = source?.TryRetire(HasElapsed) == true;
        if (!retiredNow)
        {
            signal?.Invoke();
            return;
        }

        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            retired = true;
            timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        CausalInterruptionArbiter.Source? source;
        Timer? currentTimer;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            source = causalSource;
            currentTimer = timer;
        }

        source?.Dispose();
        currentTimer?.Dispose();
    }

    private void SignalFromTimer() => ObserveOccurrence(invokeLinearizedHook: true);

    private bool HasElapsed() =>
        armed && Stopwatch.GetTimestamp() >= Volatile.Read(ref deadlineTimestamp);

    private void ObserveOccurrence(bool invokeLinearizedHook)
    {
        CausalInterruptionArbiter.Source source;
        Action? linearizedHook;
        lock (gate)
        {
            if (disposed || retired)
            {
                return;
            }
            source = causalSource
                ?? throw new InvalidOperationException("The production deadline source was not configured.");
            linearizedHook = invokeLinearizedHook ? afterOccurrenceReserved : null;
        }

        // The timer and manual trigger share this direct occurrence path. Source
        // selection and Core cause acceptance finish under the arbiter gate
        // before a test barrier or Host-owned cancellation token is exposed.
        source.ObserveOccurrence(linearizedHook);
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
            Propagate,
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

        causalSource.ObserveOccurrence();
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
        causalSource.ObserveOccurrence(afterOccurrenceReserved);
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
        Action<string>? afterOccurrenceReserved,
        Action<string>? beforeRetirementLinearized)
    {
        this.parent = parent ?? throw new ArgumentNullException(nameof(parent));
        this.arbiter = arbiter ?? throw new ArgumentNullException(nameof(arbiter));
        this.deadline = deadline ?? throw new ArgumentNullException(nameof(deadline));

        deadline.Configure(
            arbiter,
            acceptDeadlineCause,
            SignalFromDeadline,
            afterOccurrenceReserved is null
                ? null
                : () => afterOccurrenceReserved(deadlineName),
            beforeRetirementLinearized is null
                ? null
                : () => beforeRetirementLinearized(deadlineName));
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

    public void RetireDeadline()
    {
        deadline.Retire();
        if (OccurrenceSequence is not null)
        {
            Signal();
        }
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
