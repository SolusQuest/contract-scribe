using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace ContractScribe.HostValidation;

public static class SubjectProcessRunner
{
    internal static string LastObservationDiagnosticCode { get; private set; } =
        "HV944_OBSERVATION_NOT_RUN";

    internal static string LastExternalLaunchStrategy { get; private set; } =
        "HV966_EXTERNAL_LAUNCH_NOT_RUN";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async Task<ProcessExecutionResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        int standardOutputLimit,
        int standardErrorLimit,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        SubjectControl? control = null,
        IReadOnlyList<ProcessIdentityRule>? processIdentityRegistry = null,
        string? auditTemporaryRoot = null)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        if (auditTemporaryRoot is not null)
        {
            Directory.CreateDirectory(auditTemporaryRoot);
            startInfo.Environment["TMP"] = auditTemporaryRoot;
            startInfo.Environment["TEMP"] = auditTemporaryRoot;
            startInfo.Environment["TMPDIR"] = auditTemporaryRoot;
        }

        Process? process = null;
        LinuxSubjectProcess? linuxSubject = null;
        Stream? standardOutput = null;
        Stream? standardError = null;
        try
        {
            if (OperatingSystem.IsLinux()
                && control?.Action == "external-kill")
            {
                linuxSubject = LinuxSubjectProcess.Start(startInfo);
                process = linuxSubject.Process;
                standardOutput = linuxSubject.StandardOutput;
                standardError = linuxSubject.StandardError;
                LastExternalLaunchStrategy =
                    "linux-native-unregistered-child";
            }
            else
            {
                process = new Process { StartInfo = startInfo };
                if (!process.Start())
                {
                    process.Dispose();
                    return StartFailure("launch-failure");
                }
                standardOutput = process.StandardOutput.BaseStream;
                standardError = process.StandardError.BaseStream;
            }
        }
        catch (Exception exception) when (
            TryClassifyStartFailure(exception, out var processStart))
        {
            process?.Dispose();
            linuxSubject?.Dispose();
            return StartFailure(processStart);
        }
        if (process is null
            || standardOutput is null
            || standardError is null)
        {
            throw new InvalidOperationException(
                "The subject launch completed without process streams.");
        }
        using var processScope = process;
        using var linuxSubjectScope = linuxSubject;

        var executionDeadline = MonotonicDeadline.Start(timeout);
        var cleanupReserve = control?.Action == "external-kill"
            ? ComputeCleanupReserve(timeout)
            : TimeSpan.Zero;
        using var rootStatusSession =
            NativeTerminationObserver.CreateRootStatusSession(
                process,
                executionDeadline,
                control?.Action == "external-kill");
        await using var processObserver = new ProcessTreeObserver(
            process,
            processIdentityRegistry ?? [],
            rootStatusSession.OpenedIdentity);
        using var streamDeadlineSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        streamDeadlineSource.CancelAfter(executionDeadline.Remaining);
        var stdoutTask = ReadBoundedAsync(
            standardOutput,
            standardOutputLimit,
            streamDeadlineSource.Token,
            cancellationToken);
        var stderrTask = ReadBoundedAsync(
            standardError,
            standardErrorLimit,
            streamDeadlineSource.Token,
            cancellationToken);
        var controlTask = control is null
            ? Task.FromResult(new ControlExecutionResult(true, null))
            : ApplyControlAsync(
                 process,
                 processObserver,
                 rootStatusSession,
                 control,
                 executionDeadline,
                 cleanupReserve,
                 cancellationToken);
        var timedOut = false;
        ControlExecutionResult controlResult;
        int? exitCode;
        NativeTerminationEvidence? nativeTermination = null;
        if (control?.Action == "external-kill")
        {
            try
            {
                controlResult = await controlTask.ConfigureAwait(false);
                nativeTermination = controlResult.NativeTermination;
            }
            catch (OperationCanceledException)
            {
                _ = NativeTerminationObserver.TerminateTreeAndCapture(
                    process,
                    rootStatusSession,
                    processObserver.CaptureTerminationPlan(),
                    processObserver.IsCurrentTerminationTarget,
                    executionDeadline,
                    CancellationToken.None);
                throw;
            }
            exitCode = nativeTermination?.ManagedExitCode;
        }
        else
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                timedOut = true;
                _ = TryKill(process);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _ = TryKill(process);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            controlResult = await controlTask.ConfigureAwait(false);
            exitCode = process.ExitCode;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (control?.Action == "external-kill"
            && (!stdout.Complete || !stderr.Complete))
        {
            controlResult = controlResult with
            {
                Completed = false,
                Outcome = "stream-timeout"
            };
        }
        if (!controlResult.Completed && control?.Action != "external-kill")
        {
            _ = TryKill(process);
        }
        var platformTermination = ClassifyTermination(exitCode, nativeTermination);
        LastObservationDiagnosticCode = !processObserver.ObservationComplete
            ? processObserver.DiagnosticCode
            : !stdout.Complete
                ? "HV945_STANDARD_OUTPUT_INCOMPLETE"
                : !stderr.Complete
                    ? "HV946_STANDARD_ERROR_INCOMPLETE"
                    : "HV000_OBSERVATION_COMPLETE";
        var confirmedExternalKill = control?.Action == "external-kill"
            && controlResult.Completed
            && NativeTerminationObserver.IsTerminationFullyObserved(
                nativeTermination,
                stdout.Complete && stderr.Complete);
        var observedControlOutcome = control?.Action == "external-kill"
            && nativeTermination?.KillRequestOutcome == "issued"
                ? confirmedExternalKill
                    ? "issued-and-observed"
                    : "issued-but-not-observed"
                : controlResult.Outcome;
        var termination = timedOut || confirmedExternalKill
            ? "external-kill"
            : platformTermination;
        return new ProcessExecutionResult(
            exitCode,
            "started",
            termination,
            stdout.Bytes,
            stderr.Bytes,
            stdout.Overflow,
            stderr.Overflow,
            IsValidUtf8(stdout.Bytes),
            IsValidUtf8(stderr.Bytes),
            timedOut,
            controlResult.Completed,
            observedControlOutcome,
            processObserver.ObservationComplete
                && stdout.Complete
                && stderr.Complete,
            processObserver.Snapshot(),
            control?.Action == "external-kill" ? nativeTermination?.KillRequestOutcome : null,
            platformTermination,
            nativeTermination?.Kind,
            nativeTermination?.Code,
            controlResult.TemporaryDiskHighWater);
    }

    private static ProcessExecutionResult StartFailure(string processStart) =>
        new(
            null,
            processStart,
            "not-started",
            [],
            [],
            false,
            false,
            true,
            true,
            false,
            true,
            null,
            true,
            []);

    private static bool TryClassifyStartFailure(
        Exception exception,
        out string processStart)
    {
        processStart = exception switch
        {
            Win32Exception { NativeErrorCode: 5 or 13 }
                or UnauthorizedAccessException => "permission-failure",
            Win32Exception { NativeErrorCode: 8 or 12 or 193 or 216 } =>
                "runtime-load-failure",
            Win32Exception or FileNotFoundException => "launch-failure",
            _ => string.Empty
        };
        return processStart.Length > 0;
    }

    private static async Task<ControlExecutionResult> ApplyControlAsync(
        Process process,
        ProcessTreeObserver processObserver,
        NativeTerminationObserver.RootStatusSession rootStatusSession,
        SubjectControl control,
        MonotonicDeadline deadline,
        TimeSpan cleanupReserve,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(control.ControlRoot);
        var reachedPath = Path.Join(control.ControlRoot, $"{control.GateName}.reached");
        while (!File.Exists(reachedPath))
        {
            var exited = control.Action == "external-kill"
                ? NativeTerminationObserver.IsRootExitedNonReaping(
                    process,
                    rootStatusSession)
                : process.HasExited;
            if (exited
                || RemainingExcludingReserve(
                    deadline,
                    cleanupReserve) == TimeSpan.Zero)
            {
                if (control.Action == "external-kill")
                {
                    var native = NativeTerminationObserver.TerminateTreeAndCapture(
                        process,
                        rootStatusSession,
                        processObserver.CaptureTerminationPlan(),
                        processObserver.IsCurrentTerminationTarget,
                        deadline,
                        cancellationToken);
                    return new(
                        false,
                        exited ? "already-exited" : "gate-timeout",
                        native);
                }
                return new(false, exited ? "already-exited" : "gate-timeout");
            }
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
        if (control.ActionDelay > TimeSpan.Zero)
        {
            var remaining = RemainingExcludingReserve(
                deadline,
                cleanupReserve);
            if (remaining == TimeSpan.Zero)
            {
                if (control.Action == "external-kill")
                {
                    var native = NativeTerminationObserver.TerminateTreeAndCapture(
                        process,
                        rootStatusSession,
                        processObserver.CaptureTerminationPlan(),
                        processObserver.IsCurrentTerminationTarget,
                        deadline,
                        cancellationToken);
                    return new(false, "control-timeout", native);
                }
                return new(false, "control-timeout");
            }
            await Task.Delay(
                control.ActionDelay < remaining
                    ? control.ActionDelay
                    : remaining,
                cancellationToken).ConfigureAwait(false);
            if (RemainingExcludingReserve(
                    deadline,
                    cleanupReserve) == TimeSpan.Zero)
            {
                if (control.Action == "external-kill")
                {
                    var native = NativeTerminationObserver.TerminateTreeAndCapture(
                        process,
                        rootStatusSession,
                        processObserver.CaptureTerminationPlan(),
                        processObserver.IsCurrentTerminationTarget,
                        deadline,
                        cancellationToken);
                    return new(false, "control-timeout", native);
                }
                return new(false, "control-timeout");
            }
        }
        if (control.WaitForExitBeforeAction)
        {
            if (control.Action != "external-kill")
            {
                return new(false, "unsupported-control");
            }
            var naturalExit =
                await NativeTerminationObserver.WaitForNaturalExitAsync(
                    process,
                    rootStatusSession,
                    () => File.WriteAllText(
                        Path.Join(
                            control.ControlRoot,
                            $"{control.GateName}.release"),
                        string.Empty),
                    deadline,
                    cleanupReserve,
                    cancellationToken).ConfigureAwait(false);
            if (naturalExit.KillRequestOutcome == "already-exited")
            {
                return new(false, naturalExit.KillRequestOutcome, naturalExit);
            }
            var cleanup = NativeTerminationObserver.TerminateTreeAndCapture(
                process,
                rootStatusSession,
                processObserver.CaptureTerminationPlan(),
                processObserver.IsCurrentTerminationTarget,
                deadline,
                cancellationToken);
            return new(false, "natural-exit-timeout", cleanup);
        }

        switch (control.Action)
        {
            case "cancel":
                File.WriteAllText(Path.Join(control.ControlRoot, "cancel.requested"), string.Empty);
                File.WriteAllText(Path.Join(control.ControlRoot, $"{control.GateName}.release"), string.Empty);
                return new(true, "requested");
            case "external-kill":
                var generation = processObserver.CompletedSampleGeneration;
                if (!await processObserver.WaitForSampleAfterAsync(
                        generation,
                        RemainingExcludingReserve(
                            deadline,
                            cleanupReserve),
                        cancellationToken).ConfigureAwait(false))
                {
                    var incompleteNative = NativeTerminationObserver.TerminateTreeAndCapture(
                        process,
                        rootStatusSession,
                        processObserver.CaptureTerminationPlan(),
                       processObserver.IsCurrentTerminationTarget,
                       deadline,
                       cancellationToken);
                    return new(false, "post-gate-sample-missing", incompleteNative);
                }
                var native = NativeTerminationObserver.TerminateTreeAndCapture(
                    process,
                    rootStatusSession,
                    processObserver.CaptureTerminationPlan(),
                    processObserver.IsCurrentTerminationTarget,
                    deadline,
                    cancellationToken);
                return new(native.KillRequestOutcome == "issued", native.KillRequestOutcome, native);
            case "release-late-completion":
                File.WriteAllText(Path.Join(control.ControlRoot, "cancel.requested"), string.Empty);
                File.WriteAllText(Path.Join(control.ControlRoot, $"{control.GateName}.release"), string.Empty);
                return new(true, "released");
            case "observe":
                var sampleGeneration = processObserver.CompletedSampleGeneration;
                if (!await processObserver.WaitForSampleAfterAsync(
                        sampleGeneration,
                        deadline.Remaining,
                        cancellationToken).ConfigureAwait(false))
                {
                    return new(false, "post-gate-sample-missing");
                }
                File.WriteAllText(Path.Join(control.ControlRoot, $"{control.GateName}.release"), string.Empty);
                return new(true, "observed");
            case "measure-temporary-disk":
                if (control.MeasureTemporaryDisk is null)
                {
                    return new(false, "unsupported-control");
                }
                var measurement = control.MeasureTemporaryDisk(
                    () => File.WriteAllText(
                        Path.Join(
                            control.ControlRoot,
                            $"{control.GateName}.release"),
                        string.Empty),
                    deadline);
                return new(true, "observed", TemporaryDiskHighWater: measurement);
            default:
                return new(false, "unsupported-control");
        }
    }

    private static TimeSpan ComputeCleanupReserve(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }
        var proportional = TimeSpan.FromTicks(timeout.Ticks / 4);
        var minimum = timeout < TimeSpan.FromSeconds(2)
            ? TimeSpan.FromTicks(timeout.Ticks / 2)
            : TimeSpan.FromSeconds(1);
        return proportional < minimum
            ? minimum
            : proportional > TimeSpan.FromSeconds(5)
                ? TimeSpan.FromSeconds(5)
                : proportional;
    }

    private static TimeSpan RemainingExcludingReserve(
        MonotonicDeadline deadline,
        TimeSpan reserve)
    {
        var remaining = deadline.Remaining;
        return remaining <= reserve
            ? TimeSpan.Zero
            : remaining - reserve;
    }

    private static async Task<(byte[] Bytes, bool Overflow, bool Complete)> ReadBoundedAsync(
        Stream stream,
        int limit,
        CancellationToken deadlineToken,
        CancellationToken callerCancellationToken)
    {
        var buffer = new byte[8192];
        using var captured = new MemoryStream(Math.Min(limit, 64 * 1024));
        var overflow = false;
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(
                    buffer,
                    deadlineToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                var remaining = limit - checked((int)captured.Length);
                if (remaining > 0)
                {
                    captured.Write(buffer, 0, Math.Min(read, remaining));
                }
                if (read > remaining)
                {
                    overflow = true;
                }
            }
        }
        catch (OperationCanceledException) when (
            !callerCancellationToken.IsCancellationRequested)
        {
            return (captured.ToArray(), overflow, false);
        }
        return (captured.ToArray(), overflow, true);
    }

    internal static async Task<bool> ReadCompletesBeforeDeadlineForSelfTestAsync(
        Stream stream,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var deadlineSource =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        deadlineSource.CancelAfter(timeout);
        var result = await ReadBoundedAsync(
            stream,
            1024,
            deadlineSource.Token,
            cancellationToken).ConfigureAwait(false);
        return result.Complete;
    }

    private static bool IsValidUtf8(byte[] bytes)
    {
        try
        {
            _ = StrictUtf8.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static string ClassifyTermination(
        int? exitCode,
        NativeTerminationEvidence? nativeTermination)
    {
        if (nativeTermination?.Kind == "unix-signal")
        {
            return nativeTermination.Code == 6 ? "abort" : "fatal-runtime-termination";
        }
        if (exitCode is null)
        {
            return "crash";
        }
        var unsigned = unchecked((uint)exitCode);
        return unsigned switch
        {
            0 => "normal",
            0xC0000017 => "out-of-memory",
            0xC00000FD => "stack-overflow",
            0x40000015 => "abort",
            _ when exitCode is 134 or 6 => "abort",
            _ => "crash"
        };
    }

    private static string TryKill(Process process)
    {
        if (process.HasExited)
        {
            return "already-exited";
        }
        try
        {
            process.Kill(entireProcessTree: true);
            return "issued";
        }
        catch (InvalidOperationException)
        {
            return "already-exited";
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 5 or 13)
        {
            return "permission-failure";
        }
        catch (Win32Exception)
        {
            return "indeterminate";
        }
        catch (NotSupportedException)
        {
            return "unsupported";
        }
    }
}
