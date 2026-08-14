using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ContractScribe.Patching.CandidateWorkspace;

internal static class DocumentationPatchCandidateFileSystem
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint CreateNew = 1;
    private const uint OpenExisting = 3;
    private const uint FileFlagWriteThrough = 0x80000000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileTypeDisk = 0x0001;
    private const int FileDispositionInformationClass = 4;
    private const int UnixOpenReadOnly = 0;
    private const int UnixOpenReadWrite = 2;
    private const int UnixOpenCreate = 0x40;
    private const int UnixOpenExclusive = 0x80;
    private const int UnixOpenSync = 0x101000;
    private const int UnixOpenCloseOnExec = 0x80000;
    private const int UnixOpenNoFollow = 0x20000;
    private const int UnixOpenDirectory = 0x10000;
    private const int UnixAtSymlinkNoFollow = 0x100;
    private const int UnixAtRemoveDirectory = 0x200;
    private const uint UnixFileTypeMask = 0xF000;
    private const uint UnixRegularFile = 0x8000;
    private const uint UnixDirectory = 0x4000;

    public static CandidatePhysicalIdentity ReadDirectoryIdentity(string path)
    {
        using var handle = OpenDirectory(path);
        return ReadIdentity(handle, expectDirectory: true);
    }

    public static CandidatePhysicalIdentity CreateNewDirectory(
        string parentPath,
        CandidatePhysicalIdentity expectedParent,
        string leafName)
    {
        RequireLeafName(leafName);
        using var parent = OpenDirectory(parentPath);
        if (ReadIdentity(parent, expectDirectory: true) != expectedParent)
        {
            throw new IOException("The candidate parent directory changed.");
        }

        var created = OperatingSystem.IsWindows()
            ? CreateDirectoryW(Path.Join(parentPath, leafName), IntPtr.Zero)
            : OperatingSystem.IsLinux()
                ? MkdirAt(
                    checked((int)parent.DangerousGetHandle()),
                    leafName,
                    Convert.ToUInt32("700", 8)) == 0
                : throw new PlatformNotSupportedException();
        if (!created)
        {
            throw new IOException(
                "A fresh candidate directory could not be created.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        return ReadDirectoryIdentity(Path.Join(parentPath, leafName));
    }

    public static FileStream CreateNewRegularFile(
        string parentPath,
        CandidatePhysicalIdentity expectedParent,
        string leafName)
    {
        RequireLeafName(leafName);
        using var parent = OpenDirectory(parentPath);
        if (ReadIdentity(parent, expectDirectory: true) != expectedParent)
        {
            throw new IOException("The candidate parent directory changed.");
        }

        SafeFileHandle handle;
        if (OperatingSystem.IsWindows())
        {
            handle = CreateFileW(
                Path.Join(parentPath, leafName),
                GenericRead | GenericWrite | DeleteAccess,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                CreateNew,
                FileFlagWriteThrough | FileFlagOpenReparsePoint,
                IntPtr.Zero);
        }
        else if (OperatingSystem.IsLinux())
        {
            handle = new SafeFileHandle(
                (IntPtr)OpenAt(
                    checked((int)parent.DangerousGetHandle()),
                    leafName,
                    UnixOpenReadWrite | UnixOpenCreate | UnixOpenExclusive
                        | UnixOpenCloseOnExec | UnixOpenNoFollow | UnixOpenSync,
                    Convert.ToUInt32("600", 8)),
                ownsHandle: true);
        }
        else
        {
            throw new PlatformNotSupportedException();
        }

        RequireValid(handle);
        try
        {
            _ = ReadIdentity(handle, expectDirectory: false);
            return new FileStream(handle, FileAccess.ReadWrite, 64 * 1024, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public static CandidateFileRead ReadOwnedRegularFile(
        string parentPath,
        CandidatePhysicalIdentity expectedParent,
        string leafName,
        CandidatePhysicalIdentity expectedFile,
        CancellationToken cancellationToken)
    {
        using var stream = OpenOwnedRegularFile(
            parentPath,
            expectedParent,
            leafName,
            expectedFile,
            includeDeleteAccess: false);
        var bytes = ReadAll(stream, cancellationToken);
        var after = ReadIdentity(stream.SafeFileHandle, expectDirectory: false);
        if (after != expectedFile || bytes.LongLength != expectedFile.Length)
        {
            throw new IOException("The candidate file changed during readback.");
        }

        return new CandidateFileRead(bytes, after);
    }

    public static bool DeleteOwnedEntry(
        string parentPath,
        CandidatePhysicalIdentity expectedParent,
        string leafName,
        CandidatePhysicalIdentity expectedEntry)
    {
        try
        {
            RequireLeafName(leafName);
            using var parent = OpenDirectory(parentPath);
            if (ReadIdentity(parent, expectDirectory: true) != expectedParent)
            {
                return false;
            }

            if (OperatingSystem.IsLinux())
            {
                if (FStatAt(
                        checked((int)parent.DangerousGetHandle()),
                        leafName,
                        out var information,
                        UnixAtSymlinkNoFollow) != 0
                    || FromLinux(information, expectedEntry.IsDirectory) != expectedEntry)
                {
                    return false;
                }

                return UnlinkAt(
                    checked((int)parent.DangerousGetHandle()),
                    leafName,
                    expectedEntry.IsDirectory ? UnixAtRemoveDirectory : 0) == 0;
            }

            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            using var entry = OpenWindowsEntryForDelete(
                Path.Join(parentPath, leafName),
                expectedEntry.IsDirectory);
            if (ReadIdentity(entry, expectedEntry.IsDirectory) != expectedEntry)
            {
                return false;
            }

            var disposition = new FileDispositionInformation
            {
                DeleteFile = true,
            };
            return SetFileInformationByHandle(
                entry,
                FileDispositionInformationClass,
                ref disposition,
                Marshal.SizeOf<FileDispositionInformation>());
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or Win32Exception)
        {
            return false;
        }
    }

    public static CandidatePhysicalIdentity ReadFileIdentity(FileStream stream) =>
        ReadIdentity(stream.SafeFileHandle, expectDirectory: false);

    private static FileStream OpenOwnedRegularFile(
        string parentPath,
        CandidatePhysicalIdentity expectedParent,
        string leafName,
        CandidatePhysicalIdentity expectedFile,
        bool includeDeleteAccess)
    {
        RequireLeafName(leafName);
        using var parent = OpenDirectory(parentPath);
        if (ReadIdentity(parent, expectDirectory: true) != expectedParent)
        {
            throw new IOException("The candidate parent directory changed.");
        }

        SafeFileHandle handle;
        if (OperatingSystem.IsWindows())
        {
            handle = CreateFileW(
                Path.Join(parentPath, leafName),
                GenericRead | (includeDeleteAccess ? DeleteAccess : 0),
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOpenReparsePoint,
                IntPtr.Zero);
        }
        else if (OperatingSystem.IsLinux())
        {
            handle = new SafeFileHandle(
                (IntPtr)OpenAt(
                    checked((int)parent.DangerousGetHandle()),
                    leafName,
                    UnixOpenReadOnly | UnixOpenCloseOnExec | UnixOpenNoFollow,
                    0),
                ownsHandle: true);
        }
        else
        {
            throw new PlatformNotSupportedException();
        }

        RequireValid(handle);
        try
        {
            if (ReadIdentity(handle, expectDirectory: false) != expectedFile)
            {
                throw new IOException("The candidate file identity changed.");
            }

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
            throw new PlatformNotSupportedException();
        }

        RequireValid(handle);
        if (!ReadIdentity(handle, expectDirectory: true).IsDirectory)
        {
            handle.Dispose();
            throw new IOException("The candidate directory is not stable.");
        }

        return handle;
    }

    private static void EnsureNoReparseComponents(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new IOException("The candidate directory root is unavailable.");
        var current = root;
        foreach (var segment in fullPath[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Join(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("The candidate directory path contains a reparse entry.");
            }
        }
    }

    private static SafeFileHandle OpenWindowsEntryForDelete(string path, bool directory)
    {
        var handle = CreateFileW(
            path,
            GenericRead | DeleteAccess,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint | (directory ? FileFlagBackupSemantics : 0),
            IntPtr.Zero);
        RequireValid(handle);
        return handle;
    }

    private static byte[] ReadAll(FileStream stream, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream(stream.Length <= int.MaxValue
            ? checked((int)stream.Length)
            : 0);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                return output.ToArray();
            }

            output.Write(buffer, 0, read);
        }
    }

    private static CandidatePhysicalIdentity ReadIdentity(
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
                throw new IOException("The candidate entry is not a regular owned entry.");
            }

            return new CandidatePhysicalIdentity(
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
            if (FStat(handle, out var information) != 0)
            {
                throw new IOException("The candidate entry identity is unavailable.");
            }

            return FromLinux(information, expectDirectory);
        }

        throw new PlatformNotSupportedException();
    }

    private static CandidatePhysicalIdentity FromLinux(
        LinuxStat information,
        bool expectDirectory)
    {
        if ((information.Mode & UnixFileTypeMask) != (expectDirectory
                ? UnixDirectory
                : UnixRegularFile))
        {
            throw new IOException("The candidate entry has an unsupported kind.");
        }

        return new CandidatePhysicalIdentity(
            information.Device,
            information.Inode,
            expectDirectory ? 0 : information.Size,
            information.LinkCount,
            expectDirectory);
    }

    private static void RequireLeafName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name is "." or ".."
            || name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new ArgumentException("A candidate entry name must be one path segment.", nameof(name));
        }
    }

    private static void RequireValid(SafeFileHandle handle)
    {
        if (!handle.IsInvalid)
        {
            return;
        }

        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new IOException("A stable candidate handle could not be opened.", new Win32Exception(error));
    }

    internal readonly record struct CandidatePhysicalIdentity(
        ulong Volume,
        ulong FileId,
        long Length,
        ulong LinkCount,
        bool IsDirectory);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformation
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool DeleteFile;
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(
        string path,
        IntPtr securityAttributes);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out WindowsFileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle file);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        ref FileDispositionInformation fileInformation,
        int bufferSize);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAt(int directoryFileDescriptor, string path, int flags, uint mode);

    [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)]
    private static extern int MkdirAt(int directoryFileDescriptor, string path, uint mode);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int FStat(SafeFileHandle file, out LinuxStat information);

    [DllImport("libc", EntryPoint = "fstatat", SetLastError = true)]
    private static extern int FStatAt(
        int directoryFileDescriptor,
        string path,
        out LinuxStat information,
        int flags);

    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
    private static extern int UnlinkAt(int directoryFileDescriptor, string path, int flags);
}

internal sealed record CandidateFileRead(
    byte[] Bytes,
    DocumentationPatchCandidateFileSystem.CandidatePhysicalIdentity Identity);
