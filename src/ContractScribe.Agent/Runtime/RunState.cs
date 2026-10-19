using System.Collections.Immutable;
using System.Text;
using ContractScribe.Agent.Prompting;
using ContractScribe.Core;

namespace ContractScribe.Agent.Runtime;

internal sealed class RunState
{
    private readonly DocumentationScribeRuntimeOptions options;
    private readonly TimeProvider timeProvider;
    private readonly long startedAt;
    private long? inputTokens;
    private long? outputTokens;
    private long? cachedInputTokens;
    private long? uncachedInputTokens;
    private long? reasoningTokens;
    private long? costMicrounits;
    private string? currencyId;
    private DocumentationScribeCacheObservation? cache;
    private bool arithmeticOverflow;
    private int inputReports, outputReports, cachedReports, uncachedReports, reasoningReports, costReports, cacheReports;

    internal RunState(
        DocumentationScribeRequest request,
        DocumentationScribeAttemptId attemptId,
        DocumentationScribeRuntimeOptions options,
        DocumentationScribeToolRegistry registry,
        TimeProvider timeProvider, DocumentationScribeExecutionScope? scope = null)
    {
        Scope = scope ?? DocumentationScribeExecutionScope.ForStandalone(request, timeProvider);
        Request = request;
        AttemptId = attemptId;
        this.options = options;
        this.timeProvider = timeProvider;
        startedAt = timeProvider.GetTimestamp();
        AttemptNumber = checked(Scope.RestoredRetryableProviderFailures + 1);
        PerOperationToolCalls = new int[registry.Registrations.Length];
        EvidenceItemCount = request.EvidenceReferences.Length;
        try
        {
            EvidenceUtf8ByteCount = request.EvidenceReferences.Aggregate(
                0L,
                (total, reference) => checked(total + reference.IncludedUtf8ByteCount));
        }
        catch (OverflowException)
        {
            arithmeticOverflow = true;
        }
    }

    internal ImmutableDictionary<string, DocumentationScribeTargetSubmission> Submissions { get; set; } =
        ImmutableDictionary.Create<string, DocumentationScribeTargetSubmission>(StringComparer.Ordinal);
    internal ImmutableDictionary<string, DocumentationScribeTargetFailure> TargetFailures { get; set; } =
        ImmutableDictionary.Create<string, DocumentationScribeTargetFailure>(StringComparer.Ordinal);
    internal ImmutableHashSet<string> RestoredClosedTargets { get; set; } = ImmutableHashSet.Create<string>(StringComparer.Ordinal);
    internal CancellationToken CallerCancellation { get; set; }
    internal ImmutableHashSet<string> AttemptedTargets { get; set; } = ImmutableHashSet.Create<string>(StringComparer.Ordinal);
    internal int RepairCount { get; set; }
    internal int NextSequence { get; set; } = 1;

    internal DocumentationScribeRunResult CompleteResult(DocumentationScribeRunResult result) =>
        DocumentationScribeValidation.WithProgress(Request, result, Submissions.Values.ToImmutableArray(),
            TargetFailures.Values.ToImmutableArray(), RepairCount, NextSequence);

    internal DocumentationScribeRunResult CreateFinished(bool contextStop = false) =>
        DocumentationScribeValidation.CreateFinishedResult(Request, AttemptId,
            CreateEnvelope([], ElapsedMilliseconds), Submissions.Values.ToImmutableArray(),
            TargetFailures.Values.ToImmutableArray(), RepairCount, NextSequence, contextStop);

    internal DocumentationScribeExecutionScope Scope { get; }

    internal DocumentationScribeRequest Request { get; }

    internal DocumentationScribeAttemptId AttemptId { get; }

    internal int AttemptNumber { get; set; }

    internal int ProviderRequestCount { get; set; }

    internal int ToolRoundCount { get; set; }

    internal int ToolCallCount { get; set; }

    internal int[] PerOperationToolCalls { get; }

    internal long EvidenceItemCount { get; set; }

    internal long EvidenceUtf8ByteCount { get; set; }

    internal long SuccessfulToolExchangeUtf8ByteCount { get; set; }

    internal ImmutableArray<DocumentationScribeEvidenceReference> ActiveDynamicEvidenceReferences { get; set; } = [];

