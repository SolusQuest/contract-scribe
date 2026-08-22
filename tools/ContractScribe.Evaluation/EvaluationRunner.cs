using System.Diagnostics;
using System.Runtime.InteropServices;
using ContractScribe.Agent.Providers;
using ContractScribe.Agent.Runtime;
using ContractScribe.Core;

namespace ContractScribe.Evaluation;

internal static class EvaluationApplication
{
    internal static async Task<int> RunAsync(
        string[] args,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken = default)
    {
        if (!EvaluationOptions.TryParse(args, out var options, out var argumentCode)
            || options is null)
        {
            await standardError.WriteLineAsync(argumentCode).ConfigureAwait(false);
            return 2;
        }

        try
        {
            var loaded = EvaluationManifestLoader.Load(options.CorpusDirectory);
            var configuration = options.IsLive
                ? loaded.Selection.Configurations.SingleOrDefault(item =>
                    item.ConfigurationId == options.ConfigurationId)
                : null;
            if (options.IsLive
                && (configuration is null
                    || options.Endpoint!.AbsoluteUri != configuration.Endpoint
                    || options.Model != configuration.Model))
            {
                await standardError.WriteLineAsync("evaluation.selection.mismatch").ConfigureAwait(false);
                return 2;
            }

            if (options.IsPrivateResponseDiagnostic
                && (!OperatingSystem.IsLinux()
                    || RuntimeInformation.ProcessArchitecture != Architecture.X64
                    || !Directory.Exists("/proc/self/fdinfo")))
            {
                await standardError.WriteLineAsync("evaluation.capture.invalid").ConfigureAwait(false);
                return 2;
            }

            var executionPaths = EvaluationRunner.PrepareExecutionPaths(
                loaded,
                options,
                options.OutputDirectory,
                createOutputDirectory: !options.IsLive);

            TransportCredential? credential = null;
            SensitiveMarker? marker = null;
            if (options.IsLive)
            {
                if (!TransportCredential.TryCapture(options.SecretEnvironmentVariable!, out credential)
                    || credential is null)
                {
                    await standardError.WriteLineAsync("evaluation.credential.missing").ConfigureAwait(false);
                    return 2;
                }

                marker = credential.CreateMarker();
                executionPaths = EvaluationRunner.PrepareExecutionPaths(
                    loaded,
                    options,
                    options.OutputDirectory,
                    createOutputDirectory: true);
            }

            OpenAiCompatibleResponseDiagnostics? createdDiagnostics;
            try
            {
                createdDiagnostics = options.IsLive
                    ? options.IsPrivateResponseDiagnostic
                        ? OpenAiCompatibleResponseDiagnostics.CreateUnsafeLinuxCapture(
                            options.ProviderResponseCaptureDirectory!,
                            executionPaths.ForbiddenRoots.Append(executionPaths.OutputDirectory!).ToArray())
                        : OpenAiCompatibleResponseDiagnostics.CreateClosedObservations()
                    : null;
            }
            catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
            {
                if (executionPaths.OutputDirectoryCreated
                    && executionPaths.OutputDirectory is { } createdOutput)
                {
                    EvaluationOutput.DeleteEmptyPrivateDirectory(createdOutput);
                }

                throw;
            }

            using var responseDiagnostics = createdDiagnostics;
            using var liveExchange = options.IsLive
                ? new OpenAiCompatibleHttpModelExchange(new OpenAiCompatibleHttpTransportOptions(
                    options.Endpoint!,
                    options.EffectiveModel!,
                    RequestProfile(configuration!),
                    networkEnabled: true,
                    credential: credential!.Take()),
                    responseDiagnostics!)
                : null;
            var runner = new EvaluationRunner(
                loaded,
                options,
                liveExchange is null ? null : _ => liveExchange,
                marker,
                options.OutputDirectory,
                executionPaths: executionPaths,
                responseDiagnostics: responseDiagnostics);
            var report = await runner.RunAsync(cancellationToken).ConfigureAwait(false);
            var bytes = EvaluationReportWriter.Serialize(
                report,
                marker,
                loaded.CorpusDirectory);

            if (options.IsLive)
            {
                await standardOutput.WriteLineAsync("evaluation.complete").ConfigureAwait(false);
            }
            else
            {
                await standardOutput.WriteAsync(System.Text.Encoding.UTF8.GetString(bytes)).ConfigureAwait(false);
            }

            return ResultExitCode(options, report);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await standardError.WriteLineAsync("evaluation.cancelled").ConfigureAwait(false);
            return 1;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            await standardError.WriteLineAsync(FailureCode(exception)).ConfigureAwait(false);
            return 1;
        }
    }

    private static OpenAiCompatibleChatCompletionsRequestProfile RequestProfile(
        EvaluationProviderConfiguration configuration) => new(
            configuration.RequestProfile.Thinking switch
            {
                "enabled" => OpenAiCompatibleThinkingMode.Enabled,
                "disabled" => OpenAiCompatibleThinkingMode.Disabled,
                _ => throw new InvalidDataException("evaluation.selection.invalid"),
            },
            configuration.RequestProfile.ReasoningEffort switch
            {
                null => null,
                "high" => OpenAiCompatibleReasoningEffort.High,
                _ => throw new InvalidDataException("evaluation.selection.invalid"),
            },
            configuration.RequestProfile.ToolChoice switch
            {
                "omitted" => OpenAiCompatibleToolChoice.Omitted,
                "auto" => OpenAiCompatibleToolChoice.Auto,
                "required" => OpenAiCompatibleToolChoice.Required,
                _ => throw new InvalidDataException("evaluation.selection.invalid"),
            },
            configuration.RequestProfile.Continuation switch
            {
                "optional" => OpenAiCompatibleContinuationPolicy.Optional,
                "required-for-tool-calls" => OpenAiCompatibleContinuationPolicy.RequiredForToolCalls,
                _ => throw new InvalidDataException("evaluation.selection.invalid"),
            },
            configuration.RequestProfile.OutputTokenField switch
            {
                "max_tokens" => OpenAiCompatibleOutputTokenField.MaxTokens,
                "max_completion_tokens" => OpenAiCompatibleOutputTokenField.MaxCompletionTokens,
                _ => throw new InvalidDataException("evaluation.selection.invalid"),
            });

