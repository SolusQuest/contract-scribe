using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Cli;
using ContractScribe.Core;

namespace ContractScribe.Roslyn.IntegrationTests;

[Collection("Integration process lane 1")]
public sealed class CampaignChangedBaseProcessTests
{
    [Fact]
    public async Task ExactFixtureRevisions_WithIdenticalAuditAndContent_RotateAllCurrentAuthority()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await ProcessFixture.CreateAsync(executable: true);
        CopyRevision("revision-a", fixture.Repository.Root);

        await InterruptAtAsync(fixture, "start", "snapshot.changed-base.a",
            CampaignProcessBoundaryHooks.ProposalBeforeReservationCommit);
        var predecessor = ReadArtifact(fixture.StatePath);
        var predecessorKeys = predecessor.State.WorkItems.Select(item => item.WorkItemKey).ToArray();
        Assert.NotEmpty(predecessorKeys);

        CopyRevision("revision-b", fixture.Repository.Root);
        await InterruptAtAsync(fixture, "resume", "snapshot.changed-base.b",
            CampaignProcessBoundaryHooks.ProposalBeforeReservationCommit);
        var successor = ReadArtifact(fixture.StatePath);
        var successorKeys = successor.State.WorkItems.Select(item => item.WorkItemKey).ToArray();

        Assert.Equal(predecessor.State.Snapshot.RepositoryCommitmentSha256,
            successor.State.Snapshot.RepositoryCommitmentSha256);
        Assert.Equal(predecessor.State.Snapshot.InputCommitmentSha256,
            successor.State.Snapshot.InputCommitmentSha256);
        Assert.NotEqual(predecessor.State.Snapshot.ExecutionCommitmentSha256,
            successor.State.Snapshot.ExecutionCommitmentSha256);
        Assert.False(predecessorKeys.SequenceEqual(successorKeys));
        Assert.Equal(predecessor.Sha256, successor.State.Predecessor!.FinalCheckpointSha256);
        Assert.All(successor.State.WorkItems, item =>
        {
            Assert.Equal(CampaignWorkStatus.Planned, item.Status);
            Assert.Null(item.TrustedProposal);
            Assert.Equal(0, item.OuterAttemptCount);
            Assert.Equal(0, item.CandidateAttemptCount);
        });
        Assert.Null(successor.State.ActiveReservation);
        Assert.Null(successor.State.CandidateObservation);
        Assert.Null(successor.State.CumulativeOutcome);
        Assert.Equal(0, fixture.Server.RequestCount);

