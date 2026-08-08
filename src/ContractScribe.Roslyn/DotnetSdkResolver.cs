using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ContractScribe.Roslyn;

internal static class DotnetSdkResolver
{
    private const int ResolveSdk2DisallowPrerelease = 0x1;
    private const int ResolvedSdkDirectoryKey = 0;
    private static readonly object ErrorWriterGate = new();

    public static string Resolve(string dotnetHost, string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dotnetHost);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        var dotnetRoot = Path.GetDirectoryName(Path.GetFullPath(dotnetHost))
            ?? throw LoaderException.Toolchain("toolchain.sdk-unavailable");
        var libraryPath = ResolveHostFxrLibrary(dotnetRoot);
        var handle = NativeLibrary.Load(libraryPath);
        try
        {
            lock (ErrorWriterGate)
            {
                var setErrorWriterExport = NativeLibrary.GetExport(
                    handle,
                    "hostfxr_set_error_writer");
                var setErrorWriter = Marshal.GetDelegateForFunctionPointer<SetErrorWriter>(
                    setErrorWriterExport);
                HostFxrErrorWriter errorWriter = _ => { };
                var errorWriterPointer = Marshal.GetFunctionPointerForDelegate(errorWriter);
                var previousErrorWriter = setErrorWriter(errorWriterPointer);
                using var nativeOutput = NativeOutputSuppression.Install();
                try
                {
                    var export = NativeLibrary.GetExport(handle, "hostfxr_resolve_sdk2");
                    string? resolvedSdkDirectory = null;
                    int result;
                    if (OperatingSystem.IsWindows())
                    {
                        WindowsResultCallback callback = (key, value) =>
                        {
                            if (key == ResolvedSdkDirectoryKey)
                            {
                                resolvedSdkDirectory = Marshal.PtrToStringUni(value);
                            }
                        };
                        var resolver = Marshal.GetDelegateForFunctionPointer<WindowsResolveSdk2>(export);
                        result = resolver(
                            dotnetRoot,
                            Path.GetFullPath(workingDirectory),
                            ResolveSdk2DisallowPrerelease,
                            callback);
                        GC.KeepAlive(callback);
                    }
                    else
                    {
                        UnixResultCallback callback = (key, value) =>
                        {
                            if (key == ResolvedSdkDirectoryKey)
                            {
                                resolvedSdkDirectory = Marshal.PtrToStringUTF8(value);
                            }
                        };
                        var resolver = Marshal.GetDelegateForFunctionPointer<UnixResolveSdk2>(export);
                        result = resolver(
                            dotnetRoot,
                            Path.GetFullPath(workingDirectory),
                            ResolveSdk2DisallowPrerelease,
                            callback);
                        GC.KeepAlive(callback);
                    }

                    if (result != 0
                        || string.IsNullOrEmpty(resolvedSdkDirectory)
                        || !Path.IsPathRooted(resolvedSdkDirectory))
                    {
                        throw LoaderException.Toolchain("toolchain.sdk-unavailable");
                    }
                    var version = Path.GetFileName(
                        Path.TrimEndingDirectorySeparator(resolvedSdkDirectory));
                    if (!Regex.IsMatch(
                            version,
                            @"^\d+\.\d+\.\d+$",
                            RegexOptions.CultureInvariant))
                    {
                        throw LoaderException.Toolchain("toolchain.sdk-unavailable");
                    }
                    return version;
                }
                finally
                {
                    _ = setErrorWriter(previousErrorWriter);
                    GC.KeepAlive(errorWriter);
                }
            }
        }
        catch (LoaderException)
        {
            throw;
        }
        catch (Exception)
        {
            throw LoaderException.Toolchain("toolchain.sdk-unavailable");
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }

    private static string ResolveHostFxrLibrary(string dotnetRoot)
    {
        var fxrRoot = Path.Join(dotnetRoot, "host", "fxr");
        if (!Directory.Exists(fxrRoot))
        {
            throw LoaderException.Toolchain("toolchain.sdk-unavailable");
        }
        var candidates = Directory.EnumerateDirectories(fxrRoot)
            .Select(path => (Path: path, Version: ParseVersion(Path.GetFileName(path))))
            .Where(item => item.Version is not null)
            .OrderByDescending(item => item.Version)
            .ToArray();
        var fileName = OperatingSystem.IsWindows()
            ? "hostfxr.dll"
            : OperatingSystem.IsMacOS()
                ? "libhostfxr.dylib"
                : "libhostfxr.so";
        var matches = candidates
            .Select(item => Path.Join(item.Path, fileName))
            .Where(File.Exists)
            .ToArray();
        return matches.Length > 0
            ? matches[0]
            : throw LoaderException.Toolchain("toolchain.sdk-unavailable");
    }

