using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace ContractScribe.Agent.Providers;

[SupportedOSPlatform("linux")]
internal sealed class OpenAiCompatibleLinuxResponseCapture : IDisposable
{
    private const int OpenReadOnly = 0;
    private const int OpenWriteOnly = 1;
    private const int OpenCreate = 0x40;
    private const int OpenExclusive = 0x80;
    private const int OpenDirectory = 0x10000;
    private const int OpenNoFollow = 0x20000;
    private const int OpenCloseOnExec = 0x80000;
    private const int AtSymbolicLinkNoFollow = 0x100;
    private const uint DirectoryCreateMode = 0x1C0;
    private const uint FileCreateMode = 0x180;
    private const uint RenameNoReplace = 1;
    private const int AtRemoveDirectory = 0x200;
    private const int NoSuchFileOrDirectory = 2;
    private static readonly UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private static readonly UnixFileMode PrivateFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly string temporaryRoot;
    private readonly string directoryName;
    private readonly SafeFileHandle temporaryRootHandle;
    private readonly LinuxFileIdentity temporaryRootIdentity;
    private readonly ulong temporaryRootMountIdentity;
    private readonly SafeFileHandle directoryHandle;
    private readonly LinuxFileIdentity directoryIdentity;
    private readonly ulong directoryMountIdentity;
    private readonly Func<SafeFileHandle, ulong> mountIdentityReader;
    private int disposed;

    private OpenAiCompatibleLinuxResponseCapture(
        string temporaryRoot,
        string directoryName,
        SafeFileHandle temporaryRootHandle,
        LinuxFileIdentity temporaryRootIdentity,
        ulong temporaryRootMountIdentity,
        SafeFileHandle directoryHandle,
        LinuxFileIdentity directoryIdentity,
        ulong directoryMountIdentity,
        Func<SafeFileHandle, ulong> mountIdentityReader)
    {
        this.temporaryRoot = temporaryRoot;
        this.directoryName = directoryName;
        this.temporaryRootHandle = temporaryRootHandle;
        this.temporaryRootIdentity = temporaryRootIdentity;
        this.temporaryRootMountIdentity = temporaryRootMountIdentity;
        this.directoryHandle = directoryHandle;
        this.directoryIdentity = directoryIdentity;
        this.directoryMountIdentity = directoryMountIdentity;
        this.mountIdentityReader = mountIdentityReader;
    }

    internal static OpenAiCompatibleLinuxResponseCapture Create(
        string captureDirectory,
        IReadOnlyCollection<string> forbiddenRoots,
        Func<SafeFileHandle, ulong>? mountIdentityReader = null,
        Action? beforeExclusiveCreate = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureDirectory);
        ArgumentNullException.ThrowIfNull(forbiddenRoots);
        if (!OperatingSystem.IsLinux()
            || RuntimeInformation.ProcessArchitecture != Architecture.X64
            || !Path.IsPathFullyQualified(captureDirectory))
        {
            throw new InvalidDataException("evaluation.capture.invalid");
        }

        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        var candidate = Path.GetFullPath(captureDirectory);
        var parent = Path.GetDirectoryName(candidate);
        var directoryName = Path.GetFileName(candidate);
        var normalizedForbidden = forbiddenRoots.Select(Path.GetFullPath).ToArray();
        if (!Directory.Exists(temporaryRoot)
            || new DirectoryInfo(temporaryRoot).LinkTarget is not null
            || parent is null
            || !SamePath(temporaryRoot, parent)
            || string.IsNullOrEmpty(directoryName)
            || normalizedForbidden.Any(root => Overlaps(root, candidate)))
        {
            throw new InvalidDataException("evaluation.capture.invalid");
        }

