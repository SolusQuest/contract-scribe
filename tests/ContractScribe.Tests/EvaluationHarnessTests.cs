using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ContractScribe.Agent.Runtime;
using ContractScribe.Evaluation;

namespace ContractScribe.Tests;

public sealed class EvaluationHarnessTests
{
    [Fact]
    public void OfflineOptionsAreDefaultAndRejectLiveOnlyInputs()
    {
        Assert.True(EvaluationOptions.TryParse(
            ["--corpus", "corpus"],
            out var defaults,
            out _));
        Assert.Equal(EvaluationMode.Offline, defaults!.Mode);
        Assert.False(defaults.IsLive);
        Assert.False(EvaluationOptions.TryParse(
            ["--offline", "--corpus", "corpus", "--endpoint", "https://example.com"],
            out _,
            out _));
        Assert.False(EvaluationOptions.TryParse(
            ["--live", "--corpus", "corpus"],
            out _,
            out _));
    }

    [Fact]
    public void LiveOptionsRequireExactlyOneManifestBoundSelector()
    {
        var common = new[]
        {
            "--live",
            "--corpus", "corpus",
            "--endpoint", "https://api.openai.com/v1/chat/completions",
            "--model", "gpt-4.1-mini-2025-04-14",
            "--secret-env", "EVALUATION_TEST_SECRET",
            "--output", Path.Join(Path.GetTempPath(), "evaluation-output"),
        };
        Assert.True(EvaluationOptions.TryParse(
            [.. common, "--safety-gate"],
            out var safety,
            out _));
        Assert.Equal(EvaluationMode.LiveSafetyGate, safety!.Mode);
        Assert.True(EvaluationOptions.TryParse(
            [.. common, "--all"],
            out var all,
            out _));
        Assert.Equal(EvaluationMode.LiveAll, all!.Mode);
        Assert.False(EvaluationOptions.TryParse(
            [.. common, "--safety-gate", "--all"],
            out _,
            out _));
    }

    [Fact]
    public void CredentialIsRemovedImmediatelyAndCannotFormatItsValue()
    {
        var name = "CONTRACTSCRIBE_EVALUATION_TEST_" + Guid.NewGuid().ToString("N");
        const string secret = "marker-secret-123";
        Environment.SetEnvironmentVariable(name, secret);
        try
        {
            Assert.True(TransportCredential.TryCapture(name, out var credential));
            Assert.Null(Environment.GetEnvironmentVariable(name));
            Assert.Equal(nameof(TransportCredential), credential!.ToString());
            var marker = credential.CreateMarker();
            Assert.True(marker.IsPresent(Encoding.UTF8.GetBytes("prefix-" + secret + "-suffix")));
            Assert.Equal(secret, credential.Take());
            Assert.Throws<InvalidOperationException>(() => credential.Take());
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void CostPolicyClosesEveryInputPartition()
    {
        Assert.True(EvaluationCostPolicy.TryCreate("usd", 1_000_000, 2_000_000, 3_000_000, out var policy));
        AssertCost(policy!, new DocumentationScribeModelUsage(10, 4, 2, 3), "Complete", 30);
        AssertCost(policy!, new DocumentationScribeModelUsage(10, 4, 2), "Complete", 30);
        AssertCost(policy!, new DocumentationScribeModelUsage(10, 4, uncachedInputTokens: 3), "Complete", 32);
        AssertCost(policy!, new DocumentationScribeModelUsage(10, 4), "Complete", 32);
        AssertCost(policy!, new DocumentationScribeModelUsage(outputTokens: 4, cachedInputTokens: 2, uncachedInputTokens: 3), "Complete", 20);
        AssertCost(policy!, new DocumentationScribeModelUsage(cachedInputTokens: 2), "Partial", 2);
        AssertCost(policy!, new DocumentationScribeModelUsage(uncachedInputTokens: 3), "Partial", 6);
        AssertCost(policy!, new DocumentationScribeModelUsage(outputTokens: 4), "Partial", 12);
        AssertCost(policy!, new DocumentationScribeModelUsage(reasoningTokens: 4), "NotReported", null);
        Assert.False(EvaluationCostPolicy.TryCreate("USD", 1, 1, 1, out _));
        Assert.Throws<ArgumentException>(() => policy!.Calculate(
            new DocumentationScribeModelUsage(3, 1, cachedInputTokens: 2, uncachedInputTokens: 2)));
    }

    [Fact]
    public void CostRoundsOncePerResponseAndRejectsProductOverflow()
    {
        Assert.True(EvaluationCostPolicy.TryCreate("usd", 1, 1, 1, out var small));
        AssertCost(
            small!,
            new DocumentationScribeModelUsage(1, 1, cachedInputTokens: 1, uncachedInputTokens: 0),
            "Complete",
            1);
        Assert.True(EvaluationCostPolicy.TryCreate(
            "usd",
            EvaluationCostPolicy.MaximumRate,
            EvaluationCostPolicy.MaximumRate,
            EvaluationCostPolicy.MaximumRate,
            out var large));
        Assert.Throws<OverflowException>(() => large!.Calculate(
            new DocumentationScribeModelUsage(16_777_216, 1_048_576)));
    }

    [Fact]
    public void CorpusManifestAndExactSelectionAreFrozen()
    {
        var loaded = EvaluationManifestLoader.Load(CorpusRoot());
        Assert.Equal("useful-proposal", loaded.Manifest.SafetyGateCaseId);
        Assert.Equal("https://api.openai.com/v1/chat/completions", loaded.Selection.Endpoint);
        Assert.Equal("gpt-4.1-mini-2025-04-14", loaded.Selection.Model);
        Assert.Equal(10, loaded.Manifest.Scenarios.Length);
        Assert.Equal(64, loaded.CorpusIdentity.Length);
    }

    [Fact]
    public void CorpusManifestRejectsUndeclaredBuildInputs()
    {
        var temporary = Path.Join(
            Path.GetTempPath(),
            "contract-scribe-evaluation-manifest-test",
            Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temporary);
            var source = CorpusRoot();
            File.Copy(Path.Join(source, "manifest.json"), Path.Join(temporary, "manifest.json"));
            using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Join(source, "manifest.json")));
            foreach (var entry in document.RootElement.GetProperty("files").EnumerateArray())
            {
                var relative = entry.GetProperty("path").GetString()!;
                var destination = Path.Join(temporary, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(Path.Join(source, relative), destination);
            }

            File.WriteAllText(
                Path.Join(temporary, "repository", "Directory.Build.targets"),
                "<Project />");

            var failure = Assert.Throws<InvalidDataException>(() =>
                EvaluationManifestLoader.Load(temporary));
            Assert.Equal("evaluation.manifest.file-set-mismatch", failure.Message);
        }
        finally
        {
            if (Directory.Exists(temporary))
            {
                Directory.Delete(temporary, recursive: true);
            }
        }
    }

