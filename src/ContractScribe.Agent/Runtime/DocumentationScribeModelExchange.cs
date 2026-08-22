using System.Buffers;
using System.Collections.Immutable;
using System.Text.Json;
using ContractScribe.Core;

namespace ContractScribe.Agent.Runtime;

public enum DocumentationScribeMessageKind
{
    SystemPolicy,
    RunPolicy,
    RepositoryInstructions,
    MaintainedContext,
    TargetEvidence,
}

public sealed class DocumentationScribeModelMessage
{
    internal DocumentationScribeModelMessage(DocumentationScribeMessageKind kind, string content)
    {
        Kind = kind;
        Content = content;
    }

    public DocumentationScribeMessageKind Kind { get; }

    public string Content { get; }

    public override string ToString() => nameof(DocumentationScribeModelMessage);
}

public sealed class DocumentationScribeModelToolDefinition
{
    internal DocumentationScribeModelToolDefinition(
        string operationId,
        string description,
        string inputSchemaJson)
    {
        OperationId = operationId;
        Description = description;
        InputSchemaJson = inputSchemaJson;
    }

    public string OperationId { get; }

    public string Description { get; }

    public string InputSchemaJson { get; }

    public override string ToString() => nameof(DocumentationScribeModelToolDefinition);
}

public sealed class DocumentationScribeTerminalDefinition
{
    internal DocumentationScribeTerminalDefinition(string operationId, string schemaJson)
    {
        OperationId = operationId;
        SchemaJson = schemaJson;
    }

    public string OperationId { get; }

    public string SchemaJson { get; }

    public override string ToString() => nameof(DocumentationScribeTerminalDefinition);
}

public sealed class DocumentationScribeModelOutputLimits
{
    internal DocumentationScribeModelOutputLimits(
        int maximumToolCalls,
        int maximumToolArgumentUtf8Bytes,
        int maximumTerminalUtf8Bytes,
        int maximumOutputTokens,
        int maximumNormalizedResponseUtf8Bytes)
    {
        MaximumToolCalls = maximumToolCalls;
        MaximumToolArgumentUtf8Bytes = maximumToolArgumentUtf8Bytes;
        MaximumTerminalUtf8Bytes = maximumTerminalUtf8Bytes;
        MaximumOutputTokens = maximumOutputTokens;
        MaximumNormalizedResponseUtf8Bytes = maximumNormalizedResponseUtf8Bytes;
    }

    public int MaximumToolCalls { get; }

    public int MaximumToolArgumentUtf8Bytes { get; }

    public int MaximumTerminalUtf8Bytes { get; }

    public int MaximumOutputTokens { get; }

    public int MaximumNormalizedResponseUtf8Bytes { get; }

    public override string ToString() => nameof(DocumentationScribeModelOutputLimits);
}

public sealed class DocumentationScribeCompletedToolExchange
{
    internal DocumentationScribeCompletedToolExchange(
        int responseIndex,
        string callId,
        string operationId,
        ImmutableArray<byte> argumentsUtf8Json,
        string outcomeId,
        ImmutableArray<byte> resultUtf8Json,
        ImmutableArray<DocumentationScribeEvidenceReference> evidenceReferences,
        DocumentationScribeAssistantContinuation? assistantContinuation = null)
    {
        if (assistantContinuation is not null && responseIndex != 0)
        {
            throw new ArgumentException("Assistant continuation must be anchored exactly once per response round.");
        }

        ResponseIndex = responseIndex;
        CallId = callId;
        OperationId = operationId;
        ArgumentsUtf8JsonStorage = argumentsUtf8Json;
        OutcomeId = outcomeId;
        ResultUtf8JsonStorage = resultUtf8Json;
        EvidenceReferences = evidenceReferences;
        AssistantContinuation = assistantContinuation;
    }

    public int ResponseIndex { get; }

    public string CallId { get; }

    public string OperationId { get; }

    public ReadOnlyMemory<byte> ArgumentsUtf8Json => ArgumentsUtf8JsonStorage.AsMemory();

    public string OutcomeId { get; }

    public ReadOnlyMemory<byte> ResultUtf8Json => ResultUtf8JsonStorage.AsMemory();

    public ImmutableArray<DocumentationScribeEvidenceReference> EvidenceReferences { get; }

    internal ImmutableArray<byte> ArgumentsUtf8JsonStorage { get; }

    internal ImmutableArray<byte> ResultUtf8JsonStorage { get; }

    internal DocumentationScribeAssistantContinuation? AssistantContinuation { get; }

