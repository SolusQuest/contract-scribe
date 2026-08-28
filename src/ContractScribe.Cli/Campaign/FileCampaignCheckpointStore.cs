using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ContractScribe.Core;
using Microsoft.Win32.SafeHandles;

namespace ContractScribe.Cli;

/// <summary>
/// Linux-only, owner-private implementation of the campaign checkpoint store port.
/// The coordination files are deliberately an implementation detail of this adapter.
/// </summary>
internal sealed class FileCampaignCheckpointStore : ICampaignCheckpointStore
{
    private const int OpenReadOnly = 0;
    private const int OpenReadWrite = 2;
    private const int OpenCreate = 0x40;
    private const int OpenExclusive = 0x80;
    private const int OpenNonBlocking = 0x800;
    private const int OpenDirectory = 0x10000;
    private const int OpenNoFollow = 0x20000;
    private const int OpenCloseOnExec = 0x80000;
    private const int AtSymlinkNoFollow = 0x100;
    private const int LockExclusive = 2;
    private const int LockNonBlocking = 4;
    private const uint RenameNoReplace = 1;
    private const uint FileTypeMask = 0xF000;
    private const uint RegularFile = 0x8000;
    private const uint DirectoryFile = 0x4000;
    private const uint OwnerDirectoryMode = 0x1C0; // 0700
    private const uint OwnerFileMode = 0x180; // 0600
    private const int NoData = 61;
    private const int AlreadyExists = 17;
    private const int NotFound = 2;
    private const int WouldBlock = 11;
    private const int MaximumLeaseBytes = 2048;
    private const string LeaseVersion = "contract-scribe-checkpoint-lease-v1";

    private readonly string checkpointPath;
    private readonly string checkpointName;
    private readonly string stateDirectoryPath;
    private readonly string repositoryRoot;
    private readonly string leaseName;
    private readonly Action<string>? testHook;

    internal FileCampaignCheckpointStore(
        string checkpointPath,
        string resolvedRepositoryRoot,
        Action<string>? testHook = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedRepositoryRoot);
        if (!Path.IsPathFullyQualified(checkpointPath)
            || !Path.IsPathFullyQualified(resolvedRepositoryRoot))
        {
            throw new ArgumentException("Checkpoint and repository paths must be fully qualified.");
        }

