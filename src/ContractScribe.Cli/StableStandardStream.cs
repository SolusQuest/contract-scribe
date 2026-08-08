using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ContractScribe.Cli;

internal static class StableStandardStream
{
    private const int StandardOutputHandle = -11;
    private const uint DuplicateSameAccess = 0x00000002;

    public static Stream OpenOutput()
    {
        if (!OperatingSystem.IsWindows())
        {
            var descriptor = Duplicate(1);
            if (descriptor < 0)
            {
                throw new IOException("Unable to duplicate standard output.");
            }
            return new FileStream(
                new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true),
                FileAccess.Write);
        }

        var process = GetCurrentProcess();
        if (!DuplicateHandle(
                process,
                GetStdHandle(StandardOutputHandle),
                process,
                out var duplicate,
                0,
                inheritHandle: false,
                DuplicateSameAccess))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        return new FileStream(
            new SafeFileHandle(duplicate, ownsHandle: true),
            FileAccess.Write);
    }

    [DllImport("libc", EntryPoint = "dup", SetLastError = true)]
    private static extern int Duplicate(int descriptor);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(
        IntPtr sourceProcess,
        IntPtr sourceHandle,
        IntPtr targetProcess,
        out IntPtr targetHandle,
        uint desiredAccess,
        bool inheritHandle,
        uint options);
}
