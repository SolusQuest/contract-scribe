using ContractScribe.Cli;
using ContractScribe.Core;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ContractScribe.Tests;

public sealed class CampaignCheckpointStoreTests
{
    [Fact]
    public void Constructor_rejects_relative_repository_overlap_and_reserved_names()
    {
        var repository = Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "repository"));

        Assert.Throws<ArgumentException>(() => new FileCampaignCheckpointStore(
            "checkpoint.json",
            repository));
        Assert.Throws<ArgumentException>(() => new FileCampaignCheckpointStore(
            Path.Join(repository, "state", "checkpoint.json"),
            repository));
        Assert.Throws<ArgumentException>(() => new FileCampaignCheckpointStore(
            Path.Join(Path.GetTempPath(), ".checkpoint.json.contractscribe-checkpoint-lease"),
            repository));
        Assert.Throws<ArgumentException>(() => new FileCampaignCheckpointStore(
            Path.Join(Path.GetTempPath(), "campaña.json"),
            repository));
        Assert.Throws<ArgumentException>(() => new FileCampaignCheckpointStore(
            Path.Join(Path.GetTempPath(), "campaign\t.json"),
            repository));
        Assert.Throws<ArgumentException>(() => new FileCampaignCheckpointStore(
            Path.Join(Path.GetTempPath(), new string('a', 121)),
            repository));
        _ = new FileCampaignCheckpointStore(
            Path.Join(Path.GetTempPath(), new string('a', 120)),
            repository);
    }

    [Fact]
    public async Task Unsupported_platform_fails_closed_without_creating_state()
    {
        if (OperatingSystem.IsLinux()
            && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                == System.Runtime.InteropServices.Architecture.X64)
        {
            return;
        }

        var path = Path.Join(Path.GetTempPath(), $"contractscribe-{Guid.NewGuid():N}", "checkpoint.json");
        var store = new FileCampaignCheckpointStore(path, RepositoryRoot());
        var artifact = CreateOpenArtifact();

        var read = await store.ReadAsync(CancellationToken.None);
        var write = await store.CreateIfAbsentAsync(
            artifact.ExactUtf8Json.AsMemory(),
            artifact.CheckpointRevision,
            artifact.Sha256,
            CancellationToken.None);

        Assert.Equal(CampaignCheckpointReadKind.Unreadable, read.Kind);
        Assert.Equal(CampaignCheckpointWriteKind.Unwritable, write.Kind);
        Assert.False(File.Exists(path));
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Linux_store_creates_reads_and_replaces_exact_canonical_checkpoint()
    {
        if (!IsLinuxX64())
        {
            return;
        }

        using var fixture = StoreFixture.Create();
        var initial = CreateOpenArtifact();
        var successor = Assert.IsType<CampaignCheckpointArtifact>(
            CampaignStateReducer.Stop(initial, CampaignTerminalKind.Cancelled).Artifact);

        var absent = await fixture.Store.ReadAsync(CancellationToken.None);
        var created = await WriteInitialAsync(fixture.Store, initial);
        var read = await fixture.Store.ReadAsync(CancellationToken.None);
        var replaced = await fixture.Store.ReplaceIfCurrentAsync(
            initial.CheckpointRevision,
            initial.Sha256,
            successor.ExactUtf8Json.AsMemory(),
            successor.CheckpointRevision,
            successor.Sha256,
            CancellationToken.None);
        var readback = await fixture.Store.ReadAsync(CancellationToken.None);

        Assert.Equal(CampaignCheckpointReadKind.NotFound, absent.Kind);
        Assert.Equal(CampaignCheckpointWriteKind.Written, created.Kind);
        AssertExact(read, initial);
        Assert.Equal(CampaignCheckpointWriteKind.Written, replaced.Kind);
        AssertExact(readback, successor);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(fixture.CheckpointPath));
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(fixture.StateDirectory),
            path => !string.Equals(path, fixture.CheckpointPath, StringComparison.Ordinal));
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Conditional_outcomes_do_not_fall_back_or_overwrite()
    {
        if (!IsLinuxX64())
        {
            return;
        }

        using var fixture = StoreFixture.Create();
        var initial = CreateOpenArtifact();
        var successor = Assert.IsType<CampaignCheckpointArtifact>(
            CampaignStateReducer.Stop(initial, CampaignTerminalKind.Timeout).Artifact);
        Assert.Equal(CampaignCheckpointWriteKind.PredecessorMissing, (
            await fixture.Store.ReplaceIfCurrentAsync(
                initial.CheckpointRevision,
                initial.Sha256,
                successor.ExactUtf8Json.AsMemory(),
                successor.CheckpointRevision,
                successor.Sha256,
                CancellationToken.None)).Kind);

        Assert.Equal(CampaignCheckpointWriteKind.Written, (await WriteInitialAsync(fixture.Store, initial)).Kind);
        Assert.Equal(CampaignCheckpointWriteKind.AlreadyPresent, (await WriteInitialAsync(fixture.Store, initial)).Kind);
        Assert.Equal(CampaignCheckpointWriteKind.CurrentMismatch, (
            await fixture.Store.ReplaceIfCurrentAsync(
                initial.CheckpointRevision,
                new string('a', 64),
                successor.ExactUtf8Json.AsMemory(),
                successor.CheckpointRevision,
                successor.Sha256,
                CancellationToken.None)).Kind);
        AssertExact(await fixture.Store.ReadAsync(CancellationToken.None), initial);
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Live_complete_lease_rejects_a_concurrent_writer_and_cleans_exact_residue()
    {
        if (!IsLinuxX64())
        {
            return;
        }

        using var fixture = StoreFixture.Create();
        using var leaseRecorded = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = new FileCampaignCheckpointStore(
            fixture.CheckpointPath,
            RepositoryRoot(),
            phase =>
            {
                if (phase == "after-lease-record")
                {
                    leaseRecorded.Set();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                }
            });
        var artifact = CreateOpenArtifact();
        var firstWrite = Task.Run(async () => await WriteInitialAsync(first, artifact));
        Assert.True(leaseRecorded.Wait(TimeSpan.FromSeconds(10)));

        var competing = await WriteInitialAsync(fixture.Store, artifact);
        release.Set();
        var winner = await firstWrite;

        Assert.Equal(CampaignCheckpointWriteKind.Unwritable, competing.Kind);
        Assert.Equal(CampaignCheckpointWriteKind.Written, winner.Kind);
        AssertExact(await fixture.Store.ReadAsync(CancellationToken.None), artifact);
        Assert.Single(Directory.EnumerateFileSystemEntries(fixture.StateDirectory));
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Unsafe_checkpoint_is_invalid_and_is_never_repaired_or_overwritten()
    {
        if (!IsLinuxX64())
        {
            return;
        }

        using var fixture = StoreFixture.Create();
        var artifact = CreateOpenArtifact();
        await File.WriteAllBytesAsync(fixture.CheckpointPath, artifact.ExactUtf8Json.ToArray());
        File.SetUnixFileMode(
            fixture.CheckpointPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

        var read = await fixture.Store.ReadAsync(CancellationToken.None);
        var create = await WriteInitialAsync(fixture.Store, artifact);

        Assert.Equal(CampaignCheckpointReadKind.Invalid, read.Kind);
        Assert.Equal(CampaignCheckpointWriteKind.Unwritable, create.Kind);
        Assert.True(File.GetUnixFileMode(fixture.CheckpointPath).HasFlag(UnixFileMode.GroupRead));
    }

    [Theory]
    [InlineData(UnixFileMode.None)]
    [InlineData(UnixFileMode.UserWrite)]
    [InlineData(UnixFileMode.UserRead)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead)]
    [SupportedOSPlatform("linux")]
    public async Task Safely_observed_wrong_checkpoint_modes_are_invalid(UnixFileMode mode)
    {
        if (!IsLinuxX64())
        {
            return;
        }

        using var fixture = StoreFixture.Create();
        var artifact = CreateOpenArtifact();
        await File.WriteAllBytesAsync(fixture.CheckpointPath, artifact.ExactUtf8Json.ToArray());
        File.SetUnixFileMode(fixture.CheckpointPath, mode);

        Assert.Equal(
            CampaignCheckpointReadKind.Invalid,
            (await fixture.Store.ReadAsync(CancellationToken.None)).Kind);
        Assert.Equal(
            CampaignCheckpointWriteKind.Unwritable,
            (await WriteInitialAsync(fixture.Store, artifact)).Kind);
        Assert.Equal(mode, File.GetUnixFileMode(fixture.CheckpointPath));
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Safely_observed_hard_link_and_state_directory_mode_are_invalid()
    {
        if (!IsLinuxX64())
        {
            return;
        }

        using var linked = StoreFixture.Create();
        var artifact = CreateOpenArtifact();
        await File.WriteAllBytesAsync(linked.CheckpointPath, artifact.ExactUtf8Json.ToArray());
        File.SetUnixFileMode(
            linked.CheckpointPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Equal(
            0,
            CreateHardLink(linked.CheckpointPath, Path.Join(linked.Root, "checkpoint-link")));
        Assert.Equal(
            CampaignCheckpointReadKind.Invalid,
            (await linked.Store.ReadAsync(CancellationToken.None)).Kind);
        Assert.True(File.Exists(Path.Join(linked.Root, "checkpoint-link")));

        using var exposedDirectory = StoreFixture.Create();
        File.SetUnixFileMode(
            exposedDirectory.StateDirectory,
            UnixFileMode.UserRead
            | UnixFileMode.UserWrite
            | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead
            | UnixFileMode.GroupExecute);
        Assert.Equal(
            CampaignCheckpointReadKind.Invalid,
            (await exposedDirectory.Store.ReadAsync(CancellationToken.None)).Kind);
        Assert.Equal(
            CampaignCheckpointWriteKind.Unwritable,
            (await WriteInitialAsync(exposedDirectory.Store, artifact)).Kind);
        Assert.Empty(Directory.EnumerateFileSystemEntries(exposedDirectory.StateDirectory));
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Cancellation_before_acquisition_mutates_nothing()
    {
        if (!IsLinuxX64())
        {
            return;
        }

        using var fixture = StoreFixture.Create();
        var artifact = CreateOpenArtifact();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await fixture.Store.CreateIfAbsentAsync(
                artifact.ExactUtf8Json.AsMemory(),
                artifact.CheckpointRevision,
                artifact.Sha256,
                cancellation.Token));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.StateDirectory));
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Cancellation_before_publication_removes_only_exact_owned_residue()
    {
        if (!IsLinuxX64())
        {
            return;
        }

        using var fixture = StoreFixture.Create();
        using var cancellation = new CancellationTokenSource();
        var store = new FileCampaignCheckpointStore(
            fixture.CheckpointPath,
            RepositoryRoot(),
            phase =>
            {
                if (phase == "after-temp-write")
                {
                    cancellation.Cancel();
                }
            });
        var artifact = CreateOpenArtifact();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await store.CreateIfAbsentAsync(
                artifact.ExactUtf8Json.AsMemory(),
                artifact.CheckpointRevision,
                artifact.Sha256,
                cancellation.Token));

        Assert.Equal(
            CampaignCheckpointReadKind.NotFound,
            (await fixture.Store.ReadAsync(CancellationToken.None)).Kind);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.StateDirectory));
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Cancellation_after_publication_preserves_exact_state_and_cleans_lease()
    {
        if (!IsLinuxX64())
        {
            return;
        }

        using var fixture = StoreFixture.Create();
        using var cancellation = new CancellationTokenSource();
        var store = new FileCampaignCheckpointStore(
            fixture.CheckpointPath,
            RepositoryRoot(),
            phase =>
            {
                if (phase == "after-publish-before-readback")
                {
                    cancellation.Cancel();
                }
            });
        var artifact = CreateOpenArtifact();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await store.CreateIfAbsentAsync(
                artifact.ExactUtf8Json.AsMemory(),
                artifact.CheckpointRevision,
                artifact.Sha256,
                cancellation.Token));

        AssertExact(await fixture.Store.ReadAsync(CancellationToken.None), artifact);
        Assert.Single(Directory.EnumerateFileSystemEntries(fixture.StateDirectory));
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Malformed_lease_is_never_locked_rewritten_or_deleted()
    {
        if (!IsLinuxX64())
        {
            return;
        }

        using var fixture = StoreFixture.Create();
        var leasePath = Path.Join(
            fixture.StateDirectory,
            ".campaign.json.contractscribe-checkpoint-lease");
        await File.WriteAllTextAsync(leasePath, "malformed");
        File.SetUnixFileMode(leasePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var artifact = CreateOpenArtifact();

        var result = await WriteInitialAsync(fixture.Store, artifact);

        Assert.Equal(CampaignCheckpointWriteKind.Unwritable, result.Kind);
        Assert.Equal("malformed", await File.ReadAllTextAsync(leasePath));
        Assert.Single(Directory.EnumerateFileSystemEntries(fixture.StateDirectory));
    }

    [Theory]
    [InlineData(false, "truncate")]
    [InlineData(false, "append")]
    [InlineData(false, "same-size")]
    [InlineData(false, "other-c2")]
    [InlineData(true, "truncate")]
    [InlineData(true, "append")]
    [InlineData(true, "same-size")]
    [InlineData(true, "other-c2")]
    [SupportedOSPlatform("linux")]
    public async Task Final_byte_revalidation_prevents_mutated_temp_publication(
        bool replace,
        string mutation)
    {
        if (!IsLinuxX64())
        {
            return;
        }

        using var fixture = StoreFixture.Create();
        var predecessor = CreateOpenArtifact();
        var successor = Assert.IsType<CampaignCheckpointArtifact>(
            CampaignStateReducer.Stop(predecessor, CampaignTerminalKind.Cancelled).Artifact);
        if (replace)
        {
            Assert.Equal(
                CampaignCheckpointWriteKind.Written,
                (await WriteInitialAsync(fixture.Store, predecessor)).Kind);
        }
        var intended = replace ? successor : predecessor;
        var alternate = replace ? predecessor : successor;
        var store = new FileCampaignCheckpointStore(
            fixture.CheckpointPath,
            RepositoryRoot(),
            phase =>
            {
                if (phase != "before-publish")
                {
                    return;
                }
                var temp = Assert.Single(
                    Directory.EnumerateFiles(fixture.StateDirectory),
                    path => path.EndsWith(".tmp", StringComparison.Ordinal));
                switch (mutation)
                {
                    case "truncate":
                        File.WriteAllBytes(temp, [0x7B]);
                        break;
                    case "append":
                        using (var stream = new FileStream(temp, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                        {
                            stream.WriteByte(0x20);
                        }
                        break;
                    case "same-size":
                        var bytes = File.ReadAllBytes(temp);
                        bytes[bytes.Length / 2] ^= 1;
                        File.WriteAllBytes(temp, bytes);
                        break;
                    case "other-c2":
                        File.WriteAllBytes(temp, alternate.ExactUtf8Json.ToArray());
                        break;
                    default:
                        throw new InvalidOperationException("unknown mutation");
                }
            });

        var result = replace
            ? await store.ReplaceIfCurrentAsync(
                predecessor.CheckpointRevision,
                predecessor.Sha256,
                intended.ExactUtf8Json.AsMemory(),
                intended.CheckpointRevision,
                intended.Sha256,
                CancellationToken.None)
            : await store.CreateIfAbsentAsync(
                intended.ExactUtf8Json.AsMemory(),
                intended.CheckpointRevision,
                intended.Sha256,
                CancellationToken.None);

        Assert.Equal(CampaignCheckpointWriteKind.Unwritable, result.Kind);
        var authoritative = await fixture.Store.ReadAsync(CancellationToken.None);
        if (replace)
        {
            AssertExact(authoritative, predecessor);
            Assert.Single(Directory.EnumerateFileSystemEntries(fixture.StateDirectory));
        }
        else
        {
            Assert.Equal(CampaignCheckpointReadKind.NotFound, authoritative.Kind);
            Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.StateDirectory));
        }
    }

    [Theory]
    [InlineData("create-existing", "before-temp-cleanup")]
    [InlineData("create-existing", "before-lease-cleanup")]
    [InlineData("replace-missing", "before-temp-cleanup")]
    [InlineData("replace-missing", "before-lease-cleanup")]
    [InlineData("replace-mismatch", "before-temp-cleanup")]
    [InlineData("replace-mismatch", "before-lease-cleanup")]
    [SupportedOSPlatform("linux")]
    public async Task Cancellation_during_successful_conflict_cleanup_is_not_lost(
        string scenario,
        string cancelPhase)
    {
        if (!IsLinuxX64())
        {
            return;
        }

        using var fixture = StoreFixture.Create();
        var predecessor = CreateOpenArtifact();
        var successor = Assert.IsType<CampaignCheckpointArtifact>(
            CampaignStateReducer.Stop(predecessor, CampaignTerminalKind.Timeout).Artifact);
        if (scenario is "create-existing" or "replace-mismatch")
        {
            Assert.Equal(
                CampaignCheckpointWriteKind.Written,
                (await WriteInitialAsync(fixture.Store, predecessor)).Kind);
        }
        using var cancellation = new CancellationTokenSource();
        var store = new FileCampaignCheckpointStore(
            fixture.CheckpointPath,
            RepositoryRoot(),
            phase =>
            {
                if (phase == cancelPhase)
                {
                    cancellation.Cancel();
                }
            });

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            if (scenario == "create-existing")
            {
                await store.CreateIfAbsentAsync(
                    predecessor.ExactUtf8Json.AsMemory(),
                    predecessor.CheckpointRevision,
                    predecessor.Sha256,
                    cancellation.Token);
            }
            else
            {
                await store.ReplaceIfCurrentAsync(
                    predecessor.CheckpointRevision,
                    scenario == "replace-mismatch" ? new string('a', 64) : predecessor.Sha256,
                    successor.ExactUtf8Json.AsMemory(),
                    successor.CheckpointRevision,
                    successor.Sha256,
                    cancellation.Token);
            }
        });

        var authoritative = await fixture.Store.ReadAsync(CancellationToken.None);
        if (scenario == "replace-missing")
        {
            Assert.Equal(CampaignCheckpointReadKind.NotFound, authoritative.Kind);
            Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.StateDirectory));
        }
        else
        {
            AssertExact(authoritative, predecessor);
            Assert.Single(Directory.EnumerateFileSystemEntries(fixture.StateDirectory));
        }
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Setup_cleanup_failure_always_releases_the_process_local_flock()
    {
        if (!IsLinuxX64())
        {
            return;
        }

        using var fixture = StoreFixture.Create();
        var collisionPath = string.Empty;
        var store = new FileCampaignCheckpointStore(
            fixture.CheckpointPath,
            RepositoryRoot(),
            phase =>
            {
                if (phase != "after-lease-record")
                {
                    return;
                }
                collisionPath = Assert.Single(
                    Directory.EnumerateFiles(fixture.StateDirectory),
                    path => path.EndsWith(".tmp", StringComparison.Ordinal));
                File.Move(collisionPath, Path.Join(fixture.Root, "retained-original-temp"));
                File.WriteAllText(collisionPath, "collision");
                File.SetUnixFileMode(collisionPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                throw new IOException("test setup failure");
            });
        var artifact = CreateOpenArtifact();

        var failed = await WriteInitialAsync(store, artifact);
        var staleLockAcquired = false;
        var retry = new FileCampaignCheckpointStore(
            fixture.CheckpointPath,
            RepositoryRoot(),
            phase => staleLockAcquired |= phase == "after-stale-lease-lock");
        var retried = await WriteInitialAsync(retry, artifact);

        Assert.Equal(CampaignCheckpointWriteKind.Unwritable, failed.Kind);
        Assert.Equal(CampaignCheckpointWriteKind.Unwritable, retried.Kind);
        Assert.True(staleLockAcquired);
        Assert.Equal("collision", File.ReadAllText(collisionPath));
    }

    private static ValueTask<CampaignCheckpointWriteResult> WriteInitialAsync(
        FileCampaignCheckpointStore store,
        CampaignCheckpointArtifact artifact) => store.CreateIfAbsentAsync(
            artifact.ExactUtf8Json.AsMemory(),
            artifact.CheckpointRevision,
            artifact.Sha256,
            CancellationToken.None);

    private static void AssertExact(
        CampaignCheckpointReadResult result,
        CampaignCheckpointArtifact artifact)
    {
        Assert.Equal(CampaignCheckpointReadKind.Found, result.Kind);
        Assert.Equal(artifact.CheckpointRevision, result.CheckpointRevision);
        Assert.Equal(artifact.Sha256, result.Sha256);
        Assert.True(result.ExactUtf8Json.AsSpan().SequenceEqual(artifact.ExactUtf8Json.AsSpan()));
    }

    private static CampaignCheckpointArtifact CreateOpenArtifact()
    {
        var parsed = CampaignStateJson.Parse(File.ReadAllBytes(Path.GetFullPath(Path.Join(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "fixtures", "campaign", "state", "empty-terminal.json"))));
        var fixture = Assert.IsType<CampaignCheckpointArtifact>(parsed.Artifact);
        var state = fixture.State;
        return CampaignStateJson.CreateArtifact(CampaignStateFactory.CreateValidated(
            state.ProductRevision,
            state.CampaignLineage,
            state.Snapshot,
            state.CheckpointRevision,
            state.ConfiguredCeilings,
            state.LineageCharges,
            state.WorkItems,
            terminalOutcome: null));
    }

    private static bool IsLinuxX64() =>
        OperatingSystem.IsLinux()
        && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            == System.Runtime.InteropServices.Architecture.X64;

    private static string RepositoryRoot() => Path.GetFullPath(Path.Join(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", ".."));

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int CreateHardLink(string existingPath, string newPath);

    private sealed class StoreFixture : IDisposable
    {
        private StoreFixture(string root, string stateDirectory, string checkpointPath)
        {
            Root = root;
            StateDirectory = stateDirectory;
            CheckpointPath = checkpointPath;
            Store = new FileCampaignCheckpointStore(checkpointPath, RepositoryRoot());
        }

        internal string Root { get; }
        internal string StateDirectory { get; }
        internal string CheckpointPath { get; }
        internal FileCampaignCheckpointStore Store { get; }

        [SupportedOSPlatform("linux")]
        internal static StoreFixture Create()
        {
            var root = Path.Join(Path.GetTempPath(), $"contractscribe-checkpoint-{Guid.NewGuid():N}");
            var state = Path.Join(root, "state");
            Directory.CreateDirectory(state);
            File.SetUnixFileMode(
                state,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return new StoreFixture(root, state, Path.Join(state, "campaign.json"));
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