    private static Version? ParseVersion(string value) =>
        Version.TryParse(value.Split('-', 2)[0], out var version)
            ? version
            : null;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void WindowsResultCallback(int key, IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void UnixResultCallback(int key, IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void HostFxrErrorWriter(IntPtr message);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr SetErrorWriter(IntPtr errorWriter);

    private sealed class NativeOutputSuppression : IDisposable
    {
        private const int StandardOutput = 1;
        private const int WriteOnly = 1;
        private const int StandardOutputHandle = -11;
        private const uint GenericWrite = 0x40000000;
        private const uint ShareRead = 0x00000001;
        private const uint ShareWrite = 0x00000002;
        private const uint OpenExisting = 3;
        private readonly int savedOutput;
        private readonly IntPtr savedWindowsOutput;
        private readonly IntPtr nullWindowsOutput;
        private bool disposed;

        private NativeOutputSuppression(
            int savedOutput,
            IntPtr savedWindowsOutput,
            IntPtr nullWindowsOutput)
        {
            this.savedOutput = savedOutput;
            this.savedWindowsOutput = savedWindowsOutput;
            this.nullWindowsOutput = nullWindowsOutput;
        }

        public static NativeOutputSuppression Install()
        {
            _ = Flush(IntPtr.Zero);
            var saved = Duplicate(StandardOutput);
            if (saved < 0)
            {
                throw LoaderException.Toolchain("toolchain.sdk-unavailable");
            }
            var nullOutput = OpenNull(WriteOnly);
            if (nullOutput < 0 || DuplicateTo(nullOutput, StandardOutput) < 0)
            {
                if (nullOutput >= 0)
                {
                    _ = Close(nullOutput);
                }
                _ = Close(saved);
                throw LoaderException.Toolchain("toolchain.sdk-unavailable");
            }
            _ = Close(nullOutput);
            var savedWindowsOutput = IntPtr.Zero;
            var nullWindowsOutput = IntPtr.Zero;
            if (OperatingSystem.IsWindows())
            {
                savedWindowsOutput = GetStdHandle(StandardOutputHandle);
                nullWindowsOutput = CreateFile(
                    "NUL",
                    GenericWrite,
                    ShareRead | ShareWrite,
                    IntPtr.Zero,
                    OpenExisting,
                    0,
                    IntPtr.Zero);
                if (nullWindowsOutput == new IntPtr(-1)
                    || !SetStdHandle(StandardOutputHandle, nullWindowsOutput))
                {
                    if (nullWindowsOutput != new IntPtr(-1))
                    {
                        _ = CloseHandle(nullWindowsOutput);
                    }
                    _ = DuplicateTo(saved, StandardOutput);
                    _ = Close(saved);
                    throw LoaderException.Toolchain("toolchain.sdk-unavailable");
                }
            }
            return new NativeOutputSuppression(
                saved,
                savedWindowsOutput,
                nullWindowsOutput);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            _ = Flush(IntPtr.Zero);
            _ = DuplicateTo(savedOutput, StandardOutput);
            _ = Close(savedOutput);
            if (OperatingSystem.IsWindows())
            {
                _ = SetStdHandle(StandardOutputHandle, savedWindowsOutput);
                _ = CloseHandle(nullWindowsOutput);
            }
        }

        private static int Duplicate(int descriptor) => OperatingSystem.IsWindows()
            ? WindowsDuplicate(descriptor)
            : UnixDuplicate(descriptor);

        private static int DuplicateTo(int source, int destination) => OperatingSystem.IsWindows()
            ? WindowsDuplicateTo(source, destination)
            : UnixDuplicateTo(source, destination);

        private static int OpenNull(int flags) => OperatingSystem.IsWindows()
            ? WindowsOpen("NUL", flags)
            : UnixOpen("/dev/null", flags);

        private static int Close(int descriptor) => OperatingSystem.IsWindows()
            ? WindowsClose(descriptor)
            : UnixClose(descriptor);

        private static int Flush(IntPtr stream) => OperatingSystem.IsWindows()
            ? WindowsFlush(stream)
            : UnixFlush(stream);

        [DllImport("ucrtbase.dll", EntryPoint = "_dup", CallingConvention = CallingConvention.Cdecl)]
        private static extern int WindowsDuplicate(int descriptor);

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

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int standardHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetStdHandle(int standardHandle, IntPtr handle);

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("libc", EntryPoint = "dup", SetLastError = true)]
        private static extern int UnixDuplicate(int descriptor);

        [DllImport("libc", EntryPoint = "dup2", SetLastError = true)]
        private static extern int UnixDuplicateTo(int source, int destination);

        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        private static extern int UnixOpen(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
            int flags);

        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        private static extern int UnixClose(int descriptor);

        [DllImport("libc", EntryPoint = "fflush")]
        private static extern int UnixFlush(IntPtr stream);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private delegate int WindowsResolveSdk2(
        [MarshalAs(UnmanagedType.LPWStr)] string executableDirectory,
        [MarshalAs(UnmanagedType.LPWStr)] string workingDirectory,
        int flags,
        WindowsResultCallback callback);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int UnixResolveSdk2(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string executableDirectory,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string workingDirectory,
        int flags,
        UnixResultCallback callback);
}
