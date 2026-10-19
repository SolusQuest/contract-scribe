using System.Collections.Immutable;
using System.Text.Json;

namespace ContractScribe.Core;

public static partial class CampaignStateJson
{
    private static void WriteDispatchObservations(Utf8JsonWriter writer, ImmutableArray<CampaignDispatchObservation> rows)
    {
        writer.WritePropertyName("dispatchObservations");
        writer.WriteStartArray();
        foreach (var row in rows) WriteDispatchObservation(writer, row);
        writer.WriteEndArray();
    }

    private static void WriteDispatchObservation(Utf8JsonWriter writer, CampaignDispatchObservation row)
    {
        writer.WriteStartObject();
        writer.WriteString("executionCommitmentSha256", row.ExecutionCommitmentSha256);
        writer.WriteString("batchIdentity", row.BatchIdentity);
        writer.WriteString("configurationCommitmentSha256", row.ConfigurationCommitmentSha256);
        writer.WriteString("workItemKey", row.WorkItemKey);
        writer.WriteString("attemptId", row.AttemptId.Value);
        writer.WriteString("scribeRequestSha256", row.ScribeRequestSha256);
        writer.WriteNumber("executionStartRevision", row.ExecutionStartRevision);
        writer.WriteNumber("dispatchOrdinal", row.DispatchOrdinal);
        writer.WriteNumber("logicalAttemptNumber", row.LogicalAttemptNumber);
        writer.WriteNumber("providerRequestNumber", row.ProviderRequestNumber);
        writer.WriteString("requestCommitmentSha256", row.RequestCommitmentSha256);
        writer.WritePropertyName("exposure");
        WriteDispatchExposure(writer, row.Exposure);
        writer.WriteString("state", row.State switch
        {
            CampaignDispatchObservationState.Reserved => "reserved",
            CampaignDispatchObservationState.Settled => "settled",
            CampaignDispatchObservationState.NotSent => "not-sent",
            CampaignDispatchObservationState.Unknown => "unknown",
            _ => throw Vocabulary(),
        });
        writer.WriteString("disposition", row.Disposition switch
        {
            DocumentationScribeDispatchDisposition.Success => "success",
            DocumentationScribeDispatchDisposition.RetryableFailure => "retryable-failure",
            DocumentationScribeDispatchDisposition.TerminalFailure => "terminal-failure",
            DocumentationScribeDispatchDisposition.Interrupted => "interrupted",
            null => null,
            _ => throw Vocabulary(),
        });
        writer.WritePropertyName("usage");
        WriteDispatchUsage(writer, row.Usage);
        writer.WriteString("cache", DocumentationScribeVocabulary.GetId(row.Cache));
        if (row.ElapsedMilliseconds is { } elapsed) writer.WriteNumber("elapsedMilliseconds", elapsed);
        else writer.WriteNull("elapsedMilliseconds");
        writer.WriteNumber("retryAfterMilliseconds", row.RetryAfterMilliseconds);
        writer.WritePropertyName("charges");
        if (row.Charges is not { } charges) writer.WriteNullValue();
        else
        {
            writer.WriteStartObject();
            WriteCharge(writer, "providerRequests", charges.ProviderRequests);
            WriteCharge(writer, "inputTokens", charges.InputTokens);
            WriteCharge(writer, "cachedInputTokens", charges.CachedInputTokens);
            WriteCharge(writer, "uncachedInputTokens", charges.UncachedInputTokens);
            WriteCharge(writer, "outputTokens", charges.OutputTokens);
            WriteCharge(writer, "reasoningTokens", charges.ReasoningTokens);
            WriteCharge(writer, "costMicrounits", charges.CostMicrounits);
            WriteCharge(writer, "activeElapsedMilliseconds", charges.ActiveElapsedMilliseconds);
            writer.WriteEndObject();
        }
        writer.WriteString("settlementCommitmentSha256", row.SettlementCommitmentSha256);
        writer.WriteEndObject();
    }

