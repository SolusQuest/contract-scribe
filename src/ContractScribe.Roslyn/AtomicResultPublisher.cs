using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace ContractScribe.Roslyn;

internal sealed class AtomicResultPublisher : IDisposable
{
    private const string StagingFileName = ".audit-result.json.contractscribe-stage";
    private readonly ResolvedPublicationTarget target;
    private readonly StablePublicationDirectory parent;
    private readonly ProductionAuditHostControls controls;
    private StablePublicationDirectory.StableFileIdentity? stagedIdentity;
    private string? stagedSha256;
    private bool disposed;

    private AtomicResultPublisher(
        ResolvedPublicationTarget target,
        StablePublicationDirectory parent,
        ProductionAuditHostControls controls)
    {
        this.target = target;
        this.parent = parent;
        this.controls = controls;
    }

    public string StagingPath => Path.Join(target.ParentPath, StagingFileName);

    public static AtomicResultPublisher Prepare(
        ResolvedPublicationTarget target,
        ProductionAuditHostControls controls)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(controls);
        StablePublicationDirectory? parent = null;
        try
        {
            parent = StablePublicationDirectory.Open(target.ParentPath);
            var publisher = new AtomicResultPublisher(target, parent, controls);
            publisher.DeleteSafeEntry(Path.GetFileName(target.FinalPath), "invalidate-existing");
            publisher.DeleteSafeEntry(StagingFileName, "cleanup-staging");
            publisher.parent.RebindPath();
            return publisher;
        }
        catch
        {
            parent?.Dispose();
            throw;
        }
    }

    public void Stage(ReadOnlySpan<byte> bytes)
    {
        ThrowIfDisposed();
        parent.RebindPath();
        if (parent.EntryExists(StagingFileName))
        {
            throw FinalizationFailure();
        }

        using (var stream = parent.CreateNewRegularFile(StagingFileName))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            stagedIdentity = parent.ReadRegularFileIdentity(stream.SafeFileHandle);
        }

        using var readback = parent.OpenRegularFile(StagingFileName, FileAccess.Read);
        var currentIdentity = parent.ReadRegularFileIdentity(readback.SafeFileHandle);
        if (currentIdentity != stagedIdentity)
        {
            throw FinalizationFailure();
        }
        var actual = SHA256.HashData(readback);
        var expected = SHA256.HashData(bytes);
        if (!actual.AsSpan().SequenceEqual(expected))
        {
            throw FinalizationFailure();
        }
        stagedSha256 = Convert.ToHexString(expected).ToLowerInvariant();
    }

    public string CommitRename()
    {
        ThrowIfDisposed();
        parent.RebindPath();
        if (parent.EntryExists(Path.GetFileName(target.FinalPath)))
        {
            throw FinalizationFailure();
        }
        if (controls.Fault is
            ProductionHostFault.PublicationFinalization or
            ProductionHostFault.PublicationCleanup)
        {
            throw FinalizationFailure();
        }

        var expectedIdentity = stagedIdentity ?? throw FinalizationFailure();
        var expectedSha256 = stagedSha256 ?? throw FinalizationFailure();
        using var staging = parent.OpenRegularFile(
            StagingFileName,
            FileAccess.ReadWrite,
            includeDeleteAccess: true);
        if (parent.ReadRegularFileIdentity(staging.SafeFileHandle) != expectedIdentity)
        {
            throw FinalizationFailure();
        }
        var currentSha256 = Convert.ToHexString(SHA256.HashData(staging)).ToLowerInvariant();
        if (!string.Equals(currentSha256, expectedSha256, StringComparison.Ordinal))
        {
            throw FinalizationFailure();
        }

        parent.RebindPath();
        parent.RebindEntry(StagingFileName, expectedIdentity);
        parent.Rename(staging.SafeFileHandle, StagingFileName, Path.GetFileName(target.FinalPath));
        parent.RebindPath();
        parent.RebindEntry(Path.GetFileName(target.FinalPath), expectedIdentity);
        return expectedSha256;
    }

    public bool TryCleanupStaging()
    {
        if (disposed || controls.Fault == ProductionHostFault.PublicationCleanup)
        {
            return false;
        }
        try
        {
            DeleteSafeEntry(StagingFileName, "cleanup-staging");
            return !parent.EntryExists(StagingFileName);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PublicationException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        parent.Dispose();
    }

    private void DeleteSafeEntry(string name, string operation)
    {
        parent.RebindPath();
        if (!parent.EntryExists(name))
        {
            return;
        }
        if (operation == "invalidate-existing"
            && controls.Fault == ProductionHostFault.PublicationInvalidation)
        {
            throw new PublicationException("host.publication.invalidation-failed");
        }
        using (var stream = parent.OpenRegularFile(
                   name,
                   FileAccess.ReadWrite,
                   includeDeleteAccess: true))
        {
            var identity = parent.ReadRegularFileIdentity(stream.SafeFileHandle);
            parent.RebindEntry(name, identity);
            parent.Delete(stream.SafeFileHandle, name);
        }
        parent.RebindPath();
        if (parent.EntryExists(name))
        {
            throw FinalizationFailure();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private static PublicationException FinalizationFailure() =>
        new("host.publication.finalization-failed");
}

internal sealed class StablePublicationDirectory : IDisposable
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
    private const int UnixOpenReadOnly = 0;
    private const int UnixOpenWriteOnly = 0x00000001;
    private const int UnixOpenReadWrite = 0x00000002;
    private const int UnixOpenCreate = 0x00000040;
    private const int UnixOpenExclusive = 0x00000080;
    private const int UnixOpenCloseOnExec = 0x00080000;
    private const int UnixOpenNoFollow = 0x00020000;
    private const int UnixOpenDirectory = 0x00010000;
    private const int UnixOpenSync = 0x00101000;
    private const int UnixAtSymlinkNoFollow = 0x100;
    private const uint UnixFileTypeMask = 0xF000;
    private const uint UnixRegularFile = 0x8000;
    private const uint UnixDirectory = 0x4000;
    private const int FileDispositionInfo = 4;
    private const int FileRenameInformation = 10;

    private readonly string path;
    private readonly SafeFileHandle handle;
    private readonly StableNodeIdentity identity;

    private StablePublicationDirectory(
        string path,
        SafeFileHandle handle,
        StableNodeIdentity identity)
    {
        this.path = path;
        this.handle = handle;
        this.identity = identity;
    }

    public static StablePublicationDirectory Open(string path)
    {
        var fullPath = Path.GetFullPath(path);
        EnsureNoReparseComponents(fullPath);
        var handle = OpenDirectoryNoFollow(fullPath);
        try
        {
            return new StablePublicationDirectory(
                fullPath,
                handle,
                ReadDirectoryIdentity(handle));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public bool EntryExists(string name)
    {
        RequireLeafName(name);
        if (OperatingSystem.IsLinux())
        {
            return FStatAt(DirectoryDescriptor, name, out _, UnixAtSymlinkNoFollow) == 0;
        }
        return File.Exists(Path.Join(path, name)) || Directory.Exists(Path.Join(path, name));
    }

    public FileStream CreateNewRegularFile(string name)
    {
        RequireLeafName(name);
        SafeFileHandle fileHandle;
        if (OperatingSystem.IsWindows())
        {
            fileHandle = CreateFileW(
                Path.Join(path, name),
                GenericRead | GenericWrite | DeleteAccess,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                CreateNew,
                FileFlagWriteThrough | FileFlagOpenReparsePoint,
                IntPtr.Zero);
        }
        else if (OperatingSystem.IsLinux())
        {
            var descriptor = OpenAt(
                DirectoryDescriptor,
                name,
                UnixOpenReadWrite | UnixOpenCreate | UnixOpenExclusive
                    | UnixOpenCloseOnExec | UnixOpenNoFollow | UnixOpenSync,
                Convert.ToUInt32("600", 8));
            fileHandle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        }
        else
        {
            throw FinalizationFailure();
        }
        if (fileHandle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            fileHandle.Dispose();
            throw FinalizationFailure(new Win32Exception(error));
        }
        try
        {
            _ = ReadRegularFileIdentity(fileHandle);
            return new FileStream(fileHandle, FileAccess.ReadWrite, 64 * 1024, isAsync: false);
        }
        catch
        {
            fileHandle.Dispose();
            throw;
        }
    }

    public FileStream OpenRegularFile(
        string name,
        FileAccess access,
        bool includeDeleteAccess = false)
    {
        RequireLeafName(name);
        SafeFileHandle fileHandle;
        if (OperatingSystem.IsWindows())
        {
            var desiredAccess = access switch
            {
                FileAccess.Read => GenericRead,
                FileAccess.Write => GenericWrite,
                FileAccess.ReadWrite => GenericRead | GenericWrite,
                _ => throw new ArgumentOutOfRangeException(nameof(access)),
            };
            if (includeDeleteAccess)
            {
                desiredAccess |= DeleteAccess;
            }
            fileHandle = CreateFileW(
                Path.Join(path, name),
                desiredAccess,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOpenReparsePoint,
                IntPtr.Zero);
        }
        else if (OperatingSystem.IsLinux())
        {
            var flags = access == FileAccess.Read
                ? UnixOpenReadOnly
                : access == FileAccess.Write
                    ? UnixOpenWriteOnly
                    : UnixOpenReadWrite;
            var descriptor = OpenAt(
                DirectoryDescriptor,
                name,
                flags | UnixOpenCloseOnExec | UnixOpenNoFollow,
                0);
            fileHandle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        }
        else
        {
            throw FinalizationFailure();
        }
        if (fileHandle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            fileHandle.Dispose();
            throw FinalizationFailure(new Win32Exception(error));
        }
        try
        {
            _ = ReadRegularFileIdentity(fileHandle);
            return new FileStream(fileHandle, access, 64 * 1024, isAsync: false);
        }
        catch
        {
            fileHandle.Dispose();
            throw;
        }
    }

    public StableFileIdentity ReadRegularFileIdentity(SafeFileHandle fileHandle)
    {
        if (OperatingSystem.IsWindows())
        {
            if (GetFileType(fileHandle) != FileTypeDisk
                || !GetFileInformationByHandle(fileHandle, out var information)
                || (information.Attributes
                    & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
                || information.NumberOfLinks != 1)
            {
                throw FinalizationFailure();
            }
            return new StableFileIdentity(
                information.VolumeSerialNumber,
                ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow,
                ((long)information.FileSizeHigh << 32) | information.FileSizeLow,
                information.NumberOfLinks);
        }
        if (OperatingSystem.IsLinux())
        {
            if (FStat(fileHandle, out var information) != 0
                || (information.Mode & UnixFileTypeMask) != UnixRegularFile
                || information.LinkCount != 1)
            {
                throw FinalizationFailure();
            }
            return new StableFileIdentity(
                information.Device,
                information.Inode,
                information.Size,
                information.LinkCount);
        }
        throw FinalizationFailure();
    }

    public void RebindPath()
    {
        EnsureNoReparseComponents(path);
        using var current = OpenDirectoryNoFollow(path);
        if (ReadDirectoryIdentity(current) != identity)
        {
            throw FinalizationFailure();
        }
    }

    public void RebindEntry(string name, StableFileIdentity expected)
    {
        using var current = OpenRegularFile(name, FileAccess.Read);
        if (ReadRegularFileIdentity(current.SafeFileHandle) != expected)
        {
            throw FinalizationFailure();
        }
    }

    public void Delete(SafeFileHandle fileHandle, string name)
    {
        if (OperatingSystem.IsWindows())
        {
            var disposition = new FileDispositionInformation { DeleteFile = true };
            if (!SetFileInformationByHandle(
                    fileHandle,
                    FileDispositionInfo,
                    ref disposition,
                    Marshal.SizeOf<FileDispositionInformation>()))
            {
                throw FinalizationFailure(new Win32Exception(Marshal.GetLastWin32Error()));
            }
            return;
        }
        if (OperatingSystem.IsLinux()
            && UnlinkAt(DirectoryDescriptor, name, 0) == 0)
        {
            return;
        }
        throw FinalizationFailure(new Win32Exception(Marshal.GetLastWin32Error()));
    }

    public void Rename(SafeFileHandle fileHandle, string sourceName, string destinationName)
    {
        RequireLeafName(sourceName);
        RequireLeafName(destinationName);
        if (OperatingSystem.IsWindows())
        {
            RenameWindows(fileHandle, handle, destinationName);
            return;
        }
        if (OperatingSystem.IsLinux()
            && RenameAt(DirectoryDescriptor, sourceName, DirectoryDescriptor, destinationName) == 0)
        {
            return;
        }
        throw FinalizationFailure(new Win32Exception(Marshal.GetLastWin32Error()));
    }

    public void Dispose() => handle.Dispose();

    private int DirectoryDescriptor => handle.DangerousGetHandle().ToInt32();

    private static void RenameWindows(
        SafeFileHandle fileHandle,
        SafeFileHandle directoryHandle,
        string destinationName)
    {
        var nameBytes = checked(destinationName.Length * sizeof(char));
        var fileNameOffset = IntPtr.Size == 8 ? 20 : 12;
        var bufferSize = checked(fileNameOffset + nameBytes);
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            for (var index = 0; index < bufferSize; index++)
            {
                Marshal.WriteByte(buffer, index, 0);
            }
            Marshal.WriteIntPtr(
                buffer,
                IntPtr.Size == 8 ? 8 : 4,
                directoryHandle.DangerousGetHandle());
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 16 : 8, nameBytes);
            Marshal.Copy(destinationName.ToCharArray(), 0, buffer + fileNameOffset, destinationName.Length);
            var status = NtSetInformationFile(
                fileHandle,
                out _,
                buffer,
                checked((uint)bufferSize),
                FileRenameInformation);
            if (status < 0)
            {
                throw FinalizationFailure(new Win32Exception(
                    checked((int)RtlNtStatusToDosError(status))));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static SafeFileHandle OpenDirectoryNoFollow(string path)
    {
        SafeFileHandle directory;
        if (OperatingSystem.IsWindows())
        {
            directory = CreateFileW(
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
            var descriptor = Open(
                path,
                UnixOpenReadOnly | UnixOpenCloseOnExec | UnixOpenNoFollow | UnixOpenDirectory);
            directory = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        }
        else
        {
            throw FinalizationFailure();
        }
        if (directory.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            directory.Dispose();
            throw FinalizationFailure(new Win32Exception(error));
        }
        return directory;
    }

    private static StableNodeIdentity ReadDirectoryIdentity(SafeFileHandle directory)
    {
        if (OperatingSystem.IsWindows())
        {
            if (GetFileType(directory) != FileTypeDisk
                || !GetFileInformationByHandle(directory, out var information)
                || (information.Attributes & FileAttributes.Directory) == 0
                || (information.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw FinalizationFailure();
            }
            return new StableNodeIdentity(
                information.VolumeSerialNumber,
                ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
        }
        if (OperatingSystem.IsLinux())
        {
            if (FStat(directory, out var information) != 0
                || (information.Mode & UnixFileTypeMask) != UnixDirectory)
            {
                throw FinalizationFailure();
            }
            return new StableNodeIdentity(information.Device, information.Inode);
        }
        throw FinalizationFailure();
    }

    private static void EnsureNoReparseComponents(string target)
    {
        var fullPath = Path.GetFullPath(target);
        var root = Path.GetPathRoot(fullPath)
            ?? throw FinalizationFailure();
        var current = root;
        foreach (var segment in fullPath[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Join(current, segment);
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw FinalizationFailure();
            }
        }
    }

    private static void RequireLeafName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name is "." or ".."
            || name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw FinalizationFailure();
        }
    }

    private static PublicationException FinalizationFailure(Exception? inner = null) =>
        new("host.publication.finalization-failed", inner);

    internal readonly record struct StableFileIdentity(
        ulong Volume,
        ulong FileId,
        long Length,
        ulong LinkCount);

    private readonly record struct StableNodeIdentity(ulong Volume, ulong FileId);

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
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public nuint Information;
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        ref FileDispositionInformation fileInformation,
        int bufferSize);

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationFile(
        SafeFileHandle file,
        out IoStatusBlock ioStatusBlock,
        IntPtr fileInformation,
        uint length,
        int fileInformationClass);

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAt(int directoryFileDescriptor, string path, int flags, uint mode);

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

    [DllImport("libc", EntryPoint = "renameat", SetLastError = true)]
    private static extern int RenameAt(
        int oldDirectoryFileDescriptor,
        string oldPath,
        int newDirectoryFileDescriptor,
        string newPath);
}

internal sealed class PublicationException : IOException
{
    public PublicationException(string failureCode, Exception? innerException = null)
        : base("The canonical result publication operation failed.", innerException)
    {
        FailureCode = failureCode;
    }

    public string FailureCode { get; }
}
