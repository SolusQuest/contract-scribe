using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ContractScribe.Cli;

internal enum FileSystemEntryKind
{
    Absent,
    RegularFile,
    Directory,
    Link,
    Other,
}

internal static class FileSystemEntryClassifier
{
    private const int UnixOpenPath = 0x200000;
    private const int UnixOpenNoFollow = 0x20000;
    private const int UnixOpenCloseOnExec = 0x80000;
    private const uint UnixFileTypeMask = 0xF000;
    private const uint UnixRegularFile = 0x8000;
    private const uint UnixDirectory = 0x4000;
    private const uint UnixSymbolicLink = 0xA000;

    private const uint WindowsShareRead = 0x00000001;
    private const uint WindowsShareWrite = 0x00000002;
    private const uint WindowsShareDelete = 0x00000004;
    private const uint WindowsOpenExisting = 3;
    private const uint WindowsOpenReparsePoint = 0x00200000;
    private const uint WindowsBackupSemantics = 0x02000000;
    private const uint WindowsFileTypeDisk = 0x0001;
    public static FileSystemEntryKind Classify(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return OperatingSystem.IsWindows()
            ? ClassifyWindows(path)
            : OperatingSystem.IsLinux()
                ? ClassifyLinux(path)
                : ClassifyPortable(path);
    }

    private static FileSystemEntryKind ClassifyLinux(string path)
    {
        var descriptor = LinuxOpen(
            path,
            UnixOpenPath | UnixOpenNoFollow | UnixOpenCloseOnExec);
        if (descriptor < 0)
        {
            return HandleUnixFailure(path);
        }

        using var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        if (LinuxFStat(handle, out var status) != 0)
        {
            throw CreateUnixIOException(path);
        }
        return (status.Mode & UnixFileTypeMask) switch
        {
            UnixRegularFile => FileSystemEntryKind.RegularFile,
            UnixDirectory => FileSystemEntryKind.Directory,
            UnixSymbolicLink => FileSystemEntryKind.Link,
            _ => FileSystemEntryKind.Other,
        };
    }

    private static FileSystemEntryKind HandleUnixFailure(string path) =>
        Marshal.GetLastPInvokeError() switch
        {
            2 or 20 => FileSystemEntryKind.Absent,
            1 or 13 => throw new UnauthorizedAccessException(
                $"Access to '{path}' was denied."),
            _ => throw CreateUnixIOException(path),
        };

    private static IOException CreateUnixIOException(string path)
    {
        var error = Marshal.GetLastPInvokeError();
        return new IOException(
            $"Unable to inspect '{path}': {new Win32Exception(error).Message}",
            error);
    }

    private static FileSystemEntryKind ClassifyWindows(string path)
    {
        var handle = WindowsCreateFile(
            path,
            0,
            WindowsShareRead | WindowsShareWrite | WindowsShareDelete,
            IntPtr.Zero,
            WindowsOpenExisting,
            WindowsOpenReparsePoint | WindowsBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            if (error is 2 or 3)
            {
                return FileSystemEntryKind.Absent;
            }
            if (error == 5)
            {
                throw new UnauthorizedAccessException(
                    $"Access to '{path}' was denied.");
            }
            throw new IOException(
                $"Unable to inspect '{path}': {new Win32Exception(error).Message}",
                error);
        }

        using (handle)
        {
            if (!WindowsGetFileInformationByHandle(handle, out var information))
            {
                var error = Marshal.GetLastPInvokeError();
                throw new IOException(
                    $"Unable to inspect '{path}': {new Win32Exception(error).Message}",
                    error);
            }
            var attributes = (FileAttributes)information.FileAttributes;
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return FileSystemEntryKind.Link;
            }
            if (attributes.HasFlag(FileAttributes.Directory))
            {
                return FileSystemEntryKind.Directory;
            }
            return WindowsGetFileType(handle) == WindowsFileTypeDisk
                ? FileSystemEntryKind.RegularFile
                : FileSystemEntryKind.Other;
        }
    }

    private static FileSystemEntryKind ClassifyPortable(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return FileSystemEntryKind.Link;
            }
            return attributes.HasFlag(FileAttributes.Directory)
                ? FileSystemEntryKind.Directory
                : FileSystemEntryKind.RegularFile;
        }
        catch (Exception exception)
            when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return FileSystemEntryKind.Absent;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxStat
    {
        public ulong Device;
        public ulong Inode;
        public ulong LinkCount;
        public uint Mode;
        public uint UserId;
        public uint GroupId;
        public int Padding;
        public ulong DeviceType;
        public long Size;
        public long BlockSize;
        public long Blocks;
        public long AccessTimeSeconds;
        public ulong AccessTimeNanoseconds;
        public long ModificationTimeSeconds;
        public ulong ModificationTimeNanoseconds;
        public long ChangeTimeSeconds;
        public ulong ChangeTimeNanoseconds;
        public long Reserved1;
        public long Reserved2;
        public long Reserved3;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int LinuxOpen(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int LinuxFStat(SafeFileHandle file, out LinuxStat status);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle WindowsCreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WindowsGetFileInformationByHandle(
        SafeFileHandle file,
        out WindowsFileInformation information);

    [DllImport("kernel32.dll", EntryPoint = "GetFileType", SetLastError = true)]
    private static extern uint WindowsGetFileType(SafeFileHandle file);
}