    private static void WriteDispatchExposure(Utf8JsonWriter writer, CampaignProviderReservationExposure exposure)
    {
        writer.WriteStartObject();
        writer.WriteNumber("providerRequests", exposure.ProviderRequests);
        writer.WriteNumber("inputTokens", exposure.InputTokens);
        writer.WriteNumber("cachedInputTokens", exposure.CachedInputTokens);
        writer.WriteNumber("uncachedInputTokens", exposure.UncachedInputTokens);
        writer.WriteNumber("outputTokens", exposure.OutputTokens);
        writer.WriteNumber("costMicrounits", exposure.CostMicrounits);
        writer.WriteNumber("elapsedMilliseconds", exposure.ElapsedMilliseconds);
        writer.WriteEndObject();
    }

    private static void WriteDispatchUsage(Utf8JsonWriter writer, DocumentationScribeDispatchUsage? usage)
    {
        if (usage is null) { writer.WriteNullValue(); return; }
        writer.WriteStartObject();
        if (usage.InputTokens is { } inputTokens) writer.WriteNumber("inputTokens", inputTokens);
        else writer.WriteNull("inputTokens");
        if (usage.CachedInputTokens is { } cachedInputTokens) writer.WriteNumber("cachedInputTokens", cachedInputTokens);
        else writer.WriteNull("cachedInputTokens");
        if (usage.UncachedInputTokens is { } uncachedInputTokens) writer.WriteNumber("uncachedInputTokens", uncachedInputTokens);
        else writer.WriteNull("uncachedInputTokens");
        if (usage.OutputTokens is { } outputTokens) writer.WriteNumber("outputTokens", outputTokens);
        else writer.WriteNull("outputTokens");
        if (usage.ReasoningTokens is { } reasoningTokens) writer.WriteNumber("reasoningTokens", reasoningTokens);
        else writer.WriteNull("reasoningTokens");
        if (usage.CurrencyId is { } currencyId) writer.WriteString("currencyId", currencyId);
        else writer.WriteNull("currencyId");
        if (usage.CostMicrounits is { } costMicrounits) writer.WriteNumber("costMicrounits", costMicrounits);
        else writer.WriteNull("costMicrounits");
        writer.WriteEndObject();
    }

    private static CampaignDispatchObservation ParseDispatchObservation(JsonElement element)
    {
        ExpectObject(element,
            "executionCommitmentSha256", "batchIdentity", "configurationCommitmentSha256", "workItemKey", "attemptId", "scribeRequestSha256", "executionStartRevision", "dispatchOrdinal", "logicalAttemptNumber", "providerRequestNumber", "requestCommitmentSha256", "exposure", "state", "disposition", "usage", "cache", "elapsedMilliseconds", "retryAfterMilliseconds", "charges", "settlementCommitmentSha256");
        if (!DocumentationScribeAttemptId.TryParse(ReadString(element, "attemptId"), out var attempt)) throw Vocabulary();
        return new(
            ReadString(element, "executionCommitmentSha256"),
            ReadString(element, "batchIdentity"),
            ReadString(element, "configurationCommitmentSha256"),
            ReadString(element, "workItemKey"),
            attempt,
            ReadString(element, "scribeRequestSha256"),
            ReadInt64(element, "executionStartRevision"),
            ReadInt64(element, "dispatchOrdinal"),
            ReadInt32(element, "logicalAttemptNumber"),
            ReadInt32(element, "providerRequestNumber"),
            ReadString(element, "requestCommitmentSha256"),
            ParseDispatchExposure(element.GetProperty("exposure")),
            ReadString(element, "state") switch
            {
                "reserved" => CampaignDispatchObservationState.Reserved,
                "settled" => CampaignDispatchObservationState.Settled,
                "not-sent" => CampaignDispatchObservationState.NotSent,
                "unknown" => CampaignDispatchObservationState.Unknown,
                _ => throw Vocabulary(),
            },
            ReadNullableString(element, "disposition") switch
            {
                "success" => DocumentationScribeDispatchDisposition.Success,
                "retryable-failure" => DocumentationScribeDispatchDisposition.RetryableFailure,
                "terminal-failure" => DocumentationScribeDispatchDisposition.TerminalFailure,
                "interrupted" => DocumentationScribeDispatchDisposition.Interrupted,
                null => (DocumentationScribeDispatchDisposition?)null,
                _ => throw Vocabulary(),
            },
            ParseDispatchUsage(element.GetProperty("usage")),
            ReadString(element, "cache") switch
            {
                "cache.hit" => DocumentationScribeCacheObservation.Hit,
                "cache.miss" => DocumentationScribeCacheObservation.Miss,
                "cache.mixed" => DocumentationScribeCacheObservation.Mixed,
                "cache.not-reported" => DocumentationScribeCacheObservation.NotReported,
                _ => throw Vocabulary(),
            },
            ReadNullableInt32(element, "elapsedMilliseconds"), ReadInt32(element, "retryAfterMilliseconds"),
            ParseDispatchCharges(element.GetProperty("charges")), ReadNullableString(element, "settlementCommitmentSha256"));
    }

