using System.Buffers;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContractScribe.Agent.Prompting;
using ContractScribe.Agent.Runtime;
using ContractScribe.Core;

namespace ContractScribe.Agent.Providers;

internal static class OpenAiCompatibleChatCompletionsCodec
{
    internal const string TerminalAlias = "cs_terminal";
    internal const int MaximumOrdinaryTools = 127;
    internal const int MaximumRawResponseUtf8Bytes = checked(
        6 * DocumentationScribeBoundary.MaximumNormalizedResponseUtf8Bytes
        + (MaximumOrdinaryTools + 1)
            * (6 * DocumentationScribeBoundary.MaximumCorrelationIdUtf8Bytes + 512)
        + 65_536);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly OpenAiCompatibleChatCompletionsRequestProfile DefaultProfile = new(
        OpenAiCompatibleThinkingMode.Disabled,
        reasoningEffort: null,
        OpenAiCompatibleToolChoice.Required,
        OpenAiCompatibleContinuationPolicy.Optional,
        OpenAiCompatibleOutputTokenField.MaxTokens);

    internal static OpenAiCompatiblePreparedRequest Prepare(
        DocumentationScribeModelRequest request,
        string model) => Prepare(request, model, DefaultProfile);

    internal static OpenAiCompatiblePreparedRequest Prepare(
        DocumentationScribeModelRequest request,
        string model,
        OpenAiCompatibleChatCompletionsRequestProfile profile)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(model);
        ArgumentNullException.ThrowIfNull(profile);
        EnsureStrict(model);

        try
        {
            if (request.Tools.Length > MaximumOrdinaryTools)
            {
                throw Unsupported();
            }

            var orderedTools = request.Tools
                .OrderBy(tool => tool.OperationId, StringComparer.Ordinal)
                .ToImmutableArray();
            var operationToAlias = new Dictionary<string, string>(StringComparer.Ordinal);
            var aliasToOperation = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < orderedTools.Length; index++)
            {
                var tool = orderedTools[index];
                if (string.Equals(tool.OperationId, request.Terminal.OperationId, StringComparison.Ordinal)
                    || !operationToAlias.TryAdd(tool.OperationId, $"cs_tool_{index:D3}"))
                {
                    throw Unsupported();
                }

                aliasToOperation.Add($"cs_tool_{index:D3}", tool.OperationId);
                ValidateSchema(tool.InputSchemaJson);
                EnsureStrict(tool.Description);
            }

            ValidateSchema(request.Terminal.SchemaJson);
            var rounds = ReconstructRounds(request.CompletedToolExchanges, operationToAlias);
            var body = WriteBody(request, model, profile, orderedTools, operationToAlias, rounds);
            if (body.Length > DocumentationScribeBoundary.MaximumLogicalRequestUtf8Bytes)
            {
                throw Unsupported();
            }

