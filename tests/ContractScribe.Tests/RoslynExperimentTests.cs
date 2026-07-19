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

        var expected = new SemanticPayload(
            new[]
            {
                new ProjectPayload(
                    "SampleApp",
                    new[]
                    {
                        new SymbolRecord("M:SampleApp.AppType.Run", "Method", "Run"),
                        new SymbolRecord("T:SampleApp.AppType", "NamedType", "AppType"),
                    }),
                new ProjectPayload(
                    "SampleLibrary",
                    new[]
                    {
                        new SymbolRecord(
                            "M:SampleLibrary.Api.RootType.Process(System.Int32)",
                            "Method",
                            "Process"),
                        new SymbolRecord(
                            "M:SampleLibrary.Api.RootType.Process(System.String)",
                            "Method",
                            "Process"),
                        new SymbolRecord(
                            "M:SampleLibrary.Api.RootType.get_Value",
                            "Method",
                            "get_Value"),
                        new SymbolRecord(
                            "M:SampleLibrary.Api.RootType.set_Value(System.Int32)",
                            "Method",
                            "set_Value"),
                        new SymbolRecord(
                            "P:SampleLibrary.Api.RootType.Value",
                            "Property",
                            "Value"),
                        new SymbolRecord(
                            "T:SampleLibrary.Api.RootType",
                            "NamedType",
                            "RootType"),
                        new SymbolRecord(
                            "T:SampleLibrary.Api.RootType.NestedType",
                            "NamedType",
                            "NestedType"),
                    }),
            });

        Assert.Equal(
            SemanticPayloadSerializer.Serialize(expected),
            execution.SemanticPayloadBytes);
    }

    [Fact]
    public async Task IndependentProcessesProduceTheSameCanonicalPayload()
    {
        var first = await RunExperimentAsync();
        var second = await RunExperimentAsync();

        Assert.Equal(first.SemanticPayloadBytes, second.SemanticPayloadBytes);
        Assert.DoesNotContain('\n', SemanticPayloadSerializer.DecodeUtf8(first.SemanticPayloadBytes!));
        Assert.DoesNotContain('\r', SemanticPayloadSerializer.DecodeUtf8(first.SemanticPayloadBytes!));
        Assert.False(first.SemanticPayloadBytes![0] == 0xEF);
    }

    [Fact]
    public async Task HostWritesEnvelopeAndStandalonePayloadWithMatchingSemantics()
    {
        var outputDirectory = Path.Combine(FindRepositoryRoot(), "TestResults", "m0.4-host-output");
        Directory.CreateDirectory(outputDirectory);
        foreach (var file in new[] { "result.json", "semantic-payload.json" })
        {
            File.Delete(Path.Combine(outputDirectory, file));
        }

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
        startInfo.ArgumentList.Add(Path.Combine(FixtureRoot, "Sample.sln"));
        startInfo.ArgumentList.Add(outputDirectory);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The experiment host did not start.");

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(0, process.ExitCode);
        Assert.Empty(stderr);
        Assert.NotEmpty(stdout);
        Assert.True(File.Exists(Path.Combine(outputDirectory, "result.json")));
        Assert.True(File.Exists(Path.Combine(outputDirectory, "semantic-payload.json")));

        using var result = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(outputDirectory, "result.json")));
        using var payload = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(outputDirectory, "semantic-payload.json")));
        Assert.Equal("succeeded", result.RootElement.GetProperty("status").GetString());
        Assert.False(result.RootElement.TryGetProperty("failurePhase", out _));
        Assert.False(result.RootElement.TryGetProperty("failureCode", out _));
        Assert.Equal(
            payload.RootElement.GetRawText(),
            result.RootElement.GetProperty("semanticPayload").GetRawText());
    }

    [Fact]
    public async Task MissingSolutionIsAnInvalidInputFailure()
    {
        var execution = await new FrameworkDependentExperiment().RunAsync(
            Path.Combine(FixtureRoot, "missing.sln"));

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
    public void ExperimentArtifactsContainNoMachinePathPlaceholders()
    {
        var root = FindRepositoryRoot();
        var files = Directory.GetFiles(Path.Combine(root, "docs", "20_architecture", "experiments"), "*", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(FixtureRoot, "*.json", SearchOption.AllDirectories))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var content = File.ReadAllText(file);
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
