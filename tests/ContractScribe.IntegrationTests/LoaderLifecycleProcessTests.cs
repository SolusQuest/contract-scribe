using System.Diagnostics;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed class LoaderLifecycleProcessTests
{
    [Fact]
    public async Task TwoFreshSingleLoadsOverlapInsideRealRemoteBuild()
    {
        await using var leftFixture = await PreparedFixtureAsync();
        await using var rightFixture = await PreparedFixtureAsync();

        await RunSuccessfulPairAsync(leftFixture.Root, rightFixture.Root);
    }

    [Fact]
    public async Task HeldSessionRemainsHealthyAcrossFailureCancellationAndUnexpectedCleanup()
    {
        await using var heldFixture = await PreparedFixtureAsync();
        await using var failureFixture = await PreparedFixtureAsync();
        await using var cancellationFixture = await PreparedFixtureAsync();
        await using var unexpectedFixture = await PreparedFixtureAsync();
        await using var held = await LoaderLifecycleHarness.StartAsync(
            heldFixture.Root,
            "lifecycle-held-session");
        await held.WaitForTaskReadyAsync();
        await held.ReleaseTaskAsync();
        var heldResult = await held.ReadResultAsync(LoaderLifecycleHarness.SessionReady);
        Assert.Equal(RepositoryLoadStatus.Success, heldResult.Status);
        Assert.True(
            SpinWait.SpinUntil(held.OwnedProcessesHaveExited, TimeSpan.FromSeconds(10)),
            "The held session retained a load-call-owned BuildHost process.");
        Assert.False(held.Probe.HasExited);

        await RunTargetAsync(
            failureFixture.Root,
            "lifecycle-failure",
            RepositoryLoadStatus.Failure,
            "run.generated.authority-conflict");
        Assert.False(held.Probe.HasExited);

        await RunTargetAsync(
            cancellationFixture.Root,
            "lifecycle-cancellation",
            RepositoryLoadStatus.Cancelled,
            "loader.cancelled",
            LoaderLifecycleHarness.Cancel);
        Assert.False(held.Probe.HasExited);

        var unexpected = await RunTargetAsync(
            unexpectedFixture.Root,
            "lifecycle-unexpected",
            RepositoryLoadStatus.Failure,
            "loader.internal-error",
            LoaderLifecycleHarness.InjectUnexpected);
        Assert.Contains(unexpected.Exceptions, exception =>
            exception.TypeChain.Contains(
                typeof(InvalidOperationException).FullName!,
                StringComparer.Ordinal));
        Assert.False(held.Probe.HasExited);

        await held.ReleaseSessionAsync();
        var heldProcess = await held.WaitForExitAsync();
        AssertProcessExit(heldProcess, LoaderLifecycleHarness.SuccessExit);
        Assert.True(
            SpinWait.SpinUntil(held.AllProcessesHaveExited, TimeSpan.FromSeconds(10)),
            "The held session probe or one of its owned processes remained alive.");
    }

    [Fact]
    public async Task EveryControlledDriverExitClassUsesARealChildProcess()
    {
        await using var fixture = await PreparedFixtureAsync();

        using (var rejected = Process.Start(
                   LoaderLifecycleHarness.CreateStartInfo(
                       fixture.Root,
                       "lifecycle-success"))
               ?? throw new InvalidOperationException("Rejected lifecycle probe failed to start."))
        {
            var stdout = rejected.StandardOutput.ReadToEndAsync();
            var stderr = rejected.StandardError.ReadToEndAsync();
            await rejected.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            AssertProcessExit(
                new ProcessResult(rejected.ExitCode, await stdout, await stderr),
                LoaderLifecycleHarness.SetupRejectedExit);
        }

        await using (var success = await LoaderLifecycleHarness.StartAsync(
                         fixture.Root,
                         "lifecycle-success"))
        {
            await success.WaitForTaskReadyAsync();
            await success.ReleaseTaskAsync();
            var result = await success.ReadResultAsync();
            Assert.Equal(RepositoryLoadStatus.Success, result.Status);
            AssertProcessExit(
                await success.WaitForExitAsync(),
                LoaderLifecycleHarness.SuccessExit);
        }

        await using (var mismatch = await LoaderLifecycleHarness.StartAsync(
                         fixture.Root,
                         "lifecycle-expect-failure"))
        {
            await mismatch.WaitForTaskReadyAsync();
            await mismatch.ReleaseTaskAsync();
            var result = await mismatch.ReadResultAsync();
            Assert.Equal(RepositoryLoadStatus.Success, result.Status);
            AssertProcessExit(
                await mismatch.WaitForExitAsync(),
                LoaderLifecycleHarness.OutcomeMismatchExit);
        }

        await using (var controlFailure = await LoaderLifecycleHarness.StartAsync(
                         fixture.Root,
                         "lifecycle-success"))
        {
            await controlFailure.WaitForTaskReadyAsync();
            await controlFailure.ReleaseTaskAsync();
            var result = await controlFailure.ReadResultAsync(acknowledgement: byte.MaxValue);
            Assert.Equal(RepositoryLoadStatus.Success, result.Status);
            AssertProcessExit(
                await controlFailure.WaitForExitAsync(),
                LoaderLifecycleHarness.ControlFailureExit);
        }
    }

    [Fact]
    public async Task ParentAssertionTimeoutAndAbruptTerminationShareCompleteCleanup()
    {
        await using var fixture = await PreparedFixtureAsync();

        var assertionHarness = await LoaderLifecycleHarness.StartAsync(
            fixture.Root,
            "lifecycle-success");
        var assertion = await Record.ExceptionAsync(async () =>
        {
            try
            {
                await assertionHarness.WaitForTaskReadyAsync();
                Assert.Fail("Injected parent assertion after the real task reached ready.");
            }
            finally
            {
                await assertionHarness.DisposeAsync();
            }
        });
        Assert.IsType<Xunit.Sdk.FailException>(assertion);
        Assert.True(assertionHarness.AllProcessesHaveExited());

        var timeoutHarness = await LoaderLifecycleHarness.StartAsync(
            fixture.Root,
            "lifecycle-success");
        var timeout = await Record.ExceptionAsync(async () =>
        {
            try
            {
                await timeoutHarness.WaitForTaskReadyAsync();
                await Task.Delay(Timeout.InfiniteTimeSpan)
                    .WaitAsync(TimeSpan.FromMilliseconds(100));
            }
            finally
            {
                await timeoutHarness.DisposeAsync();
            }
        });
        Assert.IsType<TimeoutException>(timeout);
        Assert.True(timeoutHarness.AllProcessesHaveExited());

        var abruptHarness = await LoaderLifecycleHarness.StartAsync(
            fixture.Root,
            "lifecycle-success");
        await abruptHarness.WaitForTaskReadyAsync();
        abruptHarness.KillProbeAbruptly();
        await abruptHarness.Probe.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await abruptHarness.DisposeAsync();
        Assert.True(abruptHarness.AllProcessesHaveExited());
    }

    [Fact]
    public async Task TransportFailuresRemainHarnessOutcomesAfterTheProductResult()
    {
        await using var fixture = await PreparedFixtureAsync();

        await using (var missingReceiver = await LoaderLifecycleHarness.StartAsync(
                         fixture.Root,
                         "lifecycle-success",
                         missingControlReceiver: true))
        {
            await missingReceiver.WaitForTaskReadyAsync();
            await missingReceiver.ReleaseTaskAsync();
            var process = await missingReceiver.WaitForExitAsync();
            AssertProcessExit(process, LoaderLifecycleHarness.ControlFailureExit);
            Assert.StartsWith("Success:", process.StandardOutput, StringComparison.Ordinal);
            Assert.True(
                SpinWait.SpinUntil(missingReceiver.OwnedProcessesHaveExited, TimeSpan.FromSeconds(10)));
        }

        await using (var notReading = await LoaderLifecycleHarness.StartAsync(
                         fixture.Root,
                         "lifecycle-success"))
        {
            await notReading.WaitForTaskReadyAsync();
            await notReading.ReleaseTaskAsync();
            var process = await notReading.WaitForExitAsync();
            AssertProcessExit(process, LoaderLifecycleHarness.ControlFailureExit);
            Assert.StartsWith("Success:", process.StandardOutput, StringComparison.Ordinal);
            Assert.True(
                SpinWait.SpinUntil(notReading.OwnedProcessesHaveExited, TimeSpan.FromSeconds(10)));
        }

        await using (var malformed = await LoaderLifecycleHarness.StartAsync(
                         fixture.Root,
                         "lifecycle-cancellation"))
        {
            await malformed.WaitForTaskReadyAsync();
            await malformed.WriteControlByteAsync(byte.MaxValue);
            await malformed.ReleaseTaskAsync();
            var process = await malformed.WaitForExitAsync();
            AssertProcessExit(process, LoaderLifecycleHarness.ControlFailureExit);
            Assert.StartsWith("Success:", process.StandardOutput, StringComparison.Ordinal);
            Assert.True(
                SpinWait.SpinUntil(malformed.OwnedProcessesHaveExited, TimeSpan.FromSeconds(10)));
        }

        await using (var serialization = await LoaderLifecycleHarness.StartAsync(
                         fixture.Root,
                         "lifecycle-success",
                         injectSerializationFailure: true))
        {
            await serialization.WaitForTaskReadyAsync();
            await serialization.ReleaseTaskAsync();
            var process = await serialization.WaitForExitAsync();
            AssertProcessExit(process, LoaderLifecycleHarness.ControlFailureExit);
            Assert.StartsWith("Success:", process.StandardOutput, StringComparison.Ordinal);
            Assert.True(
                SpinWait.SpinUntil(serialization.OwnedProcessesHaveExited, TimeSpan.FromSeconds(10)));
        }
    }

    [Fact]
    public async Task WindowsCausalTopologyPassesThirtyIndependentIterations()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using var leftFixture = await PreparedFixtureAsync();
        await using var rightFixture = await PreparedFixtureAsync();
        var leftBaseline = RepositoryInventory.Capture(
            leftFixture.Root,
            CancellationToken.None);
        var rightBaseline = RepositoryInventory.Capture(
            rightFixture.Root,
            CancellationToken.None);
        var elapsed = Stopwatch.StartNew();

        for (var iteration = 1; iteration <= 30; iteration++)
        {
            try
            {
                await RunSuccessfulPairAsync(leftFixture.Root, rightFixture.Root);
                AssertProtectedInputsUnchanged(leftFixture.Root, leftBaseline);
                AssertProtectedInputsUnchanged(rightFixture.Root, rightBaseline);
                Assert.True(
                    elapsed.Elapsed < TimeSpan.FromMinutes(5),
                    $"The thirty-iteration topology exceeded its total deadline at iteration {iteration}.");
            }
            catch (Exception exception)
            {
                Assert.Fail($"Windows causal topology failed at iteration {iteration}: {exception}");
            }
        }
    }

    private static async Task<LoaderFixture> PreparedFixtureAsync() =>
        await LoaderFixture.CreateAsync(appProject: LoaderLifecycleHarness.ProbeAppProject());

    private static async Task RunSuccessfulPairAsync(string leftRoot, string rightRoot)
    {
        await using var left = await LoaderLifecycleHarness.StartAsync(
            leftRoot,
            "lifecycle-success");
        await using var right = await LoaderLifecycleHarness.StartAsync(
            rightRoot,
            "lifecycle-success");
        await Task.WhenAll(left.WaitForTaskReadyAsync(), right.WaitForTaskReadyAsync());
        Assert.NotEqual(left.Probe.Id, right.Probe.Id);
        Assert.NotNull(left.BuildHostIdentity);
        Assert.NotNull(right.BuildHostIdentity);
        Assert.NotEqual(left.BuildHostIdentity, right.BuildHostIdentity);
        await Task.WhenAll(left.ReleaseTaskAsync(), right.ReleaseTaskAsync());
        var results = await Task.WhenAll(left.ReadResultAsync(), right.ReadResultAsync());
        Assert.All(results, result =>
        {
            Assert.Equal(RepositoryLoadStatus.Success, result.Status);
            Assert.Equal(string.Empty, result.Code);
        });
        var processes = await Task.WhenAll(left.WaitForExitAsync(), right.WaitForExitAsync());
        Assert.All(processes, process =>
            AssertProcessExit(process, LoaderLifecycleHarness.SuccessExit));
        Assert.True(
            SpinWait.SpinUntil(
                () => left.OwnedProcessesHaveExited() && right.OwnedProcessesHaveExited(),
                TimeSpan.FromSeconds(10)),
            "A task process or BuildHost remained after both successful loads completed.");
    }

    private static async Task<LifecycleResult> RunTargetAsync(
        string root,
        string mode,
        RepositoryLoadStatus expectedStatus,
        string expectedCode,
        byte? command = null)
    {
        await using var target = await LoaderLifecycleHarness.StartAsync(root, mode);
        await target.WaitForTaskReadyAsync();
        if (command is not null)
        {
            await target.SendCommandAsync(command.Value);
        }
        await target.ReleaseTaskAsync();
        var result = await target.ReadResultAsync();
        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(expectedCode, result.Code);
        AssertProcessExit(
            await target.WaitForExitAsync(),
            LoaderLifecycleHarness.SuccessExit);
        Assert.True(
            SpinWait.SpinUntil(target.OwnedProcessesHaveExited, TimeSpan.FromSeconds(10)),
            $"Target-owned process remained after {mode}.");
        return result;
    }

    private static void AssertProcessExit(ProcessResult result, int expectedExit)
    {
        Assert.True(
            result.ExitCode == expectedExit,
            $"Lifecycle probe exited {result.ExitCode}, expected {expectedExit}:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}{result.StandardError}");
    }

    private static void AssertProtectedInputsUnchanged(
        string root,
        IReadOnlyDictionary<string, InventoryEntry> baseline)
    {
        var after = RepositoryInventory.Capture(root, CancellationToken.None);
        var protectedChanges = RepositoryInventory.ChangedPaths(baseline, after)
            .Where(path => Path.GetExtension(path).ToLowerInvariant() is
                ".cs" or ".csproj" or ".props" or ".targets" or ".sln" or ".slnx" or ".editorconfig")
            .ToArray();
        Assert.Empty(protectedChanges);
    }
}
