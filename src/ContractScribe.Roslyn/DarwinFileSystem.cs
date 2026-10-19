using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

// The patch engine compiles this private implementation into its own namespace.
// This preserves its opaque Roslyn boundary without a friend assembly or public API.
#if CONTRACTSCRIBE_PATCHING
namespace ContractScribe.Patching.CandidateWorkspace;
#else
namespace ContractScribe.Roslyn;
#endif

internal enum DarwinFileSystemFailure
{
    Unsafe,
    Stale,
    HardLink,
    Unavailable,
}

internal sealed class DarwinFileSystemException(DarwinFileSystemFailure failure)
    : IOException("The native filesystem authority could not be established.")
{
    internal DarwinFileSystemFailure Failure { get; } = failure;
}

/// <summary>Public Darwin operations for the qualified arm64 local developer path.</summary>
internal static class DarwinFileSystem
{
    internal const int ReadOnly = 0;
    internal const int ReadWrite = 2;
    internal const int NonBlocking = 4;
    internal const int Create = 0x200;
    internal const int Exclusive = 0x800;
    internal const int Sync = 0x80;
    internal const int Directory = 0x100000;
    internal const int CloseOnExec = 0x1000000;
    internal const int NoFollowAny = 0x20000000;
    internal const int Beneath = 0x1000;
    internal const int EventOnly = 0x8000;
    internal const int AtNoFollow = 0x20;
    internal const int AtRealDevice = 0x200;
    private const int AtDescriptorOnly = 0x400;
    internal const int AtRemoveDirectory = 0x80;
    internal const uint IgnoreOwnership = 0x200000;
    private const uint CommonAttributes = 0xa604000f;
    private const uint Firmlink = 0x800000;
    private const uint LocalMount = 0x1000;
    private const int DirectoryFlags = ReadOnly | NonBlocking | Directory | CloseOnExec | NoFollowAny;
    private const int RelativeFlags = NonBlocking | CloseOnExec | NoFollowAny | Beneath;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    [SupportedOSPlatformGuard("macos")]
    internal static bool IsSupported => OperatingSystem.IsMacOS()
        && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

    internal sealed record Entry(
        string Name, uint Device, ulong FileSystem, ulong Inode, ulong ParentInode,
        uint Kind, uint Flags, uint MountStatus);

    internal sealed record Ancestry(string VolumeUuid, ImmutableArray<ulong> Inodes)
    {
        internal string Projection => "/" + VolumeUuid + "/" + string.Join(
            '/', Inodes.Select(inode => inode.ToString(CultureInfo.InvariantCulture)));
    }

    internal static bool IsSameOrDescendant(Ancestry ancestor, Ancestry candidate) =>
        ancestor.VolumeUuid == candidate.VolumeUuid
        && candidate.Inodes.AsSpan().StartsWith(ancestor.Inodes.AsSpan());

    internal static bool Overlaps(Ancestry left, Ancestry right) =>
        IsSameOrDescendant(left, right) || IsSameOrDescendant(right, left);

    internal static SafeFileHandle OpenDirectory(string path)
    {
        RequirePlatform();
        RequireUtf8(path);
        return RequireHandle(OpenReadOnly(path, DirectoryFlags));
    }

    internal static SafeFileHandle OpenAt(SafeFileHandle parent, string name, int flags, uint mode = 0)
    {
        RequirePlatform();
        RequireLeaf(name);
        // Apple's arm64 ABI places variadic arguments on the stack. The six/five
        // unused register arguments make the mode the first stack argument.
        var descriptor = (flags & Create) != 0
            ? OpenAtWithMode(parent, name, flags, 0, 0, 0, 0, 0, mode)
            : OpenAtReadOnly(parent, name, flags);
        return RequireHandle(descriptor);
    }

    internal static int CreateAt(SafeFileHandle parent, string name, int flags, uint mode)
    {
        RequirePlatform();
        RequireLeaf(name);
        return OpenAtWithMode(parent, name, flags, 0, 0, 0, 0, 0, mode);
    }

