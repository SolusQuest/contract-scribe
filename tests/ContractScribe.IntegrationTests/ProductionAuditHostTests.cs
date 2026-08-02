using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ContractScribe.Core;
using ContractScribe.Core.Hosting;
using ContractScribe.HostValidation;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed class ProductionAuditHostTests
{
    private static readonly byte[] OptionalPolicy = Encoding.UTF8.GetBytes(
        "{\"defaultDecision\":\"optional\",\"schemaVersion\":1,\"targetProfile\":\"profile.external-api\"}\n");

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
    public void Publisher_RenamesTheHeldSingleLinkStagingFile()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "contractscribe-publisher",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Join(root, "TestResults"));
        try
        {
            var target = ResolvedPublicationTarget.ForValidationFixture(root);
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
            var validation = ResolvedPublicationTarget.ForValidationFixture(repository);
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
            var target = ResolvedPublicationTarget.ForValidationFixture(root);
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
            var target = ResolvedPublicationTarget.ForValidationFixture(root);
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
            var target = ResolvedPublicationTarget.ForValidationFixture(root);
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
    public async Task TemporaryDiskMeter_DirectAllocationCleanupProducesOneAtomicFact()
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
            await File.WriteAllBytesAsync(staging, new byte[257]);
            File.Delete(staging);

            var barrier = Path.Join(root, "watcher-barrier.bin");
            await File.WriteAllBytesAsync(barrier, new byte[258]);
            Assert.True(SpinWait.SpinUntil(
                () => meter.HighWater >= 258,
                TimeSpan.FromSeconds(5)));

            Assert.True(meter.TryCreateFactWithinThreshold(out var fact));
            Assert.Equal(258, fact!.Measured);
            File.Delete(barrier);
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
    public void ToolchainProcessMeter_FailsClosedForUnclassifiedDescendant()
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

        Assert.Throws<IOException>(() => meter.SelectToolchain(new RegisteredToolchain(
            new ToolchainIdentity("10.0.102", "10.0.0", "18.0.0", "X64"),
            Path.GetTempPath())));
    }

    [Fact]
    public async Task ToolchainProcessMeter_ClassifiesTheSelectedProductionToolchain()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        using var meter = new ToolchainProcessMeter();
        var selected = await MsBuildBootstrap.EnsureRegisteredForProductionHostAsync(
            Path.Join(fixture.Root, "App"),
            CancellationToken.None);

        _ = meter.SelectToolchain(selected);
    }

    [Fact]
    public void ValidationActivation_RejectsMixedCliAndRoslynMetadata()
    {
        Assert.False(HostValidationSubjectAdapter.IsEnabledFor(
            typeof(ProductionAuditHostTests).Assembly));
        var dynamicAssembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("ContractScribe.MixedValidationMetadata"),
            AssemblyBuilderAccess.Run);
        var constructor = typeof(AssemblyMetadataAttribute).GetConstructor(
            [typeof(string), typeof(string)])!;
        dynamicAssembly.SetCustomAttribute(new CustomAttributeBuilder(
            constructor,
            ["ContractScribeHostValidationSubject", "enabled"]));
        dynamicAssembly.SetCustomAttribute(new CustomAttributeBuilder(
            constructor,
            ["ContractScribeSourceRevision", new string('1', 40)]));
        dynamicAssembly.SetCustomAttribute(new CustomAttributeBuilder(
            constructor,
            ["ContractScribeSourceConfigurationId", "source." + new string('2', 64)]));
        dynamicAssembly.SetCustomAttribute(new CustomAttributeBuilder(
            constructor,
            ["ContractScribeBuildSdkVersion", "10.0.102"]));

        Assert.Throws<InvalidOperationException>(() =>
            HostValidationSubjectAdapter.IsEnabledFor(dynamicAssembly));
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
    public async Task UnexpectedSdkBoundaryFailure_EscapesBeforeToolchainSelection()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var resultPath = Path.Join(fixture.Root, "TestResults", "audit-result.json");

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(
            fixture,
            resultPath,
            new ProductionAuditHostControls(
                SdkDiscovery: _ => throw new InvalidOperationException("test-only"))));

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
                    }
                    return Task.CompletedTask;
                }),
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
                ResolvedPublicationTarget.ForValidationFixture(fixture.Root),
                Provenance()),
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

        var outcome = await RunAsync(
            fixture,
            resultPath,
            new ProductionAuditHostControls(
                DeadlineOverride: name => name == "sdk-discovery-timeout"
                    ? TimeSpan.FromMilliseconds(50)
                    : null,
                StageBoundary: async (stage, token) =>
                {
                    if (stage != HostStage.SdkDiscovery)
                    {
                        return;
                    }
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    }
                    catch (OperationCanceledException)
                    {
                        caller.Cancel();
                        throw;
                    }
                }),
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

        var outcome = await RunAsync(
            fixture,
            resultPath,
            new ProductionAuditHostControls(
                DeadlineOverride: name => name == "sdk-discovery-timeout"
                    ? TimeSpan.FromMilliseconds(50)
                    : null,
                StageBoundary: async (stage, token) =>
                {
                    if (stage != HostStage.SdkDiscovery)
                    {
                        return;
                    }
                    caller.Cancel();
                    await Task.Delay(100, CancellationToken.None);
                    throw new OperationCanceledException(token);
                }),
            caller.Token);

        Assert.Equal(HostExecutionOutcome.Cancelled, outcome.Terminal.ExecutionOutcome);
        Assert.Equal("host.sdk-discovery.cancelled", outcome.Terminal.Failure?.Code);
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

    [Fact]
    public async Task TemporaryDiskBound_PreventsCanonicalResultCommit()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var resultPath = Path.Join(fixture.Root, "TestResults", "audit-result.json");
        var temporaryRoot = Path.Join(fixture.Root, "obj", "contractscribe-audit-temp");
        Directory.CreateDirectory(temporaryRoot);
        await using (var oversized = new FileStream(
                         Path.Join(temporaryRoot, "oversized.logical"),
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

    [Fact]
    public async Task ValidationAdapter_EmitsCanonicalRegistryBoundInvalidInputResponse()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "contractscribe-host-adapter",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Join(root, "Fixture.csproj"), "<Project />\n");
            CanonicalJson.WriteCanonical(
                Path.Join(root, ".contractscribe-fixture.json"),
                new
                {
                    formatVersion = "contractscribe-m1-host-validation-fixture-recipe-v1",
                    fixture = "failure.invalid-input",
                });
            var requestPath = Path.Join(root, "request.json");
            var responsePath = Path.Join(root, "response.json");
            CanonicalJson.WriteCanonical(
                requestPath,
                new SubjectRequest(
                    "contractscribe-m1-host-validation-subject-request-v1",
                    "production-host",
                    "failure.invalid-input",
                    "run-1",
                    root,
                    responsePath,
                    null,
                    [],
                    "continue"));

            var exitCode = await HostValidationSubjectAdapter.RunForTestsAsync(
                requestPath,
                responsePath,
                Provenance());

            Assert.Equal(0, exitCode);
            SchemaValidation.ValidateDefinition(
                responsePath,
                Path.Join(
                    FindRepositoryRoot(),
                    "schemas",
                    "validation",
                    "m1-host-validation-subject-v1.schema.json"),
                "subjectResponse",
                requireCanonical: true);
            var response = CanonicalJson.DeserializeStrict<SubjectResponse>(
                responsePath,
                64 * 1024,
                requireCanonical: true);
            Assert.Equal("invalid-input", response.ExecutionOutcome);
            Assert.Equal("host.input.invalid-request", response.FailureCode);
            Assert.Equal("input", response.FailureStage);
            Assert.Equal("not-selected", response.HostFacts?.ToolchainSelectionState);
            Assert.Equal(
                HostContractResources.FailureRegistrySha256,
                response.FailureRegistryIdentity);
            Assert.False(File.Exists(Path.Join(root, "TestResults", "audit-result.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ValidationAdapter_SameMissingAssetsFixtureHasCommonSubjectProjection()
    {
        var missingAssets = await RunMissingAssetsAdapterAsync("toolchain.missing-assets");
        var noAutomaticRestore = await RunMissingAssetsAdapterAsync(
            "toolchain.no-automatic-restore");

        Assert.Equal("toolchain.missing-assets-classified", missingAssets.ObservationCode);
        Assert.Equal("internally-enforceable", missingAssets.EnforcementClass);
        Assert.Equal(missingAssets.ObservationCode, noAutomaticRestore.ObservationCode);
        Assert.Equal(missingAssets.EnforcementClass, noAutomaticRestore.EnforcementClass);
        Assert.Equal(missingAssets.ExecutionOutcome, noAutomaticRestore.ExecutionOutcome);
        Assert.Equal(missingAssets.FailureCode, noAutomaticRestore.FailureCode);
        Assert.Equal(missingAssets.FailureStage, noAutomaticRestore.FailureStage);
        Assert.Equal("host.workspace-load.failed", missingAssets.FailureCode);
        Assert.Equal("load-failure", missingAssets.ExecutionOutcome);
        Assert.Equal("workspace-load", missingAssets.FailureStage);
    }

    [Fact]
    public async Task ValidationAdapter_ProjectsOnlyThePrimaryDiagnosticForSchemaSemantics()
    {
        var success = await RunDiagnosticProjectionAdapterAsync("adapter.supporting-success");
        var failure = await RunDiagnosticProjectionAdapterAsync("adapter.supporting-failure");
        var reversed = await RunDiagnosticProjectionAdapterAsync(
            "adapter.supporting-failure-reversed");
        var successFacts = Assert.IsType<HostObservationFacts>(success.HostFacts);
        var failureFacts = Assert.IsType<HostObservationFacts>(failure.HostFacts);
        var reversedFacts = Assert.IsType<HostObservationFacts>(reversed.HostFacts);

        Assert.Empty(successFacts.NormalizedDiagnosticFacts);
        var primary = Assert.Single(failureFacts.NormalizedDiagnosticFacts);
        Assert.Equal("host.workspace-load.failed", primary.Code);
        Assert.Equal("workspace-load", primary.Stage);
        Assert.Equal(
            failureFacts.NormalizedDiagnosticFacts,
            reversedFacts.NormalizedDiagnosticFacts);
    }

    [Fact]
    public async Task ValidationAdapter_EmitsSchemaLegalPreselectionEnvironmentFailure()
    {
        var response = await RunDiagnosticProjectionAdapterAsync("failure.sdk-environment");

        Assert.Equal("environment-unavailable", response.ExecutionOutcome);
        Assert.Equal("host.sdk-discovery.unavailable", response.FailureCode);
        Assert.Equal("sdk-discovery", response.FailureStage);
        var facts = Assert.IsType<HostObservationFacts>(response.HostFacts);
        Assert.Equal("not-selected", facts.ToolchainSelectionState);
        var primary = Assert.Single(facts.NormalizedDiagnosticFacts);
        Assert.Equal(response.FailureCode, primary.Code);
        Assert.Equal(response.FailureStage, primary.Stage);
    }

    [Fact]
    public async Task ValidationAdapter_NeverMasksACommittedHostResultForObservationOnlyFixture()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        CanonicalJson.WriteCanonical(
            Path.Join(fixture.Root, ".contractscribe-fixture.json"),
            new { fixture = "entry.slnx" });
        var requestPath = Path.Join(fixture.Root, "request.json");
        var responsePath = Path.Join(fixture.Root, "response.json");
        CanonicalJson.WriteCanonical(
            requestPath,
            new SubjectRequest(
                "contractscribe-m1-host-validation-subject-request-v1",
                "production-host",
                "observation-only-fixture",
                "run-1",
                fixture.Root,
                responsePath,
                null,
                [],
                "continue"));

        var exitCode = await HostValidationSubjectAdapter.RunForTestsAsync(
            requestPath,
            responsePath,
            Provenance());
        var response = CanonicalJson.DeserializeStrict<SubjectResponse>(
            responsePath,
            64 * 1024,
            requireCanonical: true);

        Assert.Equal(0, exitCode);
        Assert.Equal("succeeded", response.ExecutionOutcome);
        Assert.Equal("committed", response.TerminalState);
        Assert.Equal("published", response.ArtifactState);
        Assert.NotNull(response.CanonicalResult);
        Assert.Equal("committed", response.HostFacts?.OutputCommit.Status);
        Assert.Equal(
            response.CanonicalResult!.Sha256,
            response.HostFacts?.OutputCommit.Sha256);
    }

    private static Task<ProductionAuditOutcome> RunAsync(
        LoaderFixture fixture,
        string resultPath,
        ProductionAuditHostControls? controls = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
        return new ProductionAuditHost(Provenance()).RunAsync(
            new ProductionAuditRequest(
                fixture.Root,
                "App/App.csproj",
                OptionalPolicy,
                ResolvedPublicationTarget.ForValidationFixture(fixture.Root),
                Provenance(),
                Path.Join(fixture.Root, "obj", "contractscribe-audit-temp")),
            controls ?? new ProductionAuditHostControls(),
            cancellationToken);
    }

    private static HostBuildProvenance Provenance() => new(
        new string('1', 40),
        "source." + new string('2', 64),
        "10.0.102",
        HostContractResources.ContractBaselineSha256,
        HostContractResources.FailureRegistrySha256,
        HostContractResources.CalibratedBoundsSha256);

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

    private static async Task<SubjectResponse> RunMissingAssetsAdapterAsync(
        string vectorId)
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "contractscribe-host-adapter",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(
                Path.Join(root, "Fixture.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>\n");
            await File.WriteAllTextAsync(
                Path.Join(root, "Fixture.cs"),
                "namespace ContractScribe.ValidationFixture; public sealed class FixtureType { }\n");
            CanonicalJson.WriteCanonical(
                Path.Join(root, ".contractscribe-fixture.json"),
                new { fixture = "toolchain.assets-missing" });
            var requestPath = Path.Join(root, "request.json");
            var responsePath = Path.Join(root, "response.json");
            CanonicalJson.WriteCanonical(
                requestPath,
                new SubjectRequest(
                    "contractscribe-m1-host-validation-subject-request-v1",
                    "production-host",
                    vectorId,
                    "run-1",
                    root,
                    responsePath,
                    null,
                    [],
                    "continue"));

            var exitCode = await HostValidationSubjectAdapter.RunForTestsAsync(
                requestPath,
                responsePath,
                Provenance());

            Assert.Equal(0, exitCode);
            SchemaValidation.ValidateDefinition(
                responsePath,
                Path.Join(
                    FindRepositoryRoot(),
                    "schemas",
                    "validation",
                    "m1-host-validation-subject-v1.schema.json"),
                "subjectResponse",
                requireCanonical: true);
            return CanonicalJson.DeserializeStrict<SubjectResponse>(
                responsePath,
                64 * 1024,
                requireCanonical: true);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<SubjectResponse> RunDiagnosticProjectionAdapterAsync(
        string fixtureProfile)
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        CanonicalJson.WriteCanonical(
            Path.Join(fixture.Root, ".contractscribe-fixture.json"),
            new { fixture = fixtureProfile });
        var requestPath = Path.Join(fixture.Root, "request.json");
        var responsePath = Path.Join(fixture.Root, "response.json");
        CanonicalJson.WriteCanonical(
            requestPath,
            new SubjectRequest(
                "contractscribe-m1-host-validation-subject-request-v1",
                "production-host",
                fixtureProfile,
                "run-1",
                fixture.Root,
                responsePath,
                null,
                [],
                "continue"));

        var exitCode = await HostValidationSubjectAdapter.RunForTestsAsync(
            requestPath,
            responsePath,
            Provenance());

        Assert.Equal(0, exitCode);
        SchemaValidation.ValidateDefinition(
            responsePath,
            Path.Join(
                FindRepositoryRoot(),
                "schemas",
                "validation",
                "m1-host-validation-subject-v1.schema.json"),
            "subjectResponse",
            requireCanonical: true);
        return CanonicalJson.DeserializeStrict<SubjectResponse>(
            responsePath,
            64 * 1024,
            requireCanonical: true);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Join(current.FullName, "ContractScribe.slnx")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new InvalidOperationException("Repository root not found.");
    }
}