    internal static int ResultExitCode(EvaluationOptions options, EvaluationReport report) =>
        !options.IsLive && report.Aggregate.ExpectedDifferedCount != 0 ? 1 : 0;

    private static string FailureCode(Exception exception)
    {
        var code = exception.Message;
        return code.StartsWith("evaluation.", StringComparison.Ordinal)
            && code.Length <= 200
            && code.All(character => char.IsAsciiLetterOrDigit(character)
                || character is ('.' or '-'))
                ? code
                : "evaluation.failed";
    }
}

internal sealed record EvaluationLiveObservationFacts(
    bool RuntimeExecutionApplicable,
    bool ContinuationObserved,
    bool ContinuationHistoryReplayed,
    bool ProviderResponseBounded,
    bool ToolCallOrTerminalObserved,
    bool ToolResultContinuationsSatisfied,
    bool UsageSupplied,
    bool UsageReported,
    bool CacheSupplied,
    bool CacheReported,
    bool BoundedTerminal,
    bool MissingRequiredContinuation,
    bool MalformedResponse,
    bool ToolProtocolRejected,
    bool TerminalValidationRejected,
    bool RequestPreparationRejected);

internal sealed record EvaluationObservationExpectationResult(
    string Status,
    string[] MissingExpectedObservationIds);

internal static class EvaluationLiveObservationMatcher
{
    internal static EvaluationObservationExpectationResult Match(
        IEnumerable<string> expectedObservations,
        IReadOnlyList<EvaluationLiveObservationFacts> executions)
    {
        var applicable = executions.Where(execution => execution.RuntimeExecutionApplicable).ToArray();
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var expected in expectedObservations)
        {
            var satisfied = expected switch
            {
                "continuation.observed" => executions.Any(execution => execution.ContinuationObserved),
                "continuation.history-replayed" => executions.Any(execution =>
                    execution.ContinuationHistoryReplayed),
                "request.accepted-or-bounded-provider-failure" => executions.Any(execution =>
                    execution.ProviderResponseBounded),
                "tool-call-or-terminal" => executions.Any(execution =>
                    execution.ToolCallOrTerminalObserved),
                "tool-result-continuation-when-requested" => executions.All(execution =>
                    execution.ToolResultContinuationsSatisfied),
                "usage-fields-when-supplied" => executions.All(execution =>
                    !execution.UsageSupplied || execution.UsageReported),
                "cache-fields-when-supplied" => executions.All(execution =>
                    !execution.CacheSupplied || execution.CacheReported),
                "validated-proposal-or-structured-skip-or-bounded-failure" =>
                    applicable.Length > 0 && applicable.All(execution => execution.BoundedTerminal),
                _ => throw new InvalidDataException("evaluation.selection.expected-observation-invalid"),
            };
            if (!satisfied)
            {
                missing.Add(expected);
            }
        }

        return new EvaluationObservationExpectationResult(
            missing.Count == 0 ? "matched" : "differed",
            missing.ToArray());
    }

    internal static string[] UnexpectedProtocolObservationIds(EvaluationLiveObservationFacts facts)
    {
        var unexpected = new SortedSet<string>(StringComparer.Ordinal);
        if (facts.MissingRequiredContinuation)
        {
            unexpected.Add("continuation.missing-required");
        }

        if (facts.MalformedResponse)
        {
            unexpected.Add("response.malformed");
        }

        if (facts.ToolProtocolRejected)
        {
            unexpected.Add("tool-protocol.rejected");
        }

        if (facts.TerminalValidationRejected)
        {
            unexpected.Add("terminal-validation.rejected");
        }

        if (facts.RequestPreparationRejected)
        {
            unexpected.Add("request.preparation-rejected");
        }

        return unexpected.ToArray();
    }

    internal static bool HasReportedCache(EvaluationUsageReport? usage) => usage is
    { CacheObservation: not null }
    or { CachedInputTokens: not null }
    or { UncachedInputTokens: not null };

    internal static bool ToolResultContinuationsSatisfied(
        IEnumerable<EvaluationProviderObservation> observations) => observations
        .Where(observation => observation.ToolResultContinuationRequired)
        .All(observation => observation.ContinuationObservation.HasFlag(
            DocumentationScribeContinuationObservation.HistoryReplayed));
}

internal sealed record EvaluationLiveToolExpectationResult(
    string Status,
    string[] DifferenceIds);

internal static class EvaluationLiveToolExpectationMatcher
{
    internal static EvaluationLiveToolExpectationResult Match(
        EvaluationLiveToolExpectation? expected,
        bool isLive,
        bool hasValidatedProposal,
        IEnumerable<EvaluationSafetyToolCallObservation> calls)
    {
        if (!isLive || expected is null)
        {
            return new EvaluationLiveToolExpectationResult("not-applicable", []);
        }

        if (!hasValidatedProposal)
        {
            return new EvaluationLiveToolExpectationResult("not-evaluable", []);
        }

        var observed = calls.ToArray();
        var differences = new SortedSet<string>(StringComparer.Ordinal);
        if (observed.Length != expected.CallCount)
        {
            differences.Add("safety-tool.call-count-differed");
        }

        if (observed.Any(call => !call.OperationMatches))
        {
            differences.Add("safety-tool.operation-differed");
        }

        if (observed.Any(call => call.OperationMatches && !call.ScopeMatches))
        {
            differences.Add("safety-tool.scope-differed");
        }

        if (observed.Any(call => call.OperationMatches && !call.LiteralMatches))
        {
            differences.Add("safety-tool.literal-differed");
        }

        return new EvaluationLiveToolExpectationResult(
            differences.Count == 0 ? "matched" : "differed",
            differences.ToArray());
    }
}