    internal static SafeFileHandle Duplicate(SafeFileHandle handle)
    {
        RequirePlatform();
        return RequireHandle(FcntlStackArgument(handle, 67, 0, 0, 0, 0, 0, 0, 0));
    }

    internal static Stat ReadStat(SafeFileHandle handle)
    {
        RequirePlatform();
        // Ordinary fstat may project APFS volume-group device/inode values.
        // FDONLY keeps the actual vnode; REALDEV requests its physical IDs.
        if (FStatAt(handle, string.Empty, out var stat, AtDescriptorOnly | AtRealDevice) != 0) throw Failure(DarwinFileSystemFailure.Unavailable);
        return stat;
    }

    internal static int ReadStatAt(SafeFileHandle parent, string name, out Stat stat)
    {
        RequirePlatform();
        RequireLeaf(name);
        return FStatAt(parent, name, out stat, AtNoFollow | AtRealDevice);
    }

    internal static int ReadPathStat(string path, out Stat stat)
    {
        RequirePlatform();
        RequireUtf8(path);
        return FStatPath(-2, path, out stat, AtNoFollow | AtRealDevice);
    }

    internal static StatFs ReadFileSystem(SafeFileHandle handle)
    {
        RequirePlatform();
        if (FStatFs(handle, out var fs) != 0) throw Failure(DarwinFileSystemFailure.Unavailable);
        return fs;
    }

