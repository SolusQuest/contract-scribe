using System.Runtime.InteropServices;
using System.Diagnostics;
using ContractScribe.Roslyn;

namespace ContractScribe.Tests;

internal sealed class MacArm64FactAttribute : FactAttribute
{
    public MacArm64FactAttribute()
    {
        if (!DarwinFileSystem.IsSupported) Skip = "Requires native macOS arm64 local filesystem validation.";
    }
}

internal sealed class CaseSensitiveNamesTheoryAttribute : TheoryAttribute
{
    public CaseSensitiveNamesTheoryAttribute()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            Skip = "Requires distinct case-colliding names on the case-sensitive Linux test filesystem.";
    }
}

public sealed class DarwinFileSystemTests
{
    [MacArm64Fact]
    public void Retained_file_and_independent_directory_streams_use_the_authorized_objects()
    {
        using var fixture = new Fixture();
        using var authority = new DarwinFileSystem.RootLease(fixture.Root);
        using var root = authority.OpenRoot();
        using var child = authority.OpenDirectory(root, "child", CancellationToken.None);
        Assert.Equal(["file.txt"], authority.Enumerate(child, CancellationToken.None).Select(entry => entry.Name));
        Assert.Equal(["file.txt"], authority.Enumerate(child, CancellationToken.None).Select(entry => entry.Name));
        using var file = authority.OpenFile(child, "file.txt", CancellationToken.None);
        using var stream = new FileStream(file, FileAccess.Read);
        using var reader = new StreamReader(stream);
        Assert.Equal("authorized", reader.ReadToEnd());
        root.Dispose();
        authority.Revalidate();
        Assert.Single(authority.Enumerate(child, CancellationToken.None));
    }

    [MacArm64Fact]
    public void Same_root_ancestor_rename_is_rejected_before_a_file_handle_is_authorized()
    {
        using var fixture = new Fixture();
        using var authority = new DarwinFileSystem.RootLease(fixture.Root);
        using var root = authority.OpenRoot();
        using var child = authority.OpenDirectory(root, "child", CancellationToken.None);
        Directory.Move(Path.Join(fixture.Root, "child"), Path.Join(fixture.Root, "moved"));
        Directory.CreateDirectory(Path.Join(fixture.Root, "child"));
        File.WriteAllText(Path.Join(fixture.Root, "child", "file.txt"), "replacement");
        var failure = Assert.Throws<DarwinFileSystemException>(() =>
            authority.OpenFile(child, "file.txt", CancellationToken.None));
        Assert.Equal(DarwinFileSystemFailure.Stale, failure.Failure);
    }

    [MacArm64Fact]
    public void Root_replacement_is_rejected_before_directory_enumeration()
    {
        using var fixture = new Fixture();
        using var authority = new DarwinFileSystem.RootLease(fixture.Root);
        using var root = authority.OpenRoot();
        Directory.Move(fixture.Root, Path.Join(fixture.Container, "moved"));
        Directory.CreateDirectory(fixture.Root);
        File.WriteAllText(Path.Join(fixture.Root, "foreign.txt"), "replacement");
        Assert.Throws<DarwinFileSystemException>(() => authority.Enumerate(root, CancellationToken.None).ToArray());
    }

