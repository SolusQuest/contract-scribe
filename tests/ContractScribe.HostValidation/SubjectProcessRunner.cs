using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace ContractScribe.HostValidation;

public static class SubjectProcessRunner
{
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

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return StartFailure("launch-failure");
            }
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 5 or 13)
        {
            return StartFailure("permission-failure");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 8 or 193 or 216)
        {
            return StartFailure("runtime-load-failure");
        }
        catch (Win32Exception)
        {
            return StartFailure("launch-failure");
        }
        catch (FileNotFoundException)
        {
            return StartFailure("launch-failure");
        }
        catch (UnauthorizedAccessException)
        {
            return StartFailure("permission-failure");
        }

        await using var processObserver = new ProcessTreeObserver(
            process,
            processIdentityRegistry ?? []);
        var stdoutTask = ReadBoundedAsync(process.StandardOutput.BaseStream, standardOutputLimit, cancellationToken);
        var stderrTask = ReadBoundedAsync(process.StandardError.BaseStream, standardErrorLimit, cancellationToken);
        var controlTask = control is null
            ? Task.FromResult(new ControlExecutionResult(true, null))
            : ApplyControlAsync(process, processObserver, control, cancellationToken);
        var timedOut = false;
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

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        var controlResult = await controlTask.ConfigureAwait(false);
        if (!controlResult.Completed)
        {
            _ = TryKill(process);
        }
        var platformTermination = ClassifyTermination(process.ExitCode);
        var confirmedExternalKill = control?.Action == "external-kill"
            && controlResult.Outcome == "issued"
            && IsForcedTerminationCompatible(process.ExitCode);
        var observedControlOutcome = control?.Action == "external-kill"
            && controlResult.Outcome == "issued"
                ? confirmedExternalKill
                    ? "issued-and-observed"
                    : "issued-but-not-observed"
                : controlResult.Outcome;
        var termination = timedOut || confirmedExternalKill
            ? "external-kill"
            : platformTermination;
        return new ProcessExecutionResult(
            process.ExitCode,
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
            processObserver.ObservationComplete,
            processObserver.Snapshot(),
            control?.Action == "external-kill" ? controlResult.Outcome : null,
            platformTermination);
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

    private static async Task<ControlExecutionResult> ApplyControlAsync(
        Process process,
        ProcessTreeObserver processObserver,
        SubjectControl control,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(control.ControlRoot);
        var reachedPath = Path.Join(control.ControlRoot, $"{control.GateName}.reached");
        var deadline = DateTime.UtcNow + control.GateTimeout;
        while (!File.Exists(reachedPath))
        {
            if (process.HasExited || DateTime.UtcNow >= deadline)
            {
                return new(false, process.HasExited ? "already-exited" : "gate-timeout");
            }
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
        if (control.ActionDelay > TimeSpan.Zero)
        {
            await Task.Delay(control.ActionDelay, cancellationToken).ConfigureAwait(false);
        }

        switch (control.Action)
        {
            case "cancel":
                File.WriteAllText(Path.Join(control.ControlRoot, "cancel.requested"), string.Empty);
                File.WriteAllText(Path.Join(control.ControlRoot, $"{control.GateName}.release"), string.Empty);
                return new(true, "requested");
            case "external-kill":
                var killOutcome = TryKill(process);
                return new(killOutcome == "issued", killOutcome);
            case "release-late-completion":
                File.WriteAllText(Path.Join(control.ControlRoot, "cancel.requested"), string.Empty);
                File.WriteAllText(Path.Join(control.ControlRoot, $"{control.GateName}.release"), string.Empty);
                return new(true, "released");
            case "observe":
                var sampleGeneration = processObserver.CompletedSampleGeneration;
                if (!await processObserver.WaitForSampleAfterAsync(
                        sampleGeneration,
                        control.GateTimeout,
                        cancellationToken).ConfigureAwait(false))
                {
                    return new(false, "post-gate-sample-missing");
                }
                File.WriteAllText(Path.Join(control.ControlRoot, $"{control.GateName}.release"), string.Empty);
                return new(true, "observed");
            default:
                return new(false, "unsupported-control");
        }
    }

    private static async Task<(byte[] Bytes, bool Overflow)> ReadBoundedAsync(
        Stream stream,
        int limit,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var captured = new MemoryStream(Math.Min(limit, 64 * 1024));
        var overflow = false;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
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

        return (captured.ToArray(), overflow);
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

    private static string ClassifyTermination(int exitCode)
    {
        var unsigned = unchecked((uint)exitCode);
        return unsigned switch
        {
            0 => "normal",
            0xC0000017 => "out-of-memory",
            0xC00000FD => "stack-overflow",
            0x40000015 => "abort",
            _ when exitCode is 134 or 6 => "abort",
            _ when exitCode is 137 or 9 => "fatal-runtime-termination",
            _ => "crash"
        };
    }

    private static bool IsForcedTerminationCompatible(int exitCode)
    {
        if (OperatingSystem.IsWindows())
        {
            var status = unchecked((uint)exitCode);
            return status is 0xffffffff or 0xc000013a
                || status is >= 0xc0000000 and <= 0xcfffffff;
        }
        return exitCode is 9 or 137;
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