    [Fact]
    public void OutputConfinementAcceptsOnlyPhysicalTemporaryDescendants()
    {
        var allowed = Path.Join(Path.GetTempPath(), "contract-scribe-evaluation-test", Guid.NewGuid().ToString("N"));
        Assert.True(EvaluationOutput.TryResolveDirectory(allowed, out var resolved));
        Assert.Equal(Path.GetFullPath(allowed), resolved);
        Assert.False(EvaluationOutput.TryResolveDirectory(CorpusRoot(), out _));
    }

    [Fact]
    public void ReportWriterRejectsCredentialsPathsAndForbiddenPayloadKinds()
    {
        const string secret = "marker-secret-456";
        var marker = SensitiveMarker.Create(secret);
        var report = MinimalReport(secret);
        Assert.Throws<InvalidDataException>(() => EvaluationReportWriter.Serialize(report, marker));
        var safe = MinimalReport("safe bounded line");
        Assert.Throws<InvalidDataException>(() => EvaluationReportWriter.Serialize(
            safe,
            null,
            "safe bounded line"));
        var bytes = EvaluationReportWriter.Serialize(safe, null);
        Assert.DoesNotContain("rawResponse", Encoding.UTF8.GetString(bytes), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InterruptedAndTimeoutCasesRemainExplicitPartialFailures()
    {
        var loaded = EvaluationManifestLoader.Load(CorpusRoot());
        Assert.True(EvaluationOptions.TryParse(
            ["--offline", "--corpus", CorpusRoot()],
            out var options,
            out _));
        var cancelled = MinimalCase("cancelled-case", "cancelled", "evaluation.case.cancelled");
        var timedOut = MinimalCase("timeout-case", "timeout", "scribe.failure.timeout");

        var report = EvaluationReport.Create(
            loaded,
            options!,
            [cancelled, timedOut],
            selectedCaseCount: 10,
            complete: false,
            elapsedMilliseconds: null);

        Assert.Equal("partial", report.Status);
        Assert.False(report.FullCorpusComplete);
        Assert.Equal(2, report.Aggregate.CompletedCaseCount);
        Assert.Equal(0, report.Aggregate.ExpectedMatchCount);
        Assert.Equal(2, report.Aggregate.FailedCaseCount);
        Assert.Collection(
            report.Cases,
            item => Assert.Equal("cancelled", item.Status),
            item => Assert.Equal("timeout", item.Status));
    }

    [Fact]
    public void EvaluationAssemblyDoesNotReferenceProcessLaunchApis()
    {
        var references = typeof(EvaluationOptions).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToArray();
        Assert.DoesNotContain("System.Diagnostics.Process", references);
    }

    [Fact]
    public void EvaluationProjectIsOptionalInternalInfrastructureWithNoReverseProductEdge()
    {
        var root = FindRepositoryRoot();
        var projectPath = Path.Join(root, "tools", "ContractScribe.Evaluation", "ContractScribe.Evaluation.csproj");
        var project = XDocument.Load(projectPath);
        Assert.Equal("Exe", project.Descendants("OutputType").Single().Value);
        Assert.Equal(
            new[]
            {
                "../../src/ContractScribe.Agent/ContractScribe.Agent.csproj",
                "../../src/ContractScribe.Cli/ContractScribe.Cli.csproj",
                "../../src/ContractScribe.Core/ContractScribe.Core.csproj",
                "../../src/ContractScribe.Patching/ContractScribe.Patching.csproj",
                "../../src/ContractScribe.Roslyn/ContractScribe.Roslyn.csproj",
                "../../tests/fixtures/documentation-scribe/evaluation/repository/ContractScribe.EvaluationFixture.csproj",
            },
            project.Descendants("ProjectReference")
                .Select(reference => reference.Attribute("Include")!.Value.Replace('\\', '/'))
                .Order(StringComparer.Ordinal)
                .ToArray());
        foreach (var productProject in Directory.EnumerateFiles(
            Path.Join(root, "src"),
            "*.csproj",
            SearchOption.AllDirectories))
        {
            Assert.DoesNotContain(
                XDocument.Load(productProject).Descendants("ProjectReference"),
                reference => reference.Attribute("Include")?.Value.Contains(
                    "ContractScribe.Evaluation",
                    StringComparison.Ordinal) == true);
        }

        Assert.Empty(typeof(EvaluationOptions).Assembly.GetExportedTypes());
        var sources = Directory.EnumerateFiles(
                Path.GetDirectoryName(projectPath)!,
                "*.cs",
                SearchOption.AllDirectories)
            .Where(path => !path.Contains(
                Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            .Select(File.ReadAllText)
            .ToArray();
        Assert.DoesNotContain(sources, source => source.Contains("Process.Start", StringComparison.Ordinal));
        Assert.DoesNotContain(sources, source => source.Contains("ProcessStartInfo", StringComparison.Ordinal));
        Assert.DoesNotContain(sources, source => source.Contains("dotnet run", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(sources, source => source.Contains("dotnet test", StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertCost(
        EvaluationCostPolicy policy,
        DocumentationScribeModelUsage usage,
        string completeness,
        long? amount)
    {
        var result = policy.Calculate(usage);
        Assert.Equal(completeness, result.Completeness.ToString());
        Assert.Equal(amount, result.AmountMicrounits);
    }

    private static EvaluationReport MinimalReport(string line) => new(
        1,
        "optional-local-provider-evaluation",
        "revision",
        "offline",
        "complete",
        "corpus",
        true,
        "corpus",
        new string('a', 64),
        "selection",
        new string('b', 64),
        null,
        new EvaluationLatencyReport("not-measured", null),
        [
            new EvaluationCaseReport(
                "case",
                "patch-accepted",
                "code",
                true,
                1,
                1,
                0,
                0,
                null,
                new EvaluationCostReport("not-reported", null, null),
                new EvaluationProposalReport(
                    "validated",
                    "patch-accepted",
                    "supported",
                    "unavailable",
                    [],
                    [new EvaluationContentUnitReport("content.summary", null, null, [line], "claim.purpose", ["evidence.source"])]),
                "passed",
                ["coverage"]),
        ],
        new EvaluationAggregateReport(
            1,
            1,
            1,
            0,
            1,
            0,
            new EvaluationCostReport("not-reported", null, null)));

    private static EvaluationCaseReport MinimalCase(string caseId, string status, string code) => new(
        caseId,
        status,
        code,
        false,
        0,
        0,
        0,
        0,
        null,
        new EvaluationCostReport("not-reported", null, null),
        null,
        "passed",
        ["interruption"]);

    private static string CorpusRoot() => Path.Join(
        FindRepositoryRoot(),
        "tests",
        "fixtures",
        "documentation-scribe",
        "evaluation");

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

        throw new DirectoryNotFoundException();
    }
}
