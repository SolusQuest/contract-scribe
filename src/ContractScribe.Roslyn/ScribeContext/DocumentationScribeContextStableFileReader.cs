using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ContractScribe.Roslyn;

internal sealed record DocumentationScribeContextPhysicalIdentity(
    ulong Volume,
    ulong FileId,
    long Length,
    ulong LinkCount,
    bool IsDirectory,
    long ModificationTimeSeconds,
    ulong ModificationTimeNanoseconds,
    long ChangeTimeSeconds,
    ulong ChangeTimeNanoseconds);

internal sealed record DocumentationScribeContextStableRead(
    byte[] Bytes,
    DocumentationScribeContextPhysicalIdentity Identity);

internal enum DocumentationScribeContextReadFailure
{
    Unsafe,
    Stale,
    Budget,
}

internal sealed class DocumentationScribeContextReadException : Exception
{
    internal DocumentationScribeContextReadException(
        DocumentationScribeContextReadFailure failure,
        string code)
        : base(code)
    {
        Failure = failure;
        Code = code;
    }

    internal DocumentationScribeContextReadFailure Failure { get; }

    internal string Code { get; }
}

internal static class DocumentationScribeContextStableFileReader
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileTypeDisk = 0x0001;
    private const uint SynchronizeAccess = 0x00100000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileListDirectory = 0x00000001;
    private const uint ObjectCaseInsensitive = 0x00000040;
    private const uint NtFileOpen = 1;
    private const uint NtFileDirectoryFile = 0x00000001;
    private const uint NtFileSynchronousIoNonAlert = 0x00000020;
    private const uint NtFileNonDirectoryFile = 0x00000040;
    private const uint NtFileOpenReparsePoint = 0x00200000;
    private const uint NtFileOpenForBackupIntent = 0x00004000;
    private const int UnixOpenReadOnly = 0;
    private const int UnixOpenCloseOnExec = 0x80000;
    private const int UnixOpenNoFollow = 0x20000;
    private const int UnixOpenDirectory = 0x10000;
    private const uint UnixFileTypeMask = 0xF000;
    private const uint UnixRegularFile = 0x8000;
    private const uint UnixDirectory = 0x4000;

    internal static bool RegularFileExistsNoFollow(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw Unsafe();
            }

            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (DocumentationScribeContextReadException)
        {
            throw;
        }
        catch
        {
            throw Unsafe();
        }
    }

    internal static DocumentationScribeContextStableRead ReadRegularFile(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken,
        Action? checkpoint = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        checkpoint?.Invoke();
        using var stream = OpenRegularFile(path);
        var before = ReadIdentity(stream.SafeFileHandle, expectDirectory: false);
        if (before.Length < 0 || before.Length > maximumBytes || before.Length > int.MaxValue)
        {
            throw new DocumentationScribeContextReadException(
                DocumentationScribeContextReadFailure.Budget,
                "context.budget.file-bytes");
        }

        var bytes = ReadAll(stream, maximumBytes, cancellationToken, checkpoint);

        var after = ReadIdentity(stream.SafeFileHandle, expectDirectory: false);
        if (before != after || before.Length != bytes.LongLength)
        {
            throw Stale();
        }

        stream.Dispose();
        using var rebound = OpenRegularFile(path);
        var reboundBefore = ReadIdentity(rebound.SafeFileHandle, expectDirectory: false);
        var reboundBytes = ReadAll(rebound, maximumBytes, cancellationToken, checkpoint);
        var reboundAfter = ReadIdentity(rebound.SafeFileHandle, expectDirectory: false);
        if (reboundBefore != before
            || reboundAfter != before
            || !bytes.AsSpan().SequenceEqual(reboundBytes))
        {
            throw Stale();
        }

        return new DocumentationScribeContextStableRead(bytes, before);
    }

    internal static DocumentationScribeContextStableRead ReadRegularFileAnchored(
        string repositoryRoot,
        string path,
        int maximumBytes,
        CancellationToken cancellationToken,
        Action? checkpoint = null,
        Action? afterParentOpen = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (parent, leafName) = OpenAnchoredParent(repositoryRoot, path);
        using (parent)
        {
            afterParentOpen?.Invoke();
            checkpoint?.Invoke();
            using var stream = OpenRegularFile(parent, leafName);
            var before = ReadIdentity(stream.SafeFileHandle, expectDirectory: false);
            if (before.Length < 0 || before.Length > maximumBytes || before.Length > int.MaxValue)
            {
                throw new DocumentationScribeContextReadException(
                    DocumentationScribeContextReadFailure.Budget,
                    "context.budget.file-bytes");
            }

            var bytes = ReadAll(stream, maximumBytes, cancellationToken, checkpoint);
            var after = ReadIdentity(stream.SafeFileHandle, expectDirectory: false);
            if (before != after || before.Length != bytes.LongLength)
            {
                throw Stale();
            }

            stream.Dispose();
            using var rebound = OpenRegularFile(parent, leafName);
            var reboundBefore = ReadIdentity(rebound.SafeFileHandle, expectDirectory: false);
            var reboundBytes = ReadAll(rebound, maximumBytes, cancellationToken, checkpoint);
            var reboundAfter = ReadIdentity(rebound.SafeFileHandle, expectDirectory: false);
            if (reboundBefore != before
                || reboundAfter != before
                || !bytes.AsSpan().SequenceEqual(reboundBytes))
            {
                throw Stale();
            }

            return new DocumentationScribeContextStableRead(bytes, before);
        }
    }

    private static byte[] ReadAll(
        FileStream stream,
        int maximumBytes,
        CancellationToken cancellationToken,
        Action? checkpoint)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            checkpoint?.Invoke();
            var read = stream.Read(chunk, 0, chunk.Length);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > maximumBytes)
            {
                throw new DocumentationScribeContextReadException(
                    DocumentationScribeContextReadFailure.Budget,
                    "context.budget.file-bytes");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    internal static DocumentationScribeContextPhysicalIdentity ReadDirectoryIdentity(string path)
    {
        try
        {
            using var handle = OpenDirectory(path);
            return ReadIdentity(handle, expectDirectory: true);
        }
        catch (DocumentationScribeContextReadException)
        {
            throw;
        }
        catch
        {
            throw Unsafe();
        }
    }

    internal static DocumentationScribeContextPhysicalIdentity ReadRegularFileIdentity(string path)
    {
        try
        {
            using var stream = OpenRegularFile(path);
            return ReadIdentity(stream.SafeFileHandle, expectDirectory: false);
        }
        catch (DocumentationScribeContextReadException)
        {
            throw;
        }
        catch
        {
            throw Stale();
        }
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
            throw Unsafe();
        }

        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw Stale();
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

    private static (SafeFileHandle Parent, string LeafName) OpenAnchoredParent(
        string repositoryRoot,
        string path)
    {
        var fullRoot = Path.GetFullPath(repositoryRoot);
        var fullPath = Path.GetFullPath(path);
        var repositoryRelative = Path.GetRelativePath(fullRoot, fullPath);
        if (Path.IsPathRooted(repositoryRelative)
            || repositoryRelative == ".."
            || repositoryRelative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || repositoryRelative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw Unsafe();
        }

        var parentPath = Path.GetDirectoryName(fullPath) ?? throw Unsafe();
        var leafName = Path.GetFileName(fullPath);
        if (leafName.Length == 0
            || leafName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw Unsafe();
        }

        var systemRoot = Path.GetPathRoot(parentPath) ?? throw Unsafe();
        var current = OpenDirectory(systemRoot);
        try
        {
            foreach (var segment in parentPath[systemRoot.Length..].Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                var next = OpenDirectoryRelative(current, segment);
                current.Dispose();
                current = next;
            }

            return (current, leafName);
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenDirectoryRelative(SafeFileHandle parent, string name)
    {
        var handle = OperatingSystem.IsWindows()
            ? NtOpenRelative(
                parent,
                name,
                FileListDirectory | FileReadAttributes | SynchronizeAccess,
                NtFileDirectoryFile | NtFileSynchronousIoNonAlert
                    | NtFileOpenReparsePoint | NtFileOpenForBackupIntent)
            : OperatingSystem.IsLinux()
                ? new SafeFileHandle(
                    (IntPtr)OpenAt(
                        checked((int)parent.DangerousGetHandle()),
                        name,
                        UnixOpenReadOnly | UnixOpenCloseOnExec | UnixOpenNoFollow | UnixOpenDirectory),
                    ownsHandle: true)
                : throw Unsafe();
        return ValidateOpenedHandle(handle, expectDirectory: true);
    }

    private static FileStream OpenRegularFile(SafeFileHandle parent, string name)
    {
        var handle = OperatingSystem.IsWindows()
            ? NtOpenRelative(
                parent,
                name,
                GenericRead | SynchronizeAccess,
                NtFileNonDirectoryFile | NtFileSynchronousIoNonAlert | NtFileOpenReparsePoint)
            : OperatingSystem.IsLinux()
                ? new SafeFileHandle(
                    (IntPtr)OpenAt(
                        checked((int)parent.DangerousGetHandle()),
                        name,
                        UnixOpenReadOnly | UnixOpenCloseOnExec | UnixOpenNoFollow),
                    ownsHandle: true)
                : throw Unsafe();
        ValidateOpenedHandle(handle, expectDirectory: false);
        try
        {
            return new FileStream(handle, FileAccess.Read, 64 * 1024, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle ValidateOpenedHandle(SafeFileHandle handle, bool expectDirectory)
    {
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw Stale();
        }

        try
        {
            _ = ReadIdentity(handle, expectDirectory);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle NtOpenRelative(
        SafeFileHandle parent,
        string name,
        uint desiredAccess,
        uint createOptions)
    {
        var nameBuffer = Marshal.StringToHGlobalUni(name);
        var unicodeStringBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        try
        {
            var nameBytes = checked((ushort)(name.Length * sizeof(char)));
            var unicodeString = new UnicodeString
            {
                Length = nameBytes,
                MaximumLength = checked((ushort)(nameBytes + sizeof(char))),
                Buffer = nameBuffer,
            };
            Marshal.StructureToPtr(unicodeString, unicodeStringBuffer, fDeleteOld: false);
            var attributes = new ObjectAttributes
            {
                Length = checked((uint)Marshal.SizeOf<ObjectAttributes>()),
                RootDirectory = parent.DangerousGetHandle(),
                ObjectName = unicodeStringBuffer,
                Attributes = ObjectCaseInsensitive,
            };
            var status = NtCreateFile(
                out var handle,
                desiredAccess,
                ref attributes,
                out _,
                IntPtr.Zero,
                0,
                FileShareRead | FileShareWrite | FileShareDelete,
                NtFileOpen,
                createOptions,
                IntPtr.Zero,
                0);
            if (status < 0)
            {
                handle?.Dispose();
                throw Stale();
            }

            return handle;
        }
        finally
        {
            Marshal.FreeHGlobal(unicodeStringBuffer);
            Marshal.FreeHGlobal(nameBuffer);
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
            throw Unsafe();
        }

        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw Stale();
        }

        return handle;
    }

    private static DocumentationScribeContextPhysicalIdentity ReadIdentity(
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
                throw Unsafe();
            }

            return new DocumentationScribeContextPhysicalIdentity(
                information.VolumeSerialNumber,
                ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow,
                expectDirectory
                    ? 0
                    : ((long)information.FileSizeHigh << 32) | information.FileSizeLow,
                expectDirectory ? 0 : information.NumberOfLinks,
                expectDirectory,
                expectDirectory ? 0 : WindowsFileTime(information.LastWriteTime),
                0,
                0,
                0);
        }

        if (OperatingSystem.IsLinux())
        {
            if (FStat(handle, out var information) != 0
                || (information.Mode & UnixFileTypeMask) != (expectDirectory
                    ? UnixDirectory
                    : UnixRegularFile))
            {
                throw Unsafe();
            }

            return new DocumentationScribeContextPhysicalIdentity(
                information.Device,
                information.Inode,
                expectDirectory ? 0 : information.Size,
                expectDirectory ? 0 : information.LinkCount,
                expectDirectory,
                expectDirectory ? 0 : information.ModificationTimeSeconds,
                expectDirectory ? 0 : information.ModificationTimeNanoseconds,
                expectDirectory ? 0 : information.ChangeTimeSeconds,
                expectDirectory ? 0 : information.ChangeTimeNanoseconds);
        }

        throw Unsafe();
    }

    private static long WindowsFileTime(
        System.Runtime.InteropServices.ComTypes.FILETIME value) =>
        unchecked(((long)(uint)value.dwHighDateTime << 32) | (uint)value.dwLowDateTime);

    private static void EnsureNoReparseComponents(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath) ?? throw Unsafe();
        var current = root;
        foreach (var segment in fullPath[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Join(current, segment);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch
            {
                throw Stale();
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw Unsafe();
            }
        }
    }

    private static DocumentationScribeContextReadException Unsafe() =>
        new(
            DocumentationScribeContextReadFailure.Unsafe,
            "context.unsafe.repository-object");

    private static DocumentationScribeContextReadException Stale() =>
        new(
            DocumentationScribeContextReadFailure.Stale,
            "context.stale.repository-object");

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
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public uint Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public IntPtr Information;
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
        IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out WindowsFileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle file);

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(
        out SafeFileHandle handle,
        uint desiredAccess,
        ref ObjectAttributes objectAttributes,
        out IoStatusBlock ioStatusBlock,
        IntPtr allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        IntPtr eaBuffer,
        uint eaLength);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAt(int directoryFileDescriptor, string path, int flags);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int FStat(SafeFileHandle file, out LinuxStat information);
}
