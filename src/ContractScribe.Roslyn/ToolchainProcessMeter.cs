using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ContractScribe.Core.Hosting;
using Microsoft.Win32.SafeHandles;

namespace ContractScribe.Roslyn;

internal sealed class ToolchainProcessMeter : IDisposable
{
    private readonly object gate = new();
    private readonly int rootProcessId;
    private readonly long rootStartIdentity;
    private readonly Func<IReadOnlyList<ProcessNode>> snapshot;
    private readonly HashSet<ProcessIdentity> observed = [];
    private readonly CancellationTokenSource stop = new();
    private readonly Task polling;
    private Exception? observerFailure;
    private bool disposed;

    public ToolchainProcessMeter(
        Func<IReadOnlyList<ProcessNode>>? snapshot = null,
        TimeSpan? pollingInterval = null)
    {
        using var current = Process.GetCurrentProcess();
        rootProcessId = current.Id;
        rootStartIdentity = current.StartTime.ToUniversalTime().Ticks;
        this.snapshot = snapshot ?? CaptureProcessSnapshot;
        var interval = pollingInterval ?? TimeSpan.FromMilliseconds(25);
        _ = Reconcile();
        polling = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(interval, stop.Token).ConfigureAwait(false);
                    _ = Reconcile();
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                lock (gate)
                {
                    observerFailure ??= exception;
                }
            }
        });
    }

    public long Count
    {
        get
        {
            lock (gate)
            {
                return observed.Count;
            }
        }
    }

    public long Reconcile()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (observerFailure is not null)
            {
                throw new IOException("The toolchain process observer lost continuity.", observerFailure);
            }
            var nodes = snapshot();
            var byProcessId = nodes
                .GroupBy(node => node.ProcessId)
                .ToDictionary(group => group.Key, group => group.Single());
            if (byProcessId.TryGetValue(rootProcessId, out var root)
                && root.StartIdentity != rootStartIdentity)
            {
                throw new IOException("The Host process identity changed during observation.");
            }
            foreach (var node in nodes)
            {
                if (node.ProcessId != rootProcessId
                    && IsDescendant(node, byProcessId))
                {
                    observed.Add(new ProcessIdentity(node.ProcessId, node.StartIdentity));
                }
            }
            return observed.Count;
        }
    }

    public HostMeasuredBound ToFact() => new(
        "toolchain-subprocess-count",
        "count",
        Count,
        HostContractResources.RequireBound("toolchain-subprocess-count"),
        HostEnforcementClass.ObservableOnly);

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            stop.Cancel();
        }
        try
        {
            polling.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        stop.Dispose();
    }

    private bool IsDescendant(
        ProcessNode candidate,
        IReadOnlyDictionary<int, ProcessNode> byProcessId)
    {
        var visited = new HashSet<int>();
        var parentId = candidate.ParentProcessId;
        while (parentId > 0 && visited.Add(parentId))
        {
            if (parentId == rootProcessId)
            {
                return !byProcessId.TryGetValue(parentId, out var root)
                    || root.StartIdentity == rootStartIdentity;
            }
            if (!byProcessId.TryGetValue(parentId, out var parent))
            {
                return false;
            }
            parentId = parent.ParentProcessId;
        }
        return false;
    }

    private static IReadOnlyList<ProcessNode> CaptureProcessSnapshot()
    {
        var result = new List<ProcessNode>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var parent = OperatingSystem.IsWindows()
                        ? ReadWindowsParentProcessId(process)
                        : OperatingSystem.IsLinux()
                            ? ReadLinuxParentProcessId(process.Id)
                            : 0;
                    result.Add(new ProcessNode(
                        process.Id,
                        parent,
                        process.StartTime.ToUniversalTime().Ticks));
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException
                        or ArgumentException
                        or IOException
                        or UnauthorizedAccessException
                        or Win32Exception)
                {
                    // A process that exits during the snapshot cannot be classified as owned.
                }
            }
        }
        return result;
    }

    private static int ReadWindowsParentProcessId(Process process)
    {
        var status = NtQueryInformationProcess(
            process.SafeHandle,
            0,
            out var information,
            Marshal.SizeOf<ProcessBasicInformation>(),
            out _);
        return status >= 0
            ? information.InheritedFromUniqueProcessId.ToInt32()
            : 0;
    }

    private static int ReadLinuxParentProcessId(int processId)
    {
        var stat = File.ReadAllText($"/proc/{processId}/stat");
        var closeName = stat.LastIndexOf(')');
        if (closeName < 0)
        {
            return 0;
        }
        var fields = stat[(closeName + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length >= 2 && int.TryParse(fields[1], out var parent)
            ? parent
            : 0;
    }

    internal readonly record struct ProcessNode(
        int ProcessId,
        int ParentProcessId,
        long StartIdentity);

    private readonly record struct ProcessIdentity(int ProcessId, long StartIdentity);

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

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        SafeProcessHandle process,
        int processInformationClass,
        out ProcessBasicInformation processInformation,
        int processInformationLength,
        out int returnLength);
}