    internal ImmutableDictionary<string, DocumentationScribeEvidenceReference> ChargedDynamicEvidenceById { get; set; } =
        ImmutableDictionary.Create<string, DocumentationScribeEvidenceReference>(StringComparer.Ordinal);

    internal ImmutableArray<DocumentationScribeCompletedToolExchange> CompletedToolExchanges { get; set; } = [];

    internal bool TryChargeDynamicEvidence(DocumentationScribeEvidenceReference reference)
    {
        try
        {
            var itemCount = checked(EvidenceItemCount + 1);
            var byteCount = checked(EvidenceUtf8ByteCount + reference.IncludedUtf8ByteCount);
            ChargedDynamicEvidenceById = ChargedDynamicEvidenceById.Add(
                reference.EvidenceReferenceId,
                reference);
            EvidenceItemCount = itemCount;
            EvidenceUtf8ByteCount = byteCount;
            return true;
        }
        catch (OverflowException)
        {
            arithmeticOverflow = true;
            return false;
        }
    }

    internal bool TryChargeSuccessfulToolExchange(int utf8ByteCount)
    {
        try
        {
            SuccessfulToolExchangeUtf8ByteCount = checked(
                SuccessfulToolExchangeUtf8ByteCount + utf8ByteCount);
            return true;
        }
        catch (OverflowException)
        {
            arithmeticOverflow = true;
            return false;
        }
    }

    internal bool IsEvidenceBudgetExceeded => arithmeticOverflow
        || EvidenceItemCount > Request.Limits.MaximumEvidenceReferences
        || EvidenceUtf8ByteCount > Request.Limits.MaximumEvidenceUtf8Bytes
        || SuccessfulToolExchangeUtf8ByteCount > 33_554_432;

    internal int ElapsedMilliseconds
    {
        get
        {
            var elapsed = timeProvider.GetElapsedTime(startedAt).TotalMilliseconds;
            return (int)Math.Clamp(
                Math.Ceiling(elapsed),
                0,
                DocumentationScribeContract.MaximumObservedElapsedMilliseconds);
        }
    }

    internal int RemainingMilliseconds => Math.Max(
        0,
        Math.Min(Request.Limits.MaximumElapsedMilliseconds - ElapsedMilliseconds, Scope.Allowance.RemainingMilliseconds));

    internal bool IsObservedBudgetExceeded => Scope.Allowance.HasCheckedStop || Scope.LifetimeDeadlineReached || arithmeticOverflow
        || HasUnrepresentableObservation
        || EvidenceItemCount > Request.Limits.MaximumEvidenceReferences
        || EvidenceUtf8ByteCount > Request.Limits.MaximumEvidenceUtf8Bytes
        || SuccessfulToolExchangeUtf8ByteCount > 33_554_432
        || inputTokens > Request.Limits.MaximumInputTokens
        || outputTokens > Request.Limits.MaximumOutputTokens
        || cachedInputTokens > Request.Limits.MaximumInputTokens
        || uncachedInputTokens > Request.Limits.MaximumUncachedInputTokens
        || reasoningTokens > Request.Limits.MaximumOutputTokens
        || costMicrounits > Request.Limits.MaximumCostMicrounits;

    internal bool CanStartAdditionalModelWork => Scope.Allowance.CanBeginProviderWork()
        && ToolCallCount < Request.Limits.MaximumToolCalls && Scope.Allowance.RemainingToolCalls > 0 && !arithmeticOverflow
        && !HasUnrepresentableObservation
        && (inputTokens is null || inputTokens < Request.Limits.MaximumInputTokens)
        && (outputTokens is null || outputTokens < Request.Limits.MaximumOutputTokens)
        && (cachedInputTokens is null || cachedInputTokens < Request.Limits.MaximumInputTokens)
        && (uncachedInputTokens is null || uncachedInputTokens < Request.Limits.MaximumUncachedInputTokens)
        && (reasoningTokens is null || reasoningTokens < Request.Limits.MaximumOutputTokens)
        && (costMicrounits is null || costMicrounits < Request.Limits.MaximumCostMicrounits);

    internal int RemainingOutputTokens => Math.Min(Scope.Allowance.RemainingOutputTokens, outputTokens is null
        ? Request.Limits.MaximumOutputTokens
        : (int)Math.Max(0, Request.Limits.MaximumOutputTokens - outputTokens.Value));

