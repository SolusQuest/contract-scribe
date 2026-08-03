using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ContractScribe.Roslyn;

internal static class DotnetSdkResolver
{
    private const int ResolveSdk2DisallowPrerelease = 0x1;
    private const int ResolvedSdkDirectoryKey = 0;

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
            var version = Path.GetFileName(Path.TrimEndingDirectorySeparator(resolvedSdkDirectory));
            if (!Regex.IsMatch(version, @"^\d+\.\d+\.\d+$", RegexOptions.CultureInvariant))
            {
                throw LoaderException.Toolchain("toolchain.sdk-unavailable");
            }
            return version;
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
