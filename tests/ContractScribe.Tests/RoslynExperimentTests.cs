using System.Diagnostics;
using System.Text.Json;
using ContractScribe.Roslyn;

namespace ContractScribe.Tests;

[CollectionDefinition("Roslyn experiment", DisableParallelization = true)]
public sealed class RoslynExperimentCollection;

[Collection("Roslyn experiment")]
public sealed class RoslynExperimentTests
{
    [Fact]
    public async Task LoadsSyntheticSolutionAndMatchesIndependentOracle()
    {
        var execution = await RunExperimentAsync();

        Assert.Equal(ExperimentStatus.Succeeded, execution.Result.Status);
        Assert.Null(execution.Result.FailurePhase);
        Assert.Equal(0, execution.Result.ExitCode);
        Assert.NotNull(execution.SemanticPayloadBytes);

        var expected = JsonSerializer.Deserialize<SemanticPayload>(
            File.ReadAllText(Path.Combine(FixtureRoot, "expected-symbols.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("The committed expected-symbols oracle could not be read.");

        Assert.Equal(
            SemanticPayloadSerializer.Serialize(expected),
            execution.SemanticPayloadBytes);
    }

    [Fact]
    public async Task IndependentProcessesProduceTheSameCanonicalPayload()
    {
        var first = await RunHostAsync(CreateTestOutputDirectory());
        var second = await RunHostAsync(CreateTestOutputDirectory());

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);
        var firstPayload = File.ReadAllBytes(Path.Join(first.OutputDirectory, "semantic-payload.json"));
        var secondPayload = File.ReadAllBytes(Path.Join(second.OutputDirectory, "semantic-payload.json"));
        Assert.Equal(firstPayload, secondPayload);
        Assert.DoesNotContain('\n', SemanticPayloadSerializer.DecodeUtf8(firstPayload));
        Assert.DoesNotContain('\r', SemanticPayloadSerializer.DecodeUtf8(firstPayload));
        Assert.False(firstPayload[0] == 0xEF);
    }

    [Fact]
    public async Task HostWritesEnvelopeAndStandalonePayloadWithMatchingSemantics()
    {
        var outputDirectory = CreateTestOutputDirectory();
        var run = await RunHostAsync(outputDirectory);

        Assert.Equal(0, run.ExitCode);
        Assert.Empty(run.Stderr);
        Assert.NotEmpty(run.Stdout);
        Assert.True(File.Exists(Path.Join(outputDirectory, "result.json")));
        Assert.True(File.Exists(Path.Join(outputDirectory, "semantic-payload.json")));

        using var result = JsonDocument.Parse(File.ReadAllBytes(Path.Join(outputDirectory, "result.json")));
        using var payload = JsonDocument.Parse(File.ReadAllBytes(Path.Join(outputDirectory, "semantic-payload.json")));
        Assert.Equal("succeeded", result.RootElement.GetProperty("status").GetString());
        Assert.False(result.RootElement.TryGetProperty("failurePhase", out _));
        Assert.False(result.RootElement.TryGetProperty("failureCode", out _));
        Assert.Equal(
            payload.RootElement.GetRawText(),
            result.RootElement.GetProperty("semanticPayload").GetRawText());
    }

    [Fact]
    public async Task HostRemovesStalePayloadAfterClassifiedInputFailure()
    {
        var outputDirectory = CreateTestOutputDirectory();
        var success = await RunHostAsync(outputDirectory);
        Assert.Equal(0, success.ExitCode);
        Assert.True(File.Exists(Path.Join(outputDirectory, "semantic-payload.json")));

        var failure = await RunHostAsync(outputDirectory, Path.Join(outputDirectory, "missing.sln"));
        Assert.Equal(2, failure.ExitCode);
        Assert.False(File.Exists(Path.Join(outputDirectory, "semantic-payload.json")));
        using var result = JsonDocument.Parse(File.ReadAllBytes(Path.Join(outputDirectory, "result.json")));
        Assert.Equal("invalid-input", result.RootElement.GetProperty("status").GetString());
        Assert.DoesNotContain("\\", failure.Stdout + failure.Stderr);
    }

    [Fact]
    public async Task RunnerClassifiesWorkspaceGraphMismatchFromRealSolution()
    {
        var fixture = CopyFixtureToTestDirectory();
        var solution = Path.Combine(fixture, "Sample.sln");
        var appProject = Path.Combine(fixture, "SampleApp", "SampleApp.csproj");
        File.WriteAllText(appProject, File.ReadAllText(appProject).Replace(
            "    <ProjectReference Include=\"..\\SampleLibrary\\SampleLibrary.csproj\" />",
            string.Empty,
            StringComparison.Ordinal));

        var execution = await new FrameworkDependentExperiment().RunAsync(solution);

        Assert.Equal(ExperimentStatus.ClassifiedFailure, execution.Result.Status);
        Assert.Equal(FailurePhase.WorkspaceLoad, execution.Result.FailurePhase);
        Assert.Equal("workspace.project-graph-mismatch", execution.Result.FailureCode);
    }

    [Fact]
    public async Task RunnerClassifiesCompilationErrorsFromRealSolution()
    {
        var fixture = CopyFixtureToTestDirectory();
        File.AppendAllText(Path.Combine(fixture, "SampleApp", "App.cs"), "\npublic syntax error\n");

        var execution = await new FrameworkDependentExperiment().RunAsync(Path.Combine(fixture, "Sample.sln"));

        Assert.Equal(ExperimentStatus.ClassifiedFailure, execution.Result.Status);
        Assert.Equal(FailurePhase.Compilation, execution.Result.FailurePhase);
        Assert.Equal("compilation.errors", execution.Result.FailureCode);
    }

    [Fact]
    public async Task RunnerClassifiesSerializationFailureOnRealRunPath()
    {
        var execution = await new FrameworkDependentExperiment(_ => throw new InvalidOperationException())
            .RunAsync(Path.Combine(FixtureRoot, "Sample.sln"));

        Assert.Equal(ExperimentStatus.ClassifiedFailure, execution.Result.Status);
        Assert.Equal(FailurePhase.Serialization, execution.Result.FailurePhase);
        Assert.Equal("serialization.semantic-payload-failed", execution.Result.FailureCode);
        Assert.Null(execution.SemanticPayloadBytes);
    }

    [Fact]
    public async Task RunnerClassifiesSymbolIdentityFailuresOnRealProjectionPath()
    {
        static IEnumerable<SymbolRecord> MissingDocumentationId(Microsoft.CodeAnalysis.INamespaceSymbol _)
        {
            yield return new SymbolRecord(string.Empty, "NamedType", "MissingId");
        }

        static IEnumerable<SymbolRecord> DuplicateIdentity(Microsoft.CodeAnalysis.INamespaceSymbol _)
        {
            yield return new SymbolRecord("T:Duplicate", "NamedType", "First");
            yield return new SymbolRecord("T:Duplicate", "NamedType", "Second");
        }

        var missing = await new FrameworkDependentExperiment(symbolEnumerator: MissingDocumentationId)
            .RunAsync(Path.Combine(FixtureRoot, "Sample.sln"));
        var duplicate = await new FrameworkDependentExperiment(symbolEnumerator: DuplicateIdentity)
            .RunAsync(Path.Combine(FixtureRoot, "Sample.sln"));

        Assert.Equal((FailurePhase.SymbolIdentity, "symbol.missing-documentation-id"),
            (missing.Result.FailurePhase, missing.Result.FailureCode));
        Assert.Equal((FailurePhase.SymbolIdentity, "symbol.duplicate-identity"),
            (duplicate.Result.FailurePhase, duplicate.Result.FailureCode));
        Assert.Equal(1, missing.Result.ExitCode);
        Assert.Equal(1, duplicate.Result.ExitCode);
    }

    [Fact]
    public async Task HostClassifiesUnavailableMsbuildEnvironmentWithoutPayloadOrPathLeak()
    {
        var fixture = CopyFixtureOutsideRepository();
        var outputDirectory = CreateTestOutputDirectory();
        try
        {
            var run = await RunHostAsync(outputDirectory, Path.Combine(fixture, "Sample.sln"));

            Assert.Equal(1, run.ExitCode);
            Assert.Empty(run.Stderr);
            Assert.DoesNotContain("\\", run.Stdout + run.Stderr);
            Assert.DoesNotContain("/home/", run.Stdout + run.Stderr, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", run.Stdout + run.Stderr, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(outputDirectory, "semantic-payload.json")));

            using var result = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(outputDirectory, "result.json")));
            Assert.Equal("classified-failure", result.RootElement.GetProperty("status").GetString());
            Assert.Equal("msbuild-environment", result.RootElement.GetProperty("failurePhase").GetString());
            Assert.Equal("msbuild.sdk-unavailable", result.RootElement.GetProperty("failureCode").GetString());
            Assert.False(result.RootElement.TryGetProperty("semanticPayload", out _));
        }
        finally
        {
            Directory.Delete(fixture, recursive: true);
        }
    }

    [Fact]
    public async Task MissingSolutionIsAnInvalidInputFailure()
    {
        var execution = await new FrameworkDependentExperiment().RunAsync(
            Path.Join(FixtureRoot, "missing.sln"));

        Assert.Equal(ExperimentStatus.InvalidInput, execution.Result.Status);
        Assert.Equal(FailurePhase.Input, execution.Result.FailurePhase);
        Assert.Equal("input.solution-not-found", execution.Result.FailureCode);
        Assert.Equal(2, execution.Result.ExitCode);
    }

    [Fact]
    public void FailureRegistryRejectsUnknownCodes()
    {
        foreach (var phase in Enum.GetValues<FailurePhase>())
        {
            Assert.NotEmpty(FailureRegistry.Snapshot()[phase]);
        }

        Assert.True(FailureRegistry.IsKnown(FailurePhase.SymbolIdentity, "symbol.duplicate-identity"));
        Assert.False(FailureRegistry.IsKnown(FailurePhase.SymbolIdentity, "symbol.unknown"));
    }

    [Fact]
    public void FailureRegistryMatchesCommittedPublicRegistry()
    {
        var root = FindRepositoryRoot();
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "docs", "20_architecture", "experiments", "m0.4-failure-registry-v1.json")));
        var committed = document.RootElement.GetProperty("phases");

        foreach (var phase in FailureRegistry.Snapshot())
        {
            var jsonName = phase.Key switch
            {
                FailurePhase.MsbuildEnvironment => "msbuild-environment",
                FailurePhase.WorkspaceLoad => "workspace-load",
                FailurePhase.SymbolIdentity => "symbol-identity",
                _ => phase.Key.ToString().ToLowerInvariant(),
            };

            var committedCodes = committed.GetProperty(jsonName)
                .EnumerateArray()
                .Select(code => code.GetString()!)
                .OrderBy(code => code, StringComparer.Ordinal)
                .ToArray();
            var runtimeCodes = phase.Value.OrderBy(code => code, StringComparer.Ordinal).ToArray();

            Assert.Equal(committedCodes, runtimeCodes);
        }
    }

    [Fact]
    public void WorkspaceDiagnosticClassificationKeepsEnvironmentSeparate()
    {
        Assert.Equal(
            "msbuild.sdk-unavailable",
            FailureClassifier.ClassifyWorkspaceDiagnostic("The SDK resolver could not find the targeting pack."));
        Assert.Equal(
            "workspace.solution-load-failed",
            FailureClassifier.ClassifyWorkspaceDiagnostic("The project could not be evaluated."));
    }

    [Fact]
    public void ClassifiedFailurePhasesUseExitCodeOne()
    {
        foreach (var phase in Enum.GetValues<FailurePhase>().Where(phase => phase != FailurePhase.Input))
        {
            var code = FailureRegistry.Snapshot()[phase].First();
            var result = ExperimentResult.Failure(
                ExperimentStatus.ClassifiedFailure,
                phase,
                code);

            Assert.Equal(1, result.ExitCode);
            Assert.True(FailureRegistry.IsKnown(phase, result.FailureCode!));
        }
    }

    [Fact]
    public void SemanticPayloadSerializerRejectsNullPayloadBeforeWriting()
    {
        Assert.Throws<ArgumentNullException>(() => SemanticPayloadSerializer.Serialize(null!));

        var result = ExperimentResult.Failure(
            ExperimentStatus.ClassifiedFailure,
            FailurePhase.Serialization,
            "serialization.semantic-payload-failed");
        using var document = JsonDocument.Parse(ExperimentResultSerializer.Serialize(result));
        Assert.Equal("serialization", document.RootElement.GetProperty("failurePhase").GetString());
    }

    [Fact]
    public void ClassifiedFailureDoesNotContainSemanticPayload()
    {
        var result = ExperimentResult.Failure(
            ExperimentStatus.ClassifiedFailure,
            FailurePhase.Compilation,
            "compilation.errors");
        using var document = JsonDocument.Parse(ExperimentResultSerializer.Serialize(result));

        Assert.False(document.RootElement.TryGetProperty("semanticPayload", out _));
    }

    [Fact]
    public void ResultSerializerFailsClosedForUnknownStatusAndCodeCombinations()
    {
        var unknownCode = ExperimentResult.Failure(
            ExperimentStatus.ClassifiedFailure,
            FailurePhase.Compilation,
            "workspace.solution-load-failed");
        Assert.Throws<InvalidOperationException>(() => ExperimentResultSerializer.Serialize(unknownCode));

        var invalidSuccess = new ExperimentResult(
            ExperimentFormat.Version,
            ExperimentStatus.Succeeded,
            FailurePhase.Input,
            "input.solution-not-found",
            null,
            Array.Empty<DiagnosticRecord>());
        Assert.Throws<InvalidOperationException>(() => ExperimentResultSerializer.Serialize(invalidSuccess));

        var invalidClassifiedInput = ExperimentResult.Failure(
            ExperimentStatus.ClassifiedFailure,
            FailurePhase.Input,
            "input.solution-not-found");
        Assert.Throws<InvalidOperationException>(() => ExperimentResultSerializer.Serialize(invalidClassifiedInput));
    }

    [Fact]
    public void ExperimentArtifactsContainNoMachinePathPlaceholders()
    {
        var root = FindRepositoryRoot();
        var files = Directory.GetFiles(Path.Combine(root, "docs", "20_architecture", "experiments"), "*", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(FixtureRoot, "*.json", SearchOption.AllDirectories))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var content in files.Select(File.ReadAllText))
        {
            Assert.DoesNotMatch(@"(?i)([A-Z]:\\|\\\\|/home/|TO_BE_FILLED|<private>)", content);
        }
    }

    [Fact]
    public void ResultStatusAndFailurePhaseUseStableJsonNames()
    {
        var result = ExperimentResult.Failure(
            ExperimentStatus.ClassifiedFailure,
            FailurePhase.Serialization,
            "serialization.semantic-payload-failed");

        var json = ExperimentResultSerializer.Serialize(result);
        using var document = JsonDocument.Parse(json);

        Assert.Equal("classified-failure", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("serialization", document.RootElement.GetProperty("failurePhase").GetString());
        Assert.Equal("serialization.semantic-payload-failed", document.RootElement.GetProperty("failureCode").GetString());
    }

    [Fact]
    public void OracleMismatchIsNotARunnerFailurePhase()
    {
        var expected = new SemanticPayload(
            new[] { new ProjectPayload("SampleApp", Array.Empty<SymbolRecord>()) });
        var observed = new SemanticPayload(
            new[] { new ProjectPayload("SampleApp", new[] { new SymbolRecord("T:SampleApp.AppType", "NamedType", "AppType") }) });

        Assert.NotEqual(
            SemanticPayloadSerializer.Serialize(expected),
            SemanticPayloadSerializer.Serialize(observed));
    }

    private static async Task<ExperimentExecution> RunExperimentAsync()
    {
        return await new FrameworkDependentExperiment().RunAsync(
            Path.Combine(FixtureRoot, "Sample.sln"));
    }

    private static async Task<HostRun> RunHostAsync(string outputDirectory, string? solutionPath = null)
    {
        var host = Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "ContractScribe.Roslyn.Experiment",
            "bin",
            "Release",
            "net10.0",
            "ContractScribe.Roslyn.Experiment.dll");
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(host);
        startInfo.ArgumentList.Add(solutionPath ?? Path.Combine(FixtureRoot, "Sample.sln"));
        startInfo.ArgumentList.Add(outputDirectory);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The experiment host did not start.");

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new HostRun(process.ExitCode, stdout, stderr, outputDirectory);
    }

    private static string CreateTestOutputDirectory()
    {
        var directory = Path.Combine(FindRepositoryRoot(), "TestResults", "m0.4-host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string CopyFixtureToTestDirectory()
    {
        var target = Path.Combine(FindRepositoryRoot(), "TestResults", "m0.4-fixture-tests", Guid.NewGuid().ToString("N"));
        CopyDirectory(FixtureRoot, target);
        return target;
    }

    private static string CopyFixtureOutsideRepository()
    {
        var target = Path.Combine(Path.GetTempPath(), "contract-scribe-m0.4", Guid.NewGuid().ToString("N"));
        CopyDirectory(FixtureRoot, target);
        return target;
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.GetDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }

    private sealed record HostRun(int ExitCode, string Stdout, string Stderr, string OutputDirectory);

    private static string FixtureRoot
    {
        get => Path.Combine(FindRepositoryRoot(), "tests", "fixtures", "roslyn-msbuild", "v1");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ContractScribe.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
