using ContractScribe.Cli;
using ContractScribe.Core;
using Microsoft.Win32.SafeHandles;
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

    public static IEnumerable<object[]> PostPublicationCleanupMutations()
    {
        foreach (var replace in new[] { false, true })
        {
            foreach (var hook in new[] { "after-readback-before-cleanup", "before-lease-cleanup" })
            {
                foreach (var mutation in new[]
                         {
                             "truncate", "append", "same-size", "other-c2", "marker", "mode", "hard-link",
                             "replace-path", "temp-collision", "lease-record",
                         })
                {
                    yield return [replace, hook, mutation];
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(PostPublicationCleanupMutations))]
    [SupportedOSPlatform("linux")]
    public async Task Postpublication_cleanup_revalidates_the_complete_authority_graph(
        bool replace,
        string hook,
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
        var mutated = false;
        Exception? mutationFailure = null;
        var store = new FileCampaignCheckpointStore(
            fixture.CheckpointPath,
            RepositoryRoot(),
            phase =>
            {
                if (phase == hook && !mutated)
                {
                    mutated = true;
                    try
                    {
                        ApplyCleanupMutation(fixture, intended, alternate, mutation);
                    }
                    catch (Exception exception)
                    {
                        mutationFailure = exception;
                    }
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
            : await WriteInitialAsync(store, intended);

        Assert.True(mutated);
        Assert.Null(mutationFailure);
        Assert.Equal(CampaignCheckpointWriteKind.Unwritable, result.Kind);
        Assert.True(File.Exists(LeasePath(fixture)));
        AssertCleanupMutationPreserved(fixture, intended, alternate, mutation);
    }

    [Theory]
    [InlineData(false, "after-readback-before-cleanup")]
    [InlineData(true, "after-readback-before-cleanup")]
    [InlineData(false, "before-lease-cleanup")]
    [InlineData(true, "before-lease-cleanup")]
    [SupportedOSPlatform("linux")]
    public async Task Unsafe_postpublication_cleanup_wins_over_cancellation(
        bool replace,
        string hook)
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
        using var cancellation = new CancellationTokenSource();
        var mutated = false;
        var store = new FileCampaignCheckpointStore(
            fixture.CheckpointPath,
            RepositoryRoot(),
            phase =>
            {
                if (phase == hook && !mutated)
                {
                    mutated = true;
                    cancellation.Cancel();
                    ApplyCleanupMutation(fixture, intended, alternate, "truncate");
                }
            });

        var result = replace
            ? await store.ReplaceIfCurrentAsync(
                predecessor.CheckpointRevision,
                predecessor.Sha256,
                intended.ExactUtf8Json.AsMemory(),
                intended.CheckpointRevision,
                intended.Sha256,
                cancellation.Token)
            : await store.CreateIfAbsentAsync(
                intended.ExactUtf8Json.AsMemory(),
                intended.CheckpointRevision,
                intended.Sha256,
                cancellation.Token);

        Assert.True(mutated);
        Assert.Equal(CampaignCheckpointWriteKind.Unwritable, result.Kind);
        Assert.True(File.Exists(LeasePath(fixture)));
        AssertCleanupMutationPreserved(fixture, intended, alternate, "truncate");
    }

    public static IEnumerable<object[]> UnsafeConflictCleanupMutations()
    {
        foreach (var scenario in new[] { "create-existing", "replace-missing", "replace-mismatch" })
        {
            yield return [scenario, "before-temp-cleanup", "lease-record"];
            yield return [scenario, "before-lease-cleanup", "temp-collision"];
            yield return [scenario, "before-lease-cleanup", "lease-record"];
        }
    }

    [Theory]
    [MemberData(nameof(UnsafeConflictCleanupMutations))]
    [SupportedOSPlatform("linux")]
    public async Task Unsafe_conflict_cleanup_wins_over_cancellation_and_preserves_authority(
        string scenario,
        string hook,
        string mutation)
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
        var mutated = false;
        var store = new FileCampaignCheckpointStore(
            fixture.CheckpointPath,
            RepositoryRoot(),
            phase =>
            {
                if (phase == hook && !mutated)
                {
                    mutated = true;
                    cancellation.Cancel();
                    ApplyCleanupMutation(
                        fixture,
                        scenario == "create-existing" ? predecessor : successor,
                        predecessor,
                        mutation);
                }
            });

        var result = scenario == "create-existing"
            ? await store.CreateIfAbsentAsync(
                predecessor.ExactUtf8Json.AsMemory(),
                predecessor.CheckpointRevision,
                predecessor.Sha256,
                cancellation.Token)
            : await store.ReplaceIfCurrentAsync(
                predecessor.CheckpointRevision,
                scenario == "replace-mismatch" ? new string('a', 64) : predecessor.Sha256,
                successor.ExactUtf8Json.AsMemory(),
                successor.CheckpointRevision,
                successor.Sha256,
                cancellation.Token);

        Assert.True(mutated);
        Assert.Equal(CampaignCheckpointWriteKind.Unwritable, result.Kind);
        Assert.True(File.Exists(LeasePath(fixture)));
        var authoritative = await fixture.Store.ReadAsync(CancellationToken.None);
        if (scenario == "replace-missing")
        {
            Assert.Equal(CampaignCheckpointReadKind.NotFound, authoritative.Kind);
        }
        else
        {
            AssertExact(authoritative, predecessor);
        }
        AssertCleanupMutationPreserved(
            fixture,
            scenario == "create-existing" ? predecessor : successor,
            predecessor,
            mutation);
    }

    [Theory]
    [InlineData(false, "before-publish")]
    [InlineData(true, "before-publish")]
    [InlineData(false, "after-readback-before-cleanup")]
    [InlineData(true, "after-readback-before-cleanup")]
    [InlineData(false, "before-lease-cleanup")]
    [InlineData(true, "before-lease-cleanup")]
    [SupportedOSPlatform("linux")]
    public async Task Complete_absolute_path_binding_rejects_moved_ancestor_trees(
        bool replace,
        string hook)
    {
        if (!IsLinuxX64())
        {
            return;
        }

        var anchor = Path.Join(Path.GetTempPath(), $"contractscribe-path-{Guid.NewGuid():N}");
        var movedAnchor = anchor + "-moved";
        var stateDirectory = Path.Join(anchor, "container", "state");
        var movedStateDirectory = Path.Join(movedAnchor, "container", "state");
        var checkpointPath = Path.Join(stateDirectory, "campaign.json");
        try
        {
            Directory.CreateDirectory(stateDirectory);
            File.SetUnixFileMode(
                stateDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var predecessor = CreateOpenArtifact();
            var successor = Assert.IsType<CampaignCheckpointArtifact>(
                CampaignStateReducer.Stop(predecessor, CampaignTerminalKind.Cancelled).Artifact);
            var baseline = new FileCampaignCheckpointStore(checkpointPath, RepositoryRoot());
            if (replace)
            {
                Assert.Equal(
                    CampaignCheckpointWriteKind.Written,
                    (await WriteInitialAsync(baseline, predecessor)).Kind);
            }
            var swapped = false;
            var store = new FileCampaignCheckpointStore(
                checkpointPath,
                RepositoryRoot(),
                phase =>
                {
                    if (phase != hook || swapped)
                    {
                        return;
                    }
                    swapped = true;
                    Directory.Move(anchor, movedAnchor);
                    Directory.CreateDirectory(stateDirectory);
                    File.SetUnixFileMode(
                        stateDirectory,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                });
            var intended = replace ? successor : predecessor;

            var result = replace
                ? await store.ReplaceIfCurrentAsync(
                    predecessor.CheckpointRevision,
                    predecessor.Sha256,
                    intended.ExactUtf8Json.AsMemory(),
                    intended.CheckpointRevision,
                    intended.Sha256,
                    CancellationToken.None)
                : await WriteInitialAsync(store, intended);

            Assert.True(swapped);
            Assert.Equal(CampaignCheckpointWriteKind.Unwritable, result.Kind);
            Assert.Equal(
                CampaignCheckpointReadKind.NotFound,
                (await new FileCampaignCheckpointStore(checkpointPath, RepositoryRoot())
                    .ReadAsync(CancellationToken.None)).Kind);
            Assert.Empty(Directory.EnumerateFileSystemEntries(stateDirectory));
            Assert.NotEmpty(Directory.EnumerateFileSystemEntries(movedStateDirectory));
        }
        finally
        {
            if (Directory.Exists(anchor))
            {
                Directory.Delete(anchor, recursive: true);
            }
            if (Directory.Exists(movedAnchor))
            {
                Directory.Delete(movedAnchor, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [SupportedOSPlatform("linux")]
    public async Task Fresh_lease_initialization_revalidates_confinement_after_creator_hook(bool replace)
    {
        if (!IsLinuxX64())
        {
            return;
        }

        var root = Path.Join(Path.GetTempPath(), $"contractscribe-lease-confinement-{Guid.NewGuid():N}");
        var repository = Path.Join(root, "repository");
        var stateAnchor = Path.Join(root, "state-anchor");
        var movedAnchor = Path.Join(repository, "moved-state-anchor");
        var stateDirectory = Path.Join(stateAnchor, "container", "state");
        var movedStateDirectory = Path.Join(movedAnchor, "container", "state");
        var checkpointPath = Path.Join(stateDirectory, "campaign.json");
        var movedLeasePath = Path.Join(
            movedStateDirectory,
            ".campaign.json.contractscribe-checkpoint-lease");
        try
        {
            Directory.CreateDirectory(repository);
            Directory.CreateDirectory(stateDirectory);
            File.SetUnixFileMode(
                stateDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var predecessor = CreateOpenArtifact();
            var successor = Assert.IsType<CampaignCheckpointArtifact>(
                CampaignStateReducer.Stop(predecessor, CampaignTerminalKind.Cancelled).Artifact);
            if (replace)
            {
                Assert.Equal(
                    CampaignCheckpointWriteKind.Written,
                    (await WriteInitialAsync(
                        new FileCampaignCheckpointStore(checkpointPath, repository),
                        predecessor)).Kind);
            }
            var moved = false;
            var store = new FileCampaignCheckpointStore(
                checkpointPath,
                repository,
                phase =>
                {
                    if (phase != "after-lease-create-before-lock" || moved)
                    {
                        return;
                    }
                    moved = true;
                    Directory.Move(stateAnchor, movedAnchor);
                    Directory.CreateDirectory(stateDirectory);
                    File.SetUnixFileMode(
                        stateDirectory,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                });

            var result = replace
                ? await store.ReplaceIfCurrentAsync(
                    predecessor.CheckpointRevision,
                    predecessor.Sha256,
                    successor.ExactUtf8Json.AsMemory(),
                    successor.CheckpointRevision,
                    successor.Sha256,
                    CancellationToken.None)
                : await WriteInitialAsync(store, predecessor);

            Assert.True(moved);
            Assert.Equal(CampaignCheckpointWriteKind.Unwritable, result.Kind);
            Assert.Empty(Directory.EnumerateFileSystemEntries(stateDirectory));
            Assert.True(File.Exists(movedLeasePath));
            Assert.Equal(0, new FileInfo(movedLeasePath).Length);
            Assert.Equal(
                (nint)(-1),
                GetExtendedAttributeSize(
                    movedLeasePath,
                    "user.contractscribe.checkpoint-object",
                    nint.Zero,
                    0));
            Assert.DoesNotContain(
                Directory.EnumerateFileSystemEntries(movedStateDirectory),
                path => path.EndsWith(".tmp", StringComparison.Ordinal));
            var movedEntries = Directory.EnumerateFileSystemEntries(movedStateDirectory)
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal)
                .ToArray();

            var retryStore = new FileCampaignCheckpointStore(checkpointPath, repository);
            var retry = replace
                ? await retryStore.ReplaceIfCurrentAsync(
                    predecessor.CheckpointRevision,
                    predecessor.Sha256,
                    successor.ExactUtf8Json.AsMemory(),
                    successor.CheckpointRevision,
                    successor.Sha256,
                    CancellationToken.None)
                : await WriteInitialAsync(retryStore, predecessor);

            Assert.Equal(
                replace
                    ? CampaignCheckpointWriteKind.PredecessorMissing
                    : CampaignCheckpointWriteKind.Written,
                retry.Kind);
            Assert.Equal(
                movedEntries,
                Directory.EnumerateFileSystemEntries(movedStateDirectory)
                    .Select(Path.GetFileName)
                    .Order(StringComparer.Ordinal)
                    .ToArray());
            Assert.Equal(0, new FileInfo(movedLeasePath).Length);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(false, "arbitrary-bytes")]
    [InlineData(false, "canonical-record")]
    [InlineData(false, "wrong-marker")]
    [InlineData(true, "arbitrary-bytes")]
    [InlineData(true, "canonical-record")]
    [InlineData(true, "wrong-marker")]
    [SupportedOSPlatform("linux")]
    public async Task Fresh_lease_initialization_preserves_same_inode_hook_mutations(
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

        var leasePath = LeasePath(fixture);
        var changedBytes = mutation switch
        {
            "arbitrary-bytes" => "changed lease bytes"u8.ToArray(),
            "canonical-record" => CanonicalLookingUnrelatedLeaseRecord(),
            _ => [],
        };
        var changedMarker = "contract-scribe-checkpoint-object-v1:lease:unrelated"u8.ToArray();
        var mutationApplied = false;
        Exception? mutationFailure = null;
        var store = new FileCampaignCheckpointStore(
            fixture.CheckpointPath,
            RepositoryRoot(),
            phase =>
            {
                if (phase != "after-lease-create-before-lock" || mutationApplied)
                {
                    return;
                }
                try
                {
                    if (mutation == "wrong-marker")
                    {
                        SetObjectMarker(leasePath, changedMarker);
                    }
                    else
                    {
                        WriteNativeBytes(leasePath, changedBytes);
                    }
                    mutationApplied = true;
                }
                catch (Exception exception)
                {
                    mutationFailure = exception;
                }
            });

        var result = replace
            ? await store.ReplaceIfCurrentAsync(
                predecessor.CheckpointRevision,
                predecessor.Sha256,
                successor.ExactUtf8Json.AsMemory(),
                successor.CheckpointRevision,
                successor.Sha256,
                CancellationToken.None)
            : await WriteInitialAsync(store, predecessor);

        Assert.Null(mutationFailure);
        Assert.True(mutationApplied);
        Assert.Equal(CampaignCheckpointWriteKind.Unwritable, result.Kind);
        Assert.True(File.Exists(leasePath));
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(fixture.StateDirectory),
            path => path.EndsWith(".tmp", StringComparison.Ordinal));
        if (replace)
        {
            Assert.Equal(predecessor.ExactUtf8Json.ToArray(), File.ReadAllBytes(fixture.CheckpointPath));
        }
        else
        {
            Assert.False(File.Exists(fixture.CheckpointPath));
        }
        AssertFreshLeaseMutationPreserved(leasePath, mutation, changedBytes, changedMarker);

        var retryStore = new FileCampaignCheckpointStore(fixture.CheckpointPath, RepositoryRoot());
        var retry = replace
            ? await retryStore.ReplaceIfCurrentAsync(
                predecessor.CheckpointRevision,
                predecessor.Sha256,
                successor.ExactUtf8Json.AsMemory(),
                successor.CheckpointRevision,
                successor.Sha256,
                CancellationToken.None)
            : await WriteInitialAsync(retryStore, predecessor);

        Assert.Equal(CampaignCheckpointWriteKind.Unwritable, retry.Kind);
        AssertFreshLeaseMutationPreserved(leasePath, mutation, changedBytes, changedMarker);
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(fixture.StateDirectory),
            path => path.EndsWith(".tmp", StringComparison.Ordinal));
        if (replace)
        {
            Assert.Equal(predecessor.ExactUtf8Json.ToArray(), File.ReadAllBytes(fixture.CheckpointPath));
        }
        else
        {
            Assert.False(File.Exists(fixture.CheckpointPath));
        }
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Read_rejects_a_checkpoint_from_an_ancestor_tree_moved_after_open()
    {
        if (!IsLinuxX64())
        {
            return;
        }

        var anchor = Path.Join(Path.GetTempPath(), $"contractscribe-read-path-{Guid.NewGuid():N}");
        var movedAnchor = anchor + "-moved";
        var stateDirectory = Path.Join(anchor, "container", "state");
        var checkpointPath = Path.Join(stateDirectory, "campaign.json");
        try
        {
            Directory.CreateDirectory(stateDirectory);
            File.SetUnixFileMode(
                stateDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var artifact = CreateOpenArtifact();
            Assert.Equal(
                CampaignCheckpointWriteKind.Written,
                (await WriteInitialAsync(
                    new FileCampaignCheckpointStore(checkpointPath, RepositoryRoot()),
                    artifact)).Kind);
            var swapped = false;
            var store = new FileCampaignCheckpointStore(
                checkpointPath,
                RepositoryRoot(),
                phase =>
                {
                    if (phase != "before-read" || swapped)
                    {
                        return;
                    }
                    swapped = true;
                    Directory.Move(anchor, movedAnchor);
                    Directory.CreateDirectory(stateDirectory);
                    File.SetUnixFileMode(
                        stateDirectory,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                });

            var result = await store.ReadAsync(CancellationToken.None);

            Assert.True(swapped);
            Assert.Equal(CampaignCheckpointReadKind.Unreadable, result.Kind);
            Assert.Empty(Directory.EnumerateFileSystemEntries(stateDirectory));
            Assert.True(File.Exists(Path.Join(movedAnchor, "container", "state", "campaign.json")));
        }
        finally
        {
            if (Directory.Exists(anchor))
            {
                Directory.Delete(anchor, recursive: true);
            }
            if (Directory.Exists(movedAnchor))
            {
                Directory.Delete(movedAnchor, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("after-lease-create-before-lock", 1)]
    [InlineData("before-publish", 2)]
    [SupportedOSPlatform("linux")]
    public async Task Publication_rejects_a_rebound_repository_path_used_for_confinement(
        string hook,
        int expectedStateEntryCount)
    {
        if (!IsLinuxX64())
        {
            return;
        }

        var stateRoot = Path.Join(Path.GetTempPath(), $"contractscribe-state-{Guid.NewGuid():N}");
        var stateDirectory = Path.Join(stateRoot, "state");
        var repositoryAnchor = Path.Join(Path.GetTempPath(), $"contractscribe-repository-{Guid.NewGuid():N}");
        var movedRepositoryAnchor = repositoryAnchor + "-moved";
        var repositoryPath = Path.Join(repositoryAnchor, "repository");
        try
        {
            Directory.CreateDirectory(stateDirectory);
            Directory.CreateDirectory(repositoryPath);
            File.SetUnixFileMode(
                stateDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var checkpointPath = Path.Join(stateDirectory, "campaign.json");
            var swapped = false;
            var store = new FileCampaignCheckpointStore(
                checkpointPath,
                repositoryPath,
                phase =>
                {
                    if (phase != hook || swapped)
                    {
                        return;
                    }
                    swapped = true;
                    Directory.Move(repositoryAnchor, movedRepositoryAnchor);
                    Directory.CreateDirectory(repositoryPath);
                });
            var artifact = CreateOpenArtifact();

            var result = await WriteInitialAsync(store, artifact);

            Assert.True(swapped);
            Assert.Equal(CampaignCheckpointWriteKind.Unwritable, result.Kind);
            Assert.Equal(expectedStateEntryCount, Directory.EnumerateFileSystemEntries(stateDirectory).Count());
            if (hook == "after-lease-create-before-lock")
            {
                var leasePath = Path.Join(
                    stateDirectory,
                    ".campaign.json.contractscribe-checkpoint-lease");
                Assert.Equal(0, new FileInfo(leasePath).Length);
                Assert.Equal(
                    (nint)(-1),
                    GetExtendedAttributeSize(
                        leasePath,
                        "user.contractscribe.checkpoint-object",
                        nint.Zero,
                        0));
            }
            Assert.Empty(Directory.EnumerateFileSystemEntries(repositoryPath));
        }
        finally
        {
            foreach (var path in new[] { stateRoot, repositoryAnchor, movedRepositoryAnchor })
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
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

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Setup_cleanup_preserves_a_changed_canonical_record_and_releases_flock()
    {
        if (!IsLinuxX64())
        {
            return;
        }

        using var fixture = StoreFixture.Create();
        var artifact = CreateOpenArtifact();
        var alternate = Assert.IsType<CampaignCheckpointArtifact>(
            CampaignStateReducer.Stop(artifact, CampaignTerminalKind.Cancelled).Artifact);
        var store = new FileCampaignCheckpointStore(
            fixture.CheckpointPath,
            RepositoryRoot(),
            phase =>
            {
                if (phase == "after-lease-record")
                {
                    ApplyCleanupMutation(fixture, artifact, alternate, "lease-record");
                    throw new IOException("test setup failure");
                }
            });

        var failed = await WriteInitialAsync(store, artifact);
        Assert.Equal(CampaignCheckpointWriteKind.Unwritable, failed.Kind);
        Assert.Contains("operation=replace", File.ReadAllText(LeasePath(fixture)), StringComparison.Ordinal);
        Assert.Equal(2, Directory.EnumerateFileSystemEntries(fixture.StateDirectory).Count());

        var staleLockAcquired = false;
        var retry = new FileCampaignCheckpointStore(
            fixture.CheckpointPath,
            RepositoryRoot(),
            phase => staleLockAcquired |= phase == "after-stale-lease-lock");
        var retried = await WriteInitialAsync(retry, artifact);

        Assert.Equal(CampaignCheckpointWriteKind.Unwritable, retried.Kind);
        Assert.True(staleLockAcquired);
        Assert.Contains("operation=replace", File.ReadAllText(LeasePath(fixture)), StringComparison.Ordinal);
        Assert.Equal(2, Directory.EnumerateFileSystemEntries(fixture.StateDirectory).Count());
    }

    [SupportedOSPlatform("linux")]
    private static void ApplyCleanupMutation(
        StoreFixture fixture,
        CampaignCheckpointArtifact intended,
        CampaignCheckpointArtifact alternate,
        string mutation)
    {
        var leasePath = LeasePath(fixture);
        switch (mutation)
        {
            case "truncate":
                WriteNativeBytes(fixture.CheckpointPath, [0x7B]);
                break;
            case "append":
                WriteNativeBytes(fixture.CheckpointPath, [.. ReadNativeBytes(fixture.CheckpointPath), 0x20]);
                break;
            case "same-size":
                var bytes = ReadNativeBytes(fixture.CheckpointPath);
                bytes[bytes.Length / 2] ^= 1;
                WriteNativeBytes(fixture.CheckpointPath, bytes);
                break;
            case "other-c2":
                WriteNativeBytes(fixture.CheckpointPath, alternate.ExactUtf8Json.AsSpan());
                break;
            case "marker":
                Assert.Equal(
                    0,
                    RemoveExtendedAttribute(
                        fixture.CheckpointPath,
                        "user.contractscribe.checkpoint-object"));
                break;
            case "mode":
                File.SetUnixFileMode(
                    fixture.CheckpointPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
                break;
            case "hard-link":
                Assert.Equal(
                    0,
                    CreateHardLink(
                        fixture.CheckpointPath,
                        Path.Join(fixture.Root, "checkpoint-cleanup-link")));
                break;
            case "replace-path":
                File.Move(
                    fixture.CheckpointPath,
                    Path.Join(fixture.Root, "retained-checkpoint"));
                WriteNativeBytes(fixture.CheckpointPath, intended.ExactUtf8Json.AsSpan(), create: true);
                File.SetUnixFileMode(
                    fixture.CheckpointPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
                break;
            case "temp-collision":
                var record = ReadNativeText(leasePath);
                var tempPath = Path.Join(fixture.StateDirectory, LeaseValue(record, "temp="));
                WriteNativeBytes(tempPath, "collision"u8, create: true, exclusive: true);
                File.SetUnixFileMode(
                    tempPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
                break;
            case "lease-record":
                record = ReadNativeText(leasePath);
                var changedRecord = record.Contains("operation=create", StringComparison.Ordinal)
                    ? record
                        .Replace("operation=create", "operation=replace", StringComparison.Ordinal)
                        .Replace(
                            "expected-revision=-",
                            $"expected-revision={intended.CheckpointRevision}",
                            StringComparison.Ordinal)
                        .Replace(
                            "expected-sha256=-",
                            $"expected-sha256={intended.Sha256}",
                            StringComparison.Ordinal)
                    : record
                        .Replace("operation=replace", "operation=create", StringComparison.Ordinal)
                        .Replace(
                            $"expected-revision={LeaseValue(record, "expected-revision=")}",
                            "expected-revision=-",
                            StringComparison.Ordinal)
                        .Replace(
                            $"expected-sha256={LeaseValue(record, "expected-sha256=")}",
                            "expected-sha256=-",
                            StringComparison.Ordinal);
                WriteNativeBytes(leasePath, System.Text.Encoding.UTF8.GetBytes(changedRecord));
                break;
            default:
                throw new InvalidOperationException("unknown cleanup mutation");
        }
    }

    [SupportedOSPlatform("linux")]
    private static void AssertCleanupMutationPreserved(
        StoreFixture fixture,
        CampaignCheckpointArtifact intended,
        CampaignCheckpointArtifact alternate,
        string mutation)
    {
        switch (mutation)
        {
            case "truncate":
                Assert.Equal([0x7B], File.ReadAllBytes(fixture.CheckpointPath));
                break;
            case "append":
                Assert.Equal(0x20, File.ReadAllBytes(fixture.CheckpointPath)[^1]);
                break;
            case "same-size":
                var expected = intended.ExactUtf8Json.ToArray();
                expected[expected.Length / 2] ^= 1;
                Assert.Equal(expected, File.ReadAllBytes(fixture.CheckpointPath));
                break;
            case "marker":
                Assert.Equal(
                    -1,
                    GetExtendedAttributeSize(
                        fixture.CheckpointPath,
                        "user.contractscribe.checkpoint-object",
                        nint.Zero,
                        0));
                break;
            case "other-c2":
                Assert.Equal(alternate.ExactUtf8Json.ToArray(), File.ReadAllBytes(fixture.CheckpointPath));
                break;
            case "mode":
                Assert.True(File.GetUnixFileMode(fixture.CheckpointPath).HasFlag(UnixFileMode.GroupRead));
                break;
            case "hard-link":
                Assert.True(File.Exists(Path.Join(fixture.Root, "checkpoint-cleanup-link")));
                break;
            case "replace-path":
                Assert.True(File.Exists(Path.Join(fixture.Root, "retained-checkpoint")));
                Assert.True(File.Exists(fixture.CheckpointPath));
                break;
            case "temp-collision":
                var record = File.ReadAllText(LeasePath(fixture));
                var tempPath = Path.Join(fixture.StateDirectory, LeaseValue(record, "temp="));
                Assert.Equal("collision", File.ReadAllText(tempPath));
                break;
            case "lease-record":
                Assert.Contains(
                    intended.CheckpointRevision == 0 ? "operation=replace" : "operation=create",
                    File.ReadAllText(LeasePath(fixture)),
                    StringComparison.Ordinal);
                break;
        }
    }

    private static string LeasePath(StoreFixture fixture) => Path.Join(
        fixture.StateDirectory,
        ".campaign.json.contractscribe-checkpoint-lease");

    private static string LeaseValue(string record, string prefix) => Assert.Single(
        record.Split('\n'),
        line => line.StartsWith(prefix, StringComparison.Ordinal))[prefix.Length..];

    private static byte[] CanonicalLookingUnrelatedLeaseRecord()
    {
        const string token = "11111111111111111111111111111111";
        return System.Text.Encoding.ASCII.GetBytes(string.Join('\n',
            "contract-scribe-checkpoint-lease-v1",
            "operation=create",
            "expected-revision=-",
            "expected-sha256=-",
            "intended-revision=7",
            $"intended-sha256={new string('a', 64)}",
            $"token={token}",
            $"temp=.campaign.json.contractscribe-checkpoint-7-{token}.tmp",
            "temp-device=1",
            "temp-inode=1",
            "temp-mount=1",
            string.Empty));
    }

    [SupportedOSPlatform("linux")]
    private static void AssertFreshLeaseMutationPreserved(
        string leasePath,
        string mutation,
        byte[] changedBytes,
        byte[] changedMarker)
    {
        if (mutation == "wrong-marker")
        {
            Assert.Empty(File.ReadAllBytes(leasePath));
            Assert.Equal(changedMarker, ReadObjectMarker(leasePath));
            return;
        }
        Assert.Equal(changedBytes, File.ReadAllBytes(leasePath));
        Assert.Equal(
            (nint)(-1),
            GetExtendedAttributeSize(
                leasePath,
                "user.contractscribe.checkpoint-object",
                nint.Zero,
                0));
    }

    [SupportedOSPlatform("linux")]
    private static void SetObjectMarker(string path, byte[] marker)
    {
        Assert.Equal(
            0,
            SetExtendedAttributeValue(
                path,
                "user.contractscribe.checkpoint-object",
                marker,
                checked((nuint)marker.Length),
                1));
    }

    [SupportedOSPlatform("linux")]
    private static byte[] ReadObjectMarker(string path)
    {
        var size = GetExtendedAttributeSize(
            path,
            "user.contractscribe.checkpoint-object",
            nint.Zero,
            0);
        Assert.True(size > 0);
        var marker = new byte[checked((int)size)];
        Assert.Equal(
            size,
            GetExtendedAttributeValue(
                path,
                "user.contractscribe.checkpoint-object",
                marker,
                checked((nuint)marker.Length)));
        return marker;
    }

    [SupportedOSPlatform("linux")]
    private static byte[] ReadNativeBytes(string path)
    {
        using var handle = OpenNative(path, flags: 0);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        long offset = 0;
        while (true)
        {
            var count = RandomAccess.Read(handle, buffer, offset);
            if (count == 0)
            {
                return output.ToArray();
            }
            output.Write(buffer, 0, count);
            offset += count;
        }
    }

    [SupportedOSPlatform("linux")]
    private static string ReadNativeText(string path) =>
        System.Text.Encoding.UTF8.GetString(ReadNativeBytes(path));

    [SupportedOSPlatform("linux")]
    private static void WriteNativeBytes(
        string path,
        ReadOnlySpan<byte> bytes,
        bool create = false,
        bool exclusive = false)
    {
        const int writeOnly = 1;
        const int createFlag = 0x40;
        const int exclusiveFlag = 0x80;
        const int truncate = 0x200;
        var flags = writeOnly | truncate;
        if (create)
        {
            flags |= createFlag;
        }
        if (exclusive)
        {
            flags |= exclusiveFlag;
        }

        using var handle = OpenNative(path, flags);
        var offset = 0;
        while (offset < bytes.Length)
        {
            RandomAccess.Write(handle, bytes[offset..], offset);
            offset = bytes.Length;
        }
    }

    [SupportedOSPlatform("linux")]
    private static SafeFileHandle OpenNative(string path, int flags)
    {
        var descriptor = OpenFile(path, flags, 0x180);
        Assert.True(descriptor >= 0, $"open failed for '{path}' with errno {Marshal.GetLastPInvokeError()}");
        return new SafeFileHandle((nint)descriptor, ownsHandle: true);
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

    [DllImport("libc", EntryPoint = "removexattr", SetLastError = true)]
    private static extern int RemoveExtendedAttribute(string path, string name);

    [DllImport("libc", EntryPoint = "getxattr", SetLastError = true)]
    private static extern nint GetExtendedAttributeSize(string path, string name, nint value, nuint size);

    [DllImport("libc", EntryPoint = "getxattr", SetLastError = true)]
    private static extern nint GetExtendedAttributeValue(
        string path,
        string name,
        [Out] byte[] value,
        nuint size);

    [DllImport("libc", EntryPoint = "setxattr", SetLastError = true)]
    private static extern int SetExtendedAttributeValue(
        string path,
        string name,
        [In] byte[] value,
        nuint size,
        int flags);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenFile(string path, int flags, uint mode);

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
