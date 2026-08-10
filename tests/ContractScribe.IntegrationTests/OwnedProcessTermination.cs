using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ContractScribe.TestSupport;

namespace ContractScribe.Roslyn.IntegrationTests;

internal sealed record OwnedProcessResult(string StandardOutput, string StandardError);

internal sealed record OwnedProcessTestHooks
{
    public Action<Process>? ProcessStarted { get; init; }

    public Func<Process, long>? ReadStartIdentity { get; init; }

    public Func<OwnedProcessIdentity, OwnedProcessExitState?>? ExitStateOverride { get; init; }

    public Action<Process>? KillProcessTree { get; init; }

    public Func<StreamReader, Task<string>>? ReadStandardOutput { get; init; }

    public Func<StreamReader, Task<string>>? ReadStandardError { get; init; }

    public TimeSpan? PollingInterval { get; init; }

    public TimeSpan? TerminationTimeout { get; init; }
}

internal static class OwnedProcessRunner
{
    private const int RetainedOutputCharacters = 64 * 1024;
    private static readonly TimeSpan DefaultTerminationTimeout = TimeSpan.FromSeconds(10);

    public static async Task<OwnedProcessResult> RunAsync(
        string fileName,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string?>? environment = null,
        OwnedProcessTestHooks? testHooks = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        if (environment is not null)
        {
            foreach (var (name, value) in environment)
            {
                startInfo.Environment[name] = value;
            }
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Owned process failed to start: {fileName}.");
        testHooks?.ProcessStarted?.Invoke(process);
        var stdout = StartReader(
            process.StandardOutput,
            testHooks?.ReadStandardOutput);
        var stderr = StartReader(
            process.StandardError,
            testHooks?.ReadStandardError);
        var terminationTimeout = testHooks?.TerminationTimeout ?? DefaultTerminationTimeout;

        OwnedProcessTreeObserver observer;
        try
        {
            observer = new OwnedProcessTreeObserver(process, testHooks);
        }
        catch (Exception observationFailure) when (IsObservationFailure(observationFailure))
        {
            if (HasConfirmedExit(process))
            {
                var completedDrain = await DrainAsync(stdout, stderr, terminationTimeout)
                    .ConfigureAwait(false);
                ThrowIfDrainFailed(completedDrain, observationFailure);
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"Owned process exited with code {process.ExitCode}. "
                        + completedDrain.FormatTails(),
                        observationFailure);
                }
                return new OwnedProcessResult(
                    completedDrain.StandardOutput,
                    completedDrain.StandardError);
            }

            var cleanupFailure = await TryTerminateDirectAsync(
                process,
                terminationTimeout,
                testHooks).ConfigureAwait(false);
            var drain = await DrainAsync(stdout, stderr, terminationTimeout).ConfigureAwait(false);
            throw new InvalidOperationException(
                "Owned process observation could not be established; the direct tree was reaped. "
                + drain.FormatTails(),
                CombineFailures(observationFailure, cleanupFailure, drain.Failures));
        }

