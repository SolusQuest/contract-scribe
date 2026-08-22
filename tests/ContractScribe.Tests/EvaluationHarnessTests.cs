using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ContractScribe.Agent.Providers;
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
    public void PrivateMimoDiagnosticsRequireTheExactSafetyGateAndComparatorContract()
    {
        var capture = Path.Join(Path.GetTempPath(), "contract-scribe-capture-" + Guid.NewGuid().ToString("N"));
        var common = MimoLiveArguments(capture);

        Assert.True(EvaluationOptions.TryParse(common, out var baseline, out _));
        Assert.True(baseline!.IsPrivateResponseDiagnostic);
        Assert.Equal("mimo-v2.5", baseline.EffectiveModel);
        Assert.Null(baseline.DiagnosticModel);

        Assert.True(EvaluationOptions.TryParse(
            [.. common, "--diagnostic-model", "mimo-v2.5-pro"],
            out var comparator,
            out _));
        Assert.Equal("mimo-v2.5-pro", comparator!.EffectiveModel);
        Assert.Equal("mimo-v2.5-pro", comparator.DiagnosticModel);

        Assert.False(EvaluationOptions.TryParse(
            [.. common.Where(value => value != "--safety-gate"), "--all"],
            out _,
            out _));
        Assert.False(EvaluationOptions.TryParse(
            [.. common.Take(common.Length - 2), "--unsafe-capture-provider-response", "relative"],
            out _,
            out _));
        Assert.False(EvaluationOptions.TryParse(
            [.. common.Take(common.Length - 2), "--diagnostic-model", "mimo-v2.5-pro"],
            out _,
            out _));
        Assert.False(EvaluationOptions.TryParse(
            [.. common, "--diagnostic-model", "mimo-v2.5"],
            out _,
            out _));
        Assert.False(EvaluationOptions.TryParse(
            ["--offline", "--corpus", CorpusRoot(), "--unsafe-capture-provider-response", capture],
            out _,
            out _));
    }

    [Fact]
    public async Task UnsupportedPrivateCapturePlatformFailsBeforeCredentialAcquisition()
    {
        if (OperatingSystem.IsLinux()
            && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                == System.Runtime.InteropServices.Architecture.X64)
        {
            return;
        }

        var secretName = "CONTRACTSCRIBE_DIAGNOSTIC_PREFLIGHT_" + Guid.NewGuid().ToString("N");
        const string secret = "preflight-secret-must-remain";
        var capture = Path.Join(Path.GetTempPath(), "contract-scribe-capture-" + Guid.NewGuid().ToString("N"));
        var output = Path.Join(Path.GetTempPath(), "contract-scribe-output-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable(secretName, secret);
        try
        {
            var arguments = MimoLiveArguments(capture, output, secretName);
            using var standardOutput = new StringWriter();
            using var standardError = new StringWriter();

            var exitCode = await EvaluationApplication.RunAsync(
                arguments,
                standardOutput,
                standardError);

            Assert.Equal(2, exitCode);
            Assert.Equal(secret, Environment.GetEnvironmentVariable(secretName));
            Assert.Contains("evaluation.capture.invalid", standardError.ToString(), StringComparison.Ordinal);
            Assert.False(Directory.Exists(capture));
            Assert.False(Directory.Exists(output));
        }
        finally
        {
            Environment.SetEnvironmentVariable(secretName, null);
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
        }
    }

    [Fact]
    public async Task InvalidLinuxCaptureEntriesFailBeforeCredentialAcquisition()
    {
        if (!OperatingSystem.IsLinux()
            || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        var suffix = Guid.NewGuid().ToString("N");
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        var dangling = Path.Join(temporaryRoot, "contract-scribe-dangling-" + suffix);
        var fifo = Path.Join(temporaryRoot, "contract-scribe-fifo-" + suffix);
        var applicationMarker = Path.Join(AppContext.BaseDirectory, "evaluation-corpus-path.txt");
        var markerCreated = !File.Exists(applicationMarker);
        if (markerCreated)
        {
            File.WriteAllText(applicationMarker, PreparedCorpusRoot());
        }

        File.CreateSymbolicLink(
            dangling,
            Path.Join(temporaryRoot, "contract-scribe-missing-" + suffix));
        Assert.Equal(0, CreateNamedPipe(fifo, 0x180));
        try
        {
            foreach (var capture in new[] { dangling, fifo })
            {
                var output = Path.Join(
                    temporaryRoot,
                    "contract-scribe-output-" + Guid.NewGuid().ToString("N"));
                var secretName = "CONTRACTSCRIBE_INVALID_CAPTURE_" + Guid.NewGuid().ToString("N");
                const string secret = "invalid-capture-secret-must-remain";
                Environment.SetEnvironmentVariable(secretName, secret);
                try
                {
                    using var standardOutput = new StringWriter();
                    using var standardError = new StringWriter();
                    var exitCode = await EvaluationApplication.RunAsync(
                        MimoLiveArguments(capture, output, secretName),
                        standardOutput,
                        standardError);

                    Assert.Equal(1, exitCode);
                    Assert.Equal(secret, Environment.GetEnvironmentVariable(secretName));
                    Assert.Equal(
                        "evaluation.capture.invalid" + Environment.NewLine,
                        standardError.ToString());
                    Assert.False(Directory.Exists(output));
                }
                finally
                {
                    Environment.SetEnvironmentVariable(secretName, null);
                    if (Directory.Exists(output))
                    {
                        Directory.Delete(output, recursive: true);
                    }
                }
            }
        }
        finally
        {
            File.Delete(dangling);
            File.Delete(fifo);
            if (markerCreated)
            {
                File.Delete(applicationMarker);
            }
        }
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
    public void LiveToolExpectationMatcherRejectsCountOperationScopeAndLiteralDifferences()
    {
        var expected = new EvaluationLiveToolExpectation
        {
            CallCount = 1,
            OperationId = "repository.search-text",
            ScopeId = "evidence.source",
            Literal = "public void Run",
        };
        var correct = new EvaluationSafetyToolCallObservation(true, true, true);

        var matched = EvaluationLiveToolExpectationMatcher.Match(expected, true, true, [correct]);
        Assert.Equal("matched", matched.Status);
        Assert.Empty(matched.DifferenceIds);

        var missing = EvaluationLiveToolExpectationMatcher.Match(expected, true, true, []);
        Assert.Equal("differed", missing.Status);
        Assert.Equal(["safety-tool.call-count-differed"], missing.DifferenceIds);

        var extra = EvaluationLiveToolExpectationMatcher.Match(expected, true, true, [correct, correct]);
        Assert.Equal(["safety-tool.call-count-differed"], extra.DifferenceIds);

        var wrongOperation = EvaluationLiveToolExpectationMatcher.Match(
            expected,
            true,
            true,
            [new EvaluationSafetyToolCallObservation(false, false, false)]);
        Assert.Equal(["safety-tool.operation-differed"], wrongOperation.DifferenceIds);

        var wrongScopeAndLiteral = EvaluationLiveToolExpectationMatcher.Match(
            expected,
            true,
            true,
            [new EvaluationSafetyToolCallObservation(true, false, false)]);
        Assert.Equal(
            ["safety-tool.literal-differed", "safety-tool.scope-differed"],
            wrongScopeAndLiteral.DifferenceIds);

        Assert.Equal(
            "not-applicable",
            EvaluationLiveToolExpectationMatcher.Match(expected, false, true, [correct]).Status);
        Assert.Equal(
            "not-evaluable",
            EvaluationLiveToolExpectationMatcher.Match(expected, true, false, [correct]).Status);
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
        var usefulProposal = Assert.Single(
            loaded.Manifest.Scenarios,
            scenario => scenario.Id == "useful-proposal");
        Assert.Equal("Performs no operation.", usefulProposal.ProposalLine);
        var liveTool = usefulProposal.LiveToolExpectation
            ?? throw new InvalidDataException();
        Assert.Equal(1, liveTool.CallCount);
        Assert.Equal("repository.search-text", liveTool.OperationId);
        Assert.Equal("evidence.source", liveTool.ScopeId);
        Assert.Equal("public void Run", liveTool.Literal);
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
        Assert.Equal(
            "63127ea0c6ee1efc9d273f2b67b49fd0c4acbd304b5786dde2be64b3b6d1d0fd",
            loaded.CorpusIdentity);
        Assert.Equal(
            "44005e7eed4c8871190396f4043392f60454640c3a43f2520c537d7a134b72df",
            loaded.SelectionIdentity);
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
    public void ReportWriterAcceptsTheCompleteClosedCodecVocabularyWithoutRawFields()
    {
        var values = Enum.GetValues<OpenAiCompatibleResponseCodecDisposition>();
        Assert.Equal(26, values.Length);
        var identifiers = values
            .Select(value => new OpenAiCompatibleResponseDiagnostic(1, value).CodecDisposition)
            .ToArray();
        Assert.Equal(identifiers.Length, identifiers.Distinct(StringComparer.Ordinal).Count());
        var protocol = File.ReadAllText(Path.Join(
            FindRepositoryRoot(),
            "docs",
            "20_architecture",
            "validation",
            "m3-provider-evaluation-protocol.md"));
        var allowlist = protocol.Split(
            "The complete first-disposition allowlist, in production evaluation order, is:",
            StringSplitOptions.None)[1].Split(
                "`codec.response.exceeds-limit` covers",
                StringSplitOptions.None)[0];
        Assert.Equal(
            identifiers,
            Regex.Matches(allowlist, "`(codec\\.[a-z0-9.-]+)`")
                .Select(match => match.Groups[1].Value)
                .ToArray());

        foreach (var identifier in identifiers)
        {
            var report = MinimalReport("safe bounded line");
            report = report with
            {
                Cases =
                [
                    report.Cases[0] with
                    {
                        ProviderResponses = [new EvaluationProviderResponseReport(1, identifier)],
                    },
                ],
            };

            var bytes = EvaluationReportWriter.Serialize(report, null);
            using var document = JsonDocument.Parse(bytes);
            var response = document.RootElement.GetProperty("cases")[0]
                .GetProperty("providerResponses")[0];
            Assert.Equal(1, response.GetProperty("providerRequestNumber").GetInt32());
            Assert.Equal(identifier, response.GetProperty("codecDisposition").GetString());
            Assert.Equal(2, response.EnumerateObject().Count());
            Assert.DoesNotContain("raw", Encoding.UTF8.GetString(bytes), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void PrivateDiagnosticReportsSeparateTheActualModelFromTheFrozenSelection()
    {
        var loaded = EvaluationManifestLoader.Load(CorpusRoot());
        var capture = Path.Join(Path.GetTempPath(), "contract-scribe-capture-" + Guid.NewGuid().ToString("N"));
        var arguments = MimoLiveArguments(capture);
        Assert.True(EvaluationOptions.TryParse(arguments, out var baselineOptions, out _));
        Assert.True(EvaluationOptions.TryParse(
            [.. arguments, "--diagnostic-model", "mimo-v2.5-pro"],
            out var comparatorOptions,
            out _));

        var baseline = EvaluationReport.Create(
            loaded,
            baselineOptions!,
            [],
            selectedCaseCount: 1,
            complete: true,
            elapsedMilliseconds: 1);
        var comparator = EvaluationReport.Create(
            loaded,
            comparatorOptions!,
            [],
            selectedCaseCount: 1,
            complete: true,
            elapsedMilliseconds: 1);

        Assert.Equal("private-response-diagnostic", baseline.ExecutionPurpose);
        Assert.False(baseline.FullCorpusComplete);
        Assert.Equal("mimo-compatibility", baseline.ConfigurationId);
        Assert.Equal("mimo-v2.5", baseline.DiagnosticConfiguration!.ActualModel);
        Assert.Equal("mimo-v2.5-pro", comparator.DiagnosticConfiguration!.ActualModel);
        Assert.Equal(
            baseline.DiagnosticConfiguration.InheritedProfileIdentity,
            comparator.DiagnosticConfiguration.InheritedProfileIdentity);
        Assert.NotEqual(
            baseline.DiagnosticConfiguration.DiagnosticConfigurationIdentity,
            comparator.DiagnosticConfiguration.DiagnosticConfigurationIdentity);
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
                "not-applicable",
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
        "not-applicable",
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

    private static string PreparedCorpusRoot()
    {
        var root = FindRepositoryRoot();
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

    private static DocumentationScribeModelRequest Request(int providerRequestNumber) => new(
        1,
        providerRequestNumber,
        ImmutableArray<DocumentationScribeModelMessage>.Empty,
        ImmutableArray<DocumentationScribeModelToolDefinition>.Empty,
        new DocumentationScribeTerminalDefinition("submit", "{}"),
        ImmutableArray<DocumentationScribeCompletedToolExchange>.Empty,
        new DocumentationScribeModelOutputLimits(1, 1, 1, 1, 1),
        ImmutableArray<byte>.Empty);

    private static string[] MimoLiveArguments(
        string captureDirectory,
        string? outputDirectory = null,
        string secretEnvironmentVariable = "CONTRACTSCRIBE_TEST_KEY") =>
    [
        "--live",
        "--safety-gate",
        "--corpus", CorpusRoot(),
        "--configuration", "mimo-compatibility",
        "--endpoint", "https://api.xiaomimimo.com/v1/chat/completions",
        "--model", "mimo-v2.5",
        "--secret-env", secretEnvironmentVariable,
        "--output", outputDirectory ?? Path.Join(Path.GetTempPath(), "contract-scribe-output-" + Guid.NewGuid().ToString("N")),
        "--currency", "cny",
        "--cached-input-rate", "1",
        "--uncached-input-rate", "1",
        "--output-rate", "1",
        "--unsafe-capture-provider-response", captureDirectory,
    ];

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

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int CreateNamedPipe(string path, uint mode);

    private static string FindRepositoryRoot([CallerFilePath] string sourcePath = "")
    {
        foreach (var start in new[]
        {
            Path.GetDirectoryName(sourcePath)!,
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory,
        })
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
}