    private bool HasUnrepresentableObservation => inputTokens > DocumentationScribeContract.MaximumObservedInputTokens
        || outputTokens > DocumentationScribeContract.MaximumObservedOutputTokens
        || cachedInputTokens > DocumentationScribeContract.MaximumObservedInputTokens
        || uncachedInputTokens > DocumentationScribeContract.MaximumObservedInputTokens
        || reasoningTokens > DocumentationScribeContract.MaximumObservedOutputTokens
        || costMicrounits > DocumentationScribeContract.MaximumObservedCostMicrounits;

    internal bool TryApplyObservations(DocumentationScribeModelResponse response)
    {
        try
        {
            if (response.Usage is { } usage)
            {
                if (usage.InputTokens is not null) inputReports++;
                if (usage.OutputTokens is not null) outputReports++;
                if (usage.CachedInputTokens is not null) cachedReports++;
                if (usage.UncachedInputTokens is not null) uncachedReports++;
                if (usage.ReasoningTokens is not null) reasoningReports++;
                Add(ref inputTokens, usage.InputTokens);
                Add(ref outputTokens, usage.OutputTokens);
                Add(ref cachedInputTokens, usage.CachedInputTokens);
                Add(ref uncachedInputTokens, usage.UncachedInputTokens);
                Add(ref reasoningTokens, usage.ReasoningTokens);
            }

            if (response.Cost is { } cost)
            {
                if (currencyId is not null
                    && !string.Equals(currencyId, cost.CurrencyId, StringComparison.Ordinal))
                {
                    return false;
                }

                costReports++;
                currencyId ??= cost.CurrencyId;
                Add(ref costMicrounits, cost.AmountMicrounits);
            }

            if (response.Cache is { } reported)
            {
                if (reported != DocumentationScribeCacheObservation.NotReported) cacheReports++;
                cache = cache is null || cache == reported
                    ? reported
                    : DocumentationScribeCacheObservation.Mixed;
            }

            return true;
        }
        catch (OverflowException)
        {
            arithmeticOverflow = true;
            return true;
        }
    }

    internal DocumentationScribeRunResult CreateCancelled(int elapsedMilliseconds) => CompleteResult(DocumentationScribeValidation.CreateCancelledResult(
        Request,
        AttemptId,
        DocumentationScribeCancellationCode.Caller,
        CreateEnvelope(
            ImmutableArray<DocumentationScribeDiagnosticInput>.Empty,
            elapsedMilliseconds,
            allowObservedOverrun: true)));

    internal DocumentationScribeRunResult CreateFailure(
        DocumentationScribeFailureCode code,
        int elapsedMilliseconds) => CompleteResult(DocumentationScribeValidation.CreateFailureResult(
            Request,
            AttemptId,
            code,
            CreateEnvelope(
                ImmutableArray<DocumentationScribeDiagnosticInput>.Empty,
                elapsedMilliseconds,
                allowObservedOverrun: code is DocumentationScribeFailureCode.Budget
                    or DocumentationScribeFailureCode.Timeout)));

    internal DocumentationScribeRunResult CreateProviderFailure(
        DocumentationScribeProviderFinalDisposition providerFinalDisposition,
        int elapsedMilliseconds) => CompleteResult(DocumentationScribeValidation.CreateFailureResult(
        Request,
        AttemptId,
        DocumentationScribeFailureCode.Provider,
        CreateEnvelope(
            [new DocumentationScribeDiagnosticInput("scribe.diagnostic.provider-failure", "provider")],
            elapsedMilliseconds,
            allowObservedOverrun: false),
        providerFinalDisposition));

    internal DocumentationScribeRunResult CreateToolProtocolFailure(
        string referenceId,
        int elapsedMilliseconds) => CompleteResult(DocumentationScribeValidation.CreateFailureResult(
            Request,
            AttemptId,
            DocumentationScribeFailureCode.ToolProtocol,
            CreateEnvelope(
                [new DocumentationScribeDiagnosticInput(
                    "scribe.diagnostic.tool-failure",
                    "tool",
                    DocumentationScribeBoundary.ValidateIdentifier(referenceId, nameof(referenceId)))],
                elapsedMilliseconds,
                allowObservedOverrun: false)));

