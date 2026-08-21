using System.Collections.Immutable;
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
        var unpriced = new[]
        {
            "--live",
            "--corpus", "corpus",
            "--configuration", "deepseek-primary",
            "--endpoint", "https://api.deepseek.com/chat/completions",
            "--model", "deepseek-v4-flash",
            "--secret-env", "EVALUATION_TEST_SECRET",
            "--output", Path.Join(Path.GetTempPath(), "evaluation-output"),
        };
        Assert.False(EvaluationOptions.TryParse(
            [.. unpriced, "--safety-gate"],
            out _,
            out _));
        string[] common =
        [
            .. unpriced,
            "--currency", "usd",
            "--cached-input-rate", "1",
            "--uncached-input-rate", "1",
            "--output-rate", "1",
        ];
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
    public async Task ProviderObserverPreservesEveryFailureClassificationAcrossLaterSuccess()
    {
        var codes = Enum.GetValues<DocumentationScribeModelFailureCode>();
        var responses = codes
            .Select(code => new DocumentationScribeModelResponse(
                [],
                [],
                new DocumentationScribeModelFailure(code)))
            .Append(new DocumentationScribeModelResponse([], []));
        var observer = new CostObservingExchange(
            new QueuedExchange(responses),
            EvaluationCostPolicy.Unpriced);

        for (var index = 0; index <= codes.Length; index++)
        {
            var response = await observer.SendAsync(Request(index + 1), CancellationToken.None);
            Assert.Equal(index < codes.Length ? codes[index] : null, response.Failure?.Code);
        }

        Assert.Equal(codes.Length + 1, observer.Observations.Count);
        Assert.Equal(
            [
                "model.failure.transient-unavailable",
                "model.failure.rate-limited",
                "model.failure.permanent-unavailable",
                "model.failure.authentication",
                "model.failure.unsupported",
                "model.failure.malformed-response",
            ],
            observer.Observations
                .Where(observation => observation.FailureCode is not null)
                .Select(observation => EvaluationProviderFailureReport.CodeId(
                    observation.FailureCode!.Value)));
        Assert.Null(observer.Observations[^1].FailureCode);
    }

    [Fact]
    public async Task ProviderObserverPreservesBoundedFailureProvenanceAcrossCostDecoration()
    {
        var failure = new DocumentationScribeModelFailure(
            DocumentationScribeModelFailureCode.PermanentUnavailable,
            origin: DocumentationScribeModelFailureOrigin.HttpStatus,
            httpStatusCode: 422);
        var observer = new CostObservingExchange(
            new QueuedExchange([new DocumentationScribeModelResponse([], [], failure)]),
            EvaluationCostPolicy.Unpriced);

        var response = await observer.SendAsync(Request(1), CancellationToken.None);

        Assert.Same(failure, response.Failure);
        var observation = Assert.Single(observer.Observations);
        Assert.False(observation.ResponseAccepted);
        Assert.Equal(DocumentationScribeModelFailureCode.PermanentUnavailable, observation.FailureCode);
        Assert.Equal(DocumentationScribeModelFailureOrigin.HttpStatus, observation.FailureOrigin);
        Assert.Equal(422, observation.HttpStatusCode);
        Assert.False(observation.OrdinaryToolCallObserved);
        Assert.False(observation.TerminalSubmissionObserved);
        Assert.False(observation.UsageSupplied);
        Assert.False(observation.CacheSupplied);
        Assert.False(observation.ToolResultContinuationRequired);
    }

    [Fact]
    public void LiveObservationMatcherEvaluatesPredicatesAndProtocolDifferencesSeparately()
    {
        var expected = new[]
        {
            "cache-fields-when-supplied",
            "continuation.history-replayed",
            "continuation.observed",
            "request.accepted-or-bounded-provider-failure",
            "tool-call-or-terminal",
            "tool-result-continuation-when-requested",
            "usage-fields-when-supplied",
            "validated-proposal-or-structured-skip-or-bounded-failure",
        };
        var complete = new EvaluationLiveObservationFacts(
            RuntimeExecutionApplicable: true,
            ContinuationObserved: true,
            ContinuationHistoryReplayed: true,
            ProviderResponseBounded: true,
            ToolCallOrTerminalObserved: true,
            ToolResultContinuationsSatisfied: true,
            UsageSupplied: true,
            UsageReported: true,
            CacheSupplied: true,
            CacheReported: true,
            BoundedTerminal: true,
            MissingRequiredContinuation: false,
            MalformedResponse: false,
            ToolProtocolRejected: false,
            TerminalValidationRejected: false,
            RequestPreparationRejected: false);

        var matched = EvaluationLiveObservationMatcher.Match(expected, [complete]);
        Assert.Equal("matched", matched.Status);
        Assert.Empty(matched.MissingExpectedObservationIds);

        var directTerminal = complete with
        {
            ContinuationObserved = false,
            ContinuationHistoryReplayed = false,
            ToolResultContinuationsSatisfied = true,
            UsageSupplied = false,
            UsageReported = false,
            CacheSupplied = false,
            CacheReported = false,
        };
        var direct = EvaluationLiveObservationMatcher.Match(expected, [directTerminal]);
        Assert.Equal("differed", direct.Status);
        Assert.Equal(
            ["continuation.history-replayed", "continuation.observed"],
            direct.MissingExpectedObservationIds);

        var rejectedFacts = complete with
        {
            ContinuationHistoryReplayed = false,
            ToolResultContinuationsSatisfied = false,
            UsageSupplied = true,
            UsageReported = false,
            CacheSupplied = true,
            CacheReported = false,
            MissingRequiredContinuation = true,
            MalformedResponse = true,
            ToolProtocolRejected = true,
            TerminalValidationRejected = true,
            RequestPreparationRejected = true,
        };
        var rejected = EvaluationLiveObservationMatcher.Match(expected, [rejectedFacts]);
        Assert.Equal(
            [
                "cache-fields-when-supplied",
                "continuation.history-replayed",
                "tool-result-continuation-when-requested",
                "usage-fields-when-supplied",
            ],
            rejected.MissingExpectedObservationIds);
        Assert.Equal(
            [
                "continuation.missing-required",
                "request.preparation-rejected",
                "response.malformed",
                "terminal-validation.rejected",
                "tool-protocol.rejected",
            ],
            EvaluationLiveObservationMatcher.UnexpectedProtocolObservationIds(rejectedFacts));

        var preflight = complete with
        {
            RuntimeExecutionApplicable = false,
            ContinuationObserved = false,
            ContinuationHistoryReplayed = false,
            ProviderResponseBounded = false,
            ToolCallOrTerminalObserved = false,
            UsageSupplied = false,
            UsageReported = false,
            CacheSupplied = false,
            CacheReported = false,
            BoundedTerminal = false,
        };
        var patchTerminal = directTerminal;
        var fullDeepSeek = EvaluationLiveObservationMatcher.Match(
            expected,
            [preflight, complete, patchTerminal]);
        Assert.Equal("matched", fullDeepSeek.Status);
        Assert.Empty(fullDeepSeek.MissingExpectedObservationIds);

        Assert.False(EvaluationLiveObservationMatcher.HasReportedCache(null));
        Assert.True(EvaluationLiveObservationMatcher.HasReportedCache(new EvaluationUsageReport(
            null, null, 1, null, null, null)));
        Assert.True(EvaluationLiveObservationMatcher.HasReportedCache(new EvaluationUsageReport(
            null, null, null, 1, null, null)));
        Assert.True(EvaluationLiveObservationMatcher.HasReportedCache(new EvaluationUsageReport(
            null, null, null, null, null, "cache.hit")));

        var preparationRejected = preflight with
        {
            RuntimeExecutionApplicable = true,
            RequestPreparationRejected = true,
        };
        var preparationResult = EvaluationLiveObservationMatcher.Match(
            ["request.accepted-or-bounded-provider-failure"],
            [preparationRejected]);
        Assert.Equal("differed", preparationResult.Status);
        Assert.Equal(
            ["request.preparation-rejected"],
            EvaluationLiveObservationMatcher.UnexpectedProtocolObservationIds(preparationRejected));
    }

    [Fact]
    public void CorpusManifestAndExactSelectionAreFrozen()
    {
        var loaded = EvaluationManifestLoader.Load(CorpusRoot());
        Assert.Equal("useful-proposal", loaded.Manifest.SafetyGateCaseId);
        Assert.Equal("deepseek-mimo.chat-completions-thinking.v1", loaded.Selection.SelectionId);
        Assert.Equal(2, loaded.Selection.Configurations.Length);
        var deepSeek = loaded.Selection.Configurations[0];
        Assert.Equal("deepseek-primary", deepSeek.ConfigurationId);
        Assert.Equal("https://api.deepseek.com/chat/completions", deepSeek.Endpoint);
        Assert.Equal("deepseek-v4-flash", deepSeek.Model);
        Assert.Equal("2026-08-21", deepSeek.EvidenceDate);
        Assert.Equal("max_tokens", deepSeek.RequestProfile.OutputTokenField);
        Assert.Equal(
        [
            "cache-fields-when-supplied",
            "continuation.history-replayed",
            "continuation.observed",
            "request.accepted-or-bounded-provider-failure",
            "tool-call-or-terminal",
            "tool-result-continuation-when-requested",
            "usage-fields-when-supplied",
            "validated-proposal-or-structured-skip-or-bounded-failure",
        ], deepSeek.ExpectedObservations);
        Assert.Equal(11, loaded.Manifest.Scenarios.Length);
        Assert.Equal(
            ["conflicting-evidence", "patch-rejection", "useful-proposal"],
            deepSeek.LiveScenarioIds);
        var miMo = loaded.Selection.Configurations[1];
        Assert.Equal("mimo-compatibility", miMo.ConfigurationId);
        Assert.Equal("https://api.xiaomimimo.com/v1/chat/completions", miMo.Endpoint);
        Assert.Equal("mimo-v2.5", miMo.Model);
        Assert.Equal("2026-08-21", miMo.EvidenceDate);
        Assert.Equal(["useful-proposal"], miMo.LiveScenarioIds);
        Assert.Equal("max_completion_tokens", miMo.RequestProfile.OutputTokenField);
        Assert.Equal(
        [
            "continuation.history-replayed",
            "continuation.observed",
            "request.accepted-or-bounded-provider-failure",
            "tool-call-or-terminal",
            "tool-result-continuation-when-requested",
            "usage-fields-when-supplied",
            "validated-proposal-or-structured-skip-or-bounded-failure",
        ], miMo.ExpectedObservations);
        Assert.Equal(64, loaded.CorpusIdentity.Length);
        Assert.Equal(64, loaded.SelectionIdentity.Length);
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
        var temporary = Path.Join(
            Path.GetTempPath(),
            "contract-scribe-evaluation-test",
            Guid.NewGuid().ToString("N"));
        try
        {
            var checkout = Path.Join(temporary, "checkout");
            var prepared = Path.Join(temporary, "prepared");
            Directory.CreateDirectory(checkout);
            Directory.CreateDirectory(Path.Join(prepared, "repository", "bin"));
            Directory.CreateDirectory(Path.Join(prepared, "repository", "obj"));
            var forbidden = new[] { checkout, prepared };
            Assert.False(EvaluationOutput.TryResolveDirectory(checkout, forbidden, out _));
            Assert.False(EvaluationOutput.TryResolveDirectory(
                Path.Join(checkout, "output"), forbidden, out _));
            Assert.False(EvaluationOutput.TryResolveDirectory(temporary, forbidden, out _));
            Assert.False(EvaluationOutput.TryResolveDirectory(
                Path.Join(prepared, "repository", "bin", "output"), forbidden, out _));
            Assert.False(EvaluationOutput.TryResolveDirectory(
                Path.Join(prepared, "repository", "obj", "output"), forbidden, out _));

            var allowed = Path.Join(temporary, "unrelated-output");
            Assert.True(EvaluationOutput.TryResolveDirectory(allowed, forbidden, out var resolved));
            Assert.Equal(Path.GetFullPath(allowed), resolved);
            File.WriteAllText(Path.Join(allowed, "evaluation-report.json"), "stale");
            Assert.False(EvaluationOutput.TryResolveDirectory(allowed, forbidden, out _));
            Assert.False(EvaluationOutput.TryResolveDirectory(CorpusRoot(), forbidden, out _));
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
        _ = EvaluationReportWriter.Serialize(MinimalReport("See https://example.com/reference."), null);
        _ = EvaluationReportWriter.Serialize(MinimalReport("Use the input/output projection."), null);
        Assert.Throws<InvalidDataException>(() => EvaluationReportWriter.Serialize(
            MinimalReport("Read /home/alice/private/contract.cs before use."),
            null));
        Assert.Throws<InvalidDataException>(() => EvaluationReportWriter.Serialize(
            MinimalReport("Read `/home/alice/private/contract.cs` before use."),
            null));
        Assert.Throws<InvalidDataException>(() => EvaluationReportWriter.Serialize(
            MinimalReport("> /home/alice/private/contract.cs"),
            null));
        Assert.Throws<InvalidDataException>(() => EvaluationReportWriter.Serialize(
            MinimalReport(">/home/alice/private/contract.cs"),
            null));
        Assert.Throws<InvalidDataException>(() => EvaluationReportWriter.Serialize(
            MinimalReport("- /home/alice/private/contract.cs"),
            null));
        Assert.Throws<InvalidDataException>(() => EvaluationReportWriter.Serialize(
            MinimalReport("Read //server/share/Contract.cs before use."),
            null));
        Assert.Throws<InvalidDataException>(() => EvaluationReportWriter.Serialize(
            MinimalReport("Read C:\\Users\\Alice\\source\\Contract.cs before use."),
            null));
        Assert.Throws<InvalidDataException>(() => EvaluationReportWriter.Serialize(
            MinimalReport("Read `C:/Users/Alice/source/Contract.cs` before use."),
            null));
        Assert.Throws<InvalidDataException>(() => EvaluationReportWriter.Serialize(
            MinimalReport("Read \\\\server\\share\\Contract.cs before use."),
            null));
        Assert.Throws<InvalidDataException>(() => EvaluationReportWriter.Serialize(
            MinimalReport("Read \\\\?\\C:\\private\\Contract.cs before use."),
            null));
        Assert.Throws<InvalidDataException>(() => EvaluationReportWriter.Serialize(
            MinimalReport("Read file:///home/alice/private/contract.cs before use."),
            null));
        Assert.Throws<InvalidDataException>(() => EvaluationReportWriter.Serialize(
            MinimalReport("reasoning_content"),
            null));
        Assert.Throws<InvalidDataException>(() => EvaluationReportWriter.Serialize(
            MinimalReport("assistantContinuation"),
            null));
    }

    [Fact]
    public void ReportProjectsOnlyClosedProviderAndRuntimeDiagnosticFacts()
    {
        var report = MinimalReport("safe bounded line");
        report = report with
        {
            ObservationExpectationStatus = "differed",
            MissingExpectedObservationIds = ["continuation.history-replayed"],
        };
        var reportCase = report.Cases[0] with
        {
            UnexpectedProtocolObservationIds = ["terminal-validation.rejected"],
            ProviderFailures =
            [
                new EvaluationProviderFailureReport(
                    1,
                    "model.failure.permanent-unavailable",
                    "model.failure-origin.http-status",
                    400),
            ],
            RuntimeDiagnostics =
            [
                new EvaluationRuntimeDiagnosticReport(
                    "scribe.diagnostic.result-rejected",
                    "result",
                    null,
                    "scribe.result.invalid-shape"),
                new EvaluationRuntimeDiagnosticReport(
                    "scribe.diagnostic.tool-failure",
                    "tool",
                    "repository.read-excerpt",
                    null),
            ],
        };

        var bytes = EvaluationReportWriter.Serialize(report with { Cases = [reportCase] }, null);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("model.failure-origin.http-status", text, StringComparison.Ordinal);
        using (var document = JsonDocument.Parse(bytes))
        {
            Assert.Equal(400, document.RootElement.GetProperty("cases")[0]
                .GetProperty("providerFailures")[0].GetProperty("httpStatusCode").GetInt32());
        }
        Assert.Contains("scribe.result.invalid-shape", text, StringComparison.Ordinal);
        Assert.Contains("repository.read-excerpt", text, StringComparison.Ordinal);
        Assert.DoesNotContain("pointer", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rawResponse", text, StringComparison.OrdinalIgnoreCase);

        var hostile = reportCase with
        {
            RuntimeDiagnostics =
            [
                new EvaluationRuntimeDiagnosticReport(
                    "scribe.diagnostic.tool-failure",
                    "tool",
                    "C:\\private\\provider-error-body",
                    null),
            ],
        };
        Assert.Throws<InvalidDataException>(() => EvaluationReportWriter.Serialize(
            report with { Cases = [hostile] },
            null));
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
            selectedCaseCount: 11,
            complete: false,
            elapsedMilliseconds: null);

        Assert.Equal("partial", report.Status);
        Assert.False(report.FullCorpusComplete);
        Assert.Equal(2, report.Aggregate.CompletedCaseCount);
        Assert.Equal(0, report.Aggregate.ExpectedMatchCount);
        Assert.Equal(2, report.Aggregate.ExpectedDifferedCount);
        Assert.Equal(2, report.Aggregate.FailedCaseCount);
        Assert.Collection(
            report.Cases,
            item => Assert.Equal("cancelled", item.Status),
            item => Assert.Equal("timeout", item.Status));
        Assert.Equal(1, EvaluationApplication.ResultExitCode(options!, report));

        var liveOptions = new EvaluationOptions(
            EvaluationMode.LiveAll,
            CorpusRoot(),
            Path.GetTempPath(),
            "deepseek-primary",
            new Uri("https://api.deepseek.com/chat/completions"),
            "deepseek-v4-flash",
            "CONTRACTSCRIBE_TEST_KEY",
            EvaluationCostPolicy.Unpriced);
        var interruptedLive = EvaluationReport.Create(
            loaded,
            liveOptions,
            [cancelled],
            selectedCaseCount: 3,
            complete: false,
            elapsedMilliseconds: 1);
        Assert.Equal("partial", interruptedLive.Status);
        Assert.Equal("not-evaluable", interruptedLive.ObservationExpectationStatus);
        Assert.Empty(interruptedLive.MissingExpectedObservationIds);
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
        new string('c', 64),
        null,
        null,
        "not-applicable",
        [],
        new EvaluationLatencyReport("not-measured", null),
        [
            new EvaluationCaseReport(
                "case",
                "patch-accepted",
                "code",
                "matched",
                [],
                [],
                1,
                1,
                0,
                0,
                [],
                [],
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
                ["coverage"],
                ["patch-accepted"]),
        ],
        new EvaluationAggregateReport(
            1,
            1,
            1,
            0,
            0,
            0,
            1,
            0,
            new EvaluationCostReport("not-reported", null, null)));

    private static EvaluationCaseReport MinimalCase(string caseId, string status, string code) => new(
        caseId,
        status,
        code,
        "differed",
        ["case.execution-differed"],
        [],
        0,
        0,
        0,
        0,
        [],
        [],
        null,
        new EvaluationCostReport("not-reported", null, null),
        null,
        "passed",
        ["interruption"],
        [code, status]);

    private static string CorpusRoot() => Path.Join(
        FindRepositoryRoot(),
        "tests",
        "fixtures",
        "documentation-scribe",
        "evaluation");

    private static DocumentationScribeModelRequest Request(int providerRequestNumber) => new(
        1,
        providerRequestNumber,
        ImmutableArray<DocumentationScribeModelMessage>.Empty,
        ImmutableArray<DocumentationScribeModelToolDefinition>.Empty,
        new DocumentationScribeTerminalDefinition("submit", "{}"),
        ImmutableArray<DocumentationScribeCompletedToolExchange>.Empty,
        new DocumentationScribeModelOutputLimits(1, 1, 1, 1, 1),
        ImmutableArray<byte>.Empty);

    private sealed class QueuedExchange : IDocumentationScribeModelExchange
    {
        private readonly Queue<DocumentationScribeModelResponse> responses;

        internal QueuedExchange(IEnumerable<DocumentationScribeModelResponse> responses) =>
            this.responses = new Queue<DocumentationScribeModelResponse>(responses);

        public ValueTask<DocumentationScribeModelResponse> SendAsync(
            DocumentationScribeModelRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(responses.Dequeue());
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

        throw new DirectoryNotFoundException();
    }
}
