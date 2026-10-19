using System.Buffers;
using System.Collections.Immutable;
using System.Text.Json;
using ContractScribe.Core;

namespace ContractScribe.Agent.Prompting;

internal static class DocumentationScribeRunResultWriter
{
    internal static ImmutableArray<byte> Write(DocumentationScribeRequest request, DocumentationScribeRunResult result)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("scribeRunResultVersion", DocumentationScribeContract.Version);
            writer.WriteString("scribeRequestSha256", result.ScribeRequestSha256);
            writer.WriteString("attemptId", result.AttemptId.Value);
            writer.WritePropertyName("dynamicEvidenceReferences");
            writer.WriteRawValue(CanonicalJson.Serialize(result.DynamicEvidenceReferences).AsSpan());
            writer.WritePropertyName("submissions"); writer.WriteStartArray();
            foreach (var item in result.Submissions)
            {
                writer.WriteStartObject(); writer.WriteString("targetId", item.TargetId);
                writer.WriteString("contentSha256", item.ContentSha256);
                writer.WriteString("stage", DocumentationScribeValidation.StageId(item.Stage));
                writer.WritePropertyName("result"); writer.WriteRawValue(item.Validation.ResultUtf8Json.AsSpan()); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WritePropertyName("targetFailures");
            writer.WriteRawValue(CanonicalJson.Serialize(result.TargetFailures).AsSpan());
            writer.WritePropertyName("stop"); WriteStop(writer, result.Stop);
            writer.WriteNumber("repairCount", result.RepairCount); writer.WriteNumber("nextSequence", result.NextSequence);
            writer.WritePropertyName("coverage"); writer.WriteStartArray();
            foreach (var row in DocumentationScribeValidation.GetCoverage(request, result))
            {
                writer.WriteStartObject(); writer.WriteString("targetId", row.TargetId);
                writer.WriteString("auditOutcome", AuditVocabulary.GetId(row.AuditOutcome)); writer.WriteString("status", row.Status);
                if (row.Stage is { } stage) writer.WriteString("stage", DocumentationScribeValidation.StageId(stage));
                writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WritePropertyName("runEnvelope");
            WriteEnvelope(writer, request, result.AttemptId, result.RunEnvelope); writer.WriteEndObject();
        }
        if (buffer.WrittenCount > DocumentationScribeContract.MaximumArtifactUtf8Bytes)
            throw new JsonException("The run result is outside the product boundary.");
        return buffer.WrittenSpan.ToArray().ToImmutableArray();
    }

    private static void WriteStop(Utf8JsonWriter writer, DocumentationScribeTerminal stop)
    {
        writer.WriteStartObject(); writer.WriteString("kind", DocumentationScribeVocabulary.GetId(stop.Kind));
        switch (stop)
        {
            case DocumentationScribeFinishedTerminal finished:
                writer.WriteString("reason", finished.Reason switch
                { DocumentationScribeFinishReason.Completed => "completed", DocumentationScribeFinishReason.Unresolved => "unresolved", DocumentationScribeFinishReason.Context => "context", _ => throw new JsonException() }); break;
            case DocumentationScribeFailureTerminal failure:
                writer.WriteString("code", DocumentationScribeVocabulary.GetId(failure.Code));
                if (failure.ProviderFinalDisposition is { } disposition)
                    writer.WriteString("providerFinalDisposition", DocumentationScribeVocabulary.GetId(disposition)); break;
            case DocumentationScribeCancelledTerminal cancelled:
                writer.WriteString("code", DocumentationScribeVocabulary.GetId(cancelled.Code)); break;
            default: throw new JsonException("A session stop is required.");
        }
        writer.WriteEndObject();
    }

    private static void WriteEnvelope(
        Utf8JsonWriter writer,
        DocumentationScribeRequest request,
        DocumentationScribeAttemptId attemptId,
        DocumentationScribeRunEnvelope envelope)
    {
        writer.WriteStartObject();
        writer.WriteString("scribeRequestSha256", request.ArtifactSha256);
        writer.WriteString("attemptId", attemptId.Value);
        writer.WriteString("providerConfigurationId", envelope.ProviderConfigurationId);
        writer.WriteString("modelConfigurationId", envelope.ModelConfigurationId);
        writer.WriteString("scribeProtocolId", envelope.ScribeProtocolId);
        writer.WriteString("toolPolicyId", request.ToolPolicyId);
        writer.WritePropertyName("styleProfileIds");
        writer.WriteRawValue(CanonicalJson.Serialize(envelope.StyleProfileIds).AsSpan());
        writer.WriteNumber("attemptNumber", envelope.AttemptNumber);
        writer.WriteNumber("restoredRetryableProviderFailures", envelope.RestoredRetryableProviderFailures);
        writer.WriteNumber("providerRequestCount", envelope.ProviderRequestCount);
        writer.WriteNumber("toolRoundCount", envelope.ToolRoundCount);
        writer.WriteNumber("toolCallCount", envelope.ToolCallCount);
        writer.WriteNumber("elapsedMilliseconds", envelope.ElapsedMilliseconds);
        if (envelope.UsageCoverage is { } coverage)
        { writer.WritePropertyName("usageCoverage"); writer.WriteRawValue(CanonicalJson.Serialize(coverage).AsSpan()); }
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