    internal DocumentationScribeRunResult CreateValidationFailure(
        string validationCode,
        int elapsedMilliseconds) => CompleteResult(DocumentationScribeValidation.CreateFailureResult(
            Request,
            AttemptId,
            DocumentationScribeFailureCode.Validation,
            CreateEnvelope(
                [new DocumentationScribeDiagnosticInput(
                    "scribe.diagnostic.result-rejected",
                    "result",
                    ValidationCode: DocumentationScribeBoundary.ValidateIdentifier(
                        validationCode,
                        nameof(validationCode)))],
                elapsedMilliseconds,
                allowObservedOverrun: false)));

    internal DocumentationScribeRunResult CreateInternalFailure(int elapsedMilliseconds) => CompleteResult(DocumentationScribeValidation.CreateFailureResult(
        Request,
        AttemptId,
        DocumentationScribeFailureCode.Internal,
        CreateEnvelope(
            [new DocumentationScribeDiagnosticInput("scribe.diagnostic.runtime-failure", "runtime")],
            elapsedMilliseconds,
            allowObservedOverrun: false)));

    internal DocumentationScribeRunEnvelopeInput CreateEnvelope(
        ImmutableArray<DocumentationScribeDiagnosticInput> diagnostics,
        int elapsedMilliseconds,
        bool allowObservedOverrun = false)
    {
        var usage = CreateUsage(allowObservedOverrun);
        var cost = CreateCost(allowObservedOverrun);
        return new DocumentationScribeRunEnvelopeInput(
            options.ProviderConfigurationId,
            options.ModelConfigurationId,
            options.ScribeProtocolId,
            AttemptNumber,
            ProviderRequestCount,
            ToolRoundCount,
            ToolCallCount,
            elapsedMilliseconds,
            usage,
            cache,
            cost,
            diagnostics)
        {
            RestoredRetryableProviderFailures = Scope.RestoredRetryableProviderFailures,
            UsageCoverage = new(inputReports, outputReports, cachedReports, uncachedReports, reasoningReports, costReports, cacheReports)
        };
    }

    private DocumentationScribeUsageObservationInput? CreateUsage(bool allowObservedOverrun)
    {
        if (inputTokens is null
            && outputTokens is null
            && cachedInputTokens is null
            && uncachedInputTokens is null
            && reasoningTokens is null)
        {
            return null;
        }

        if (!allowObservedOverrun && (inputTokens > Request.Limits.MaximumInputTokens
            || outputTokens > Request.Limits.MaximumOutputTokens
            || cachedInputTokens > Request.Limits.MaximumInputTokens
            || uncachedInputTokens > Request.Limits.MaximumUncachedInputTokens
            || reasoningTokens > Request.Limits.MaximumOutputTokens))
        {
            return null;
        }

        var representableInput = Representable(inputTokens, DocumentationScribeContract.MaximumObservedInputTokens);
        var representableOutput = Representable(outputTokens, DocumentationScribeContract.MaximumObservedOutputTokens);
        var representableCached = Representable(cachedInputTokens, DocumentationScribeContract.MaximumObservedInputTokens);
        var representableUncached = Representable(uncachedInputTokens, DocumentationScribeContract.MaximumObservedInputTokens);
        var representableReasoning = Representable(reasoningTokens, DocumentationScribeContract.MaximumObservedOutputTokens);
        return representableInput is null
            && representableOutput is null
            && representableCached is null
            && representableUncached is null
            && representableReasoning is null
                ? null
                : new DocumentationScribeUsageObservationInput(
                    representableInput,
                    representableOutput,
                    representableCached,
                    representableUncached,
                    representableReasoning);
    }

    private DocumentationScribeCostObservationInput? CreateCost(bool allowObservedOverrun)
    {
        if (currencyId is null
            || costMicrounits is null
            || costMicrounits > DocumentationScribeContract.MaximumObservedCostMicrounits)
        {
            return null;
        }

        if (!allowObservedOverrun && costMicrounits > Request.Limits.MaximumCostMicrounits)
        {
            return null;
        }

        return new DocumentationScribeCostObservationInput(
            currencyId,
            costMicrounits.Value);
    }

    private static void Add(ref long? total, int? delta)
    {
        if (delta is { } value)
        {
            total = checked((total ?? 0) + value);
        }
    }

    private static void Add(ref long? total, long delta) => total = checked((total ?? 0) + delta);

    private static int? Representable(long? value, int maximum) => value is null || value > maximum
        ? null
        : (int)value.Value;
}