        this.checkpointPath = Path.GetFullPath(checkpointPath);
        repositoryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolvedRepositoryRoot));
        checkpointName = Path.GetFileName(this.checkpointPath);
        stateDirectoryPath = Path.GetDirectoryName(this.checkpointPath)
            ?? throw new ArgumentException("The checkpoint must have a parent directory.", nameof(checkpointPath));
        if (checkpointName.Length is 0 or > 120
            || checkpointName is "." or ".."
            || checkpointName.Contains(".contractscribe-checkpoint-", StringComparison.Ordinal))
        {
            throw new ArgumentException("The checkpoint filename is reserved or unsupported.", nameof(checkpointPath));
        }
        if (Overlaps(this.checkpointPath, repositoryRoot)
            || Overlaps(stateDirectoryPath, repositoryRoot))
        {
            throw new ArgumentException("Checkpoint state must be outside the source repository.", nameof(checkpointPath));
        }

        leaseName = $".{checkpointName}.contractscribe-checkpoint-lease";
        this.testHook = testHook;
    }

    public ValueTask<CampaignCheckpointReadResult> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsSupportedPlatform())
        {
            return ValueTask.FromResult(CampaignCheckpointReadResult.Unreadable());
        }

        try
        {
            using var context = OpenContext();
            var observation = ReadEntry(context, checkpointName, cancellationToken);
            return ValueTask.FromResult(observation.ToPublicResult());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (StoreFault fault)
        {
            return ValueTask.FromResult(
                fault.Kind == StoreFaultKind.Invalid
                    ? CampaignCheckpointReadResult.Invalid()
                    : CampaignCheckpointReadResult.Unreadable());
        }
        catch (Exception exception) when (IsBoundedFailure(exception))
        {
            return ValueTask.FromResult(CampaignCheckpointReadResult.Unreadable());
        }
    }

    public ValueTask<CampaignCheckpointWriteResult> CreateIfAbsentAsync(
        ReadOnlyMemory<byte> exactUtf8Json,
        long checkpointRevision,
        string sha256,
        CancellationToken cancellationToken) => ValueTask.FromResult(WriteCore(
            OperationKind.Create,
            null,
            null,
            exactUtf8Json,
            checkpointRevision,
            sha256,
            cancellationToken));

    public ValueTask<CampaignCheckpointWriteResult> ReplaceIfCurrentAsync(
        long expectedCheckpointRevision,
        string expectedSha256,
        ReadOnlyMemory<byte> exactUtf8Json,
        long checkpointRevision,
        string sha256,
        CancellationToken cancellationToken) => ValueTask.FromResult(WriteCore(
            OperationKind.Replace,
            expectedCheckpointRevision,
            expectedSha256,
            exactUtf8Json,
            checkpointRevision,
            sha256,
            cancellationToken));

    private CampaignCheckpointWriteResult WriteCore(
        OperationKind operation,
        long? expectedRevision,
        string? expectedSha256,
        ReadOnlyMemory<byte> intendedBytes,
        long intendedRevision,
        string intendedSha256,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsSupportedPlatform()
            || !TryValidateIntended(intendedBytes, intendedRevision, intendedSha256)
            || operation == OperationKind.Replace
                && (!IsRevision(expectedRevision) || !IsSha256(expectedSha256)))
        {
            return Unwritable();
        }

        ActiveLease? active = null;
        DirectoryContext? context = null;
        var published = false;
        var cleanupComplete = false;
        try
        {
            context = OpenContext();
            for (var acquisitionAttempt = 0; acquisitionAttempt < 2; acquisitionAttempt++)
            {
                var acquire = TryCreateFreshLease(
                    context,
                    operation,
                    expectedRevision,
                    expectedSha256,
                    intendedRevision,
                    intendedSha256);
                if (acquire is not null)
                {
                    active = acquire;
                    break;
                }

                if (acquisitionAttempt != 0 || !TryRecoverStaleLease(context))
                {
                    return Unwritable();
                }
            }
            if (active is null)
            {
                return Unwritable();
            }

            cancellationToken.ThrowIfCancellationRequested();
            var current = ReadEntry(context, checkpointName, cancellationToken);
            var conditional = ClassifyConditional(operation, current, expectedRevision, expectedSha256);
            if (conditional != CampaignCheckpointWriteKind.Written)
            {
                cleanupComplete = CleanupBeforePublication(context, active);
                return cleanupComplete
                    ? new CampaignCheckpointWriteResult(conditional)
                    : Unwritable();
            }

            WriteExact(active.TempHandle, intendedBytes.Span);
            RandomAccess.FlushToDisk(active.TempHandle);
            testHook?.Invoke("after-temp-write");
            var staged = ReadEntry(context, active.Record.TempName, cancellationToken);
            if (!staged.IsExact(intendedBytes.Span, intendedRevision, intendedSha256)
                || staged.Identity != active.Record.TempIdentity)
            {
                cleanupComplete = CleanupBeforePublication(context, active);
                return Unwritable();
            }

            cancellationToken.ThrowIfCancellationRequested();
            testHook?.Invoke("before-publish");

            // No callback, cancellation observation, or caller code is admitted from the
            // final identity/predecessor revalidation through the rename linearization point.
            RevalidateContext(context);
            ValidateNamedHandle(context, leaseName, active.LeaseHandle, active.LeaseIdentity);
            ValidateNamedHandle(context, active.Record.TempName, active.TempHandle, active.Record.TempIdentity);
            var finalCurrent = ReadEntry(context, checkpointName, CancellationToken.None);
            if (ClassifyConditional(operation, finalCurrent, expectedRevision, expectedSha256)
                != CampaignCheckpointWriteKind.Written)
            {
                cleanupComplete = CleanupBeforePublication(context, active);
                return Unwritable();
            }
            if (RenameAt2(
                    FileDescriptor(context.DirectoryHandle),
                    active.Record.TempName,
                    FileDescriptor(context.DirectoryHandle),
                    checkpointName,
                    operation == OperationKind.Create ? RenameNoReplace : 0) != 0)
            {
                cleanupComplete = CleanupBeforePublication(context, active);
                return Unwritable();
            }
            published = true;

            testHook?.Invoke("after-publish-before-readback");
            var readback = ReadEntry(context, checkpointName, CancellationToken.None);
            if (!readback.IsExact(intendedBytes.Span, intendedRevision, intendedSha256)
                || readback.Identity != active.Record.TempIdentity)
            {
                return Unwritable();
            }

            cleanupComplete = CleanupAfterPublication(context, active, readback);
            if (!cleanupComplete)
            {
                return Unwritable();
            }
            active.Dispose();
            active = null;
            context.Dispose();
            context = null;
            cancellationToken.ThrowIfCancellationRequested();
            return new CampaignCheckpointWriteResult(CampaignCheckpointWriteKind.Written);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cleanupComplete = active is null
                || context is not null
                    && (published
                        ? TryCleanupPublishedCancellation(context, active, intendedBytes.Span, intendedRevision, intendedSha256)
                        : CleanupBeforePublication(context, active));
            if (cleanupComplete)
            {
                throw;
            }
            return Unwritable();
        }
        catch (Exception exception) when (IsBoundedFailure(exception))
        {
            if (active is not null && context is not null && !published)
            {
                cleanupComplete = CleanupBeforePublication(context, active);
            }
            return Unwritable();
        }
        finally
        {
            if (active is not null && !cleanupComplete && !published && context is not null)
            {
                CleanupBeforePublication(context, active);
            }
            active?.Dispose();
            context?.Dispose();
        }
    }

    private ActiveLease? TryCreateFreshLease(
        DirectoryContext context,
        OperationKind operation,
        long? expectedRevision,
        string? expectedSha256,
        long intendedRevision,
        string intendedSha256)
    {
        RevalidateContext(context);
        var descriptor = OpenAt(
            FileDescriptor(context.DirectoryHandle),
            leaseName,
            OpenReadWrite | OpenCreate | OpenExclusive | OpenNonBlocking | OpenNoFollow | OpenCloseOnExec,
            OwnerFileMode);
        if (descriptor < 0)
        {
            return Marshal.GetLastPInvokeError() == AlreadyExists
                ? null
                : throw UnreadableFault();
        }

        var leaseHandle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
        SafeFileHandle? tempHandle = null;
        FileIdentity? leaseIdentity = null;
        FileIdentity? tempIdentity = null;
        string? tempName = null;
        try
        {
            testHook?.Invoke("after-lease-create-before-lock");
            if (Flock(descriptor, LockExclusive | LockNonBlocking) != 0)
            {
                throw UnreadableFault();
            }

            leaseIdentity = ValidatePrivateFile(leaseHandle);
            ValidateNamedHandle(context, leaseName, leaseHandle, leaseIdentity.Value);
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            tempName = $".{checkpointName}.contractscribe-checkpoint-{intendedRevision.ToString(CultureInfo.InvariantCulture)}-{token}.tmp";
            var tempDescriptor = OpenAt(
                FileDescriptor(context.DirectoryHandle),
                tempName,
                OpenReadWrite | OpenCreate | OpenExclusive | OpenNonBlocking | OpenNoFollow | OpenCloseOnExec,
                OwnerFileMode);
            if (tempDescriptor < 0)
            {
                throw UnreadableFault();
            }

            tempHandle = new SafeFileHandle((nint)tempDescriptor, ownsHandle: true);
            tempIdentity = ValidatePrivateFile(tempHandle);
            ValidateNamedHandle(context, tempName, tempHandle, tempIdentity.Value);
            var record = new LeaseRecord(
                operation,
                expectedRevision,
                expectedSha256,
                intendedRevision,
                intendedSha256,
                token,
                tempName,
                tempIdentity.Value);
            var encoded = record.Encode();
            WriteExact(leaseHandle, encoded);
            RandomAccess.FlushToDisk(leaseHandle);
            if (!ReadExactBytes(leaseHandle, MaximumLeaseBytes).AsSpan().SequenceEqual(encoded))
            {
                throw UnreadableFault();
            }
            testHook?.Invoke("after-lease-record");
            return new ActiveLease(leaseHandle, leaseIdentity.Value, tempHandle, record);
        }
        catch
        {
            if (tempHandle is not null && tempIdentity is not null && tempName is not null)
            {
                TryDeleteExact(context, tempName, tempHandle, tempIdentity.Value);
            }
            tempHandle?.Dispose();
            if (leaseIdentity is not null)
            {
                TryDeleteExact(context, leaseName, leaseHandle, leaseIdentity.Value);
            }
            leaseHandle.Dispose();
            throw;
        }
    }

    private bool TryRecoverStaleLease(DirectoryContext context)
    {
        var leaseStatus = InspectName(context, leaseName);
        if (leaseStatus is not { Kind: NameKind.Regular, Identity: { } observedLeaseIdentity })
        {
            return false;
        }

        using var lease = OpenObserved(context, leaseName, observedLeaseIdentity, write: true);
        var leaseIdentity = ValidatePrivateFile(lease);
        if (leaseIdentity != observedLeaseIdentity)
        {
            return false;
        }

        // B005: an existing-path contender never locks an empty/partial/malformed
        // creator inode. Only a complete canonical record is lock-eligible.
        var beforeLockBytes = ReadExactBytes(lease, MaximumLeaseBytes);
        if (!LeaseRecord.TryParse(beforeLockBytes, checkpointName, out var record))
        {
            return false;
        }
        if (Flock(FileDescriptor(lease), LockExclusive | LockNonBlocking) != 0)
        {
            return false;
        }

        var afterLockBytes = ReadExactBytes(lease, MaximumLeaseBytes);
        if (!afterLockBytes.AsSpan().SequenceEqual(beforeLockBytes)
            || !LeaseRecord.TryParse(afterLockBytes, checkpointName, out var lockedRecord)
            || lockedRecord != record)
        {
            return false;
        }
        RevalidateContext(context);
        ValidateNamedHandle(context, leaseName, lease, leaseIdentity);

        var checkpoint = ReadEntry(context, checkpointName, CancellationToken.None);
        var tempStatus = InspectName(context, record.TempName);
        if (checkpoint.IsExact(record.IntendedRevision, record.IntendedSha256))
        {
            if (tempStatus.Kind != NameKind.Absent
                || checkpoint.Identity != record.TempIdentity)
            {
                return false;
            }
            return TryDeleteExact(context, leaseName, lease, leaseIdentity);
        }

        var expectedState = record.Operation == OperationKind.Create
            ? checkpoint.Kind == ObservationKind.NotFound
            : checkpoint.IsExact(record.ExpectedRevision!.Value, record.ExpectedSha256!);
        if (!expectedState)
        {
            return false;
        }

        if (tempStatus.Kind == NameKind.Regular)
        {
            using var temp = OpenObserved(context, record.TempName, tempStatus.Identity!.Value, write: true);
            var currentTempIdentity = ValidatePrivateFile(temp);
            if (currentTempIdentity != record.TempIdentity
                || !TryDeleteExact(context, record.TempName, temp, currentTempIdentity))
            {
                return false;
            }
        }
        else if (tempStatus.Kind != NameKind.Absent)
        {
            return false;
        }

        RevalidateContext(context);
        ValidateNamedHandle(context, leaseName, lease, leaseIdentity);
        return TryDeleteExact(context, leaseName, lease, leaseIdentity);
    }

    private static CampaignCheckpointWriteKind ClassifyConditional(
        OperationKind operation,
        ReadObservation current,
        long? expectedRevision,
        string? expectedSha256)
    {
        if (operation == OperationKind.Create)
        {
            return current.Kind switch
            {
                ObservationKind.NotFound => CampaignCheckpointWriteKind.Written,
                ObservationKind.Found => CampaignCheckpointWriteKind.AlreadyPresent,
                _ => CampaignCheckpointWriteKind.Unwritable,
            };
        }

        return current.Kind switch
        {
            ObservationKind.NotFound => CampaignCheckpointWriteKind.PredecessorMissing,
            ObservationKind.Found when current.IsExact(expectedRevision!.Value, expectedSha256!) =>
                CampaignCheckpointWriteKind.Written,
            ObservationKind.Found => CampaignCheckpointWriteKind.CurrentMismatch,
            _ => CampaignCheckpointWriteKind.Unwritable,
        };
    }

    private bool CleanupBeforePublication(DirectoryContext context, ActiveLease active)
    {
        try
        {
            testHook?.Invoke("before-temp-cleanup");
            if (!TryDeleteExact(context, active.Record.TempName, active.TempHandle, active.Record.TempIdentity))
            {
                return false;
            }
            RevalidateContext(context);
            ValidateNamedHandle(context, leaseName, active.LeaseHandle, active.LeaseIdentity);
            testHook?.Invoke("before-lease-cleanup");
            return TryDeleteExact(context, leaseName, active.LeaseHandle, active.LeaseIdentity);
        }
        catch (Exception exception) when (IsBoundedFailure(exception))
        {
            return false;
        }
    }

    private bool CleanupAfterPublication(
        DirectoryContext context,
        ActiveLease active,
        ReadObservation readback)
    {
        try
        {
            if (readback.Identity != active.Record.TempIdentity
                || InspectName(context, active.Record.TempName).Kind != NameKind.Absent)
            {
                return false;
            }
            RevalidateContext(context);
            ValidateNamedHandle(context, leaseName, active.LeaseHandle, active.LeaseIdentity);
            testHook?.Invoke("before-lease-cleanup");
            return TryDeleteExact(context, leaseName, active.LeaseHandle, active.LeaseIdentity);
        }
        catch (Exception exception) when (IsBoundedFailure(exception))
        {
            return false;
        }
    }

    private bool TryCleanupPublishedCancellation(
        DirectoryContext context,
        ActiveLease active,
        ReadOnlySpan<byte> intendedBytes,
        long intendedRevision,
        string intendedSha256)
    {
        try
        {
            var readback = ReadEntry(context, checkpointName, CancellationToken.None);
            return readback.IsExact(intendedBytes, intendedRevision, intendedSha256)
                && CleanupAfterPublication(context, active, readback);
        }
        catch (Exception exception) when (IsBoundedFailure(exception))
        {
            return false;
        }
    }

    private ReadObservation ReadEntry(
        DirectoryContext context,
        string name,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RevalidateContext(context);
        var status = InspectName(context, name);
        if (status.Kind == NameKind.Absent)
        {
            return ReadObservation.NotFound();
        }
        if (status is not { Kind: NameKind.Regular, Identity: { } observedIdentity })
        {
            return ReadObservation.Invalid();
        }

        try
        {
            using var handle = OpenObserved(context, name, observedIdentity, write: false);
            var identity = ValidatePrivateFile(handle);
            if (identity != observedIdentity)
            {
                return ReadObservation.Unreadable();
            }
            var stat = ReadStat(handle);
            if (stat.Size is < 0 or > CampaignStateContract.MaximumArtifactUtf8Bytes)
            {
                return ReadObservation.Invalid();
            }

            var bytes = new byte[checked((int)stat.Size)];
            var offset = 0;
            while (offset < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = RandomAccess.Read(handle, bytes.AsSpan(offset), offset);
                if (read == 0)
                {
                    return ReadObservation.Unreadable();
                }
                offset += read;
            }
            Span<byte> extra = stackalloc byte[1];
            if (RandomAccess.Read(handle, extra, bytes.Length) != 0)
            {
                return ReadObservation.Unreadable();
            }

            cancellationToken.ThrowIfCancellationRequested();
            var after = ValidatePrivateFile(handle);
            RevalidateContext(context);
            var rebound = InspectName(context, name);
            if (after != identity
                || rebound.Kind != NameKind.Regular
                || rebound.Identity != identity
                || ReadStat(handle).Size != bytes.Length)
            {
                return ReadObservation.Unreadable();
            }

            var parsed = CampaignStateJson.Parse(bytes);
            if (!parsed.IsValid
                || parsed.Artifact is null
                || !parsed.Artifact.ExactUtf8Json.AsSpan().SequenceEqual(bytes))
            {
                return ReadObservation.Invalid();
            }
            var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(parsed.Artifact.Sha256, digest, StringComparison.Ordinal))
            {
                return ReadObservation.Invalid();
            }
            return ReadObservation.Found(bytes, parsed.Artifact.CheckpointRevision, digest, identity);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (StoreFault fault)
        {
            return fault.Kind == StoreFaultKind.Invalid
                ? ReadObservation.Invalid()
                : ReadObservation.Unreadable();
        }
        catch (Exception exception) when (IsBoundedFailure(exception))
        {
            return ReadObservation.Unreadable();
        }
    }

    private DirectoryContext OpenContext()
    {
        try
        {
            using var repository = OpenDirectoryChain(repositoryRoot);
            var state = OpenDirectoryChain(stateDirectoryPath);
            try
            {
                RevalidateDirectoryBinding(repository);
                if (state.AncestorIdentities.Contains(repository.DirectoryIdentity))
                {
                    throw InvalidFault();
                }
                ValidatePrivateDirectory(state.DirectoryHandle);
                RevalidateContext(state);
                return state;
            }
            catch
            {
                state.Dispose();
                throw;
            }
        }
        catch (StoreFault)
        {
            throw;
        }
        catch (Exception exception) when (IsBoundedFailure(exception))
        {
            throw UnreadableFault();
        }
    }

    private static DirectoryContext OpenDirectoryChain(string path)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var rootPath = Path.GetPathRoot(fullPath) ?? throw UnreadableFault();
        var rootDescriptor = Open(rootPath, OpenReadOnly | OpenDirectory | OpenNoFollow | OpenCloseOnExec, 0);
        if (rootDescriptor < 0)
        {
            throw UnreadableFault();
        }

        var current = new SafeFileHandle((nint)rootDescriptor, ownsHandle: true);
        var currentIdentity = ReadIdentity(current);
        var ancestors = new HashSet<FileIdentity> { currentIdentity };
        SafeFileHandle? parent = null;
        FileIdentity parentIdentity = default;
        var finalName = string.Empty;
        try
        {
            var relative = fullPath[rootPath.Length..];
            var segments = relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                throw InvalidFault();
            }
            foreach (var segment in segments)
            {
                var observed = InspectAt(current, segment);
                if (observed.Kind != NameKind.Directory)
                {
                    throw observed.Kind == NameKind.Absent ? UnreadableFault() : InvalidFault();
                }
                var descriptor = OpenAt(
                    FileDescriptor(current),
                    segment,
                    OpenReadOnly | OpenDirectory | OpenNoFollow | OpenCloseOnExec,
                    0);
                if (descriptor < 0)
                {
                    throw UnreadableFault();
                }
                var next = new SafeFileHandle((nint)descriptor, ownsHandle: true);
                var nextIdentity = ReadIdentity(next);
                if (nextIdentity != observed.Identity)
                {
                    next.Dispose();
                    throw UnreadableFault();
                }
                ancestors.Add(nextIdentity);
                parent?.Dispose();
                parent = current;
                parentIdentity = currentIdentity;
                current = next;
                currentIdentity = nextIdentity;
                finalName = segment;
            }
            return new DirectoryContext(
                parent ?? throw InvalidFault(),
                parentIdentity,
                current,
                currentIdentity,
                finalName,
                ancestors);
        }
        catch
        {
            parent?.Dispose();
            current.Dispose();
            throw;
        }
    }

    private static void RevalidateContext(DirectoryContext context)
    {
        RevalidateDirectoryBinding(context);
        ValidatePrivateDirectory(context.DirectoryHandle);
    }

    private static void RevalidateDirectoryBinding(DirectoryContext context)
    {
        if (ReadIdentity(context.ParentHandle) != context.ParentIdentity
            || ReadIdentity(context.DirectoryHandle) != context.DirectoryIdentity
            || InspectAt(context.ParentHandle, context.DirectoryName) is not { Kind: NameKind.Directory } rebound
            || rebound.Identity != context.DirectoryIdentity)
        {
            throw UnreadableFault();
        }
    }

    private static NameStatus InspectName(DirectoryContext context, string name) =>
        InspectAt(context.DirectoryHandle, name);

    private static NameStatus InspectAt(SafeFileHandle directory, string name)
    {
        if (FStatAt(FileDescriptor(directory), name, out var stat, AtSymlinkNoFollow) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == NotFound)
            {
                return NameStatus.Absent();
            }
            throw UnreadableFault();
        }

        var identity = Identity(stat, ReadMountIdentityForName(directory, name));
        return (stat.Mode & FileTypeMask) switch
        {
            RegularFile => NameStatus.Regular(identity),
            DirectoryFile => NameStatus.Directory(identity),
            _ => NameStatus.Other(identity),
        };
    }

    private static SafeFileHandle OpenObserved(
        DirectoryContext context,
        string name,
        FileIdentity observedIdentity,
        bool write)
    {
        var descriptor = OpenAt(
            FileDescriptor(context.DirectoryHandle),
            name,
            (write ? OpenReadWrite : OpenReadOnly) | OpenNonBlocking | OpenNoFollow | OpenCloseOnExec,
            0);
        if (descriptor < 0)
        {
            throw UnreadableFault();
        }
        var handle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
        try
        {
            if (ReadIdentity(handle) != observedIdentity)
            {
                throw UnreadableFault();
            }
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static FileIdentity ValidatePrivateDirectory(SafeFileHandle handle)
    {
        var stat = ReadStat(handle);
        if ((stat.Mode & FileTypeMask) != DirectoryFile
            || (stat.Mode & 0xFFF) != OwnerDirectoryMode
            || stat.UserId != GetEffectiveUserId())
        {
            throw InvalidFault();
        }
        RequireNoAcl(handle, includeDefault: true);
        return Identity(stat, ReadMountIdentity(handle));
    }

    private static FileIdentity ValidatePrivateFile(SafeFileHandle handle)
    {
        var stat = ReadStat(handle);
        if ((stat.Mode & FileTypeMask) != RegularFile
            || (stat.Mode & 0xFFF) != OwnerFileMode
            || stat.UserId != GetEffectiveUserId()
            || stat.LinkCount != 1)
        {
            throw InvalidFault();
        }
        RequireNoAcl(handle, includeDefault: false);
        return Identity(stat, ReadMountIdentity(handle));
    }

    private static void ValidateNamedHandle(
        DirectoryContext context,
        string name,
        SafeFileHandle handle,
        FileIdentity expected)
    {
        var current = ValidatePrivateFile(handle);
        var named = InspectName(context, name);
        if (current != expected || named.Kind != NameKind.Regular || named.Identity != expected)
        {
            throw UnreadableFault();
        }
    }

    private static bool TryDeleteExact(
        DirectoryContext context,
        string name,
        SafeFileHandle handle,
        FileIdentity expected)
    {
        RevalidateContext(context);
        ValidateNamedHandle(context, name, handle, expected);
        return UnlinkAt(FileDescriptor(context.DirectoryHandle), name, 0) == 0;
    }

    private static void RequireNoAcl(SafeFileHandle handle, bool includeDefault)
    {
        RequireNoAcl(handle, "system.posix_acl_access");
        if (includeDefault)
        {
            RequireNoAcl(handle, "system.posix_acl_default");
        }
    }

    private static void RequireNoAcl(SafeFileHandle handle, string name)
    {
        var result = GetExtendedAttribute(FileDescriptor(handle), name, IntPtr.Zero, 0);
        if (result >= 0)
        {
            throw InvalidFault();
        }
        var error = Marshal.GetLastPInvokeError();
        if (error != NoData)
        {
            throw UnreadableFault();
        }
    }

    private static LinuxStat ReadStat(SafeFileHandle handle)
    {
        if (FStat(FileDescriptor(handle), out var stat) != 0)
        {
            throw UnreadableFault();
        }
        return stat;
    }

    private static FileIdentity ReadIdentity(SafeFileHandle handle) =>
        Identity(ReadStat(handle), ReadMountIdentity(handle));

    private static FileIdentity Identity(LinuxStat stat, ulong mountIdentity) =>
        new(stat.Device, stat.Inode, mountIdentity);

    private static ulong ReadMountIdentityForName(SafeFileHandle directory, string name)
    {
        var descriptor = OpenAt(
            FileDescriptor(directory),
            name,
            0x200000 | OpenNoFollow | OpenCloseOnExec, // O_PATH
            0);
        if (descriptor < 0)
        {
            throw UnreadableFault();
        }
        using var handle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
        return ReadMountIdentity(handle);
    }

    private static ulong ReadMountIdentity(SafeFileHandle handle)
    {
        const string prefix = "mnt_id:\t";
        try
        {
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
        }
        catch (Exception exception) when (IsBoundedFailure(exception))
        {
            throw UnreadableFault();
        }
        throw UnreadableFault();
    }

    private static byte[] ReadExactBytes(SafeFileHandle handle, int maximum)
    {
        var stat = ReadStat(handle);
        if (stat.Size is < 1 || stat.Size > maximum)
        {
            return [];
        }
        var bytes = new byte[checked((int)stat.Size)];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var count = RandomAccess.Read(handle, bytes.AsSpan(offset), offset);
            if (count == 0)
            {
                return [];
            }
            offset += count;
        }
        Span<byte> extra = stackalloc byte[1];
        return RandomAccess.Read(handle, extra, bytes.Length) == 0
            ? bytes
            : [];
    }

    private static void WriteExact(SafeFileHandle handle, ReadOnlySpan<byte> bytes)
    {
        if (SetLength(FileDescriptor(handle), 0) != 0)
        {
            throw UnreadableFault();
        }
        var offset = 0;
        while (offset < bytes.Length)
        {
            RandomAccess.Write(handle, bytes[offset..], offset);
            offset = bytes.Length;
        }
    }

    private static bool TryValidateIntended(
        ReadOnlyMemory<byte> bytes,
        long revision,
        string sha256)
    {
        if (bytes.Length > CampaignStateContract.MaximumArtifactUtf8Bytes
            || !IsRevision(revision)
            || !IsSha256(sha256))
        {
            return false;
        }
        var parsed = CampaignStateJson.Parse(bytes);
        return parsed.IsValid
            && parsed.Artifact is not null
            && parsed.Artifact.CheckpointRevision == revision
            && parsed.Artifact.ExactUtf8Json.AsSpan().SequenceEqual(bytes.Span)
            && string.Equals(parsed.Artifact.Sha256, sha256, StringComparison.Ordinal)
            && string.Equals(
                Convert.ToHexString(SHA256.HashData(bytes.Span)).ToLowerInvariant(),
                sha256,
                StringComparison.Ordinal);
    }

    private static bool IsRevision(long? value) =>
        value is >= 0 and <= CampaignStateContract.MaximumObservation;

    private static bool IsSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsToken(string value) =>
        value.Length == 32
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsSupportedPlatform() =>
        OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

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

    private static CampaignCheckpointWriteResult Unwritable() =>
        new(CampaignCheckpointWriteKind.Unwritable);

    private static bool IsBoundedFailure(Exception exception) => exception is
        IOException
        or UnauthorizedAccessException
        or InvalidDataException
        or NotSupportedException
        or ArgumentException
        or OverflowException
        or StoreFault;

    private static StoreFault InvalidFault() => new(StoreFaultKind.Invalid);
    private static StoreFault UnreadableFault() => new(StoreFaultKind.Unreadable);

    private static int FileDescriptor(SafeFileHandle handle) =>
        checked((int)handle.DangerousGetHandle());

    private enum OperationKind
    {
        Create,
        Replace,
    }

    private enum StoreFaultKind
    {
        Invalid,
        Unreadable,
    }

    private enum ObservationKind
    {
        NotFound,
        Found,
        Invalid,
        Unreadable,
    }

    private enum NameKind
    {
        Absent,
        Regular,
        Directory,
        Other,
    }

    private sealed class StoreFault(StoreFaultKind kind) : IOException("checkpoint.store.failure")
    {
        internal StoreFaultKind Kind { get; } = kind;
    }

    private sealed class ActiveLease(
        SafeFileHandle leaseHandle,
        FileIdentity leaseIdentity,
        SafeFileHandle tempHandle,
        LeaseRecord record) : IDisposable
    {
        internal SafeFileHandle LeaseHandle { get; } = leaseHandle;
        internal FileIdentity LeaseIdentity { get; } = leaseIdentity;
        internal SafeFileHandle TempHandle { get; } = tempHandle;
        internal LeaseRecord Record { get; } = record;

        public void Dispose()
        {
            TempHandle.Dispose();
            LeaseHandle.Dispose();
        }
    }

    private sealed class DirectoryContext(
        SafeFileHandle parentHandle,
        FileIdentity parentIdentity,
        SafeFileHandle directoryHandle,
        FileIdentity directoryIdentity,
        string directoryName,
        HashSet<FileIdentity> ancestorIdentities) : IDisposable
    {
        internal SafeFileHandle ParentHandle { get; } = parentHandle;
        internal FileIdentity ParentIdentity { get; } = parentIdentity;
        internal SafeFileHandle DirectoryHandle { get; } = directoryHandle;
        internal FileIdentity DirectoryIdentity { get; } = directoryIdentity;
        internal string DirectoryName { get; } = directoryName;
        internal HashSet<FileIdentity> AncestorIdentities { get; } = ancestorIdentities;

        public void Dispose()
        {
            DirectoryHandle.Dispose();
            ParentHandle.Dispose();
        }
    }

    private readonly record struct FileIdentity(ulong Device, ulong Inode, ulong Mount);

    private readonly record struct NameStatus(NameKind Kind, FileIdentity? Identity)
    {
        internal static NameStatus Absent() => new(NameKind.Absent, null);
        internal static NameStatus Regular(FileIdentity identity) => new(NameKind.Regular, identity);
        internal static NameStatus Directory(FileIdentity identity) => new(NameKind.Directory, identity);
        internal static NameStatus Other(FileIdentity identity) => new(NameKind.Other, identity);
    }

    private sealed record ReadObservation(
        ObservationKind Kind,
        byte[]? Bytes,
        long? Revision,
        string? Sha256,
        FileIdentity? Identity)
    {
        internal static ReadObservation NotFound() => new(ObservationKind.NotFound, null, null, null, null);
        internal static ReadObservation Invalid() => new(ObservationKind.Invalid, null, null, null, null);
        internal static ReadObservation Unreadable() => new(ObservationKind.Unreadable, null, null, null, null);
        internal static ReadObservation Found(byte[] bytes, long revision, string sha256, FileIdentity identity) =>
            new(ObservationKind.Found, bytes, revision, sha256, identity);

        internal bool IsExact(long revision, string sha256) =>
            Kind == ObservationKind.Found
            && Revision == revision
            && string.Equals(Sha256, sha256, StringComparison.Ordinal);

        internal bool IsExact(ReadOnlySpan<byte> bytes, long revision, string sha256) =>
            IsExact(revision, sha256) && Bytes.AsSpan().SequenceEqual(bytes);

        internal CampaignCheckpointReadResult ToPublicResult() => Kind switch
        {
            ObservationKind.NotFound => CampaignCheckpointReadResult.NotFound(),
            ObservationKind.Found => CampaignCheckpointReadResult.Found(Bytes, Revision!.Value, Sha256!),
            ObservationKind.Invalid => CampaignCheckpointReadResult.Invalid(),
            _ => CampaignCheckpointReadResult.Unreadable(),
        };
    }

    private sealed record LeaseRecord(
        OperationKind Operation,
        long? ExpectedRevision,
        string? ExpectedSha256,
        long IntendedRevision,
        string IntendedSha256,
        string Token,
        string TempName,
        FileIdentity TempIdentity)
    {
        internal byte[] Encode() => Encoding.ASCII.GetBytes(string.Join('\n',
            LeaseVersion,
            Operation == OperationKind.Create ? "operation=create" : "operation=replace",
            $"expected-revision={ExpectedRevision?.ToString(CultureInfo.InvariantCulture) ?? "-"}",
            $"expected-sha256={ExpectedSha256 ?? "-"}",
            $"intended-revision={IntendedRevision.ToString(CultureInfo.InvariantCulture)}",
            $"intended-sha256={IntendedSha256}",
            $"token={Token}",
            $"temp={TempName}",
            $"temp-device={TempIdentity.Device.ToString(CultureInfo.InvariantCulture)}",
            $"temp-inode={TempIdentity.Inode.ToString(CultureInfo.InvariantCulture)}",
            $"temp-mount={TempIdentity.Mount.ToString(CultureInfo.InvariantCulture)}",
            string.Empty));

        internal static bool TryParse(byte[] bytes, string checkpointName, out LeaseRecord record)
        {
            record = null!;
            if (bytes.Length is 0 or > MaximumLeaseBytes || bytes.Any(value => value is < 0x20 and not 0x0A or > 0x7E))
            {
                return false;
            }
            var text = Encoding.ASCII.GetString(bytes);
            var lines = text.Split('\n');
            if (lines.Length != 12 || lines[0] != LeaseVersion || lines[^1].Length != 0)
            {
                return false;
            }
            var operation = lines[1] switch
            {
                "operation=create" => OperationKind.Create,
                "operation=replace" => OperationKind.Replace,
                _ => (OperationKind?)null,
            };
            if (operation is null
                || !TryValue(lines[2], "expected-revision=", out var expectedRevisionText)
                || !TryValue(lines[3], "expected-sha256=", out var expectedSha256Text)
                || !TryValue(lines[4], "intended-revision=", out var intendedRevisionText)
                || !TryValue(lines[5], "intended-sha256=", out var intendedSha256)
                || !TryValue(lines[6], "token=", out var token)
                || !TryValue(lines[7], "temp=", out var tempName)
                || !TryValue(lines[8], "temp-device=", out var deviceText)
                || !TryValue(lines[9], "temp-inode=", out var inodeText)
                || !TryValue(lines[10], "temp-mount=", out var mountText)
                || !long.TryParse(intendedRevisionText, NumberStyles.None, CultureInfo.InvariantCulture, out var intendedRevision)
                || !IsRevision(intendedRevision)
                || !IsSha256(intendedSha256)
                || !IsToken(token)
                || tempName != $".{checkpointName}.contractscribe-checkpoint-{intendedRevisionText}-{token}.tmp"
                || !ulong.TryParse(deviceText, NumberStyles.None, CultureInfo.InvariantCulture, out var device)
                || !ulong.TryParse(inodeText, NumberStyles.None, CultureInfo.InvariantCulture, out var inode)
                || !ulong.TryParse(mountText, NumberStyles.None, CultureInfo.InvariantCulture, out var mount))
            {
                return false;
            }

            long? expectedRevision = null;
            string? expectedSha256 = null;
            if (operation == OperationKind.Create)
            {
                if (expectedRevisionText != "-" || expectedSha256Text != "-")
                {
                    return false;
                }
            }
            else if (!long.TryParse(expectedRevisionText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedExpected)
                || !IsRevision(parsedExpected)
                || !IsSha256(expectedSha256Text))
            {
                return false;
            }
            else
            {
                expectedRevision = parsedExpected;
                expectedSha256 = expectedSha256Text;
            }

            record = new LeaseRecord(
                operation.Value,
                expectedRevision,
                expectedSha256,
                intendedRevision,
                intendedSha256,
                token,
                tempName,
                new FileIdentity(device, inode, mount));
            return record.Encode().AsSpan().SequenceEqual(bytes);
        }

        private static bool TryValue(string line, string prefix, out string value)
        {
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
            {
                value = string.Empty;
                return false;
            }
            value = line[prefix.Length..];
            return value.Length > 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxStat
    {
        internal ulong Device;
        internal ulong Inode;
        internal ulong LinkCount;
        internal uint Mode;
        internal uint UserId;
        internal uint GroupId;
        internal int Padding;
        internal ulong RawDevice;
        internal long Size;
        internal long BlockSize;
        internal long Blocks;
        internal long AccessTimeSeconds;
        internal ulong AccessTimeNanoseconds;
        internal long ModificationTimeSeconds;
        internal ulong ModificationTimeNanoseconds;
        internal long ChangeTimeSeconds;
        internal ulong ChangeTimeNanoseconds;
        internal long Reserved0;
        internal long Reserved1;
        internal long Reserved2;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags, uint mode);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAt(int directory, string path, int flags, uint mode);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int FStat(int fileDescriptor, out LinuxStat value);

    [DllImport("libc", EntryPoint = "fstatat", SetLastError = true)]
    private static extern int FStatAt(int directory, string path, out LinuxStat value, int flags);

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(int fileDescriptor, int operation);

    [DllImport("libc", EntryPoint = "ftruncate", SetLastError = true)]
    private static extern int SetLength(int fileDescriptor, long length);

    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    private static extern int RenameAt2(
        int oldDirectory,
        string oldPath,
        int newDirectory,
        string newPath,
        uint flags);

    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
    private static extern int UnlinkAt(int directory, string path, int flags);

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    [DllImport("libc", EntryPoint = "fgetxattr", SetLastError = true)]
    private static extern nint GetExtendedAttribute(
        int fileDescriptor,
        string name,
        IntPtr value,
        nuint size);
}
