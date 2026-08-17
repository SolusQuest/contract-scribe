using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ContractScribe.Roslyn;

internal sealed record DocumentationScribeRepositoryDirectoryEntry(
    string Name,
    DocumentationScribeContextPhysicalIdentity Identity);

internal sealed record DocumentationScribeRepositoryDirectoryObservation(
    string RepositoryPath,
    ImmutableArray<DocumentationScribeContextPhysicalIdentity> DirectoryIdentities,
    ImmutableArray<DocumentationScribeRepositoryDirectoryEntry> Entries);

internal static class DocumentationScribeRepositoryDirectoryReader
{
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
    private const uint NtFileOpen = 1;
    private const uint NtFileDirectoryFile = 0x00000001;
    private const uint NtFileSynchronousIoNonAlert = 0x00000020;
    private const uint NtFileOpenReparsePoint = 0x00200000;
    private const uint NtFileOpenForBackupIntent = 0x00004000;
    private const int WindowsFileIdBothDirectoryInfo = 10;
    private const int WindowsFileIdBothDirectoryRestartInfo = 11;
    private const int WindowsErrorNoMoreFiles = 18;
    private const int LinuxEntryNameOffset = 19;
    private const int UnixOpenReadOnly = 0;
    private const int UnixOpenCloseOnExec = 0x80000;
    private const int UnixOpenNoFollow = 0x20000;
    private const int UnixOpenDirectory = 0x10000;
    private const int UnixOpenPath = 0x200000;
    private const uint UnixFileTypeMask = 0xF000;
    private const uint UnixRegularFile = 0x8000;
    private const uint UnixDirectory = 0x4000;

    internal static DocumentationScribeRepositoryDirectoryObservation Capture(
        string repositoryRoot,
        DocumentationScribeContextPhysicalIdentity rootIdentity,
        string repositoryPath,
        CancellationToken cancellationToken,
        Action checkpoint,
        Action chargeEntry,
        Action<DocumentationScribeRepositoryToolCheckpoint>? observer = null)
    {
        var segments = Segments(repositoryPath);
        var before = ObserveChain(repositoryRoot, rootIdentity, segments, cancellationToken, checkpoint, chargeEntry);
        observer?.Invoke(DocumentationScribeRepositoryToolCheckpoint.AfterDirectoryPreObservation);
        checkpoint();

        ImmutableArray<DocumentationScribeRepositoryDirectoryEntry> entries;
        using (var directory = OpenBoundDirectory(
                   repositoryRoot,
                   rootIdentity,
                   repositoryPath,
                   segments,
                   before,
                   cancellationToken,
                   checkpoint,
                   chargeEntry,
                   observer))
        {
            entries = ReadEntries(directory, cancellationToken, checkpoint, chargeEntry);
            var repeated = ReadEntries(directory, cancellationToken, checkpoint, static () => { });
            if (!entries.SequenceEqual(repeated))
            {
                throw Stale();
            }
        }

        var after = ObserveChain(repositoryRoot, rootIdentity, segments, cancellationToken, checkpoint, chargeEntry);
        if (!before.SequenceEqual(after))
        {
            throw Stale();
        }

        return new(repositoryPath, before, entries);
    }

    internal static void Revalidate(
        string repositoryRoot,
        DocumentationScribeContextPhysicalIdentity rootIdentity,
        DocumentationScribeRepositoryDirectoryObservation observation,
        CancellationToken cancellationToken,
        Action checkpoint)
    {
        var current = Capture(
            repositoryRoot,
            rootIdentity,
            observation.RepositoryPath,
            cancellationToken,
            checkpoint,
            static () => { });
        if (!string.Equals(current.RepositoryPath, observation.RepositoryPath, StringComparison.Ordinal)
            || !current.DirectoryIdentities.SequenceEqual(observation.DirectoryIdentities)
            || !current.Entries.SequenceEqual(observation.Entries))
        {
            throw Stale();
        }
    }

    private static ImmutableArray<DocumentationScribeContextPhysicalIdentity> ObserveChain(
        string repositoryRoot,
        DocumentationScribeContextPhysicalIdentity rootIdentity,
        string[] segments,
        CancellationToken cancellationToken,
        Action checkpoint,
        Action? chargeEntry = null)
    {
        var identities = ImmutableArray.CreateBuilder<DocumentationScribeContextPhysicalIdentity>();
        var current = OpenRoot(repositoryRoot);
        try
        {
            var identity = ReadIdentity(current);
            if (!identity.IsDirectory || identity != rootIdentity)
            {
                throw Stale();
            }

            identities.Add(identity);
            foreach (var segment in segments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                checkpoint();
                RequireExactName(current, segment, cancellationToken, checkpoint, chargeEntry);
                var next = OpenRelative(current, segment, requireDirectory: true);
                current.Dispose();
                current = next;
                identities.Add(ReadIdentity(current));
            }

            return identities.ToImmutable();
        }
        finally
        {
            current.Dispose();
        }
    }

