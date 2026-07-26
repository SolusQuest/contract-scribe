using System.Security.Cryptography;
using System.Text.Json;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: ContractScribe.ContractBaselineProbe <json-file>");
    return 2;
}

using var document = JsonDocument.Parse(
    File.ReadAllBytes(args[0]),
    new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
using var stream = new MemoryStream();
using (var writer = new Utf8JsonWriter(stream))
{
    WriteCanonical(writer, document.RootElement);
}

Console.WriteLine(Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant());
return 0;

static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
{
    switch (value.ValueKind)
    {
        case JsonValueKind.Object:
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value);
            }
            writer.WriteEndObject();
            break;
        case JsonValueKind.Array:
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray())
            {
                WriteCanonical(writer, item);
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
            throw new InvalidOperationException($"Unsupported JSON kind {value.ValueKind}.");
    }
}
