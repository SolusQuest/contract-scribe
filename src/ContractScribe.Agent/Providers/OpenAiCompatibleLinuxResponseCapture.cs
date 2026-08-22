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
    private const uint DirectoryCreateMode = 0x1C0;
    private const uint FileCreateMode = 0x180;
    private const uint RenameNoReplace = 1;
    private const int AtRemoveDirectory = 0x200;
    private static readonly UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private static readonly UnixFileMode PrivateFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly string directory;
    private readonly string temporaryRoot;
    private readonly string[] forbiddenRoots;
    private readonly SafeFileHandle directoryHandle;
    private readonly LinuxFileIdentity identity;
    private int disposed;

    private OpenAiCompatibleLinuxResponseCapture(
        string directory,
        string temporaryRoot,
        string[] forbiddenRoots,
        SafeFileHandle directoryHandle,
        LinuxFileIdentity identity)
    {
        this.directory = directory;
        this.temporaryRoot = temporaryRoot;
        this.forbiddenRoots = forbiddenRoots;
        this.directoryHandle = directoryHandle;
        this.identity = identity;
    }

    internal static OpenAiCompatibleLinuxResponseCapture Create(
        string captureDirectory,
        IReadOnlyCollection<string> forbiddenRoots)
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
        var normalizedForbidden = forbiddenRoots.Select(Path.GetFullPath).ToArray();
        if (!Directory.Exists(temporaryRoot)
            || new DirectoryInfo(temporaryRoot).LinkTarget is not null
            || !IsStrictDescendant(temporaryRoot, candidate)
            || normalizedForbidden.Any(root => Overlaps(root, candidate))
            || Directory.Exists(candidate)
            || File.Exists(candidate))
        {
            throw new InvalidDataException("evaluation.capture.invalid");
        }

        var parent = Path.GetDirectoryName(candidate);
        if (parent is null
            || !Directory.Exists(parent)
            || ContainsSymbolicLink(temporaryRoot, parent))
        {
            throw new InvalidDataException("evaluation.capture.invalid");
        }

        SafeFileHandle? handle = null;
        var created = false;
        try
        {
            Directory.CreateDirectory(candidate, PrivateDirectoryMode);
            created = true;
            handle = OpenDirectoryHandle(candidate);
            File.SetUnixFileMode(handle, PrivateDirectoryMode);
            if (File.GetUnixFileMode(handle) != PrivateDirectoryMode)
            {
                throw new InvalidDataException("evaluation.capture.invalid");
            }

            var identity = ReadIdentity(handle);
            using var temporaryHandle = OpenDirectoryHandle(temporaryRoot);
            var temporaryIdentity = ReadIdentity(temporaryHandle);
            if (identity.Device != temporaryIdentity.Device
                || !Revalidate(
                    candidate,
                    temporaryRoot,
                    normalizedForbidden,
                    handle,
                    identity))
            {
                throw new InvalidDataException("evaluation.capture.invalid");
            }

            var result = new OpenAiCompatibleLinuxResponseCapture(
                candidate,
                temporaryRoot,
                normalizedForbidden,
                handle,
                identity);
            handle = null;
            return result;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            handle?.Dispose();
            if (created && Directory.Exists(candidate))
            {
                DeleteCreatedDirectoryBestEffort(candidate);
            }

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
        }
    }

    private int DirectoryFileDescriptor => checked((int)directoryHandle.DangerousGetHandle());

    private void EnsureCurrentIdentity()
    {
        if (!Revalidate(
                directory,
                temporaryRoot,
                forbiddenRoots,
                directoryHandle,
                identity))
        {
            throw new IOException("evaluation.capture.failed");
        }
    }

    private static bool Revalidate(
        string candidate,
        string temporaryRoot,
        IReadOnlyCollection<string> forbiddenRoots,
        SafeFileHandle retainedHandle,
        LinuxFileIdentity expected)
    {
        if (!Directory.Exists(candidate)
            || !IsStrictDescendant(temporaryRoot, candidate)
            || forbiddenRoots.Any(root => Overlaps(root, candidate))
            || ContainsSymbolicLink(temporaryRoot, candidate)
            || File.GetUnixFileMode(retainedHandle) != PrivateDirectoryMode)
        {
            return false;
        }

        using var current = OpenDirectoryHandle(candidate);
        return ReadIdentity(current) == expected
            && ReadIdentity(retainedHandle) == expected
            && File.GetUnixFileMode(current) == PrivateDirectoryMode;
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

    private static void DeleteCreatedDirectoryBestEffort(string candidate)
    {
        try
        {
            Directory.Delete(candidate);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
            return;
        }
    }

    private static int OpenAt(
        SafeFileHandle directoryHandle,
        string name,
        int flags,
        uint mode) => OpenAt(
            checked((int)directoryHandle.DangerousGetHandle()),
            name,
            flags,
            mode);

    private static LinuxFileIdentity ReadIdentity(SafeFileHandle handle)
    {
        if (FStat(checked((int)handle.DangerousGetHandle()), out var value) != 0)
        {
            throw new IOException("evaluation.capture.invalid");
        }

        return new LinuxFileIdentity(value.Device, value.Inode);
    }

    private static bool ContainsSymbolicLink(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        var current = root;
        foreach (var component in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Join(current, component);
            if ((Directory.Exists(current) || File.Exists(current))
                && new FileInfo(current).LinkTarget is not null)
            {
                return true;
            }
        }

        return false;
    }

    private static bool Overlaps(string first, string second) =>
        IsSameOrDescendant(first, second) || IsSameOrDescendant(second, first);

    private static bool IsStrictDescendant(string root, string candidate) =>
        !SamePath(root, candidate) && IsSameOrDescendant(root, candidate);

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

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int FStat(int fileDescriptor, out LinuxStat value);

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
