using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ContractScribe.Cli;

internal sealed class StandardStreamIsolation : IDisposable
{
    private const int StandardOutputDescriptor = 1;
    private const int StandardErrorDescriptor = 2;
    private const int FileDescriptorFlags = 2;
    private const int CloseOnExec = 1;
    private const int WriteOnly = 1;
    private const int Binary = 0x8000;
    private const int StandardOutputHandle = -11;
    private const int StandardErrorHandle = -12;
    private const uint DuplicateSameAccess = 0x00000002;
    private const uint GenericWrite = 0x40000000;
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;

    private SafeFileHandle? presentationOutput;
    private SafeFileHandle? presentationError;
    private readonly SafeFileHandle? windowsNullHandle;
    private bool disposed;

    private StandardStreamIsolation(
        SafeFileHandle presentationOutput,
        SafeFileHandle presentationError,
        SafeFileHandle? windowsNullHandle)
    {
        this.presentationOutput = presentationOutput;
        this.presentationError = presentationError;
        this.windowsNullHandle = windowsNullHandle;
    }

    public static StandardStreamIsolation Install()
    {
        SafeFileHandle? output = null;
        SafeFileHandle? error = null;
        SafeFileHandle? nullHandle = null;
        try
        {
            output = DuplicateForPresentation(StandardOutputDescriptor, StandardOutputHandle);
            error = DuplicateForPresentation(StandardErrorDescriptor, StandardErrorHandle);
            nullHandle = OperatingSystem.IsWindows()
                ? RedirectWindowsStreams()
                : RedirectUnixStreams();

            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
            return new StandardStreamIsolation(output, error, nullHandle);
        }
        catch
        {
            output?.Dispose();
            error?.Dispose();
            nullHandle?.Dispose();
            throw;
        }
    }

    public Stream OpenPresentationOutput() =>
        OpenPresentationStream(ref presentationOutput);

    public Stream OpenPresentationError() =>
        OpenPresentationStream(ref presentationError);

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        presentationOutput?.Dispose();
        presentationError?.Dispose();
        windowsNullHandle?.Dispose();
    }

    private static Stream OpenPresentationStream(ref SafeFileHandle? handle)
    {
        var owned = handle
            ?? throw new InvalidOperationException("The presentation stream was already opened.");
        handle = null;
        return new FileStream(owned, FileAccess.Write, bufferSize: 4096, isAsync: false);
    }

    private static SafeFileHandle DuplicateForPresentation(
        int descriptor,
        int standardHandle)
    {
        if (OperatingSystem.IsWindows())
        {
            var source = GetStdHandle(standardHandle);
            if (source == IntPtr.Zero || source == new IntPtr(-1))
            {
                throw new IOException("A presentation standard handle is unavailable.");
            }

            var process = GetCurrentProcess();
            if (!DuplicateHandle(
                    process,
                    source,
                    process,
                    out var duplicate,
                    desiredAccess: 0,
                    inheritHandle: false,
                    DuplicateSameAccess))
            {
                throw new IOException(
                    "A presentation standard handle could not be duplicated.",
                    new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            }

            return duplicate;
        }

        var saved = UnixDuplicate(descriptor);
        if (saved < 0)
        {
            throw new IOException(
                "A presentation standard descriptor could not be duplicated.",
                new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        }

        if (UnixFcntl(saved, FileDescriptorFlags, CloseOnExec) < 0)
        {
            _ = UnixClose(saved);
            throw new IOException(
                "A presentation standard descriptor could not be isolated from child processes.",
                new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        }

        return new SafeFileHandle(new IntPtr(saved), ownsHandle: true);
    }

    private static SafeFileHandle RedirectWindowsStreams()
    {
        _ = WindowsFlush(IntPtr.Zero);
        var attributes = new SecurityAttributes
        {
            Length = Marshal.SizeOf<SecurityAttributes>(),
            InheritHandle = 1,
        };
        var nullHandle = CreateFile(
            "NUL",
            GenericWrite,
            ShareRead | ShareWrite,
            ref attributes,
            OpenExisting,
            FileAttributeNormal,
            IntPtr.Zero);
        if (nullHandle.IsInvalid)
        {
            nullHandle.Dispose();
            throw new IOException(
                "The Windows output sink could not be opened.",
                new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        }

        try
        {
            if (!SetStdHandle(StandardOutputHandle, nullHandle)
                || !SetStdHandle(StandardErrorHandle, nullHandle))
            {
                throw new IOException(
                    "The Windows process standard streams could not be isolated.",
                    new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            }

            var nullDescriptor = WindowsOpen("NUL", WriteOnly | Binary);
            if (nullDescriptor < 0)
            {
                throw new IOException("The Windows CRT output sink could not be opened.");
            }

            var redirected = WindowsDuplicateTo(nullDescriptor, StandardOutputDescriptor) >= 0
                && WindowsDuplicateTo(nullDescriptor, StandardErrorDescriptor) >= 0;
            var closed = WindowsClose(nullDescriptor) == 0;
            if (!redirected || !closed)
            {
                throw new IOException("The Windows CRT standard streams could not be isolated.");
            }

            return nullHandle;
        }
        catch
        {
            nullHandle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle? RedirectUnixStreams()
    {
        _ = UnixFlush(IntPtr.Zero);
        var nullDescriptor = UnixOpen("/dev/null", WriteOnly);
        if (nullDescriptor < 0)
        {
            throw new IOException(
                "The Unix output sink could not be opened.",
                new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        }

        var redirected = UnixDuplicateTo(nullDescriptor, StandardOutputDescriptor) >= 0
            && UnixDuplicateTo(nullDescriptor, StandardErrorDescriptor) >= 0;
        var closed = UnixClose(nullDescriptor) == 0;
        if (!redirected || !closed)
        {
            throw new IOException(
                "The Unix standard streams could not be isolated.",
                new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        }

        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetStdHandle(int standardHandle, SafeFileHandle handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(
        IntPtr sourceProcess,
        IntPtr sourceHandle,
        IntPtr targetProcess,
        out SafeFileHandle targetHandle,
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint options);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        ref SecurityAttributes securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("ucrtbase.dll", EntryPoint = "_dup2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int WindowsDuplicateTo(int source, int destination);

    [DllImport("ucrtbase.dll", EntryPoint = "_open", CallingConvention = CallingConvention.Cdecl)]
    private static extern int WindowsOpen(
        [MarshalAs(UnmanagedType.LPStr)] string path,
        int flags);

    [DllImport("ucrtbase.dll", EntryPoint = "_close", CallingConvention = CallingConvention.Cdecl)]
    private static extern int WindowsClose(int descriptor);

    [DllImport("ucrtbase.dll", EntryPoint = "fflush", CallingConvention = CallingConvention.Cdecl)]
    private static extern int WindowsFlush(IntPtr stream);

    [DllImport("libc", EntryPoint = "dup", SetLastError = true)]
    private static extern int UnixDuplicate(int descriptor);

    [DllImport("libc", EntryPoint = "dup2", SetLastError = true)]
    private static extern int UnixDuplicateTo(int source, int destination);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int UnixOpen(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int UnixFcntl(int descriptor, int command, int value);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int UnixClose(int descriptor);

    [DllImport("libc", EntryPoint = "fflush")]
    private static extern int UnixFlush(IntPtr stream);
}
