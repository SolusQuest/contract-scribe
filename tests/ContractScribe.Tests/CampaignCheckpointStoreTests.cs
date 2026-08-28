using ContractScribe.Cli;
using ContractScribe.Core;
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
