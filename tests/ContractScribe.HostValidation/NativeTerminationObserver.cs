using System.Runtime.InteropServices;
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace ContractScribe.HostValidation;

public static class NativeTerminationObserver
{
    public const uint WindowsTerminationSentinel = 0xE02600F1;
    public const int UnixSigKill = 9;

    private const int Esrch = 3;
    private const int Eintr = 4;
    private const int Eperm = 1;
    private const int Echild = 10;
    private const int WnoHang = 1;
    private const uint ProcessTerminate = 0x0001;
    private const uint Synchronize = 0x00100000;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint WaitObject0 = 0x00000000;
    private const uint WaitTimeout = 0x00000102;

    public static bool IsAliveNonReaping(int processId)
    {
        if (OperatingSystem.IsWindows())
        {
            using var handle = OpenProcess(
                Synchronize | ProcessQueryLimitedInformation,
                false,
                checked((uint)processId));
            if (handle.IsInvalid)
            {
                return false;
            }
            return WaitForSingleObject(handle, 0) == WaitTimeout;
        }

        if (OperatingSystem.IsLinux() && IsLinuxExitedNonReaping(processId))
        {
            return false;
        }
        var result = Kill(processId, 0);
        if (result == 0)
        {
            return true;
        }
        return Marshal.GetLastPInvokeError() == Eperm;
    }

    public static NativeTerminationEvidence TerminateTreeAndCapture(
        Process rootProcess,
        IReadOnlyList<ObservedProcess> observedProcesses)
    {
        if (OperatingSystem.IsWindows())
        {
            return TerminateWindows(
                rootProcess.SafeHandle,
                rootProcess.Id,
                observedProcesses);
        }
        if (OperatingSystem.IsLinux())
        {
            return TerminateLinux(rootProcess.Id, observedProcesses);
        }
        return new("unsupported", null, null, "unsupported", false);
    }

