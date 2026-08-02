using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
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
    private readonly Dictionary<string, string> protectedEntryPointHashes;
    private readonly CancellationTokenSource stop = new();
    private readonly Task polling;
    private RegisteredToolchain? selectedToolchain;
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
        protectedEntryPointHashes = new Dictionary<string, string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
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

    public long SelectToolchain(RegisteredToolchain toolchain)
    {
        ArgumentNullException.ThrowIfNull(toolchain);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            selectedToolchain = toolchain;
        }
        return Reconcile();
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
            if (selectedToolchain is null)
            {
                return observed.Count;
            }
            foreach (var node in nodes)
            {
                if (node.ProcessId == rootProcessId || !IsDescendant(node, byProcessId))
                {
                    continue;
                }
                if (node.StartIdentity <= 0)
                {
                    throw new IOException("A descendant process has no stable start identity.");
                }
                var identity = new ProcessIdentity(node.ProcessId, node.StartIdentity);
                switch (Classify(node, selectedToolchain))
                {
                    case ProcessRole.ToolchainOwned:
                        observed.Add(identity);
                        break;
                    case ProcessRole.ContractScribeWorker:
                    case ProcessRole.RestoreOrRuntimeDownload:
                    case ProcessRole.PlatformInfrastructure:
                        break;
                    default:
                        throw new IOException(
                            $"A Host descendant could not be classified against the selected toolchain: "
                            + $"image={Path.GetFileName(node.ImageName)},"
                            + $"entry={Path.GetFileName(node.EntryPointPath)},"
                            + $"complete={node.ClassificationComplete}.");
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

    private ProcessRole Classify(ProcessNode node, RegisteredToolchain toolchain)
    {
        if (!node.ClassificationComplete)
        {
            return ProcessRole.Unknown;
        }
        if (IsRestoreOrRuntimeDownload(node.CommandArguments))
        {
            return ProcessRole.RestoreOrRuntimeDownload;
        }
        var imageName = Path.GetFileNameWithoutExtension(node.ImageName);
        var entryPointName = node.EntryPointPath is null
            ? string.Empty
            : Path.GetFileNameWithoutExtension(node.EntryPointPath);
        if (imageName.StartsWith("ContractScribe", StringComparison.OrdinalIgnoreCase)
            || entryPointName.StartsWith("ContractScribe", StringComparison.OrdinalIgnoreCase))
        {
            return ProcessRole.ContractScribeWorker;
        }
        if (imageName.Equals("conhost", StringComparison.OrdinalIgnoreCase)
            && entryPointName.Equals("conhost", StringComparison.OrdinalIgnoreCase))
        {
            return ProcessRole.PlatformInfrastructure;
        }
        if (node.EntryPointPath is null
            || !Path.IsPathFullyQualified(node.EntryPointPath)
            || !IsContained(toolchain.MsbuildPath, node.EntryPointPath)
            || entryPointName.ToLowerInvariant() is not (
                "msbuild" or "vbcscompiler" or "csc" or "vbc"))
        {
            return imageName.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                ? ProcessRole.ContractScribeWorker
                : ProcessRole.Unknown;
        }

        var entryPoint = Path.GetFullPath(node.EntryPointPath);
        if (!File.Exists(entryPoint))
        {
            return ProcessRole.Unknown;
        }
        using var entryPointStream = new FileStream(
            entryPoint,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(entryPointStream));
        if (protectedEntryPointHashes.TryGetValue(entryPoint, out var expected)
            && !string.Equals(expected, sha256, StringComparison.Ordinal))
        {
            return ProcessRole.Unknown;
        }
        protectedEntryPointHashes[entryPoint] = sha256;
        return ProcessRole.ToolchainOwned;
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

    private IReadOnlyList<ProcessNode> CaptureProcessSnapshot()
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
                    var startIdentity = process.StartTime.ToUniversalTime().Ticks;
                    var imageName = process.ProcessName;
                    result.Add(new ProcessNode(
                        process.Id,
                        parent,
                        startIdentity,
                        imageName,
                        null,
                        [],
                        false));
                }
                catch (Exception exception) when (IsObservationException(exception))
                {
                    // The process disappeared before a stable ancestry identity could be captured.
                }
            }
        }
        var byProcessId = result.ToDictionary(node => node.ProcessId);
        for (var index = 0; index < result.Count; index++)
        {
            var node = result[index];
            if (node.ProcessId == rootProcessId || !IsDescendant(node, byProcessId))
            {
                continue;
            }
            try
            {
                using var process = Process.GetProcessById(node.ProcessId);
                if (process.StartTime.ToUniversalTime().Ticks != node.StartIdentity)
                {
                    continue;
                }
                var commandLine = GetCommandLineArguments(process);
                var (entryPoint, arguments) = ResolveCommand(
                    process,
                    node.ImageName,
                    commandLine);
                result[index] = node with
                {
                    EntryPointPath = entryPoint,
                    CommandArguments = arguments,
                    ClassificationComplete = true,
                };
            }
            catch (Exception exception) when (IsObservationException(exception))
            {
                // The completed snapshot linearizes after identity and command capture. A process
                // that exits during capture is absent from that snapshot; a process that remains
                // present but cannot be classified is retained as explicitly incomplete.
                try
                {
                    using var current = Process.GetProcessById(node.ProcessId);
                    if (current.StartTime.ToUniversalTime().Ticks == node.StartIdentity)
                    {
                        continue;
                    }
                }
                catch (Exception currentException) when (IsObservationException(currentException))
                {
                }
                result.RemoveAt(index);
                index--;
            }
        }
        return result;
    }

    private static (string? EntryPoint, IReadOnlyList<string> Arguments) ResolveCommand(
        Process process,
        string imageName,
        IReadOnlyList<string> commandLine)
    {
        if (imageName.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var entryPointIndex = commandLine
                .Select((argument, index) => (argument, index))
                .FirstOrDefault(candidate =>
                    candidate.argument.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    && Path.IsPathFullyQualified(candidate.argument))
                .index;
            return entryPointIndex > 0
                ? (commandLine[entryPointIndex], commandLine.Skip(entryPointIndex + 1).ToArray())
                : (null, []);
        }
        return (process.MainModule?.FileName, commandLine.Skip(1).ToArray());
    }

    private static bool IsRestoreOrRuntimeDownload(IReadOnlyList<string> arguments) =>
        arguments.Any(argument =>
        {
            var normalized = argument.Trim().ToLowerInvariant();
            return normalized is "restore" or "-restore" or "/restore"
                or "--runtime" or "--runtime-id"
                || normalized.StartsWith("-t:restore", StringComparison.Ordinal)
                || normalized.StartsWith("/t:restore", StringComparison.Ordinal)
                || normalized.StartsWith("--runtime=", StringComparison.Ordinal)
                || normalized.StartsWith("--runtime-id=", StringComparison.Ordinal);
        });

    private static bool IsContained(string root, string candidate)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullCandidate = Path.GetFullPath(candidate);
        return fullCandidate.StartsWith(
            fullRoot + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }

    private static bool IsObservationException(Exception exception) => exception is
        InvalidOperationException
        or ArgumentException
        or IOException
        or UnauthorizedAccessException
        or Win32Exception
        or NotSupportedException
        or FormatException
        or OverflowException;

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

    private static IReadOnlyList<string> GetCommandLineArguments(Process process)
    {
        if (OperatingSystem.IsLinux())
        {
            var bytes = File.ReadAllBytes($"/proc/{process.Id}/cmdline");
            return Encoding.UTF8.GetString(bytes)
                .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        const int processCommandLineInformation = 60;
        _ = NtQueryInformationProcess(
            process.Handle,
            processCommandLineInformation,
            IntPtr.Zero,
            0,
            out var requiredLength);
        if (requiredLength <= Marshal.SizeOf<UnicodeString>())
        {
            return [];
        }
        var buffer = Marshal.AllocHGlobal(requiredLength);
        try
        {
            var status = NtQueryInformationProcess(
                process.Handle,
                processCommandLineInformation,
                buffer,
                requiredLength,
                out _);
            if (status != 0)
            {
                return [];
            }
            var commandLine = Marshal.PtrToStructure<UnicodeString>(buffer);
            return SplitWindowsCommandLine(Marshal.PtrToStringUni(
                commandLine.Buffer,
                commandLine.Length / sizeof(char)));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IReadOnlyList<string> SplitWindowsCommandLine(string? commandLine)
    {
        if (string.IsNullOrEmpty(commandLine))
        {
            return [];
        }
        var argv = CommandLineToArgvW(commandLine, out var count);
        if (argv == IntPtr.Zero || count <= 0)
        {
            return [];
        }
        try
        {
            var arguments = new string[count];
            for (var index = 0; index < count; index++)
            {
                arguments[index] = Marshal.PtrToStringUni(
                    Marshal.ReadIntPtr(argv, index * IntPtr.Size)) ?? string.Empty;
            }
            return arguments;
        }
        finally
        {
            _ = LocalFree(argv);
        }
    }

    internal readonly record struct ProcessNode(
        int ProcessId,
        int ParentProcessId,
        long StartIdentity,
        string ImageName,
        string? EntryPointPath,
        IReadOnlyList<string> CommandArguments,
        bool ClassificationComplete);

    private readonly record struct ProcessIdentity(int ProcessId, long StartIdentity);

    private enum ProcessRole
    {
        ToolchainOwned,
        ContractScribeWorker,
        RestoreOrRuntimeDownload,
        PlatformInfrastructure,
        Unknown,
    }

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

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        SafeProcessHandle process,
        int processInformationClass,
        out ProcessBasicInformation processInformation,
        int processInformationLength,
        out int returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        IntPtr processInformation,
        int processInformationLength,
        out int returnLength);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
