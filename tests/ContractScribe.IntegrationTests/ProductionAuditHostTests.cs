using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ContractScribe.Core;
using ContractScribe.Core.Hosting;

namespace ContractScribe.Roslyn.IntegrationTests;

[Collection("Integration process lane 2")]
public sealed class ProductionAuditHostTests
{
    private static readonly byte[] OptionalPolicy = Encoding.UTF8.GetBytes(
        "{\"defaultDecision\":\"optional\",\"schemaVersion\":1,\"targetProfile\":\"profile.external-api\"}\n");
    private static readonly HashSet<string> ProtectedRepositoryExtensions = new(
        [".cs", ".csproj", ".fs", ".fsproj", ".vb", ".vbproj", ".props", ".targets", ".sln", ".slnx", ".slnf"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ProtectedRepositoryNames = new(
        ["global.json", "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "NuGet.Config", "packages.lock.json"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> IgnoredRepositorySnapshotDirectories = new(
        [".git", ".tmp", "TestResults"],
        StringComparer.OrdinalIgnoreCase);

    [Fact]
    public async Task RealComposition_PublishesCanonicalBytesAtomicallyWithProvenance()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var resultPath = Path.Join(fixture.Root, "TestResults", "audit-result.json");
        var outcome = await RunAsync(fixture, resultPath);

        Assert.True(
            outcome.Terminal.ExecutionOutcome == HostExecutionOutcome.Succeeded,
            outcome.Terminal.Failure?.Code);
        Assert.Equal(HostTerminalState.CommittedResult, outcome.Terminal.TerminalState);
        Assert.Equal(HostArtifactState.Published, outcome.Terminal.OutputCommit.State);
        Assert.NotNull(outcome.CanonicalResult);
        Assert.Equal(outcome.CanonicalResult, await File.ReadAllBytesAsync(resultPath));
        Assert.Equal(Provenance(), outcome.Terminal.Provenance);
        Assert.Contains("invalidation-completed", outcome.TransitionEvents);
        Assert.Contains("atomic-rename-committed", outcome.TransitionEvents);
        Assert.False(File.Exists(Path.Join(
            fixture.Root,
            "TestResults",
            ".audit-result.json.contractscribe-stage")));
    }

    [Fact]
    public async Task RealComposition_DoesNotModifyProtectedRepositoryFiles()
    {
        await using var fixture = await LoaderFixture.CreateAsync(
            appProject:
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="../Library/Library.csproj" /></ItemGroup>
              <Target Name="ContractScribeReadOnlyCustomTarget" BeforeTargets="CoreCompile">
                <WriteLinesToFile File="$(IntermediateOutputPath)contractscribe-custom-target.txt"
                                  Lines="custom-target-ran"
                                  Overwrite="true" />
              </Target>
            </Project>
            """,
            withGenerator: true);
        var customTargetOutputs = Directory.EnumerateFiles(
                Path.Join(fixture.Root, "App", "obj"),
                "contractscribe-custom-target.txt",
                SearchOption.AllDirectories)
            .ToArray();
        Assert.NotEmpty(customTargetOutputs);
        foreach (var output in customTargetOutputs)
        {
            File.Delete(output);
        }
        var before = CaptureProtectedRepositoryFiles(fixture.Root);
        var resultPath = Path.Join(fixture.Root, "TestResults", "audit-result.json");

        var outcome = await RunAsync(fixture, resultPath);

        Assert.Equal(HostExecutionOutcome.Succeeded, outcome.Terminal.ExecutionOutcome);
        Assert.NotEmpty(Directory.EnumerateFiles(
            Path.Join(fixture.Root, "App", "obj"),
            "contractscribe-custom-target.txt",
            SearchOption.AllDirectories));
        Assert.Empty(FindProtectedRepositoryChanges(
            before,
            CaptureProtectedRepositoryFiles(fixture.Root)));
    }

    [Fact]
    public async Task ProtectedRepositorySnapshot_DetectsAProtectedByteMutation()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var before = CaptureProtectedRepositoryFiles(fixture.Root);

        await File.AppendAllTextAsync(
            Path.Join(fixture.Root, "App", "App.cs"),
            "\n// deliberate snapshot self-check");

        Assert.Equal(
            ["App/App.cs"],
            FindProtectedRepositoryChanges(
                before,
                CaptureProtectedRepositoryFiles(fixture.Root)));
    }

    [Fact]
    public void Publisher_RenamesTheHeldSingleLinkStagingFile()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "contractscribe-publisher",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Join(root, "TestResults"));
        try
        {
            var target = ResolvedPublicationTarget.ForTestResult(root);
            using var publisher = AtomicResultPublisher.Prepare(
                target,
                new ProductionAuditHostControls());
            var bytes = "{\"result\":true}\n"u8.ToArray();

            publisher.Stage(bytes);
            var sha256 = publisher.CommitRename();

            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), sha256);
            Assert.Equal(bytes, File.ReadAllBytes(target.FinalPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PublicationTarget_SeparatesCliPreflightFromFrozenValidationCapability()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "contractscribe-target",
            Guid.NewGuid().ToString("N"));
        var repository = Path.Join(root, "repository");
        var external = Path.Join(root, "output", "audit.json");
        Directory.CreateDirectory(Path.Join(repository, "TestResults"));
        Directory.CreateDirectory(Path.GetDirectoryName(external)!);
        try
        {
            var validation = ResolvedPublicationTarget.ForTestResult(repository);
            var cli = ResolvedPublicationTarget.ForExternalCli(repository, external);

            Assert.Equal(
                Path.Join(repository, "TestResults", "audit-result.json"),
                validation.FinalPath);
            Assert.Equal(external, cli.FinalPath);
            Assert.Throws<ArgumentException>(() =>
                ResolvedPublicationTarget.ForExternalCli(
                    repository,
                    Path.Join(repository, "audit.json")));
            Assert.Throws<ArgumentException>(() =>
                ResolvedPublicationTarget.ForExternalCli(
                    repository,
                    Path.Join(root, "missing", "audit.json")));

            using var publisher = AtomicResultPublisher.Prepare(
                cli,
                new ProductionAuditHostControls());
            var bytes = "{\"external\":true}\n"u8.ToArray();
            publisher.Stage(bytes);
            var sha256 = publisher.CommitRename();
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), sha256);
            Assert.Equal(bytes, File.ReadAllBytes(external));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Publisher_RejectsParentReplacementAndStagingHardLinks()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "contractscribe-publisher",
            Guid.NewGuid().ToString("N"));
        var resultDirectory = Path.Join(root, "TestResults");
        var movedDirectory = Path.Join(root, "TestResults-original");
        Directory.CreateDirectory(resultDirectory);
        try
        {
            var target = ResolvedPublicationTarget.ForTestResult(root);
            using (var publisher = AtomicResultPublisher.Prepare(
                       target,
                       new ProductionAuditHostControls()))
            {
                Directory.Move(resultDirectory, movedDirectory);
                Directory.CreateDirectory(resultDirectory);
                Assert.Throws<PublicationException>(() => publisher.Stage("{}\n"u8));
            }

            Directory.Delete(resultDirectory);
            Directory.Move(movedDirectory, resultDirectory);
            using (var publisher = AtomicResultPublisher.Prepare(
                       target,
                       new ProductionAuditHostControls()))
            {
                publisher.Stage("{}\n"u8);
                var alias = Path.Join(resultDirectory, "staging-alias.json");
                CreateHardLinkForTest(publisher.StagingPath, alias);
                Assert.Throws<PublicationException>(() => publisher.CommitRename());
                File.Delete(alias);
                Assert.True(publisher.TryCleanupStaging());
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Publisher_RejectsFinalHardLinksAndStagingNameReplacement()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "contractscribe-publisher",
            Guid.NewGuid().ToString("N"));
        var resultDirectory = Path.Join(root, "TestResults");
        Directory.CreateDirectory(resultDirectory);
        try
        {
            var target = ResolvedPublicationTarget.ForTestResult(root);
            File.WriteAllText(target.FinalPath, "prior\n");
            var finalAlias = Path.Join(resultDirectory, "prior-alias.json");
            CreateHardLinkForTest(target.FinalPath, finalAlias);
            Assert.Throws<PublicationException>(() => AtomicResultPublisher.Prepare(
                target,
                new ProductionAuditHostControls()));
            Assert.True(File.Exists(target.FinalPath));
            Assert.True(File.Exists(finalAlias));
            File.Delete(finalAlias);
            File.Delete(target.FinalPath);

            using var publisher = AtomicResultPublisher.Prepare(
                target,
                new ProductionAuditHostControls());
            publisher.Stage("{\"original\":true}\n"u8);
            File.Delete(publisher.StagingPath);
            File.WriteAllText(publisher.StagingPath, "{\"replacement\":true}\n");

            Assert.Throws<PublicationException>(() => publisher.CommitRename());
            Assert.False(File.Exists(target.FinalPath));
            Assert.True(publisher.TryCleanupStaging());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Publisher_RejectsParentReplacementAfterStagingAndCleansHeldDirectory()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "contractscribe-publisher",
            Guid.NewGuid().ToString("N"));
        var resultDirectory = Path.Join(root, "TestResults");
        var heldDirectory = Path.Join(root, "held-TestResults");
        Directory.CreateDirectory(resultDirectory);
        try
        {
            var target = ResolvedPublicationTarget.ForTestResult(root);
            using var publisher = AtomicResultPublisher.Prepare(
                target,
                new ProductionAuditHostControls());
            publisher.Stage("{}\n"u8);

            Directory.Move(resultDirectory, heldDirectory);
            Directory.CreateDirectory(resultDirectory);

            Assert.Throws<PublicationException>(() => publisher.CommitRename());
            Assert.False(publisher.TryCleanupStaging());
            Assert.True(File.Exists(Path.Join(
                heldDirectory,
                ".audit-result.json.contractscribe-stage")));
            Assert.False(File.Exists(target.FinalPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PublicationRenameRace_PreservesCompetitorAndCommitsFailure()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var resultPath = Path.Join(fixture.Root, "TestResults", "audit-result.json");
        var competitor = "competitor-result\n"u8.ToArray();

        var outcome = await RunAsync(
            fixture,
            resultPath,
            new ProductionAuditHostControls(
                BeforeAtomicRename: () => File.WriteAllBytes(resultPath, competitor)));

        Assert.Equal(HostExecutionOutcome.PublicationFailure, outcome.Terminal.ExecutionOutcome);
        Assert.Equal("host.publication.finalization-failed", outcome.Terminal.Failure?.Code);
        Assert.Equal(competitor, await File.ReadAllBytesAsync(resultPath));
        Assert.False(File.Exists(Path.Join(
            fixture.Root,
            "TestResults",
            ".audit-result.json.contractscribe-stage")));
        Assert.Null(outcome.CanonicalResult);
    }

    [Fact]
    public async Task DefaultOutputMeter_IgnoresFilesNotOwnedByTheAuditInvocation()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var outputDirectory = Path.Join(fixture.Root, "TestResults");
        var resultPath = Path.Join(outputDirectory, "audit-result.json");
        Directory.CreateDirectory(outputDirectory);
        var unrelated = Path.Join(outputDirectory, "unrelated.bin");
        using (var stream = new FileStream(
                   unrelated,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.Read))
        {
            stream.SetLength(HostContractResources.RequireBound("temporary-disk-bytes") + 1);
        }

        var outcome = await RunAsync(fixture, resultPath);

        Assert.Equal(HostExecutionOutcome.Succeeded, outcome.Terminal.ExecutionOutcome);
        Assert.True(File.Exists(resultPath));
        Assert.True(File.Exists(unrelated));
        Assert.All(
            outcome.Terminal.MeasuredBounds,
            fact => Assert.True(
                fact.Name != "temporary-disk-bytes" || fact.Measured <= fact.Threshold));
    }

    [Fact]
    public async Task TemporaryDiskMeter_TracksTransientHighWaterAcrossDistinctAndNestedRoots()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "contractscribe-disk-meter",
            Guid.NewGuid().ToString("N"));
        var nested = Path.Join(root, "nested");
        var second = Path.Join(root, "..", Path.GetFileName(root) + "-second");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(second);
        try
        {
            using var meter = new TemporaryDiskMeter(root, nested);
            var transient = Path.Join(nested, "transient.bin");
            await File.WriteAllBytesAsync(transient, new byte[4096]);
            Assert.Equal(4096, meter.Reconcile());
            File.Delete(transient);
            Assert.Equal(0, meter.Reconcile());
            Assert.Equal(4096, meter.HighWater);

            using var distinct = new TemporaryDiskMeter(root, second);
            await File.WriteAllBytesAsync(Path.Join(root, "first.bin"), new byte[17]);
            await File.WriteAllBytesAsync(Path.Join(second, "second.bin"), new byte[23]);
            Assert.Equal(40, distinct.Reconcile());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(second, recursive: true);
        }
    }

    [Theory]
    [InlineData(".contractscribe-hv-freeze-")]
    [InlineData(".contractscribe-hv-release-")]
    public async Task TemporaryDiskMeter_CountsPreexistingRetiredValidationPrefixFiles(string prefix)
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "contractscribe-disk-meter",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var reconciled = Path.Join(root, prefix + "reconcile.bin");
        await File.WriteAllBytesAsync(reconciled, new byte[257]);
        try
        {
            using var meter = new TemporaryDiskMeter(root, null);
            Assert.Equal(257, meter.Reconcile());
            File.Delete(reconciled);
            Assert.Equal(0, meter.Reconcile());

            meter.ObserveHostAllocation(Path.Join(root, prefix + "direct.bin"), 0, 258);
            Assert.Equal(258, meter.HighWater);

            var eventObserved = Path.Join(root, prefix + "event.bin");
            await using (var stream = new FileStream(
                             eventObserved,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.Read))
            {
                stream.SetLength(HostContractResources.RequireBound("temporary-disk-bytes") + 1);
            }
            meter.ObservePath(eventObserved);

            Assert.False(meter.TryCreateFactWithinThreshold(out _));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TemporaryDiskMeter_ObservesGovernedRootCreatedAfterMonitoringStarts()
    {
        var parent = Path.Join(
            Path.GetTempPath(),
            "contractscribe-disk-meter",
            Guid.NewGuid().ToString("N"));
        var governed = Path.Join(parent, "created-later");
        Directory.CreateDirectory(parent);
        try
        {
            using var meter = new TemporaryDiskMeter(governed, null);
            Directory.CreateDirectory(governed);
            await File.WriteAllBytesAsync(Path.Join(governed, "observed.bin"), new byte[257]);

            Assert.Equal(257, meter.Reconcile());
            Assert.Equal(257, meter.HighWater);
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void TemporaryDiskMeter_RetainsOverLimitWriteDeletedBeforeRescan()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "contractscribe-disk-meter",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var meter = new TemporaryDiskMeter(root, null);
            var transient = Path.Join(root, "over-limit-then-delete.bin");
            using (var stream = new FileStream(
                       transient,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.ReadWrite | FileShare.Delete))
            {
                stream.SetLength(HostContractResources.RequireBound("temporary-disk-bytes") + 1);
                stream.Flush(flushToDisk: true);
            }
            File.Delete(transient);

            Assert.True(SpinWait.SpinUntil(
                () => meter.HighWater > HostContractResources.RequireBound("temporary-disk-bytes"),
                TimeSpan.FromSeconds(5)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TemporaryDiskMeter_DirectAllocationCleanupProducesOneAtomicFact()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "contractscribe-disk-meter",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var meter = new TemporaryDiskMeter(root, null);
            var staging = Path.Join(root, "direct-staging.bin");
            meter.ObserveHostAllocation(staging, 0, 257);
            meter.ObservePath(staging);

            var barrier = Path.Join(root, "direct-barrier.bin");
            meter.ObserveHostAllocation(barrier, 0, 258);
            Assert.Equal(258, meter.HighWater);

            Assert.True(meter.TryCreateFactWithinThreshold(out var fact));
            Assert.Equal(258, fact!.Measured);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ToolchainProcessMeter_UsesParentChainsAndPidStartIdentity()
    {
        var toolchainRoot = Path.Join(
            Path.GetTempPath(),
            "contractscribe-process-meter",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(toolchainRoot);
        var compiler = Path.Join(toolchainRoot, "csc.dll");
        File.WriteAllBytes(compiler, "compiler-fixture"u8.ToArray());
        try
        {
            using var current = Process.GetCurrentProcess();
            var rootNode = new ToolchainProcessMeter.ProcessNode(
                current.Id,
                0,
                current.StartTime.ToUniversalTime().Ticks,
                current.ProcessName,
                current.MainModule?.FileName,
                [],
                true);
            IReadOnlyList<ToolchainProcessMeter.ProcessNode> snapshot =
            [
                rootNode,
                new(910001, current.Id, 1, "dotnet", compiler, [], true),
                new(910002, 910001, 2, "dotnet", compiler, [], true),
                new(910003, 999999, 3, "dotnet", compiler, [], true),
                new(910004, current.Id, 4, "ContractScribe.Helper", null, [], true),
                new(910005, current.Id, 5, "dotnet", compiler, ["restore"], true),
            ];
            using var meter = new ToolchainProcessMeter(
                () => snapshot,
                TimeSpan.FromDays(1));

            Assert.Equal(2, meter.SelectToolchain(new RegisteredToolchain(
                new ToolchainIdentity("10.0.102", "10.0.0", "18.0.0", "X64"),
                toolchainRoot)));
            snapshot =
            [
                rootNode,
                new(910001, current.Id, 6, "dotnet", compiler, [], true),
            ];

            Assert.Equal(3, meter.Reconcile());
            Assert.Equal(3, meter.ToFact().Measured);
        }
        finally
        {
            Directory.Delete(toolchainRoot, recursive: true);
        }
    }

    [Fact]
    public void ToolchainProcessMeter_ObservesUnclassifiedTrustedDescendant()
    {
        using var current = Process.GetCurrentProcess();
        IReadOnlyList<ToolchainProcessMeter.ProcessNode> snapshot =
        [
            new(
                current.Id,
                0,
                current.StartTime.ToUniversalTime().Ticks,
                current.ProcessName,
                current.MainModule?.FileName,
                [],
                true),
            new(920001, current.Id, 1, "unknown", null, [], false),
        ];
        using var meter = new ToolchainProcessMeter(
            () => snapshot,
            TimeSpan.FromDays(1));

        Assert.Equal(1, meter.SelectToolchain(new RegisteredToolchain(
            new ToolchainIdentity("10.0.102", "10.0.0", "18.0.0", "X64"),
            Path.GetTempPath())));
        Assert.Equal(1, meter.ToFact().Measured);
    }

    [Fact]
    public async Task ProcessFacts_FinalizeBeforeRenameAndMayExceedObservableThreshold()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var resultPath = Path.Join(fixture.Root, "TestResults", "audit-result.json");
        using var current = Process.GetCurrentProcess();
        var rootNode = new ToolchainProcessMeter.ProcessNode(
            current.Id,
            0,
            current.StartTime.ToUniversalTime().Ticks,
            current.ProcessName,
            current.MainModule?.FileName,
            [],
            true);
        var count = checked((int)HostContractResources.RequireBound("toolchain-subprocess-count") + 1);
        IReadOnlyList<ToolchainProcessMeter.ProcessNode> snapshot =
        [
            rootNode,
            .. Enumerable.Range(0, count).Select(index =>
                new ToolchainProcessMeter.ProcessNode(
                    930000 + index,
                    current.Id,
                    index + 1,
                    $"custom-target-{index}",
                    null,
                    [],
                    true)),
        ];
        var failObservationAfterRenameStarts = false;

        var outcome = await RunAsync(
            fixture,
            resultPath,
            new ProductionAuditHostControls(
                ProcessMeterFactory: () => new ToolchainProcessMeter(
                    () => failObservationAfterRenameStarts
                        ? throw new IOException("test-only post-finalization observation")
                        : snapshot,
                    TimeSpan.FromDays(1)),
                BeforeAtomicRename: () => failObservationAfterRenameStarts = true));

        Assert.Equal(HostExecutionOutcome.Succeeded, outcome.Terminal.ExecutionOutcome);
        Assert.True(File.Exists(resultPath));
        var fact = Assert.Single(
            outcome.Terminal.MeasuredBounds,
            item => item.Name == "toolchain-subprocess-count");
        Assert.Equal(count, fact.Measured);
        Assert.True(fact.Measured > fact.Threshold);
        Assert.Equal(HostEnforcementClass.ObservableOnly, fact.EnforcementClass);
    }

    [Fact]
    public async Task InvalidationFailure_PreservesPriorCommittedBytesAndCommitsOneFailure()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var resultPath = Path.Join(fixture.Root, "TestResults", "audit-result.json");
        Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
        var prior = "{\"prior\":true}\n"u8.ToArray();
        await File.WriteAllBytesAsync(resultPath, prior);

        var outcome = await RunAsync(
            fixture,
            resultPath,
            new ProductionAuditHostControls(
                Fault: ProductionHostFault.PublicationInvalidation,
                LateCompletion: _ => Task.CompletedTask));

        Assert.Equal("host.publication.invalidation-failed", outcome.Terminal.Failure?.Code);
        Assert.Equal(HostExecutionOutcome.PublicationFailure, outcome.Terminal.ExecutionOutcome);
        Assert.Equal(prior, await File.ReadAllBytesAsync(resultPath));
        Assert.Equal(
            [
                "invalidation-attempt-failed",
                "terminal-commit-publication-failure",
                "late-terminal-attempt-rejected",
            ],
            outcome.TransitionEvents);
    }

    [Fact]
    public async Task CancellationDuringSuccessfulInvalidation_CommitsNotSelectedCancellation()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var resultPath = Path.Join(fixture.Root, "TestResults", "audit-result.json");
        Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
        await File.WriteAllTextAsync(resultPath, "{\"prior\":true}\n");
        using var cancellation = new CancellationTokenSource();

        var outcome = await RunAsync(
            fixture,
            resultPath,
            new ProductionAuditHostControls(
                BeforeInvalidation: cancellation.Cancel,
                LateCompletion: _ => Task.CompletedTask),
            cancellation.Token);

        Assert.Equal(HostExecutionOutcome.Cancelled, outcome.Terminal.ExecutionOutcome);
        Assert.Equal("host.publication.cancelled", outcome.Terminal.Failure?.Code);
        Assert.Equal(
            HostToolchainSelectionState.NotSelected,
            outcome.Terminal.Toolchain.SelectionState);
        Assert.False(File.Exists(resultPath));
        Assert.Equal(
            [
                "invalidation-completed",
                "terminal-commit-cancelled",
                "late-terminal-attempt-rejected",
            ],
            outcome.TransitionEvents);
    }

    [Fact]
    public async Task CancellationDuringFailedInvalidation_PreservesEarlierCancellation()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var resultPath = Path.Join(fixture.Root, "TestResults", "audit-result.json");
        Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
        var prior = "{\"prior\":true}\n"u8.ToArray();
        await File.WriteAllBytesAsync(resultPath, prior);
        using var cancellation = new CancellationTokenSource();

        var outcome = await RunAsync(
            fixture,
            resultPath,
            new ProductionAuditHostControls(
                Fault: ProductionHostFault.PublicationInvalidation,
                BeforeInvalidation: cancellation.Cancel,
                LateCompletion: _ => Task.CompletedTask),
            cancellation.Token);

        Assert.Equal(HostExecutionOutcome.Cancelled, outcome.Terminal.ExecutionOutcome);
        Assert.Equal("host.publication.cancelled", outcome.Terminal.Failure?.Code);
        Assert.Equal(
            HostToolchainSelectionState.NotSelected,
            outcome.Terminal.Toolchain.SelectionState);
        Assert.Equal(prior, await File.ReadAllBytesAsync(resultPath));
        Assert.Equal(
            [
                "invalidation-attempt-failed",
                "terminal-commit-cancelled",
                "late-terminal-attempt-rejected",
            ],
            outcome.TransitionEvents);
    }

    [Fact]
    public async Task FinalizationFailure_CleansStagingBeforeCommittingFailure()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var resultPath = Path.Join(fixture.Root, "TestResults", "audit-result.json");
        var outcome = await RunAsync(
            fixture,
            resultPath,
            new ProductionAuditHostControls(
                Fault: ProductionHostFault.PublicationFinalization,
                LateCompletion: _ => Task.CompletedTask));

        Assert.Equal("host.publication.finalization-failed", outcome.Terminal.Failure?.Code);
        Assert.False(File.Exists(resultPath));
        Assert.False(File.Exists(Path.Join(
            fixture.Root,
            "TestResults",
            ".audit-result.json.contractscribe-stage")));
        Assert.Equal(
            [
                "invalidation-completed",
                "failure-prone-stage-entered",
                "staging-created-in-destination",
                "atomic-replace-attempt-failed",
                "staging-cleanup-completed",
                "terminal-commit-publication-failure",
                "late-terminal-attempt-rejected",
            ],
            outcome.TransitionEvents);
    }

    [Fact]
    public async Task CleanupFailure_WinsOverFinalizationFailureAndRetainsStagingEvidence()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var resultPath = Path.Join(fixture.Root, "TestResults", "audit-result.json");
        var outcome = await RunAsync(
            fixture,
            resultPath,
            new ProductionAuditHostControls(
                Fault: ProductionHostFault.PublicationCleanup));

        Assert.Equal("host.publication.cleanup-failed", outcome.Terminal.Failure?.Code);
        Assert.False(File.Exists(resultPath));
        Assert.True(File.Exists(Path.Join(
            fixture.Root,
            "TestResults",
            ".audit-result.json.contractscribe-stage")));
    }

    [Fact]
    public async Task CancellationBeforeCommit_InvalidatesOutputAndRejectsLateSuccess()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var resultPath = Path.Join(fixture.Root, "TestResults", "audit-result.json");
        using var cancellation = new CancellationTokenSource();
        var controls = new ProductionAuditHostControls(
            Gate: (point, _) =>
            {
                if (point == ProductionHostControlPoint.BeforeCommit)
                {
                    cancellation.Cancel();
                }
                return Task.CompletedTask;
            },
            LateCompletion: _ => Task.CompletedTask,
            LateAttemptKind: ProductionLateAttemptKind.CompetingTerminal);

        var outcome = await RunAsync(
            fixture,
            resultPath,
            controls,
            cancellation.Token);

        Assert.Equal(HostExecutionOutcome.Cancelled, outcome.Terminal.ExecutionOutcome);
        Assert.Equal("host.publication.cancelled", outcome.Terminal.Failure?.Code);
        Assert.False(File.Exists(resultPath));
        Assert.Contains("terminal-commit-cancelled", outcome.TransitionEvents);
        Assert.Contains("competing-terminal-attempt-rejected", outcome.TransitionEvents);
    }

    [Fact]
    public async Task CancellationRegistration_WinsAtomicPublicationDecisionAndCleanupCannotReclassifyIt()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var resultPath = Path.Join(fixture.Root, "TestResults", "audit-result.json");
        using var cancellation = new CancellationTokenSource();
        var controls = new ProductionAuditHostControls(
            Fault: ProductionHostFault.PublicationCleanup,
            Gate: (point, _) =>
            {
                if (point == ProductionHostControlPoint.BeforePublicationDecision)
                {
                    cancellation.Cancel();
                }
                return Task.CompletedTask;
            });

        var outcome = await RunAsync(
            fixture,
            resultPath,
            controls,
            cancellation.Token);

        Assert.Equal(HostExecutionOutcome.Cancelled, outcome.Terminal.ExecutionOutcome);
        Assert.Equal("host.publication.cancelled", outcome.Terminal.Failure?.Code);
        Assert.Contains(
            outcome.Terminal.Diagnostics,
            diagnostic => diagnostic.Code == "host.publication.cleanup-failed");
        Assert.False(File.Exists(resultPath));
    }

    [Fact]
    public async Task UnexpectedSdkBoundaryFailure_CommitsBoundedPreselectionFailure()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var resultPath = Path.Join(fixture.Root, "TestResults", "audit-result.json");

        var outcome = await RunAsync(
            fixture,
            resultPath,
            new ProductionAuditHostControls(
                SdkDiscovery: _ => throw new InvalidOperationException("test-only")));

        Assert.Equal(HostExecutionOutcome.EnvironmentUnavailable, outcome.Terminal.ExecutionOutcome);
        Assert.Equal("host.sdk-discovery.unavailable", outcome.Terminal.Failure?.Code);
        Assert.Equal(HostTerminalState.CommittedNonSuccess, outcome.Terminal.TerminalState);
        Assert.Equal(
            HostToolchainSelectionState.NotSelected,
            outcome.Terminal.Toolchain.SelectionState);
        Assert.Null(outcome.CanonicalResult);
        Assert.False(File.Exists(resultPath));
    }

    [Theory]
    [InlineData(HostStage.Classification, "host.classification.failed")]
    [InlineData(HostStage.DocumentationObservation, "host.documentation-observation.failed")]
    [InlineData(HostStage.PolicyEvidence, "host.policy-evidence.failed")]
    [InlineData(HostStage.Audit, "host.audit.aggregation-failed")]
    [InlineData(HostStage.ResultValidation, "host.result-validation.failed")]
    [InlineData(HostStage.Shutdown, "host.shutdown.failed")]
    [InlineData(HostStage.Publication, "host.publication.finalization-failed")]
    public async Task ManagedStageFailure_MapsThroughTheClosedRegistry(
        HostStage failingStage,
        string expectedCode)
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var resultPath = Path.Join(fixture.Root, "TestResults", "audit-result.json");

        var outcome = await RunAsync(
            fixture,
            resultPath,
            new ProductionAuditHostControls(
                StageBoundary: (stage, _) => stage == failingStage
                    ? Task.FromException(new InvalidOperationException("test-only"))
                    : Task.CompletedTask));

        Assert.Equal(expectedCode, outcome.Terminal.Failure?.Code);
        Assert.Equal(failingStage, outcome.Terminal.Failure?.Stage);
        Assert.Equal(HostTerminalState.CommittedNonSuccess, outcome.Terminal.TerminalState);
        Assert.Null(outcome.CanonicalResult);
        Assert.False(File.Exists(resultPath));
    }

    [Theory]
    [InlineData(HostStage.Input, "host.input.cancelled")]
    [InlineData(HostStage.SdkDiscovery, "host.sdk-discovery.cancelled")]
    [InlineData(HostStage.WorkspaceLoad, "host.workspace-load.cancelled")]
    [InlineData(HostStage.Classification, "host.classification.cancelled")]
    [InlineData(HostStage.DocumentationObservation, "host.documentation-observation.cancelled")]
    [InlineData(HostStage.PolicyEvidence, "host.policy-evidence.cancelled")]
    [InlineData(HostStage.Audit, "host.audit.cancelled")]
    [InlineData(HostStage.ResultValidation, "host.result-validation.cancelled")]
    [InlineData(HostStage.Shutdown, "host.shutdown.cancelled")]
    [InlineData(HostStage.Publication, "host.publication.cancelled")]
    public async Task CallerCancellationAtEveryManagedStage_CommitsTheStageRow(
        HostStage cancelledStage,
        string expectedCode)
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var resultPath = Path.Join(fixture.Root, "TestResults", "audit-result.json");
        using var cancellation = new CancellationTokenSource();
        var deadlines = new TestDeadlineRegistry();

        var outcome = await RunAsync(
            fixture,
            resultPath,
            new ProductionAuditHostControls(
                StageBoundary: (stage, cancellationToken) =>
                {
                    _ = cancellationToken;
                    if (stage == cancelledStage)
                    {
                        cancellation.Cancel();
                        deadlines["total-audit-timeout"].Trigger();
                    }
                    return Task.CompletedTask;
                },
                DeadlineSourceFactory: deadlines.Create),
            cancellation.Token);

        Assert.Equal(HostExecutionOutcome.Cancelled, outcome.Terminal.ExecutionOutcome);
        Assert.Equal(expectedCode, outcome.Terminal.Failure?.Code);
        Assert.Equal(cancelledStage, outcome.Terminal.Failure?.Stage);
        Assert.Null(outcome.CanonicalResult);
        Assert.False(File.Exists(resultPath));
    }

    [Theory]
    [InlineData(HostStage.Input, "host.input.timeout")]
    [InlineData(HostStage.SdkDiscovery, "host.sdk-discovery.timeout")]
    [InlineData(HostStage.WorkspaceLoad, "host.workspace-load.timeout")]
    [InlineData(HostStage.Classification, "host.classification.timeout")]
    [InlineData(HostStage.DocumentationObservation, "host.documentation-observation.timeout")]
    [InlineData(HostStage.PolicyEvidence, "host.policy-evidence.timeout")]
    [InlineData(HostStage.Audit, "host.audit.timeout")]
    [InlineData(HostStage.ResultValidation, "host.result-validation.timeout")]
    [InlineData(HostStage.Shutdown, "host.shutdown.timeout")]
    [InlineData(HostStage.Publication, "host.publication.timeout")]
    public async Task CooperativeTimeoutAtEveryManagedStage_CommitsTheStageRow(
        HostStage timedOutStage,
        string expectedCode)
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var resultPath = Path.Join(fixture.Root, "TestResults", "audit-result.json");

        var outcome = await RunAsync(
            fixture,
            resultPath,
            new ProductionAuditHostControls(
                StageBoundary: (stage, _) => stage == timedOutStage
                    ? Task.FromException(new OperationCanceledException("test-only"))
                    : Task.CompletedTask));

        Assert.Equal(HostExecutionOutcome.Timeout, outcome.Terminal.ExecutionOutcome);
        Assert.Equal(expectedCode, outcome.Terminal.Failure?.Code);
        Assert.Equal(timedOutStage, outcome.Terminal.Failure?.Stage);
        Assert.Null(outcome.CanonicalResult);
        Assert.False(File.Exists(resultPath));
    }

    [Theory]
    [InlineData(HostStage.Input, "host.input.timeout")]
    [InlineData(HostStage.SdkDiscovery, "host.sdk-discovery.timeout")]
    [InlineData(HostStage.WorkspaceLoad, "host.workspace-load.timeout")]
    [InlineData(HostStage.Classification, "host.classification.timeout")]
    [InlineData(HostStage.DocumentationObservation, "host.documentation-observation.timeout")]
    [InlineData(HostStage.PolicyEvidence, "host.policy-evidence.timeout")]
    [InlineData(HostStage.Audit, "host.audit.timeout")]
    [InlineData(HostStage.ResultValidation, "host.result-validation.timeout")]
    [InlineData(HostStage.Shutdown, "host.shutdown.timeout")]
    [InlineData(HostStage.Publication, "host.publication.timeout")]
    public async Task TotalAuditDeadlineAtEveryManagedStage_CommitsTheStageRow(
        HostStage timedOutStage,
        string expectedCode)
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var resultPath = Path.Join(fixture.Root, "TestResults", "audit-result.json");
        using var cancellation = new CancellationTokenSource();
        var deadlines = new TestDeadlineRegistry();

        var outcome = await RunAsync(
            fixture,
            resultPath,
            new ProductionAuditHostControls(
                StageBoundary: (stage, _) =>
                {
                    if (stage == timedOutStage)
                    {
                        deadlines["total-audit-timeout"].Trigger();
                        cancellation.Cancel();
                    }
                    return Task.CompletedTask;
                },
                DeadlineSourceFactory: deadlines.Create),
            cancellation.Token);

        Assert.Equal(HostExecutionOutcome.Timeout, outcome.Terminal.ExecutionOutcome);
        Assert.Equal(expectedCode, outcome.Terminal.Failure?.Code);
        Assert.Equal(timedOutStage, outcome.Terminal.Failure?.Stage);
        Assert.Null(outcome.CanonicalResult);
        Assert.False(File.Exists(resultPath));
    }

    [Fact]
    public async Task TotalAuditDeadline_UsesTheArmedProductionTimerPath()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var resultPath = Path.Join(fixture.Root, "TestResults", "audit-result.json");
        var stopwatch = Stopwatch.StartNew();

        var outcome = await RunAsync(
            fixture,
            resultPath,
            new ProductionAuditHostControls(
                DeadlineOverride: name => name == "total-audit-timeout"
                    ? TimeSpan.FromMilliseconds(50)
                    : null,
                StageBoundary: async (stage, token) =>
                {
                    if (stage == HostStage.Input)
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    }
                }));

        stopwatch.Stop();
        Assert.Equal(HostExecutionOutcome.Timeout, outcome.Terminal.ExecutionOutcome);
        Assert.Equal("host.input.timeout", outcome.Terminal.Failure?.Code);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.False(File.Exists(resultPath));
    }

    [Fact]
    public async Task InvalidInputPrecedesEnvironmentFailureInTheOrderedHost()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var resultPath = Path.Join(fixture.Root, "TestResults", "audit-result.json");
        Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);

        var outcome = await new ProductionAuditHost(Provenance()).RunAsync(
            new ProductionAuditRequest(
                fixture.Root,
                "App/App.csproj",
                "{}\n"u8.ToArray(),
                ResolvedPublicationTarget.ForTestResult(fixture.Root)),
            new ProductionAuditHostControls(
                Fault: ProductionHostFault.EnvironmentUnavailable));

        Assert.Equal("host.input.invalid-request", outcome.Terminal.Failure?.Code);
        Assert.Equal(HostStage.Input, outcome.Terminal.Failure?.Stage);
    }

    [Theory]
    [InlineData(
        "environment",
        "host.sdk-discovery.unavailable",
        HostExecutionOutcome.EnvironmentUnavailable)]
    [InlineData(
        "load",
        "host.workspace-load.failed",
        HostExecutionOutcome.LoadFailure)]
    [InlineData(
        "audit",
        "host.audit.aggregation-failed",
        HostExecutionOutcome.AuditError)]
    public async Task NamedProductionFaults_CommitTheirClosedFailureRows(
        string faultName,
        string expectedCode,
        HostExecutionOutcome expectedOutcome)
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var resultPath = Path.Join(fixture.Root, "TestResults", "audit-result.json");

        var outcome = await RunAsync(
            fixture,
            resultPath,
            new ProductionAuditHostControls(Fault: faultName switch
            {
                "environment" => ProductionHostFault.EnvironmentUnavailable,
                "load" => ProductionHostFault.LoadFailure,
                "audit" => ProductionHostFault.AuditError,
                _ => throw new ArgumentOutOfRangeException(nameof(faultName)),
            }));

        Assert.Equal(expectedOutcome, outcome.Terminal.ExecutionOutcome);
        Assert.Equal(expectedCode, outcome.Terminal.Failure?.Code);
        Assert.Null(outcome.CanonicalResult);
        Assert.False(File.Exists(resultPath));
    }

    [Fact]
    public async Task AcceptedLoaderDiagnostics_ArePreservedThroughSuccessfulComposition()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var resultPath = Path.Join(fixture.Root, "TestResults", "audit-result.json");
        var diagnostic = new LoaderDiagnostic(
            "workspace",
            "loader.workspace-warning",
            "warning");

        var outcome = await RunAsync(
            fixture,
            resultPath,
            new ProductionAuditHostControls(
                RepositoryLoad: async (request, token) =>
                {
                    var loaded = await new RepositoryLoader(observer: null)
                        .LoadAsync(request, token)
                        .ConfigureAwait(false);
                    Assert.Equal(RepositoryLoadStatus.Success, loaded.Status);
                    return RepositoryLoadOutcome.Success(loaded.Session!, [diagnostic]);
                }));

        Assert.Equal(HostExecutionOutcome.Succeeded, outcome.Terminal.ExecutionOutcome);
        var accepted = Assert.Single(
            outcome.Terminal.Diagnostics,
            item => item.Code == diagnostic.Code);
        Assert.Equal(HostStage.WorkspaceLoad, accepted.Stage);
        Assert.Equal(HostDiagnosticSeverity.Warning, accepted.Severity);
    }

    [Fact]
    public async Task SdkDiscoveryDeadline_RacesAProviderThatIgnoresCancellation()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var resultPath = Path.Join(fixture.Root, "TestResults", "audit-result.json");
        var late = new TaskCompletionSource<RegisteredToolchain>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = Stopwatch.StartNew();

        var outcome = await RunAsync(
            fixture,
            resultPath,
            new ProductionAuditHostControls(
                DeadlineOverride: name => name == "sdk-discovery-timeout"
                    ? TimeSpan.FromMilliseconds(50)
                    : null,
                SdkDiscovery: _ => late.Task));

        stopwatch.Stop();
        Assert.Equal(HostExecutionOutcome.Timeout, outcome.Terminal.ExecutionOutcome);
        Assert.Equal("host.sdk-discovery.timeout", outcome.Terminal.Failure?.Code);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
        late.SetResult(TestToolchain(fixture.Root));
        await Task.Delay(25);
    }

    [Fact]
    public async Task SdkDiscoveryCauseOrder_TimeoutThenCancellationCommitsTimeout()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var resultPath = Path.Join(fixture.Root, "TestResults", "audit-result.json");
        using var caller = new CancellationTokenSource();
        var deadlines = new TestDeadlineRegistry();

        var outcome = await RunAsync(
            fixture,
            resultPath,
            new ProductionAuditHostControls(
                StageBoundary: (stage, _) =>
                {
                    if (stage == HostStage.SdkDiscovery)
                    {
                        deadlines["sdk-discovery-timeout"].Trigger();
                        caller.Cancel();
                    }
                    return Task.CompletedTask;
                },
                DeadlineSourceFactory: deadlines.Create),
            caller.Token);

        Assert.Equal(HostExecutionOutcome.Timeout, outcome.Terminal.ExecutionOutcome);
        Assert.Equal("host.sdk-discovery.timeout", outcome.Terminal.Failure?.Code);
    }

    [Fact]
    public async Task SdkDiscoveryCauseOrder_CancellationThenTimeoutCommitsCancellation()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var resultPath = Path.Join(fixture.Root, "TestResults", "audit-result.json");
        using var caller = new CancellationTokenSource();
        var deadlines = new TestDeadlineRegistry();

        var outcome = await RunAsync(
            fixture,
            resultPath,
            new ProductionAuditHostControls(
                StageBoundary: (stage, _) =>
                {
                    if (stage == HostStage.SdkDiscovery)
                    {
                        caller.Cancel();
                        deadlines["sdk-discovery-timeout"].Trigger();
                    }
                    return Task.CompletedTask;
                },
                DeadlineSourceFactory: deadlines.Create),
            caller.Token);

        Assert.Equal(HostExecutionOutcome.Cancelled, outcome.Terminal.ExecutionOutcome);
        Assert.Equal("host.sdk-discovery.cancelled", outcome.Terminal.Failure?.Code);
    }

    [Fact]
    public async Task DelayedCallerCallback_CannotLetALaterSdkDeadlineReplaceCancellation()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var resultPath = Path.Join(fixture.Root, "TestResults", "audit-result.json");
        using var caller = new CancellationTokenSource();
        using var callbackEntered = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        var deadlines = new TestDeadlineRegistry();
        Task? cancellationTask = null;

        try
        {
            var outcome = await RunAsync(
                fixture,
                resultPath,
                new ProductionAuditHostControls(
                    StageBoundary: (stage, _) =>
                    {
                        if (stage != HostStage.SdkDiscovery)
                        {
                            return Task.CompletedTask;
                        }

                        cancellationTask = Task.Run(caller.Cancel);
                        Assert.True(callbackEntered.Wait(TimeSpan.FromSeconds(5)));
                        deadlines["sdk-discovery-timeout"].Trigger();
                        releaseCallback.Set();
                        return Task.CompletedTask;
                    },
                    DeadlineSourceFactory: deadlines.Create,
                    AfterInterruptionSourceReserved: name =>
                    {
                        if (name == "caller")
                        {
                            callbackEntered.Set();
                            Assert.True(releaseCallback.Wait(TimeSpan.FromSeconds(10)));
                        }
                    }),
                caller.Token);

            Assert.Equal(HostExecutionOutcome.Cancelled, outcome.Terminal.ExecutionOutcome);
            Assert.Equal("host.sdk-discovery.cancelled", outcome.Terminal.Failure?.Code);
        }
        finally
        {
            releaseCallback.Set();
            if (cancellationTask is not null)
            {
                await cancellationTask;
            }
        }
    }

    [Fact]
    public async Task DelayedDeadlineCallback_CannotLetLaterCallerCancellationReplaceTimeout()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var resultPath = Path.Join(fixture.Root, "TestResults", "audit-result.json");
        using var caller = new CancellationTokenSource();
        using var callbackEntered = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        var deadlines = new TestDeadlineRegistry();
        Task? deadlineTask = null;

        try
        {
            var outcome = await RunAsync(
                fixture,
                resultPath,
                new ProductionAuditHostControls(
                    StageBoundary: (stage, _) =>
                    {
                        if (stage != HostStage.SdkDiscovery)
                        {
                            return Task.CompletedTask;
                        }

                        deadlineTask = Task.Run(deadlines["sdk-discovery-timeout"].Trigger);
                        Assert.True(callbackEntered.Wait(TimeSpan.FromSeconds(5)));
                        caller.Cancel();
                        releaseCallback.Set();
                        return Task.CompletedTask;
                    },
                    DeadlineSourceFactory: deadlines.Create,
                    AfterInterruptionSourceReserved: name =>
                    {
                        if (name == "sdk-discovery-timeout")
                        {
                            callbackEntered.Set();
                            Assert.True(releaseCallback.Wait(TimeSpan.FromSeconds(10)));
                        }
                    }),
                caller.Token);

            Assert.Equal(HostExecutionOutcome.Timeout, outcome.Terminal.ExecutionOutcome);
            Assert.Equal("host.sdk-discovery.timeout", outcome.Terminal.Failure?.Code);
        }
        finally
        {
            releaseCallback.Set();
            if (deadlineTask is not null)
            {
                await deadlineTask;
            }
        }
    }

    [Theory]
    [InlineData(HostStage.SdkDiscovery, "sdk-discovery-timeout", false, "host.sdk-discovery.cancelled")]
    [InlineData(HostStage.SdkDiscovery, "sdk-discovery-timeout", true, "host.sdk-discovery.timeout")]
    [InlineData(HostStage.WorkspaceLoad, "workspace-load-timeout", false, "host.workspace-load.cancelled")]
    [InlineData(HostStage.WorkspaceLoad, "workspace-load-timeout", true, "host.workspace-load.timeout")]
    [InlineData(HostStage.Shutdown, "graceful-shutdown-timeout", false, "host.shutdown.cancelled")]
    [InlineData(HostStage.Shutdown, "graceful-shutdown-timeout", true, "host.shutdown.timeout")]
    public async Task LocalDeadlineAndCallerOrders_UseTheActualProductionSource(
        HostStage targetStage,
        string deadlineName,
        bool deadlineFirst,
        string expectedCode)
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var resultPath = Path.Join(fixture.Root, "TestResults", "audit-result.json");
        using var caller = new CancellationTokenSource();
        var deadlines = new TestDeadlineRegistry();

        var outcome = await RunAsync(
            fixture,
            resultPath,
            new ProductionAuditHostControls(
                StageBoundary: (stage, _) =>
                {
                    if (stage == targetStage)
                    {
                        if (deadlineFirst)
                        {
                            deadlines[deadlineName].Trigger();
                            caller.Cancel();
                        }
                        else
                        {
                            caller.Cancel();
                            deadlines[deadlineName].Trigger();
                        }
                    }
                    return Task.CompletedTask;
                },
                DeadlineSourceFactory: deadlines.Create),
            caller.Token);

        Assert.Equal(
            deadlineFirst ? HostExecutionOutcome.Timeout : HostExecutionOutcome.Cancelled,
            outcome.Terminal.ExecutionOutcome);
        Assert.Equal(expectedCode, outcome.Terminal.Failure?.Code);
        Assert.Equal(targetStage, outcome.Terminal.Failure?.Stage);
        Assert.Equal(
            targetStage == HostStage.SdkDiscovery
                ? HostToolchainSelectionState.NotSelected
                : HostToolchainSelectionState.Selected,
            outcome.Terminal.Toolchain.SelectionState);
    }

    [Theory]
    [InlineData(false, "host.shutdown.cancelled")]
    [InlineData(true, "host.shutdown.timeout")]
    public async Task SessionConsumerShutdown_RetiresTotalDeadlineAndPreservesBothCausalOrders(
        bool deadlineFirst,
        string expectedCode)
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var resultPath = Path.Join(fixture.Root, "TestResults", "audit-result.json");
        using var caller = new CancellationTokenSource();
        var deadlines = new TestDeadlineRegistry();
        CancellationToken? consumerToken = null;

        var outcome = await RunAsync(
            fixture,
            resultPath,
            new ProductionAuditHostControls(
                StageBoundary: (stage, _) =>
                {
                    if (stage == HostStage.Shutdown)
                    {
                        if (deadlineFirst)
                        {
                            deadlines["graceful-shutdown-timeout"].Trigger();
                            caller.Cancel();
                        }
                        else
                        {
                            caller.Cancel();
                            deadlines["graceful-shutdown-timeout"].Trigger();
                        }
                    }
                    return Task.CompletedTask;
                },
                SessionConsumer: (_, token) =>
                {
                    consumerToken = token;
                    deadlines["total-audit-timeout"].Trigger();
                    return Task.CompletedTask;
                },
                DeadlineSourceFactory: deadlines.Create),
            caller.Token);

        Assert.Equal(caller.Token, consumerToken);
        Assert.Contains(
            "audit-deadline-retired-before-session-consumer",
            outcome.TransitionEvents);
        Assert.Equal(
            deadlineFirst ? HostExecutionOutcome.Timeout : HostExecutionOutcome.Cancelled,
            outcome.Terminal.ExecutionOutcome);
        Assert.Equal(expectedCode, outcome.Terminal.Failure?.Code);
        Assert.Equal(HostStage.Shutdown, outcome.Terminal.Failure?.Stage);
        Assert.Equal(
            HostToolchainSelectionState.Selected,
            outcome.Terminal.Toolchain.SelectionState);
    }

    [Fact]
    public async Task AcceptedCancellation_BlocksALaterStageFailureWhileTheCallbackIsPaused()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var resultPath = Path.Join(fixture.Root, "TestResults", "audit-result.json");
        using var caller = new CancellationTokenSource();
        using var accepted = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var outcome = await RunAsync(
            fixture,
            resultPath,
            new ProductionAuditHostControls(
                StageBoundary: (stage, cancellationToken) =>
                {
                    _ = cancellationToken;
                    if (stage != HostStage.Audit)
                    {
                        return Task.CompletedTask;
                    }
                    _ = Task.Run(caller.Cancel);
                    Assert.True(accepted.Wait(TimeSpan.FromSeconds(5)));
                    return Task.FromException(new InvalidOperationException("test-only stage failure"));
                },
                LateCompletion: _ =>
                {
                    release.Set();
                    return Task.CompletedTask;
                },
                AfterCauseAccepted: _ =>
                {
                    accepted.Set();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                }),
            caller.Token);

        Assert.Equal(HostExecutionOutcome.Cancelled, outcome.Terminal.ExecutionOutcome);
        Assert.Equal("host.audit.cancelled", outcome.Terminal.Failure?.Code);
        Assert.False(File.Exists(resultPath));
    }

    [Fact]
    public async Task AcceptedCancellation_BlocksPublicationWhileTheCallbackIsPaused()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var resultPath = Path.Join(fixture.Root, "TestResults", "audit-result.json");
        using var caller = new CancellationTokenSource();
        using var accepted = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var outcome = await RunAsync(
            fixture,
            resultPath,
            new ProductionAuditHostControls(
                Gate: (point, cancellationToken) =>
                {
                    _ = cancellationToken;
                    if (point == ProductionHostControlPoint.BeforePublicationDecision)
                    {
                        _ = Task.Run(caller.Cancel);
                        Assert.True(accepted.Wait(TimeSpan.FromSeconds(5)));
                    }
                    return Task.CompletedTask;
                },
                LateCompletion: _ =>
                {
                    release.Set();
                    return Task.CompletedTask;
                },
                AfterCauseAccepted: _ =>
                {
                    accepted.Set();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                }),
            caller.Token);

        Assert.Equal(HostExecutionOutcome.Cancelled, outcome.Terminal.ExecutionOutcome);
        Assert.Equal("host.publication.cancelled", outcome.Terminal.Failure?.Code);
        Assert.False(File.Exists(resultPath));
    }

    [Fact]
    public async Task CancellationImmediatelyBeforeAndAfterToolchainSelectionPreservesItsExactSide()
    {
        await using var beforeFixture = await LoaderFixture.CreateAsync();
        var beforeResult = Path.Join(beforeFixture.Root, "TestResults", "audit-result.json");
        using var beforeCaller = new CancellationTokenSource();
        var before = await RunAsync(
            beforeFixture,
            beforeResult,
            new ProductionAuditHostControls(
                StageBoundary: (stage, token) =>
                {
                    if (stage == HostStage.SdkDiscovery)
                    {
                        beforeCaller.Cancel();
                        return Task.FromException(new OperationCanceledException(token));
                    }
                    return Task.CompletedTask;
                }),
            beforeCaller.Token);
        Assert.Equal("host.sdk-discovery.cancelled", before.Terminal.Failure?.Code);
        Assert.Equal(
            HostToolchainSelectionState.NotSelected,
            before.Terminal.Toolchain.SelectionState);

        await using var afterFixture = await LoaderFixture.CreateAsync();
        var afterResult = Path.Join(afterFixture.Root, "TestResults", "audit-result.json");
        using var afterCaller = new CancellationTokenSource();
        HostToolchainFact? selectedAtBoundary = null;
        var after = await RunAsync(
            afterFixture,
            afterResult,
            new ProductionAuditHostControls(
                AfterToolchainSelection: selected =>
                {
                    selectedAtBoundary = selected;
                    afterCaller.Cancel();
                }),
            afterCaller.Token);

        Assert.NotNull(selectedAtBoundary);
        Assert.Equal(HostExecutionOutcome.Cancelled, after.Terminal.ExecutionOutcome);
        Assert.Equal("host.sdk-discovery.cancelled", after.Terminal.Failure?.Code);
        Assert.Equal(selectedAtBoundary, after.Terminal.Toolchain);
        Assert.Equal(
            HostToolchainSelectionState.Selected,
            after.Terminal.Toolchain.SelectionState);
    }

    [Fact]
    public async Task WorkspaceDeadline_RacesBlockingTailInventoryAndObservesLateCompletion()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var resultPath = Path.Join(fixture.Root, "TestResults", "audit-result.json");
        var late = new TaskCompletionSource<RepositoryLoadOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = Stopwatch.StartNew();

        var outcome = await RunAsync(
            fixture,
            resultPath,
            new ProductionAuditHostControls(
                DeadlineOverride: name => name == "workspace-load-timeout"
                    ? TimeSpan.FromMilliseconds(50)
                    : null,
                SdkDiscovery: _ => Task.FromResult(TestToolchain(fixture.Root)),
                RepositoryLoad: (_, _) => late.Task));

        stopwatch.Stop();
        Assert.Equal(HostExecutionOutcome.Timeout, outcome.Terminal.ExecutionOutcome);
        Assert.Equal("host.workspace-load.timeout", outcome.Terminal.Failure?.Code);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
        late.SetResult(RepositoryLoadOutcome.Failure(
            new LoaderFact("workspace", "loader.test-stimulus")));
        await Task.Delay(25);
    }

    [Fact]
    public async Task ShutdownDeadline_RacesSynchronousDisposalEntryAndObservesLateCleanup()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var resultPath = Path.Join(fixture.Root, "TestResults", "audit-result.json");
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = Stopwatch.StartNew();

        var outcome = await RunAsync(
            fixture,
            resultPath,
            new ProductionAuditHostControls(
                DeadlineOverride: name => name == "graceful-shutdown-timeout"
                    ? TimeSpan.FromMilliseconds(50)
                    : null,
                Shutdown: async session =>
                {
                    await release.Task.ConfigureAwait(false);
                    await session.DisposeAsync().ConfigureAwait(false);
                }));

        stopwatch.Stop();
        Assert.Equal(HostExecutionOutcome.Timeout, outcome.Terminal.ExecutionOutcome);
        Assert.Equal("host.shutdown.timeout", outcome.Terminal.Failure?.Code);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15));
        release.SetResult();
        await Task.Delay(50);
    }

    [Theory]
    [InlineData("oversized.logical")]
    [InlineData(".contractscribe-hv-freeze-oversized.logical")]
    [InlineData(".contractscribe-hv-release-oversized.logical")]
    public async Task TemporaryDiskBound_PreventsCanonicalResultCommit(string fileName)
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var resultPath = Path.Join(fixture.Root, "TestResults", "audit-result.json");
        var temporaryRoot = Path.Join(fixture.Root, "obj", "contractscribe-audit-temp");
        Directory.CreateDirectory(temporaryRoot);
        await using (var oversized = new FileStream(
                         Path.Join(temporaryRoot, fileName),
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None))
        {
            oversized.SetLength(
                HostContractResources.RequireBound("temporary-disk-bytes") + 1);
        }

        var outcome = await RunAsync(fixture, resultPath);

        Assert.Equal(
            "host.result-validation.temporary-disk-bound",
            outcome.Terminal.Failure?.Code);
        Assert.Equal(HostExecutionOutcome.AuditError, outcome.Terminal.ExecutionOutcome);
        Assert.Equal(HostTerminalState.CommittedNonSuccess, outcome.Terminal.TerminalState);
        Assert.False(File.Exists(resultPath));
        Assert.Null(outcome.CanonicalResult);
    }

    private static IReadOnlyDictionary<string, string> CaptureProtectedRepositoryFiles(string root)
    {
        var snapshot = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
            var segments = relative.Split('/');
            if (segments[..^1].Any(IgnoredRepositorySnapshotDirectories.Contains))
            {
                continue;
            }
            var fileName = segments[^1];
            if (!ProtectedRepositoryNames.Contains(fileName)
                && !ProtectedRepositoryExtensions.Contains(Path.GetExtension(fileName)))
            {
                continue;
            }
            snapshot.Add(
                relative,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant());
        }
        return snapshot;
    }

    private static IReadOnlyList<string> FindProtectedRepositoryChanges(
        IReadOnlyDictionary<string, string> before,
        IReadOnlyDictionary<string, string> after) =>
        before.Keys
            .Concat(after.Keys)
            .Distinct(StringComparer.Ordinal)
            .Where(path =>
                !before.TryGetValue(path, out var beforeIdentity)
                || !after.TryGetValue(path, out var afterIdentity)
                || !StringComparer.Ordinal.Equals(beforeIdentity, afterIdentity))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static Task<ProductionAuditOutcome> RunAsync(
        LoaderFixture fixture,
        string resultPath,
        ProductionAuditHostControls? controls = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
        var effectiveControls = controls ?? new ProductionAuditHostControls();
        if (effectiveControls.ProcessMeterFactory is null)
        {
            effectiveControls = effectiveControls with
            {
                ProcessMeterFactory = () => new ToolchainProcessMeter(() => []),
            };
        }
        return new ProductionAuditHost(Provenance()).RunAsync(
            new ProductionAuditRequest(
                fixture.Root,
                "App/App.csproj",
                OptionalPolicy,
                ResolvedPublicationTarget.ForTestResult(fixture.Root),
                Path.Join(fixture.Root, "obj", "contractscribe-audit-temp")),
            effectiveControls,
            cancellationToken);
    }

    private static HostBuildProvenance Provenance() => new(new string('1', 40));

    private sealed class TestDeadlineRegistry
    {
        private readonly Dictionary<string, ProductionDeadlineSource> sources =
            new(StringComparer.Ordinal);

        public ProductionDeadlineSource this[string name] => sources[name];

        public ProductionDeadlineSource Create(string name)
        {
            var source = new ProductionDeadlineSource();
            Assert.True(sources.TryAdd(name, source), $"Duplicate deadline source: {name}");
            return source;
        }
    }

    private static RegisteredToolchain TestToolchain(string root) => new(
        new ToolchainIdentity("10.0.102", "10.0.0", "18.0.0", "X64"),
        root);

    private static void CreateHardLinkForTest(string existingPath, string linkPath)
    {
        var succeeded = OperatingSystem.IsWindows()
            ? CreateHardLinkW(linkPath, existingPath, IntPtr.Zero)
            : Link(existingPath, linkPath) == 0;
        if (!succeeded)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(string existingPath, string newPath);

}
