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

    private static Task<ProductionAuditOutcome> RunAsync(
        LoaderFixture fixture,
        string resultPath,
        ProductionAuditHostControls? controls = null,
        CancellationToken cancellationToken = default) =>
        new ProductionAuditHost(Provenance()).RunAsync(
            new ProductionAuditRequest(
                fixture.Root,
                "App/App.csproj",
                OptionalPolicy,
                resultPath,
                Provenance(),
                Path.Join(fixture.Root, "obj", "contractscribe-audit-temp")),
            controls ?? new ProductionAuditHostControls(),
            cancellationToken);

    private static HostBuildProvenance Provenance() => new(
        new string('1', 40),
        "source." + new string('2', 64),
        "10.0.102",
        HostContractResources.ContractBaselineSha256,
        HostContractResources.FailureRegistrySha256,
        HostContractResources.CalibratedBoundsSha256);

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