    internal static string PhysicalPath(SafeFileHandle handle)
    {
        RequirePlatform();
        // A fixed-arity API avoids fcntl's variadic pointer ABI entirely.
        var attributes = new Attributes { Count = 5, Common = 0x80000000, Extended = 0x20 };
        var bytes = new byte[2048];
        if (FGetAttributes(handle, ref attributes, bytes, (nuint)bytes.Length, 0x28) != 0)
            throw Failure(DarwinFileSystemFailure.Unavailable);
        var length = U32(bytes, 0);
        var offset = 24L + BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24));
        var textLength = U32(bytes, 28);
        if (length is < 32 or > 2048 || (U32(bytes, 20) & 0x20) == 0
            || offset < 32 || textLength is 0 or > 1024 || offset + textLength > length
            || bytes[(int)(offset + textLength - 1)] != 0)
            throw Failure();
        return Decode(bytes.AsSpan((int)offset, (int)textLength));
    }

    internal static Entry ReadEntry(SafeFileHandle handle)
    {
        RequirePlatform();
        var attributes = new Attributes { Count = 5, Common = CommonAttributes };
        var bytes = new byte[2048];
        if (FGetAttributes(handle, ref attributes, bytes, (nuint)bytes.Length, 0x208) != 0)
            throw Failure(DarwinFileSystemFailure.Unavailable);
        var length = U32(bytes, 0);
        if (length is < 72 or > 2048) throw Failure();
        return ParseEntry(bytes.AsSpan(0, (int)length), directoryFields: false);
    }

    internal static uint ReadMountStatus(SafeFileHandle directory)
    {
        RequirePlatform();
        var attributes = new Attributes { Count = 5, Common = 0x80000000, Directory = 4 };
        var bytes = new byte[28];
        if (FGetAttributes(directory, ref attributes, bytes, (nuint)bytes.Length, 0) != 0
            || U32(bytes, 0) != bytes.Length || (U32(bytes, 12) & 4) == 0)
            throw Failure(DarwinFileSystemFailure.Unavailable);
        return U32(bytes, 24);
    }

    internal static void RequireRelativeDirectoryMount(SafeFileHandle directory)
    {
        if (ReadMountStatus(directory) != 0) throw Failure();
    }

    internal static void Qualify(SafeFileHandle directory, bool requireOwnership = false)
    {
        var stat = ReadStat(directory);
        var fs = ReadFileSystem(directory);
        var metadata = ReadEntry(directory);
        if ((stat.Mode & 0xf000) != 0x4000 || metadata.Kind != 2
            || DecodeTerminated(fs.Kind) != "apfs" || (fs.Flags & LocalMount) == 0
            || stat.Inode != metadata.Inode || unchecked((uint)stat.Device) != metadata.Device
            || fs.FileSystem != metadata.FileSystem)
            throw Failure(DarwinFileSystemFailure.Unavailable);
        if (requireOwnership && (fs.Flags & IgnoreOwnership) != 0) throw Failure();
        var attributes = new Attributes { Count = 5, Common = 0x80000000, Volume = 0x80020000 };
        var bytes = new byte[56];
        if (FGetAttributes(directory, ref attributes, bytes, (nuint)bytes.Length, 0x200) != 0
            || U32(bytes, 0) != bytes.Length || (U32(bytes, 8) & 0x20000) == 0)
            throw Failure(DarwinFileSystemFailure.Unavailable);
        const uint required = 1 | 0x4000 | 0x20000 | 0x40000;
        if ((U32(bytes, 40) & required) != required
            || (U32(bytes, 24) & required) != (required & ~0x40000u))
            throw Failure(DarwinFileSystemFailure.Unavailable);
    }

    internal static void RequirePrivate(SafeFileHandle handle, bool directory)
    {
        var stat = ReadStat(handle);
        if ((ReadFileSystem(handle).Flags & IgnoreOwnership) != 0
            || (stat.Mode & 0xf000) != (directory ? 0x4000 : 0x8000)
            || (stat.Mode & 0xfff) != (directory ? 0x1c0 : 0x180)
            || stat.UserId != GetEffectiveUserId() || (!directory && stat.LinkCount != 1))
            throw Failure();
        RequireNoAcl(handle);
    }

    internal static void RequireNoAcl(SafeFileHandle handle)
    {
        RequirePlatform();
        var acl = GetAcl(handle, 0x100);
        var error = Marshal.GetLastPInvokeError();
        if (acl != IntPtr.Zero)
        {
            _ = FreeAcl(acl);
            throw Failure();
        }
        if (error != 2) throw Failure(DarwinFileSystemFailure.Unavailable);
    }

    internal static Ancestry ReadAncestry(
        SafeFileHandle directory, CancellationToken cancellationToken = default, Action? checkpoint = null)
    {
        Qualify(directory);
        var fs = ReadFileSystem(directory);
        var domain = ReadVolumeUuid(directory);
        using var volumeRoot = OpenDirectory(DecodeTerminated(fs.MountPoint));
        var root = ReadEntry(volumeRoot);
        var selected = ReadEntry(directory);
        if (root.Device != selected.Device || root.FileSystem != selected.FileSystem
            || ReadVolumeUuid(volumeRoot) != domain || ReadMountStatus(volumeRoot) != 1)
            throw Failure();
        var retained = new List<(SafeFileHandle Handle, Entry Entry, string Path)>();
        try
        {
            var current = Duplicate(directory);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                checkpoint?.Invoke();
                Entry entry;
                string path;
                try
                {
                    entry = ReadEntry(current);
                    path = PhysicalPath(current);
                    if (retained.Count >= 256 || entry.Kind != 2
                        || entry.Device != selected.Device || entry.FileSystem != selected.FileSystem
                        || ReadVolumeUuid(current) != domain
                        || retained.Any(node => node.Entry.Inode == entry.Inode)
                        || (retained.Count > 0 && retained[^1].Entry.ParentInode != entry.Inode)
                        || ReadMountStatus(current) != (entry.Inode == root.Inode ? 1u : 0u))
                        throw Failure();
                }
                catch { current.Dispose(); throw; }
                retained.Add((current, entry, path));
                if (entry.Inode == root.Inode) break;
                current = OpenDirectory(Path.GetDirectoryName(path) ?? throw Failure());
            }
            foreach (var node in retained)
            {
                cancellationToken.ThrowIfCancellationRequested();
                checkpoint?.Invoke();
                if (!SameObject(ReadEntry(node.Handle), node.Entry)
                    || PhysicalPath(node.Handle) != node.Path || ReadVolumeUuid(node.Handle) != domain)
                    throw Failure(DarwinFileSystemFailure.Stale);
            }
            return new Ancestry(domain, retained.Select(node => node.Entry.Inode).Reverse().ToImmutableArray());
        }
        finally
        {
            foreach (var node in retained.AsEnumerable().Reverse()) node.Handle.Dispose();
        }
    }

    internal sealed class RootLease : IDisposable
    {
        private sealed record Pin(SafeFileHandle Handle, Entry Identity, string Path, uint Mount, Pin? Parent, string? Name);
        private readonly List<Pin> pins = [];
        private readonly Dictionary<SafeFileHandle, Pin> bindings = [];
        // A stream may outlive its traversal wrapper. Its original ancestors,
        // including those of explicit first-pass candidate files, stay pinned.
        private readonly Dictionary<SafeFileHandle, Pin> fileBindings = [];
        private readonly Dictionary<string, Entry> observations = new(StringComparer.Ordinal);
        private readonly string selectedPath;
        private readonly Pin root;
        private readonly string rootVolumeUuid;
        private readonly bool requirePrivateDirectories;
        private bool disposed;

        internal RootLease(string path, bool requirePrivateDirectories = false)
        {
            this.requirePrivateDirectories = requirePrivateDirectories;
            selectedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var handle = DarwinFileSystem.OpenDirectory(selectedPath);
            try
            {
                Qualify(handle, requireOwnership: requirePrivateDirectories);
                if (requirePrivateDirectories) RequirePrivate(handle, directory: true);
                rootVolumeUuid = ReadAncestry(handle).VolumeUuid;
                var mount = ReadMountStatus(handle);
                if (mount > 1) throw Failure();
                root = new Pin(handle, ReadEntry(handle), PhysicalPath(handle), mount, null, null);
                pins.Add(root);
                observations.Add(root.Path, root.Identity);
            }
            catch { handle.Dispose(); throw; }
        }

        internal SafeFileHandle OpenRoot()
        {
            Revalidate();
            var handle = Duplicate(root.Handle);
            bindings.Add(handle, root);
            return handle;
        }

        internal void Revalidate(CancellationToken cancellationToken = default, Action? checkpoint = null)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ReleaseCompletedBranches();
            foreach (var pin in pins)
            {
                cancellationToken.ThrowIfCancellationRequested();
                checkpoint?.Invoke();
                if (!SameObject(ReadEntry(pin.Handle), pin.Identity) || PhysicalPath(pin.Handle) != pin.Path
                    || ReadVolumeUuid(pin.Handle) != rootVolumeUuid || ReadMountStatus(pin.Handle) != pin.Mount)
                    throw Failure(DarwinFileSystemFailure.Stale);
                using var rebound = pin.Parent is null
                    ? DarwinFileSystem.OpenDirectory(selectedPath)
                    : OpenAt(pin.Parent.Handle, pin.Name!, DirectoryFlags | Beneath);
                if (!SameObject(ReadEntry(rebound), pin.Identity) || PhysicalPath(rebound) != pin.Path
                    || ReadVolumeUuid(rebound) != rootVolumeUuid || ReadMountStatus(rebound) != pin.Mount)
                    throw Failure(DarwinFileSystemFailure.Stale);
                if (requirePrivateDirectories)
                {
                    RequirePrivate(pin.Handle, directory: true);
                    RequirePrivate(rebound, directory: true);
                }
            }
        }

        private void ReleaseCompletedBranches()
        {
            foreach (var handle in bindings.Keys.Where(handle => handle.IsClosed).ToArray())
                bindings.Remove(handle);
            foreach (var handle in fileBindings.Keys.Where(handle => handle.IsClosed).ToArray())
                fileBindings.Remove(handle);
            var required = new HashSet<Pin>(ReferenceEqualityComparer.Instance) { root };
            foreach (var active in bindings.Values.Concat(fileBindings.Values))
                for (Pin? pin = active; pin is not null && required.Add(pin); pin = pin.Parent) { }
            for (var index = pins.Count - 1; index >= 0; index--)
            {
                if (required.Contains(pins[index])) continue;
                pins[index].Handle.Dispose();
                pins.RemoveAt(index);
            }
        }

        internal void RetainFileAncestors(SafeFileHandle file, SafeFileHandle parent)
        {
            var parentPin = Binding(parent);
            Revalidate();
            var entry = ReadEntry(file);
            if (entry.Kind != 1 || entry.Device != root.Identity.Device
                || entry.FileSystem != root.Identity.FileSystem || entry.ParentInode != parentPin.Identity.Inode
                || (entry.Flags & Firmlink) != 0 || ReadStat(file).LinkCount != 1
                || PhysicalPath(file) != Path.Join(parentPin.Path, entry.Name)) throw Failure();
            if (requirePrivateDirectories) RequirePrivate(file, directory: false);
            fileBindings.Add(file, parentPin);
        }

        internal IEnumerable<Entry> Enumerate(
            SafeFileHandle directory, CancellationToken cancellationToken,
            Action? checkpoint = null, Action? chargeEntry = null)
        {
            var pin = Binding(directory);
            Revalidate(cancellationToken, checkpoint);
            // dup shares the offset; obtain and verify an independent stream.
            using var fresh = RequireHandle(OpenAtReadOnly(pin.Handle, ".", DirectoryFlags | Beneath));
            if (!SameObject(ReadEntry(fresh), pin.Identity) || PhysicalPath(fresh) != pin.Path
                || ReadMountStatus(fresh) != pin.Mount)
                throw Failure();
            var attributes = new Attributes { Count = 5, Common = CommonAttributes, Directory = 4 };
            var bytes = new byte[64 * 1024];
            while (true)
            {
                Revalidate(cancellationToken, checkpoint);
                var count = GetBulkAttributes(fresh, ref attributes, bytes, (nuint)bytes.Length, 0x208);
                if (count < 0) throw Failure(DarwinFileSystemFailure.Unavailable);
                if (count == 0) break;
                var offset = 0;
                for (var index = 0; index < count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    checkpoint?.Invoke();
                    chargeEntry?.Invoke();
                    if (offset > bytes.Length - 4) throw Failure();
                    var length = U32(bytes, offset);
                    if (length < 72 || (length & 7) != 0 || length > bytes.Length - offset) throw Failure();
                    var entry = ParseEntry(bytes.AsSpan(offset, (int)length), directoryFields: true);
                    RequireLeaf(entry.Name);
                    if (entry.Device != pin.Identity.Device || entry.FileSystem != pin.Identity.FileSystem
                        || entry.ParentInode != pin.Identity.Inode)
                        throw Failure();
                    yield return entry;
                    offset += (int)length;
                }
            }
            Revalidate(cancellationToken, checkpoint);
        }

        internal Entry? Find(
            SafeFileHandle parent, string name, CancellationToken cancellationToken,
            Action? checkpoint = null, Action? chargeEntry = null)
        {
            RequireLeaf(name);
            Entry? found = null;
            var folded = 0;
            foreach (var entry in Enumerate(parent, cancellationToken, checkpoint, chargeEntry))
            {
                if (!string.Equals(entry.Name.Normalize(NormalizationForm.FormD),
                        name.Normalize(NormalizationForm.FormD), StringComparison.OrdinalIgnoreCase)) continue;
                folded++;
                if (entry.Name == name) found = entry;
            }
            if (folded > 1 || folded == 1 && found is null) throw Failure();
            if (found is not null) return found;
            Revalidate(cancellationToken, checkpoint);
            var result = FStatAt(parent, name, out _, AtNoFollow | AtRealDevice);
            var error = Marshal.GetLastPInvokeError();
            Revalidate(cancellationToken, checkpoint);
            // The filesystem's equivalence rules may exceed .NET's folding.
            if (result == 0 || error != 2) throw Failure();
            return null;
        }

        internal SafeFileHandle OpenDirectory(
            SafeFileHandle parent, string name, CancellationToken cancellationToken,
            Action? checkpoint = null, Entry? observation = null)
        {
            var entry = observation ?? Find(parent, name, cancellationToken, checkpoint)
                ?? throw Failure(DarwinFileSystemFailure.Stale);
            var handle = OpenChild(parent, name, entry, directory: true, cancellationToken, checkpoint);
            try
            {
                var pin = pins.SingleOrDefault(candidate => candidate.Identity.Inode == entry.Inode);
                var parentPin = Binding(parent);
                if (pin is null)
                {
                    var identity = ReadEntry(handle);
                    var path = PhysicalPath(handle);
                    // Qualified APFS IDs are non-recycled. Keep the original
                    // observation without keeping a descriptor for a completed
                    // branch; reopening its path must still reject substitution.
                    if (observations.TryGetValue(path, out var original) && !SameObject(identity, original))
                        throw Failure(DarwinFileSystemFailure.Stale);
                    observations.TryAdd(path, identity);
                    pin = new Pin(Duplicate(handle), identity, path, 0, parentPin, name);
                    pins.Add(pin);
                }
                else if (pin.Parent != parentPin || pin.Name != name) throw Failure();
                bindings.Add(handle, pin);
                return handle;
            }
            catch { handle.Dispose(); throw; }
        }

        internal SafeFileHandle OpenDirectoryPath(
            string relativePath, CancellationToken cancellationToken, Action? checkpoint = null)
        {
            var current = OpenRoot();
            try
            {
                if (relativePath is "" or ".") return current;
                foreach (var segment in relativePath.Split('/'))
                {
                    var next = OpenDirectory(current, segment, cancellationToken, checkpoint);
                    current.Dispose();
                    current = next;
                }
                return current;
            }
            catch { current.Dispose(); throw; }
        }

        internal SafeFileHandle OpenFile(
            SafeFileHandle parent, string name, CancellationToken cancellationToken,
            Action? checkpoint = null, Entry? observation = null, bool writable = false) =>
            OpenChild(parent, name, observation ?? Find(parent, name, cancellationToken, checkpoint)
                ?? throw Failure(DarwinFileSystemFailure.Stale), directory: false, cancellationToken, checkpoint, writable);

        internal SafeFileHandle OpenMetadataChild(
            SafeFileHandle parent, Entry observation, CancellationToken cancellationToken,
            Action? checkpoint = null) => OpenChild(
                parent, observation.Name, observation, observation.Kind == 2, cancellationToken, checkpoint);

        private SafeFileHandle OpenChild(
            SafeFileHandle parent, string name, Entry entry, bool directory,
            CancellationToken cancellationToken, Action? checkpoint, bool writable = false)
        {
            RequireLeaf(name);
            var parentPin = Binding(parent);
            Revalidate(cancellationToken, checkpoint);
            if (entry.Name != name || entry.Kind != (directory ? 2u : 1u) || (entry.Flags & Firmlink) != 0
                || entry.MountStatus != 0 || entry.Device != root.Identity.Device
                || entry.FileSystem != root.Identity.FileSystem || entry.ParentInode != parentPin.Identity.Inode)
                throw Failure();
            var handle = OpenAt(parentPin.Handle, name, RelativeFlags | (directory ? Directory : writable ? ReadWrite : 0));
            try
            {
                // Bulk can report a returned-valid zero without a vnode. This
                // independent query of the actual opened vnode is mandatory.
                if (directory) RequireRelativeDirectoryMount(handle);
                var stat = ReadStat(handle);
                if ((stat.Mode & 0xf000) != (directory ? 0x4000 : 0x8000)) throw Failure();
                if (!directory && stat.LinkCount != 1) throw Failure(DarwinFileSystemFailure.HardLink);
                if (requirePrivateDirectories) RequirePrivate(handle, directory);
                var actual = ReadEntry(handle);
                var fs = ReadFileSystem(handle);
                if (!SameObject(actual, entry) || (actual.Flags & Firmlink) != 0
                    || fs.FileSystem != root.Identity.FileSystem || DecodeTerminated(fs.Kind) != "apfs"
                    || (stat.Mode & 0xf000) != (directory ? 0x4000 : 0x8000)
                    || stat.Inode != entry.Inode || unchecked((uint)stat.Device) != entry.Device)
                    throw Failure(DarwinFileSystemFailure.Stale);
                if (PhysicalPath(handle) != Path.Join(parentPin.Path, name)) throw Failure();
                Revalidate(cancellationToken, checkpoint);
                if (!directory) fileBindings.Add(handle, parentPin);
                return handle;
            }
            catch { handle.Dispose(); throw; }
        }

        private Pin Binding(SafeFileHandle handle)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return bindings.TryGetValue(handle, out var pin) ? pin : throw Failure();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (var pin in pins.AsEnumerable().Reverse()) pin.Handle.Dispose();
            pins.Clear();
            bindings.Clear();
            fileBindings.Clear();
            observations.Clear();
        }
    }

    internal static string ReadVolumeUuid(SafeFileHandle handle)
    {
        RequirePlatform();
        var attributes = new Attributes { Count = 5, Common = 0x80000000, Volume = 0x80040000 };
        var bytes = new byte[40];
        if (FGetAttributes(handle, ref attributes, bytes, (nuint)bytes.Length, 0x200) != 0
            || U32(bytes, 0) != bytes.Length || (U32(bytes, 8) & 0x40000) == 0
            || bytes.AsSpan(24).IndexOfAnyExcept((byte)0) < 0)
            throw Failure(DarwinFileSystemFailure.Unavailable);
        return Convert.ToHexString(bytes.AsSpan(24));
    }

    private static Entry ParseEntry(ReadOnlySpan<byte> bytes, bool directoryFields)
    {
        if (bytes.Length < 72 || U32(bytes, 0) != bytes.Length
            || (U32(bytes, 4) & CommonAttributes) != CommonAttributes || U32(bytes, 24) != 0)
            throw Failure();
        var kind = U32(bytes, 48);
        var fixedLength = directoryFields && kind == 2 ? 76 : 72;
        if (bytes.Length < fixedLength) throw Failure();
        var offset = 28L + BinaryPrimitives.ReadInt32LittleEndian(bytes[28..]);
        var length = U32(bytes, 32);
        if (offset < fixedLength || length is 0 or > 1024 || offset + length > bytes.Length) throw Failure();
        var mount = 0u;
        if (directoryFields && kind == 2)
        {
            if ((U32(bytes, 12) & 4) == 0) throw Failure();
            mount = U32(bytes, 72);
        }
        var entry = new Entry(Decode(bytes.Slice((int)offset, (int)length)), U32(bytes, 36),
            U64(bytes, 40), U64(bytes, 56), U64(bytes, 64), kind, U32(bytes, 52), mount);
        if (entry.Inode == 0 || entry.ParentInode == 0) throw Failure();
        return entry;
    }

    private static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0 || bytes[^1] != 0 || bytes[..^1].Contains((byte)0)) throw Failure();
        try { return StrictUtf8.GetString(bytes[..^1]); }
        catch (DecoderFallbackException) { throw Failure(); }
    }

    private static string DecodeTerminated(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        if (end < 0) throw Failure();
        return Decode(bytes[..(end + 1)]);
    }

    private static bool SameObject(Entry left, Entry right) =>
        left.Device == right.Device && left.FileSystem == right.FileSystem && left.Inode == right.Inode
        && left.ParentInode == right.ParentInode && left.Kind == right.Kind && left.Name == right.Name
        && left.Flags == right.Flags;

    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    private static ulong U64(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..]);
    private static DarwinFileSystemException Failure(DarwinFileSystemFailure failure = DarwinFileSystemFailure.Unsafe) => new(failure);
    private static void RequirePlatform()
    {
        if (!IsSupported || !BitConverter.IsLittleEndian) throw Failure(DarwinFileSystemFailure.Unavailable);
    }
    private static void RequireUtf8(string value)
    {
        try { _ = StrictUtf8.GetByteCount(value); }
        catch (EncoderFallbackException) { throw Failure(); }
    }
    private static void RequireLeaf(string name)
    {
        RequireUtf8(name);
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(['/', '\0']) >= 0) throw Failure();
    }
    private static SafeFileHandle RequireHandle(int descriptor)
    {
        if (descriptor < 0)
            throw Failure(Marshal.GetLastPInvokeError() == 2 ? DarwinFileSystemFailure.Stale : DarwinFileSystemFailure.Unsafe);
        return new SafeFileHandle((nint)descriptor, ownsHandle: true);
    }

    [InlineArray(16)] internal struct Bytes16 { private byte first; }
    [InlineArray(1024)] internal struct Bytes1024 { private byte first; }
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    internal struct Stat
    {
        [FieldOffset(0)] public int Device;
        [FieldOffset(4)] public ushort Mode;
        [FieldOffset(6)] public ushort LinkCount;
        [FieldOffset(8)] public ulong Inode;
        [FieldOffset(16)] public uint UserId;
        [FieldOffset(20)] public uint GroupId;
        [FieldOffset(32)] public long AccessTimeSeconds;
        [FieldOffset(40)] public long AccessTimeNanoseconds;
        [FieldOffset(48)] public long ModificationTimeSeconds;
        [FieldOffset(56)] public long ModificationTimeNanoseconds;
        [FieldOffset(64)] public long ChangeTimeSeconds;
        [FieldOffset(72)] public long ChangeTimeNanoseconds;
        [FieldOffset(96)] public long Size;
        [FieldOffset(104)] public long Blocks;
        [FieldOffset(112)] public int BlockSize;
        [FieldOffset(116)] public uint Flags;
    }
    [StructLayout(LayoutKind.Explicit, Size = 2168)]
    internal struct StatFs
    {
        [FieldOffset(48)] public ulong FileSystem;
        [FieldOffset(64)] public uint Flags;
        [FieldOffset(72)] public Bytes16 Kind;
        [FieldOffset(88)] public Bytes1024 MountPoint;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct Attributes
    {
        public ushort Count, Reserved;
        public uint Common, Volume, Directory, File, Extended;
    }
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenReadOnly([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAtReadOnly(SafeFileHandle directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags);
    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAtWithMode(SafeFileHandle directory,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags,
        nint x3, nint x4, nint x5, nint x6, nint x7, nuint mode);
    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int FcntlStackArgument(SafeFileHandle handle, int command,
        nint x2, nint x3, nint x4, nint x5, nint x6, nint x7, nint argument);
    [DllImport("libc", EntryPoint = "fstatat", SetLastError = true)]
    private static extern int FStatPath(int directory, string path, out Stat stat, int flags);

    [DllImport("libc", EntryPoint = "fstatat", SetLastError = true)]
    private static extern int FStatAt(SafeFileHandle directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, out Stat stat, int flags);
    [DllImport("libc", EntryPoint = "fstatfs", SetLastError = true)]
    private static extern int FStatFs(SafeFileHandle handle, out StatFs stat);
    [DllImport("libc", EntryPoint = "fgetattrlist", SetLastError = true)]
    private static extern int FGetAttributes(SafeFileHandle handle, ref Attributes attributes, [Out] byte[] bytes, nuint length, nuint options);
    [DllImport("libc", EntryPoint = "getattrlistbulk", SetLastError = true)]
    private static extern int GetBulkAttributes(SafeFileHandle handle, ref Attributes attributes, [Out] byte[] bytes, nuint length, ulong options);
    [DllImport("libc", EntryPoint = "geteuid")]
    internal static extern uint GetEffectiveUserId();
    [DllImport("libc", EntryPoint = "acl_get_fd_np", SetLastError = true)]
    private static extern IntPtr GetAcl(SafeFileHandle handle, int type);
    [DllImport("libc", EntryPoint = "acl_free")]
    private static extern int FreeAcl(IntPtr acl);
}