internal sealed class EvaluationRunner
{
    private readonly LoadedEvaluationManifest loaded;
    private readonly EvaluationOptions options;
    private readonly Func<PreparedEvaluationCase, IDocumentationScribeModelExchange>? liveExchangeFactory;
    private readonly SensitiveMarker? credentialMarker;
    private readonly string? requestedOutputDirectory;
    private readonly string? preparedCorpusDirectory;
    private readonly EvaluationExecutionPaths? executionPaths;
    private readonly OpenAiCompatibleResponseDiagnostics? responseDiagnostics;
    private readonly List<EvaluationCaseReport> reports = [];
    private readonly List<EvaluationLiveObservationFacts> liveObservationFacts = [];
    private readonly EvaluationProviderConfiguration? selectedConfiguration;
    private string? outputDirectory;
    private string[] outputForbiddenRoots = [];

    internal EvaluationRunner(
        LoadedEvaluationManifest loaded,
        EvaluationOptions options,
        Func<PreparedEvaluationCase, IDocumentationScribeModelExchange>? liveExchangeFactory,
        SensitiveMarker? credentialMarker,
        string? requestedOutputDirectory,
        string? preparedCorpusDirectory = null,
        EvaluationExecutionPaths? executionPaths = null,
        OpenAiCompatibleResponseDiagnostics? responseDiagnostics = null)
    {
        this.loaded = loaded;
        this.options = options;
        this.liveExchangeFactory = liveExchangeFactory;
        this.credentialMarker = credentialMarker;
        this.requestedOutputDirectory = requestedOutputDirectory;
        this.preparedCorpusDirectory = preparedCorpusDirectory;
        this.executionPaths = executionPaths;
        this.responseDiagnostics = responseDiagnostics;
        selectedConfiguration = options.IsLive
            ? loaded.Selection.Configurations.SingleOrDefault(item =>
                item.ConfigurationId == options.ConfigurationId)
                ?? throw new InvalidDataException("evaluation.selection.mismatch")
            : null;
    }

    internal static EvaluationExecutionPaths PrepareExecutionPaths(
        LoadedEvaluationManifest loaded,
        EvaluationOptions options,
        string? requestedOutputDirectory,
        string? preparedCorpusDirectory = null,
        bool createOutputDirectory = true)
    {
        var preparedPathFile = Path.Join(AppContext.BaseDirectory, "evaluation-corpus-path.txt");
        var preparedPath = preparedCorpusDirectory ?? File.ReadAllText(preparedPathFile).Trim();
        if (!EvaluationOutput.TryResolveExistingTemporaryDirectory(preparedPath, out var preparedDirectory)
            || preparedDirectory is null)
        {
            throw new InvalidDataException("evaluation.corpus.prepared-path-invalid");
        }

        var preparedCorpus = EvaluationManifestLoader.Load(preparedDirectory);
        if (preparedCorpus.CorpusIdentity != loaded.CorpusIdentity)
        {
            throw new InvalidDataException("evaluation.corpus.prepared-mismatch");
        }

        var preparedRepository = Path.GetDirectoryName(Path.Join(
            preparedDirectory,
            preparedCorpus.Manifest.RepositoryProject))
            ?? throw new InvalidDataException("evaluation.repository.path-invalid");
        var checkout = FindCheckoutRoot(loaded.CorpusDirectory);
        string[] forbiddenRoots =
        [
            loaded.CorpusDirectory,
            preparedDirectory,
            preparedRepository,
            checkout,
        ];
        string? outputDirectory = null;
        var outputDirectoryCreated = false;
        if (requestedOutputDirectory is not null)
        {
            var outputValid = options.IsPrivateResponseDiagnostic
                ? createOutputDirectory
                    ? EvaluationOutput.TryResolveNewPrivateDirectory(
                        requestedOutputDirectory,
                        forbiddenRoots,
                        out outputDirectory)
                    : EvaluationOutput.TryValidateNewPrivateDirectory(
                        requestedOutputDirectory,
                        forbiddenRoots,
                        out outputDirectory)
                : createOutputDirectory
                    ? EvaluationOutput.TryResolveDirectory(
                        requestedOutputDirectory,
                        forbiddenRoots,
                        out outputDirectory)
                    : EvaluationOutput.TryValidateDirectory(
                        requestedOutputDirectory,
                        forbiddenRoots,
                        out outputDirectory);
            if (!outputValid)
            {
                throw new InvalidDataException("evaluation.output.invalid");
            }

            outputDirectoryCreated = options.IsPrivateResponseDiagnostic && createOutputDirectory;
        }

        if (options.IsPrivateResponseDiagnostic
            && (outputDirectory is null
                || !EvaluationOutput.TryValidateNewPrivateDirectory(
                    options.ProviderResponseCaptureDirectory!,
                    forbiddenRoots.Append(outputDirectory).ToArray(),
                    out _)))
        {
            if (outputDirectoryCreated && outputDirectory is not null)
            {
                EvaluationOutput.DeleteEmptyPrivateDirectory(outputDirectory);
            }

            throw new InvalidDataException("evaluation.capture.invalid");
        }

        return new EvaluationExecutionPaths(
            preparedDirectory,
            outputDirectory,
            forbiddenRoots,
            outputDirectoryCreated);
    }

