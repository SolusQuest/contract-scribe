using System.Buffers;
using System.Collections.Immutable;
using System.Text.Json;
using ContractScribe.Core;

namespace ContractScribe.Agent.Prompting;

internal static class DocumentationScribeRunResultWriter
{
    internal static ImmutableArray<byte> Write(
        DocumentationScribeRequest request,
        DocumentationScribeAttemptId attemptId,
        ReadOnlyMemory<byte> terminalUtf8Json,
        DocumentationScribeRunEnvelopeInput envelope)
    {
        using var terminal = JsonDocument.Parse(
            terminalUtf8Json,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = DocumentationScribeContract.MaximumJsonDepth,
            });
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("scribeRunResultVersion", DocumentationScribeContract.Version);
            writer.WriteString("scribeRequestSha256", request.ArtifactSha256);
            writer.WriteString("attemptId", attemptId.Value);
            writer.WritePropertyName("terminal");
            terminal.RootElement.WriteTo(writer);
            writer.WritePropertyName("runEnvelope");
            WriteEnvelope(writer, request, attemptId, envelope);
            writer.WriteEndObject();
        }

        if (buffer.WrittenCount > DocumentationScribeContract.MaximumArtifactUtf8Bytes)
        {
            throw new JsonException("The run result is outside the product boundary.");
        }

        return buffer.WrittenSpan.ToArray().ToImmutableArray();
    }

    private static void WriteEnvelope(
        Utf8JsonWriter writer,
        DocumentationScribeRequest request,
        DocumentationScribeAttemptId attemptId,
        DocumentationScribeRunEnvelopeInput envelope)
    {
        writer.WriteStartObject();
        writer.WriteString("scribeRequestSha256", request.ArtifactSha256);
        writer.WriteString("attemptId", attemptId.Value);
        writer.WriteString("providerConfigurationId", envelope.ProviderConfigurationId);
        writer.WriteString("modelConfigurationId", envelope.ModelConfigurationId);
        writer.WriteString("scribeProtocolId", envelope.ScribeProtocolId);
        writer.WriteString("toolPolicyId", request.ToolPolicyId);
        writer.WriteString("styleProfileId", request.StyleProfile.StyleProfileId);
        writer.WriteNumber("attemptNumber", envelope.AttemptNumber);
        writer.WriteNumber("providerRequestCount", envelope.ProviderRequestCount);
        writer.WriteNumber("toolRoundCount", envelope.ToolRoundCount);
        writer.WriteNumber("toolCallCount", envelope.ToolCallCount);
        writer.WriteNumber("elapsedMilliseconds", envelope.ElapsedMilliseconds);
        if (envelope.Usage is { } usage)
        {
            writer.WritePropertyName("usage");
            writer.WriteStartObject();
            WriteOptional(writer, "inputTokens", usage.InputTokens);
            WriteOptional(writer, "outputTokens", usage.OutputTokens);
            WriteOptional(writer, "cachedInputTokens", usage.CachedInputTokens);
            WriteOptional(writer, "uncachedInputTokens", usage.UncachedInputTokens);
            WriteOptional(writer, "reasoningTokens", usage.ReasoningTokens);
            writer.WriteEndObject();
        }

        if (envelope.Cache is { } cache)
        {
            writer.WriteString("cacheObservation", DocumentationScribeVocabulary.GetId(cache));
        }

        if (envelope.Cost is { } cost)
        {
            writer.WritePropertyName("cost");
            writer.WriteStartObject();
            writer.WriteString("currencyId", cost.CurrencyId);
            writer.WriteNumber("amountMicrounits", cost.AmountMicrounits);
            writer.WriteEndObject();
        }

        writer.WritePropertyName("diagnostics");
        writer.WriteStartArray();
        foreach (var diagnostic in envelope.Diagnostics)
        {
            writer.WriteStartObject();
            writer.WriteString("code", diagnostic.Code);
            writer.WriteString("stage", diagnostic.Stage);
            if (diagnostic.ReferenceId is not null)
            {
                writer.WriteString("referenceId", diagnostic.ReferenceId);
            }

            if (diagnostic.ValidationCode is not null)
            {
                writer.WriteString("validationCode", diagnostic.ValidationCode);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteOptional(Utf8JsonWriter writer, string propertyName, int? value)
    {
        if (value is { } present)
        {
            writer.WriteNumber(propertyName, present);
        }
    }
}
