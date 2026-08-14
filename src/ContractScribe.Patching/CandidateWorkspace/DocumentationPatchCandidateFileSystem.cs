using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ContractScribe.Patching.CandidateWorkspace;

internal static class DocumentationPatchCandidateFileSystem
{
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

    public static CandidatePhysicalIdentity ReadOwnedDirectoryIdentity(
        string parentPath,
        CandidatePhysicalIdentity expectedParent,
        string leafName,
        CandidatePhysicalIdentity expectedDirectory)
    {
        RequireLeafName(leafName);
        using var parent = OpenDirectory(parentPath);
        if (ReadIdentity(parent, expectDirectory: true) != expectedParent)
        {
            throw new IOException("The candidate parent directory changed.");
        }

        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException();
        }

        using var directory = new SafeFileHandle(
            (IntPtr)OpenAt(
                checked((int)parent.DangerousGetHandle()),
                leafName,
                UnixOpenReadOnly | UnixOpenCloseOnExec | UnixOpenNoFollow | UnixOpenDirectory,
                0),
            ownsHandle: true);
        RequireValid(directory);
        var actual = ReadIdentity(directory, expectDirectory: true);
        if (actual != expectedDirectory)
        {
            throw new IOException("The candidate directory identity changed.");
        }

        return actual;
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

        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException();
        }

        var created = MkdirAt(
            checked((int)parent.DangerousGetHandle()),
            leafName,
            Convert.ToUInt32("700", 8)) == 0;
        if (!created)
        {
            throw new IOException(
                "A fresh candidate directory could not be created.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        using var directory = new SafeFileHandle(
            (IntPtr)OpenAt(
                checked((int)parent.DangerousGetHandle()),
                leafName,
                UnixOpenReadOnly | UnixOpenCloseOnExec | UnixOpenNoFollow | UnixOpenDirectory,
                0),
            ownsHandle: true);
        RequireValid(directory);
        return ReadIdentity(directory, expectDirectory: true);
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

        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException();
        }

        var handle = new SafeFileHandle(
            (IntPtr)OpenAt(
                checked((int)parent.DangerousGetHandle()),
                leafName,
                UnixOpenReadWrite | UnixOpenCreate | UnixOpenExclusive
                    | UnixOpenCloseOnExec | UnixOpenNoFollow | UnixOpenSync,
                Convert.ToUInt32("600", 8)),
            ownsHandle: true);

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
            expectedFile);
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

            if (!OperatingSystem.IsLinux())
            {
                return false;
            }

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
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
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
        CandidatePhysicalIdentity expectedFile)
    {
        RequireLeafName(leafName);
        using var parent = OpenDirectory(parentPath);
        if (ReadIdentity(parent, expectDirectory: true) != expectedParent)
        {
            throw new IOException("The candidate parent directory changed.");
        }

        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException();
        }

        var handle = new SafeFileHandle(
            (IntPtr)OpenAt(
                checked((int)parent.DangerousGetHandle()),
                leafName,
                UnixOpenReadOnly | UnixOpenCloseOnExec | UnixOpenNoFollow,
                0),
            ownsHandle: true);

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
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException();
        }

        var handle = new SafeFileHandle(
            (IntPtr)Open(
                path,
                UnixOpenReadOnly | UnixOpenCloseOnExec | UnixOpenNoFollow | UnixOpenDirectory),
            ownsHandle: true);

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
            expectDirectory ? 0 : information.LinkCount,
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