            var projection = WriteProjection(request, orderedTools);
            return new OpenAiCompatiblePreparedRequest(
                body,
                projection,
                aliasToOperation.ToImmutable(),
                request.CompletedToolExchanges.Select(exchange => exchange.CallId)
                    .ToImmutableHashSet(StringComparer.Ordinal),
                request.OutputLimits,
                profile,
                rounds.Any(round => round[0].AssistantContinuation is not null));
        }
        catch (OpenAiCompatibleProtocolException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException
            or InvalidOperationException
            or DecoderFallbackException
            or EncoderFallbackException
            or ArgumentException
            or OverflowException)
        {
            throw Unsupported();
        }
    }

    internal static DocumentationScribeModelResponse ParseResponse(
        ReadOnlyMemory<byte> utf8Json,
        OpenAiCompatiblePreparedRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (utf8Json.Length > MaximumRawResponseUtf8Bytes)
        {
            throw Malformed();
        }

        try
        {
            using var document = JsonDocument.Parse(utf8Json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = DocumentationScribeContract.MaximumJsonDepth,
            });
            RejectDuplicateProperties(document.RootElement);
            var root = RequireObject(document.RootElement);
            var choices = RequireProperty(root, "choices");
            if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() != 1)
            {
                throw Malformed();
            }

            var choice = RequireObject(choices[0]);
            if (!TryGetExactInt(choice, "index", out var choiceIndex) || choiceIndex != 0)
            {
                throw Malformed();
            }

            var message = RequireObject(RequireProperty(choice, "message"));
            if (!string.Equals(RequireString(message, "role"), "assistant", StringComparison.Ordinal))
            {
                throw Malformed();
            }

            string? contentText = null;
            if (message.TryGetProperty("content", out var content))
            {
                if (content.ValueKind == JsonValueKind.String)
                {
                    contentText = content.GetString();
                    if (contentText is null)
                    {
                        throw Malformed();
                    }

                    EnsureStrict(contentText);
                }
                else if (content.ValueKind != JsonValueKind.Null)
                {
                    throw Malformed();
                }
            }

            var reasoningContentPresent = message.TryGetProperty("reasoning_content", out var reasoningContent);
            string? reasoningContentText = null;
            if (reasoningContentPresent)
            {
                if (reasoningContent.ValueKind == JsonValueKind.String)
                {
                    reasoningContentText = reasoningContent.GetString() ?? throw Malformed();
                    EnsureStrict(reasoningContentText);
                }
                else if (reasoningContent.ValueKind != JsonValueKind.Null)
                {
                    throw Malformed();
                }
            }

            var hasRefusal = message.TryGetProperty("refusal", out var refusal);
            if (hasRefusal && refusal.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
            {
                throw Malformed();
            }

            if (hasRefusal && refusal.ValueKind == JsonValueKind.String)
            {
                EnsureStrict(refusal.GetString() ?? throw Malformed());
            }

            var calls = ImmutableArray.CreateBuilder<ParsedCall>();
            var callIds = new HashSet<string>(StringComparer.Ordinal);
            if (message.TryGetProperty("tool_calls", out var callsElement))
            {
                if (callsElement.ValueKind != JsonValueKind.Array
                    || callsElement.GetArrayLength() > MaximumOrdinaryTools + 1)
                {
                    throw Malformed();
                }

                for (var index = 0; index < callsElement.GetArrayLength(); index++)
                {
                    var call = RequireObject(callsElement[index]);
                    if (call.TryGetProperty("index", out _))
                    {
                        throw Malformed();
                    }

                    var callId = RequireString(call, "id");
                    DocumentationScribeBoundary.ValidateCorrelationId(callId, nameof(callId));
                    if (request.CompletedCallIds.Contains(callId)
                        || !callIds.Add(callId)
                        || !string.Equals(RequireString(call, "type"), "function", StringComparison.Ordinal))
                    {
                        throw Malformed();
                    }

                    var function = RequireObject(RequireProperty(call, "function"));
                    var alias = RequireString(function, "name");
                    if (!string.Equals(alias, TerminalAlias, StringComparison.Ordinal)
                        && !request.AliasToOperation.ContainsKey(alias))
                    {
                        throw Malformed();
                    }

                    var argumentsText = RequireString(function, "arguments");
                    var arguments = StrictUtf8.GetBytes(argumentsText);
                    ValidateArguments(arguments);
                    calls.Add(new ParsedCall(index, callId, alias, arguments));
                }
            }

            var terminalCount = calls.Count(call => string.Equals(call.Alias, TerminalAlias, StringComparison.Ordinal));
            if (terminalCount > 0 && (terminalCount != 1 || calls.Count != 1))
            {
                throw Malformed();
            }

            var observations = ParseUsage(root);
            var finishReason = RequireString(choice, "finish_reason");
            if (!string.Equals(finishReason, "tool_calls", StringComparison.Ordinal))
            {
                throw Unsupported();
            }

            if ((hasRefusal && refusal.ValueKind != JsonValueKind.Null)
                || calls.Count == 0)
            {
                throw Malformed();
            }

            if (terminalCount > 0)
            {
                var terminal = calls[0];
                if (terminal.Arguments.Length > request.OutputLimits.MaximumTerminalUtf8Bytes)
                {
                    throw Malformed();
                }

                return Response(
                    [],
                    [new DocumentationScribeModelTerminalSubmission(terminal.Arguments)],
                    observations,
                    continuation: null,
                    Observation(request, reasoningContentPresent && reasoningContentText is not null));
            }

            if (request.Profile.ContinuationPolicy
                    == OpenAiCompatibleContinuationPolicy.RequiredForToolCalls
                && (!reasoningContentPresent || reasoningContentText is null))
            {
                throw MissingRequiredContinuation(request);
            }

            if (calls.Count > request.OutputLimits.MaximumToolCalls)
            {
                throw Malformed();
            }

            var normalized = ImmutableArray.CreateBuilder<DocumentationScribeModelToolCall>(calls.Count);
            foreach (var call in calls)
            {
                if (!request.AliasToOperation.TryGetValue(call.Alias, out var operationId)
                    || call.Arguments.Length > request.OutputLimits.MaximumToolArgumentUtf8Bytes)
                {
                    throw Malformed();
                }

                normalized.Add(new DocumentationScribeModelToolCall(
                    call.ResponseIndex,
                    call.CallId,
                    operationId,
                    call.Arguments));
            }

            var continuation = new DocumentationScribeAssistantContinuation(
                contentText ?? string.Empty,
                reasoningContentText);
            return Response(
                normalized.ToImmutable(),
                [],
                observations,
                continuation,
                Observation(request, reasoningContentPresent && reasoningContentText is not null));
        }
        catch (OpenAiCompatibleProtocolException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException
            or InvalidOperationException
            or DecoderFallbackException
            or EncoderFallbackException
            or ArgumentException
            or OverflowException)
        {
            throw Malformed();
        }
    }

    internal static string Digest(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static byte[] WriteBody(
        DocumentationScribeModelRequest request,
        string model,
        OpenAiCompatibleChatCompletionsRequestProfile profile,
        ImmutableArray<DocumentationScribeModelToolDefinition> tools,
        IReadOnlyDictionary<string, string> aliases,
        ImmutableArray<ImmutableArray<DocumentationScribeCompletedToolExchange>> rounds)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model);
            writer.WritePropertyName("messages");
            writer.WriteStartArray();
            foreach (var message in request.Messages)
            {
                writer.WriteStartObject();
                writer.WriteString("role", Role(message.Kind));
                writer.WriteString("content", message.Content);
                writer.WriteEndObject();
            }

            foreach (var round in rounds)
            {
                var continuation = round[0].AssistantContinuation;
                writer.WriteStartObject();
                writer.WriteString("role", "assistant");
                writer.WriteString("content", continuation?.Content ?? string.Empty);
                if (continuation?.ReasoningContent is { } reasoningContent)
                {
                    writer.WriteString("reasoning_content", reasoningContent);
                }

                writer.WritePropertyName("tool_calls");
                writer.WriteStartArray();
                foreach (var exchange in round)
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", exchange.CallId);
                    writer.WriteString("type", "function");
                    writer.WritePropertyName("function");
                    writer.WriteStartObject();
                    writer.WriteString("name", aliases[exchange.OperationId]);
                    writer.WriteString("arguments", StrictUtf8.GetString(exchange.ArgumentsUtf8Json.Span));
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
                foreach (var exchange in round)
                {
                    writer.WriteStartObject();
                    writer.WriteString("role", "tool");
                    writer.WriteString("tool_call_id", exchange.CallId);
                    writer.WriteString("content", WriteResultWrapper(exchange));
                    writer.WriteEndObject();
                }
            }

            writer.WriteEndArray();
            writer.WritePropertyName("tools");
            writer.WriteStartArray();
            foreach (var tool in tools)
            {
                WriteTool(writer, aliases[tool.OperationId], tool.Description, tool.InputSchemaJson);
            }

            WriteTool(writer, TerminalAlias, "Submit one structured terminal result.", request.Terminal.SchemaJson);
            writer.WriteEndArray();
            writer.WritePropertyName("thinking");
            writer.WriteStartObject();
            writer.WriteString("type", profile.ThinkingMode switch
            {
                OpenAiCompatibleThinkingMode.Enabled => "enabled",
                OpenAiCompatibleThinkingMode.Disabled => "disabled",
                _ => throw Unsupported(),
            });
            writer.WriteEndObject();
            if (profile.ReasoningEffort is { } reasoningEffort)
            {
                writer.WriteString("reasoning_effort", reasoningEffort switch
                {
                    OpenAiCompatibleReasoningEffort.High => "high",
                    _ => throw Unsupported(),
                });
            }

            if (profile.ToolChoice != OpenAiCompatibleToolChoice.Omitted)
            {
                writer.WriteString("tool_choice", profile.ToolChoice switch
                {
                    OpenAiCompatibleToolChoice.Auto => "auto",
                    OpenAiCompatibleToolChoice.Required => "required",
                    _ => throw Unsupported(),
                });
            }

            writer.WriteNumber(profile.OutputTokenField switch
            {
                OpenAiCompatibleOutputTokenField.MaxTokens => "max_tokens",
                OpenAiCompatibleOutputTokenField.MaxCompletionTokens => "max_completion_tokens",
                _ => throw Unsupported(),
            }, request.OutputLimits.MaximumOutputTokens);
            writer.WriteBoolean("stream", false);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] WriteProjection(
        DocumentationScribeModelRequest request,
        ImmutableArray<DocumentationScribeModelToolDefinition> tools)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("messages");
            writer.WriteStartArray();
            foreach (var message in request.Messages)
            {
                writer.WriteStartObject();
                writer.WriteString("kind", message.Kind.ToString());
                writer.WriteString("content", message.Content);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("tools");
            writer.WriteStartArray();
            foreach (var tool in tools)
            {
                writer.WriteStartObject();
                writer.WriteString("operationId", tool.OperationId);
                writer.WriteString("description", tool.Description);
                writer.WritePropertyName("schema");
                writer.WriteRawValue(tool.InputSchemaJson);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("terminal");
            writer.WriteStartObject();
            writer.WriteString("operationId", request.Terminal.OperationId);
            writer.WritePropertyName("schema");
            writer.WriteRawValue(request.Terminal.SchemaJson);
            writer.WriteEndObject();
            writer.WritePropertyName("completedToolExchanges");
            writer.WriteStartArray();
            foreach (var exchange in request.CompletedToolExchanges)
            {
                writer.WriteStartObject();
                writer.WriteNumber("responseIndex", exchange.ResponseIndex);
                writer.WriteString("callId", exchange.CallId);
                writer.WriteString("operationId", exchange.OperationId);
                writer.WritePropertyName("arguments");
                writer.WriteRawValue(exchange.ArgumentsUtf8Json.Span);
                writer.WriteString("outcome", exchange.OutcomeId);
                writer.WritePropertyName("result");
                writer.WriteRawValue(exchange.ResultUtf8Json.Span);
                writer.WritePropertyName("evidenceReferences");
                writer.WriteRawValue(CanonicalJson.Serialize(exchange.EvidenceReferences).AsSpan());
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("outputLimits");
            writer.WriteStartObject();
            writer.WriteNumber("maximumToolCalls", request.OutputLimits.MaximumToolCalls);
            writer.WriteNumber("maximumToolArgumentUtf8Bytes", request.OutputLimits.MaximumToolArgumentUtf8Bytes);
            writer.WriteNumber("maximumTerminalUtf8Bytes", request.OutputLimits.MaximumTerminalUtf8Bytes);
            writer.WriteNumber("maximumOutputTokens", request.OutputLimits.MaximumOutputTokens);
            writer.WriteNumber("maximumNormalizedResponseUtf8Bytes", request.OutputLimits.MaximumNormalizedResponseUtf8Bytes);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteTool(Utf8JsonWriter writer, string alias, string description, string schema)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "function");
        writer.WritePropertyName("function");
        writer.WriteStartObject();
        writer.WriteString("name", alias);
        writer.WriteString("description", description);
        writer.WritePropertyName("parameters");
        writer.WriteRawValue(schema);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static string WriteResultWrapper(DocumentationScribeCompletedToolExchange exchange)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("outcome", exchange.OutcomeId);
            writer.WritePropertyName("result");
            writer.WriteRawValue(exchange.ResultUtf8Json.Span);
            writer.WritePropertyName("evidenceReferences");
            writer.WriteRawValue(CanonicalJson.Serialize(exchange.EvidenceReferences).AsSpan());
            writer.WriteEndObject();
        }

        return StrictUtf8.GetString(buffer.WrittenSpan);
    }

    private static ImmutableArray<ImmutableArray<DocumentationScribeCompletedToolExchange>> ReconstructRounds(
        ImmutableArray<DocumentationScribeCompletedToolExchange> exchanges,
        IReadOnlyDictionary<string, string> aliases)
    {
        var rounds = ImmutableArray.CreateBuilder<ImmutableArray<DocumentationScribeCompletedToolExchange>>();
        var current = ImmutableArray.CreateBuilder<DocumentationScribeCompletedToolExchange>();
        var callIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var exchange in exchanges)
        {
            if (!aliases.ContainsKey(exchange.OperationId)
                || !callIds.Add(exchange.CallId)
                || exchange.ResponseIndex < 0)
            {
                throw Unsupported();
            }

            if (exchange.ResponseIndex == 0)
            {
                if (current.Count > 0)
                {
                    rounds.Add(current.ToImmutable());
                    current.Clear();
                }
            }
            else if (exchange.ResponseIndex != current.Count)
            {
                throw Unsupported();
            }

            if (exchange.ResponseIndex != 0 && exchange.AssistantContinuation is not null)
            {
                throw Unsupported();
            }

            current.Add(exchange);
        }

        if (current.Count > 0)
        {
            rounds.Add(current.ToImmutable());
        }

        return rounds.ToImmutable();
    }

    private static UsageObservations ParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage))
        {
            return default;
        }

        usage = RequireObject(usage);
        var input = OptionalInt(usage, "prompt_tokens", DocumentationScribeContract.MaximumObservedInputTokens);
        var output = OptionalInt(usage, "completion_tokens", DocumentationScribeContract.MaximumObservedOutputTokens);
        var directHit = OptionalInt(usage, "prompt_cache_hit_tokens", DocumentationScribeContract.MaximumObservedInputTokens);
        var directMiss = OptionalInt(usage, "prompt_cache_miss_tokens", DocumentationScribeContract.MaximumObservedInputTokens);
        int? cachedDetail = null;
        if (usage.TryGetProperty("prompt_tokens_details", out var promptDetails))
        {
            cachedDetail = OptionalInt(RequireObject(promptDetails), "cached_tokens", DocumentationScribeContract.MaximumObservedInputTokens);
        }

        int? reasoning = null;
        if (usage.TryGetProperty("completion_tokens_details", out var completionDetails))
        {
            reasoning = OptionalInt(RequireObject(completionDetails), "reasoning_tokens", DocumentationScribeContract.MaximumObservedOutputTokens);
        }

        var cached = directHit ?? cachedDetail;
        var cacheComponentsExceedInput = input is int inputTotal
            && cached is int cachedTokens
            && directMiss is int uncachedTokens
            && cachedTokens + uncachedTokens > inputTotal;
        if ((directHit is not null && cachedDetail is not null && directHit != cachedDetail)
            || (input is not null && (cached > input || directMiss > input))
            || cacheComponentsExceedInput)
        {
            throw Malformed();
        }

        var modelUsage = input is null && output is null && cached is null && directMiss is null && reasoning is null
            ? null
            : new DocumentationScribeModelUsage(input, output, cached, directMiss, reasoning);
        DocumentationScribeCacheObservation? cache = (directHit, directMiss) switch
        {
            ( > 0, > 0) => DocumentationScribeCacheObservation.Mixed,
            ( > 0, 0) => DocumentationScribeCacheObservation.Hit,
            (0, > 0) => DocumentationScribeCacheObservation.Miss,
            _ => null,
        };
        return new UsageObservations(modelUsage, cache);
    }

    private static int? OptionalInt(JsonElement owner, string name, int maximum)
    {
        if (!owner.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (!value.TryGetInt32(out var result) || result < 0 || result > maximum)
        {
            throw Malformed();
        }

        return result;
    }

    private static DocumentationScribeModelResponse Response(
        ImmutableArray<DocumentationScribeModelToolCall> calls,
        ImmutableArray<DocumentationScribeModelTerminalSubmission> terminals,
        UsageObservations observations,
        DocumentationScribeAssistantContinuation? continuation,
        DocumentationScribeContinuationObservation continuationObservation) => new(
            calls,
            terminals,
            failure: null,
            observations.Usage,
            observations.Cache,
            cost: null,
            continuation,
            continuationObservation);

    private static DocumentationScribeContinuationObservation Observation(
        OpenAiCompatiblePreparedRequest request,
        bool observed)
    {
        var result = observed
            ? DocumentationScribeContinuationObservation.Observed
            : DocumentationScribeContinuationObservation.None;
        return request.HistoryReplayed
            ? result | DocumentationScribeContinuationObservation.HistoryReplayed
            : result;
    }

    private static string Role(DocumentationScribeMessageKind kind) => kind switch
    {
        DocumentationScribeMessageKind.SystemPolicy => "system",
        DocumentationScribeMessageKind.RepositoryInstructions => "user",
        DocumentationScribeMessageKind.MaintainedContext => "user",
        DocumentationScribeMessageKind.RunPolicy => "system",
        DocumentationScribeMessageKind.TargetEvidence => "user",
        _ => throw Unsupported(),
    };

    private static void ValidateSchema(string schema)
    {
        try
        {
            EnsureStrict(schema);
            using var document = JsonDocument.Parse(schema, new JsonDocumentOptions
            {
                MaxDepth = DocumentationScribeContract.MaximumJsonDepth,
            });
            RejectDuplicateProperties(document.RootElement);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw Unsupported();
            }
        }
        catch (OpenAiCompatibleProtocolException)
        {
            throw Unsupported();
        }
        catch (JsonException)
        {
            throw Unsupported();
        }
    }

    private static void ValidateArguments(ReadOnlyMemory<byte> arguments)
    {
        using var document = JsonDocument.Parse(arguments, new JsonDocumentOptions
        {
            MaxDepth = DocumentationScribeContract.MaximumJsonDepth,
        });
        RejectDuplicateProperties(document.RootElement);
    }

    private static void EnsureStrict(string value) => _ = StrictUtf8.GetByteCount(value);

    private static JsonElement RequireObject(JsonElement value) => value.ValueKind == JsonValueKind.Object
        ? value
        : throw Malformed();

    private static JsonElement RequireProperty(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) ? value : throw Malformed();

    private static string RequireString(JsonElement owner, string name)
    {
        var value = RequireProperty(owner, name);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw Malformed();
        }

        var result = value.GetString() ?? throw Malformed();
        EnsureStrict(result);
        return result;
    }

    private static bool TryGetExactInt(JsonElement owner, string name, out int value)
    {
        value = default;
        return owner.TryGetProperty(name, out var property) && property.TryGetInt32(out value);
    }

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw Malformed();
                }

                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                RejectDuplicateProperties(item);
            }
        }
    }

    private static OpenAiCompatibleProtocolException Unsupported() => new(
        DocumentationScribeModelFailureCode.Unsupported);

    private static OpenAiCompatibleProtocolException Malformed() => new(
        DocumentationScribeModelFailureCode.MalformedResponse);

    private static OpenAiCompatibleProtocolException MissingRequiredContinuation(
        OpenAiCompatiblePreparedRequest request) => new(
        DocumentationScribeModelFailureCode.MalformedResponse,
        Observation(request, observed: false)
            | DocumentationScribeContinuationObservation.MissingRequired);

    private readonly record struct ParsedCall(
        int ResponseIndex,
        string CallId,
        string Alias,
        byte[] Arguments);

    private readonly record struct UsageObservations(
        DocumentationScribeModelUsage? Usage,
        DocumentationScribeCacheObservation? Cache);
}

