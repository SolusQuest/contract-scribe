using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace ContractScribe.TestSupport;

internal static class StableProcessStartIdentity
{
    public static long Read(int processId)
    {
        using var process = Process.GetProcessById(processId);
        return Read(process);
    }

    public static long Read(Process process)
    {
        if (process is null)
        {
            throw new ArgumentNullException(nameof(process));
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return ReadLinuxStartTicks(process.Id);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (!GetProcessTimes(
                    process.Handle,
                    out var creation,
                    out _,
                    out _,
                    out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return creation;
        }

        return process.StartTime.ToUniversalTime().Ticks;
    }

    private static long ReadLinuxStartTicks(int processId)
    {
        var stat = File.ReadAllText($"/proc/{processId}/stat");
        var commandEnd = stat.LastIndexOf(')');
        if (commandEnd < 0 || commandEnd + 2 >= stat.Length)
        {
            throw new InvalidDataException("The Linux process stat record is malformed.");
        }

        var fields = stat.Substring(commandEnd + 2)
            .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        const int startTimeIndexAfterCommand = 19;
        if (fields.Length <= startTimeIndexAfterCommand)
        {
            throw new InvalidDataException("The Linux process stat record has no start identity.");
        }

        return long.Parse(
            fields[startTimeIndexAfterCommand],
            NumberStyles.None,
            CultureInfo.InvariantCulture);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        IntPtr processHandle,
        out long creationTime,
        out long exitTime,
        out long kernelTime,
        out long userTime);
}