    private static CampaignProviderReservationExposure ParseDispatchExposure(JsonElement element)
    {
        ExpectObject(element, "providerRequests", "inputTokens", "cachedInputTokens", "uncachedInputTokens", "outputTokens", "costMicrounits", "elapsedMilliseconds");
        return new(ReadInt32(element, "providerRequests"), ReadInt32(element, "inputTokens"), ReadInt32(element, "uncachedInputTokens"), ReadInt32(element, "outputTokens"), ReadInt64(element, "costMicrounits"), ReadInt32(element, "elapsedMilliseconds"))
        { CachedInputTokens = ReadInt32(element, "cachedInputTokens") };
    }

    private static DocumentationScribeDispatchUsage? ParseDispatchUsage(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null) return null;
        ExpectObject(element, "inputTokens", "cachedInputTokens", "uncachedInputTokens", "outputTokens", "reasoningTokens", "currencyId", "costMicrounits");
        return new(ReadNullableInt32(element, "inputTokens"), ReadNullableInt32(element, "cachedInputTokens"), ReadNullableInt32(element, "uncachedInputTokens"), ReadNullableInt32(element, "outputTokens"), ReadNullableInt32(element, "reasoningTokens"), ReadNullableString(element, "currencyId"), ReadNullableInt64(element, "costMicrounits"));
    }

    private static CampaignDispatchCharges? ParseDispatchCharges(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null) return null;
        ExpectObject(element, "providerRequests", "inputTokens", "cachedInputTokens", "uncachedInputTokens", "outputTokens", "reasoningTokens", "costMicrounits", "activeElapsedMilliseconds");
        return new(ParseCharge(element.GetProperty("providerRequests")), ParseCharge(element.GetProperty("inputTokens")), ParseCharge(element.GetProperty("cachedInputTokens")), ParseCharge(element.GetProperty("uncachedInputTokens")), ParseCharge(element.GetProperty("outputTokens")), ParseCharge(element.GetProperty("reasoningTokens")), ParseCharge(element.GetProperty("costMicrounits")), ParseCharge(element.GetProperty("activeElapsedMilliseconds")));
    }
}

public static partial class CampaignStateJson
{
    internal static long MeasureDispatchSettlementGrowth(CampaignCheckpointState state)
    {
        if (state.DispatchObservations.LastOrDefault() is not { State: CampaignDispatchObservationState.Reserved } row)
            return 0;
        const long maximum = CampaignStateContract.MaximumObservation;
        var charge = new CampaignChargeObservation(maximum, maximum, maximum);
        var upper = row with
        {
            State = CampaignDispatchObservationState.Settled,
            Disposition = DocumentationScribeDispatchDisposition.RetryableFailure,
            Usage = new(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue,
                new string('a', 128), maximum),
            Cache = DocumentationScribeCacheObservation.NotReported,
            ElapsedMilliseconds = int.MaxValue,
            RetryAfterMilliseconds = 300_000,
            Charges = new(charge, charge, charge, charge, charge, charge, charge, charge),
            SettlementCommitmentSha256 = new string('f', 64),
        };
        static long Measure(CampaignDispatchObservation value)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream)) WriteDispatchObservation(writer, value);
            return stream.Length;
        }
        // Encoding-only upper bound. It creates neither a semantic artifact nor a send permit.
        return Math.Max(0, Measure(upper) - Measure(row));
    }
}