    public override string ToString() => nameof(DocumentationScribeCompletedToolExchange);
}

internal sealed class DocumentationScribeAssistantContinuation
{
    internal DocumentationScribeAssistantContinuation(string content, string? reasoningContent)
        : this(contentPresent: true, content, reasoningContent)
    {
    }

    internal DocumentationScribeAssistantContinuation(
        bool contentPresent,
        string? content,
        string? reasoningContent)
    {
        if (!contentPresent && content is not null)
        {
            throw new ArgumentException("Absent assistant content cannot carry text.", nameof(content));
        }

        ContentPresent = contentPresent;
        Content = content is null
            ? null
            : DocumentationScribeBoundary.ValidateCommittedText(
                content,
                nameof(content),
                DocumentationScribeBoundary.MaximumNormalizedResponseUtf8Bytes);
        ReasoningContent = reasoningContent is null
            ? null
            : DocumentationScribeBoundary.ValidateCommittedText(
                reasoningContent,
                nameof(reasoningContent),
                DocumentationScribeBoundary.MaximumNormalizedResponseUtf8Bytes);
    }

    internal bool ContentPresent { get; }

    internal string? Content { get; }

    internal string? ReasoningContent { get; }

    public override string ToString() => nameof(DocumentationScribeAssistantContinuation);
}

public sealed class DocumentationScribeModelRequest
{
    internal DocumentationScribeModelRequest(
        int attemptNumber,
        int providerRequestNumber,
        ImmutableArray<DocumentationScribeModelMessage> messages,
        ImmutableArray<DocumentationScribeModelToolDefinition> tools,
        DocumentationScribeTerminalDefinition terminal,
        ImmutableArray<DocumentationScribeCompletedToolExchange> completedToolExchanges,
        DocumentationScribeModelOutputLimits outputLimits,
        ImmutableArray<byte> deterministicUtf8)
    {
        AttemptNumber = attemptNumber;
        ProviderRequestNumber = providerRequestNumber;
        Messages = messages;
        Tools = tools;
        Terminal = terminal;
        CompletedToolExchanges = completedToolExchanges;
        OutputLimits = outputLimits;
        DeterministicUtf8 = deterministicUtf8;
    }

    public int AttemptNumber { get; }

    public int ProviderRequestNumber { get; }

    public ImmutableArray<DocumentationScribeModelMessage> Messages { get; }

    public ImmutableArray<DocumentationScribeModelToolDefinition> Tools { get; }

    public DocumentationScribeTerminalDefinition Terminal { get; }

    public ImmutableArray<DocumentationScribeCompletedToolExchange> CompletedToolExchanges { get; }

    public DocumentationScribeModelOutputLimits OutputLimits { get; }

    internal ImmutableArray<byte> DeterministicUtf8 { get; }

    public override string ToString() => nameof(DocumentationScribeModelRequest);
}

public interface IDocumentationScribeModelExchange
{
    ValueTask<DocumentationScribeModelResponse> SendAsync(
        DocumentationScribeModelRequest request,
        CancellationToken cancellationToken);
}

public sealed class DocumentationScribeModelToolCall
{
    public DocumentationScribeModelToolCall(
        int responseIndex,
        string callId,
        string operationId,
        ReadOnlyMemory<byte> argumentsUtf8Json)
    {
        if (responseIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(responseIndex));
        }

        ResponseIndex = responseIndex;
        CallId = DocumentationScribeBoundary.ValidateCorrelationId(callId, nameof(callId));
        OperationId = DocumentationScribeBoundary.ValidateIdentifier(operationId, nameof(operationId));
        ArgumentsUtf8JsonStorage = DocumentationScribeBoundary.ValidateJson(
            argumentsUtf8Json,
            nameof(argumentsUtf8Json),
            DocumentationScribeContract.MaximumArtifactUtf8Bytes);
    }

    public int ResponseIndex { get; }

    public string CallId { get; }

    public string OperationId { get; }

    public ReadOnlyMemory<byte> ArgumentsUtf8Json => ArgumentsUtf8JsonStorage.AsMemory();

    internal ImmutableArray<byte> ArgumentsUtf8JsonStorage { get; }

    public override string ToString() => nameof(DocumentationScribeModelToolCall);
}

public sealed class DocumentationScribeModelTerminalSubmission
{
    public DocumentationScribeModelTerminalSubmission(ReadOnlyMemory<byte> terminalUtf8Json)
    {
        TerminalUtf8JsonStorage = DocumentationScribeBoundary.ValidateJson(
            terminalUtf8Json,
            nameof(terminalUtf8Json),
            DocumentationScribeContract.MaximumArtifactUtf8Bytes);
    }

