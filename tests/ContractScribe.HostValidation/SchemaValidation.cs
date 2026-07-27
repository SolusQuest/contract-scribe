using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace ContractScribe.HostValidation;

public static class SchemaValidation
{
    private static readonly ConcurrentDictionary<string, Lazy<JsonSchema>> Schemas =
        new(StringComparer.OrdinalIgnoreCase);

    public static void Validate(string documentPath, string schemaPath, bool requireCanonical = false)
    {
        using var document = CanonicalJson.ReadStrict(documentPath, 4 * 1024 * 1024, requireCanonical);
        var schema = Schemas.GetOrAdd(
            Path.GetFullPath(schemaPath),
            path => new Lazy<JsonSchema>(
                () => LoadSchema(path),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;

        var result = schema.Evaluate(document.RootElement, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
            RequireFormatValidation = true
        });
        if (!result.IsValid)
        {
            throw new ProtocolException("HV111_SCHEMA_REJECTED");
        }
    }

    public static void ValidateDefinition(
        string documentPath,
        string schemaPath,
        string definition,
        bool requireCanonical = false)
    {
        using var document = CanonicalJson.ReadStrict(documentPath, 4 * 1024 * 1024, requireCanonical);
        var key = $"{Path.GetFullPath(schemaPath)}#{definition}";
        var schema = Schemas.GetOrAdd(
            key,
            _ => new Lazy<JsonSchema>(
                () => LoadDefinition(schemaPath, definition),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        var result = schema.Evaluate(document.RootElement, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
            RequireFormatValidation = true
        });
        if (!result.IsValid)
        {
            throw new ProtocolException("HV111_SCHEMA_REJECTED");
        }
    }

    private static JsonSchema LoadSchema(string schemaPath)
    {
        using var schemaDocument = CanonicalJson.ReadStrict(schemaPath, 2 * 1024 * 1024);
        try
        {
            return JsonSchema.FromText(schemaDocument.RootElement.GetRawText());
        }
        catch (Exception exception) when (exception is JsonException or JsonSchemaException)
        {
            throw new ProtocolException("HV110_SCHEMA_INVALID", exception);
        }
    }

    private static JsonSchema LoadDefinition(string schemaPath, string definition)
    {
        using var schemaDocument = CanonicalJson.ReadStrict(schemaPath, 2 * 1024 * 1024);
        try
        {
            var root = JsonNode.Parse(schemaDocument.RootElement.GetRawText())!.AsObject();
            var schema = new JsonObject
            {
                ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
                ["$ref"] = $"#/$defs/{definition}",
                ["$defs"] = root["$defs"]!.DeepClone()
            };
            return JsonSchema.FromText(schema.ToJsonString());
        }
        catch (Exception exception) when (exception is JsonException or JsonSchemaException or InvalidOperationException)
        {
            throw new ProtocolException("HV110_SCHEMA_INVALID", exception);
        }
    }
}
