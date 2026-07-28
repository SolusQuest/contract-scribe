using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ContractScribe.HostValidation;

internal sealed class LinuxSubjectProcess : IDisposable
{
    private const int SigKill = 9;

    private LinuxSubjectProcess(
        Process process,
        FileStream standardOutput,
        FileStream standardError)
    {
        Process = process;
        StandardOutput = standardOutput;
        StandardError = standardError;
    }

    internal Process Process { get; }

    internal Stream StandardOutput { get; }

    internal Stream StandardError { get; }

    internal static LinuxSubjectProcess Start(ProcessStartInfo startInfo)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException();
        }
        if (!startInfo.RedirectStandardOutput
            || !startInfo.RedirectStandardError
            || startInfo.UseShellExecute)
        {
            throw new InvalidOperationException(
                "The native Linux subject launcher requires redirected output and no shell.");
        }

        var workingDirectory = string.IsNullOrEmpty(
            startInfo.WorkingDirectory)
                ? Environment.CurrentDirectory
                : startInfo.WorkingDirectory;
        var executable = ResolveExecutable(
            startInfo.FileName,
            workingDirectory,
            startInfo.Environment.TryGetValue("PATH", out var path)
                ? path
                : null);
        using var arguments = new Utf8PointerArray(
            new[] { executable }.Concat(startInfo.ArgumentList));
        using var environment = new Utf8PointerArray(
            startInfo.Environment
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => $"{entry.Key}={entry.Value}"));
        var childPid = -1;
        var standardInputDescriptor = -1;
        var standardOutputDescriptor = -1;
        var standardErrorDescriptor = -1;
        try
        {
            var result = ForkAndExecProcess(
                executable,
                arguments.Pointer,
                environment.Pointer,
                workingDirectory,
                redirectStdin: 0,
                redirectStdout: 1,
                redirectStderr: 1,
                setCredentials: 0,
                userId: 0,
                groupId: 0,
                groups: IntPtr.Zero,
                groupsLength: 0,
                out childPid,
                out standardInputDescriptor,
                out standardOutputDescriptor,
                out standardErrorDescriptor);
            if (result != 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            CloseDescriptor(ref standardInputDescriptor);
            var standardOutput = OpenReadStream(
                ref standardOutputDescriptor);
            FileStream? standardError = null;
            Process? process = null;
            try
            {
                standardError = OpenReadStream(
                    ref standardErrorDescriptor);
                // GetProcessById creates a non-child observation handle. This must
                // never become Process.Start, whose Unix child registration races
                // the raw waitpid owner required by the protocol.
                process = Process.GetProcessById(childPid);
                return new(process, standardOutput, standardError);
            }
            catch (Exception exception)
            {
                process?.Dispose();
                standardError?.Dispose();
                standardOutput.Dispose();
                ReapFailedLaunch(childPid);
                throw new Win32Exception(
                    3,
                    $"The native child could not be observed: {exception.Message}");
            }
        }
        finally
        {
            CloseDescriptor(ref standardInputDescriptor);
            CloseDescriptor(ref standardOutputDescriptor);
            CloseDescriptor(ref standardErrorDescriptor);
        }
    }

    public void Dispose()
    {
        StandardOutput.Dispose();
        StandardError.Dispose();
    }

    private static string ResolveExecutable(
        string executable,
        string workingDirectory,
        string? path)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new FileNotFoundException();
        }
        if (Path.IsPathRooted(executable)
            || executable.Contains(Path.DirectorySeparatorChar)
            || executable.Contains(Path.AltDirectorySeparatorChar))
        {
            var candidate = Path.IsPathRooted(executable)
                ? Path.GetFullPath(executable)
                : Path.GetFullPath(executable, workingDirectory);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            throw new FileNotFoundException(null, executable);
        }

        foreach (var directory in (path ?? string.Empty).Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Join(directory, executable);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }
        throw new FileNotFoundException(null, executable);
    }

    private static FileStream OpenReadStream(ref int descriptor)
    {
        var ownedDescriptor = descriptor;
        descriptor = -1;
        return new(
            new SafeFileHandle(
                new IntPtr(ownedDescriptor),
                ownsHandle: true),
            FileAccess.Read,
            bufferSize: 4096,
            isAsync: false);
    }

    private static void CloseDescriptor(ref int descriptor)
    {
        if (descriptor < 0)
        {
            return;
        }
        _ = Close(descriptor);
        descriptor = -1;
    }

    private static void ReapFailedLaunch(int processId)
    {
        if (processId <= 0)
        {
            return;
        }
        _ = Kill(processId, SigKill);
        while (WaitPid(processId, out _, 0) < 0
               && Marshal.GetLastPInvokeError() == 4)
        {
            // Retry only when waitpid was interrupted before it consumed status.
        }
    }

    private sealed class Utf8PointerArray : IDisposable
    {
        private readonly List<IntPtr> strings = [];

        internal Utf8PointerArray(IEnumerable<string> values)
        {
            var materialized = values.ToArray();
            Pointer = Marshal.AllocHGlobal(
                checked((materialized.Length + 1) * IntPtr.Size));
            for (var index = 0; index <= materialized.Length; index++)
            {
                Marshal.WriteIntPtr(
                    Pointer,
                    index * IntPtr.Size,
                    IntPtr.Zero);
            }
            try
            {
                for (var index = 0; index < materialized.Length; index++)
                {
                    var value = Marshal.StringToCoTaskMemUTF8(
                        materialized[index]);
                    strings.Add(value);
                    Marshal.WriteIntPtr(
                        Pointer,
                        index * IntPtr.Size,
                        value);
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal IntPtr Pointer { get; private set; }

        public void Dispose()
        {
            foreach (var value in strings)
            {
                Marshal.FreeCoTaskMem(value);
            }
            strings.Clear();
            if (Pointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(Pointer);
                Pointer = IntPtr.Zero;
            }
        }
    }

    [DllImport("System.Native", EntryPoint = "SystemNative_ForkAndExecProcess", SetLastError = true)]
    private static extern int ForkAndExecProcess(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string filename,
        IntPtr argv,
        IntPtr envp,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string workingDirectory,
        int redirectStdin,
        int redirectStdout,
        int redirectStderr,
        int setCredentials,
        uint userId,
        uint groupId,
        IntPtr groups,
        int groupsLength,
        out int childPid,
        out int stdinFd,
        out int stdoutFd,
        out int stderrFd);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int fileDescriptor);

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int processId, int signal);

    [DllImport("libc", EntryPoint = "waitpid", SetLastError = true)]
    private static extern int WaitPid(int processId, out int status, int options);
}
