using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ContractScribe.HostValidation;

public sealed class ProcessTreeObserver : IAsyncDisposable
{
    private readonly int subjectProcessId;
    private readonly IReadOnlyList<ProcessIdentityRule> identityRegistry;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Dictionary<int, ObservedProcess> observed = new();
    private readonly Task sampler;
    private volatile bool complete = true;
    private long completedSampleGeneration;

    public ProcessTreeObserver(
        Process subjectProcess,
        IReadOnlyList<ProcessIdentityRule> identityRegistry)
    {
        subjectProcessId = subjectProcess.Id;
        this.identityRegistry = identityRegistry;
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
                    : ClassifyProcess(candidate.Key, candidate.Value.Name);
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

    public static string ClassifyIdentity(
        string imageName,
        string? entryPointPath,
        IReadOnlyList<string> commandArguments,
        IReadOnlyList<ProcessIdentityRule> identityRegistry)
    {
        if (entryPointPath is not null && File.Exists(entryPointPath))
        {
            var fingerprint = ComputeIdentityFingerprint(
                imageName,
                entryPointPath,
                commandArguments);
            var matches = identityRegistry
                .Where(rule => rule.FingerprintSha256 == fingerprint)
                .ToArray();
            if (matches.Length == 1)
            {
                return ClassifyProtectedCommand(
                    imageName,
                    entryPointPath,
                    commandArguments,
                    matches[0]);
            }
        }

        return imageName.StartsWith("ContractScribe", StringComparison.OrdinalIgnoreCase)
            || imageName.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
             ? "contractscribe-worker"
             : "unknown-descendant";
    }

    private static string ClassifyProtectedCommand(
        string imageName,
        string entryPointPath,
        IReadOnlyList<string> commandArguments,
        ProcessIdentityRule rule)
    {
        if (CanonicalJson.Sha256File(entryPointPath) != rule.EntryPointSha256)
        {
            return "unknown-descendant";
        }
        if (IsRestoreOrRuntimeDownload(commandArguments))
        {
            return "restore-or-runtime-download";
        }

        var entryPointName = Path.GetFileName(entryPointPath);
        if (rule.ArtifactKind is "production-subject" or "fixture-helper"
            && (imageName.StartsWith("ContractScribe", StringComparison.OrdinalIgnoreCase)
            || entryPointName.StartsWith("ContractScribe", StringComparison.OrdinalIgnoreCase))
            )
        {
            return "contractscribe-worker";
        }

        return rule.ArtifactKind == "selected-toolchain"
            ? entryPointName.ToLowerInvariant() switch
            {
                "msbuild" or "msbuild.exe" or "msbuild.dll"
                    or "vbcscompiler" or "vbcscompiler.exe" or "vbcscompiler.dll"
                    or "csc" or "csc.exe" or "csc.dll"
                    or "vbc" or "vbc.exe" or "vbc.dll" => "toolchain-owned",
                _ => "unknown-descendant"
            }
            : "unknown-descendant";
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

    public static string ComputeIdentityFingerprint(
        string imageName,
        string entryPointPath,
        IReadOnlyList<string> commandArguments)
    {
        var normalizedImage = SanitizeImageName(imageName).ToLowerInvariant();
        var entryPointSha256 = CanonicalJson.Sha256File(entryPointPath);
        using var preimage = new MemoryStream();
        foreach (var value in new[] { normalizedImage, entryPointSha256 }.Concat(commandArguments))
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            preimage.Write(bytes);
            preimage.WriteByte(0);
        }
        return Convert.ToHexStringLower(SHA256.HashData(preimage.ToArray()));
    }

    private string ClassifyProcess(int processId, string imageName)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var commandLine = GetCommandLineArguments(process);
            string? entryPoint;
            IReadOnlyList<string> arguments;
            if (imageName.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                var entryPointIndex = commandLine
                    .Select((argument, index) => (argument, index))
                    .FirstOrDefault(candidate =>
                        candidate.argument.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                        && Path.IsPathFullyQualified(candidate.argument))
                    .index;
                entryPoint = entryPointIndex > 0 ? commandLine[entryPointIndex] : null;
                arguments = entryPoint is null ? [] : commandLine.Skip(entryPointIndex + 1).ToArray();
            }
            else
            {
                entryPoint = process.MainModule?.FileName;
                arguments = commandLine.Skip(1).ToArray();
            }
            return ClassifyIdentity(
                imageName,
                entryPoint,
                arguments,
                identityRegistry);
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
            return imageName.StartsWith("ContractScribe", StringComparison.OrdinalIgnoreCase)
                || imageName.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                ? "contractscribe-worker"
                : "unknown-descendant";
        }
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
            var text = Marshal.PtrToStringUni(
                commandLine.Buffer,
                commandLine.Length / sizeof(char));
            return SplitWindowsCommandLine(text);
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

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
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

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        IntPtr processInformation,
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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