internal sealed class OpenAiCompatiblePreparedRequest
{
    internal OpenAiCompatiblePreparedRequest(
        byte[] bodyUtf8,
        byte[] productProjectionUtf8,
        ImmutableDictionary<string, string> aliasToOperation,
        ImmutableHashSet<string> completedCallIds,
        DocumentationScribeModelOutputLimits outputLimits,
        OpenAiCompatibleChatCompletionsRequestProfile profile,
        bool historyReplayed)
    {
        BodyUtf8 = bodyUtf8;
        ProductProjectionUtf8 = productProjectionUtf8;
        AliasToOperation = aliasToOperation;
        CompletedCallIds = completedCallIds;
        OutputLimits = outputLimits;
        Profile = profile;
        HistoryReplayed = historyReplayed;
    }

    internal byte[] BodyUtf8 { get; }

    internal byte[] ProductProjectionUtf8 { get; }

    internal ImmutableDictionary<string, string> AliasToOperation { get; }

    internal ImmutableHashSet<string> CompletedCallIds { get; }

    internal DocumentationScribeModelOutputLimits OutputLimits { get; }

    internal OpenAiCompatibleChatCompletionsRequestProfile Profile { get; }

    internal bool HistoryReplayed { get; }
}

internal sealed class OpenAiCompatibleProtocolException : Exception
{
    internal OpenAiCompatibleProtocolException(
        DocumentationScribeModelFailureCode code,
        DocumentationScribeContinuationObservation continuationObservation =
            DocumentationScribeContinuationObservation.None)
        : base("The selected provider protocol rejected an exchange.")
    {
        Code = code;
        ContinuationObservation = continuationObservation;
    }

    internal DocumentationScribeModelFailureCode Code { get; }

    internal DocumentationScribeContinuationObservation ContinuationObservation { get; }
}
