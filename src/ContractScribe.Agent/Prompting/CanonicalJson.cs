using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ContractScribe.Core;

namespace ContractScribe.Agent.Prompting;

internal static class CanonicalJson
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    internal static ImmutableArray<byte> Serialize<T>(T value)
    {
        var element = JsonSerializer.SerializeToElement(value, SerializerOptions);
        return Write(element, rejectDuplicateProperties: true);
    }

    internal static ImmutableArray<byte> Normalize(
        ReadOnlyMemory<byte> utf8Json,
        bool rejectDuplicateProperties = true)
    {
        using var document = JsonDocument.Parse(
            utf8Json,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = DocumentationScribeContract.MaximumJsonDepth,
            });
        return Write(document.RootElement, rejectDuplicateProperties);
    }

    internal static string AsString(ImmutableArray<byte> utf8) => Encoding.UTF8.GetString(utf8.AsSpan());

    private static ImmutableArray<byte> Write(JsonElement element, bool rejectDuplicateProperties)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            WriteElement(writer, element, rejectDuplicateProperties);
        }

        return buffer.WrittenSpan.ToArray().ToImmutableArray();
    }

    private static void WriteElement(
        Utf8JsonWriter writer,
        JsonElement element,
        bool rejectDuplicateProperties)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    var properties = element.EnumerateObject().ToArray();
                    if (rejectDuplicateProperties)
                    {
                        var names = new HashSet<string>(StringComparer.Ordinal);
                        if (properties.Any(property => !names.Add(property.Name)))
                        {
                            throw new JsonException("Duplicate JSON properties are not canonical.");
                        }
                    }

                    writer.WriteStartObject();
                    foreach (var property in properties.OrderBy(property => property.Name, StringComparer.Ordinal))
                    {
                        writer.WritePropertyName(property.Name);
                        WriteElement(writer, property.Value, rejectDuplicateProperties);
                    }

                    writer.WriteEndObject();
                    break;
                }

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteElement(writer, item, rejectDuplicateProperties);
                }

                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(NormalizeString(element.GetString()!));
                break;

            case JsonValueKind.Number:
                if (element.TryGetInt64(out var integer))
                {
                    writer.WriteNumberValue(integer);
                }
                else if (element.TryGetDecimal(out var decimalValue))
                {
                    writer.WriteNumberValue(decimalValue);
                }
                else
                {
                    writer.WriteNumberValue(element.GetDouble());
                }

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
                throw new JsonException("An unsupported JSON value was encountered.");
        }
    }

    private static string NormalizeString(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n');
}
