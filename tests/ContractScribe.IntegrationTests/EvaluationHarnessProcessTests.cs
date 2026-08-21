using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Agent.Runtime;
using ContractScribe.Core;
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
            new EvaluationCostPolicy("usd", 1, 1, 1));
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
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal("patch-accepted", result.Status);
            Assert.Equal("scribe.patch.accepted", result.Code);
            Assert.Equal("patch-accepted", result.Proposal?.PatchStatus);
        }
        Assert.False(report.FullCorpusComplete);
        Assert.Equal("safety-gate", report.ExecutionPurpose);
        Assert.Equal(1, report.Aggregate.SelectedCaseCount);
    }

    [Fact]
    public async Task LiveAllUsesFrozenSubsetAndSeparatesIntendedFromObservedCoverage()
    {
        var root = FindRepositoryRoot();
        var loaded = EvaluationManifestLoader.Load(CorpusRoot(root));
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new InvalidOperationException();
        var prepared = File.ReadAllText(Path.Join(
            root,
            "tools",
            "ContractScribe.Evaluation",
            "bin",
            configuration,
            "net10.0",
            "evaluation-corpus-path.txt")).Trim();
        var options = new EvaluationOptions(
            EvaluationMode.LiveAll,
            CorpusRoot(root),
            null,
            new Uri(loaded.Selection.Endpoint),
            loaded.Selection.Model,
            "UNUSED_TEST_SECRET",
            new EvaluationCostPolicy("usd", 1, 1, 1));
        var runner = new EvaluationRunner(
            loaded,
            options,
            evaluationCase => new ScriptedEvaluationExchange(evaluationCase),
            null,
            null,
            prepared);

        var report = await runner.RunAsync(CancellationToken.None);

        Assert.Equal(
            ["conflicting-evidence", "patch-rejection", "useful-proposal"],
            report.Cases.Select(item => item.CaseId).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(3, report.Aggregate.SelectedCaseCount);
        Assert.DoesNotContain(report.Cases, item => item.CaseId == "invalid-tool");
        var useful = Assert.Single(report.Cases, item => item.CaseId == "useful-proposal");
        Assert.Contains("prompt-injection", useful.IntendedCoverage);
        Assert.DoesNotContain("prompt-injection", useful.ObservedCoverage);
        Assert.Contains("tool-call", useful.ObservedCoverage);
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
        var cases = report.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        Assert.Equal(11, cases.Length);
        Assert.Equal(
            new Dictionary<string, (string Status, string Code)>(StringComparer.Ordinal)
            {
                ["useful-proposal"] = (OperatingSystem.IsLinux() ? "patch-accepted" : "runtime-failure", OperatingSystem.IsLinux() ? "scribe.patch.accepted" : "patch.host.environment-failure"),
                ["structured-skip"] = ("proposal-skipped", "scribe.proposal.skipped"),
                ["insufficient-evidence"] = ("proposal-skipped", "scribe.proposal.skipped"),
                ["conflicting-evidence"] = ("preflight-rejected", "scribe.preflight.prompt-evidence-mismatch"),
                ["invalid-tool"] = ("runtime-failure", "scribe.failure.tool-protocol"),
                ["malformed-output"] = ("runtime-failure", "scribe.failure.validation"),
                ["patch-rejection"] = ("patch-rejected", "scribe.patch.rejected"),
                ["rate-limited"] = ("provider-failure", "scribe.failure.provider"),
                ["provider-unavailable"] = ("provider-failure", "scribe.failure.provider"),
                ["budget-exhausted"] = ("budget-exhausted", "scribe.failure.budget"),
                ["timeout"] = ("timeout", "scribe.failure.timeout"),
            },
            cases.ToDictionary(
                item => item.GetProperty("caseId").GetString()!,
                item => (
                    item.GetProperty("status").GetString()!,
                    item.GetProperty("code").GetString()!),
                StringComparer.Ordinal));
        var aggregate = report.RootElement.GetProperty("aggregate");
        Assert.Equal(OperatingSystem.IsLinux() ? 11 : 10, aggregate.GetProperty("expectedMatchCount").GetInt32());
        Assert.Equal(0, aggregate.GetProperty("expectedDifferedCount").GetInt32());
        Assert.Equal(OperatingSystem.IsLinux() ? 0 : 1, aggregate.GetProperty("platformNotObservedCount").GetInt32());
        var rateLimited = Assert.Single(cases, item => item.GetProperty("caseId").GetString() == "rate-limited");
        Assert.Equal(
            [(1, "model.failure.rate-limited"), (2, "model.failure.rate-limited")],
            rateLimited.GetProperty("providerFailures").EnumerateArray()
                .Select(item => (
                    item.GetProperty("providerRequestNumber").GetInt32(),
                    item.GetProperty("code").GetString()!)));
        var unavailable = Assert.Single(
            cases,
            item => item.GetProperty("caseId").GetString() == "provider-unavailable");
        var unavailableFailure = Assert.Single(
            unavailable.GetProperty("providerFailures").EnumerateArray());
        Assert.Equal(1, unavailableFailure.GetProperty("providerRequestNumber").GetInt32());
        Assert.Equal(
            "model.failure.permanent-unavailable",
            unavailableFailure.GetProperty("code").GetString());
        Assert.All(
            cases.Where(item => item.GetProperty("caseId").GetString() is not (
                "rate-limited" or "provider-unavailable")),
            item => Assert.Empty(item.GetProperty("providerFailures").EnumerateArray()));
        var timeout = Assert.Single(cases, item => item.GetProperty("caseId").GetString() == "timeout");
        Assert.Equal("matched", timeout.GetProperty("expectationStatus").GetString());
        Assert.All(cases, item => Assert.Empty(item.GetProperty("differenceIds").EnumerateArray()));
        var useful = Assert.Single(cases, item => item.GetProperty("caseId").GetString() == "useful-proposal");
        Assert.Equal(1, useful.GetProperty("attemptCount").GetInt32());
        Assert.Equal(2, useful.GetProperty("providerRequestCount").GetInt32());
        Assert.Equal(1, useful.GetProperty("toolRoundCount").GetInt32());
        Assert.Equal(1, useful.GetProperty("toolCallCount").GetInt32());
        var usage = useful.GetProperty("usage");
        Assert.Equal(220, usage.GetProperty("inputTokens").GetInt32());
        Assert.Equal(60, usage.GetProperty("outputTokens").GetInt32());
        Assert.Equal(80, usage.GetProperty("cachedInputTokens").GetInt32());
        Assert.Equal(140, usage.GetProperty("uncachedInputTokens").GetInt32());
        Assert.Equal("cache.mixed", usage.GetProperty("cacheObservation").GetString());
        var proposal = useful.GetProperty("proposal");
        Assert.Equal("matched", proposal.GetProperty("expectationStatus").GetString());
        Assert.Empty(proposal.GetProperty("differenceIds").EnumerateArray());
        Assert.Contains(
            proposal.GetProperty("contentUnits").EnumerateArray()
                .SelectMany(unit => unit.GetProperty("lines").EnumerateArray())
                .Select(line => line.GetString()),
            line => line == "Runs the selected operation.");
    }

    [Theory]
    [InlineData("proposal-line", "proposal.expected-line-differed")]
    [InlineData("usage", "usage.missing")]
    [InlineData("cache", "usage.cache-observation-differed")]
    [InlineData("request-count", "provider-request-count-differed")]
    public async Task OfflineExpectedObservationRegressionsFailValidation(
        string perturbation,
        string expectedDifferenceId)
    {
        var root = FindRepositoryRoot();
        var loaded = EvaluationManifestLoader.Load(CorpusRoot(root));
        if (perturbation == "request-count")
        {
            loaded = loaded with
            {
                Manifest = loaded.Manifest with
                {
                    Scenarios = loaded.Manifest.Scenarios.Select(scenario =>
                        scenario.Id == "useful-proposal"
                            ? scenario with
                            {
                                OfflineExpectation = scenario.OfflineExpectation with
                                {
                                    ProviderRequestCount = scenario.OfflineExpectation.ProviderRequestCount + 1,
                                },
                            }
                            : scenario).ToArray(),
                },
            };
        }

        Assert.True(EvaluationOptions.TryParse(
            ["--offline", "--corpus", CorpusRoot(root)],
            out var options,
            out _));
        var runner = new EvaluationRunner(
            loaded,
            options!,
            prepared => prepared.Scenario.Id == "useful-proposal" && perturbation != "request-count"
                ? new PerturbingExchange(new ScriptedEvaluationExchange(prepared), perturbation)
                : new ScriptedEvaluationExchange(prepared),
            null,
            null,
            PreparedCorpusRoot(root));

        var report = await runner.RunAsync(CancellationToken.None);

        var useful = Assert.Single(report.Cases, item => item.CaseId == "useful-proposal");
        Assert.Equal("differed", useful.ExpectationStatus);
        Assert.Contains(expectedDifferenceId, useful.DifferenceIds);
        Assert.Equal(1, report.Aggregate.ExpectedDifferedCount);
        Assert.Equal(1, EvaluationApplication.ResultExitCode(options!, report));
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
    public async Task OfflineOutputRejectsCorpusAndPreparedCorpusAliasesBeforePublication()
    {
        var root = FindRepositoryRoot();
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new InvalidOperationException();
        var prepared = File.ReadAllText(Path.Join(
            root,
            "tools",
            "ContractScribe.Evaluation",
            "bin",
            configuration,
            "net10.0",
            "evaluation-corpus-path.txt")).Trim();
        var candidates = new[]
        {
            CorpusRoot(root),
            Path.Join(CorpusRoot(root), "nested-output"),
            prepared,
            Path.Join(prepared, "repository", "bin"),
            Path.Join(prepared, "repository", "obj"),
        };
        foreach (var candidate in candidates)
        {
            var result = await RunAsync(root,
            [
                "--offline",
                "--corpus", CorpusRoot(root),
                "--output", candidate,
            ]);

            Assert.Equal(1, result.ExitCode);
            Assert.Empty(result.StandardOutput);
            Assert.Equal(
                "evaluation.output.invalid" + Environment.NewLine,
                Encoding.UTF8.GetString(result.StandardError));
            Assert.False(File.Exists(Path.Join(candidate, "evaluation-partial.json")));
            Assert.False(File.Exists(Path.Join(candidate, "evaluation-report.json")));
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

    [Fact]
    public async Task LiveArtifactRequiresCallerCostPolicyBeforeCredentialOrNetworkUse()
    {
        var root = FindRepositoryRoot();
        var output = Path.Join(
            Path.GetTempPath(),
            "contract-scribe-evaluation-live-cost-test",
            Guid.NewGuid().ToString("N"));
        var secretName = "CONTRACTSCRIBE_EVALUATION_PRESENT_" + Guid.NewGuid().ToString("N");
        var result = await RunAsync(root,
        [
            "--live",
            "--safety-gate",
            "--corpus", CorpusRoot(root),
            "--endpoint", "https://api.openai.com/v1/chat/completions",
            "--model", "gpt-4.1-mini-2025-04-14",
            "--secret-env", secretName,
            "--output", output,
        ], new Dictionary<string, string?> { [secretName] = "test-secret-never-used" });

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal("evaluation.arguments.invalid" + Environment.NewLine, Encoding.UTF8.GetString(result.StandardError));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task DirectArtifactSignalPersistsCurrentCaseCancellation()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var root = FindRepositoryRoot();
        var output = Path.Join(
            Path.GetTempPath(),
            "contract-scribe-evaluation-cancel-test",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        using var process = Start(root,
        [
            "--offline",
            "--corpus", CorpusRoot(root),
            "--output", output,
        ]);
        var stdout = CopyAsync(process.StandardOutput.BaseStream);
        var stderr = CopyAsync(process.StandardError.BaseStream);
        var partial = Path.Join(output, "evaluation-partial.json");
        try
        {
            await WaitForActiveCaseAsync(
                partial,
                "useful-proposal",
                process,
                TimeSpan.FromMinutes(1));
            Assert.Equal(0, Kill(process.Id, 15));
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(1, process.ExitCode);
            Assert.Empty(await stdout);
            Assert.Equal(
                "evaluation.cancelled" + Environment.NewLine,
                Encoding.UTF8.GetString(await stderr));
            Assert.False(File.Exists(Path.Join(output, "evaluation-report.json")));
            using var report = JsonDocument.Parse(await File.ReadAllBytesAsync(partial));
            Assert.Equal("partial", report.RootElement.GetProperty("status").GetString());
            Assert.Equal("useful-proposal", report.RootElement.GetProperty("activeCaseId").GetString());
            var cancelled = Assert.Single(report.RootElement.GetProperty("cases").EnumerateArray());
            Assert.Equal("cancelled", cancelled.GetProperty("status").GetString());
            Assert.Equal("differed", cancelled.GetProperty("expectationStatus").GetString());
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
        }
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

    private static Process Start(string workingDirectory, string[] arguments)
    {
        var start = StartInfo(workingDirectory, arguments);
        return Process.Start(start) ?? throw new InvalidOperationException("Evaluation process did not start.");
    }

    private static ProcessStartInfo StartInfo(string workingDirectory, string[] arguments)
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

        return start;
    }

    private static async Task<byte[]> CopyAsync(Stream stream)
    {
        await using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        return output.ToArray();
    }

    private static async Task WaitForActiveCaseAsync(
        string path,
        string caseId,
        Process process,
        TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            if (process.HasExited)
            {
                throw new Xunit.Sdk.XunitException($"Evaluation exited before cancellation marker: {process.ExitCode}");
            }

            if (elapsed.Elapsed > timeout)
            {
                throw new TimeoutException("Evaluation cancellation marker was not published.");
            }

            if (File.Exists(path))
            {
                try
                {
                    using var report = JsonDocument.Parse(await File.ReadAllBytesAsync(path));
                    if (report.RootElement.TryGetProperty("activeCaseId", out var active)
                        && active.GetString() == caseId)
                    {
                        return;
                    }
                }
                catch (IOException)
                {
                    // Atomic replacement can race this observation; retry the bounded read.
                }
                catch (JsonException)
                {
                    // Atomic replacement can race this observation; retry the bounded read.
                }
            }

            await Task.Delay(25);
        }
    }

    private static string PreparedCorpusRoot(string root)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new InvalidOperationException();
        return File.ReadAllText(Path.Join(
            root,
            "tools",
            "ContractScribe.Evaluation",
            "bin",
            configuration,
            "net10.0",
            "evaluation-corpus-path.txt")).Trim();
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

    private sealed class PerturbingExchange(
        IDocumentationScribeModelExchange inner,
        string perturbation) : IDocumentationScribeModelExchange
    {
        public async ValueTask<DocumentationScribeModelResponse> SendAsync(
            DocumentationScribeModelRequest request,
            CancellationToken cancellationToken)
        {
            var response = await inner.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return perturbation switch
            {
                "proposal-line" when request.ProviderRequestNumber == 2 => WithDifferentProposal(response),
                "usage" => Clone(response, null, response.Cache),
                "cache" => Clone(response, response.Usage, null),
                _ => response,
            };
        }

        private static DocumentationScribeModelResponse WithDifferentProposal(
            DocumentationScribeModelResponse response)
        {
            var terminal = Assert.Single(response.TerminalSubmissions);
            var document = JsonNode.Parse(terminal.TerminalUtf8Json.Span)
                ?? throw new InvalidDataException();
            document["contentUnits"]!.AsArray()[0]!["lines"]!.AsArray()[0] =
                "Performs the selected operation.";
            return new DocumentationScribeModelResponse(
                response.ToolCalls,
                [new DocumentationScribeModelTerminalSubmission(
                    JsonSerializer.SerializeToUtf8Bytes(document))],
                response.Failure,
                response.Usage,
                response.Cache,
                response.Cost);
        }

        private static DocumentationScribeModelResponse Clone(
            DocumentationScribeModelResponse response,
            DocumentationScribeModelUsage? usage,
            DocumentationScribeCacheObservation? cache) => new(
                response.ToolCalls,
                response.TerminalSubmissions,
                response.Failure,
                usage,
                cache,
                response.Cost);
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int processId, int signal);
}
