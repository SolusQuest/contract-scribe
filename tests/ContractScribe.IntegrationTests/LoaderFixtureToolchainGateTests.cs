using ContractScribe.TestSupport;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed partial class LoaderFixtureTests
{
    [Fact]
    public async Task Toolchain_queries_wait_for_delayed_actual_root_identity_binding()
    {
        var directory = Directory.CreateTempSubdirectory("toolchain-gate-").FullName;
        var marker = Path.Join(directory, "queries");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? exited = null;
        var released = false;
        Task<ToolchainSnapshotResult>? running = null;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var hooks = new OwnedProcessTestHooks
        {
            ProcessStarted = process => exited = process.WaitForExitAsync(),
            ReadStartIdentity = process =>
            {
                entered.TrySetResult();
                resume.Task.WaitAsync(cancellation.Token).GetAwaiter().GetResult();
                return StableProcessStartIdentity.Read(process);
            },
            ReleaseStandardInputAfterObservation = (_, _) =>
            {
                Assert.False(File.Exists(marker));
                released = true;
                return Task.CompletedTask;
            }
        };
        try
        {
            running = Task.Run(() => ToolchainSnapshotProbe.RunAsync(
                Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet", FindRepositoryRoot(), LoaderProbePath(),
                TimeSpan.FromSeconds(30), cancellation.Token, hooks, marker));
            await entered.Task.WaitAsync(cancellation.Token);
            while (!File.Exists(marker + ".ready")) await Task.Delay(10, cancellation.Token);
            Assert.False(released);
            Assert.False(File.Exists(marker));
            Assert.False(running.IsCompleted);
            resume.TrySetResult();
            var snapshot = await running;
            Assert.True(released);
            Assert.False(string.IsNullOrWhiteSpace(snapshot.SdkVersion));
            Assert.Contains(snapshot.SdkVersion + " [", snapshot.InstalledSdks);
            Assert.Equal(new[] { "--version", "--list-sdks" }, await File.ReadAllLinesAsync(marker, cancellation.Token));
            Assert.NotNull(exited);
            await exited.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            resume.TrySetResult(); cancellation.Cancel();
            if (running is not null)
            {
                try { await running; }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            }
            if (exited is not null) await exited.WaitAsync(TimeSpan.FromSeconds(5));
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Toolchain_observer_failure_reaps_the_gated_root_without_releasing_queries()
    {
        var directory = Directory.CreateTempSubdirectory("toolchain-observer-failure-").FullName;
        var marker = Path.Join(directory, "queries");
        Task? exited = null;
        var released = false;
        var hooks = new OwnedProcessTestHooks
        {
            ProcessStarted = process => exited = process.WaitForExitAsync(),
            ReadStartIdentity = _ => throw new IOException("deterministic identity failure"),
            ReleaseStandardInputAfterObservation = (_, _) => { released = true; return Task.CompletedTask; }
        };
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ToolchainSnapshotProbe.RunAsync(
                Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet", FindRepositoryRoot(), LoaderProbePath(),
                TimeSpan.FromSeconds(30), hooks: hooks, marker: marker));
            Assert.Contains("observation could not be established", error.Message);
            Assert.False(released);
            Assert.False(File.Exists(marker));
            Assert.NotNull(exited);
            await exited.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Toolchain_original_deadline_or_cancellation_reaps_the_unreleased_root(bool cancelCaller)
    {
        var directory = Directory.CreateTempSubdirectory("toolchain-cancel-").FullName;
        var marker = Path.Join(directory, "queries");
        using var caller = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? exited = null;
        var hooks = new OwnedProcessTestHooks
        {
            ProcessStarted = process => exited = process.WaitForExitAsync(),
            ReleaseStandardInputAfterObservation = async (_, token) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        };
        try
        {
            var running = ToolchainSnapshotProbe.RunAsync(
                Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet", FindRepositoryRoot(), LoaderProbePath(),
                TimeSpan.FromSeconds(cancelCaller ? 30 : 2), caller.Token, hooks, marker);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (cancelCaller) caller.Cancel();
            if (cancelCaller) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            else await Assert.ThrowsAsync<TimeoutException>(() => running);
            Assert.False(File.Exists(marker));
            Assert.NotNull(exited);
            await exited.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { caller.Cancel(); Directory.Delete(directory, recursive: true); }
    }
}
