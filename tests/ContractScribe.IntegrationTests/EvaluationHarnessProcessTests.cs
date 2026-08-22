using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Agent.Providers;
using ContractScribe.Agent.Runtime;
using ContractScribe.Core;
using ContractScribe.Evaluation;
using ContractScribe.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ContractScribe.IntegrationTests;

[Collection("Integration process lane 2")]
public sealed class EvaluationHarnessProcessTests
{
    [Fact]
    public async Task LiveSafetyGateRunsOneCompleteMultiRequestCaseAndStopsBeforeCaseTwo()
    {
        var root = FindRepositoryRoot();
        var corpus = CorpusRoot(root);
        var loaded = EvaluationManifestLoader.Load(corpus);
        var usefulScenario = loaded.Manifest.Scenarios.Single(scenario =>
            scenario.Id == "useful-proposal");
        Assert.Equal("Performs no operation.", usefulScenario.ProposalLine);
        var structuredSkip = loaded.Manifest.Scenarios.Single(scenario =>
            scenario.Id == "structured-skip");
        Assert.Equal(0, structuredSkip.OfflineExpectation.ToolCallCount);

        var source = await File.ReadAllTextAsync(Path.Join(
            corpus,
            "repository",
            "src",
            "Fixture.cs"));
        var tree = CSharpSyntaxTree.ParseText(source);
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(candidate => candidate.Identifier.ValueText == "Run");
        var compilation = CSharpCompilation.Create(
            "EvaluationCorpusOracle",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var symbol = compilation.GetSemanticModel(tree).GetDeclaredSymbol(method);
        Assert.NotNull(symbol);
        Assert.Equal(usefulScenario.TargetDocumentationId, symbol.GetDocumentationCommentId());
        Assert.NotNull(method.Body);
        Assert.Empty(method.Body.Statements);

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
        var selected = loaded.Selection.Configurations.Single(item =>
            item.ConfigurationId == "deepseek-primary");
        var options = new EvaluationOptions(
            EvaluationMode.LiveSafetyGate,
            corpus,
            null,
            selected.ConfigurationId,
            new Uri(selected.Endpoint),
            selected.Model,
            "UNUSED_TEST_SECRET",
            new EvaluationCostPolicy("usd", 1, 1, 1));
        var factoryCalls = 0;
        PreparedEvaluationCase? preparedCase = null;
        RecordingExchange? recording = null;
        var runner = new EvaluationRunner(
            loaded,
            options,
            evaluationCase =>
            {
                factoryCalls++;
                preparedCase = evaluationCase;
                recording = new RecordingExchange(new ScriptedEvaluationExchange(evaluationCase));
                return recording;
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
        Assert.Equal("matched", result.SafetyToolExpectationStatus);
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal("patch-accepted", result.Status);
            Assert.Equal("scribe.patch.accepted", result.Code);
            Assert.Equal("patch-accepted", result.Proposal?.PatchStatus);
        }
        Assert.False(report.FullCorpusComplete);
        Assert.Equal("safety-gate", report.ExecutionPurpose);
        Assert.Equal("deepseek-primary", report.ConfigurationId);
        Assert.Equal(1, report.Aggregate.SelectedCaseCount);

        Assert.NotNull(preparedCase);
        var sourceEvidence = preparedCase.Request.EvidenceReferences.Single(reference =>
            reference.EvidenceReferenceId == "evidence.source");
        Assert.Equal(DocumentationScribeEvidenceAuthority.SourceDeclaration, sourceEvidence.Authority);
        var proposal = result.Proposal ?? throw new InvalidDataException();
        var contentUnit = Assert.Single(proposal.ContentUnits);
        Assert.Equal(["evidence.source"], contentUnit.EvidenceReferenceIds);

        Assert.NotNull(recording);
        var firstRequest = Assert.Single(recording.Requests, request =>
            request.ProviderRequestNumber == 1);
        const string instruction =
            "Before submitting a documentation proposal for `M:EvaluationCorpus.Fixture.Run`, "
            + "make exactly one call to the provided repository tool described as "
            + "\"Search for one ordinal literal inside an authorized repository scope.\" "
            + "Use it to verify the declaration with the literal `public void Run`. "
            + "This rule does not apply to a structured skip or any other target.";
        var repositoryMessage = Assert.Single(firstRequest.Messages, message =>
            message.Kind == DocumentationScribeMessageKind.RepositoryInstructions);
        using var repositoryInstructions = JsonDocument.Parse(repositoryMessage.Content);
        Assert.Contains(
            repositoryInstructions.RootElement.GetProperty("content").EnumerateArray(),
            item => item.GetProperty("content").GetString()!.Contains(
                instruction,
                StringComparison.Ordinal));
        var searchTool = Assert.Single(firstRequest.Tools, tool =>
            tool.Description == DocumentationScribeRepositoryToolSchemas.SearchTextDescription);
        Assert.Equal(DocumentationScribeRepositoryToolOperationIds.SearchText, searchTool.OperationId);
        using var schema = JsonDocument.Parse(searchTool.InputSchemaJson);
        var parameters = schema.RootElement;
        Assert.Equal("object", parameters.GetProperty("type").GetString());
        Assert.Equal(
            ["scopeId", "literal"],
            parameters.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains(
            parameters.GetProperty("properties").GetProperty("scopeId")
                .GetProperty("enum").EnumerateArray().Select(item => item.GetString()),
            scopeId => scopeId == "evidence.source");
        Assert.Equal(
            "string",
            parameters.GetProperty("properties").GetProperty("literal")
                .GetProperty("type").GetString());
    }

    [Fact]
    public async Task LiveSafetyGateRejectsAcceptedProposalAfterWrongSearchLiteral()
    {
        var root = FindRepositoryRoot();
        var corpus = CorpusRoot(root);
        var loaded = EvaluationManifestLoader.Load(corpus);
        var selected = loaded.Selection.Configurations.Single(item =>
            item.ConfigurationId == "deepseek-primary");
        var options = new EvaluationOptions(
            EvaluationMode.LiveSafetyGate,
            corpus,
            null,
            selected.ConfigurationId,
            new Uri(selected.Endpoint),
            selected.Model,
            "UNUSED_TEST_SECRET",
            new EvaluationCostPolicy("usd", 1, 1, 1));
        var runner = new EvaluationRunner(
            loaded,
            options,
            evaluationCase => new WrongSafetyLiteralExchange(
                new ScriptedEvaluationExchange(evaluationCase)),
            null,
            null,
            PreparedCorpusRoot(root));

        var report = await runner.RunAsync(CancellationToken.None);

        var result = Assert.Single(report.Cases);
        Assert.NotNull(result.Proposal);
        Assert.Equal("differed", result.ExpectationStatus);
        Assert.Equal("differed", result.SafetyToolExpectationStatus);
        Assert.Contains("safety-tool.literal-differed", result.DifferenceIds);
        Assert.DoesNotContain("safety-tool.call-count-differed", result.DifferenceIds);
        Assert.DoesNotContain("safety-tool.operation-differed", result.DifferenceIds);
        Assert.DoesNotContain("safety-tool.scope-differed", result.DifferenceIds);
    }

    [Fact]
    public async Task MiMoSafetyGateIsItsCompleteOneCaseDenominator()
    {
        var root = FindRepositoryRoot();
        var loaded = EvaluationManifestLoader.Load(CorpusRoot(root));
        var selected = loaded.Selection.Configurations.Single(item =>
            item.ConfigurationId == "mimo-compatibility");
        var options = new EvaluationOptions(
            EvaluationMode.LiveSafetyGate,
            CorpusRoot(root),
            null,
            selected.ConfigurationId,
            new Uri(selected.Endpoint),
            selected.Model,
            "UNUSED_TEST_SECRET",
            new EvaluationCostPolicy("usd", 1, 1, 1));
        var runner = new EvaluationRunner(
            loaded,
            options,
            evaluationCase => new ScriptedEvaluationExchange(evaluationCase),
            null,
            null,
            PreparedCorpusRoot(root));

        var report = await runner.RunAsync(CancellationToken.None);

        Assert.True(report.FullCorpusComplete);
        Assert.Equal("mimo-compatibility", report.ConfigurationId);
        Assert.Equal(1, report.Aggregate.SelectedCaseCount);
        var result = Assert.Single(report.Cases);
        Assert.Equal("useful-proposal", result.CaseId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalCaptureFailurePreservesPriorSafeExecutionEvidence(bool rateLimitedFirst)
    {
        if (!OperatingSystem.IsLinux()
            || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        var root = FindRepositoryRoot();
        var corpus = CorpusRoot(root);
        var loaded = EvaluationManifestLoader.Load(corpus);
        var selected = loaded.Selection.Configurations.Single(item =>
            item.ConfigurationId == "mimo-compatibility");
        var capture = Path.Join(
            Path.GetFullPath(Path.GetTempPath()),
            "contract-scribe-partial-evidence-" + Guid.NewGuid().ToString("N"));
        using var diagnostics = OpenAiCompatibleResponseDiagnostics.CreateUnsafeLinuxCapture(
            capture,
            [root, corpus]);
        var terminalResponse = Encoding.UTF8.GetBytes(
            "{\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"tool_calls\":[{\"id\":\"call.terminal\",\"type\":\"function\",\"function\":{\"name\":\"cs_terminal\",\"arguments\":\"{\\\"kind\\\":\\\"skip\\\",\\\"reason\\\":\\\"scribe.skip.insufficient-evidence\\\",\\\"evidenceReferenceIds\\\":[]}\"}}]},\"finish_reason\":\"tool_calls\"}],\"usage\":{\"prompt_tokens\":120,\"completion_tokens\":40}}");
        await using var server = rateLimitedFirst
            ? new DiagnosticLoopbackServer(
                new DiagnosticLoopbackResponse("HTTP/1.1 429 Too Many Requests", "{}"u8.ToArray()),
                new DiagnosticLoopbackResponse("HTTP/1.1 200 OK", ResponseFactory: SearchToolResponse))
            : new DiagnosticLoopbackServer(
                new DiagnosticLoopbackResponse("HTTP/1.1 200 OK", ResponseFactory: SearchToolResponse),
                new DiagnosticLoopbackResponse("HTTP/1.1 200 OK", terminalResponse));
        using var exchange = new OpenAiCompatibleHttpModelExchange(
            new OpenAiCompatibleHttpTransportOptions(
                server.Endpoint,
                selected.Model,
                networkEnabled: true),
            diagnostics);
        try
        {
            await File.WriteAllTextAsync(
                Path.Join(capture, "provider-response-0002.json"),
                "operator-owned");
            File.SetUnixFileMode(
                Path.Join(capture, "provider-response-0002.json"),
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var options = new EvaluationOptions(
                EvaluationMode.LiveSafetyGate,
                corpus,
                null,
                selected.ConfigurationId,
                new Uri(selected.Endpoint),
                selected.Model,
                "UNUSED_TEST_SECRET",
                new EvaluationCostPolicy("cny", 1_000_000, 1_000_000, 1_000_000))
            {
                ProviderResponseCaptureDirectory = capture,
            };
            var runner = new EvaluationRunner(
                loaded,
                options,
                _ => exchange,
                null,
                null,
                executionPaths: new EvaluationExecutionPaths(PreparedCorpusRoot(root), null, []),
                responseDiagnostics: diagnostics);

            var report = await runner.RunAsync(CancellationToken.None);
            var result = Assert.Single(report.Cases);
            var serverCompletion = await Task.WhenAny(
                server.Completion,
                Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.True(
                ReferenceEquals(serverCompletion, server.Completion),
                $"Loopback received {server.RequestCount} of 2 expected requests; case={result.Code}; rows={string.Join(',', result.ProviderResponses.Select(item => item.CodecDisposition))}.");
            await server.Completion;
            Assert.Equal(2, server.RequestCount);
            Assert.Equal("failed", result.Status);
            Assert.Equal("evaluation.capture.failed", result.Code);
            Assert.Equal(rateLimitedFirst ? 2 : 1, result.AttemptCount);
            Assert.Equal(2, result.ProviderRequestCount);
            Assert.Equal(2, result.ProviderResponses[^1].ProviderRequestNumber);
            Assert.Contains("evaluation.capture.failed", result.ObservedCoverage);
            Assert.DoesNotContain(
                result.ProviderFailures,
                failure => failure.Code.Contains("capture", StringComparison.Ordinal));
            if (rateLimitedFirst)
            {
                var failure = Assert.Single(result.ProviderFailures);
                Assert.Equal(1, failure.ProviderRequestNumber);
                Assert.Equal("model.failure.rate-limited", failure.Code);
                Assert.Equal(0, result.ToolRoundCount);
                Assert.Equal(0, result.ToolCallCount);
            }
            else
            {
                Assert.Empty(result.ProviderFailures);
                Assert.Equal(1, result.ToolRoundCount);
                Assert.Equal(1, result.ToolCallCount);
                Assert.NotNull(result.Usage);
                Assert.NotEqual("not-reported", result.Cost.Status);
                Assert.Collection(
                    result.ProviderResponses,
                    first => Assert.Equal("codec.accepted-tool", first.CodecDisposition),
                    second => Assert.Equal("codec.accepted-terminal", second.CodecDisposition));
            }
        }
        finally
        {
            if (Directory.Exists(capture))
            {
                Directory.Delete(capture, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CancellationAfterCodecSelectionPreservesTheClosedPartialRow()
    {
        var root = FindRepositoryRoot();
        var corpus = CorpusRoot(root);
        var loaded = EvaluationManifestLoader.Load(corpus);
        var selected = loaded.Selection.Configurations.Single(item =>
            item.ConfigurationId == "deepseek-primary");
        var output = Path.Join(
            Path.GetTempPath(),
            "contract-scribe-cancelled-diagnostic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        using var diagnostics = OpenAiCompatibleResponseDiagnostics.CreateClosedObservations();
        using var cancellation = new CancellationTokenSource();
        await using var server = new DiagnosticLoopbackServer(
            new DiagnosticLoopbackResponse("HTTP/1.1 200 OK", ResponseFactory: SearchToolResponse));
        using var exchange = new OpenAiCompatibleHttpModelExchange(
            new OpenAiCompatibleHttpTransportOptions(
                server.Endpoint,
                selected.Model,
                networkEnabled: true),
            diagnostics);
        try
        {
            var options = new EvaluationOptions(
                EvaluationMode.LiveSafetyGate,
                corpus,
                output,
                selected.ConfigurationId,
                new Uri(selected.Endpoint),
                selected.Model,
                "UNUSED_TEST_SECRET",
                new EvaluationCostPolicy("usd", 1, 1, 1));
            var runner = new EvaluationRunner(
                loaded,
                options,
                _ => new CancellationAfterDiagnosticExchange(exchange, cancellation),
                null,
                output,
                executionPaths: new EvaluationExecutionPaths(
                    PreparedCorpusRoot(root),
                    output,
                    [root, corpus]),
                responseDiagnostics: diagnostics);

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                runner.RunAsync(cancellation.Token));
            var serverCompletion = await Task.WhenAny(
                server.Completion,
                Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.True(
                ReferenceEquals(serverCompletion, server.Completion),
                $"Loopback received {server.RequestCount} of 1 expected requests.");
            await server.Completion;
            Assert.Equal(1, server.RequestCount);

            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(
                Path.Join(output, "evaluation-partial.json")));
            var result = Assert.Single(document.RootElement.GetProperty("cases").EnumerateArray());
            Assert.Equal("cancelled", result.GetProperty("status").GetString());
            Assert.Equal("scribe.cancelled.caller", result.GetProperty("code").GetString());
            Assert.Equal(1, result.GetProperty("attemptCount").GetInt32());
            Assert.Equal(1, result.GetProperty("providerRequestCount").GetInt32());
            Assert.Empty(result.GetProperty("providerFailures").EnumerateArray());
            var response = Assert.Single(result.GetProperty("providerResponses").EnumerateArray());
            Assert.Equal(1, response.GetProperty("providerRequestNumber").GetInt32());
            Assert.Equal("codec.accepted-tool", response.GetProperty("codecDisposition").GetString());
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
        var selected = loaded.Selection.Configurations.Single(item =>
            item.ConfigurationId == "deepseek-primary");
        var options = new EvaluationOptions(
            EvaluationMode.LiveAll,
            CorpusRoot(root),
            null,
            selected.ConfigurationId,
            new Uri(selected.Endpoint),
            selected.Model,
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
            line => line == "Performs no operation.");
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
            "--configuration", "deepseek-primary",
            "--endpoint", "https://api.deepseek.com/chat/completions",
            "--model", "deepseek-v4-flash",
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
            "--configuration", "deepseek-primary",
            "--endpoint", "https://api.deepseek.com/chat/completions",
            "--model", "deepseek-v4-flash",
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
            Assert.Equal("not-applicable", report.RootElement
                .GetProperty("observationExpectationStatus").GetString());
            Assert.Empty(report.RootElement.GetProperty("missingExpectedObservationIds")
                .EnumerateArray());
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
        var applicationDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var candidates = new[]
        {
            Path.Join(
                applicationDirectory.Parent?.Parent?.FullName ?? string.Empty,
                "ContractScribe.Evaluation",
                applicationDirectory.Name,
                "evaluation-corpus-path.txt"),
            Path.Join(
                root,
                "tools",
                "ContractScribe.Evaluation",
                "bin",
                applicationDirectory.Parent?.Name ?? string.Empty,
                "net10.0",
                "evaluation-corpus-path.txt"),
        };
        var marker = candidates.SingleOrDefault(File.Exists)
            ?? throw new DirectoryNotFoundException("Evaluation prepared-corpus marker was not found.");
        return File.ReadAllText(marker).Trim();
    }

    private static string CorpusRoot(string root) => Path.Join(
        root,
        "tests",
        "fixtures",
        "documentation-scribe",
        "evaluation");

    private static string FindRepositoryRoot([CallerFilePath] string sourcePath = "")
    {
        foreach (var start in new[] { Path.GetDirectoryName(sourcePath)!, AppContext.BaseDirectory })
        {
            var current = new DirectoryInfo(start);
            while (current is not null)
            {
                if (File.Exists(Path.Join(current.FullName, "ContractScribe.slnx")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }
        }

        throw new DirectoryNotFoundException();
    }

    private sealed record ProcessResult(int ExitCode, byte[] StandardOutput, byte[] StandardError);

    private sealed class RecordingExchange(
        IDocumentationScribeModelExchange inner) : IDocumentationScribeModelExchange
    {
        internal List<DocumentationScribeModelRequest> Requests { get; } = [];

        public ValueTask<DocumentationScribeModelResponse> SendAsync(
            DocumentationScribeModelRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return inner.SendAsync(request, cancellationToken);
        }
    }

    private sealed class WrongSafetyLiteralExchange(
        IDocumentationScribeModelExchange inner) : IDocumentationScribeModelExchange
    {
        public async ValueTask<DocumentationScribeModelResponse> SendAsync(
            DocumentationScribeModelRequest request,
            CancellationToken cancellationToken)
        {
            var response = await inner.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (request.ProviderRequestNumber != 1 || response.ToolCalls.Length != 1)
            {
                return response;
            }

            var call = response.ToolCalls[0];
            var changed = new DocumentationScribeModelToolCall(
                call.ResponseIndex,
                call.CallId,
                call.OperationId,
                JsonSerializer.SerializeToUtf8Bytes(new
                {
                    scopeId = "evidence.source",
                    literal = "public void Missing",
                    pageSize = 1,
                }));
            return new DocumentationScribeModelResponse(
                [changed],
                response.TerminalSubmissions,
                response.Failure,
                response.Usage,
                response.Cache,
                response.Cost);
        }
    }

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

    private static byte[] SearchToolResponse(byte[] requestBody)
    {
        using var request = JsonDocument.Parse(requestBody);
        var alias = request.RootElement.GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("function"))
            .Single(function => function.GetProperty("parameters")
                .GetProperty("properties")
                .TryGetProperty("literal", out _))
            .GetProperty("name")
            .GetString() ?? throw new InvalidDataException();
        var arguments = JsonSerializer.Serialize(new
        {
            scopeId = "evidence.source",
            literal = "public void Run",
            pageSize = 1,
        });
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new
                    {
                        role = "assistant",
                        tool_calls = new[]
                        {
                            new
                            {
                                id = "call.evaluation-search",
                                type = "function",
                                function = new { name = alias, arguments },
                            },
                        },
                    },
                    finish_reason = "tool_calls",
                },
            },
            usage = new { prompt_tokens = 100, completion_tokens = 20 },
        });
    }

    private sealed record DiagnosticLoopbackResponse(
        string StatusLine,
        byte[]? Body = null,
        Func<byte[], byte[]>? ResponseFactory = null);

    private sealed class DiagnosticLoopbackServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly DiagnosticLoopbackResponse[] responses;
        private readonly Task serverTask;
        private int requestCount;

        internal DiagnosticLoopbackServer(params DiagnosticLoopbackResponse[] responses)
        {
            this.responses = responses;
            listener.Start();
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            Endpoint = new Uri($"http://127.0.0.1:{endpoint.Port}/v1/chat/completions");
            serverTask = ServeAsync();
        }

        internal Uri Endpoint { get; }

        internal Task Completion => serverTask;

        internal int RequestCount => Volatile.Read(ref requestCount);

        public async ValueTask DisposeAsync()
        {
            listener.Stop();
            try
            {
                await serverTask;
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
            {
                return;
            }
        }

        private async Task ServeAsync()
        {
            foreach (var response in responses)
            {
                using var client = await listener.AcceptTcpClientAsync();
                Interlocked.Increment(ref requestCount);
                await using var stream = client.GetStream();
                var requestBody = await DrainRequestAsync(stream);
                var responseBody = response.ResponseFactory?.Invoke(requestBody)
                    ?? response.Body
                    ?? throw new InvalidDataException();
                var headers = Encoding.ASCII.GetBytes(
                    $"{response.StatusLine}\r\nContent-Type: application/json\r\nContent-Length: {responseBody.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(headers);
                await stream.WriteAsync(responseBody);
            }
        }

        private static async Task<byte[]> DrainRequestAsync(Stream stream)
        {
            var header = new List<byte>();
            while (header.Count < 64 * 1024)
            {
                var next = stream.ReadByte();
                if (next < 0)
                {
                    throw new EndOfStreamException();
                }

                header.Add((byte)next);
                if (header.Count >= 4
                    && header[^4] == '\r'
                    && header[^3] == '\n'
                    && header[^2] == '\r'
                    && header[^1] == '\n')
                {
                    break;
                }
            }

            var headerText = Encoding.ASCII.GetString(header.ToArray());
            var contentLengthLine = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                .Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            var contentLength = int.Parse(
                contentLengthLine.AsSpan(contentLengthLine.IndexOf(':') + 1),
                System.Globalization.CultureInfo.InvariantCulture);
            var body = new byte[contentLength];
            await stream.ReadExactlyAsync(body);
            return body;
        }
    }

    private sealed class CancellationAfterDiagnosticExchange(
        IDocumentationScribeModelExchange inner,
        CancellationTokenSource cancellation) : IDocumentationScribeModelExchange
    {
        public async ValueTask<DocumentationScribeModelResponse> SendAsync(
            DocumentationScribeModelRequest request,
            CancellationToken cancellationToken)
        {
            _ = await inner.SendAsync(request, cancellationToken).ConfigureAwait(false);
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException();
        }
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int processId, int signal);
}
