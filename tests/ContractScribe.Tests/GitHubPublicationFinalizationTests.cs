using ContractScribe.Cli;
using System.Reflection;
using ContractScribe.GitHub.Transport;

namespace ContractScribe.Tests;

[Collection("GitHub transport hook")]
public sealed class GitHubPublicationFinalizationTests
{
    [Fact]
    public void Deadline_first_remains_timeout_when_caller_cancels_before_projection()
    {
        var clock = new DeadlineClock();
        using var caller = new CancellationTokenSource();
        using var host = new GitHubPublicationFinalization(caller.Token, clock);
        host.Begin(default);
        using var transport = RegisterClockTransport();
        using var client = GitHubApiClient.Create(GitHubCoordinationRefTests.InitialAuthority("deadline", '5'), GitHubTransportTestHook.Placeholder);
        client.BindFinalizationDeadline(host.DeadlineToken, () => host.TimedOut);
        clock.Expire();
        var first = client.CancellationFailure;
        caller.Cancel();
        Assert.True(host.TimedOut);
        Assert.Equal(GitHubFailureCode.Timeout, first.Code);
        Assert.Equal(first, client.CancellationFailure);
    }

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
    public void Claim_cancellation_keeps_its_disposition_when_the_host_deadline_later_expires()
    {
        var clock = new DeadlineClock();
        using var claim = new CancellationTokenSource();
        using var host = new GitHubPublicationFinalization(default, clock);
        var execution = host.Begin(claim.Token);
        claim.Cancel();
        Assert.True(execution.IsCancellationRequested);
        clock.Expire();
        Assert.True(host.DeadlineToken.IsCancellationRequested);
        Assert.False(host.TimedOut);
    }

    [Fact]
    public void Claim_stop_is_recorded_before_its_linked_cancellation_can_expire_the_deadline()
    {
        var clock = new DeadlineClock();
        using var claim = new CancellationTokenSource();
        using var host = new GitHubPublicationFinalization(default, clock);
        var execution = host.Begin(claim.Token);
        using var propagation = execution.Register(clock.Expire);
        claim.Cancel();
        Assert.True(host.DeadlineToken.IsCancellationRequested);
        Assert.False(host.TimedOut);
    }

    [Fact]
    public void Deadline_timeout_is_not_reclassified_by_a_later_claim_stop()
    {
        var clock = new DeadlineClock();
        using var claim = new CancellationTokenSource();
        using var host = new GitHubPublicationFinalization(default, clock);
        host.Begin(claim.Token);
        clock.Expire();
        claim.Cancel();
        Assert.True(host.TimedOut);
    }

    [Fact]
    public void Caller_cancellation_preserves_independent_readback_but_host_deadline_still_bounds_it()
    {
        var clock = new DeadlineClock();
        using var caller = new CancellationTokenSource();
        using var host = new GitHubPublicationFinalization(caller.Token, clock: clock);
        var execution = host.Begin(default);
        using var transport = RegisterClockTransport();
        using var client = GitHubApiClient.Create(GitHubCoordinationRefTests.InitialAuthority("deadline", '5'), GitHubTransportTestHook.Placeholder);
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

    internal static IDisposable RegisterClockTransport() =>
        (IDisposable)typeof(GitHubTransportTestHook).GetMethod("Register", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [new Uri("http://127.0.0.1:18765/"), new HttpClientHandler(), null])!;

    internal sealed class DeadlineClock : TimeProvider
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