    private static SafeFileHandle OpenBoundDirectory(
        string repositoryRoot,
        DocumentationScribeContextPhysicalIdentity rootIdentity,
        string repositoryPath,
        string[] segments,
        ImmutableArray<DocumentationScribeContextPhysicalIdentity> expected,
        CancellationToken cancellationToken,
        Action checkpoint,
        Action? chargeEntry,
        Action<DocumentationScribeRepositoryToolCheckpoint>? observer)
    {
        if (expected.Length != segments.Length + 1)
        {
            throw new InvalidOperationException("repository.internal.directory-chain");
        }

        var current = OpenRoot(repositoryRoot);
        try
        {
            var identity = ReadIdentity(current);
            if (identity != rootIdentity || identity != expected[0])
            {
                throw Stale();
            }

            for (var index = 0; index < segments.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                checkpoint();
                RequireExactName(current, segments[index], cancellationToken, checkpoint, chargeEntry);
                var next = OpenRelative(current, segments[index], requireDirectory: true);
                observer?.Invoke(DocumentationScribeRepositoryToolCheckpoint.AfterBoundDirectoryOpen);
                checkpoint();
                RequireExactName(current, segments[index], cancellationToken, checkpoint, chargeEntry);
                var nextIdentity = ReadIdentity(next);
                if (nextIdentity != expected[index + 1])
                {
                    next.Dispose();
                    throw Stale();
                }

                current.Dispose();
                current = next;
            }

            if (segments.Length == 0)
            {
                observer?.Invoke(DocumentationScribeRepositoryToolCheckpoint.AfterBoundDirectoryOpen);
                checkpoint();
            }

            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static ImmutableArray<DocumentationScribeRepositoryDirectoryEntry> ReadEntries(
        SafeFileHandle directory,
        CancellationToken cancellationToken,
        Action checkpoint,
        Action chargeEntry)
    {
        var entries = ImmutableArray.CreateBuilder<DocumentationScribeRepositoryDirectoryEntry>();
        var spellings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in EnumerateNames(directory, cancellationToken, checkpoint))
        {
            chargeEntry();
            if (spellings.TryGetValue(name, out var existing)
                && !string.Equals(existing, name, StringComparison.Ordinal))
            {
                throw Unsafe();
            }

            spellings[name] = name;
            using var child = OpenRelative(directory, name, requireDirectory: null);
            RequireExactName(directory, name, cancellationToken, checkpoint);
            entries.Add(new(name, ReadIdentity(child)));
        }

        return entries.OrderBy(entry => entry.Name, StringComparer.Ordinal).ToImmutableArray();
    }

    private static void RequireExactName(
        SafeFileHandle directory,
        string expected,
        CancellationToken cancellationToken,
        Action checkpoint,
        Action? chargeEntry = null)
    {
        var exact = 0;
        var folded = 0;
        foreach (var name in EnumerateNames(directory, cancellationToken, checkpoint))
        {
            chargeEntry?.Invoke();
            if (!string.Equals(name, expected, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            folded++;
            if (string.Equals(name, expected, StringComparison.Ordinal))
            {
                exact++;
            }
        }

        if (folded != 1 || exact != 1)
        {
            throw folded == 0 ? Stale() : Unsafe();
        }
    }

    private static string[] Segments(string repositoryPath)
    {
        if (repositoryPath == ".")
        {
            return [];
        }

        if (string.IsNullOrWhiteSpace(repositoryPath)
            || Path.IsPathFullyQualified(repositoryPath)
            || repositoryPath.Contains('\\', StringComparison.Ordinal)
            || repositoryPath.StartsWith("/", StringComparison.Ordinal)
            || repositoryPath.EndsWith("/", StringComparison.Ordinal))
        {
            throw Unsafe();
        }

        var segments = repositoryPath.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw Unsafe();
        }

        return segments;
    }

    private static IEnumerable<string> EnumerateNames(
        SafeFileHandle directory,
        CancellationToken cancellationToken,
        Action checkpoint) => OperatingSystem.IsWindows()
            ? EnumerateWindowsNames(directory, cancellationToken, checkpoint)
            : OperatingSystem.IsLinux()
                ? EnumerateLinuxNames(directory, cancellationToken, checkpoint)
                : throw Unsafe();

    private static IEnumerable<string> EnumerateWindowsNames(
        SafeFileHandle directory,
        CancellationToken cancellationToken,
        Action checkpoint)
    {
        const int bufferSize = 64 * 1024;
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            var informationClass = WindowsFileIdBothDirectoryRestartInfo;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                checkpoint();
                if (!GetFileInformationByHandleEx(directory, informationClass, buffer, bufferSize))
                {
                    if (Marshal.GetLastPInvokeError() == WindowsErrorNoMoreFiles)
                    {
                        yield break;
                    }

                    throw new InvalidOperationException("repository.internal.native-enumeration");
                }

                informationClass = WindowsFileIdBothDirectoryInfo;
                var offset = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    checkpoint();
                    var entry = IntPtr.Add(buffer, offset);
                    var nextOffset = Marshal.ReadInt32(entry, 0);
                    var nameLength = Marshal.ReadInt32(entry, 60);
                    if (nameLength < 0 || (nameLength & 1) != 0 || nameLength > bufferSize - offset - 104)
                    {
                        throw new InvalidOperationException("repository.internal.native-enumeration");
                    }

                    var name = Marshal.PtrToStringUni(IntPtr.Add(entry, 104), nameLength / 2)
                        ?? throw new InvalidOperationException("repository.internal.native-enumeration");
                    if (name is not "." and not "..")
                    {
                        yield return name;
                    }

                    if (nextOffset == 0)
                    {
                        break;
                    }

                    if (nextOffset < 0 || nextOffset > bufferSize - offset)
                    {
                        throw new InvalidOperationException("repository.internal.native-enumeration");
                    }

                    offset += nextOffset;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IEnumerable<string> EnumerateLinuxNames(
        SafeFileHandle directory,
        CancellationToken cancellationToken,
        Action checkpoint)
    {
        var fd = OpenAt(directory.DangerousGetHandle().ToInt32(), ".", UnixOpenReadOnly | UnixOpenCloseOnExec | UnixOpenNoFollow | UnixOpenDirectory);
        if (fd < 0)
        {
            throw new InvalidOperationException("repository.internal.native-enumeration");
        }

        var stream = FdOpenDirectory(fd);
        if (stream == IntPtr.Zero)
        {
            _ = Close(fd);
            throw new InvalidOperationException("repository.internal.native-enumeration");
        }

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                checkpoint();
                Marshal.SetLastPInvokeError(0);
                var entry = ReadDirectoryEntry(stream);
                if (entry == IntPtr.Zero)
                {
                    if (Marshal.GetLastPInvokeError() != 0)
                    {
                        throw new InvalidOperationException("repository.internal.native-enumeration");
                    }

                    yield break;
                }

                var name = Marshal.PtrToStringUTF8(IntPtr.Add(entry, LinuxEntryNameOffset))
                    ?? throw new InvalidOperationException("repository.internal.native-enumeration");
                if (name is not "." and not "..")
                {
                    yield return name;
                }
            }
        }
        finally
        {
            _ = CloseDirectory(stream);
        }
    }

    private static SafeFileHandle OpenRoot(string path)
    {
        _ = DocumentationScribeContextStableFileReader.ReadDirectoryIdentity(path);
        SafeFileHandle handle;
        if (OperatingSystem.IsWindows())
        {
            handle = CreateFileW(path, FileListDirectory | FileReadAttributes, FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        }
        else if (OperatingSystem.IsLinux())
        {
            handle = new SafeFileHandle((IntPtr)Open(path, UnixOpenReadOnly | UnixOpenCloseOnExec | UnixOpenNoFollow | UnixOpenDirectory), ownsHandle: true);
        }
        else
        {
            throw Unsafe();
        }

        return ValidateOpen(handle);
    }

    private static SafeFileHandle OpenRelative(SafeFileHandle parent, string name, bool? requireDirectory)
    {
        SafeFileHandle handle;
        if (OperatingSystem.IsWindows())
        {
            var options = NtFileSynchronousIoNonAlert | NtFileOpenReparsePoint | NtFileOpenForBackupIntent;
            if (requireDirectory == true)
            {
                options |= NtFileDirectoryFile;
            }

            var access = FileReadAttributes | SynchronizeAccess;
            if (requireDirectory == true)
            {
                access |= FileListDirectory;
            }

            handle = NtOpenRelative(parent, name, access, options);
        }
        else if (OperatingSystem.IsLinux())
        {
            var flags = UnixOpenCloseOnExec | UnixOpenNoFollow | (requireDirectory == true ? UnixOpenReadOnly | UnixOpenDirectory : UnixOpenPath);
            handle = new SafeFileHandle((IntPtr)OpenAt(parent.DangerousGetHandle().ToInt32(), name, flags), ownsHandle: true);
            ValidateUnixOpen(handle);
        }
        else
        {
            throw Unsafe();
        }

        handle = ValidateOpen(handle);
        var identity = ReadIdentity(handle);
        if (requireDirectory is not null && identity.IsDirectory != requireDirectory)
        {
            handle.Dispose();
            throw Unsafe();
        }

        return handle;
    }

    private static SafeFileHandle ValidateOpen(SafeFileHandle handle)
    {
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw OperatingSystem.IsWindows()
                ? error switch { 2 or 3 => Stale(), 267 => Unsafe(), _ => new InvalidOperationException("repository.internal.native-open") }
                : error switch { 2 => Stale(), 20 or 21 or 40 => Unsafe(), _ => new InvalidOperationException("repository.internal.native-open") };
        }

        return handle;
    }

    private static void ValidateUnixOpen(SafeFileHandle handle)
    {
        if (!handle.IsInvalid)
        {
            return;
        }

        var error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        throw error switch { 2 => Stale(), 20 or 21 or 40 => Unsafe(), _ => new InvalidOperationException("repository.internal.native-open") };
    }

    private static SafeFileHandle NtOpenRelative(SafeFileHandle parent, string name, uint desiredAccess, uint options)
    {
        var nameBuffer = Marshal.StringToHGlobalUni(name);
        var unicodeBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        try
        {
            var bytes = checked((ushort)(name.Length * sizeof(char)));
            Marshal.StructureToPtr(new UnicodeString { Length = bytes, MaximumLength = checked((ushort)(bytes + sizeof(char))), Buffer = nameBuffer }, unicodeBuffer, false);
            var attributes = new ObjectAttributes { Length = checked((uint)Marshal.SizeOf<ObjectAttributes>()), RootDirectory = parent.DangerousGetHandle(), ObjectName = unicodeBuffer };
            var status = NtCreateFile(out var handle, desiredAccess, ref attributes, out _, IntPtr.Zero, 0, FileShareRead | FileShareWrite | FileShareDelete, NtFileOpen, options, IntPtr.Zero, 0);
            if (status < 0)
            {
                handle?.Dispose();
                throw unchecked((uint)status) switch
                {
                    0xC000000F or 0xC0000034 or 0xC000003A => Stale(),
                    0xC00000BA or 0xC0000103 => Unsafe(),
                    _ => new InvalidOperationException("repository.internal.native-open"),
                };
            }

            return handle;
        }
        finally
        {
            Marshal.FreeHGlobal(unicodeBuffer);
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    private static DocumentationScribeContextPhysicalIdentity ReadIdentity(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows())
        {
            Marshal.SetLastPInvokeError(0);
            var type = GetFileType(handle);
            DocumentationScribeContextStableFileReader.ValidateWindowsFileType(type, Marshal.GetLastPInvokeError());
            if (!GetFileInformationByHandle(handle, out var info) || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw Unsafe();
            }

            var directory = (info.Attributes & FileAttributes.Directory) != 0;
            return new(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow, directory ? 0 : ((long)info.FileSizeHigh << 32) | info.FileSizeLow, directory ? 0 : info.NumberOfLinks, directory, directory ? 0 : WindowsFileTime(info.LastWriteTime), 0, 0, 0);
        }

        if (OperatingSystem.IsLinux())
        {
            if (FStat(handle, out var info) != 0)
            {
                throw new InvalidOperationException("repository.internal.native-identity");
            }

            var kind = info.Mode & UnixFileTypeMask;
            if (kind is not UnixRegularFile and not UnixDirectory)
            {
                throw Unsafe();
            }

            var directory = kind == UnixDirectory;
            return new(info.Device, info.Inode, directory ? 0 : info.Size, directory ? 0 : info.LinkCount, directory, directory ? 0 : info.ModificationTimeSeconds, directory ? 0 : info.ModificationTimeNanoseconds, directory ? 0 : info.ChangeTimeSeconds, directory ? 0 : info.ChangeTimeNanoseconds);
        }

        throw Unsafe();
    }

    private static long WindowsFileTime(System.Runtime.InteropServices.ComTypes.FILETIME value) =>
        unchecked(((long)(uint)value.dwHighDateTime << 32) | (uint)value.dwLowDateTime);

    private static DocumentationScribeContextReadException Unsafe() => new(DocumentationScribeContextReadFailure.Unsafe, "context.unsafe.repository-object");
    private static DocumentationScribeContextReadException Stale() => new(DocumentationScribeContextReadFailure.Stale, "context.stale.repository-object");

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
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out WindowsFileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle file);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, IntPtr information, int bufferSize);

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

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int fileDescriptor);

    [DllImport("libc", EntryPoint = "fdopendir", SetLastError = true)]
    private static extern IntPtr FdOpenDirectory(int fileDescriptor);

    [DllImport("libc", EntryPoint = "readdir", SetLastError = true)]
    private static extern IntPtr ReadDirectoryEntry(IntPtr directory);

    [DllImport("libc", EntryPoint = "closedir", SetLastError = true)]
    private static extern int CloseDirectory(IntPtr directory);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int FStat(SafeFileHandle file, out LinuxStat information);
}