        await using (observer.ConfigureAwait(false))
        {
            return await RunObservedAsync(
                process,
                observer,
                stdout,
                stderr,
                timeout,
                terminationTimeout,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<OwnedProcessResult> RunObservedAsync(
        Process process,
        OwnedProcessTreeObserver observer,
        Task<string> stdout,
        Task<string> stderr,
        TimeSpan timeout,
        TimeSpan terminationTimeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCancellation = new CancellationTokenSource(timeout);
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);
        using var readerMonitoring = new CancellationTokenSource();
        var exit = process.WaitForExitAsync(waitCancellation.Token);
        var stdoutFailure = ObserveReaderFailureAsync(stdout, readerMonitoring.Token);
        var stderrFailure = ObserveReaderFailureAsync(stderr, readerMonitoring.Token);
        try
        {
            var completed = await Task.WhenAny(exit, stdoutFailure, stderrFailure).ConfigureAwait(false);

            if (completed != exit)
            {
                var readFailure = await CaptureFailureAsync(completed).ConfigureAwait(false);
                var cleanupFailure = await TryTerminateObservedAsync(
                    observer,
                    process,
                    terminationTimeout).ConfigureAwait(false);
                var drain = await DrainAsync(stdout, stderr, terminationTimeout).ConfigureAwait(false);
                throw new IOException(
                    "Owned process stream observation failed; the owned tree was terminated. "
                    + observer.FormatObservedDescendants()
                    + drain.FormatTails(),
                    CombineFailures(readFailure, cleanupFailure, drain.Failures));
            }

            try
            {
                await exit.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (waitCancellation.IsCancellationRequested)
            {
                var cleanupFailure = await TryTerminateObservedAsync(
                    observer,
                    process,
                    terminationTimeout).ConfigureAwait(false);
                var drain = await DrainAsync(stdout, stderr, terminationTimeout).ConfigureAwait(false);
                var inner = CombineFailures(cleanupFailure, drain.Failures);
                var details = observer.FormatObservedDescendants() + drain.FormatTails();
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(
                        "Owned process was cancelled. " + details,
                        inner,
                        cancellationToken);
                }

                throw new TimeoutException(
                    $"Owned process exceeded {timeout}. {details}",
                    inner);
            }

            var completedOutput = await DrainAsync(stdout, stderr, terminationTimeout)
                .ConfigureAwait(false);
            ThrowIfDrainFailed(completedOutput);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Owned process exited with code {process.ExitCode}. "
                    + completedOutput.FormatTails());
            }

            return new OwnedProcessResult(
                completedOutput.StandardOutput,
                completedOutput.StandardError);
        }
        finally
        {
            readerMonitoring.Cancel();
        }
    }

    private static Task<string> StartReader(
        StreamReader reader,
        Func<StreamReader, Task<string>>? replacement)
    {
        try
        {
            return replacement?.Invoke(reader) ?? ReadBoundedAsync(reader);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            return Task.FromException<string>(exception);
        }
    }

    private static async Task ObserveReaderFailureAsync(
        Task<string> reader,
        CancellationToken stop)
    {
        await reader.ConfigureAwait(false);
        await Task.Delay(Timeout.InfiniteTimeSpan, stop).ConfigureAwait(false);
    }

    private static async Task<Exception> CaptureFailureAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
            return new IOException("The stream observer ended without reporting a failure.");
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async Task<Exception?> TryTerminateObservedAsync(
        OwnedProcessTreeObserver observer,
        Process process,
        TimeSpan timeout)
    {
        try
        {
            await observer.TerminateAsync(process, timeout).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            return exception;
        }
    }

    private static async Task<Exception?> TryTerminateDirectAsync(
        Process process,
        TimeSpan timeout,
        OwnedProcessTestHooks? hooks)
    {
        Exception? killFailure = null;
        try
        {
            if (!process.HasExited)
            {
                if (hooks?.KillProcessTree is { } kill)
                {
                    kill(process);
                }
                else
                {
                    process.Kill(entireProcessTree: true);
                }
            }
        }
        catch (Exception exception) when (IsObservationFailure(exception))
        {
            killFailure = exception;
        }

        try
        {
            await process.WaitForExitAsync().WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or InvalidOperationException)
        {
            return CombineFailures(killFailure, exception);
        }

        return killFailure;
    }

    private static bool HasConfirmedExit(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private static async Task<OwnedProcessDrain> DrainAsync(
        Task<string> stdout,
        Task<string> stderr,
        TimeSpan timeout)
    {
        var failures = new List<Exception>();
        var standardOutput = await DrainOneAsync(stdout, timeout, failures).ConfigureAwait(false);
        var standardError = await DrainOneAsync(stderr, timeout, failures).ConfigureAwait(false);
        return new OwnedProcessDrain(standardOutput, standardError, failures);
    }

    private static async Task<string> DrainOneAsync(
        Task<string> stream,
        TimeSpan timeout,
        List<Exception> failures)
    {
        try
        {
            return await stream.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            failures.Add(exception);
            return string.Empty;
        }
    }

    private static void ThrowIfDrainFailed(
        OwnedProcessDrain drain,
        Exception? primary = null)
    {
        if (drain.Failures.Count == 0)
        {
            return;
        }
        throw new IOException(
            "Owned process streams did not drain cleanly. " + drain.FormatTails(),
            CombineFailures(primary, drain.Failures));
    }

    private static Exception? CombineFailures(
        Exception? first,
        Exception? second,
        IReadOnlyList<Exception> additional)
    {
        var failures = new List<Exception>();
        if (first is not null)
        {
            failures.Add(first);
        }
        if (second is not null)
        {
            failures.Add(second);
        }
        failures.AddRange(additional);
        return failures.Count switch
        {
            0 => null,
            1 => failures[0],
            _ => new AggregateException(failures),
        };
    }

    private static Exception? CombineFailures(
        Exception? first,
        IReadOnlyList<Exception> additional) =>
        CombineFailures(first, null, additional);

    private static Exception? CombineFailures(Exception? first, Exception? second) =>
        CombineFailures(first, second, []);

    private static bool IsObservationFailure(Exception exception) => exception is
        ArgumentException
        or FileNotFoundException
        or DirectoryNotFoundException
        or InvalidOperationException
        or IOException
        or UnauthorizedAccessException
        or Win32Exception
        or NotSupportedException
        or FormatException
        or OverflowException;

    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        var retained = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0)
            {
                return retained.ToString();
            }

            retained.Append(buffer, 0, read);
            if (retained.Length > RetainedOutputCharacters)
            {
                retained.Remove(0, retained.Length - RetainedOutputCharacters);
            }
        }
    }

    private sealed record OwnedProcessDrain(
        string StandardOutput,
        string StandardError,
        IReadOnlyList<Exception> Failures)
    {
        public string FormatTails() =>
            $"stdout tail: {StandardOutput}\nstderr tail: {StandardError}";
    }
}