    internal async Task<EvaluationReport> RunAsync(CancellationToken cancellationToken)
    {
        var liveIds = selectedConfiguration?.LiveScenarioIds.ToHashSet(StringComparer.Ordinal)
            ?? [];
        var selected = options.Mode switch
        {
            EvaluationMode.LiveSafetyGate => loaded.Manifest.Scenarios.Where(scenario =>
                scenario.Id == selectedConfiguration!.SafetyGateCaseId).ToArray(),
            EvaluationMode.LiveAll => loaded.Manifest.Scenarios.Where(scenario =>
                liveIds.Contains(scenario.Id)).ToArray(),
            _ => loaded.Manifest.Scenarios,
        };
        var stopwatch = options.IsLive ? Stopwatch.StartNew() : null;
        var adapter = new ProductionCompositionAdapter();
        var paths = executionPaths ?? PrepareExecutionPaths(
            loaded,
            options,
            requestedOutputDirectory,
            preparedCorpusDirectory);
        var preparedDirectory = paths.PreparedDirectory;
        var preparedCorpus = EvaluationManifestLoader.Load(preparedDirectory);
        outputForbiddenRoots = paths.ForbiddenRoots;
        outputDirectory = paths.OutputDirectory;

        await PersistAsync(
            EvaluationReport.Create(
                loaded,
                options,
                reports,
                selected.Length,
                false,
                null),
            CancellationToken.None).ConfigureAwait(false);

        EvaluationRepositorySession repository;
        try
        {
            repository = await EvaluationRepositorySession.CreateAsync(
                preparedCorpus,
                adapter,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested
            && selected.FirstOrDefault() is { } interrupted)
        {
            reports.Add(FailedCase(interrupted, "cancelled", "evaluation.case.cancelled"));
            await PersistAsync(
                EvaluationReport.Create(
                    loaded,
                    options,
                    reports,
                    selected.Length,
                    false,
                    Elapsed(stopwatch),
                    interrupted.Id),
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        await using (repository.ConfigureAwait(false))
        {
            foreach (var scenario in selected)
            {
                await PersistAsync(
                    EvaluationReport.Create(
                        loaded,
                        options,
                        reports,
                        selected.Length,
                        false,
                        Elapsed(stopwatch),
                        scenario.Id),
                    CancellationToken.None).ConfigureAwait(false);
                var cancelled = false;
                var diagnosticsBegan = false;
                OpenAiCompatibleResponseDiagnosticCase? diagnosticCase = null;
                PreparedEvaluationCase? prepared = null;
                CostObservingExchange? observing = null;
                EvaluationCompositionOutcome? outcome = null;
                try
                {
                    if (responseDiagnostics is not null)
                    {
                        responseDiagnostics.BeginCase(
                            (selectedConfiguration ?? loaded.Selection.Configurations[0])
                                .Limits.MaximumProviderRequests);
                        diagnosticsBegan = true;
                    }

                    prepared = repository.Prepare(
                        loaded,
                        scenario,
                        (selectedConfiguration ?? loaded.Selection.Configurations[0]).Limits);
                    var baseExchange = liveExchangeFactory?.Invoke(prepared)
                        ?? new ScriptedEvaluationExchange(prepared);
                    observing = new CostObservingExchange(
                        baseExchange,
                        options.CostPolicy,
                        options.IsLive ? scenario.LiveToolExpectation : null);
                    outcome = await adapter.ExecuteAsync(
                        prepared.SelectedAudit,
                        prepared.RequestBytes,
                        prepared.AttemptId,
                        RuntimeOptions(),
                        observing,
                        cancellationToken).ConfigureAwait(false);
                    if (diagnosticsBegan)
                    {
                        diagnosticCase = responseDiagnostics!.CompleteCase();
                        diagnosticsBegan = false;
                    }

                    if (diagnosticCase?.FailureCode is { } diagnosticFailure)
                    {
                        throw new InvalidDataException(diagnosticFailure);
                    }

                    var caseReport = CreateCaseReport(
                        prepared,
                        outcome,
                        observing.Observations,
                        diagnosticCase);
                    reports.Add(caseReport);
                    if (options.IsLive)
                    {
                        liveObservationFacts.Add(LiveObservationFacts(
                            outcome,
                            caseReport.Usage,
                            observing.Observations));
                    }
                    cancelled = caseReport.Status == "cancelled";
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    diagnosticCase ??= CompleteDiagnostics(responseDiagnostics, ref diagnosticsBegan);
                    reports.Add(FailedCase(
                        scenario,
                        "cancelled",
                        "evaluation.case.cancelled",
                        diagnosticCase,
                        prepared,
                        outcome,
                        observing?.Observations));
                    cancelled = true;
                }
                catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
                {
                    diagnosticCase ??= CompleteDiagnostics(responseDiagnostics, ref diagnosticsBegan);
                    reports.Add(FailedCase(
                        scenario,
                        "failed",
                        diagnosticCase?.FailureCode ?? SafeCaseCode(exception),
                        diagnosticCase,
                        prepared,
                        outcome,
                        observing?.Observations));
                }

                if (cancelled)
                {
                    await PersistAsync(
                        EvaluationReport.Create(
                            loaded,
                            options,
                            reports,
                            selected.Length,
                            false,
                            Elapsed(stopwatch),
                            scenario.Id),
                        CancellationToken.None).ConfigureAwait(false);
                    throw new OperationCanceledException(cancellationToken);
                }

                await PersistAsync(
                    EvaluationReport.Create(loaded, options, reports, selected.Length, false, Elapsed(stopwatch)),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        stopwatch?.Stop();
        var final = EvaluationReport.Create(
            loaded,
            options,
            reports,
            selected.Length,
            true,
            Elapsed(stopwatch),
            observationExpectation: options.IsLive
                ? EvaluationLiveObservationMatcher.Match(
                    selectedConfiguration!.ExpectedObservations,
                    liveObservationFacts)
                : null);
        await PersistAsync(final, cancellationToken).ConfigureAwait(false);
        if (outputDirectory is not null)
        {
            EvaluationOutput.DeleteOwnedPartial(outputDirectory, outputForbiddenRoots);
        }

        return final;
    }

    private async Task PersistAsync(EvaluationReport report, CancellationToken cancellationToken)
    {
        if (outputDirectory is null)
        {
            return;
        }

        var forbiddenValues = outputForbiddenRoots
            .Append(outputDirectory)
            .Concat(options.ProviderResponseCaptureDirectory is null
                ? []
                : [options.ProviderResponseCaptureDirectory])
            .ToArray();
        var bytes = EvaluationReportWriter.Serialize(report, credentialMarker, forbiddenValues);
        await EvaluationOutput.WriteAtomicAsync(
            outputDirectory,
            report.Status == "complete" ? "evaluation-report.json" : "evaluation-partial.json",
            bytes,
            outputForbiddenRoots,
            cancellationToken).ConfigureAwait(false);
    }

    private DocumentationScribeRuntimeOptions RuntimeOptions() => options.IsLive
        ? selectedConfiguration!.ConfigurationId switch
        {
            "deepseek-primary" => new DocumentationScribeRuntimeOptions(
                "provider.deepseek.v1",
                "model.deepseek-v4-flash",
                "scribe-protocol.v1"),
            "mimo-compatibility" => new DocumentationScribeRuntimeOptions(
                "provider.mimo.v1",
                options.DiagnosticModel == "mimo-v2.5-pro"
                    ? "model.mimo-v2-5-pro"
                    : "model.mimo-v2-5",
                "scribe-protocol.v1"),
            _ => throw new InvalidDataException("evaluation.selection.invalid"),
        }
        : new DocumentationScribeRuntimeOptions(
            "provider.synthetic.v1",
            "model.synthetic.v1",
            "scribe-protocol.v1");

    private EvaluationCaseReport CreateCaseReport(
        PreparedEvaluationCase prepared,
        EvaluationCompositionOutcome outcome,
        IReadOnlyList<EvaluationProviderObservation> providerObservations,
        OpenAiCompatibleResponseDiagnosticCase? diagnosticCase)
    {
        var status = Status(outcome.Status);
        var envelope = outcome.RunResult?.RunEnvelope;
        var cost = AggregateCost(providerObservations);
        var providerFailures = providerObservations
            .Where(observation => observation.FailureCode is not null)
            .Select(observation => new EvaluationProviderFailureReport(
                observation.ProviderRequestNumber,
                EvaluationProviderFailureReport.CodeId(observation.FailureCode!.Value),
                observation.FailureOrigin is { } origin
                    ? EvaluationProviderFailureReport.OriginId(origin)
                    : null,
                observation.HttpStatusCode))
            .OrderBy(observation => observation.ProviderRequestNumber)
            .ToArray();
        var runtimeDiagnostics = envelope?.Diagnostics
            .Select(diagnostic => new EvaluationRuntimeDiagnosticReport(
                diagnostic.Code,
                diagnostic.Stage,
                diagnostic.ReferenceId,
                diagnostic.ValidationCode))
            .ToArray() ?? [];
        var usage = envelope?.Usage is { } modelUsage
            ? new EvaluationUsageReport(
                modelUsage.InputTokens,
                modelUsage.OutputTokens,
                modelUsage.CachedInputTokens,
                modelUsage.UncachedInputTokens,
                modelUsage.ReasoningTokens,
                envelope.Cache is { } cache ? DocumentationScribeVocabulary.GetId(cache) : null)
            : null;
        var proposal = EvaluationReportWriter.ProjectProposal(
            outcome.RunResult,
            status,
            prepared.Scenario.ProposalLine);
        var safetyToolExpectation = EvaluationLiveToolExpectationMatcher.Match(
            prepared.Scenario.LiveToolExpectation,
            options.IsLive,
            proposal is not null,
            providerObservations.SelectMany(observation => observation.SafetyToolCalls));
        var observedCoverage = ObservedCoverage(
            prepared.Scenario,
            options.IsLive,
            status,
            outcome.Code,
            envelope,
            outcome.RunResult,
            providerObservations);
        var expectation = Expectation(
            prepared.Scenario,
            options.IsLive,
            status,
            outcome.Code,
            envelope?.AttemptNumber ?? 0,
            envelope?.ProviderRequestCount ?? 0,
            envelope?.ToolRoundCount ?? 0,
            envelope?.ToolCallCount ?? 0,
            usage,
            proposal,
            observedCoverage,
            safetyToolExpectation.DifferenceIds);
        return new EvaluationCaseReport(
            prepared.Scenario.Id,
            status,
            outcome.Code,
            expectation.Status,
            expectation.DifferenceIds,
            safetyToolExpectation.Status,
            UnexpectedProtocolObservations(outcome, envelope, providerObservations),
            envelope?.AttemptNumber ?? 0,
            envelope?.ProviderRequestCount ?? 0,
            envelope?.ToolRoundCount ?? 0,
            envelope?.ToolCallCount ?? 0,
            providerFailures,
            runtimeDiagnostics,
            usage,
            cost,
            proposal,
            "passed",
            prepared.Scenario.Coverage.Order(StringComparer.Ordinal).ToArray(),
            observedCoverage)
        {
            ProviderResponses = ProjectProviderResponses(
                diagnosticCase,
                selectedConfiguration?.Limits.MaximumProviderRequests),
        };
    }

    private EvaluationCaseReport FailedCase(
        EvaluationScenario scenario,
        string status,
        string code,
        OpenAiCompatibleResponseDiagnosticCase? diagnosticCase = null,
        PreparedEvaluationCase? prepared = null,
        EvaluationCompositionOutcome? outcome = null,
        IReadOnlyList<EvaluationProviderObservation>? providerObservations = null)
    {
        providerObservations ??= [];
        var providerResponses = ProjectProviderResponses(
            diagnosticCase,
            selectedConfiguration?.Limits.MaximumProviderRequests);
        var envelope = outcome?.RunResult?.RunEnvelope;
        var providerFailures = providerObservations
            .Where(observation => observation.FailureCode is not null)
            .Select(observation => new EvaluationProviderFailureReport(
                observation.ProviderRequestNumber,
                EvaluationProviderFailureReport.CodeId(observation.FailureCode!.Value),
                observation.FailureOrigin is { } origin
                    ? EvaluationProviderFailureReport.OriginId(origin)
                    : null,
                observation.HttpStatusCode))
            .OrderBy(observation => observation.ProviderRequestNumber)
            .ToArray();
        var runtimeDiagnostics = envelope?.Diagnostics
            .Select(diagnostic => new EvaluationRuntimeDiagnosticReport(
                diagnostic.Code,
                diagnostic.Stage,
                diagnostic.ReferenceId,
                diagnostic.ValidationCode))
            .ToArray() ?? [];
        var usage = envelope?.Usage is { } modelUsage
            ? new EvaluationUsageReport(
                modelUsage.InputTokens,
                modelUsage.OutputTokens,
                modelUsage.CachedInputTokens,
                modelUsage.UncachedInputTokens,
                modelUsage.ReasoningTokens,
                envelope.Cache is { } cache ? DocumentationScribeVocabulary.GetId(cache) : null)
            : null;
        var providerRequestCount = new[]
        {
            envelope?.ProviderRequestCount ?? 0,
            diagnosticCase?.ProviderRequestCount ?? 0,
            providerObservations.Count == 0
                ? 0
                : providerObservations.Max(observation => observation.ProviderRequestNumber),
        }.Max();
        var toolRoundCount = envelope?.ToolRoundCount
            ?? providerObservations.Count(observation => observation.OrdinaryToolCallObserved);
        var toolCallCount = envelope?.ToolCallCount
            ?? providerObservations.Sum(observation => observation.OrdinaryToolCallCount);
        var proposal = prepared is not null && outcome is not null
            ? EvaluationReportWriter.ProjectProposal(
                outcome.RunResult,
                Status(outcome.Status),
                prepared.Scenario.ProposalLine)
            : null;
        var safetyToolExpectation = EvaluationLiveToolExpectationMatcher.Match(
            scenario.LiveToolExpectation,
            options.IsLive,
            proposal is not null,
            providerObservations.SelectMany(observation => observation.SafetyToolCalls));
        var unexpected = outcome is null
            ? []
            : UnexpectedProtocolObservations(outcome, envelope, providerObservations);
        var observedCoverage = prepared is not null && outcome is not null
            ? ObservedCoverage(
                prepared.Scenario,
                options.IsLive,
                Status(outcome.Status),
                outcome.Code,
                envelope,
                outcome.RunResult,
                providerObservations)
                .Append(code)
                .Append(status)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray()
            : [code, status];
        return new EvaluationCaseReport(
            scenario.Id,
            status,
            code,
            "differed",
            [.. new[] { "case.execution-differed" }.Concat(safetyToolExpectation.DifferenceIds)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)],
            safetyToolExpectation.Status,
            unexpected,
            envelope?.AttemptNumber
                ?? (prepared is not null
                    && (diagnosticCase?.ProviderRequestCount > 0
                        || providerObservations.Count > 0)
                        ? 1
                        : 0),
            providerRequestCount,
            toolRoundCount,
            toolCallCount,
            providerFailures,
            runtimeDiagnostics,
            usage,
            AggregateCost(providerObservations),
            proposal,
            "passed",
            scenario.Coverage.Order(StringComparer.Ordinal).ToArray(),
            observedCoverage)
        {
            ProviderResponses = providerResponses,
        };
    }

    private static EvaluationProviderResponseReport[] ProjectProviderResponses(
        OpenAiCompatibleResponseDiagnosticCase? diagnosticCase,
        int? maximumProviderRequests)
    {
        if (diagnosticCase is null)
        {
            return [];
        }

        var responses = diagnosticCase.Responses
            .Select(response => new EvaluationProviderResponseReport(
                response.ProviderRequestNumber,
                response.CodecDisposition))
            .ToArray();
        var maximum = maximumProviderRequests ?? 0;
        if (maximum <= 0
            || responses.Length > maximum
            || diagnosticCase.ProviderRequestCount is < 0
                || diagnosticCase.ProviderRequestCount > maximum
            || responses.Length > 0
                && responses[^1].ProviderRequestNumber > diagnosticCase.ProviderRequestCount
            || responses.Where((response, index) =>
                    response.ProviderRequestNumber is <= 0
                    || response.ProviderRequestNumber > maximum
                    || index > 0
                        && responses[index - 1].ProviderRequestNumber
                            >= response.ProviderRequestNumber)
                .Any())
        {
            throw new InvalidDataException("evaluation.diagnostics.failed");
        }

        return responses;
    }

    private static OpenAiCompatibleResponseDiagnosticCase? CompleteDiagnostics(
        OpenAiCompatibleResponseDiagnostics? diagnostics,
        ref bool began)
    {
        if (diagnostics is null || !began)
        {
            return null;
        }

        began = false;
        return diagnostics.CompleteCase();
    }

    private static EvaluationCostReport AggregateCost(
        IReadOnlyList<EvaluationProviderObservation> observations)
    {
        if (observations.Count == 0
            || observations.All(item => item.Cost.Completeness == EvaluationCostCompleteness.NotReported))
        {
            return new EvaluationCostReport("not-reported", null, null);
        }

        var priced = observations.Where(item => item.Cost.AmountMicrounits is not null).ToArray();
        var currencies = priced.Select(item => item.Cost.CurrencyId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (currencies.Length != 1)
        {
            return new EvaluationCostReport("not-reported", null, null);
        }

        var amount = priced.Aggregate(
            0L,
            (sum, item) => checked(sum + item.Cost.AmountMicrounits!.Value));
        var status = observations.All(item =>
            item.Cost.Completeness == EvaluationCostCompleteness.Complete)
                ? "complete"
                : "partial";
        return new EvaluationCostReport(status, currencies[0], amount);
    }

    private static ExpectationResult Expectation(
        EvaluationScenario scenario,
        bool isLive,
        string status,
        string code,
        int attemptCount,
        int providerRequestCount,
        int toolRoundCount,
        int toolCallCount,
        EvaluationUsageReport? usage,
        EvaluationProposalReport? proposal,
        string[] observedCoverage,
        IEnumerable<string> liveToolDifferenceIds)
    {
        var exactOutcome = scenario.ExpectedStatus == status && scenario.ExpectedCode == code;
        var platformNotObserved = !exactOutcome
            && scenario.RequiredPlatform is { } platform
            && platform != PlatformId()
            && scenario.NonRequiredPlatformStatus == status
            && scenario.NonRequiredPlatformCode == code;
        var differences = new SortedSet<string>(StringComparer.Ordinal);
        if (!exactOutcome && !platformNotObserved)
        {
            if (scenario.ExpectedStatus != status)
            {
                differences.Add("outcome.status-differed");
            }

            if (scenario.ExpectedCode != code)
            {
                differences.Add("outcome.code-differed");
            }
        }

        if (!isLive)
        {
            AddOfflineDifferences(
                scenario,
                attemptCount,
                providerRequestCount,
                toolRoundCount,
                toolCallCount,
                usage,
                proposal,
                observedCoverage,
                status,
                code,
                differences);
        }

        foreach (var difference in liveToolDifferenceIds)
        {
            differences.Add(difference);
        }

        var differenceIds = differences.ToArray();
        return new ExpectationResult(
            differenceIds.Length != 0
                ? "differed"
                : platformNotObserved
                    ? "platform-not-observed"
                    : "matched",
            differenceIds);
    }

    private static void AddOfflineDifferences(
        EvaluationScenario scenario,
        int attemptCount,
        int providerRequestCount,
        int toolRoundCount,
        int toolCallCount,
        EvaluationUsageReport? usage,
        EvaluationProposalReport? proposal,
        string[] observedCoverage,
        string status,
        string code,
        ISet<string> differences)
    {
        var expected = scenario.OfflineExpectation;
        AddCountDifference(expected.AttemptCount, attemptCount, "attempt-count-differed", differences);
        AddCountDifference(
            expected.ProviderRequestCount,
            providerRequestCount,
            "provider-request-count-differed",
            differences);
        AddCountDifference(expected.ToolRoundCount, toolRoundCount, "tool-round-count-differed", differences);
        AddCountDifference(expected.ToolCallCount, toolCallCount, "tool-call-count-differed", differences);

        var proposalStatus = proposal?.ValidationStatus ?? "not-reported";
        if (expected.ProposalStatus != proposalStatus)
        {
            differences.Add("proposal.validation-status-differed");
        }

        if (proposal is not null)
        {
            foreach (var difference in proposal.DifferenceIds)
            {
                differences.Add(difference);
            }
        }

        AddUsageDifferences(expected.Usage, usage, differences);
        var actualObservations = observedCoverage
            .Where(observation => observation != status && observation != code)
            .ToHashSet(StringComparer.Ordinal);
        var expectedObservations = expected.ObservationIds.ToHashSet(StringComparer.Ordinal);
        if (!expectedObservations.IsSubsetOf(actualObservations))
        {
            differences.Add("observation.missing");
        }

        if (!actualObservations.IsSubsetOf(expectedObservations))
        {
            differences.Add("observation.unexpected");
        }
    }

    private static void AddCountDifference(
        int expected,
        int actual,
        string differenceId,
        ISet<string> differences)
    {
        if (expected != actual)
        {
            differences.Add(differenceId);
        }
    }

    private static void AddUsageDifferences(
        EvaluationUsageExpectation? expected,
        EvaluationUsageReport? actual,
        ISet<string> differences)
    {
        if (expected is null)
        {
            if (actual is not null)
            {
                differences.Add("usage.unexpected");
            }

            return;
        }

        if (actual is null)
        {
            differences.Add("usage.missing");
            return;
        }

        AddUsageDifference(expected.InputTokens, actual.InputTokens, "usage.input-tokens-differed", differences);
        AddUsageDifference(expected.OutputTokens, actual.OutputTokens, "usage.output-tokens-differed", differences);
        AddUsageDifference(
            expected.CachedInputTokens,
            actual.CachedInputTokens,
            "usage.cached-input-tokens-differed",
            differences);
        AddUsageDifference(
            expected.UncachedInputTokens,
            actual.UncachedInputTokens,
            "usage.uncached-input-tokens-differed",
            differences);
        AddUsageDifference(
            expected.ReasoningTokens,
            actual.ReasoningTokens,
            "usage.reasoning-tokens-differed",
            differences);
        if (expected.CacheObservation != actual.CacheObservation)
        {
            differences.Add("usage.cache-observation-differed");
        }
    }

    private static void AddUsageDifference(
        int? expected,
        int? actual,
        string differenceId,
        ISet<string> differences)
    {
        if (expected != actual)
        {
            differences.Add(differenceId);
        }
    }

    private sealed record ExpectationResult(string Status, string[] DifferenceIds);

    private static EvaluationLiveObservationFacts LiveObservationFacts(
        EvaluationCompositionOutcome outcome,
        EvaluationUsageReport? usage,
        IReadOnlyList<EvaluationProviderObservation> providerObservations)
    {
        var envelope = outcome.RunResult?.RunEnvelope;
        return new EvaluationLiveObservationFacts(
            envelope?.AttemptNumber > 0,
            providerObservations.Any(observation => observation.ContinuationObservation.HasFlag(
                DocumentationScribeContinuationObservation.Observed)),
            providerObservations.Any(observation => observation.ContinuationObservation.HasFlag(
                DocumentationScribeContinuationObservation.HistoryReplayed)),
            providerObservations.Any(observation =>
                observation.ResponseAccepted
                || observation.FailureOrigin is DocumentationScribeModelFailureOrigin.Transport
                    or DocumentationScribeModelFailureOrigin.HttpStatus
                    or DocumentationScribeModelFailureOrigin.SuccessfulResponse
                    or DocumentationScribeModelFailureOrigin.ResponseCodec),
            providerObservations.Any(observation =>
                observation.OrdinaryToolCallObserved || observation.TerminalSubmissionObserved),
            EvaluationLiveObservationMatcher.ToolResultContinuationsSatisfied(providerObservations),
            providerObservations.Any(observation => observation.UsageSupplied),
            usage is not null,
            providerObservations.Any(observation => observation.CacheSupplied),
            EvaluationLiveObservationMatcher.HasReportedCache(usage),
            outcome.RunResult?.Terminal is DocumentationScribeProposalTerminal
                or DocumentationScribeSkipTerminal
                or DocumentationScribeFailureTerminal,
            providerObservations.Any(observation => observation.ContinuationObservation.HasFlag(
                DocumentationScribeContinuationObservation.MissingRequired)),
            providerObservations.Any(observation =>
                observation.FailureCode == DocumentationScribeModelFailureCode.MalformedResponse),
            outcome.RunResult?.Terminal is DocumentationScribeFailureTerminal terminalFailure
                && terminalFailure.Code == DocumentationScribeFailureCode.ToolProtocol,
            envelope?.Diagnostics.Any(diagnostic =>
                diagnostic.Code == "scribe.diagnostic.result-rejected") == true,
            providerObservations.Any(observation =>
                observation.FailureOrigin == DocumentationScribeModelFailureOrigin.RequestPreparation));
    }

    private static string[] UnexpectedProtocolObservations(
        EvaluationCompositionOutcome outcome,
        DocumentationScribeRunEnvelope? envelope,
        IReadOnlyList<EvaluationProviderObservation> providerObservations)
    {
        var facts = LiveObservationFacts(outcome, envelope?.Usage is { } usage
            ? new EvaluationUsageReport(
                usage.InputTokens,
                usage.OutputTokens,
                usage.CachedInputTokens,
                usage.UncachedInputTokens,
                usage.ReasoningTokens,
                envelope.Cache is { } cache ? DocumentationScribeVocabulary.GetId(cache) : null)
            : null, providerObservations);
        return EvaluationLiveObservationMatcher.UnexpectedProtocolObservationIds(facts);
    }

    private static string[] ObservedCoverage(
        EvaluationScenario scenario,
        bool isLive,
        string status,
        string code,
        DocumentationScribeRunEnvelope? envelope,
        DocumentationScribeRunResult? runResult,
        IReadOnlyList<EvaluationProviderObservation> providerObservations)
    {
        var observed = new HashSet<string>(StringComparer.Ordinal) { status, code };
        if (envelope?.ProviderRequestCount > 0)
        {
            observed.Add("provider-request");
        }

        if (envelope?.ToolCallCount > 0)
        {
            observed.Add("tool-call");
        }

        if (envelope?.Usage is not null)
        {
            observed.Add("usage-observed");
        }

        if (envelope?.Cache is { } cache)
        {
            observed.Add(DocumentationScribeVocabulary.GetId(cache));
        }

        if (runResult?.Terminal is DocumentationScribeProposalTerminal)
        {
            observed.Add("proposal-validated");
        }

        foreach (var observation in providerObservations)
        {
            if (observation.ContinuationObservation.HasFlag(
                    DocumentationScribeContinuationObservation.Observed))
            {
                observed.Add("continuation.observed");
            }

            if (observation.ContinuationObservation.HasFlag(
                    DocumentationScribeContinuationObservation.HistoryReplayed))
            {
                observed.Add("continuation.history-replayed");
            }

            if (observation.ContinuationObservation.HasFlag(
                    DocumentationScribeContinuationObservation.MissingRequired))
            {
                observed.Add("continuation.missing-required");
            }

            var coverage = observation.FailureCode switch
            {
                DocumentationScribeModelFailureCode.TransientUnavailable
                    or DocumentationScribeModelFailureCode.PermanentUnavailable => "provider-unavailable",
                DocumentationScribeModelFailureCode.RateLimited => "provider-rate-limit",
                DocumentationScribeModelFailureCode.Authentication => "provider-authentication",
                DocumentationScribeModelFailureCode.Unsupported => "provider-unsupported",
                DocumentationScribeModelFailureCode.MalformedResponse => "provider-malformed-response",
                null => null,
                _ => throw new ArgumentOutOfRangeException(nameof(providerObservations)),
            };
            if (coverage is not null)
            {
                observed.Add(coverage);
            }
        }

        if (!isLive && envelope?.ProviderRequestCount > 0)
        {
            foreach (var item in scenario.Script switch
            {
                "tool-proposal" => new[] { "tool-loop", "useful-proposal" },
                "skip" when scenario.Id == "structured-skip" => ["structured-skip"],
                "skip" => ["evidence-insufficient"],
                "invalid-tool" => ["tool-invalid", "tool-unsupported"],
                "malformed-output" => ["model-output-malformed"],
                "rate-limited" => ["retry"],
                "budget-exhausted" => ["budget-exhausted"],
                "timeout" => ["timeout"],
                _ => [],
            })
            {
                observed.Add(item);
            }
        }

        return observed.Order(StringComparer.Ordinal).ToArray();
    }

    private static string PlatformId() => OperatingSystem.IsLinux()
        ? "linux"
        : OperatingSystem.IsWindows()
            ? "windows"
            : OperatingSystem.IsMacOS()
                ? "macos"
                : "other";

    private static string FindCheckoutRoot(string corpusDirectory)
    {
        foreach (var start in new[] { corpusDirectory, AppContext.BaseDirectory })
        {
            var current = new DirectoryInfo(Path.GetFullPath(start));
            while (current is not null)
            {
                if (File.Exists(Path.Join(current.FullName, "ContractScribe.slnx")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }
        }

        throw new InvalidDataException("evaluation.checkout.not-found");
    }

    private static string Status(string value) => value switch
    {
        "PreflightRejected" => "preflight-rejected",
        "ProposalSkipped" => "proposal-skipped",
        "ProposalRejected" => "proposal-rejected",
        "PatchAccepted" => "patch-accepted",
        "PatchRejected" => "patch-rejected",
        "PatchStale" => "patch-stale",
        "ProviderFailure" => "provider-failure",
        "RuntimeFailure" => "runtime-failure",
        "Cancelled" => "cancelled",
        "Timeout" => "timeout",
        "BudgetExhausted" => "budget-exhausted",
        _ => "failed",
    };

    private static int? Elapsed(Stopwatch? stopwatch) => stopwatch is null
        ? null
        : checked((int)Math.Min(stopwatch.ElapsedMilliseconds, int.MaxValue));

    private static string SafeCaseCode(Exception exception)
    {
        var code = exception.Message;
        return code.StartsWith("evaluation.", StringComparison.Ordinal)
            && code.Length <= 200
            && code.All(character => char.IsAsciiLetterOrDigit(character)
                || character is ('.' or '-'))
                ? code
                : "evaluation.case.internal";
    }
}

internal sealed record EvaluationExecutionPaths(
    string PreparedDirectory,
    string? OutputDirectory,
    string[] ForbiddenRoots,
    bool OutputDirectoryCreated = false);
