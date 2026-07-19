using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ContractScribe.Roslyn;

public static class SemanticPayloadSerializer
{
    public static byte[] Serialize(SemanticPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   stream,
                   new JsonWriterOptions
                   {
                       Encoder = JavaScriptEncoder.Default,
                       Indented = false,
                   }))
        {
            writer.WriteStartObject();
            writer.WriteString("formatVersion", ExperimentFormat.SemanticPayloadVersion);
            writer.WritePropertyName("projects");
            writer.WriteStartArray();

            foreach (var project in payload.Projects.OrderBy(project => project.ProjectId, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("projectId", project.ProjectId);
                writer.WritePropertyName("symbols");
                writer.WriteStartArray();

                foreach (var symbol in project.Symbols
                             .OrderBy(symbol => symbol.DocumentationCommentId, StringComparer.Ordinal))
                {
                    writer.WriteStartObject();
                    writer.WriteString("documentationCommentId", symbol.DocumentationCommentId);
                    writer.WriteString("kind", symbol.Kind);
                    writer.WriteString("name", symbol.Name);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
        }

        return stream.ToArray();
    }

    public static string DecodeUtf8(byte[] bytes)
    {
        return Encoding.UTF8.GetString(bytes);
    }
}
