using System.ComponentModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ContractScribe.TestSupport;

namespace ContractScribe.Roslyn.IntegrationTests;

internal sealed record OwnedProcessResult(string StandardOutput, string StandardError);

internal static class OwnedProcessRunner
{
    private const int RetainedOutputCharacters = 64 * 1024;
    private static readonly TimeSpan TerminationTimeout = TimeSpan.FromSeconds(10);

    public static async Task<OwnedProcessResult> RunAsync(
        string fileName,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        if (environment is not null)
        {
            foreach (var (name, value) in environment)
            {
                startInfo.Environment[name] = value;
            }
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Owned process failed to start: {fileName}.");
        await using var descendants = new OwnedProcessTreeObserver(process);
        var stdout = ReadBoundedAsync(process.StandardOutput);
        var stderr = ReadBoundedAsync(process.StandardError);
        using var timeoutCancellation = new CancellationTokenSource(timeout);
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);

        try
        {
            await process.WaitForExitAsync(waitCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (waitCancellation.IsCancellationRequested)
        {
            await descendants.TerminateAsync(process, TerminationTimeout).ConfigureAwait(false);
            var drainedOutput = await stdout.WaitAsync(TerminationTimeout).ConfigureAwait(false);
            var drainedError = await stderr.WaitAsync(TerminationTimeout).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(
                    $"Owned process was cancelled. stdout tail: {drainedOutput}\nstderr tail: {drainedError}",
                    cancellationToken);
            }

            throw new TimeoutException(
                $"Owned process exceeded {timeout}. stdout tail: {drainedOutput}\nstderr tail: {drainedError}");
        }

        var standardOutput = await stdout.ConfigureAwait(false);
        var standardError = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Owned process exited with code {process.ExitCode}. stdout tail: {standardOutput}\nstderr tail: {standardError}");
        }

        return new OwnedProcessResult(standardOutput, standardError);
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        var retained = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0)
            {
                return retained.ToString();
            }

            retained.Append(buffer, 0, read);
            if (retained.Length > RetainedOutputCharacters)
            {
                retained.Remove(0, retained.Length - RetainedOutputCharacters);
            }
        }
    }
}

internal sealed class OwnedProcessTreeObserver : IAsyncDisposable
{
    private readonly OwnedProcessIdentity root;
    private readonly ConcurrentDictionary<OwnedProcessIdentity, byte> observed = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Task polling;

    public OwnedProcessTreeObserver(Process rootProcess)
    {
        root = new OwnedProcessIdentity(
            rootProcess.Id,
            StableProcessStartIdentity.Read(rootProcess));
        polling = Task.Run(PollAsync);
    }

    public IReadOnlyCollection<OwnedProcessIdentity> ObservedDescendants => observed.Keys.ToArray();

    public async Task TerminateAsync(Process rootProcess, TimeSpan timeout)
    {
        CaptureOnce();
        try
        {
            if (!rootProcess.HasExited
                && StableProcessStartIdentity.Read(rootProcess) == root.StartIdentity)
            {
                rootProcess.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (IsObservationException(exception))
        {
        }

        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            await rootProcess.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException
            or InvalidOperationException)
        {
        }

        while (!deadline.IsCancellationRequested)
        {
            CaptureOnce();
            if (IdentityHasExited(root) && observed.Keys.All(IdentityHasExited))
            {
                return;
            }
            try
            {
                await Task.Delay(25, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        var survivors = observed.Keys.Where(identity => !IdentityHasExited(identity)).ToArray();
        throw new TimeoutException(
            $"Owned process cleanup did not observe bounded exit for root {root.ProcessId}; "
            + $"surviving descendants: {string.Join(", ", survivors.Select(identity => identity.ProcessId))}.");
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        try
        {
            await polling.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        stop.Dispose();
    }

    private async Task PollAsync()
    {
        try
        {
            while (true)
            {
                CaptureOnce();
                await Task.Delay(25, stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
    }

    private void CaptureOnce()
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == root.ProcessId
                        || !IsDescendantOf(process.Id, root.ProcessId))
                    {
                        continue;
                    }

                    observed.TryAdd(
                        new OwnedProcessIdentity(
                            process.Id,
                            StableProcessStartIdentity.Read(process)),
                        0);
                }
                catch (Exception exception) when (IsObservationException(exception))
                {
                }
            }
        }
    }

    private static bool IsDescendantOf(int processId, int ancestorProcessId)
    {
        var visited = new HashSet<int>();
        var current = processId;
        while (visited.Add(current))
        {
            var parent = ParentProcessId(current);
            if (parent == ancestorProcessId)
            {
                return true;
            }
            if (parent is null or <= 1)
            {
                return false;
            }
            current = parent.Value;
        }
        return false;
    }

    private static int? ParentProcessId(int processId)
    {
        if (OperatingSystem.IsLinux())
        {
            var stat = File.ReadAllText($"/proc/{processId}/stat");
            var commandEnd = stat.LastIndexOf(')');
            if (commandEnd < 0)
            {
                return null;
            }
            var fields = stat[(commandEnd + 2)..]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return fields.Length >= 2
                ? int.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture)
                : null;
        }
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using var process = Process.GetProcessById(processId);
        var status = NtQueryInformationProcess(
            process.Handle,
            0,
            out var information,
            Marshal.SizeOf<ProcessBasicInformation>(),
            out _);
        return status >= 0
            ? information.InheritedFromUniqueProcessId.ToInt32()
            : null;
    }

    private static bool IdentityHasExited(OwnedProcessIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            return process.HasExited
                || StableProcessStartIdentity.Read(process) != identity.StartIdentity;
        }
        catch (Exception exception) when (IsObservationException(exception))
        {
            return true;
        }
    }

    private static bool IsObservationException(Exception exception) => exception is
        ArgumentException
        or FileNotFoundException
        or DirectoryNotFoundException
        or InvalidOperationException
        or IOException
        or UnauthorizedAccessException
        or Win32Exception
        or NotSupportedException
        or FormatException
        or OverflowException;

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
        IntPtr processHandle,
        int processInformationClass,
        out ProcessBasicInformation processInformation,
        int processInformationLength,
        out int returnLength);
}

internal readonly record struct OwnedProcessIdentity(int ProcessId, long StartIdentity);