        var readMountIdentity = mountIdentityReader ?? ReadMountIdentity;
        SafeFileHandle? temporaryHandle = null;
        SafeFileHandle? captureHandle = null;
        var created = false;
        try
        {
            temporaryHandle = OpenDirectoryHandle(temporaryRoot);
            var temporaryIdentity = ReadIdentity(temporaryHandle);
            var temporaryMountIdentity = readMountIdentity(temporaryHandle);
            if (!EntryIsAbsent(temporaryHandle, directoryName))
            {
                throw new InvalidDataException("evaluation.capture.invalid");
            }

            beforeExclusiveCreate?.Invoke();
            if (MakeDirectoryAt(FileDescriptor(temporaryHandle), directoryName, DirectoryCreateMode) != 0)
            {
                throw new InvalidDataException("evaluation.capture.invalid");
            }

            created = true;
            captureHandle = OpenDirectoryAt(temporaryHandle, directoryName);
            File.SetUnixFileMode(captureHandle, PrivateDirectoryMode);
            var captureIdentity = ReadIdentity(captureHandle);
            var captureMountIdentity = readMountIdentity(captureHandle);
            if (captureMountIdentity != temporaryMountIdentity
                || File.GetUnixFileMode(captureHandle) != PrivateDirectoryMode
                || !Revalidate(
                    temporaryRoot,
                    directoryName,
                    temporaryHandle,
                    temporaryIdentity,
                    temporaryMountIdentity,
                    captureHandle,
                    captureIdentity,
                    captureMountIdentity,
                    readMountIdentity))
            {
                throw new InvalidDataException("evaluation.capture.invalid");
            }

            var result = new OpenAiCompatibleLinuxResponseCapture(
                temporaryRoot,
                directoryName,
                temporaryHandle,
                temporaryIdentity,
                temporaryMountIdentity,
                captureHandle,
                captureIdentity,
                captureMountIdentity,
                readMountIdentity);
            temporaryHandle = null;
            captureHandle = null;
            return result;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            captureHandle?.Dispose();
            if (created && temporaryHandle is not null && !temporaryHandle.IsInvalid)
            {
                _ = UnlinkAt(
                    FileDescriptor(temporaryHandle),
                    directoryName,
                    AtRemoveDirectory);
            }

            temporaryHandle?.Dispose();
            throw new InvalidDataException("evaluation.capture.invalid");
        }
    }

    internal async ValueTask WriteAsync(
        int providerRequestNumber,
        ReadOnlyMemory<byte> responseBody,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureCurrentIdentity();

        var temporaryName = ".capture-" + System.Security.Cryptography.RandomNumberGenerator.GetHexString(16);
        var destinationName = FormattableString.Invariant(
            $"provider-response-{providerRequestNumber:D4}.json");
        var temporaryCreated = false;
        try
        {
            var fileDescriptor = OpenAt(
                directoryHandle,
                temporaryName,
                OpenWriteOnly | OpenCreate | OpenExclusive | OpenNoFollow | OpenCloseOnExec,
                FileCreateMode);
            if (fileDescriptor < 0)
            {
                throw new IOException("evaluation.capture.failed");
            }

            temporaryCreated = true;
            using (var fileHandle = new SafeFileHandle((nint)fileDescriptor, ownsHandle: true))
            {
                File.SetUnixFileMode(fileHandle, PrivateFileMode);
                if (File.GetUnixFileMode(fileHandle) != PrivateFileMode)
                {
                    throw new IOException("evaluation.capture.failed");
                }

                await using var stream = new FileStream(
                    fileHandle,
                    FileAccess.Write,
                    bufferSize: 4_096,
                    isAsync: false);
                await stream.WriteAsync(responseBody, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            EnsureCurrentIdentity();
            if (RenameAt2(
                    DirectoryFileDescriptor,
                    temporaryName,
                    DirectoryFileDescriptor,
                    destinationName,
                    RenameNoReplace) != 0)
            {
                throw new IOException("evaluation.capture.failed");
            }

            temporaryCreated = false;
        }
        finally
        {
            if (temporaryCreated)
            {
                _ = UnlinkAt(DirectoryFileDescriptor, temporaryName, flags: 0);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            directoryHandle.Dispose();
            temporaryRootHandle.Dispose();
        }
    }

    private int DirectoryFileDescriptor => FileDescriptor(directoryHandle);

    private void EnsureCurrentIdentity()
    {
        if (!Revalidate(
                temporaryRoot,
                directoryName,
                temporaryRootHandle,
                temporaryRootIdentity,
                temporaryRootMountIdentity,
                directoryHandle,
                directoryIdentity,
                directoryMountIdentity,
                mountIdentityReader))
        {
            throw new IOException("evaluation.capture.failed");
        }
    }

    private static bool Revalidate(
        string temporaryRoot,
        string directoryName,
        SafeFileHandle retainedTemporaryRoot,
        LinuxFileIdentity expectedTemporaryRootIdentity,
        ulong expectedTemporaryRootMountIdentity,
        SafeFileHandle retainedDirectory,
        LinuxFileIdentity expectedDirectoryIdentity,
        ulong expectedDirectoryMountIdentity,
        Func<SafeFileHandle, ulong> readMountIdentity)
    {
        try
        {
            using var currentTemporaryRoot = OpenDirectoryHandle(temporaryRoot);
            using var currentDirectory = OpenDirectoryAt(retainedTemporaryRoot, directoryName);
            return ReadIdentity(currentTemporaryRoot) == expectedTemporaryRootIdentity
                && ReadIdentity(retainedTemporaryRoot) == expectedTemporaryRootIdentity
                && readMountIdentity(currentTemporaryRoot) == expectedTemporaryRootMountIdentity
                && readMountIdentity(retainedTemporaryRoot) == expectedTemporaryRootMountIdentity
                && ReadIdentity(currentDirectory) == expectedDirectoryIdentity
                && ReadIdentity(retainedDirectory) == expectedDirectoryIdentity
                && readMountIdentity(currentDirectory) == expectedDirectoryMountIdentity
                && readMountIdentity(retainedDirectory) == expectedDirectoryMountIdentity
                && expectedDirectoryMountIdentity == expectedTemporaryRootMountIdentity
                && File.GetUnixFileMode(currentDirectory) == PrivateDirectoryMode
                && File.GetUnixFileMode(retainedDirectory) == PrivateDirectoryMode;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException)
        {
            return false;
        }
    }

    private static bool EntryIsAbsent(SafeFileHandle parent, string name)
    {
        if (FStatAt(
                FileDescriptor(parent),
                name,
                out _,
                AtSymbolicLinkNoFollow) == 0)
        {
            return false;
        }

        return Marshal.GetLastPInvokeError() == NoSuchFileOrDirectory;
    }

    private static SafeFileHandle OpenDirectoryHandle(string path)
    {
        var fileDescriptor = Open(
            path,
            OpenReadOnly | OpenDirectory | OpenNoFollow | OpenCloseOnExec,
            DirectoryCreateMode);
        if (fileDescriptor < 0)
        {
            throw new IOException("evaluation.capture.invalid");
        }

        return new SafeFileHandle((nint)fileDescriptor, ownsHandle: true);
    }

    private static SafeFileHandle OpenDirectoryAt(SafeFileHandle parent, string name)
    {
        var fileDescriptor = OpenAt(
            parent,
            name,
            OpenReadOnly | OpenDirectory | OpenNoFollow | OpenCloseOnExec,
            DirectoryCreateMode);
        if (fileDescriptor < 0)
        {
            throw new IOException("evaluation.capture.invalid");
        }

        return new SafeFileHandle((nint)fileDescriptor, ownsHandle: true);
    }

    private static int FileDescriptor(SafeFileHandle handle) =>
        checked((int)handle.DangerousGetHandle());

    private static int OpenAt(
        SafeFileHandle directoryHandle,
        string name,
        int flags,
        uint mode) => OpenAt(
            FileDescriptor(directoryHandle),
            name,
            flags,
            mode);

    private static LinuxFileIdentity ReadIdentity(SafeFileHandle handle)
    {
        if (FStat(FileDescriptor(handle), out var value) != 0)
        {
            throw new IOException("evaluation.capture.invalid");
        }

        return new LinuxFileIdentity(value.Device, value.Inode);
    }

    private static ulong ReadMountIdentity(SafeFileHandle handle)
    {
        const string prefix = "mnt_id:\t";
        foreach (var line in File.ReadLines(
            FormattableString.Invariant($"/proc/self/fdinfo/{FileDescriptor(handle)}")))
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal)
                && ulong.TryParse(
                    line.AsSpan(prefix.Length),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var mountIdentity))
            {
                return mountIdentity;
            }
        }

        throw new InvalidDataException("evaluation.capture.invalid");
    }

    private static bool Overlaps(string first, string second) =>
        IsSameOrDescendant(first, second) || IsSameOrDescendant(second, first);

    private static bool IsSameOrDescendant(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative == "."
            || !Path.IsPathRooted(relative)
                && relative != ".."
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool SamePath(string first, string second) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
        StringComparison.Ordinal);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags, uint mode);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAt(int directory, string path, int flags, uint mode);

    [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)]
    private static extern int MakeDirectoryAt(int directory, string path, uint mode);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int FStat(int fileDescriptor, out LinuxStat value);

    [DllImport("libc", EntryPoint = "fstatat", SetLastError = true)]
    private static extern int FStatAt(
        int directory,
        string path,
        out LinuxStat value,
        int flags);

    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    private static extern int RenameAt2(
        int oldDirectory,
        string oldPath,
        int newDirectory,
        string newPath,
        uint flags);

    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
    private static extern int UnlinkAt(int directory, string path, int flags);

    private readonly record struct LinuxFileIdentity(ulong Device, ulong Inode);

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxStat
    {
        internal ulong Device;
        internal ulong Inode;
        internal ulong HardLinkCount;
        internal uint Mode;
        internal uint UserId;
        internal uint GroupId;
        internal int Padding;
        internal ulong RawDevice;
        internal long Size;
        internal long BlockSize;
        internal long Blocks;
        internal LinuxTimespec AccessTime;
        internal LinuxTimespec ModificationTime;
        internal LinuxTimespec ChangeTime;
        internal long Reserved0;
        internal long Reserved1;
        internal long Reserved2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxTimespec
    {
        internal long Seconds;
        internal long Nanoseconds;
    }
}