    public ReadOnlyMemory<byte> TerminalUtf8Json => TerminalUtf8JsonStorage.AsMemory();

    internal ImmutableArray<byte> TerminalUtf8JsonStorage { get; }

    public override string ToString() => nameof(DocumentationScribeModelTerminalSubmission);
}

public enum DocumentationScribeModelFailureCode
{
    TransientUnavailable,
    RateLimited,
    PermanentUnavailable,
    Authentication,
    Unsupported,
    MalformedResponse,
}

public enum DocumentationScribeModelFailureOrigin
{
    RequestPreparation,
    HttpStatus,
    Transport,
    SuccessfulResponse,
    ResponseCodec,
}

public sealed class DocumentationScribeModelFailure
{
    public DocumentationScribeModelFailure(
        DocumentationScribeModelFailureCode code,
        int? retryAfterMilliseconds = null,
        DocumentationScribeModelFailureOrigin? origin = null,
        int? httpStatusCode = null)
    {
        if (!Enum.IsDefined(code))
        {
            throw new ArgumentOutOfRangeException(nameof(code));
        }

        if (origin is { } presentOrigin && !Enum.IsDefined(presentOrigin))
        {
            throw new ArgumentOutOfRangeException(nameof(origin));
        }

        if (retryAfterMilliseconds is < 0 or > DocumentationScribeBoundary.MaximumRetryHintMilliseconds)
        {
            throw new ArgumentOutOfRangeException(nameof(retryAfterMilliseconds));
        }

        if (retryAfterMilliseconds is not null
            && code is not (DocumentationScribeModelFailureCode.TransientUnavailable
                or DocumentationScribeModelFailureCode.RateLimited))
        {
            throw new ArgumentException(
                "A retry hint is valid only for a transient failure.",
                nameof(retryAfterMilliseconds));
        }

        if (httpStatusCode is < 100 or > 599)
        {
            throw new ArgumentOutOfRangeException(nameof(httpStatusCode));
        }

        if ((origin == DocumentationScribeModelFailureOrigin.HttpStatus) != (httpStatusCode is not null))
        {
            throw new ArgumentException(
                "An HTTP status is required only for an HTTP-status failure origin.",
                nameof(httpStatusCode));
        }

        Code = code;
        RetryAfterMilliseconds = retryAfterMilliseconds;
        Origin = origin;
        HttpStatusCode = httpStatusCode;
    }

    public DocumentationScribeModelFailureCode Code { get; }

    public int? RetryAfterMilliseconds { get; }

    public DocumentationScribeModelFailureOrigin? Origin { get; }

    public int? HttpStatusCode { get; }

    public bool IsTransient => Code is DocumentationScribeModelFailureCode.TransientUnavailable
        or DocumentationScribeModelFailureCode.RateLimited;

    public override string ToString() => nameof(DocumentationScribeModelFailure);
}

public sealed class DocumentationScribeModelUsage
{
    public DocumentationScribeModelUsage(
        int? inputTokens = null,
        int? outputTokens = null,
        int? cachedInputTokens = null,
        int? uncachedInputTokens = null,
        int? reasoningTokens = null)
    {
        if (inputTokens is null
            && outputTokens is null
            && cachedInputTokens is null
            && uncachedInputTokens is null
            && reasoningTokens is null)
        {
            throw new ArgumentException("At least one usage observation is required.");
        }

        Validate(inputTokens, DocumentationScribeContract.MaximumObservedInputTokens, nameof(inputTokens));
        Validate(outputTokens, DocumentationScribeContract.MaximumObservedOutputTokens, nameof(outputTokens));
        Validate(cachedInputTokens, DocumentationScribeContract.MaximumObservedInputTokens, nameof(cachedInputTokens));
        Validate(uncachedInputTokens, DocumentationScribeContract.MaximumObservedInputTokens, nameof(uncachedInputTokens));
        Validate(reasoningTokens, DocumentationScribeContract.MaximumObservedOutputTokens, nameof(reasoningTokens));
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        CachedInputTokens = cachedInputTokens;
        UncachedInputTokens = uncachedInputTokens;
        ReasoningTokens = reasoningTokens;
    }

    public int? InputTokens { get; }

    public int? OutputTokens { get; }

    public int? CachedInputTokens { get; }

    public int? UncachedInputTokens { get; }

    public int? ReasoningTokens { get; }

    public override string ToString() => nameof(DocumentationScribeModelUsage);

