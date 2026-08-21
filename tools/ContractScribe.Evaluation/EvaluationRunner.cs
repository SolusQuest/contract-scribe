using System.Diagnostics;
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
        }

        try
        {
            var loaded = EvaluationManifestLoader.Load(options.CorpusDirectory);
            if (options.IsLive
                && (options.Endpoint!.AbsoluteUri != loaded.Selection.Endpoint
                    || options.Model != loaded.Selection.Model))
            {
                await standardError.WriteLineAsync("evaluation.selection.mismatch").ConfigureAwait(false);
                return 2;
            }

            string? outputDirectory = null;
            if (options.OutputDirectory is not null
                && !EvaluationOutput.TryResolveDirectory(options.OutputDirectory, out outputDirectory))
            {
                await standardError.WriteLineAsync("evaluation.output.invalid").ConfigureAwait(false);
                return 2;
            }

            using var liveExchange = options.IsLive
                ? new OpenAiCompatibleHttpModelExchange(new OpenAiCompatibleHttpTransportOptions(
                    options.Endpoint!,
                    options.Model!,
                    networkEnabled: true,
                    credential!.Take()))
                : null;
            var runner = new EvaluationRunner(
                loaded,
                options,
                liveExchange is null ? null : _ => liveExchange,
                marker,
                outputDirectory);
            var report = await runner.RunAsync(cancellationToken).ConfigureAwait(false);
            var bytes = EvaluationReportWriter.Serialize(
                report,
                marker,
                loaded.CorpusDirectory,
                outputDirectory ?? string.Empty);
            if (outputDirectory is not null)
            {
                await EvaluationOutput.WriteAtomicAsync(
                    outputDirectory,
                    "evaluation-report.json",
                    bytes,
                    cancellationToken).ConfigureAwait(false);
            }

            if (options.IsLive)
            {
                await standardOutput.WriteLineAsync("evaluation.complete").ConfigureAwait(false);
            }
            else
            {
                await standardOutput.WriteAsync(System.Text.Encoding.UTF8.GetString(bytes)).ConfigureAwait(false);
            }

            return 0;
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

internal sealed class EvaluationRunner
{
    private readonly LoadedEvaluationManifest loaded;
    private readonly EvaluationOptions options;
    private readonly Func<PreparedEvaluationCase, IDocumentationScribeModelExchange>? liveExchangeFactory;
    private readonly SensitiveMarker? credentialMarker;
    private readonly string? outputDirectory;
    private readonly string? preparedCorpusDirectory;
    private readonly List<EvaluationCaseReport> reports = [];

    internal EvaluationRunner(
        LoadedEvaluationManifest loaded,
        EvaluationOptions options,
        Func<PreparedEvaluationCase, IDocumentationScribeModelExchange>? liveExchangeFactory,
        SensitiveMarker? credentialMarker,
        string? outputDirectory,
        string? preparedCorpusDirectory = null)
    {
        this.loaded = loaded;
        this.options = options;
        this.liveExchangeFactory = liveExchangeFactory;
        this.credentialMarker = credentialMarker;
        this.outputDirectory = outputDirectory;
        this.preparedCorpusDirectory = preparedCorpusDirectory;
    }

    internal async Task<EvaluationReport> RunAsync(CancellationToken cancellationToken)
    {
        var selected = options.Mode == EvaluationMode.LiveSafetyGate
            ? loaded.Manifest.Scenarios.Where(scenario =>
                scenario.Id == loaded.Manifest.SafetyGateCaseId).ToArray()
            : loaded.Manifest.Scenarios;
        await PersistAsync(
            EvaluationReport.Create(loaded, options, reports, selected.Length, false, null),
            cancellationToken).ConfigureAwait(false);
        var stopwatch = options.IsLive ? Stopwatch.StartNew() : null;
        var adapter = new ProductionCompositionAdapter();
        var preparedPathFile = Path.Join(AppContext.BaseDirectory, "evaluation-corpus-path.txt");
        var preparedPath = preparedCorpusDirectory ?? File.ReadAllText(preparedPathFile).Trim();
        if (!EvaluationOutput.TryResolveDirectory(preparedPath, out var preparedDirectory)
            || preparedDirectory is null)
        {
            throw new InvalidDataException("evaluation.corpus.prepared-path-invalid");
        }

        var preparedCorpus = EvaluationManifestLoader.Load(preparedDirectory);
        if (preparedCorpus.CorpusIdentity != loaded.CorpusIdentity)
        {
            throw new InvalidDataException("evaluation.corpus.prepared-mismatch");
        }

        await using var repository = await EvaluationRepositorySession.CreateAsync(
            preparedCorpus,
            adapter,
            cancellationToken).ConfigureAwait(false);
        foreach (var scenario in selected)
        {
            try
            {
                var prepared = repository.Prepare(loaded, scenario);
                var baseExchange = liveExchangeFactory?.Invoke(prepared)
                    ?? new ScriptedEvaluationExchange(prepared);
                var observing = new CostObservingExchange(baseExchange, options.CostPolicy);
                var outcome = await adapter.ExecuteAsync(
                    prepared.SelectedAudit,
                    prepared.RequestBytes,
                    prepared.AttemptId,
                    RuntimeOptions(),
                    observing,
                    cancellationToken).ConfigureAwait(false);
                reports.Add(CreateCaseReport(prepared, outcome, observing.Observations));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                reports.Add(FailedCase(scenario, "cancelled", "evaluation.case.cancelled"));
                await PersistAsync(
                    EvaluationReport.Create(loaded, options, reports, selected.Length, false, Elapsed(stopwatch)),
                    CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
            {
                reports.Add(FailedCase(
                    scenario,
                    "failed",
                    SafeCaseCode(exception)));
            }

            await PersistAsync(
                EvaluationReport.Create(loaded, options, reports, selected.Length, false, Elapsed(stopwatch)),
                cancellationToken).ConfigureAwait(false);
        }

        stopwatch?.Stop();
        return EvaluationReport.Create(
            loaded,
            options,
            reports,
            selected.Length,
            true,
            Elapsed(stopwatch));
    }

    private async Task PersistAsync(EvaluationReport report, CancellationToken cancellationToken)
    {
        if (outputDirectory is null)
        {
            return;
        }

        var bytes = EvaluationReportWriter.Serialize(
            report,
            credentialMarker,
            loaded.CorpusDirectory,
            outputDirectory);
        await EvaluationOutput.WriteAtomicAsync(
            outputDirectory,
            report.Status == "complete" ? "evaluation-report.json" : "evaluation-partial.json",
            bytes,
            cancellationToken).ConfigureAwait(false);
    }

    private DocumentationScribeRuntimeOptions RuntimeOptions() => options.IsLive
        ? new DocumentationScribeRuntimeOptions(
            "provider.openai.v1",
            "model.gpt-4-1-mini-2025-04-14",
            "scribe-protocol.v1")
        : new DocumentationScribeRuntimeOptions(
            "provider.synthetic.v1",
            "model.synthetic.v1",
            "scribe-protocol.v1");

    private static EvaluationCaseReport CreateCaseReport(
        PreparedEvaluationCase prepared,
        EvaluationCompositionOutcome outcome,
        IReadOnlyList<EvaluationCostObservation> costObservations)
    {
        var status = Status(outcome.Status);
        var envelope = outcome.RunResult?.RunEnvelope;
        var cost = AggregateCost(costObservations);
        return new EvaluationCaseReport(
            prepared.Scenario.Id,
            status,
            outcome.Code,
            prepared.Scenario.ExpectedStatuses.Contains(status, StringComparer.Ordinal),
            envelope?.AttemptNumber ?? 0,
            envelope?.ProviderRequestCount ?? 0,
            envelope?.ToolRoundCount ?? 0,
            envelope?.ToolCallCount ?? 0,
            envelope?.Usage is { } usage
                ? new EvaluationUsageReport(
                    usage.InputTokens,
                    usage.OutputTokens,
                    usage.CachedInputTokens,
                    usage.UncachedInputTokens,
                    usage.ReasoningTokens,
                    envelope.Cache is { } cache ? DocumentationScribeVocabulary.GetId(cache) : null)
                : null,
            cost,
            EvaluationReportWriter.ProjectProposal(
                outcome.RunResult,
                status,
                prepared.Scenario.ProposalLine),
            "passed",
            prepared.Scenario.Coverage.Order(StringComparer.Ordinal).ToArray());
    }

    private static EvaluationCaseReport FailedCase(
        EvaluationScenario scenario,
        string status,
        string code) => new(
        scenario.Id,
        status,
        code,
        scenario.ExpectedStatuses.Contains(status, StringComparer.Ordinal),
        0,
        0,
        0,
        0,
        null,
        new EvaluationCostReport("not-reported", null, null),
        null,
        "passed",
        scenario.Coverage.Order(StringComparer.Ordinal).ToArray());

    private static EvaluationCostReport AggregateCost(
        IReadOnlyList<EvaluationCostObservation> observations)
    {
        if (observations.Count == 0
            || observations.All(item => item.Result.Completeness == EvaluationCostCompleteness.NotReported))
        {
            return new EvaluationCostReport("not-reported", null, null);
        }

        var priced = observations.Where(item => item.Result.AmountMicrounits is not null).ToArray();
        var currencies = priced.Select(item => item.Result.CurrencyId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (currencies.Length != 1)
        {
            return new EvaluationCostReport("not-reported", null, null);
        }

        var amount = priced.Aggregate(
            0L,
            (sum, item) => checked(sum + item.Result.AmountMicrounits!.Value));
        var status = observations.All(item =>
            item.Result.Completeness == EvaluationCostCompleteness.Complete)
                ? "complete"
                : "partial";
        return new EvaluationCostReport(status, currencies[0], amount);
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
