using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ContractScribe.HostValidation;

internal sealed class LinuxSubjectProcess : IDisposable
{
    private const int SigKill = 9;
    private const int FGetFl = 3;
    private const int FSetFl = 4;
    private const int ONonBlock = 0x800;
    private const int EBadF = 9;
    private const int EAgain = 11;
    private const int EIntr = 4;
    private const short PollIn = 0x0001;
    private const short PollError = 0x0008;
    private const short PollHangUp = 0x0010;
    private const short PollInvalid = 0x0020;
    private const int PollSliceMilliseconds = 25;

    private LinuxSubjectProcess(
        Process process,
        Stream standardOutput,
        Stream standardError)
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
            Stream? standardError = null;
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

    internal static HeldPipeForSelfTest CreateHeldPipeForSelfTest()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException();
        }

        var descriptors = new int[2];
        if (Pipe(descriptors) != 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        var readDescriptor = descriptors[0];
        var writeDescriptor = descriptors[1];
        try
        {
            var reader = OpenReadStream(ref readDescriptor);
            var held = new HeldPipeForSelfTest(
                reader,
                writeDescriptor);
            writeDescriptor = -1;
            return held;
        }
        finally
        {
            CloseDescriptor(ref readDescriptor);
            CloseDescriptor(ref writeDescriptor);
        }
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

    private static Stream OpenReadStream(ref int descriptor)
    {
        var ownedDescriptor = descriptor;
        SetNonblocking(ownedDescriptor);
        descriptor = -1;
        return new LinuxNonblockingPipeStream(ownedDescriptor);
    }

    private static void SetNonblocking(int descriptor)
    {
        var flags = Fcntl(descriptor, FGetFl);
        if (flags < 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        if ((flags & ONonBlock) != 0)
        {
            return;
        }
        if (Fcntl(descriptor, FSetFl, flags | ONonBlock) != 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
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

    internal sealed class HeldPipeForSelfTest : IDisposable
    {
        private int writeDescriptor;

        internal HeldPipeForSelfTest(
            Stream reader,
            int writeDescriptor)
        {
            Reader = reader;
            this.writeDescriptor = writeDescriptor;
        }

        internal Stream Reader { get; }

        internal bool WriterOpen => writeDescriptor >= 0;

        public void Dispose()
        {
            Reader.Dispose();
            var descriptor = Interlocked.Exchange(
                ref writeDescriptor,
                -1);
            if (descriptor >= 0)
            {
                _ = LinuxSubjectProcess.Close(descriptor);
            }
        }
    }

    private sealed class LinuxNonblockingPipeStream : Stream
    {
        private readonly SafeFileHandle handle;

        internal LinuxNonblockingPipeStream(int descriptor)
        {
            handle = new(
                new IntPtr(descriptor),
                ownsHandle: true);
        }

        public override bool CanRead =>
            !handle.IsClosed && !handle.IsInvalid;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length =>
            throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(
            byte[] buffer,
            int offset,
            int count) =>
            throw new NotSupportedException(
                "Synchronous Linux pipe reads are not supported.");

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadAsync(
                buffer.AsMemory(offset, count),
                cancellationToken).AsTask();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (buffer.Length == 0)
            {
                return ValueTask.FromResult(0);
            }
            return new(Task.Run(
                () => ReadCore(buffer, cancellationToken),
                CancellationToken.None));
        }

        public override long Seek(
            long offset,
            SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(
            byte[] buffer,
            int offset,
            int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                handle.Dispose();
            }
            base.Dispose(disposing);
        }

        private int ReadCore(
            Memory<byte> destination,
            CancellationToken cancellationToken)
        {
            var handleReferenceAdded = false;
            try
            {
                handle.DangerousAddRef(
                    ref handleReferenceAdded);
                var activeDescriptor = checked(
                    (int)handle.DangerousGetHandle());
                var rented =
                    ArrayPool<byte>.Shared.Rent(destination.Length);
                try
                {
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var pollDescriptor = new PollFileDescriptor
                        {
                            FileDescriptor = activeDescriptor,
                            Events = PollIn
                        };
                        var pollResult = LinuxSubjectProcess.Poll(
                            ref pollDescriptor,
                            1,
                            PollSliceMilliseconds);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (pollResult == 0)
                        {
                            continue;
                        }
                        if (pollResult < 0)
                        {
                            var pollError =
                                Marshal.GetLastPInvokeError();
                            if (pollError == EIntr)
                            {
                                continue;
                            }
                            throw NativeIOException(
                                "poll",
                                pollError);
                        }
                        if ((pollDescriptor.ReturnedEvents
                             & PollInvalid) != 0)
                        {
                            throw new ObjectDisposedException(
                                nameof(LinuxNonblockingPipeStream));
                        }
                        if ((pollDescriptor.ReturnedEvents
                             & (PollIn | PollError | PollHangUp)) == 0)
                        {
                            continue;
                        }

                        var readResult = LinuxSubjectProcess.Read(
                            activeDescriptor,
                            rented,
                            checked((nuint)destination.Length));
                        if (readResult > 0)
                        {
                            var count = checked((int)readResult);
                            rented.AsSpan(0, count).CopyTo(
                                destination.Span);
                            return count;
                        }
                        if (readResult == 0)
                        {
                            return 0;
                        }
                        var readError = Marshal.GetLastPInvokeError();
                        if (readError is EIntr or EAgain)
                        {
                            continue;
                        }
                        if (readError == EBadF)
                        {
                            throw new ObjectDisposedException(
                                nameof(LinuxNonblockingPipeStream));
                        }
                        throw NativeIOException("read", readError);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }
            finally
            {
                if (handleReferenceAdded)
                {
                    handle.DangerousRelease();
                }
            }
        }

        private static IOException NativeIOException(
            string operation,
            int error) =>
            new(
                $"{operation} failed with errno {error}: "
                + new Win32Exception(error).Message);
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

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int Fcntl(int fileDescriptor, int command);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int Fcntl(
        int fileDescriptor,
        int command,
        int argument);

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int processId, int signal);

    [DllImport("libc", EntryPoint = "pipe", SetLastError = true)]
    private static extern int Pipe([Out] int[] descriptors);

    [DllImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static extern int Poll(
        ref PollFileDescriptor descriptor,
        nuint descriptorCount,
        int timeoutMilliseconds);

    [DllImport("libc", EntryPoint = "read", SetLastError = true)]
    private static extern nint Read(
        int fileDescriptor,
        [Out] byte[] buffer,
        nuint count);

    [DllImport("libc", EntryPoint = "waitpid", SetLastError = true)]
    private static extern int WaitPid(int processId, out int status, int options);

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFileDescriptor
    {
        public int FileDescriptor;
        public short Events;
        public short ReturnedEvents;
    }
}