        var completed = await CampaignCliProcessTests.RunAsync(
            fixture.Args("resume", "snapshot.changed-base.b"), TimeSpan.FromMinutes(5));
        var completedArtifact = ReadArtifact(fixture.StatePath);
        CampaignCliProcessTests.AssertCampaign(
            completed, 0, "campaign.complete", completedArtifact.CheckpointRevision);
        Assert.True(fixture.Server.RequestCount > 0);
    }

    [Theory]
    [InlineData(CampaignProcessBoundaryHooks.CheckpointBeforeReplacement, false, false)]
    [InlineData(CampaignProcessBoundaryHooks.CheckpointAfterReplacementBeforeReadback, true, false)]
    [InlineData(CampaignProcessBoundaryHooks.ProposalBeforeReservationCommit, true, true)]
    public async Task SupersessionCrash_LeavesExactlyOneCompleteAuthority(
        string hook,
        bool successorExpected,
        bool noReservationExpected)
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await ProcessFixture.CreateAsync(executable: true);
        await InterruptAtAsync(fixture, "start", "snapshot.crash.a",
            CampaignProcessBoundaryHooks.ProposalBeforeReservationCommit);
        var predecessorBytes = await File.ReadAllBytesAsync(fixture.StatePath);
        var predecessor = ReadArtifact(fixture.StatePath);

        await InterruptAtAsync(fixture, "resume", "snapshot.crash.b", hook);
        var observedBytes = await File.ReadAllBytesAsync(fixture.StatePath);
        var observed = ReadArtifact(fixture.StatePath);

        if (successorExpected)
        {
            Assert.Equal("snapshot.crash.b", observed.State.Snapshot.OpaqueSnapshotBinding);
            Assert.Equal(predecessor.Sha256, observed.State.Predecessor!.FinalCheckpointSha256);
            if (noReservationExpected) Assert.Null(observed.State.ActiveReservation);
        }
        else
        {
            Assert.Equal(predecessorBytes, observedBytes);
            Assert.Equal("snapshot.crash.a", observed.State.Snapshot.OpaqueSnapshotBinding);
        }

        var recovered = await CampaignCliProcessTests.RunAsync(
            fixture.Args("resume", "snapshot.crash.b"), TimeSpan.FromMinutes(5));
        var current = ReadArtifact(fixture.StatePath);
        CampaignCliProcessTests.AssertCampaign(recovered, 0, "campaign.complete", current.CheckpointRevision);
    }

    [Fact]
    public async Task DriftAndProductMismatch_LeaveThePredecessorByteIdenticalWithoutDispatch()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await ProcessFixture.CreateAsync(executable: true);
        await InterruptAtAsync(fixture, "start", "snapshot.drift.a",
            CampaignProcessBoundaryHooks.ProposalBeforeReservationCommit);
        var originalState = await File.ReadAllBytesAsync(fixture.StatePath);
        var originalConfiguration = await File.ReadAllBytesAsync(fixture.ConfigurationPath);
        var sourcePath = Path.Join(fixture.Repository.Root, "App", "App.cs");
        var originalSource = await File.ReadAllBytesAsync(sourcePath);

        await File.WriteAllTextAsync(sourcePath,
            "namespace Fixture;\n/// <summary>Provides fixture operations.</summary>\npublic static class App\n{\n    public static void Changed() { }\n}\n");
        var staleSameSnapshot = await CampaignCliProcessTests.RunAsync(
            fixture.Args("resume", "snapshot.drift.a"), TimeSpan.FromMinutes(3));
        CampaignCliProcessTests.AssertCampaign(
            staleSameSnapshot, 4, "campaign.incompatible-snapshot", 0);
        Assert.Equal(originalState, await File.ReadAllBytesAsync(fixture.StatePath));
        Assert.Equal(0, fixture.Server.RequestCount);
        await File.WriteAllBytesAsync(sourcePath, originalSource);

        await File.WriteAllTextAsync(
            Path.Join(fixture.Repository.Root, "policy.json"),
            CampaignCliProcessTests.OptionalPolicy);
        var policyDrift = await CampaignCliProcessTests.RunAsync(
            fixture.Args("resume", "snapshot.drift.b"), TimeSpan.FromMinutes(3));
        CampaignCliProcessTests.AssertCampaign(policyDrift, 4, "campaign.incompatible-snapshot", 0);
        Assert.Equal(originalState, await File.ReadAllBytesAsync(fixture.StatePath));
        Assert.Equal(0, fixture.Server.RequestCount);

        await File.WriteAllTextAsync(
            Path.Join(fixture.Repository.Root, "policy.json"),
            CampaignCliProcessTests.RequiredPolicy);
        await CampaignCliProcessTests.WriteConfigurationAsync(
            fixture.ConfigurationPath,
            fixture.Server.Endpoint,
            maximumPatchElapsedMilliseconds: 1);
        var configurationDrift = await CampaignCliProcessTests.RunAsync(
            fixture.Args("resume", "snapshot.drift.b"), TimeSpan.FromMinutes(3));
        CampaignCliProcessTests.AssertCampaign(configurationDrift, 4, "campaign.incompatible-snapshot", 0);
        Assert.Equal(originalState, await File.ReadAllBytesAsync(fixture.StatePath));
        Assert.Equal(0, fixture.Server.RequestCount);

        var invalidProduct = JsonNode.Parse(originalConfiguration)!.AsObject();
        invalidProduct["planning"]!["productContractRevisionSha256"] = new string('0', 64);
        await File.WriteAllTextAsync(
            fixture.ConfigurationPath,
            invalidProduct.ToJsonString(),
            new UTF8Encoding(false, true));
        var productMismatch = await CampaignCliProcessTests.RunAsync(
            fixture.Args("resume", "snapshot.drift.b"), TimeSpan.FromMinutes(3));
        CampaignCliProcessTests.AssertCampaign(productMismatch, 4, "campaign.invalid-configuration", null);
        Assert.Equal(originalState, await File.ReadAllBytesAsync(fixture.StatePath));
        Assert.Equal(0, fixture.Server.RequestCount);
    }

    [Fact]
    public async Task TargetEvolution_SelectsOnlyTheFreshAuditAndPlan()
    {
        if (!OperatingSystem.IsLinux()) return;
        var matrixPath = Path.Join(
            CampaignCliProcessTests.RepositoryRoot,
            "tests", "fixtures", "campaign", "changed-base", "target-evolution-matrix.json");
        using var matrix = JsonDocument.Parse(await File.ReadAllBytesAsync(matrixPath));
        foreach (var evolution in matrix.RootElement.EnumerateArray().Select(item => item.GetString()!))
        {
            await using var fixture = await ProcessFixture.CreateAsync(executable: true);
            await InterruptAtAsync(fixture, "start", "snapshot.target.a",
                CampaignProcessBoundaryHooks.ProposalBeforeReservationCommit);
            var predecessor = ReadArtifact(fixture.StatePath);
            var predecessorKeys = predecessor.State.WorkItems.Select(item => item.WorkItemKey).ToHashSet(
                StringComparer.Ordinal);
            var expectsNoWork = await ApplyTargetEvolutionAsync(fixture.Repository.Root, evolution);

            if (expectsNoWork)
            {
                var noWork = await CampaignCliProcessTests.RunAsync(
                    fixture.Args("resume", "snapshot.target.b"), TimeSpan.FromMinutes(5));
                var successor = ReadArtifact(fixture.StatePath);
                CampaignCliProcessTests.AssertCampaign(
                    noWork, 0, "campaign.no-work", successor.CheckpointRevision);
            }
            else
            {
                await InterruptAtAsync(fixture, "resume", "snapshot.target.b",
                    CampaignProcessBoundaryHooks.ProposalBeforeReservationCommit);
            }

            var current = ReadArtifact(fixture.StatePath);
            Assert.Equal("snapshot.target.b", current.State.Snapshot.OpaqueSnapshotBinding);
            Assert.Equal(predecessor.Sha256, current.State.Predecessor!.FinalCheckpointSha256);
            Assert.DoesNotContain(current.State.WorkItems, item => predecessorKeys.Contains(item.WorkItemKey));
            Assert.All(current.State.WorkItems, item =>
            {
                Assert.Null(item.TrustedProposal);
                Assert.Equal(0, item.OuterAttemptCount);
                Assert.Equal(0, item.CandidateAttemptCount);
            });
            Assert.Null(current.State.ActiveReservation);
            Assert.Null(current.State.CandidateObservation);
            Assert.Equal(0, fixture.Server.RequestCount);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentSupersession_ProducesOneSuccessorAndNoDuplicateDispatch(bool competing)
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await ProcessFixture.CreateAsync(executable: true);
        await InterruptAtAsync(fixture, "start", "snapshot.concurrent.a",
            CampaignProcessBoundaryHooks.ProposalBeforeReservationCommit);
        var firstAck = Path.Join(fixture.Outside, "concurrent.first.ack");
        var firstRelease = Path.Join(fixture.Outside, "concurrent.first.release");
        using var first = CampaignCliProcessTests.Start(
            fixture.Args("resume", "snapshot.concurrent.b"),
            CampaignCliProcessTests.HookEnvironment(
                CampaignProcessBoundaryHooks.CheckpointBeforeReplacement, firstAck, firstRelease));
        try
        {
            await CampaignCliProcessTests.WaitForFileAsync(
                firstAck, first, CampaignProcessBoundaryHooks.CheckpointBeforeReplacement, TimeSpan.FromMinutes(3));
            var blocked = await CampaignCliProcessTests.RunAsync(
                fixture.Args("resume", competing ? "snapshot.concurrent.c" : "snapshot.concurrent.b"),
                TimeSpan.FromMinutes(3));
            CampaignCliProcessTests.AssertCampaign(blocked, 4, "campaign.lease-conflict", null);
            Assert.Equal(0, fixture.Server.RequestCount);

            await File.WriteAllTextAsync(firstRelease, "release\n");
            await first.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(5));
            var winner = await first.CompleteAsync();
            var current = ReadArtifact(fixture.StatePath);
            Assert.Equal("snapshot.concurrent.b", current.State.Snapshot.OpaqueSnapshotBinding);
            Assert.Null(current.State.ActiveReservation);
            CampaignCliProcessTests.AssertCampaign(winner, 0, "campaign.complete", current.CheckpointRevision);
            Assert.Equal(1, fixture.Server.RequestCount);
        }
        finally
        {
            await CampaignCliProcessTests.StopAsync(first);
        }
    }

    [Fact]
    public async Task CompleteM4ProductionPath_RecoversSupersedesAndCompletes()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await ProcessFixture.CreateAsync(
            executable: true,
            maximumCampaignElapsedMilliseconds: 600_000);
        var first = await CampaignCliProcessTests.RunAsync(
            fixture.Args("start", "snapshot.production.a"), TimeSpan.FromMinutes(5));
        var accepted = ReadArtifact(fixture.StatePath);
        CampaignCliProcessTests.AssertCampaign(first, 0, "campaign.complete", accepted.CheckpointRevision);
        Assert.NotNull(accepted.State.CumulativeOutcome);
        Assert.NotNull(accepted.State.CandidateObservation);
        Assert.Contains(accepted.State.WorkItems, item => item.Status == CampaignWorkStatus.Accepted);
        var providerCalls = fixture.Server.RequestCount;

        await InterruptAtAsync(fixture, "resume", "snapshot.production.a",
            CampaignProcessBoundaryHooks.PatchBeforeDispatch);
        var ambiguous = ReadArtifact(fixture.StatePath);
        Assert.IsType<CampaignPatchReservation>(ambiguous.State.ActiveReservation);
        Assert.NotNull(ambiguous.State.CumulativeOutcome);
        Assert.Equal(providerCalls, fixture.Server.RequestCount);

        var sameSnapshot = await CampaignCliProcessTests.RunAsync(
            fixture.Args("resume", "snapshot.production.a"), TimeSpan.FromMinutes(5));
        Assert.DoesNotContain("campaign.host-contract-error", sameSnapshot.Stdout, StringComparison.Ordinal);
        var recovered = ReadArtifact(fixture.StatePath);
        Assert.Null(recovered.State.ActiveReservation);
        Assert.True(recovered.State.LineageCharges.PatchValidationInvocations
            >= accepted.State.LineageCharges.PatchValidationInvocations);

        await File.WriteAllTextAsync(
            Path.Join(fixture.Repository.Root, "App", "App.cs"),
            "namespace Fixture;\npublic static class App\n{\n    public static int Changed(int value) => value;\n}\n");
        var changed = await CampaignCliProcessTests.RunAsync(
            fixture.Args("resume", "snapshot.production.b"), TimeSpan.FromMinutes(5));
        var completed = ReadArtifact(fixture.StatePath);
        CampaignCliProcessTests.AssertCampaign(changed, 0, "campaign.complete", completed.CheckpointRevision);
        Assert.Equal("snapshot.production.b", completed.State.Snapshot.OpaqueSnapshotBinding);
        Assert.Equal(recovered.Sha256, completed.State.Predecessor!.FinalCheckpointSha256);
        Assert.True(completed.State.LineageCharges.ProviderRequests.TotalCharged
            >= recovered.State.LineageCharges.ProviderRequests.TotalCharged);
        Assert.All(completed.State.WorkItems, item => Assert.NotEqual(
            ambiguous.State.WorkItems[0].WorkItemKey, item.WorkItemKey));
        Assert.True(fixture.Server.RequestCount > providerCalls);
    }

    private static async Task InterruptAtAsync(
        ProcessFixture fixture,
        string operation,
        string snapshot,
        string hook)
    {
        var id = Guid.NewGuid().ToString("N");
        var acknowledgement = Path.Join(fixture.Outside, id + ".ack");
        using var running = CampaignCliProcessTests.Start(
            fixture.Args(operation, snapshot),
            new Dictionary<string, string?>
            {
                ["DOTNET_STARTUP_HOOKS"] = CampaignCliProcessTests.StartupHookPath,
                ["CONTRACTSCRIBE_TEST_CAMPAIGN_HOOK_NAME"] = hook,
                ["CONTRACTSCRIBE_TEST_CAMPAIGN_HOOK_ACK"] = acknowledgement,
            });
        try
        {
            await CampaignCliProcessTests.WaitForFileAsync(
                acknowledgement, running, hook, TimeSpan.FromMinutes(3));
            running.Process.Kill(entireProcessTree: true);
            await running.Process.WaitForExitAsync();
            var interrupted = await running.CompleteAsync();
            Assert.Empty(interrupted.Stdout);
            Assert.Empty(interrupted.Stderr);
            Assert.True(CampaignStateJson.Parse(await File.ReadAllBytesAsync(fixture.StatePath)).IsValid);
        }
        finally
        {
            await CampaignCliProcessTests.StopAsync(running);
        }
    }

    private static CampaignCheckpointArtifact ReadArtifact(string statePath)
    {
        var parsed = CampaignStateJson.Parse(File.ReadAllBytes(statePath));
        Assert.True(parsed.IsValid);
        return Assert.IsType<CampaignCheckpointArtifact>(parsed.Artifact);
    }

    private static async Task<bool> ApplyTargetEvolutionAsync(string repositoryRoot, string evolution)
    {
        var sourcePath = Path.Join(repositoryRoot, "App", "App.cs");
        switch (evolution)
        {
            case "disappeared":
                await File.WriteAllTextAsync(sourcePath,
                    "namespace Fixture;\n/// <summary>Provides fixture operations.</summary>\npublic static class App { }\n");
                return true;
            case "became-compliant":
                await File.WriteAllTextAsync(sourcePath,
                    "namespace Fixture;\n/// <summary>Provides fixture operations.</summary>\npublic static class App\n{\n    /// <summary>Runs the operation.</summary>\n    public static void Run() { }\n}\n");
                return true;
            case "moved-source-authority":
                await File.WriteAllTextAsync(sourcePath,
                    "namespace Fixture;\n/// <summary>Provides fixture operations.</summary>\npublic static class App { }\n");
                await File.WriteAllTextAsync(Path.Join(repositoryRoot, "App", "Moved.cs"),
                    "namespace Fixture;\n/// <summary>Provides moved operations.</summary>\npublic static class Moved\n{\n    public static void Run() { }\n}\n");
                return false;
            case "changed-compilation-context":
                var projectPath = Path.Join(repositoryRoot, "App", "App.csproj");
                var project = await File.ReadAllTextAsync(projectPath);
                await File.WriteAllTextAsync(projectPath, project.Replace(
                    "</Project>",
                    "  <PropertyGroup><DefineConstants>CHANGED_BASE</DefineConstants></PropertyGroup>\n</Project>",
                    StringComparison.Ordinal));
                return false;
            case "changed-applicable-components":
                await File.WriteAllTextAsync(sourcePath,
                    "namespace Fixture;\n/// <summary>Provides fixture operations.</summary>\npublic static class App\n{\n    public static int Run(int value) => value;\n}\n");
                return false;
            case "similar-display-name":
                await File.WriteAllTextAsync(sourcePath,
                    "namespace Fixture;\n/// <summary>Provides fixture operations.</summary>\npublic static class App\n{\n    public static void RunAgain() { }\n}\n");
                return false;
            case "reordered-plan":
                await File.WriteAllTextAsync(sourcePath,
                    "namespace Fixture;\n/// <summary>Provides fixture operations.</summary>\npublic static class App\n{\n    public static void Zulu() { }\n    public static void Alpha() { }\n}\n");
                return false;
            default:
                throw new InvalidDataException("Unknown target-evolution fixture.");
        }
    }

    private static void CopyRevision(string revision, string repositoryRoot) => File.Copy(
        Path.Join(
            CampaignCliProcessTests.RepositoryRoot,
            "tests", "fixtures", "campaign", "changed-base", revision, "revision.json"),
        Path.Join(repositoryRoot, "changed-base-revision.json"),
        overwrite: true);

    private sealed class ProcessFixture : IAsyncDisposable
    {
        private ProcessFixture(
            LoaderFixture repository,
            CampaignCliProcessTests.ProposalLoopbackServer server,
            string outside,
            string configurationPath,
            string statePath)
        {
            Repository = repository;
            Server = server;
            Outside = outside;
            ConfigurationPath = configurationPath;
            StatePath = statePath;
        }

        internal LoaderFixture Repository { get; }
        internal CampaignCliProcessTests.ProposalLoopbackServer Server { get; }
        internal string Outside { get; }
        internal string ConfigurationPath { get; }
        internal string StatePath { get; }

        internal string[] Args(string operation, string snapshot) =>
            CampaignCliProcessTests.Args(
                operation, Repository.Root, StatePath, ConfigurationPath, snapshot);

        [SupportedOSPlatform("linux")]
        internal static async Task<ProcessFixture> CreateAsync(
            bool executable,
            int? maximumCampaignElapsedMilliseconds = null)
        {
            var repository = await LoaderFixture.CreateAsync();
            if (executable)
            {
                await CampaignCliProcessTests.SetSingleWorkItemSourceAsync(repository.Root);
            }
            await File.WriteAllTextAsync(
                Path.Join(repository.Root, "policy.json"),
                executable ? CampaignCliProcessTests.RequiredPolicy : CampaignCliProcessTests.OptionalPolicy);
            var server = new CampaignCliProcessTests.ProposalLoopbackServer();
            var outside = CampaignCliProcessTests.CreatePrivateDirectory("contract-scribe-changed-base");
            var stateDirectory = Path.Join(outside, "state");
            Directory.CreateDirectory(stateDirectory);
            File.SetUnixFileMode(stateDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var configurationPath = Path.Join(outside, "campaign.json");
            await CampaignCliProcessTests.WriteConfigurationAsync(configurationPath, server.Endpoint);
            if (maximumCampaignElapsedMilliseconds is { } maximumElapsed)
            {
                var configuration = JsonNode.Parse(await File.ReadAllBytesAsync(configurationPath))!.AsObject();
                configuration["budgets"]!["campaign"]!["maximumElapsedMilliseconds"] = maximumElapsed;
                await File.WriteAllTextAsync(
                    configurationPath,
                    configuration.ToJsonString(),
                    new UTF8Encoding(false, true));
            }
            return new(
                repository,
                server,
                outside,
                configurationPath,
                Path.Join(stateDirectory, "checkpoint.json"));
        }

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            await Repository.DisposeAsync();
            if (Directory.Exists(Outside)) Directory.Delete(Outside, recursive: true);
        }
    }
}
