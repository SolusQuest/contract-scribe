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
        SubjectControl? control = null)
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

        await using var processObserver = new ProcessTreeObserver(process);
        var stdoutTask = ReadBoundedAsync(process.StandardOutput.BaseStream, standardOutputLimit, cancellationToken);
        var stderrTask = ReadBoundedAsync(process.StandardError.BaseStream, standardErrorLimit, cancellationToken);
        var controlTask = control is null
            ? Task.FromResult(true)
            : ApplyControlAsync(process, control, cancellationToken);
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
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        var controlCompleted = await controlTask.ConfigureAwait(false);
        if (!controlCompleted)
        {
            TryKill(process);
        }
        var termination = timedOut || control?.Action == "external-kill" && controlCompleted
            ? "external-kill"
            : ClassifyTermination(process.ExitCode);
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
            controlCompleted,
            processObserver.ObservationComplete,
            processObserver.Snapshot());
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
            true,
            []);

    private static async Task<bool> ApplyControlAsync(
        Process process,
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
                return false;
            }
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }

        switch (control.Action)
        {
            case "cancel":
                File.WriteAllText(Path.Join(control.ControlRoot, "cancel.requested"), string.Empty);
                File.WriteAllText(Path.Join(control.ControlRoot, $"{control.GateName}.release"), string.Empty);
                return true;
            case "external-kill":
                TryKill(process);
                return true;
            case "release-late-completion":
                File.WriteAllText(Path.Join(control.ControlRoot, "cancel.requested"), string.Empty);
                File.WriteAllText(Path.Join(control.ControlRoot, $"{control.GateName}.release"), string.Empty);
                return true;
            case "observe":
                File.WriteAllText(Path.Join(control.ControlRoot, $"{control.GateName}.release"), string.Empty);
                return true;
            default:
                return false;
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

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
        }
    }
}
