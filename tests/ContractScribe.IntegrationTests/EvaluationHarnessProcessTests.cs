using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ContractScribe.Evaluation;

namespace ContractScribe.IntegrationTests;

[Collection("Integration process lane 2")]
public sealed class EvaluationHarnessProcessTests
{
    [Fact]
    public async Task LiveSafetyGateRunsOneCompleteMultiRequestCaseAndStopsBeforeCaseTwo()
    {
        var root = FindRepositoryRoot();
        var loaded = EvaluationManifestLoader.Load(CorpusRoot(root));
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new InvalidOperationException();
        var marker = Path.Join(
            root,
            "tools",
            "ContractScribe.Evaluation",
            "bin",
            configuration,
            "net10.0",
            "evaluation-corpus-path.txt");
        var prepared = File.ReadAllText(marker).Trim();
        var options = new EvaluationOptions(
            EvaluationMode.LiveSafetyGate,
            CorpusRoot(root),
            null,
            new Uri(loaded.Selection.Endpoint),
            loaded.Selection.Model,
            "UNUSED_TEST_SECRET",
            EvaluationCostPolicy.Unpriced);
        var factoryCalls = 0;
        var runner = new EvaluationRunner(
            loaded,
            options,
            evaluationCase =>
            {
                factoryCalls++;
                return new ScriptedEvaluationExchange(evaluationCase);
            },
            null,
            null,
            prepared);

        var report = await runner.RunAsync(CancellationToken.None);

        Assert.Equal(1, factoryCalls);
        var result = Assert.Single(report.Cases);
        Assert.Equal("useful-proposal", result.CaseId);
        Assert.Equal(2, result.ProviderRequestCount);
        Assert.Equal(1, result.ToolRoundCount);
        Assert.Equal(1, result.ToolCallCount);
        Assert.False(report.FullCorpusComplete);
        Assert.Equal("safety-gate", report.ExecutionPurpose);
        Assert.Equal(1, report.Aggregate.SelectedCaseCount);
    }

    [Fact]
    public async Task OfflineArtifactIsDeterministicAcrossFreshProcessesAndWorkingDirectories()
    {
        var root = FindRepositoryRoot();
        var first = await RunAsync(root, ["--offline", "--corpus", CorpusRoot(root)],
            new Dictionary<string, string?>
            {
                ["DOTNET_CLI_UI_LANGUAGE"] = "en-US",
                ["LANG"] = "en_US.UTF-8",
                ["TZ"] = "UTC",
            });
        var second = await RunAsync(Path.GetTempPath(), ["--corpus", CorpusRoot(root)],
            new Dictionary<string, string?>
            {
                ["DOTNET_CLI_UI_LANGUAGE"] = "zh-CN",
                ["LANG"] = "zh_CN.UTF-8",
                ["TZ"] = "Asia/Shanghai",
            });

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);
        Assert.Empty(first.StandardError);
        Assert.Empty(second.StandardError);
        Assert.Equal(first.StandardOutput, second.StandardOutput);
        using var report = JsonDocument.Parse(first.StandardOutput);
        Assert.Equal("complete", report.RootElement.GetProperty("status").GetString());
        Assert.Equal("not-measured", report.RootElement.GetProperty("latency").GetProperty("status").GetString());
        Assert.Equal(10, report.RootElement.GetProperty("cases").GetArrayLength());
    }

    [Fact]
    public async Task OfflineOutputIsConfinedAndContainsNoForbiddenMaterial()
    {
        var root = FindRepositoryRoot();
        var output = Path.Join(
            Path.GetTempPath(),
            "contract-scribe-evaluation-process-test",
            Guid.NewGuid().ToString("N"));
        try
        {
            var result = await RunAsync(root,
            [
                "--offline",
                "--corpus", CorpusRoot(root),
                "--output", output,
            ]);
            Assert.Equal(0, result.ExitCode);
            var reportPath = Path.Join(output, "evaluation-report.json");
            Assert.True(File.Exists(reportPath));
            var persisted = await File.ReadAllBytesAsync(reportPath);
            Assert.Equal(result.StandardOutput, persisted);
            var text = Encoding.UTF8.GetString(persisted);
            Assert.DoesNotContain(root, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(output, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("rawRequest", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("rawResponse", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("hiddenReasoning", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("fullDiff", text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
        }
    }

    [Fact]
    public async Task LiveArtifactFailsBeforeNetworkWhenSecretIsMissing()
    {
        var root = FindRepositoryRoot();
        var output = Path.Join(
            Path.GetTempPath(),
            "contract-scribe-evaluation-live-test",
            Guid.NewGuid().ToString("N"));
        var secretName = "CONTRACTSCRIBE_EVALUATION_ABSENT_" + Guid.NewGuid().ToString("N");
        var result = await RunAsync(root,
        [
            "--live",
            "--safety-gate",
            "--corpus", CorpusRoot(root),
            "--endpoint", "https://api.openai.com/v1/chat/completions",
            "--model", "gpt-4.1-mini-2025-04-14",
            "--secret-env", secretName,
            "--output", output,
            "--currency", "usd",
            "--cached-input-rate", "1",
            "--uncached-input-rate", "1",
            "--output-rate", "1",
        ], new Dictionary<string, string?> { [secretName] = null });

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal("evaluation.credential.missing" + Environment.NewLine, Encoding.UTF8.GetString(result.StandardError));
        Assert.False(Directory.Exists(output));
    }

    private static async Task<ProcessResult> RunAsync(
        string workingDirectory,
        string[] arguments,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var root = FindRepositoryRoot();
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new InvalidOperationException("Cannot determine configuration.");
        var artifact = Path.Join(
            root,
            "tools",
            "ContractScribe.Evaluation",
            "bin",
            configuration,
            "net10.0",
            "ContractScribe.Evaluation.dll");
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(artifact);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                if (pair.Value is null)
                {
                    start.Environment.Remove(pair.Key);
                }
                else
                {
                    start.Environment[pair.Key] = pair.Value;
                }
            }
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Evaluation process did not start.");
        await using var output = new MemoryStream();
        await using var error = new MemoryStream();
        var readOutput = process.StandardOutput.BaseStream.CopyToAsync(output);
        var readError = process.StandardError.BaseStream.CopyToAsync(error);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Evaluation process timed out.");
        }

        await Task.WhenAll(readOutput, readError);
        return new ProcessResult(process.ExitCode, output.ToArray(), error.ToArray());
    }

    private static string CorpusRoot(string root) => Path.Join(
        root,
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

    private sealed record ProcessResult(int ExitCode, byte[] StandardOutput, byte[] StandardError);
}
