using System.Diagnostics;

namespace ContractScribe.HostValidation;

public sealed class MonotonicDeadline
{
    private readonly long deadlineTimestamp;

    private MonotonicDeadline(long deadlineTimestamp)
    {
        this.deadlineTimestamp = deadlineTimestamp;
    }

    public static MonotonicDeadline Start(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            return new(Stopwatch.GetTimestamp());
        }
        var ticks = checked((long)Math.Ceiling(
            timeout.TotalSeconds * Stopwatch.Frequency));
        return new(checked(Stopwatch.GetTimestamp() + ticks));
    }

    public TimeSpan Remaining
    {
        get
        {
            var ticks = deadlineTimestamp - Stopwatch.GetTimestamp();
            return ticks <= 0
                ? TimeSpan.Zero
                : TimeSpan.FromSeconds((double)ticks / Stopwatch.Frequency);
        }
    }

    public bool IsExpired => Remaining == TimeSpan.Zero;

    public int NextWaitMilliseconds(int maximum)
    {
        var remaining = Remaining;
        if (remaining == TimeSpan.Zero)
        {
            return 0;
        }
        return Math.Max(
            1,
            Math.Min(maximum, checked((int)Math.Ceiling(remaining.TotalMilliseconds))));
    }
}
