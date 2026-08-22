using System.Security.Cryptography;
using System.Text;
using ContractScribe.Agent.Runtime;
using ContractScribe.Core;

namespace ContractScribe.Evaluation;

internal enum EvaluationCostCompleteness
{
    Complete,
    Partial,
    NotReported,
}

internal sealed record EvaluationCostResult(
    EvaluationCostCompleteness Completeness,
    string? CurrencyId,
    long? AmountMicrounits);

internal sealed record EvaluationCostPolicy(
    string CurrencyId,
    long CachedInputRate,
    long UncachedInputRate,
    long OutputRate)
{
    internal const long MaximumRate = 1_000_000_000_000;

    internal static EvaluationCostPolicy Unpriced { get; } = new("cost.unpriced", 0, 0, 0);

    internal string Identity
    {
        get
        {
            var value = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{CurrencyId}|{CachedInputRate}|{UncachedInputRate}|{OutputRate}");
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
                .ToLowerInvariant();
        }
    }

    internal bool IsPriced => !ReferenceEquals(this, Unpriced)
        && !string.Equals(CurrencyId, Unpriced.CurrencyId, StringComparison.Ordinal);

    internal static bool TryCreate(
        string currencyId,
        long cachedInputRate,
        long uncachedInputRate,
        long outputRate,
        out EvaluationCostPolicy? policy)
    {
        policy = null;
        if (string.IsNullOrEmpty(currencyId)
            || currencyId.Length > 64
            || currencyId[0] is not (>= 'a' and <= 'z')
            || currencyId[^1] is ('.' or '-')
            || currencyId.Any(character => character is not (>= 'a' and <= 'z')
                && character is not (>= '0' and <= '9')
                && character is not ('.' or '-'))
            || cachedInputRate is < 0 or > MaximumRate
            || uncachedInputRate is < 0 or > MaximumRate
            || outputRate is < 0 or > MaximumRate)
        {
            return false;
        }

        policy = new EvaluationCostPolicy(
            currencyId,
            cachedInputRate,
            uncachedInputRate,
            outputRate);
        return true;
    }

    internal EvaluationCostResult Calculate(DocumentationScribeModelUsage? usage)
    {
        if (!IsPriced || usage is null)
        {
            return new EvaluationCostResult(EvaluationCostCompleteness.NotReported, null, null);
        }

        var input = usage.InputTokens;
        var cached = usage.CachedInputTokens;
        var uncached = usage.UncachedInputTokens;
        if (input is { } inputTotal
            && (cached > inputTotal
                || uncached > inputTotal
                || cached is { } cachedValue && uncached is { } uncachedValue
                    && (long)cachedValue + uncachedValue > inputTotal))
        {
            throw new ArgumentException("evaluation.cost.usage-contradiction", nameof(usage));
        }

        long pricedCached = 0;
        long pricedUncached = 0;
        var inputComplete = false;
        var hasInputObservation = false;
        if (input is { } total)
        {
            hasInputObservation = true;
            inputComplete = true;
            if (cached is { } cachedTokens && uncached is { } uncachedTokens)
            {
                pricedCached = cachedTokens;
                pricedUncached = total - cachedTokens;
            }
            else if (cached is { } cachedOnly)
            {
                pricedCached = cachedOnly;
                pricedUncached = total - cachedOnly;
            }
            else
            {
                pricedUncached = total;
            }
        }
        else if (cached is { } cachedTokens && uncached is { } uncachedTokens)
        {
            hasInputObservation = true;
            inputComplete = true;
            pricedCached = cachedTokens;
            pricedUncached = uncachedTokens;
        }
        else if (cached is { } cachedOnly)
        {
            hasInputObservation = true;
            pricedCached = cachedOnly;
        }
        else if (uncached is { } uncachedOnly)
        {
            hasInputObservation = true;
            pricedUncached = uncachedOnly;
        }

        var hasOutput = usage.OutputTokens is not null;
        if (!hasInputObservation && !hasOutput)
        {
            return new EvaluationCostResult(EvaluationCostCompleteness.NotReported, null, null);
        }

        var numerator = checked(
            (Int128)pricedCached * CachedInputRate
            + (Int128)pricedUncached * UncachedInputRate
            + (Int128)(usage.OutputTokens ?? 0) * OutputRate);
        var rounded = checked((numerator + 999_999) / 1_000_000);
        if (rounded > DocumentationScribeContract.MaximumObservedCostMicrounits)
        {
            throw new OverflowException("evaluation.cost.maximum-exceeded");
        }

        return new EvaluationCostResult(
            inputComplete && hasOutput
                ? EvaluationCostCompleteness.Complete
                : EvaluationCostCompleteness.Partial,
            CurrencyId,
            checked((long)rounded));
    }

    public override string ToString() => nameof(EvaluationCostPolicy);
}

internal sealed record EvaluationProviderObservation(
    int ProviderRequestNumber,
    EvaluationCostResult Cost,
    bool ResponseAccepted,
    bool OrdinaryToolCallObserved,
    bool TerminalSubmissionObserved,
    bool UsageSupplied,
    bool CacheSupplied,
    bool ToolResultContinuationRequired,
    DocumentationScribeModelFailureCode? FailureCode,
    DocumentationScribeModelFailureOrigin? FailureOrigin,
    int? HttpStatusCode,
    DocumentationScribeContinuationObservation ContinuationObservation,
    int OrdinaryToolCallCount = 0);

internal sealed class CostObservingExchange : IDocumentationScribeModelExchange
{
    private readonly IDocumentationScribeModelExchange inner;
    private readonly EvaluationCostPolicy policy;
    private readonly List<EvaluationProviderObservation> observations = [];

    internal CostObservingExchange(
        IDocumentationScribeModelExchange inner,
        EvaluationCostPolicy policy)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    internal IReadOnlyList<EvaluationProviderObservation> Observations => observations;

    public async ValueTask<DocumentationScribeModelResponse> SendAsync(
        DocumentationScribeModelRequest request,
        CancellationToken cancellationToken)
    {
        var response = await inner.SendAsync(request, cancellationToken).ConfigureAwait(false);
        EvaluationCostResult result;
        try
        {
            result = policy.Calculate(response.Usage);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            result = new EvaluationCostResult(EvaluationCostCompleteness.NotReported, null, null);
            response = new DocumentationScribeModelResponse(
                [],
                [],
                new DocumentationScribeModelFailure(DocumentationScribeModelFailureCode.MalformedResponse));
        }

        observations.Add(new EvaluationProviderObservation(
            request.ProviderRequestNumber,
            result,
            response.Failure is null,
            !response.ToolCalls.IsEmpty,
            !response.TerminalSubmissions.IsEmpty,
            response.Usage is not null,
            response.Cache is not null
                || response.Usage?.CachedInputTokens is not null
                || response.Usage?.UncachedInputTokens is not null,
            !request.CompletedToolExchanges.IsEmpty,
            response.Failure?.Code,
            response.Failure?.Origin,
            response.Failure?.HttpStatusCode,
            response.ContinuationObservation,
            response.ToolCalls.Length));
        var completeCost = result.Completeness == EvaluationCostCompleteness.Complete
            && result.AmountMicrounits is { } amount
            && result.CurrencyId is { } currency
                ? new DocumentationScribeModelCost(currency, amount)
                : null;
        return response.WithCost(completeCost);
    }

    public override string ToString() => nameof(CostObservingExchange);
}