internal enum OwnedProcessExitState
{
    ExitedOrReused,
    StillAlive,
    ObservationUnavailable,
}

internal sealed class OwnedProcessTreeObserver : IAsyncDisposable
{
    private readonly OwnedProcessIdentity root;
    private readonly OwnedProcessTestHooks? hooks;
    private readonly ConcurrentDictionary<OwnedProcessIdentity, byte> observed = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Task polling;

    public OwnedProcessTreeObserver(Process rootProcess, OwnedProcessTestHooks? hooks = null)
    {
        this.hooks = hooks;
        root = new OwnedProcessIdentity(
            rootProcess.Id,
            ReadStartIdentity(rootProcess));
        polling = Task.Run(PollAsync);
    }

    public IReadOnlyCollection<OwnedProcessIdentity> ObservedDescendants => observed.Keys.ToArray();

    public async Task TerminateAsync(Process rootProcess, TimeSpan timeout)
    {
        CaptureOnce();
        Exception? killFailure = null;
        try
        {
            if (ReadExitState(root) != OwnedProcessExitState.ExitedOrReused)
            {
                if (hooks?.KillProcessTree is { } kill)
                {
                    kill(rootProcess);
                }
                else
                {
                    rootProcess.Kill(entireProcessTree: true);
                }
            }
        }
        catch (Exception exception) when (IsObservationException(exception))
        {
            killFailure = exception;
        }

        using var deadline = new CancellationTokenSource(timeout);
        while (!deadline.IsCancellationRequested)
        {
            CaptureOnce();
            var states = new[] { root }
                .Concat(observed.Keys)
                .Select(identity => (Identity: identity, State: ReadExitState(identity)))
                .ToArray();
            if (states.All(item => item.State == OwnedProcessExitState.ExitedOrReused))
            {
                if (killFailure is not null)
                {
                    throw new IOException(
                        "Owned process kill reported a failure before exit was confirmed.",
                        killFailure);
                }
                return;
            }
            try
            {
                await Task.Delay(50, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        var finalStates = new[] { root }
            .Concat(observed.Keys)
            .Select(identity => (Identity: identity, State: ReadExitState(identity)))
            .Where(item => item.State != OwnedProcessExitState.ExitedOrReused)
            .ToArray();
        var details = string.Join(
            ", ",
            finalStates.Select(item => $"{item.Identity.ProcessId}:{item.State}"));
        throw new TimeoutException(
            $"Owned process cleanup did not prove bounded exit for root {root.ProcessId}; "
            + $"remaining identities: {details}.",
            killFailure);
    }

    public string FormatObservedDescendants() =>
        "observed descendants: "
        + string.Join(",", observed.Keys.Select(identity => identity.ProcessId).Order())
        + ". ";

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        try
        {
            await polling.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        stop.Dispose();
    }

    private long ReadStartIdentity(Process process) =>
        hooks?.ReadStartIdentity?.Invoke(process)
        ?? StableProcessStartIdentity.Read(process);

    private async Task PollAsync()
    {
        try
        {
            while (true)
            {
                CaptureOnce();
                await Task.Delay(
                        hooks?.PollingInterval ?? TimeSpan.FromMilliseconds(250),
                        stop.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
    }

    private void CaptureOnce()
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == root.ProcessId
                        || !IsDescendantOf(process.Id, root.ProcessId))
                    {
                        continue;
                    }

                    observed.TryAdd(
                        new OwnedProcessIdentity(
                            process.Id,
                            ReadStartIdentity(process)),
                        0);
                }
                catch (Exception exception) when (IsObservationException(exception))
                {
                }
            }
        }
    }

    private OwnedProcessExitState ReadExitState(OwnedProcessIdentity identity)
    {
        if (hooks?.ExitStateOverride?.Invoke(identity) is { } overridden)
        {
            return overridden;
        }
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            if (process.HasExited)
            {
                return OwnedProcessExitState.ExitedOrReused;
            }
            return ReadStartIdentity(process) != identity.StartIdentity
                ? OwnedProcessExitState.ExitedOrReused
                : OwnedProcessExitState.StillAlive;
        }
        catch (Exception exception) when (exception is
            ArgumentException
            or FileNotFoundException
            or DirectoryNotFoundException)
        {
            return OwnedProcessExitState.ExitedOrReused;
        }
        catch (Exception exception) when (IsObservationException(exception))
        {
            return OwnedProcessExitState.ObservationUnavailable;
        }
    }

