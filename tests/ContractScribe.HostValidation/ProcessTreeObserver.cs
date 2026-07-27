using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ContractScribe.HostValidation;

internal sealed class ProcessTreeObserver : IAsyncDisposable
{
    private readonly int subjectProcessId;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Dictionary<int, ObservedProcess> observed = new();
    private readonly Task sampler;
    private bool complete = true;

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
            var processes = new Dictionary<int, (int ParentId, string Name)>();
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        processes[process.Id] = (GetParentProcessId(process), SanitizeImageName(process.ProcessName));
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
                        // A system process may exit between enumeration and inspection.
                    }
                }
            }

            foreach (var candidate in processes)
            {
                if (!IsDescendant(candidate.Key, processes))
                {
                    continue;
                }

                var role = candidate.Key == subjectProcessId
                    ? "subject-runtime"
                    : candidate.Value.Name.StartsWith("ContractScribe", StringComparison.OrdinalIgnoreCase)
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

            await Task.Delay(20, token).ConfigureAwait(false);
        }
    }

    private bool IsDescendant(int processId, IReadOnlyDictionary<int, (int ParentId, string Name)> processes)
    {
        var current = processId;
        var visited = new HashSet<int>();
        while (visited.Add(current) && processes.TryGetValue(current, out var process))
        {
            if (current == subjectProcessId)
            {
                return true;
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
        imageName.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
        || imageName.Equals("msbuild", StringComparison.OrdinalIgnoreCase)
        || imageName.Equals("vbcscompiler", StringComparison.OrdinalIgnoreCase)
        || imageName.Equals("VBCSCompiler", StringComparison.OrdinalIgnoreCase);

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

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref ProcessBasicInformation processInformation,
        int processInformationLength,
        out int returnLength);
}
