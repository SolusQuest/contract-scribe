using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Runtime.Versioning;
using System.Text;
using ContractScribe.Core;

namespace ContractScribe.Roslyn.IntegrationTests;

[Collection("Integration process lane 2")]
public sealed class CampaignCheckpointStoreProcessTests
{
    private const string WorkerVariable = "CONTRACTSCRIBE_CHECKPOINT_WORKER";

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Old_timestamps_do_not_make_a_live_lease_stale_and_only_one_process_publishes()
    {
        if (!IsLinuxX64())
        {
            return;
        }

        using var fixture = ProcessFixture.Create();
        var first = RunWorkerAsync(fixture, "hold-after-record", "first");
        await WaitForFileAsync(fixture.ReadyPath, TimeSpan.FromSeconds(20));
        foreach (var residue in Directory.EnumerateFiles(fixture.StateDirectory))
        {
            File.SetLastWriteTimeUtc(residue, DateTime.UnixEpoch);
        }

        await RunWorkerAsync(fixture, "create", "second");
        Assert.Equal("Unwritable", await File.ReadAllTextAsync(fixture.ResultPath("second")));
        await File.WriteAllTextAsync(fixture.ReleasePath, "release");
        await first;

        Assert.Equal("Written", await File.ReadAllTextAsync(fixture.ResultPath("first")));
        AssertExact(await CreateStore(fixture.CheckpointPath).ReadAsync(CancellationToken.None));
        Assert.Single(Directory.EnumerateFileSystemEntries(fixture.StateDirectory));
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Contender_never_locks_a_fresh_but_uninitialized_lease_inode()
    {
        if (!IsLinuxX64())
        {
            return;
        }

        using var fixture = ProcessFixture.Create();
        var first = RunWorkerAsync(fixture, "hold-after-lease-create", "first");
        await WaitForFileAsync(fixture.ReadyPath, TimeSpan.FromSeconds(20));

        await RunWorkerAsync(fixture, "create", "second");
        Assert.Equal("Unwritable", await File.ReadAllTextAsync(fixture.ResultPath("second")));
        await File.WriteAllTextAsync(fixture.ReleasePath, "release");
        await first;

        Assert.Equal("Written", await File.ReadAllTextAsync(fixture.ResultPath("first")));
        AssertExact(await CreateStore(fixture.CheckpointPath).ReadAsync(CancellationToken.None));
        Assert.Single(Directory.EnumerateFileSystemEntries(fixture.StateDirectory));
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Crash_before_publish_recovers_only_the_witnessed_temp_then_retries_fresh()
    {
        if (!IsLinuxX64())
        {
            return;
        }

        using var fixture = ProcessFixture.Create();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunWorkerAsync(fixture, "crash-after-record", "crashed"));

        var artifact = CreateArtifact();
        var recovered = await CreateStore(fixture.CheckpointPath).CreateIfAbsentAsync(
            artifact.ExactUtf8Json.AsMemory(),
            artifact.CheckpointRevision,
            artifact.Sha256,
            CancellationToken.None);

        Assert.Equal(CampaignCheckpointWriteKind.Written, recovered.Kind);
        AssertExact(await CreateStore(fixture.CheckpointPath).ReadAsync(CancellationToken.None));
        Assert.Single(Directory.EnumerateFileSystemEntries(fixture.StateDirectory));
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Recovery_preserves_a_substituted_same_name_temp_inode()
    {
        if (!IsLinuxX64())
        {
            return;
        }

        using var fixture = ProcessFixture.Create();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunWorkerAsync(fixture, "crash-after-record", "crashed"));
        var tempPath = Assert.Single(
            Directory.EnumerateFileSystemEntries(fixture.StateDirectory),
            path => path.EndsWith(".tmp", StringComparison.Ordinal));
        var retainedOriginal = Path.Join(fixture.ControlDirectory, "retained-original-temp");
        File.Move(tempPath, retainedOriginal);
        await File.WriteAllTextAsync(tempPath, "collision");
        File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var artifact = CreateArtifact();

        var result = await CreateStore(fixture.CheckpointPath).CreateIfAbsentAsync(
            artifact.ExactUtf8Json.AsMemory(),
            artifact.CheckpointRevision,
            artifact.Sha256,
            CancellationToken.None);

        Assert.Equal(CampaignCheckpointWriteKind.Unwritable, result.Kind);
        Assert.Equal("collision", await File.ReadAllTextAsync(tempPath));
        Assert.Equal(2, Directory.EnumerateFileSystemEntries(fixture.StateDirectory).Count());
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Crash_after_publish_recovers_lost_ack_without_republishing_partial_bytes()
    {
        if (!IsLinuxX64())
        {
            return;
        }

        using var fixture = ProcessFixture.Create();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunWorkerAsync(fixture, "crash-after-publish", "crashed"));

        var artifact = CreateArtifact();
        var recovered = await CreateStore(fixture.CheckpointPath).CreateIfAbsentAsync(
            artifact.ExactUtf8Json.AsMemory(),
            artifact.CheckpointRevision,
            artifact.Sha256,
            CancellationToken.None);

        Assert.Equal(CampaignCheckpointWriteKind.AlreadyPresent, recovered.Kind);
        AssertExact(await CreateStore(fixture.CheckpointPath).ReadAsync(CancellationToken.None));
        Assert.Single(Directory.EnumerateFileSystemEntries(fixture.StateDirectory));
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Fresh_process_replace_preserves_exact_conditional_outcomes()
    {
        if (!IsLinuxX64())
        {
            return;
        }

        var predecessor = CreateOpenArtifact();
        var successor = Assert.IsType<CampaignCheckpointArtifact>(
            CampaignStateReducer.Stop(predecessor, CampaignTerminalKind.Cancelled).Artifact);

        using var exact = ProcessFixture.Create();
        Assert.Equal(
            CampaignCheckpointWriteKind.Written,
            (await CreateStore(exact.CheckpointPath).CreateIfAbsentAsync(
                predecessor.ExactUtf8Json.AsMemory(),
                predecessor.CheckpointRevision,
                predecessor.Sha256,
                CancellationToken.None)).Kind);
        await RunWorkerAsync(exact, "replace", "replace");
        Assert.Equal("Written", await File.ReadAllTextAsync(exact.ResultPath("replace")));
        AssertExact(await CreateStore(exact.CheckpointPath).ReadAsync(CancellationToken.None), successor);

        using var missing = ProcessFixture.Create();
        await RunWorkerAsync(missing, "replace-missing", "missing");
        Assert.Equal("PredecessorMissing", await File.ReadAllTextAsync(missing.ResultPath("missing")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(missing.StateDirectory));

        using var mismatch = ProcessFixture.Create();
        Assert.Equal(
            CampaignCheckpointWriteKind.Written,
            (await CreateStore(mismatch.CheckpointPath).CreateIfAbsentAsync(
                predecessor.ExactUtf8Json.AsMemory(),
                predecessor.CheckpointRevision,
                predecessor.Sha256,
                CancellationToken.None)).Kind);
        await RunWorkerAsync(mismatch, "replace-mismatch", "mismatch");
        Assert.Equal("CurrentMismatch", await File.ReadAllTextAsync(mismatch.ResultPath("mismatch")));
        AssertExact(await CreateStore(mismatch.CheckpointPath).ReadAsync(CancellationToken.None), predecessor);
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Stale_recovery_releases_old_domain_before_one_fresh_writer_wins()
    {
        if (!IsLinuxX64())
        {
            return;
        }

        using var fixture = ProcessFixture.Create();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunWorkerAsync(fixture, "crash-after-record", "crashed"));
        var recovering = RunWorkerAsync(fixture, "hold-after-stale-recovery", "recovering");
        await WaitForFileAsync(fixture.ReadyPath, TimeSpan.FromSeconds(20));

        await RunWorkerAsync(fixture, "create", "winner");
        Assert.Equal("Written", await File.ReadAllTextAsync(fixture.ResultPath("winner")));
        await File.WriteAllTextAsync(fixture.ReleasePath, "release");
        await recovering;

        Assert.Equal("Unwritable", await File.ReadAllTextAsync(fixture.ResultPath("recovering")));
        AssertExact(await CreateStore(fixture.CheckpointPath).ReadAsync(CancellationToken.None));
        Assert.Single(Directory.EnumerateFileSystemEntries(fixture.StateDirectory));
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Witnessed_partial_temp_is_deleted_but_never_adopted()
    {
        if (!IsLinuxX64())
        {
            return;
        }

        using var fixture = ProcessFixture.Create();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunWorkerAsync(fixture, "crash-after-record", "crashed"));
        var temp = Assert.Single(
            Directory.EnumerateFiles(fixture.StateDirectory),
            path => path.EndsWith(".tmp", StringComparison.Ordinal));
        await File.WriteAllTextAsync(temp, "partial");
        var artifact = CreateArtifact();

        var recovered = await CreateStore(fixture.CheckpointPath).CreateIfAbsentAsync(
            artifact.ExactUtf8Json.AsMemory(),
            artifact.CheckpointRevision,
            artifact.Sha256,
            CancellationToken.None);

        Assert.Equal(CampaignCheckpointWriteKind.Written, recovered.Kind);
        AssertExact(await CreateStore(fixture.CheckpointPath).ReadAsync(CancellationToken.None));
        Assert.Single(Directory.EnumerateFileSystemEntries(fixture.StateDirectory));
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Strict_record_and_object_marker_faults_are_never_recovery_authority()
    {
        if (!IsLinuxX64())
        {
            return;
        }

        using var copiedLease = ProcessFixture.Create();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunWorkerAsync(copiedLease, "crash-after-record", "crashed"));
        var lease = Assert.Single(
            Directory.EnumerateFiles(copiedLease.StateDirectory),
            path => path.EndsWith("checkpoint-lease", StringComparison.Ordinal));
        var retainedLease = Path.Join(copiedLease.ControlDirectory, "retained-original-lease");
        File.Move(lease, retainedLease);
        File.Copy(retainedLease, lease);
        File.SetUnixFileMode(lease, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var artifact = CreateArtifact();
        var copiedResult = await CreateStore(copiedLease.CheckpointPath).CreateIfAbsentAsync(
            artifact.ExactUtf8Json.AsMemory(),
            artifact.CheckpointRevision,
            artifact.Sha256,
            CancellationToken.None);
        Assert.Equal(CampaignCheckpointWriteKind.Unwritable, copiedResult.Kind);
        Assert.True(File.Exists(lease));

        using var malformedToken = ProcessFixture.Create();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunWorkerAsync(malformedToken, "crash-after-record", "crashed"));
        var malformedLease = Assert.Single(
            Directory.EnumerateFiles(malformedToken.StateDirectory),
            path => path.EndsWith("checkpoint-lease", StringComparison.Ordinal));
        var originalRecord = await File.ReadAllTextAsync(malformedLease);
        var tokenLine = Assert.Single(
            originalRecord.Split('\n'),
            line => line.StartsWith("token=", StringComparison.Ordinal));
        var malformedRecord = originalRecord.Replace(
            tokenLine,
            tokenLine.ToUpperInvariant(),
            StringComparison.Ordinal);
        await File.WriteAllTextAsync(malformedLease, malformedRecord);
        var malformedResult = await CreateStore(malformedToken.CheckpointPath).CreateIfAbsentAsync(
            artifact.ExactUtf8Json.AsMemory(),
            artifact.CheckpointRevision,
            artifact.Sha256,
            CancellationToken.None);
        Assert.Equal(CampaignCheckpointWriteKind.Unwritable, malformedResult.Kind);
        Assert.Equal(malformedRecord, await File.ReadAllTextAsync(malformedLease));

        using var missingMarker = ProcessFixture.Create();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunWorkerAsync(missingMarker, "crash-after-record", "crashed"));
        var unmarkedTemp = Assert.Single(
            Directory.EnumerateFiles(missingMarker.StateDirectory),
            path => path.EndsWith(".tmp", StringComparison.Ordinal));
        RemoveMarker(unmarkedTemp);
        var missingMarkerResult = await CreateStore(missingMarker.CheckpointPath).CreateIfAbsentAsync(
            artifact.ExactUtf8Json.AsMemory(),
            artifact.CheckpointRevision,
            artifact.Sha256,
            CancellationToken.None);
        Assert.Equal(CampaignCheckpointWriteKind.Unwritable, missingMarkerResult.Kind);
        Assert.True(File.Exists(unmarkedTemp));

        using var wrongMarker = ProcessFixture.Create();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunWorkerAsync(wrongMarker, "crash-after-record", "crashed"));
        var markedTemp = Assert.Single(
            Directory.EnumerateFiles(wrongMarker.StateDirectory),
            path => path.EndsWith(".tmp", StringComparison.Ordinal));
        SetMarker(markedTemp, "wrong-marker");
        var markerResult = await CreateStore(wrongMarker.CheckpointPath).CreateIfAbsentAsync(
            artifact.ExactUtf8Json.AsMemory(),
            artifact.CheckpointRevision,
            artifact.Sha256,
            CancellationToken.None);
        Assert.Equal(CampaignCheckpointWriteKind.Unwritable, markerResult.Kind);
        Assert.True(File.Exists(markedTemp));
    }

    [Theory]
    [InlineData("cancel-after-temp-write", false)]
    [InlineData("cancel-after-publish", true)]
    [SupportedOSPlatform("linux")]
    public async Task Fresh_process_cancellation_cleans_before_publish_and_preserves_after(
        string mode,
        bool published)
    {
        if (!IsLinuxX64())
        {
            return;
        }

        using var fixture = ProcessFixture.Create();
        await RunWorkerAsync(fixture, mode, "cancelled");

        Assert.Equal("Cancelled", await File.ReadAllTextAsync(fixture.ResultPath("cancelled")));
        var read = await CreateStore(fixture.CheckpointPath).ReadAsync(CancellationToken.None);
        if (published)
        {
            AssertExact(read);
            Assert.Single(Directory.EnumerateFileSystemEntries(fixture.StateDirectory));
        }
        else
        {
            Assert.Equal(CampaignCheckpointReadKind.NotFound, read.Kind);
            Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.StateDirectory));
        }
    }

    [Theory]
    [InlineData("crash-before-publish", "Written")]
    [InlineData("crash-after-readback", "AlreadyPresent")]
    [SupportedOSPlatform("linux")]
    public async Task Crashes_around_rename_and_readback_recover_only_complete_state(
        string crashMode,
        string recoveryKind)
    {
        if (!IsLinuxX64())
        {
            return;
        }

        using var fixture = ProcessFixture.Create();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunWorkerAsync(fixture, crashMode, "crashed"));
        var artifact = CreateArtifact();

        var recovered = await CreateStore(fixture.CheckpointPath).CreateIfAbsentAsync(
            artifact.ExactUtf8Json.AsMemory(),
            artifact.CheckpointRevision,
            artifact.Sha256,
            CancellationToken.None);

        Assert.Equal(recoveryKind, recovered.Kind.ToString());
        AssertExact(await CreateStore(fixture.CheckpointPath).ReadAsync(CancellationToken.None));
        Assert.Single(Directory.EnumerateFileSystemEntries(fixture.StateDirectory));
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Fifo_checkpoint_and_lease_names_return_without_blocking()
    {
        if (!IsLinuxX64())
        {
            return;
        }

        using var checkpointFixture = ProcessFixture.Create();
        Assert.Equal(0, MakeFifo(checkpointFixture.CheckpointPath, 0x180));
        var read = await CreateStore(checkpointFixture.CheckpointPath)
            .ReadAsync(CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(CampaignCheckpointReadKind.Invalid, read.Kind);

        using var leaseFixture = ProcessFixture.Create();
        var leasePath = Path.Join(
            leaseFixture.StateDirectory,
            ".campaign.json.contractscribe-checkpoint-lease");
        Assert.Equal(0, MakeFifo(leasePath, 0x180));
        var artifact = CreateArtifact();
        var write = await CreateStore(leaseFixture.CheckpointPath)
            .CreateIfAbsentAsync(
                artifact.ExactUtf8Json.AsMemory(),
                artifact.CheckpointRevision,
                artifact.Sha256,
                CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(CampaignCheckpointWriteKind.Unwritable, write.Kind);
        Assert.True(File.Exists(leasePath));
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Process_worker()
    {
        var operation = Environment.GetEnvironmentVariable(WorkerVariable);
        if (string.IsNullOrEmpty(operation))
        {
            return;
        }

        var checkpointPath = RequireWorkerValue("CONTRACTSCRIBE_CHECKPOINT_PATH");
        var resultPath = RequireWorkerValue("CONTRACTSCRIBE_CHECKPOINT_RESULT");
        var readyPath = RequireWorkerValue("CONTRACTSCRIBE_CHECKPOINT_READY");
        var releasePath = RequireWorkerValue("CONTRACTSCRIBE_CHECKPOINT_RELEASE");
        using var cancellation = new CancellationTokenSource();
        Action<string>? hook = operation switch
        {
            "hold-after-lease-create" => phase =>
            {
                if (phase == "after-lease-create-before-lock")
                {
                    WaitAtBarrier(readyPath, releasePath);
                }
            }
            ,
            "hold-after-record" => phase =>
            {
                if (phase == "after-lease-record")
                {
                    WaitAtBarrier(readyPath, releasePath);
                }
            }
            ,
            "crash-after-record" => phase =>
            {
                if (phase == "after-lease-record")
                {
                    Environment.Exit(73);
                }
            }
            ,
            "crash-after-temp-write" => phase =>
            {
                if (phase == "after-temp-write")
                {
                    Environment.Exit(73);
                }
            }
            ,
            "crash-after-publish" => phase =>
            {
                if (phase == "after-publish-before-readback")
                {
                    Environment.Exit(73);
                }
            }
            ,
            "crash-before-publish" => phase =>
            {
                if (phase == "before-publish")
                {
                    Environment.Exit(73);
                }
            }
            ,
            "crash-after-readback" => phase =>
            {
                if (phase == "after-readback-before-cleanup")
                {
                    Environment.Exit(73);
                }
            }
            ,
            "hold-after-stale-recovery" => phase =>
            {
                if (phase == "after-stale-recovery-before-reacquire")
                {
                    WaitAtBarrier(readyPath, releasePath);
                }
            }
            ,
            "cancel-after-temp-write" => phase =>
            {
                if (phase == "after-temp-write")
                {
                    cancellation.Cancel();
                }
            }
            ,
            "cancel-after-publish" => phase =>
            {
                if (phase == "after-publish-before-readback")
                {
                    cancellation.Cancel();
                }
            }
            ,
            _ => null,
        };
        var store = CreateStore(checkpointPath, hook);
        var artifact = CreateArtifact();
        var predecessor = CreateOpenArtifact();
        var successor = Assert.IsType<CampaignCheckpointArtifact>(
            CampaignStateReducer.Stop(predecessor, CampaignTerminalKind.Cancelled).Artifact);
        try
        {
            var result = operation switch
            {
                "replace" or "replace-missing" => await store.ReplaceIfCurrentAsync(
                    predecessor.CheckpointRevision,
                    predecessor.Sha256,
                    successor.ExactUtf8Json.AsMemory(),
                    successor.CheckpointRevision,
                    successor.Sha256,
                    cancellation.Token),
                "replace-mismatch" => await store.ReplaceIfCurrentAsync(
                    predecessor.CheckpointRevision,
                    new string('a', 64),
                    successor.ExactUtf8Json.AsMemory(),
                    successor.CheckpointRevision,
                    successor.Sha256,
                    cancellation.Token),
                _ => await store.CreateIfAbsentAsync(
                    artifact.ExactUtf8Json.AsMemory(),
                    artifact.CheckpointRevision,
                    artifact.Sha256,
                    cancellation.Token),
            };
            await File.WriteAllTextAsync(resultPath, result.Kind.ToString());
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            await File.WriteAllTextAsync(resultPath, "Cancelled");
        }
    }

    private static void WaitAtBarrier(string readyPath, string releasePath)
    {
        File.WriteAllText(readyPath, "ready");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!File.Exists(releasePath) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
        }
        if (!File.Exists(releasePath))
        {
            throw new TimeoutException("worker barrier timed out");
        }
    }

    private static async Task RunWorkerAsync(ProcessFixture fixture, string operation, string resultName)
    {
        var environment = new Dictionary<string, string?>
        {
            [WorkerVariable] = operation,
            ["CONTRACTSCRIBE_CHECKPOINT_PATH"] = fixture.CheckpointPath,
            ["CONTRACTSCRIBE_CHECKPOINT_RESULT"] = fixture.ResultPath(resultName),
            ["CONTRACTSCRIBE_CHECKPOINT_READY"] = fixture.ReadyPath,
            ["CONTRACTSCRIBE_CHECKPOINT_RELEASE"] = fixture.ReleasePath,
        };
        await OwnedProcessRunner.RunAsync(
            "dotnet",
            RepositoryRoot(),
            [
                "test",
                "tests/ContractScribe.IntegrationTests/ContractScribe.IntegrationTests.csproj",
                "--configuration", "Release",
                "--no-build",
                "--no-restore",
                "--filter",
                $"FullyQualifiedName={typeof(CampaignCheckpointStoreProcessTests).FullName}.Process_worker",
            ],
            TimeSpan.FromSeconds(40),
            environment: environment);
    }

    private static ICampaignCheckpointStore CreateStore(
        string checkpointPath,
        Action<string>? hook = null)
    {
        var assemblyPath = Path.Join(
            RepositoryRoot(),
            "src", "ContractScribe.Cli", "bin", "Release", "net10.0", "ContractScribe.Cli.dll");
        var assembly = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(
                candidate => string.Equals(candidate.Location, assemblyPath, StringComparison.OrdinalIgnoreCase))
            ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
        var type = assembly.GetType("ContractScribe.Cli.FileCampaignCheckpointStore", throwOnError: true)!;
        var port = Assert.Single(type.GetInterfaces(), candidate => candidate == typeof(ICampaignCheckpointStore));
        Assert.Same(typeof(ICampaignCheckpointStore).Assembly, port.Assembly);
        return Assert.IsAssignableFrom<ICampaignCheckpointStore>(Activator.CreateInstance(
            type,
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [checkpointPath, RepositoryRoot(), hook],
            culture: null));
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        while (!File.Exists(path))
        {
            await Task.Delay(20, deadline.Token);
        }
    }

    private static void AssertExact(CampaignCheckpointReadResult result)
    {
        AssertExact(result, CreateArtifact());
    }

    private static void AssertExact(
        CampaignCheckpointReadResult result,
        CampaignCheckpointArtifact artifact)
    {
        Assert.Equal(CampaignCheckpointReadKind.Found, result.Kind);
        Assert.Equal(artifact.CheckpointRevision, result.CheckpointRevision);
        Assert.Equal(artifact.Sha256, result.Sha256);
        Assert.True(result.ExactUtf8Json.AsSpan().SequenceEqual(artifact.ExactUtf8Json.AsSpan()));
    }

    [SupportedOSPlatform("linux")]
    private static void SetMarker(string path, string marker)
    {
        var bytes = Encoding.ASCII.GetBytes(marker);
        Assert.Equal(0, SetExtendedAttribute(
            path,
            "user.contractscribe.checkpoint-object",
            bytes,
            (nuint)bytes.Length,
            0));
    }

    [SupportedOSPlatform("linux")]
    private static void RemoveMarker(string path) => Assert.Equal(
        0,
        RemoveExtendedAttribute(path, "user.contractscribe.checkpoint-object"));

    private static CampaignCheckpointArtifact CreateArtifact()
    {
        var path = Path.Join(
            RepositoryRoot(),
            "tests", "fixtures", "campaign", "state", "empty-terminal.json");
        return Assert.IsType<CampaignCheckpointArtifact>(
            CampaignStateJson.Parse(File.ReadAllBytes(path)).Artifact);
    }

    private static CampaignCheckpointArtifact CreateOpenArtifact()
    {
        var fixture = CreateArtifact();
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

    private static string RequireWorkerValue(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException("worker configuration missing");

    private static string RepositoryRoot() => Path.GetFullPath(Path.Join(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", ".."));

    private static bool IsLinuxX64() =>
        OperatingSystem.IsLinux()
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    private sealed class ProcessFixture : IDisposable
    {
        private ProcessFixture(string root, string stateDirectory, string controlDirectory)
        {
            Root = root;
            StateDirectory = stateDirectory;
            ControlDirectory = controlDirectory;
            CheckpointPath = Path.Join(stateDirectory, "campaign.json");
            ReadyPath = Path.Join(controlDirectory, "ready");
            ReleasePath = Path.Join(controlDirectory, "release");
        }

        internal string Root { get; }
        internal string StateDirectory { get; }
        internal string ControlDirectory { get; }
        internal string CheckpointPath { get; }
        internal string ReadyPath { get; }
        internal string ReleasePath { get; }
        internal string ResultPath(string name) => Path.Join(ControlDirectory, $"{name}.result");

        [SupportedOSPlatform("linux")]
        internal static ProcessFixture Create()
        {
            var root = Path.Join(Path.GetTempPath(), $"contractscribe-process-{Guid.NewGuid():N}");
            var state = Path.Join(root, "state");
            var control = Path.Join(root, "control");
            Directory.CreateDirectory(state);
            Directory.CreateDirectory(control);
            File.SetUnixFileMode(
                state,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return new ProcessFixture(root, state, control);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MakeFifo(string path, uint mode);

    [DllImport("libc", EntryPoint = "setxattr", SetLastError = true)]
    private static extern int SetExtendedAttribute(
        string path,
        string name,
        byte[] value,
        nuint size,
        int flags);

    [DllImport("libc", EntryPoint = "removexattr", SetLastError = true)]
    private static extern int RemoveExtendedAttribute(string path, string name);
}