    [MacArm64Fact]
    public void Kernel_aliases_never_establish_exact_presence_or_absence()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Join(fixture.Root, "Case.txt"), "authorized");
        File.WriteAllText(Path.Join(fixture.Root, "é.txt"), "authorized");
        using var authority = new DarwinFileSystem.RootLease(fixture.Root);
        using var root = authority.OpenRoot();
        Assert.Null(authority.Find(root, "absent.txt", CancellationToken.None));
        Assert.Throws<DarwinFileSystemException>(() => authority.Find(root, "case.txt", CancellationToken.None));
        Assert.Throws<DarwinFileSystemException>(() => authority.Find(root, "e\u0301.txt", CancellationToken.None));
    }

    [MacArm64Fact]
    public void Link_fifo_and_hardlink_never_produce_an_authorized_read_handle()
    {
        using var fixture = new Fixture();
        File.CreateSymbolicLink(Path.Join(fixture.Root, "link"), Path.Join(fixture.Root, "child", "file.txt"));
        Assert.Equal(0, MakeFifo(Path.Join(fixture.Root, "fifo"), 0x180));
        Assert.Equal(0, Link(Path.Join(fixture.Root, "child", "file.txt"), Path.Join(fixture.Root, "hardlink")));
        using var authority = new DarwinFileSystem.RootLease(fixture.Root);
        using var root = authority.OpenRoot();
        Assert.Throws<DarwinFileSystemException>(() => authority.OpenFile(root, "link", CancellationToken.None));
        Assert.Throws<DarwinFileSystemException>(() => authority.OpenFile(root, "fifo", CancellationToken.None));
        var failure = Assert.Throws<DarwinFileSystemException>(() => authority.OpenFile(root, "hardlink", CancellationToken.None));
        Assert.Equal(DarwinFileSystemFailure.HardLink, failure.Failure);
    }

    [MacArm64Fact]
    public void Actual_mounted_descriptor_cannot_be_authorized_by_bulk_observations()
    {
        using var authority = new DarwinFileSystem.RootLease("/");
        using var root = authority.OpenRoot();
        var observed = Assert.IsType<DarwinFileSystem.Entry>(authority.Find(root, "dev", CancellationToken.None));
        using var actual = DarwinFileSystem.OpenDirectory("/dev");
        Assert.Equal(1u, DarwinFileSystem.ReadMountStatus(actual));
        Assert.Throws<DarwinFileSystemException>(() => DarwinFileSystem.RequireRelativeDirectoryMount(actual));
        Assert.Throws<DarwinFileSystemException>(() => authority.OpenDirectory(root, "dev", CancellationToken.None,
            observation: observed with { MountStatus = 0 }));
        using var volume = new DarwinFileSystem.RootLease("/System/Volumes/Data");
        using var selectedRoot = volume.OpenRoot();
        Assert.Equal(1u, DarwinFileSystem.ReadMountStatus(selectedRoot));
    }

    [MacArm64Fact]
    public void A_firmlink_entry_never_authorizes_a_relative_directory()
    {
        using (var system = new DarwinFileSystem.RootLease("/"))
        using (var systemRoot = system.OpenRoot())
        {
            var actual = Assert.IsType<DarwinFileSystem.Entry>(system.Find(systemRoot, "Users", CancellationToken.None));
            Assert.NotEqual(0u, actual.Flags & 0x800000);
            Assert.Throws<DarwinFileSystemException>(() => system.OpenDirectory(systemRoot, "Users", CancellationToken.None));
        }
        using var fixture = new Fixture();
        using var authority = new DarwinFileSystem.RootLease(fixture.Root);
        using var root = authority.OpenRoot();
        var observed = Assert.IsType<DarwinFileSystem.Entry>(authority.Find(root, "child", CancellationToken.None));
        Assert.Throws<DarwinFileSystemException>(() => authority.OpenDirectory(root, "child", CancellationToken.None,
            observation: observed with { Flags = observed.Flags | 0x800000 }));
    }

    [MacArm64Fact]
    public void Physical_ancestry_agrees_across_native_APFS_namespaces()
    {
        using var first = DarwinFileSystem.OpenDirectory("/private/tmp");
        using var alias = DarwinFileSystem.OpenDirectory("/System/Volumes/Data/private/tmp");
        var expected = DarwinFileSystem.ReadAncestry(first);
        var alternate = DarwinFileSystem.ReadAncestry(alias);
        Assert.Equal(expected.Projection, alternate.Projection);
        Assert.True(DarwinFileSystem.Overlaps(expected, alternate));
        using var fixture = new Fixture();
        using var source = DarwinFileSystem.OpenDirectory(fixture.Root);
        Directory.CreateDirectory(Path.Join(fixture.Root, "unused"));
        using var descendant = DarwinFileSystem.OpenDirectory(Path.Join(fixture.Root, "unused"));
        Assert.True(DarwinFileSystem.IsSameOrDescendant(
            DarwinFileSystem.ReadAncestry(source), DarwinFileSystem.ReadAncestry(descendant)));
    }

    [MacArm64Fact]
    public void Arm64_variadic_creation_is_exclusive_and_uses_exact_private_mode()
    {
        using var fixture = new Fixture();
        using var parent = DarwinFileSystem.OpenDirectory(fixture.Root);
        DarwinFileSystem.Qualify(parent, requireOwnership: true);
        using var file = DarwinFileSystem.OpenAt(parent, "private.bin",
            DarwinFileSystem.ReadWrite | DarwinFileSystem.Create | DarwinFileSystem.Exclusive
                | DarwinFileSystem.NoFollowAny | DarwinFileSystem.CloseOnExec, 0x180);
        DarwinFileSystem.RequirePrivate(file, directory: false);
        var stat = DarwinFileSystem.ReadStat(file);
        Assert.Equal(0x180, stat.Mode & 0xfff);
        Assert.Equal(-1, DarwinFileSystem.CreateAt(parent, "private.bin",
            DarwinFileSystem.ReadWrite | DarwinFileSystem.Create | DarwinFileSystem.Exclusive, 0x1ff));
        Assert.Equal(17, Marshal.GetLastPInvokeError());
        Assert.Equal(stat.Inode, DarwinFileSystem.ReadStat(file).Inode);
        Assert.Equal(0, DarwinFileSystem.ReadStat(file).Size);
    }

    [MacArm64Fact]
    public async Task Native_owned_child_and_grandchild_have_stable_identity_and_exact_arguments()
    {
        var start = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("/bin/sleep 30 & child=$!; echo $child; read release; kill $child; wait $child; exit 0");
        start.ArgumentList.Add("contractscribe-native-process");
        start.ArgumentList.Add("");
        start.ArgumentList.Add("argument with space");
        using var child = Process.Start(start)!;
        try
        {
            var childIdentity = DarwinProcessObservation.ReadIdentity(child.Id);
            Assert.Equal(Environment.ProcessId, childIdentity.ParentProcessId);
            Assert.Equal(childIdentity.StartIdentity, DarwinProcessObservation.ReadIdentity(child.Id).StartIdentity);
            var grandchildId = int.Parse((await child.StandardOutput.ReadLineAsync())!);
            var grandchild = DarwinProcessObservation.ReadIdentity(grandchildId);
            Assert.Equal(child.Id, grandchild.ParentProcessId);
            using var meter = new ToolchainProcessMeter(pollingInterval: TimeSpan.FromDays(1));
            Assert.True(meter.SelectToolchain(new RegisteredToolchain(
                new ToolchainIdentity("test", "test", "test", "Arm64"), Path.GetTempPath())) >= 2);
            Assert.True(meter.ToFact().Measured >= 2);
            var arguments = DarwinProcessObservation.ReadOwnedArguments(child.Id, childIdentity.StartIdentity, Environment.ProcessId);
            Assert.Equal("", arguments[^2]);
            Assert.Equal("argument with space", arguments[^1]);
            Assert.NotEmpty(DarwinProcessObservation.ReadOwnedArguments(grandchildId, grandchild.StartIdentity, Environment.ProcessId));
            Assert.Throws<IOException>(() => DarwinProcessObservation.ReadOwnedArguments(
                child.Id, childIdentity.StartIdentity + 1, Environment.ProcessId));
        }
        finally
        {
            await child.StandardInput.WriteLineAsync("release");
            await child.StandardInput.FlushAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await child.WaitForExitAsync(timeout.Token);
        }
        Assert.Equal(0, child.ExitCode);
    }

    [MacArm64Fact]
    public void Actual_workspace_backing_qualifies_for_reads_and_enforces_private_ownership_separately()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Join(repository.FullName, "ContractScribe.slnx")))
            repository = repository.Parent;
        Assert.NotNull(repository);
        using var source = DarwinFileSystem.OpenDirectory(repository!.FullName);
        DarwinFileSystem.Qualify(source);
        var noOwners = (DarwinFileSystem.ReadFileSystem(source).Flags & DarwinFileSystem.IgnoreOwnership) != 0;
        if (noOwners)
            Assert.Throws<DarwinFileSystemException>(() => DarwinFileSystem.Qualify(source, requireOwnership: true));
        else
            DarwinFileSystem.Qualify(source, requireOwnership: true);
        using var authority = new DarwinFileSystem.RootLease(repository.FullName);
        using var root = authority.OpenRoot();
        using var file = authority.OpenFile(root, "global.json", CancellationToken.None);
        using var stream = new FileStream(file, FileAccess.Read);
        Assert.True(stream.ReadByte() >= 0);
    }

    [MacArm64Fact]
    public async Task Empty_argv_zero_does_not_promote_an_environment_tail_to_an_argument()
    {
        var start = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("exec -a '' /bin/sleep 30");
        start.Environment["CONTRACTSCRIBE_NATIVE_SENTINEL"] = "synthetic-environment-tail";
        using var process = Process.Start(start)!;
        try
        {
            IReadOnlyList<string>? captured = null;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                var identity = DarwinProcessObservation.ReadIdentity(process.Id);
                var arguments = DarwinProcessObservation.ReadOwnedArguments(process.Id, identity.StartIdentity, Environment.ProcessId);
                if (arguments.Count == 2 && arguments[0].Length == 0) { captured = arguments; break; }
                await Task.Delay(10);
            }
            Assert.NotNull(captured);
            Assert.Equal(["", "30"], captured);
        }
        finally
        {
            var identity = DarwinProcessObservation.ReadIdentity(process.Id);
            if (!process.HasExited && DarwinProcessObservation.ReadIdentity(process.Id) == identity) process.Kill();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal string Container { get; } = Path.Join("/private/tmp", "contractscribe-native-" + Guid.NewGuid().ToString("N"));
        internal string Root => Path.Join(Container, "source");
        internal Fixture()
        {
            Directory.CreateDirectory(Path.Join(Root, "child"));
            File.WriteAllText(Path.Join(Root, "child", "file.txt"), "authorized");
        }
        public void Dispose() => Directory.Delete(Container, recursive: true);
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MakeFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link([MarshalAs(UnmanagedType.LPUTF8Str)] string source, [MarshalAs(UnmanagedType.LPUTF8Str)] string destination);
}
