using System.Collections.Immutable;
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

internal sealed record DocumentationScribeContextPathObservation(
    string RepositoryPath,
    ImmutableArray<DocumentationScribeContextPhysicalIdentity> DirectoryIdentities,
    DocumentationScribeContextPhysicalIdentity? FileIdentity);

internal sealed record DocumentationScribeContextObservedRead(
    DocumentationScribeContextStableRead Read,
    DocumentationScribeContextPathObservation Observation);

internal enum DocumentationScribeContextObservationStage
{
    AfterPreObservation,
    AfterDirectoryHandleAcquired,
    AfterFirstLeafRead,
    BeforeFinalObservation,
}

internal sealed record DocumentationScribeContextObservationEvent(
    DocumentationScribeContextObservationStage Stage,
    string RepositoryPath,
    int SegmentIndex);

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
    private const uint NtFileOpen = 1;
    private const uint NtFileDirectoryFile = 0x00000001;
    private const uint NtFileSynchronousIoNonAlert = 0x00000020;
    private const uint NtFileNonDirectoryFile = 0x00000040;
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
    private const uint UnixFileTypeMask = 0xF000;
    private const uint UnixRegularFile = 0x8000;
    private const uint UnixDirectory = 0x4000;

    internal static DocumentationScribeContextPathObservation CapturePath(
        string repositoryRoot,
        DocumentationScribeContextPhysicalIdentity rootIdentity,
        string repositoryPath,
        CancellationToken cancellationToken,
        Action? checkpoint = null,
        Action<DocumentationScribeContextObservationEvent>? observer = null)
    {
        var segments = Segments(repositoryPath);
        var before = ObservePath(
            repositoryRoot,
            rootIdentity,
            repositoryPath,
            segments,
            cancellationToken,
            checkpoint);
        InvokeObserver(
            observer,
            DocumentationScribeContextObservationStage.AfterPreObservation,
            repositoryPath,
            -1,
            cancellationToken,
            checkpoint);
        using (var parent = OpenBoundParent(
                   repositoryRoot,
                   rootIdentity,
                   repositoryPath,
                   segments,
                   before.DirectoryIdentities,
                   cancellationToken,
                   checkpoint,
                   observer))
        {
            var leaf = InspectExactName(parent, segments[^1], cancellationToken, checkpoint);
            RequireSpelling(leaf, allowMissing: before.FileIdentity is null);
            var identity = leaf.ExactCount == 0
                ? null
                : ReadRelativeFileIdentity(parent, segments[^1]);
            if (identity != before.FileIdentity)
            {
                throw Stale();
            }
        }

        InvokeObserver(
            observer,
            DocumentationScribeContextObservationStage.BeforeFinalObservation,
            repositoryPath,
            -1,
            cancellationToken,
            checkpoint);
        var after = ObservePath(
            repositoryRoot,
            rootIdentity,
            repositoryPath,
            segments,
            cancellationToken,
            checkpoint);
        if (!ObservationsEqual(after, before))
        {
            throw Stale();
        }

        return before;
    }

    internal static DocumentationScribeContextObservedRead CaptureRegularFile(
        string repositoryRoot,
        DocumentationScribeContextPhysicalIdentity rootIdentity,
        string repositoryPath,
        int maximumBytes,
        CancellationToken cancellationToken,
        Action? checkpoint = null,
        Action<DocumentationScribeContextObservationEvent>? observer = null) =>
        ReadRelative(
            repositoryRoot,
            rootIdentity,
            CapturePath(
                repositoryRoot,
                rootIdentity,
                repositoryPath,
                cancellationToken,
                checkpoint,
                observer),
            maximumBytes,
            acceptedBytes: false,
            cancellationToken,
            checkpoint,
            observer,
            invokePreObservation: false);

    internal static DocumentationScribeContextObservedRead ReadCapturedFile(
        string repositoryRoot,
        DocumentationScribeContextPhysicalIdentity rootIdentity,
        DocumentationScribeContextPathObservation observation,
        int maximumBytes,
        bool acceptedBytes,
        CancellationToken cancellationToken,
        Action? checkpoint = null,
        Action<DocumentationScribeContextObservationEvent>? observer = null) =>
        ReadRelative(
            repositoryRoot,
            rootIdentity,
            observation,
            maximumBytes,
            acceptedBytes,
            cancellationToken,
            checkpoint,
            observer,
            invokePreObservation: true);

    internal static void RevalidateAbsence(
        string repositoryRoot,
        DocumentationScribeContextPhysicalIdentity rootIdentity,
        DocumentationScribeContextPathObservation observation,
        CancellationToken cancellationToken,
        Action? checkpoint = null,
        Action<DocumentationScribeContextObservationEvent>? observer = null)
    {
        if (observation.FileIdentity is not null)
        {
            throw new InvalidOperationException("context.internal.observation-kind");
        }

        _ = CaptureAgainstObservation(
            repositoryRoot,
            rootIdentity,
            observation,
            cancellationToken,
            checkpoint,
            observer);
    }

    private static DocumentationScribeContextObservedRead ReadRelative(
        string repositoryRoot,
        DocumentationScribeContextPhysicalIdentity rootIdentity,
        DocumentationScribeContextPathObservation observation,
        int maximumBytes,
        bool acceptedBytes,
        CancellationToken cancellationToken,
        Action? checkpoint,
        Action<DocumentationScribeContextObservationEvent>? observer,
        bool invokePreObservation)
    {
        if (observation.FileIdentity is null)
        {
            throw Stale();
        }

        var segments = Segments(observation.RepositoryPath);
        if (invokePreObservation)
        {
            InvokeObserver(
                observer,
                DocumentationScribeContextObservationStage.AfterPreObservation,
                observation.RepositoryPath,
                -1,
                cancellationToken,
                checkpoint);
        }

        byte[] bytes;
        DocumentationScribeContextPhysicalIdentity identity;
        using (var parent = OpenBoundParent(
                   repositoryRoot,
                   rootIdentity,
                   observation.RepositoryPath,
                   segments,
                   observation.DirectoryIdentities,
                   cancellationToken,
                   checkpoint,
                   observer))
        {
            RequireSpelling(
                InspectExactName(parent, segments[^1], cancellationToken, checkpoint),
                allowMissing: false);
            using var stream = OpenRegularFileRelative(parent, segments[^1]);
            identity = ReadIdentity(stream.SafeFileHandle, expectDirectory: false);
            if (identity.LinkCount != 1)
            {
                throw UnsafePhysicalIdentity();
            }

            if (identity != observation.FileIdentity)
            {
                throw Stale();
            }

            if (identity.Length < 0 || identity.Length > maximumBytes || identity.Length > int.MaxValue)
            {
                throw acceptedBytes
                    ? Stale()
                    : new DocumentationScribeContextReadException(
                        DocumentationScribeContextReadFailure.Budget,
                        "context.budget.file-bytes");
            }

            bytes = ReadAll(stream, maximumBytes, cancellationToken, checkpoint);
            var afterFirstRead = ReadIdentity(stream.SafeFileHandle, expectDirectory: false);
            if (afterFirstRead != identity || afterFirstRead.Length != bytes.LongLength)
            {
                throw Stale();
            }

            InvokeObserver(
                observer,
                DocumentationScribeContextObservationStage.AfterFirstLeafRead,
                observation.RepositoryPath,
                segments.Length - 1,
                cancellationToken,
                checkpoint);
            RequireSpelling(
                InspectExactName(parent, segments[^1], cancellationToken, checkpoint),
                allowMissing: false);
            stream.Dispose();
            using var rebound = OpenRegularFileRelative(parent, segments[^1]);
            var reboundBefore = ReadIdentity(rebound.SafeFileHandle, expectDirectory: false);
            var reboundBytes = ReadAll(rebound, maximumBytes, cancellationToken, checkpoint);
            var reboundAfter = ReadIdentity(rebound.SafeFileHandle, expectDirectory: false);
            if (reboundBefore != identity
                || reboundAfter != identity
                || !bytes.AsSpan().SequenceEqual(reboundBytes))
            {
                throw Stale();
            }
        }

        InvokeObserver(
            observer,
            DocumentationScribeContextObservationStage.BeforeFinalObservation,
            observation.RepositoryPath,
            -1,
            cancellationToken,
            checkpoint);
        var final = ObservePath(
            repositoryRoot,
            rootIdentity,
            observation.RepositoryPath,
            segments,
            cancellationToken,
            checkpoint);
        if (!ObservationsEqual(final, observation))
        {
            throw Stale();
        }

        return new(new DocumentationScribeContextStableRead(bytes, identity), observation);
    }

    private static DocumentationScribeContextPathObservation CaptureAgainstObservation(
        string repositoryRoot,
        DocumentationScribeContextPhysicalIdentity rootIdentity,
        DocumentationScribeContextPathObservation observation,
        CancellationToken cancellationToken,
        Action? checkpoint,
        Action<DocumentationScribeContextObservationEvent>? observer)
    {
        var segments = Segments(observation.RepositoryPath);
        InvokeObserver(
            observer,
            DocumentationScribeContextObservationStage.AfterPreObservation,
            observation.RepositoryPath,
            -1,
            cancellationToken,
            checkpoint);
        using (var parent = OpenBoundParent(
                   repositoryRoot,
                   rootIdentity,
                   observation.RepositoryPath,
                   segments,
                   observation.DirectoryIdentities,
                   cancellationToken,
                   checkpoint,
                   observer))
        {
            var leaf = InspectExactName(parent, segments[^1], cancellationToken, checkpoint);
            RequireSpelling(leaf, allowMissing: observation.FileIdentity is null);
            var identity = leaf.ExactCount == 0
                ? null
                : ReadRelativeFileIdentity(parent, segments[^1]);
            if (identity != observation.FileIdentity)
            {
                throw Stale();
            }
        }

        InvokeObserver(
            observer,
            DocumentationScribeContextObservationStage.BeforeFinalObservation,
            observation.RepositoryPath,
            -1,
            cancellationToken,
            checkpoint);
        var final = ObservePath(
            repositoryRoot,
            rootIdentity,
            observation.RepositoryPath,
            segments,
            cancellationToken,
            checkpoint);
        if (!ObservationsEqual(final, observation))
        {
            throw Stale();
        }

        return final;
    }

    private static DocumentationScribeContextPathObservation ObservePath(
        string repositoryRoot,
        DocumentationScribeContextPhysicalIdentity rootIdentity,
        string repositoryPath,
        string[] segments,
        CancellationToken cancellationToken,
        Action? checkpoint)
    {
        cancellationToken.ThrowIfCancellationRequested();
        checkpoint?.Invoke();
        using var parent = OpenObservedParent(
            repositoryRoot,
            rootIdentity,
            segments,
            cancellationToken,
            checkpoint,
            out var directoryIdentities);
        var spelling = InspectExactName(parent, segments[^1], cancellationToken, checkpoint);
        RequireSpelling(spelling, allowMissing: true);
        var fileIdentity = spelling.ExactCount == 0
            ? null
            : ReadRelativeFileIdentity(parent, segments[^1]);
        return new(repositoryPath, directoryIdentities, fileIdentity);
    }

    private static SafeFileHandle OpenObservedParent(
        string repositoryRoot,
        DocumentationScribeContextPhysicalIdentity rootIdentity,
        string[] segments,
        CancellationToken cancellationToken,
        Action? checkpoint,
        out ImmutableArray<DocumentationScribeContextPhysicalIdentity> identities)
    {
        var builder = ImmutableArray.CreateBuilder<DocumentationScribeContextPhysicalIdentity>();
        var current = OpenDirectory(repositoryRoot);
        try
        {
            var currentIdentity = ReadIdentity(current, expectDirectory: true);
            if (currentIdentity != rootIdentity)
            {
                throw Stale();
            }

            builder.Add(currentIdentity);
            for (var index = 0; index < segments.Length - 1; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                checkpoint?.Invoke();
                RequireSpelling(
                    InspectExactName(current, segments[index], cancellationToken, checkpoint),
                    allowMissing: false);
                var next = OpenDirectoryRelative(current, segments[index]);
                current.Dispose();
                current = next;
                builder.Add(ReadIdentity(current, expectDirectory: true));
            }

            identities = builder.ToImmutable();
            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenBoundParent(
        string repositoryRoot,
        DocumentationScribeContextPhysicalIdentity rootIdentity,
        string repositoryPath,
        string[] segments,
        ImmutableArray<DocumentationScribeContextPhysicalIdentity> expectedIdentities,
        CancellationToken cancellationToken,
        Action? checkpoint,
        Action<DocumentationScribeContextObservationEvent>? observer)
    {
        if (expectedIdentities.Length != segments.Length)
        {
            throw new InvalidOperationException("context.internal.directory-chain");
        }

        var current = OpenDirectory(repositoryRoot);
        try
        {
            InvokeObserver(
                observer,
                DocumentationScribeContextObservationStage.AfterDirectoryHandleAcquired,
                repositoryPath,
                -1,
                cancellationToken,
                checkpoint);
            var identity = ReadIdentity(current, expectDirectory: true);
            if (identity != rootIdentity || identity != expectedIdentities[0])
            {
                throw Stale();
            }

            for (var index = 0; index < segments.Length - 1; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                checkpoint?.Invoke();
                var spelling = InspectExactName(
                    current,
                    segments[index],
                    cancellationToken,
                    checkpoint);
                if (spelling.ExactCount == 0)
                {
                    RequireSpelling(spelling, allowMissing: false);
                }

                SafeFileHandle? next = OpenDirectoryRelative(current, segments[index]);
                try
                {
                    InvokeObserver(
                        observer,
                        DocumentationScribeContextObservationStage.AfterDirectoryHandleAcquired,
                        repositoryPath,
                        index,
                        cancellationToken,
                        checkpoint);
                    RequireSpelling(spelling, allowMissing: false);
                    var nextIdentity = ReadIdentity(next, expectDirectory: true);
                    if (nextIdentity != expectedIdentities[index + 1])
                    {
                        throw Stale();
                    }

                    current.Dispose();
                    current = next;
                    next = null;
                }
                finally
                {
                    next?.Dispose();
                }
            }

            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static void InvokeObserver(
        Action<DocumentationScribeContextObservationEvent>? observer,
        DocumentationScribeContextObservationStage stage,
        string repositoryPath,
        int segmentIndex,
        CancellationToken cancellationToken,
        Action? checkpoint)
    {
        cancellationToken.ThrowIfCancellationRequested();
        checkpoint?.Invoke();
        observer?.Invoke(new(stage, repositoryPath, segmentIndex));
        checkpoint?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static bool ObservationsEqual(
        DocumentationScribeContextPathObservation left,
        DocumentationScribeContextPathObservation right) =>
        string.Equals(left.RepositoryPath, right.RepositoryPath, StringComparison.Ordinal)
        && left.FileIdentity == right.FileIdentity
        && left.DirectoryIdentities.SequenceEqual(right.DirectoryIdentities);

    private static string[] Segments(string repositoryPath)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath)
            || Path.IsPathFullyQualified(repositoryPath)
            || repositoryPath.Contains('\\', StringComparison.Ordinal)
            || repositoryPath.StartsWith("/", StringComparison.Ordinal)
            || repositoryPath.EndsWith("/", StringComparison.Ordinal))
        {
            throw Unsafe();
        }

        var segments = repositoryPath.Split('/');
        if (segments.Any(segment => segment.Length == 0
                || segment is "." or ".."
                || segment.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0))
        {
            throw Unsafe();
        }

        return segments;
    }

    private static DocumentationScribeContextPhysicalIdentity ReadRelativeFileIdentity(
        SafeFileHandle parent,
        string name)
    {
        using var stream = OpenRegularFileRelative(parent, name);
        var identity = ReadIdentity(stream.SafeFileHandle, expectDirectory: false);
        if (identity.LinkCount != 1)
        {
            throw UnsafePhysicalIdentity();
        }

        return identity;
    }

    private static NameInspection InspectExactName(
        SafeFileHandle parent,
        string expected,
        CancellationToken cancellationToken,
        Action? checkpoint)
    {
        var exact = 0;
        var folded = 0;
        foreach (var name in EnumerateNames(parent, cancellationToken, checkpoint))
        {
            if (!string.Equals(name, expected, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            folded += 1;
            if (string.Equals(name, expected, StringComparison.Ordinal))
            {
                exact += 1;
            }
        }

        return new(exact, folded);
    }

    private static void RequireSpelling(NameInspection inspection, bool allowMissing)
    {
        if (inspection.FoldedCount > 1
            || inspection.FoldedCount == 1 && inspection.ExactCount == 0)
        {
            throw Unsafe();
        }

        if (inspection.ExactCount == 0 && !allowMissing)
        {
            throw Stale();
        }

        if (inspection.ExactCount > 1)
        {
            throw Unsafe();
        }
    }

    private static IEnumerable<string> EnumerateNames(
        SafeFileHandle parent,
        CancellationToken cancellationToken,
        Action? checkpoint)
    {
        if (OperatingSystem.IsWindows())
        {
            return EnumerateWindowsNames(parent, cancellationToken, checkpoint);
        }

        if (OperatingSystem.IsLinux())
        {
            return EnumerateLinuxNames(parent, cancellationToken, checkpoint);
        }

        throw Unsafe();
    }

    private sealed record NameInspection(int ExactCount, int FoldedCount);

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

    private static IEnumerable<string> EnumerateWindowsNames(
        SafeFileHandle parent,
        CancellationToken cancellationToken,
        Action? checkpoint)
    {
        const int bufferSize = 64 * 1024;
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            var informationClass = WindowsFileIdBothDirectoryRestartInfo;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                checkpoint?.Invoke();
                if (!GetFileInformationByHandleEx(
                        parent,
                        informationClass,
                        buffer,
                        bufferSize))
                {
                    if (Marshal.GetLastPInvokeError() == WindowsErrorNoMoreFiles)
                    {
                        yield break;
                    }

                    throw new InvalidOperationException("context.internal.native-enumeration");
                }

                informationClass = WindowsFileIdBothDirectoryInfo;
                var offset = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    checkpoint?.Invoke();
                    var entry = IntPtr.Add(buffer, offset);
                    var nextOffset = Marshal.ReadInt32(entry, 0);
                    var nameLength = Marshal.ReadInt32(entry, 60);
                    if (nameLength < 0
                        || (nameLength & 1) != 0
                        || nameLength > bufferSize - offset - 104)
                    {
                        throw new InvalidOperationException("context.internal.native-enumeration");
                    }

                    var name = Marshal.PtrToStringUni(IntPtr.Add(entry, 104), nameLength / 2)
                        ?? throw new InvalidOperationException("context.internal.native-enumeration");
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
                        throw new InvalidOperationException("context.internal.native-enumeration");
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
        SafeFileHandle parent,
        CancellationToken cancellationToken,
        Action? checkpoint)
    {
        var enumerationFileDescriptor = OpenAt(
            parent.DangerousGetHandle().ToInt32(),
            ".",
            UnixOpenReadOnly | UnixOpenCloseOnExec | UnixOpenNoFollow | UnixOpenDirectory);
        if (enumerationFileDescriptor < 0)
        {
            throw new InvalidOperationException("context.internal.native-enumeration");
        }

        var directory = FdOpenDirectory(enumerationFileDescriptor);
        if (directory == IntPtr.Zero)
        {
            _ = Close(enumerationFileDescriptor);
            throw new InvalidOperationException("context.internal.native-enumeration");
        }

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                checkpoint?.Invoke();
                Marshal.SetLastPInvokeError(0);
                var entry = ReadDirectoryEntry(directory);
                if (entry == IntPtr.Zero)
                {
                    if (Marshal.GetLastPInvokeError() != 0)
                    {
                        throw new InvalidOperationException("context.internal.native-enumeration");
                    }

                    yield break;
                }

                var name = Marshal.PtrToStringUTF8(IntPtr.Add(entry, LinuxEntryNameOffset))
                    ?? throw new InvalidOperationException("context.internal.native-enumeration");
                if (name is not "." and not "..")
                {
                    yield return name;
                }
            }
        }
        finally
        {
            _ = CloseDirectory(directory);
        }
    }

    private static SafeFileHandle OpenDirectoryRelative(SafeFileHandle parent, string name)
    {
        SafeFileHandle handle;
        if (OperatingSystem.IsWindows())
        {
            handle = NtOpenRelative(
                parent,
                name,
                FileListDirectory | FileReadAttributes | SynchronizeAccess,
                NtFileDirectoryFile | NtFileSynchronousIoNonAlert
                    | NtFileOpenReparsePoint | NtFileOpenForBackupIntent);
        }
        else if (OperatingSystem.IsLinux())
        {
            handle = new SafeFileHandle(
                (IntPtr)OpenAt(
                    parent.DangerousGetHandle().ToInt32(),
                    name,
                    UnixOpenReadOnly | UnixOpenCloseOnExec | UnixOpenNoFollow | UnixOpenDirectory),
                ownsHandle: true);
            ValidateUnixOpen(handle);
        }
        else
        {
            throw Unsafe();
        }

        return ValidateOpenedHandle(handle, expectDirectory: true);
    }

    private static FileStream OpenRegularFileRelative(SafeFileHandle parent, string name)
    {
        SafeFileHandle handle;
        if (OperatingSystem.IsWindows())
        {
            handle = NtOpenRelative(
                parent,
                name,
                GenericRead | SynchronizeAccess,
                NtFileNonDirectoryFile | NtFileSynchronousIoNonAlert | NtFileOpenReparsePoint);
        }
        else if (OperatingSystem.IsLinux())
        {
            handle = new SafeFileHandle(
                (IntPtr)OpenAt(
                    parent.DangerousGetHandle().ToInt32(),
                    name,
                    UnixOpenReadOnly | UnixOpenCloseOnExec | UnixOpenNoFollow),
                ownsHandle: true);
            ValidateUnixOpen(handle);
        }
        else
        {
            throw Unsafe();
        }

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
            throw new InvalidOperationException("context.internal.invalid-handle");
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

    private static void ValidateUnixOpen(SafeFileHandle handle)
    {
        if (!handle.IsInvalid)
        {
            return;
        }

        var error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        throw error switch
        {
            2 => Stale(),
            20 or 21 or 40 => Unsafe(),
            _ => new InvalidOperationException("context.internal.native-open"),
        };
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
            Marshal.StructureToPtr(
                new UnicodeString
                {
                    Length = nameBytes,
                    MaximumLength = checked((ushort)(nameBytes + sizeof(char))),
                    Buffer = nameBuffer,
                },
                unicodeStringBuffer,
                fDeleteOld: false);
            var attributes = new ObjectAttributes
            {
                Length = checked((uint)Marshal.SizeOf<ObjectAttributes>()),
                RootDirectory = parent.DangerousGetHandle(),
                ObjectName = unicodeStringBuffer,
                Attributes = 0,
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
                throw unchecked((uint)status) switch
                {
                    0xC000000F or 0xC0000034 or 0xC000003A => Stale(),
                    0xC00000BA or 0xC0000103 => Unsafe(),
                    _ => new InvalidOperationException("context.internal.native-open"),
                };
            }

            return handle;
        }
        finally
        {
            Marshal.FreeHGlobal(unicodeStringBuffer);
            Marshal.FreeHGlobal(nameBuffer);
        }
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

    private static SafeFileHandle OpenDirectory(string path)
    {
        EnsureNoReparseComponents(path);
        SafeFileHandle handle;
        if (OperatingSystem.IsWindows())
        {
            handle = CreateFileW(
                path,
                FileListDirectory | FileReadAttributes,
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

    private static DocumentationScribeContextReadException UnsafePhysicalIdentity() =>
        new(
            DocumentationScribeContextReadFailure.Unsafe,
            "context.unsafe.physical-identity");

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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int informationClass,
        IntPtr information,
        int bufferSize);

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
