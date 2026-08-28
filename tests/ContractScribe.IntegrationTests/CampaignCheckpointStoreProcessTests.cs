using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Runtime.Versioning;
using ContractScribe.Core;

namespace ContractScribe.Roslyn.IntegrationTests;

[Collection("Integration process lane 2")]
public sealed class CampaignCheckpointStoreProcessTests
{
    private const string WorkerVariable = "CONTRACTSCRIBE_CHECKPOINT_WORKER";

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Two_processes_share_one_live_lease_and_only_one_publishes()
    {
        if (!IsLinuxX64())
        {
            return;
        }

        using var fixture = ProcessFixture.Create();
        var first = RunWorkerAsync(fixture, "hold-after-record", "first");
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
            _ => null,
        };
        var artifact = CreateArtifact();
        var result = await CreateStore(checkpointPath, hook).CreateIfAbsentAsync(
            artifact.ExactUtf8Json.AsMemory(),
            artifact.CheckpointRevision,
            artifact.Sha256,
            CancellationToken.None);
        await File.WriteAllTextAsync(resultPath, result.Kind.ToString());
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
        var artifact = CreateArtifact();
        Assert.Equal(CampaignCheckpointReadKind.Found, result.Kind);
        Assert.Equal(artifact.CheckpointRevision, result.CheckpointRevision);
        Assert.Equal(artifact.Sha256, result.Sha256);
        Assert.True(result.ExactUtf8Json.AsSpan().SequenceEqual(artifact.ExactUtf8Json.AsSpan()));
    }

    private static CampaignCheckpointArtifact CreateArtifact()
    {
        var path = Path.Join(
            RepositoryRoot(),
            "tests", "fixtures", "campaign", "state", "empty-terminal.json");
        return Assert.IsType<CampaignCheckpointArtifact>(
            CampaignStateJson.Parse(File.ReadAllBytes(path)).Artifact);
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
}
