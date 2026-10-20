using ContractScribe.Cli;
using ContractScribe.GitHub.Transport;

namespace ContractScribe.Tests;

[Collection("GitHub transport hook")]
public sealed class GitHubPublicationFinalizationTests
{
    [Fact]
    public void Finalization_starts_once_and_never_renews_with_another_stage()
    {
        var clock = new DeadlineClock();
        using var host = new GitHubPublicationFinalization(default, clock: clock);
        Assert.False(host.DeadlineToken.CanBeCanceled);
        var original = host.Begin(default);
        Assert.Equal(TimeSpan.FromMinutes(5), Assert.Single(clock.DueTimes));
        Assert.Equal(original, host.Begin(default));
        Assert.Single(clock.DueTimes);
        clock.Expire();
        Assert.True(original.IsCancellationRequested);
        Assert.True(host.TimedOut);
        Assert.Equal(original, host.Begin(default));
        Assert.True(host.DeadlineToken.IsCancellationRequested);
    }

    [Fact]
    public void Caller_cancellation_preserves_independent_readback_but_host_deadline_still_bounds_it()
    {
        var clock = new DeadlineClock();
        using var caller = new CancellationTokenSource();
        using var host = new GitHubPublicationFinalization(caller.Token, clock: clock);
        var execution = host.Begin(default);
        using var client = GitHubApiClient.Create(GitHubCoordinationRefTests.InitialAuthority("deadline", '5'), "synthetic");
        client.BindFinalizationDeadline(host.DeadlineToken);
        caller.Cancel();
        Assert.True(execution.IsCancellationRequested);
        Assert.False(host.TimedOut);
        using var recovery = client.CreateRecoveryScope(TimeSpan.FromSeconds(90));
        Assert.False(recovery.IsCancellationRequested);
        clock.Expire();
        Assert.True(recovery.IsCancellationRequested);
        Assert.False(host.TimedOut); // Actual caller cancellation retains its distinct disposition.
        using var later = client.CreateRecoveryScope(TimeSpan.FromSeconds(90));
        Assert.True(later.IsCancellationRequested);
    }

    private sealed class DeadlineClock : TimeProvider
    {
        private readonly List<Action> callbacks = [];
        internal List<TimeSpan> DueTimes { get; } = [];
        internal void Expire() { foreach (var callback in callbacks.ToArray()) callback(); }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            DueTimes.Add(dueTime);
            var timer = new DeadlineTimer(callback, state);
            callbacks.Add(timer.Fire);
            return timer;
        }
        private sealed class DeadlineTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool disposed;
            internal void Fire() { if (!disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !disposed;
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
