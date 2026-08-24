using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ContractScribe.Core;

internal static class CampaignPlanningProjectionCanonicalizer
{
    private const int MaximumDepth = 32;
    private const int MaximumProperties = 4_096;
    private const int MaximumArrayItems = 16_384;
    private const int MaximumScalars = 32_768;
    private const int MaximumScalarUtf8Bytes = 65_536;
    private const int MaximumNumberCharacters = 128;
    private const int MaximumCanonicalUtf8Bytes = 1_048_576;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static byte[] Canonicalize(JsonElement root)
    {
        try
        {
            var counts = new ProjectionCounts();
            Validate(root, 1, counts);
            Require(counts.EstimatedCanonicalBytes + 1 <= MaximumCanonicalUtf8Bytes);

            using var stream = new MemoryStream((int)counts.EstimatedCanonicalBytes + 1);
            using (var writer = new Utf8JsonWriter(
                stream,
                new JsonWriterOptions
                {
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    Indented = false,
                    SkipValidation = false,
                }))
            {
                WriteValue(writer, root);
            }

            Require(stream.Length + 1 <= MaximumCanonicalUtf8Bytes);
            stream.WriteByte((byte)'\n');
            return stream.ToArray();
        }
        catch (CampaignPlanningValidationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException
            or AuditValidationException
            or EncoderFallbackException
            or InvalidOperationException
            or JsonException
            or OverflowException)
        {
            throw Failure();
        }
    }

    private static void Validate(JsonElement value, int depth, ProjectionCounts counts)
    {
        Require(depth <= MaximumDepth);
        counts.AddEstimatedBytes(2);
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    Require(names.Add(property.Name));
                    counts.PropertyCount = checked(counts.PropertyCount + 1);
                    Require(counts.PropertyCount <= MaximumProperties);
                    counts.AddEscapedText(property.Name);
                    Validate(property.Value, depth + 1, counts);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    counts.ArrayItemCount = checked(counts.ArrayItemCount + 1);
                    Require(counts.ArrayItemCount <= MaximumArrayItems);
                    Validate(item, depth + 1, counts);
                }
                break;
            case JsonValueKind.String:
                counts.ScalarCount = checked(counts.ScalarCount + 1);
                Require(counts.ScalarCount <= MaximumScalars);
                counts.AddEscapedText(value.GetString()!);
                break;
            case JsonValueKind.Number:
                counts.ScalarCount = checked(counts.ScalarCount + 1);
                Require(counts.ScalarCount <= MaximumScalars);
                var number = value.GetRawText();
                Require(number.Length <= MaximumNumberCharacters);
                AuditJsonModel.ValidateCanonicalInteger(number);
                counts.AddEstimatedBytes(number.Length);
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                counts.ScalarCount = checked(counts.ScalarCount + 1);
                Require(counts.ScalarCount <= MaximumScalars);
                counts.AddEstimatedBytes(5);
                break;
            default:
                throw Failure();
        }
    }

    private static void WriteValue(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject()
                             .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteValue(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteValue(writer, item);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(value.GetRawText(), skipInputValidation: false);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw Failure();
        }
    }

    private static void Require(bool condition)
    {
        if (!condition)
        {
            throw Failure();
        }
    }

    private static CampaignPlanningValidationException Failure() => new(
        CampaignPlanningValidationCode.InvalidConfiguration,
        "Configuration projection violates the closed campaign-planning JSON bounds or shape.");

    private sealed class ProjectionCounts
    {
        internal int PropertyCount { get; set; }
        internal int ArrayItemCount { get; set; }
        internal int ScalarCount { get; set; }
        internal long EstimatedCanonicalBytes { get; private set; }

        internal void AddEscapedText(string text)
        {
            var bytes = StrictUtf8.GetByteCount(text);
            Require(bytes <= MaximumScalarUtf8Bytes);
            AddEstimatedBytes(checked((long)bytes * 6 + 3));
        }

        internal void AddEstimatedBytes(long bytes)
        {
            EstimatedCanonicalBytes = checked(EstimatedCanonicalBytes + bytes);
            Require(EstimatedCanonicalBytes <= MaximumCanonicalUtf8Bytes);
        }
    }
}