    private static bool IsDescendantOf(int processId, int ancestorProcessId)
    {
        var visited = new HashSet<int>();
        var current = processId;
        while (visited.Add(current))
        {
            var parent = ParentProcessId(current);
            if (parent == ancestorProcessId)
            {
                return true;
            }
            if (parent is null or <= 1)
            {
                return false;
            }
            current = parent.Value;
        }
        return false;
    }

    private static int? ParentProcessId(int processId)
    {
        if (OperatingSystem.IsLinux())
        {
            var stat = File.ReadAllText($"/proc/{processId}/stat");
            var commandEnd = stat.LastIndexOf(')');
            if (commandEnd < 0)
            {
                return null;
            }
            var fields = stat[(commandEnd + 2)..]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return fields.Length >= 2
                ? int.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture)
                : null;
        }
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using var process = Process.GetProcessById(processId);
        var status = NtQueryInformationProcess(
            process.Handle,
            0,
            out var information,
            Marshal.SizeOf<ProcessBasicInformation>(),
            out _);
        return status >= 0
            ? information.InheritedFromUniqueProcessId.ToInt32()
            : null;
    }

    private static bool IsObservationException(Exception exception) => exception is
        ArgumentException
        or FileNotFoundException
        or DirectoryNotFoundException
        or InvalidOperationException
        or IOException
        or UnauthorizedAccessException
        or Win32Exception
        or NotSupportedException
        or FormatException
        or OverflowException;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    // System.Diagnostics exposes no managed parent-process API. This test-only
    // query is required to keep descendant observation scoped to the exact
    // owned tree rather than matching unrelated process names.
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        out ProcessBasicInformation processInformation,
        int processInformationLength,
        out int returnLength);
}

internal readonly record struct OwnedProcessIdentity(int ProcessId, long StartIdentity);
