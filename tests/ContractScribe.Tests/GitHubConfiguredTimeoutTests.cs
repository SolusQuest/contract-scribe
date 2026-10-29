using System.Diagnostics;
using System.Net;
using System.Reflection;
using ContractScribe.Cli;
using ContractScribe.GitHub.Transport;

namespace ContractScribe.Tests;

[Collection("GitHub transport hook")]
public sealed class GitHubConfiguredTimeoutTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Configured_request_bounds_response_and_body_and_original_caller_still_wins(bool body, bool callerFirst)
    {
        var calls = 0;
        using var parent = new CancellationTokenSource(TimeSpan.FromMilliseconds(callerFirst ? 100 : 10000));
        var handler = new ConfiguredHandler(async (_, token) =>
        {
            calls++;
            if (!body) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(HttpStatusCode.OK) { Content = new StreamContent(new BlockedBody()) };
        });
        // Null hook timeout exercises the actual configured production value.
        using var hook = (IDisposable)typeof(GitHubTransportTestHook)
            .GetMethod("Register", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [new Uri("http://127.0.0.1:18765/"), handler, null])!;
        using var client = GitHubApiClient.Create(GitHubCoordinationRefTests.InitialAuthority("configured", '5'),
            GitHubTransportTestHook.Placeholder, GitHubTransportTimeouts.Create(callerFirst ? 5000 : 150, 50));
        var result = await client.GetAuthenticatedUserAsync(parent.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(callerFirst ? GitHubFailureCode.Cancelled : GitHubFailureCode.Timeout, result.Failure!.Code);
        Assert.Equal(callerFirst, parent.IsCancellationRequested);
        Assert.Null(result.Value);
        Assert.Equal(1, calls); // No new transport retry for a timeout or cancellation.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Production_handler_configured_connect_cutoff_cancels_the_actual_blocked_connector(bool callerFirst)
    {
        using var handler = GitHubApiClient.CreateProductionHandler(GitHubTransportTimeouts.Create(5000, 150));
        var entered = false;
        var connectorCancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.ConnectCallback = async (_, token) =>
        {
            entered = true;
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { connectorCancelled.TrySetResult(token.IsCancellationRequested); throw; }
            throw new InvalidOperationException("The blocked connector cannot return a stream.");
        };
        using var client = new HttpMessageInvoker(handler, disposeHandler: false);
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(callerFirst ? 50 : 10000));
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:18765/");
        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(request, caller.Token));
        Assert.True(entered);
        // The caller can complete before the owned connector's cancellation
        // continuation. Join that existing operation before checking cleanup.
        Assert.True(await connectorCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(callerFirst, caller.IsCancellationRequested);
        Assert.InRange(stopwatch.ElapsedMilliseconds, callerFirst ? 20 : 100, 5000);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.Null(handler.Credentials);
    }

    [Fact]
    public void Configured_publication_starts_only_once_and_keeps_the_same_recovery_cutoff()
    {
        var clock = new GitHubPublicationFinalizationTests.DeadlineClock();
        using var host = new GitHubPublicationFinalization(default, clock, 12000);
        Assert.Empty(clock.DueTimes);
        var token = host.Begin(default);
        Assert.Equal(TimeSpan.FromSeconds(12), Assert.Single(clock.DueTimes));
        using var transport = GitHubPublicationFinalizationTests.RegisterClockTransport();
        using var client = GitHubApiClient.Create(GitHubCoordinationRefTests.InitialAuthority("configured", '5'), GitHubTransportTestHook.Placeholder);
        client.BindFinalizationDeadline(host.DeadlineToken, () => host.TimedOut);
        using var recovery = client.CreateRecoveryScope(TimeSpan.FromSeconds(90));
        clock.Expire();
        Assert.True(token.IsCancellationRequested);
        Assert.True(recovery.IsCancellationRequested);
        Assert.True(host.TimedOut);
        Assert.Equal(token, host.Begin(default));
        Assert.Single(clock.DueTimes);
        Assert.Throws<ArgumentOutOfRangeException>(() => new GitHubPublicationFinalization(default, clock, 300001));
        Assert.Throws<ArgumentOutOfRangeException>(() => GitHubTransportTimeouts.Create(10, 11));
    }

    private sealed class ConfiguredHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }

    private sealed class BlockedBody : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { await Task.Delay(Timeout.InfiniteTimeSpan, token); return 0; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
