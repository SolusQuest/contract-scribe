using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ContractScribe.Roslyn;

internal static class DocumentationPatchBaselineFileSystem
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileTypeDisk = 0x0001;
    private const int UnixOpenReadOnly = 0;
    private const int UnixOpenCloseOnExec = 0x80000;
    private const int UnixOpenNoFollow = 0x20000;
    private const int UnixOpenDirectory = 0x10000;
    private const uint UnixFileTypeMask = 0xF000;
    private const uint UnixRegularFile = 0x8000;
    private const uint UnixDirectory = 0x4000;

    public static DocumentationPatchStableFileRead ReadRegularFile(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = OpenRegularFile(path);
        var before = ReadIdentity(stream.SafeFileHandle, expectDirectory: false);
        using var buffer = new MemoryStream(before.Length <= int.MaxValue
            ? checked((int)before.Length)
            : 0);
        var chunk = new byte[64 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(chunk, 0, chunk.Length);
            if (read == 0)
            {
                break;
            }

            buffer.Write(chunk, 0, read);
        }

        var after = ReadIdentity(stream.SafeFileHandle, expectDirectory: false);
        var bytes = buffer.ToArray();
        if (before != after || before.Length != bytes.LongLength)
        {
            throw DocumentationPatchBaselineException.Stale();
        }

        stream.Dispose();
        using var rebound = OpenRegularFile(path);
        if (ReadIdentity(rebound.SafeFileHandle, expectDirectory: false) != before)
        {
            throw DocumentationPatchBaselineException.Stale();
        }

        return new DocumentationPatchStableFileRead(bytes, before);
    }

    public static DocumentationPatchPhysicalIdentity ReadDirectoryIdentity(string path)
    {
        using var handle = OpenDirectory(path);
        return ReadIdentity(handle, expectDirectory: true);
    }

    private static FileStream OpenRegularFile(string path)
    {
        SafeFileHandle handle;
        if (OperatingSystem.IsWindows())
        {
            handle = CreateFileW(
                path,
                GenericRead,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOpenReparsePoint,
                IntPtr.Zero);
        }
        else if (OperatingSystem.IsLinux())
        {
            handle = new SafeFileHandle(
                (IntPtr)Open(
                    path,
                    UnixOpenReadOnly | UnixOpenCloseOnExec | UnixOpenNoFollow),
                ownsHandle: true);
        }
        else
        {
            throw DocumentationPatchBaselineException.Rejected();
        }

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException("Stable repository file open failed.", new Win32Exception(error));
        }

        try
        {
            _ = ReadIdentity(handle, expectDirectory: false);
            return new FileStream(handle, FileAccess.Read, 64 * 1024, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenDirectory(string path)
    {
        EnsureNoReparseComponents(path);
        SafeFileHandle handle;
        if (OperatingSystem.IsWindows())
        {
            handle = CreateFileW(
                path,
                0,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero);
        }
        else if (OperatingSystem.IsLinux())
        {
            handle = new SafeFileHandle(
                (IntPtr)Open(
                    path,
                    UnixOpenReadOnly | UnixOpenCloseOnExec | UnixOpenNoFollow | UnixOpenDirectory),
                ownsHandle: true);
        }
        else
        {
            throw DocumentationPatchBaselineException.Rejected();
        }

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException("Stable repository directory open failed.", new Win32Exception(error));
        }

        return handle;
    }

    private static DocumentationPatchPhysicalIdentity ReadIdentity(
        SafeFileHandle handle,
        bool expectDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            if (GetFileType(handle) != FileTypeDisk
                || !GetFileInformationByHandle(handle, out var information)
                || (information.Attributes & FileAttributes.ReparsePoint) != 0
                || ((information.Attributes & FileAttributes.Directory) != 0) != expectDirectory)
            {
                throw DocumentationPatchBaselineException.Rejected();
            }

            return new DocumentationPatchPhysicalIdentity(
                information.VolumeSerialNumber,
                ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow,
                expectDirectory
                    ? 0
                    : ((long)information.FileSizeHigh << 32) | information.FileSizeLow,
                information.NumberOfLinks,
                expectDirectory);
        }

        if (OperatingSystem.IsLinux())
        {
            if (FStat(handle, out var information) != 0
                || (information.Mode & UnixFileTypeMask) != (expectDirectory
                    ? UnixDirectory
                    : UnixRegularFile))
            {
                throw DocumentationPatchBaselineException.Rejected();
            }

            return new DocumentationPatchPhysicalIdentity(
                information.Device,
                information.Inode,
                expectDirectory ? 0 : information.Size,
                information.LinkCount,
                expectDirectory);
        }

        throw DocumentationPatchBaselineException.Rejected();
    }

    private static void EnsureNoReparseComponents(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw DocumentationPatchBaselineException.Rejected();
        var current = root;
        foreach (var segment in fullPath[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Join(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw DocumentationPatchBaselineException.Rejected();
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsFileInformation
    {
        public FileAttributes Attributes;
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out WindowsFileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle file);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int FStat(SafeFileHandle file, out LinuxStat information);
}

internal sealed record DocumentationPatchStableFileRead(
    byte[] Bytes,
    DocumentationPatchPhysicalIdentity Identity);