    private static void Validate(int? value, int maximum, string parameterName)
    {
        if (value is < 0 || value > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}

public sealed class DocumentationScribeModelCost
{
    public DocumentationScribeModelCost(string currencyId, long amountMicrounits)
    {
        CurrencyId = DocumentationScribeBoundary.ValidateIdentifier(currencyId, nameof(currencyId));
        if (amountMicrounits is < 0 or > DocumentationScribeContract.MaximumObservedCostMicrounits)
        {
            throw new ArgumentOutOfRangeException(nameof(amountMicrounits));
        }

        AmountMicrounits = amountMicrounits;
    }

    public string CurrencyId { get; }

    public long AmountMicrounits { get; }

    public override string ToString() => nameof(DocumentationScribeModelCost);
}

public sealed class DocumentationScribeModelResponse
{
    public DocumentationScribeModelResponse(
        ImmutableArray<DocumentationScribeModelToolCall> toolCalls,
        ImmutableArray<DocumentationScribeModelTerminalSubmission> terminalSubmissions,
        DocumentationScribeModelFailure? failure = null,
        DocumentationScribeModelUsage? usage = null,
        DocumentationScribeCacheObservation? cache = null,
        DocumentationScribeModelCost? cost = null)
        : this(toolCalls, terminalSubmissions, failure, usage, cache, cost, null,
            DocumentationScribeContinuationObservation.None)
    {
    }

    internal DocumentationScribeModelResponse(
        ImmutableArray<DocumentationScribeModelToolCall> toolCalls,
        ImmutableArray<DocumentationScribeModelTerminalSubmission> terminalSubmissions,
        DocumentationScribeModelFailure? failure,
        DocumentationScribeModelUsage? usage,
        DocumentationScribeCacheObservation? cache,
        DocumentationScribeModelCost? cost,
        DocumentationScribeAssistantContinuation? assistantContinuation,
        DocumentationScribeContinuationObservation continuationObservation)
    {
        if (toolCalls.IsDefault || toolCalls.Length > DocumentationScribeBoundary.MaximumToolCallsPerResponse
            || toolCalls.Any(call => call is null))
        {
            throw new ArgumentException("The tool-call collection is not bounded.", nameof(toolCalls));
        }

        if (terminalSubmissions.IsDefault
            || terminalSubmissions.Length > DocumentationScribeBoundary.MaximumTerminalSubmissions
            || terminalSubmissions.Any(submission => submission is null))
        {
            throw new ArgumentException(
                "The terminal-submission collection is not bounded.",
                nameof(terminalSubmissions));
        }

        if (cache is { } cacheValue && !Enum.IsDefined(cacheValue))
        {
            throw new ArgumentOutOfRangeException(nameof(cache));
        }

        const DocumentationScribeContinuationObservation knownObservations =
            DocumentationScribeContinuationObservation.Observed
            | DocumentationScribeContinuationObservation.HistoryReplayed
            | DocumentationScribeContinuationObservation.MissingRequired;
        if ((continuationObservation & ~knownObservations) != 0
            || assistantContinuation is not null && toolCalls.IsEmpty)
        {
            throw new ArgumentException("The continuation observation is outside the product boundary.");
        }

        long payloadBytes = 0;
        try
        {
            foreach (var call in toolCalls)
            {
                payloadBytes = checked(payloadBytes + call.ArgumentsUtf8Json.Length);
            }

            foreach (var terminal in terminalSubmissions)
            {
                payloadBytes = checked(payloadBytes + terminal.TerminalUtf8Json.Length);
            }
        }
        catch (OverflowException)
        {
            throw new ArgumentException("The normalized response is outside the product boundary.");
        }

        if (payloadBytes > DocumentationScribeBoundary.MaximumNormalizedResponseUtf8Bytes
            || MeasureNormalizedResponse(
                toolCalls,
                terminalSubmissions,
                failure,
                usage,
                cache,
                cost,
                assistantContinuation,
                continuationObservation) > DocumentationScribeBoundary.MaximumNormalizedResponseUtf8Bytes)
        {
            throw new ArgumentException("The normalized response is outside the product boundary.");
        }

        ToolCalls = toolCalls;
        TerminalSubmissions = terminalSubmissions;
        Failure = failure;
        Usage = usage;
        Cache = cache;
        Cost = cost;
        AssistantContinuation = assistantContinuation;
        ContinuationObservation = continuationObservation;
    }

    public ImmutableArray<DocumentationScribeModelToolCall> ToolCalls { get; }

    public ImmutableArray<DocumentationScribeModelTerminalSubmission> TerminalSubmissions { get; }

    public DocumentationScribeModelFailure? Failure { get; }

    public DocumentationScribeModelUsage? Usage { get; }

    public DocumentationScribeCacheObservation? Cache { get; }

    public DocumentationScribeModelCost? Cost { get; }

    public DocumentationScribeContinuationObservation ContinuationObservation { get; }

    internal DocumentationScribeAssistantContinuation? AssistantContinuation { get; }

    public DocumentationScribeModelResponse WithCost(DocumentationScribeModelCost? cost) => new(
        ToolCalls,
        TerminalSubmissions,
        Failure,
        Usage,
        Cache,
        cost,
        AssistantContinuation,
        ContinuationObservation);

    public override string ToString() => nameof(DocumentationScribeModelResponse);

    internal static int MeasureNormalizedResponse(
        ImmutableArray<DocumentationScribeModelToolCall> toolCalls,
        ImmutableArray<DocumentationScribeModelTerminalSubmission> terminalSubmissions,
        DocumentationScribeModelFailure? failure,
        DocumentationScribeModelUsage? usage,
        DocumentationScribeCacheObservation? cache,
        DocumentationScribeModelCost? cost,
        DocumentationScribeAssistantContinuation? assistantContinuation = null,
        DocumentationScribeContinuationObservation continuationObservation =
            DocumentationScribeContinuationObservation.None)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("toolCalls");
            writer.WriteStartArray();
            foreach (var call in toolCalls)
            {
                writer.WriteStartObject();
                writer.WriteNumber("responseIndex", call.ResponseIndex);
                writer.WriteString("callId", call.CallId);
                writer.WriteString("operationId", call.OperationId);
                writer.WritePropertyName("arguments");
                writer.WriteRawValue(call.ArgumentsUtf8Json.Span, skipInputValidation: true);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("terminalSubmissions");
            writer.WriteStartArray();
            foreach (var terminal in terminalSubmissions)
            {
                writer.WriteRawValue(terminal.TerminalUtf8Json.Span, skipInputValidation: true);
            }

            writer.WriteEndArray();
            if (failure is not null)
            {
                writer.WritePropertyName("failure");
                writer.WriteStartObject();
                writer.WriteString("code", failure.Code.ToString());
                if (failure.RetryAfterMilliseconds is { } retryAfter)
                {
                    writer.WriteNumber("retryAfterMilliseconds", retryAfter);
                }

                writer.WriteEndObject();
            }

            if (usage is not null)
            {
                writer.WritePropertyName("usage");
                writer.WriteStartObject();
                WriteNullable(writer, "inputTokens", usage.InputTokens);
                WriteNullable(writer, "outputTokens", usage.OutputTokens);
                WriteNullable(writer, "cachedInputTokens", usage.CachedInputTokens);
                WriteNullable(writer, "uncachedInputTokens", usage.UncachedInputTokens);
                WriteNullable(writer, "reasoningTokens", usage.ReasoningTokens);
                writer.WriteEndObject();
            }

            if (cache is { } cacheValue)
            {
                writer.WriteString("cache", DocumentationScribeVocabulary.GetId(cacheValue));
            }

            if (cost is not null)
            {
                writer.WritePropertyName("cost");
                writer.WriteStartObject();
                writer.WriteString("currencyId", cost.CurrencyId);
                writer.WriteNumber("amountMicrounits", cost.AmountMicrounits);
                writer.WriteEndObject();
            }

            if (assistantContinuation is not null)
            {
                writer.WritePropertyName("assistantContinuation");
                writer.WriteStartObject();
                if (assistantContinuation.ContentPresent)
                {
                    if (assistantContinuation.Content is { } content)
                    {
                        writer.WriteString("content", content);
                    }
                    else
                    {
                        writer.WriteNull("content");
                    }
                }

                if (assistantContinuation.ReasoningContent is { } reasoningContent)
                {
                    writer.WriteString("reasoningContent", reasoningContent);
                }

                writer.WriteEndObject();
            }

            if (continuationObservation != DocumentationScribeContinuationObservation.None)
            {
                writer.WriteNumber("continuationObservation", (int)continuationObservation);
            }

            writer.WriteEndObject();
        }

        return buffer.WrittenCount;
    }

    private static void WriteNullable(Utf8JsonWriter writer, string propertyName, int? value)
    {
        if (value is { } present)
        {
            writer.WriteNumber(propertyName, present);
        }
    }
}

[Flags]
public enum DocumentationScribeContinuationObservation
{
    None = 0,
    Observed = 1,
    HistoryReplayed = 2,
    MissingRequired = 4,
}
