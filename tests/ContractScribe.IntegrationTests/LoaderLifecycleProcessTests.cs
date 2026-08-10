using System.Diagnostics;

namespace ContractScribe.Roslyn.IntegrationTests;

[Collection("Integration process lane 2")]
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
        await using var successFixture = await PreparedFixtureAsync();
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
            successFixture.Root,
            "lifecycle-success",
            RepositoryLoadStatus.Success,
            string.Empty);
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

        var rejected = await LoaderLifecycleHarness.StartAsync(
            fixture.Root,
            "lifecycle-success",
            configureControl: false);
        await using (rejected)
        {
            var rejectedProcess = await rejected.WaitForExitAsync();
            AssertProcessExit(
                rejectedProcess,
                LoaderLifecycleHarness.SetupRejectedExit);
            Assert.Null(rejected.TaskIdentity);
            Assert.Null(rejected.BuildHostIdentity);
        }
        Assert.True(rejected.AllProcessesHaveExited());

        var success = await LoaderLifecycleHarness.StartAsync(
            fixture.Root,
            "lifecycle-success");
        await using (success)
        {
            await success.WaitForTaskReadyAsync();
            await success.ReleaseTaskAsync();
            var result = await success.ReadResultAsync();
            Assert.Equal(RepositoryLoadStatus.Success, result.Status);
            AssertProcessExit(
                await success.WaitForExitAsync(),
                LoaderLifecycleHarness.SuccessExit);
        }
        Assert.True(success.AllProcessesHaveExited());

        var mismatch = await LoaderLifecycleHarness.StartAsync(
            fixture.Root,
            "lifecycle-expect-failure");
        await using (mismatch)
        {
            await mismatch.WaitForTaskReadyAsync();
            await mismatch.ReleaseTaskAsync();
            var result = await mismatch.ReadResultAsync();
            Assert.Equal(RepositoryLoadStatus.Success, result.Status);
            AssertProcessExit(
                await mismatch.WaitForExitAsync(),
                LoaderLifecycleHarness.OutcomeMismatchExit);
        }
        Assert.True(mismatch.AllProcessesHaveExited());

        var controlFailure = await LoaderLifecycleHarness.StartAsync(
            fixture.Root,
            "lifecycle-success");
        await using (controlFailure)
        {
            await controlFailure.WaitForTaskReadyAsync();
            await controlFailure.ReleaseTaskAsync();
            var result = await controlFailure.ReadResultAsync(acknowledgement: byte.MaxValue);
            Assert.Equal(RepositoryLoadStatus.Success, result.Status);
            AssertProcessExit(
                await controlFailure.WaitForExitAsync(),
                LoaderLifecycleHarness.ControlFailureExit);
        }
        Assert.True(controlFailure.AllProcessesHaveExited());
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

        var identityFailureHarness = await LoaderLifecycleHarness.StartAsync(
            fixture.Root,
            "lifecycle-success");
        var identityFailure = await Record.ExceptionAsync(async () =>
        {
            try
            {
                await identityFailureHarness
                    .WaitForTaskReadyWithInjectedIdentityFailureAsync();
            }
            finally
            {
                await identityFailureHarness.DisposeAsync();
            }
        });
        Assert.IsType<Xunit.Sdk.EqualException>(identityFailure);
        Assert.NotNull(identityFailureHarness.TaskIdentity);
        Assert.NotNull(identityFailureHarness.BuildHostIdentity);
        Assert.True(identityFailureHarness.AllProcessesHaveExited());

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
            AssertNormalizedOutput(process, RepositoryLoadStatus.Success, string.Empty);
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
            AssertNormalizedOutput(process, RepositoryLoadStatus.Success, string.Empty);
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
            AssertNormalizedOutput(process, RepositoryLoadStatus.Success, string.Empty);
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
            AssertNormalizedOutput(process, RepositoryLoadStatus.Success, string.Empty);
            Assert.True(
                SpinWait.SpinUntil(serialization.OwnedProcessesHaveExited, TimeSpan.FromSeconds(10)));
        }
    }

    [Fact]
    public async Task PreTaskResultHangAndExitRemainDistinctBoundedObservations()
    {
        await using var fixture = await PreparedFixtureAsync();

        var postBuildHostFailure = await LoaderLifecycleHarness.StartAsync(
            fixture.Root,
            "lifecycle-post-buildhost-pre-task-failure");
        await using (postBuildHostFailure)
        {
            await postBuildHostFailure.FailAfterBuildHostStartsBeforeTaskReadyAsync();
            Assert.NotNull(postBuildHostFailure.TaskIdentity);
            Assert.NotNull(postBuildHostFailure.BuildHostIdentity);

            var observation = await postBuildHostFailure.ObserveTaskBarrierAsync();
            Assert.Equal(TaskBarrierObservationKind.ProductResult, observation.Kind);
            var result = Assert.IsType<LifecycleResult>(observation.Result);
            Assert.Equal(RepositoryLoadStatus.Failure, result.Status);
            Assert.Equal("workspace.load-failed", result.Code);
            AssertProcessExit(
                await postBuildHostFailure.WaitForExitAsync(),
                LoaderLifecycleHarness.SuccessExit);
        }
        Assert.True(postBuildHostFailure.AllProcessesHaveExited());

        await using (var failure = await LoaderLifecycleHarness.StartAsync(
                         fixture.Root,
                         "lifecycle-pre-task-unexpected"))
        {
            var observation = await failure.ObserveTaskBarrierAsync();
            Assert.Equal(TaskBarrierObservationKind.ProductResult, observation.Kind);
            var result = Assert.IsType<LifecycleResult>(observation.Result);
            Assert.Equal(RepositoryLoadStatus.Failure, result.Status);
            Assert.Equal("loader.internal-error", result.Code);
            Assert.Contains(result.Exceptions, exception =>
                exception.Phase == LoaderExecutionPhase.GraphEvaluation
                && exception.TypeChain.Contains(
                    typeof(InvalidOperationException).FullName!,
                    StringComparer.Ordinal));
            Assert.Null(failure.TaskIdentity);
            Assert.Null(failure.BuildHostIdentity);
            AssertProcessExit(
                await failure.WaitForExitAsync(),
                LoaderLifecycleHarness.SuccessExit);
        }

        var hang = await LoaderLifecycleHarness.StartAsync(
            fixture.Root,
            "lifecycle-pre-task-hang");
        try
        {
            var reached = await hang.ObserveTaskBarrierAsync();
            Assert.Equal(TaskBarrierObservationKind.PreTaskStageReached, reached.Kind);
            var timeout = await hang.ObserveTaskBarrierAsync(TimeSpan.FromMilliseconds(100));
            Assert.Equal(TaskBarrierObservationKind.TimedOut, timeout.Kind);
            Assert.False(hang.Probe.HasExited);

            hang.CloseControlPipeWithPendingReader();
            var livePipeClosure = await Record.ExceptionAsync(async () =>
                await hang.ObserveTaskBarrierAsync(TimeSpan.FromMilliseconds(100)));
            Assert.NotNull(livePipeClosure);
            Assert.True(LoaderLifecycleHarness.IsExpectedPipeClosureException(livePipeClosure));
            Assert.False(hang.Probe.HasExited);
        }
        finally
        {
            await hang.DisposeAsync();
        }
        Assert.True(hang.AllProcessesHaveExited());

        var abrupt = await LoaderLifecycleHarness.StartAsync(
            fixture.Root,
            "lifecycle-pre-task-hang");
        try
        {
            var reached = await abrupt.ObserveTaskBarrierAsync();
            Assert.Equal(TaskBarrierObservationKind.PreTaskStageReached, reached.Kind);
            abrupt.KillProbeAbruptly();
            var exit = await abrupt.ObserveTaskBarrierAsync(
                deferProcessExitUntilPipeClosure: true);
            Assert.Equal(TaskBarrierObservationKind.ProcessExited, exit.Kind);
            Assert.NotNull(exit.ExitCode);
            Assert.NotNull(exit.Process);
            Assert.False(exit.Process.StandardError.HasContent);
        }
        finally
        {
            await abrupt.DisposeAsync();
        }
        Assert.True(abrupt.AllProcessesHaveExited());
    }

    [Fact]
    public async Task PendingPipeObservationSuppressesClosureButPreservesProtocolFailure()
    {
        await LoaderLifecycleHarness.ObserveRealPipePeerClosureAsync();
        await LoaderLifecycleHarness.ObservePendingPipeTaskAsync(
            Task.FromException(new EndOfStreamException()));
        await LoaderLifecycleHarness.ObservePendingPipeTaskAsync(
            Task.FromException(new ObjectDisposedException("pipe")));
        await LoaderLifecycleHarness.ObservePendingPipeTaskAsync(
            Task.FromException(new OperationCanceledException()));
        await LoaderLifecycleHarness.ObservePendingPipeTaskAsync(
            Task.FromCanceled(new CancellationToken(canceled: true)));
        await LoaderLifecycleHarness.ObservePendingPipeTaskAsync(
            Task.FromException(new TimeoutException()));
        await LoaderLifecycleHarness.ObservePendingPipeTaskAsync(
            Task.FromException(new System.Net.Sockets.SocketException(
                (int)System.Net.Sockets.SocketError.OperationAborted)));
        await LoaderLifecycleHarness.ObservePendingPipeTaskAsync(
            Task.FromException(new System.Net.Sockets.SocketException(
                (int)System.Net.Sockets.SocketError.Interrupted)));

        var protocolFailure = await Record.ExceptionAsync(() =>
            LoaderLifecycleHarness.ObservePendingPipeTaskAsync(
                Task.Run(() => Assert.Equal(1, 2))));
        Assert.IsType<Xunit.Sdk.EqualException>(protocolFailure);

        var mixedFailure = await Record.ExceptionAsync(() =>
            LoaderLifecycleHarness.ObservePendingPipeTaskAsync(
                Task.WhenAll(
                    Task.FromException(new EndOfStreamException()),
                    Task.Run(() => Assert.Equal(1, 2)))));
        Assert.IsType<Xunit.Sdk.EqualException>(mixedFailure);

        var unrelatedSocketFailure = await Record.ExceptionAsync(() =>
            LoaderLifecycleHarness.ObservePendingPipeTaskAsync(
                Task.FromException(new System.Net.Sockets.SocketException(
                    (int)System.Net.Sockets.SocketError.HostNotFound))));
        Assert.IsType<System.Net.Sockets.SocketException>(unrelatedSocketFailure);
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

        await RunSuccessfulIterationsAsync(
            leftFixture.Root,
            rightFixture.Root,
            leftBaseline,
            rightBaseline,
            firstIteration: 1,
            iterationStep: 1,
            elapsed);
    }

    private static async Task RunSuccessfulIterationsAsync(
        string leftRoot,
        string rightRoot,
        IReadOnlyDictionary<string, InventoryEntry> leftBaseline,
        IReadOnlyDictionary<string, InventoryEntry> rightBaseline,
        int firstIteration,
        int iterationStep,
        Stopwatch elapsed)
    {
        for (var iteration = firstIteration; iteration <= 30; iteration += iterationStep)
        {
            try
            {
                await RunSuccessfulPairAsync(leftRoot, rightRoot);
                AssertProtectedInputsUnchanged(leftRoot, leftBaseline);
                AssertProtectedInputsUnchanged(rightRoot, rightBaseline);
                Assert.True(
                    elapsed.Elapsed < TimeSpan.FromMinutes(5),
                    $"The thirty-iteration topology exceeded its total deadline at iteration {iteration}.");
            }
            catch (Exception exception)
            {
                var evidence = exception is LifecyclePairPhaseException phaseFailure
                    ? $"{phaseFailure.Phase}:{phaseFailure.FailureType} after {phaseFailure.ElapsedMilliseconds}ms"
                    : exception.GetType().FullName ?? exception.GetType().Name;
                Assert.Fail(
                    $"Windows causal topology failed at iteration {iteration} with {Bound(evidence, 240)}.");
            }
        }
    }

    private static async Task<LoaderFixture> PreparedFixtureAsync() =>
        await LoaderFixture.CreateAsync(appProject: LoaderLifecycleHarness.ProbeAppProject());

    private static async Task RunSuccessfulPairAsync(string leftRoot, string rightRoot)
    {
        var elapsed = Stopwatch.StartNew();
        await using var left = await ObservePhaseAsync(
            "left.start",
            LoaderLifecycleHarness.StartAsync(leftRoot, "lifecycle-success"),
            elapsed);
        await using var right = await ObservePhaseAsync(
            "right.start",
            LoaderLifecycleHarness.StartAsync(rightRoot, "lifecycle-success"),
            elapsed);
        await Task.WhenAll(
            ObserveTaskReadyPhaseAsync("left", left, elapsed),
            ObserveTaskReadyPhaseAsync("right", right, elapsed));
        Assert.NotEqual(left.Probe.Id, right.Probe.Id);
        Assert.NotNull(left.TaskIdentity);
        Assert.NotNull(right.TaskIdentity);
        Assert.NotEqual(left.TaskIdentity, right.TaskIdentity);
        Assert.NotNull(left.BuildHostIdentity);
        Assert.NotNull(right.BuildHostIdentity);
        Assert.NotEqual(left.BuildHostIdentity, right.BuildHostIdentity);
        await Task.WhenAll(
            ObservePhaseAsync("left.task-release", left.ReleaseTaskAsync(), elapsed),
            ObservePhaseAsync("right.task-release", right.ReleaseTaskAsync(), elapsed));
        var results = await Task.WhenAll(
            ObservePhaseAsync("left.result", left.ReadResultAsync(), elapsed),
            ObservePhaseAsync("right.result", right.ReadResultAsync(), elapsed));
        Assert.All(results, result =>
        {
            Assert.Equal(RepositoryLoadStatus.Success, result.Status);
            Assert.Equal(string.Empty, result.Code);
        });
        var processes = await Task.WhenAll(
            ObservePhaseAsync("left.process-exit", left.WaitForExitAsync(), elapsed),
            ObservePhaseAsync("right.process-exit", right.WaitForExitAsync(), elapsed));
        Assert.All(processes, process =>
            AssertProcessExit(process, LoaderLifecycleHarness.SuccessExit));
        Assert.True(
            SpinWait.SpinUntil(
                () => left.OwnedProcessesHaveExited() && right.OwnedProcessesHaveExited(),
                TimeSpan.FromSeconds(10)),
            "A task process or BuildHost remained after both successful loads completed.");
    }

    private static async Task ObserveTaskReadyPhaseAsync(
        string side,
        LoaderLifecycleHarness target,
        Stopwatch elapsed)
    {
        try
        {
            await target.WaitForTaskReadyAsync();
        }
        catch (Exception exception)
        {
            var observation = target.LastTaskBarrierObservation?.Kind.ToString()
                ?? "ObservationUnavailable";
            throw new LifecyclePairPhaseException(
                $"{side}.task-ready[{observation}]",
                elapsed.ElapsedMilliseconds,
                exception);
        }
    }

    private static async Task ObservePhaseAsync(
        string phase,
        Task action,
        Stopwatch elapsed)
    {
        try
        {
            await action;
        }
        catch (Exception exception)
        {
            throw new LifecyclePairPhaseException(
                phase,
                elapsed.ElapsedMilliseconds,
                exception);
        }
    }

    private static async Task<T> ObservePhaseAsync<T>(
        string phase,
        Task<T> action,
        Stopwatch elapsed)
    {
        try
        {
            return await action;
        }
        catch (Exception exception)
        {
            throw new LifecyclePairPhaseException(
                phase,
                elapsed.ElapsedMilliseconds,
                exception);
        }
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
            $"Lifecycle probe exited {result.ExitCode}, expected {expectedExit}; "
            + $"stdout={DescribeOutput(result.StandardOutput)}; "
            + $"stderr={DescribeStream(result.StandardError)}.");
        Assert.False(
            result.StandardError.HasContent,
            $"Lifecycle probe stderr was {DescribeStream(result.StandardError)}.");
    }

    private static void AssertNormalizedOutput(
        ProcessResult result,
        RepositoryLoadStatus expectedStatus,
        string expectedCode)
    {
        Assert.False(result.StandardOutput.Truncated);
        var normalized = result.StandardOutput.Text.TrimEnd('\r', '\n');
        var separator = normalized.IndexOf(':');
        var status = default(RepositoryLoadStatus);
        var recognized = separator > 0
            && normalized.IndexOfAny(['\r', '\n']) < 0
            && Enum.TryParse<RepositoryLoadStatus>(
                normalized[..separator],
                ignoreCase: false,
                out status)
            && IsClosedDiagnosticCode(normalized[(separator + 1)..]);
        Assert.True(recognized, "Lifecycle probe stdout was not one normalized status/code line.");
        Assert.Equal(expectedStatus, status);
        Assert.Equal(expectedCode, normalized[(separator + 1)..]);
    }

    private static string DescribeOutput(BoundedProcessText output)
    {
        if (output.Truncated)
        {
            return "truncated";
        }

        var normalized = output.Text.TrimEnd('\r', '\n');
        var separator = normalized.IndexOf(':');
        if (separator < 0
            || !Enum.TryParse<RepositoryLoadStatus>(
                normalized[..separator],
                ignoreCase: false,
                out var status))
        {
            return output.HasContent ? "unrecognized" : "empty";
        }

        var code = normalized[(separator + 1)..];
        return IsClosedDiagnosticCode(code)
            ? $"{status}:{code}"
            : $"{status}:unrecognized";
    }

    private static string DescribeStream(BoundedProcessText stream) =>
        stream.Truncated ? "nonempty-truncated" : stream.HasContent ? "nonempty" : "empty";

    private static string Bound(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];

    private static bool IsClosedDiagnosticCode(string value) =>
        value.Length <= 128
        && value.All(character =>
            character is >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '.'
            or '-');

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

    private sealed class LifecyclePairPhaseException(
        string phase,
        long elapsedMilliseconds,
        Exception innerException) : Exception(null, innerException)
    {
        public string Phase { get; } = phase;

        public long ElapsedMilliseconds { get; } = elapsedMilliseconds;

        public string FailureType { get; } =
            innerException.GetType().FullName ?? innerException.GetType().Name;
    }
}

[CollectionDefinition("Integration process lane 2")]
public sealed class IntegrationProcessLaneTwoCollection;
