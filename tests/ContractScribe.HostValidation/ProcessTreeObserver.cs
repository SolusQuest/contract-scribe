using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ContractScribe.HostValidation;

internal sealed class ProcessTreeObserver : IAsyncDisposable
{
    private readonly int subjectProcessId;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Dictionary<int, ObservedProcess> observed = new();
    private readonly Task sampler;
    private volatile bool complete = true;
    private long completedSampleGeneration;

    public ProcessTreeObserver(Process subjectProcess)
    {
        subjectProcessId = subjectProcess.Id;
        try
        {
            observed[subjectProcessId] = new ObservedProcess(
                subjectProcessId,
                GetParentProcessId(subjectProcess),
                "subject-runtime",
                SanitizeImageName(subjectProcess.ProcessName));
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or NotSupportedException
                or IOException
                or UnauthorizedAccessException
                or FormatException
                or OverflowException)
        {
            complete = false;
        }
        sampler = SampleAsync(cancellation.Token);
    }

    public bool ObservationComplete => complete && !sampler.IsFaulted;

    public long CompletedSampleGeneration => Interlocked.Read(ref completedSampleGeneration);

    public async Task<bool> WaitForSampleAfterAsync(
        long generation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (CompletedSampleGeneration <= generation)
        {
            if (!ObservationComplete || DateTime.UtcNow >= deadline)
            {
                return false;
            }
            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
        }
        return ObservationComplete;
    }

    public IReadOnlyList<ObservedProcess> Snapshot()
    {
        lock (observed)
        {
            return observed.Values
                .OrderBy(process => process.ProcessId)
                .ToArray();
        }
    }

    public async ValueTask DisposeAsync()
    {
        cancellation.Cancel();
        try
        {
            await sampler.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            complete = false;
        }
        cancellation.Dispose();
    }

    private async Task SampleAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var processes = OperatingSystem.IsWindows()
                ? CaptureWindowsProcesses()
                : CapturePortableProcesses();

            foreach (var candidate in processes)
            {
                if (!IsDescendant(candidate.Key, processes))
                {
                    continue;
                }

                var role = candidate.Key == subjectProcessId
                    ? "subject-runtime"
                    : candidate.Value.Name.StartsWith("ContractScribe", StringComparison.OrdinalIgnoreCase)
                        || candidate.Value.Name.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                        ? "contractscribe-worker"
                        : IsToolchainImage(candidate.Value.Name)
                            ? "toolchain-owned"
                            : "unknown-descendant";
                lock (observed)
                {
                    observed[candidate.Key] = new ObservedProcess(
                        candidate.Key,
                        candidate.Value.ParentId,
                        role,
                        candidate.Value.Name);
                }
            }

            Interlocked.Increment(ref completedSampleGeneration);
            await Task.Delay(20, token).ConfigureAwait(false);
        }
    }

    private bool IsDescendant(int processId, IReadOnlyDictionary<int, (int ParentId, string Name)> processes)
    {
        var current = processId;
        var visited = new HashSet<int>();
        while (visited.Add(current))
        {
            if (current == subjectProcessId)
            {
                return true;
            }
            if (!processes.TryGetValue(current, out var process))
            {
                return false;
            }
            if (process.ParentId <= 0)
            {
                return false;
            }
            current = process.ParentId;
        }
        return false;
    }

    private static bool IsToolchainImage(string imageName) =>
        imageName.Equals("msbuild", StringComparison.OrdinalIgnoreCase)
        || imageName.Equals("vbcscompiler", StringComparison.OrdinalIgnoreCase)
        || imageName.Equals("VBCSCompiler", StringComparison.OrdinalIgnoreCase);

    private Dictionary<int, (int ParentId, string Name)> CapturePortableProcesses()
    {
        var processes = new Dictionary<int, (int ParentId, string Name)>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    processes[process.Id] = (
                        GetParentProcessId(process),
                        SanitizeImageName(process.ProcessName));
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException
                        or System.ComponentModel.Win32Exception
                        or NotSupportedException
                        or IOException
                        or UnauthorizedAccessException
                        or FormatException
                        or OverflowException)
                {
                    if (!HasDisappeared(process))
                    {
                        complete = false;
                    }
                }
            }
        }
        return processes;
    }

    private Dictionary<int, (int ParentId, string Name)> CaptureWindowsProcesses()
    {
        const uint snapshotProcesses = 0x00000002;
        var snapshot = CreateToolhelp32Snapshot(snapshotProcesses, 0);
        if (snapshot == new IntPtr(-1))
        {
            complete = false;
            return [];
        }
        try
        {
            var entry = new ProcessEntry32
            {
                Size = (uint)Marshal.SizeOf<ProcessEntry32>()
            };
            if (!Process32First(snapshot, ref entry))
            {
                complete = false;
                return [];
            }
            var processes = new Dictionary<int, (int ParentId, string Name)>();
            do
            {
                var image = Path.GetFileNameWithoutExtension(entry.ExecutableFile);
                processes[checked((int)entry.ProcessId)] = (
                    checked((int)entry.ParentProcessId),
                    SanitizeImageName(image));
                entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
            }
            while (Process32Next(snapshot, ref entry));
            return processes;
        }
        finally
        {
            _ = CloseHandle(snapshot);
        }
    }

    private static bool HasDisappeared(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return true;
            }
        }
        catch (InvalidOperationException)
        {
            return true;
        }

        return OperatingSystem.IsLinux() && !Directory.Exists($"/proc/{process.Id}");
    }

    private static string SanitizeImageName(string name)
    {
        if (name.Length is < 1 or > 128
            || name.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')))
        {
            return "unclassified";
        }
        return name;
    }

    private static int GetParentProcessId(Process process)
    {
        if (OperatingSystem.IsLinux())
        {
            var stat = File.ReadAllText($"/proc/{process.Id}/stat");
            var closingParenthesis = stat.LastIndexOf(')');
            var fields = stat[(closingParenthesis + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return int.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture);
        }

        if (OperatingSystem.IsWindows())
        {
            var information = new ProcessBasicInformation();
            var status = NtQueryInformationProcess(
                process.Handle,
                0,
                ref information,
                Marshal.SizeOf<ProcessBasicInformation>(),
                out _);
            if (status != 0)
            {
                throw new System.ComponentModel.Win32Exception(status);
            }
            return information.InheritedFromUniqueProcessId.ToInt32();
        }

        return 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2A;
        public IntPtr Reserved2B;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableFile;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref ProcessBasicInformation processInformation,
        int processInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