    public static async Task<NativeTerminationEvidence?> WaitForNaturalExitAsync(
        Process rootProcess,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (OperatingSystem.IsWindows())
            {
                if (WaitForSingleObject(rootProcess.SafeHandle, 0) == WaitObject0)
                {
                    return CaptureWindowsExit(
                        rootProcess.SafeHandle,
                        "already-exited",
                        false);
                }
            }
            else if (OperatingSystem.IsLinux())
            {
                int waitResult;
                int rawStatus;
                do
                {
                    waitResult = WaitPid(rootProcess.Id, out rawStatus, WnoHang);
                }
                while (waitResult < 0
                    && Marshal.GetLastPInvokeError() == Eintr);
                if (waitResult == rootProcess.Id)
                {
                    return CaptureLinuxStatus(
                        rawStatus,
                        "already-exited",
                        causalMatch: false);
                }
                if (waitResult < 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    return new(
                        "unix-wait-status",
                        null,
                        null,
                        error == Echild ? "already-exited" : "indeterminate",
                        false);
                }
            }
            else if (!IsAliveNonReaping(rootProcess.Id))
            {
                return new("unsupported", null, null, "already-exited", false);
            }
            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    private static NativeTerminationEvidence TerminateWindows(
        SafeProcessHandle rootHandle,
        int rootProcessId,
        IReadOnlyList<ObservedProcess> observedProcesses)
    {
        var descendantFailure = TerminateWindowsDescendants(rootProcessId, observedProcesses);
        if (rootHandle.IsInvalid)
        {
            return WindowsOpenFailure();
        }
        if (WaitForSingleObject(rootHandle, 0) == WaitObject0)
        {
            return CaptureWindowsExit(rootHandle, "already-exited", false);
        }
        if (!TerminateProcess(rootHandle, WindowsTerminationSentinel))
        {
            return WindowsOperationFailure();
        }
        if (WaitForSingleObject(rootHandle, 30_000) != WaitObject0)
        {
            return new("windows-terminate-process", null, WindowsTerminationSentinel, "indeterminate", false);
        }

        var captured = CaptureWindowsExit(rootHandle, "issued", true);
        return descendantFailure is null
            ? captured
            : captured with { KillRequestOutcome = descendantFailure, CausalMatch = false };
    }

    private static string? TerminateWindowsDescendants(
        int rootProcessId,
        IReadOnlyList<ObservedProcess> observedProcesses)
    {
        foreach (var descendant in observedProcesses
                     .Where(process => process.ProcessId != rootProcessId)
                     .OrderByDescending(process => process.ProcessId))
        {
            using var handle = OpenProcess(
                ProcessTerminate | Synchronize | ProcessQueryLimitedInformation,
                false,
                checked((uint)descendant.ProcessId));
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error is 5)
                {
                    return "permission-failure";
                }
                continue;
            }
            if (WaitForSingleObject(handle, 0) == WaitObject0)
            {
                continue;
            }
            if (!TerminateProcess(handle, WindowsTerminationSentinel))
            {
                return Marshal.GetLastPInvokeError() == 5
                    ? "permission-failure"
                    : "indeterminate";
            }
            _ = WaitForSingleObject(handle, 30_000);
        }
        return null;
    }

    private static NativeTerminationEvidence CaptureWindowsExit(
        SafeProcessHandle handle,
        string requestOutcome,
        bool issued)
    {
        if (!GetExitCodeProcess(handle, out var exitCode))
        {
            return new("windows-terminate-process", null, null, "indeterminate", false);
        }
        return new(
            "windows-terminate-process",
            unchecked((int)exitCode),
            exitCode,
            requestOutcome,
            issued && exitCode == WindowsTerminationSentinel);
    }

    private static NativeTerminationEvidence WindowsOpenFailure()
    {
        var error = Marshal.GetLastPInvokeError();
        return new(
            "windows-terminate-process",
            null,
            null,
            error == 5 ? "permission-failure" : "already-exited",
            false);
    }

    private static NativeTerminationEvidence WindowsOperationFailure()
    {
        var error = Marshal.GetLastPInvokeError();
        return new(
            "windows-terminate-process",
            null,
            WindowsTerminationSentinel,
            error == 5 ? "permission-failure" : "indeterminate",
            false);
    }

    private static NativeTerminationEvidence TerminateLinux(
        int rootProcessId,
        IReadOnlyList<ObservedProcess> observedProcesses)
    {
        var descendantFailure = TerminateLinuxDescendants(rootProcessId, observedProcesses);
        var killResult = Kill(rootProcessId, UnixSigKill);
        var killError = killResult == 0 ? 0 : Marshal.GetLastPInvokeError();
        if (killResult != 0 && killError is not Esrch)
        {
            return new(
                "unix-wait-status",
                null,
                null,
                killError == Eperm ? "permission-failure" : "indeterminate",
                false);
        }

        int waitResult;
        int rawStatus;
        do
        {
            waitResult = WaitPid(rootProcessId, out rawStatus, 0);
        }
        while (waitResult < 0 && Marshal.GetLastPInvokeError() == Eintr);

        if (waitResult != rootProcessId)
        {
            return new(
                "unix-wait-status",
                null,
                null,
                killResult == 0 ? "issued" : "already-exited",
                false);
        }

        var requestOutcome = descendantFailure
            ?? (killResult == 0 ? "issued" : "already-exited");
        return CaptureLinuxStatus(
            rawStatus,
            requestOutcome,
            descendantFailure is null && killResult == 0);
    }

    private static NativeTerminationEvidence CaptureLinuxStatus(
        int rawStatus,
        string requestOutcome,
        bool causalMatch)
    {
        if (IsSignaled(rawStatus))
        {
            var signal = TermSignal(rawStatus);
            return new(
                "unix-signal",
                null,
                signal,
                requestOutcome,
                causalMatch && signal == UnixSigKill);
        }
        if (IsExited(rawStatus))
        {
            return new(
                "unix-exit",
                ExitStatus(rawStatus),
                ExitStatus(rawStatus),
                requestOutcome,
                false);
        }
        return new("unix-wait-status", null, rawStatus, requestOutcome, false);
    }

    private static string? TerminateLinuxDescendants(
        int rootProcessId,
        IReadOnlyList<ObservedProcess> observedProcesses)
    {
        foreach (var descendant in observedProcesses
                     .Where(process => process.ProcessId != rootProcessId)
                     .OrderByDescending(process => process.ProcessId))
        {
            if (Kill(descendant.ProcessId, UnixSigKill) == 0)
            {
                continue;
            }
            var error = Marshal.GetLastPInvokeError();
            if (error == Esrch)
            {
                continue;
            }
            return error == Eperm ? "permission-failure" : "indeterminate";
        }
        return null;
    }

    internal static bool IsExited(int rawStatus) => (rawStatus & 0x7f) == 0;

    internal static int ExitStatus(int rawStatus) => (rawStatus >> 8) & 0xff;

    internal static bool IsSignaled(int rawStatus)
    {
        var signal = rawStatus & 0x7f;
        return signal != 0 && signal != 0x7f;
    }

    internal static int TermSignal(int rawStatus) => rawStatus & 0x7f;

    private static bool IsLinuxExitedNonReaping(int processId)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{processId}/stat");
            return IsExitedProcStat(stat);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool IsExitedProcStat(string stat)
    {
        var commandEnd = stat.LastIndexOf(')');
        return commandEnd >= 0
            && commandEnd + 2 < stat.Length
            && stat[commandEnd + 2] is 'Z' or 'X';
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int processId, int signal);

    [DllImport("libc", EntryPoint = "waitpid", SetLastError = true)]
    private static extern int WaitPid(int processId, out int status, int options);
}
