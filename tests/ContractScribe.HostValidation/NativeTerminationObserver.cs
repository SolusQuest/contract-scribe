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
    private const short PollIn = 0x0001;
    private const uint ProcessTerminate = 0x0001;
    private const uint Synchronize = 0x00100000;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint WaitObject0 = 0x00000000;
    private const uint WaitTimeout = 0x00000102;

    internal static string LastDiagnosticCode { get; private set; } =
        "HV932_NATIVE_CONTROL_NOT_RUN";

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
        ProcessTerminationPlan terminationPlan,
        Func<ProcessTerminationTarget, bool> validateCurrentTarget,
        MonotonicDeadline deadline,
        CancellationToken cancellationToken)
    {
        LastDiagnosticCode = "HV932_NATIVE_CONTROL_STARTED";
        if (OperatingSystem.IsWindows())
        {
            return TerminateWindows(
                rootProcess.SafeHandle,
                rootProcess.Id,
                terminationPlan,
                validateCurrentTarget,
                deadline,
                cancellationToken);
        }
        if (OperatingSystem.IsLinux())
        {
            return TerminateLinux(
                rootProcess.Id,
                terminationPlan,
                validateCurrentTarget,
                deadline,
                cancellationToken);
        }
        return new("unsupported", null, null, "unsupported", false);
    }

    public static async Task<NativeTerminationEvidence> WaitForNaturalExitAsync(
        Process rootProcess,
        Action release,
        MonotonicDeadline deadline,
        CancellationToken cancellationToken)
    {
        var exit = rootProcess.WaitForExitAsync(cancellationToken);
        release();
        var remaining = deadline.Remaining;
        if (remaining == TimeSpan.Zero)
        {
            return new("unsupported", null, null, "indeterminate", false);
        }
        try
        {
            await exit.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new("unsupported", null, null, "indeterminate", false);
        }
        return OperatingSystem.IsWindows()
            ? CaptureWindowsExit(
                rootProcess.SafeHandle,
                "already-exited",
                false)
            : new(
                "unsupported",
                rootProcess.ExitCode,
                rootProcess.ExitCode,
                "already-exited",
                false);
    }

    private static NativeTerminationEvidence TerminateWindows(
        SafeProcessHandle rootHandle,
        int rootProcessId,
        ProcessTerminationPlan terminationPlan,
        Func<ProcessTerminationTarget, bool> validateCurrentTarget,
        MonotonicDeadline deadline,
        CancellationToken cancellationToken)
    {
        var descendantFailure = terminationPlan.Complete
            && terminationPlan.Root.ProcessId == rootProcessId
            && terminationPlan.Root.StartIdentity != 0
                ? null
                : "indeterminate";
        if (descendantFailure is not null)
        {
            LastDiagnosticCode = "HV933_WINDOWS_TERMINATION_PLAN_INCOMPLETE";
        }
        foreach (var descendant in terminationPlan.Descendants
                     .OrderByDescending(target => target.Depth))
        {
            using var handle = OpenProcess(
                ProcessTerminate | Synchronize | ProcessQueryLimitedInformation,
                false,
                checked((uint)descendant.Identity.ProcessId));
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error != 87)
                {
                    descendantFailure = MergeFailure(
                        descendantFailure,
                        error == 5 ? "permission-failure" : "indeterminate");
                }
                continue;
            }
            if (!TryGetWindowsStartIdentity(handle, out var startIdentity)
                || startIdentity != descendant.Identity.StartIdentity
                || !validateCurrentTarget(descendant))
            {
                descendantFailure = MergeFailure(
                    descendantFailure,
                    "indeterminate");
                continue;
            }
            if (WaitForSingleObject(handle, 0) == WaitObject0)
            {
                continue;
            }
            if (!TerminateProcess(handle, WindowsTerminationSentinel))
            {
                descendantFailure = MergeFailure(
                    descendantFailure,
                    Marshal.GetLastPInvokeError() == 5
                        ? "permission-failure"
                        : "indeterminate");
                continue;
            }
            if (!WaitForWindowsExit(handle, deadline, cancellationToken))
            {
                descendantFailure = MergeFailure(
                    descendantFailure,
                    "indeterminate");
            }
        }
        if (rootHandle.IsInvalid)
        {
            return WindowsOpenFailure();
        }
        if (!TryGetWindowsStartIdentity(rootHandle, out var rootStartIdentity)
            || rootStartIdentity != terminationPlan.Root.StartIdentity)
        {
            descendantFailure = MergeFailure(
                descendantFailure,
                "indeterminate");
        }
        if (WaitForSingleObject(rootHandle, 0) == WaitObject0)
        {
            return CaptureWindowsExit(rootHandle, "already-exited", false);
        }
        if (!TerminateProcess(rootHandle, WindowsTerminationSentinel))
        {
            return WindowsOperationFailure();
        }
        if (!WaitForWindowsExit(rootHandle, deadline, cancellationToken))
        {
            return new("windows-terminate-process", null, WindowsTerminationSentinel, "indeterminate", false);
        }

        var captured = CaptureWindowsExit(rootHandle, "issued", true);
        if (descendantFailure is null && captured.CausalMatch)
        {
            LastDiagnosticCode = "HV000_NATIVE_CONTROL_COMPLETE";
        }
        return descendantFailure is null
            ? captured
            : captured with { KillRequestOutcome = descendantFailure, CausalMatch = false };
    }

    private static bool WaitForWindowsExit(
        SafeProcessHandle handle,
        MonotonicDeadline deadline,
        CancellationToken cancellationToken)
    {
        while (!deadline.IsExpired)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = WaitForSingleObject(
                handle,
                checked((uint)deadline.NextWaitMilliseconds(50)));
            if (result == WaitObject0)
            {
                return true;
            }
            if (result != WaitTimeout)
            {
                return false;
            }
        }
        return false;
    }

    private static bool TryGetWindowsStartIdentity(
        SafeProcessHandle handle,
        out long startIdentity)
    {
        startIdentity = 0;
        if (!GetProcessTimes(
                handle,
                out var creation,
                out _,
                out _,
                out _))
        {
            return false;
        }
        startIdentity = unchecked(
            ((long)creation.HighDateTime << 32) | creation.LowDateTime);
        return startIdentity != 0;
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
        ProcessTerminationPlan terminationPlan,
        Func<ProcessTerminationTarget, bool> validateCurrentTarget,
        MonotonicDeadline deadline,
        CancellationToken cancellationToken)
    {
        var descendantFailure = terminationPlan.Complete
            && terminationPlan.Root.ProcessId == rootProcessId
            && terminationPlan.Root.StartIdentity != 0
                ? null
                : "indeterminate";
        if (descendantFailure is not null)
        {
            LastDiagnosticCode = "HV934_LINUX_TERMINATION_PLAN_INCOMPLETE";
        }
        foreach (var descendant in terminationPlan.Descendants
                     .OrderByDescending(target => target.Depth))
        {
            var processFileDescriptor = PidFdOpen(
                descendant.Identity.ProcessId,
                0);
            if (processFileDescriptor < 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error != Esrch)
                {
                    descendantFailure = MergeFailure(
                        descendantFailure,
                        error == Eperm ? "permission-failure" : "indeterminate");
                }
                continue;
            }
            try
            {
                if (!TryReadLinuxStartIdentity(
                        descendant.Identity.ProcessId,
                        out var startIdentity))
                {
                    if (!PollLinuxExit(
                            processFileDescriptor,
                            MonotonicDeadline.Start(TimeSpan.Zero),
                            cancellationToken))
                    {
                        descendantFailure = MergeFailure(
                            descendantFailure,
                            "indeterminate");
                    }
                    continue;
                }
                if (startIdentity != descendant.Identity.StartIdentity)
                {
                    descendantFailure = MergeFailure(
                        descendantFailure,
                        "indeterminate");
                    continue;
                }
                if (!validateCurrentTarget(descendant))
                {
                    descendantFailure = MergeFailure(
                        descendantFailure,
                        "indeterminate");
                    continue;
                }
                if (PidFdSendSignal(
                        processFileDescriptor,
                        UnixSigKill,
                        IntPtr.Zero,
                        0) != 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error != Esrch)
                    {
                        descendantFailure = MergeFailure(
                            descendantFailure,
                            error == Eperm ? "permission-failure" : "indeterminate");
                    }
                    continue;
                }
                if (!PollLinuxExit(
                        processFileDescriptor,
                        deadline,
                        cancellationToken))
                {
                    descendantFailure = MergeFailure(
                        descendantFailure,
                        "indeterminate");
                }
            }
            finally
            {
                _ = Close(processFileDescriptor);
            }
        }

        if (!TryReadLinuxStartIdentity(rootProcessId, out var rootStartIdentity)
            || rootStartIdentity != terminationPlan.Root.StartIdentity)
        {
            LastDiagnosticCode = "HV936_LINUX_ROOT_IDENTITY";
            return new(
                "unix-wait-status",
                null,
                null,
                "indeterminate",
                false);
        }
        using var waiterArmed = new ManualResetEventSlim(false);
        var waiterTask = Task.Factory.StartNew(
            () => WaitForLinuxRootStatusExclusive(
                rootProcessId,
                waiterArmed),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        if (!WaitForSignal(waiterArmed, deadline, cancellationToken))
        {
            LastDiagnosticCode = "HV942_LINUX_ROOT_WAITER_NOT_ARMED";
            return new(
                "unix-wait-status",
                null,
                null,
                "indeterminate",
                false);
        }

        var rootFileDescriptor = PidFdOpen(rootProcessId, 0);
        if (rootFileDescriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != Esrch)
            {
                LastDiagnosticCode = "HV935_LINUX_ROOT_PIDFD_OPEN";
                return new(
                    "unix-wait-status",
                    null,
                    null,
                    error == Eperm ? "permission-failure" : "indeterminate",
                    false);
            }
            return CaptureLinuxRootWait(
                waiterTask,
                "already-exited",
                causalMatch: false,
                deadline,
                cancellationToken);
        }
        try
        {
            var killResult = PidFdSendSignal(
                rootFileDescriptor,
                UnixSigKill,
                IntPtr.Zero,
                0);
            var killError = killResult == 0 ? 0 : Marshal.GetLastPInvokeError();
            if (killResult != 0 && killError != Esrch)
            {
                LastDiagnosticCode = "HV937_LINUX_ROOT_SIGNAL";
                return new(
                    "unix-wait-status",
                    null,
                    null,
                    killError == Eperm ? "permission-failure" : "indeterminate",
                    false);
            }
            var requestOutcome = descendantFailure
                ?? (killResult == 0 ? "issued" : "already-exited");
            var captured = CaptureLinuxRootWait(
                waiterTask,
                requestOutcome,
                descendantFailure is null && killResult == 0,
                deadline,
                cancellationToken);
            if (captured.CausalMatch)
            {
                LastDiagnosticCode = "HV000_NATIVE_CONTROL_COMPLETE";
            }
            return captured;
        }
        finally
        {
            _ = Close(rootFileDescriptor);
        }
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

    private static LinuxWaitResult WaitForLinuxRootStatusExclusive(
        int rootProcessId,
        ManualResetEventSlim armed)
    {
        armed.Set();
        int waitResult;
        int rawStatus;
        do
        {
            waitResult = WaitPid(rootProcessId, out rawStatus, 0);
        }
        while (waitResult < 0 && Marshal.GetLastPInvokeError() == Eintr);
        return new(
            waitResult,
            rawStatus,
            waitResult < 0 ? Marshal.GetLastPInvokeError() : 0);
    }

    private static NativeTerminationEvidence CaptureLinuxRootWait(
        Task<LinuxWaitResult> waiterTask,
        string requestOutcome,
        bool causalMatch,
        MonotonicDeadline deadline,
        CancellationToken cancellationToken)
    {
        while (!deadline.IsExpired)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (waiterTask.Wait(deadline.NextWaitMilliseconds(50)))
            {
                var result = waiterTask.GetAwaiter().GetResult();
                if (result.Result > 0)
                {
                    return CaptureLinuxStatus(
                        result.RawStatus,
                        requestOutcome,
                        causalMatch);
                }
                LastDiagnosticCode = result.Error == Echild
                    ? "HV939_LINUX_ROOT_STATUS_ALREADY_REAPED"
                    : "HV940_LINUX_ROOT_STATUS_ERROR";
                return new(
                    "unix-wait-status",
                    null,
                    null,
                    result.Error == Echild ? "indeterminate" : requestOutcome,
                    false);
            }
        }
        LastDiagnosticCode = "HV941_LINUX_ROOT_STATUS_TIMEOUT";
        return new(
            "unix-wait-status",
            null,
            null,
            "indeterminate",
            false);
    }

    private static bool WaitForSignal(
        ManualResetEventSlim signal,
        MonotonicDeadline deadline,
        CancellationToken cancellationToken)
    {
        while (!deadline.IsExpired)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (signal.Wait(deadline.NextWaitMilliseconds(50)))
            {
                return true;
            }
        }
        return false;
    }

    private static bool PollLinuxExit(
        int processFileDescriptor,
        MonotonicDeadline deadline,
        CancellationToken cancellationToken)
    {
        var pollDescriptor = new PollFileDescriptor
        {
            FileDescriptor = processFileDescriptor,
            Events = PollIn
        };
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = Poll(
                ref pollDescriptor,
                1,
                deadline.NextWaitMilliseconds(50));
            if (result > 0 && (pollDescriptor.ReturnedEvents & PollIn) != 0)
            {
                return true;
            }
            if (result < 0 && Marshal.GetLastPInvokeError() != Eintr)
            {
                return false;
            }
        }
        while (!deadline.IsExpired);
        return false;
    }

    private static bool TryReadLinuxStartIdentity(
        int processId,
        out long startIdentity)
    {
        try
        {
            startIdentity = ProcessTreeObserver.ParseLinuxStartIdentity(
                File.ReadAllText($"/proc/{processId}/stat"));
            return true;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException
                or DirectoryNotFoundException
                or IOException
                or UnauthorizedAccessException
                or FormatException
                or OverflowException)
        {
            startIdentity = 0;
            return false;
        }
    }

    internal static string? CombineTerminationFailuresForSelfTest(
        bool planComplete,
        params string?[] outcomes)
    {
        string? failure = planComplete ? null : "indeterminate";
        foreach (var outcome in outcomes)
        {
            failure = MergeFailure(failure, outcome);
        }
        return failure;
    }

    internal static bool IsTerminationFullyObserved(
        NativeTerminationEvidence? evidence,
        bool streamsComplete) =>
        streamsComplete
        && evidence is
        {
            KillRequestOutcome: "issued",
            CausalMatch: true
        };

    private static string? MergeFailure(string? current, string? candidate)
    {
        if (candidate is null)
        {
            return current;
        }
        if (current == "permission-failure"
            || candidate == "permission-failure")
        {
            return "permission-failure";
        }
        return "indeterminate";
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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFileDescriptor
    {
        public int FileDescriptor;
        public short Events;
        public short ReturnedEvents;
    }

    private sealed record LinuxWaitResult(
        int Result,
        int RawStatus,
        int Error);

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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        SafeProcessHandle process,
        out NativeFileTime creationTime,
        out NativeFileTime exitTime,
        out NativeFileTime kernelTime,
        out NativeFileTime userTime);

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int processId, int signal);

    [DllImport("libc", EntryPoint = "waitpid", SetLastError = true)]
    private static extern int WaitPid(int processId, out int status, int options);

    [DllImport("libc", EntryPoint = "pidfd_open", SetLastError = true)]
    private static extern int PidFdOpen(int processId, uint flags);

    [DllImport("libc", EntryPoint = "pidfd_send_signal", SetLastError = true)]
    private static extern int PidFdSendSignal(
        int processFileDescriptor,
        int signal,
        IntPtr signalInfo,
        uint flags);

    [DllImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static extern int Poll(
        ref PollFileDescriptor descriptors,
        nuint descriptorCount,
        int timeoutMilliseconds);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int fileDescriptor);
}
