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

    internal static int LastRootStatusOwnerCount { get; private set; }

    internal static bool LastRootStatusWaitCompleted { get; private set; }

    internal static RootStatusSession CreateRootStatusSession(
        Process rootProcess,
        MonotonicDeadline deadline,
        bool activate) =>
        new(
            rootProcess.Id,
            deadline,
            activate && OperatingSystem.IsLinux(),
            RootStatusOperations.Native);

    internal static RootStatusSession CreateRootStatusSessionForSelfTest(
        int rootProcessId,
        MonotonicDeadline deadline,
        RootStatusOperations operations) =>
        new(
            rootProcessId,
            deadline,
            activate: true,
            operations);

    internal static bool IsRootExitedNonReaping(
        Process rootProcess,
        RootStatusSession rootSession) =>
        OperatingSystem.IsLinux()
            ? rootSession.WaiterCompleted
            : !IsAliveNonReaping(rootProcess.Id);

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

    internal static NativeTerminationEvidence TerminateTreeAndCapture(
        Process rootProcess,
        RootStatusSession rootSession,
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
                rootSession,
                terminationPlan,
                validateCurrentTarget,
                deadline,
                cancellationToken);
        }
        return new("unsupported", null, null, "unsupported", false);
    }

    internal static async Task<NativeTerminationEvidence> WaitForNaturalExitAsync(
        Process rootProcess,
        RootStatusSession rootSession,
        Action release,
        MonotonicDeadline deadline,
        TimeSpan cleanupReserve,
        CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsLinux())
        {
            release();
            return rootSession.CaptureWait(
                "already-exited",
                causalMatch: false,
                deadline,
                cleanupReserve,
                cancellationToken);
        }
        var exit = rootProcess.WaitForExitAsync(cancellationToken);
        release();
        var remaining = RemainingExcludingReserve(
            deadline,
            cleanupReserve);
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

    private static TimeSpan RemainingExcludingReserve(
        MonotonicDeadline deadline,
        TimeSpan reserve)
    {
        var remaining = deadline.Remaining;
        return remaining <= reserve
            ? TimeSpan.Zero
            : remaining - reserve;
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
        RootStatusSession rootSession,
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

        return rootSession.TerminateRoot(
            terminationPlan.Root,
            descendantFailure,
            deadline,
            cancellationToken);
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
        Action armed)
    {
        armed();
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
        TimeSpan reserve,
        CancellationToken cancellationToken)
    {
        while (RemainingExcludingReserve(deadline, reserve) > TimeSpan.Zero)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = RemainingExcludingReserve(deadline, reserve);
            var waitMilliseconds = Math.Max(
                1,
                Math.Min(
                    50,
                    checked((int)Math.Ceiling(remaining.TotalMilliseconds))));
            if (waiterTask.Wait(waitMilliseconds))
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
        Task signal,
        MonotonicDeadline deadline,
        CancellationToken cancellationToken)
    {
        while (!deadline.IsExpired)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (signal.Wait(deadline.NextWaitMilliseconds(50), cancellationToken))
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

    internal static bool IsLinuxRootSignalAuthorized(
        ProcessInstanceIdentity planned,
        ProcessInstanceIdentity opened,
        ProcessInstanceIdentity? current) =>
        planned.ProcessId == opened.ProcessId
        && planned.StartIdentity != 0
        && planned.StartIdentity == opened.StartIdentity
        && current is not null
        && current.ProcessId == opened.ProcessId
        && current.StartIdentity == opened.StartIdentity;

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

    internal sealed class RootStatusSession : IDisposable
    {
        private readonly int rootProcessId;
        private readonly int rootFileDescriptor;
        private readonly ProcessInstanceIdentity? openedIdentity;
        private readonly Task<LinuxWaitResult>? waiterTask;
        private readonly RootStatusOperations operations;
        private readonly string initializationOutcome;
        private bool disposed;

        internal RootStatusSession(
            int rootProcessId,
            MonotonicDeadline deadline,
            bool activate,
            RootStatusOperations operations)
        {
            this.rootProcessId = rootProcessId;
            this.operations = operations;
            rootFileDescriptor = -1;
            initializationOutcome = "unsupported";
            LastRootStatusOwnerCount = 0;
            LastRootStatusWaitCompleted = false;
            if (!activate)
            {
                return;
            }
            if (deadline.IsExpired)
            {
                LastDiagnosticCode = "HV942_LINUX_ROOT_WAITER_NOT_ARMED";
                initializationOutcome = "indeterminate";
                return;
            }

            var opened = operations.OpenPidFd(rootProcessId);
            rootFileDescriptor = opened.Descriptor;
            if (rootFileDescriptor < 0)
            {
                LastDiagnosticCode = "HV935_LINUX_ROOT_PIDFD_OPEN";
                initializationOutcome = opened.Error == Esrch
                    ? "already-exited"
                    : opened.Error == Eperm
                        ? "permission-failure"
                        : "indeterminate";
                return;
            }
            var initialIdentity = operations.ReadStartIdentity(rootProcessId);
            if (!initialIdentity.Success)
            {
                LastDiagnosticCode = "HV936_LINUX_ROOT_IDENTITY";
                initializationOutcome = "indeterminate";
                return;
            }
            openedIdentity = new(
                rootProcessId,
                initialIdentity.StartIdentity);

            var waiterArmed = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            waiterTask = Task.Factory.StartNew(
                () => operations.WaitForRootStatus(
                    rootProcessId,
                    () => waiterArmed.TrySetResult()),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            LastRootStatusOwnerCount = 1;
            if (!WaitForSignal(
                    waiterArmed.Task,
                    deadline,
                    CancellationToken.None))
            {
                LastDiagnosticCode = "HV942_LINUX_ROOT_WAITER_NOT_ARMED";
                initializationOutcome = "indeterminate";
                return;
            }
            initializationOutcome = "ready";
        }

        internal bool WaiterCompleted => waiterTask?.IsCompleted == true;

        internal int StatusOwnerCount => waiterTask is null ? 0 : 1;

        internal NativeTerminationEvidence CaptureWait(
            string requestOutcome,
            bool causalMatch,
            MonotonicDeadline deadline,
            TimeSpan reserve,
            CancellationToken cancellationToken)
        {
            if (waiterTask is null)
            {
                return InitializationEvidence();
            }
            return CaptureLinuxRootWait(
                waiterTask,
                requestOutcome,
                causalMatch,
                deadline,
                reserve,
                cancellationToken);
        }

        internal NativeTerminationEvidence TerminateRoot(
            ProcessInstanceIdentity plannedIdentity,
            string? descendantFailure,
            MonotonicDeadline deadline,
            CancellationToken cancellationToken)
        {
            if (waiterTask is null
                || openedIdentity is null
                || rootFileDescriptor < 0)
            {
                return InitializationEvidence();
            }
            if (waiterTask.IsCompleted)
            {
                return CaptureWait(
                    "already-exited",
                    causalMatch: false,
                    deadline,
                    TimeSpan.Zero,
                    cancellationToken);
            }

            ProcessInstanceIdentity? currentIdentity = null;
            var current = operations.ReadStartIdentity(rootProcessId);
            if (current.Success)
            {
                currentIdentity = new(
                    rootProcessId,
                    current.StartIdentity);
            }
            if (!IsLinuxRootSignalAuthorized(
                    plannedIdentity,
                    openedIdentity,
                    currentIdentity))
            {
                LastDiagnosticCode = "HV936_LINUX_ROOT_IDENTITY";
                return new(
                    "unix-wait-status",
                    null,
                    null,
                    "indeterminate",
                    false);
            }

            var signal = operations.SendSignal(rootFileDescriptor);
            if (signal.Result != 0 && signal.Error != Esrch)
            {
                LastDiagnosticCode = "HV937_LINUX_ROOT_SIGNAL";
                return new(
                    "unix-wait-status",
                    null,
                    null,
                    signal.Error == Eperm
                        ? "permission-failure"
                        : "indeterminate",
                    false);
            }
            var requestOutcome = descendantFailure
                ?? (signal.Result == 0 ? "issued" : "already-exited");
            var captured = CaptureWait(
                requestOutcome,
                descendantFailure is null && signal.Result == 0,
                deadline,
                TimeSpan.Zero,
                cancellationToken);
            if (captured.CausalMatch)
            {
                LastDiagnosticCode = "HV000_NATIVE_CONTROL_COMPLETE";
            }
            return captured;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            LastRootStatusOwnerCount = StatusOwnerCount;
            LastRootStatusWaitCompleted = WaiterCompleted;
            if (rootFileDescriptor >= 0)
            {
                operations.Close(rootFileDescriptor);
            }
        }

        private NativeTerminationEvidence InitializationEvidence() =>
            new(
                "unix-wait-status",
                null,
                null,
                initializationOutcome,
                false);
    }

    internal sealed record RootStatusOperations(
        Func<int, (int Descriptor, int Error)> OpenPidFd,
        Func<int, (bool Success, long StartIdentity)> ReadStartIdentity,
        Func<int, Action, LinuxWaitResult> WaitForRootStatus,
        Func<int, (int Result, int Error)> SendSignal,
        Action<int> Close)
    {
        internal static RootStatusOperations Native { get; } = new(
            processId =>
            {
                var descriptor = PidFdOpen(processId, 0);
                return (
                    descriptor,
                    descriptor < 0 ? Marshal.GetLastPInvokeError() : 0);
            },
            processId =>
            {
                var success = TryReadLinuxStartIdentity(
                    processId,
                    out var startIdentity);
                return (success, startIdentity);
            },
            WaitForLinuxRootStatusExclusive,
            descriptor =>
            {
                var result = PidFdSendSignal(
                    descriptor,
                    UnixSigKill,
                    IntPtr.Zero,
                    0);
                return (
                    result,
                    result == 0 ? 0 : Marshal.GetLastPInvokeError());
            },
            descriptor => _ = NativeTerminationObserver.Close(descriptor));
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

    internal sealed record LinuxWaitResult(
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
