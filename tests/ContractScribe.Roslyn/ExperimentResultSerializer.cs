using System.Text.Encodings.Web;
using System.Text.Json;

namespace ContractScribe.Roslyn;

public static class ExperimentResultSerializer
{
    public static byte[] Serialize(ExperimentResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        result.Validate();

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
            writer.WriteString("formatVersion", result.FormatVersion);
            writer.WriteString("status", ToJsonName(result.Status));

            if (result.FailurePhase is { } phase)
            {
                writer.WriteString("failurePhase", ToJsonName(phase));
            }

            if (result.FailureCode is not null)
            {
                writer.WriteString("failureCode", result.FailureCode);
            }

            if (result.SemanticPayload is not null)
            {
                writer.WritePropertyName("semanticPayload");
                WritePayload(writer, result.SemanticPayload);
            }

            writer.WritePropertyName("diagnostics");
            writer.WriteStartArray();
            foreach (var diagnostic in result.Diagnostics.OrderBy(diagnostic => diagnostic.Code, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("code", diagnostic.Code);
                writer.WriteString("severity", diagnostic.Severity);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            if (result.Toolchain is not null)
            {
                writer.WritePropertyName("toolchain");
                writer.WriteStartObject();
                writer.WriteString("sdkVersion", result.Toolchain.SdkVersion);
                writer.WriteString("msbuildVersion", result.Toolchain.MsbuildVersion);
                writer.WriteString("discoveryType", result.Toolchain.DiscoveryType);
                writer.WriteString("runtimeVersion", result.Toolchain.RuntimeVersion);
                writer.WriteString("processArchitecture", result.Toolchain.ProcessArchitecture);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.Flush();
        }

        return stream.ToArray();
    }

    private static void WritePayload(Utf8JsonWriter writer, SemanticPayload payload)
    {
        writer.WriteStartObject();
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
    }

    private static string ToJsonName(ExperimentStatus status)
    {
        return status switch
        {
            ExperimentStatus.Succeeded => "succeeded",
            ExperimentStatus.ClassifiedFailure => "classified-failure",
            ExperimentStatus.InvalidInput => "invalid-input",
            ExperimentStatus.InternalError => "internal-error",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
        };
    }

    private static string ToJsonName(FailurePhase phase)
    {
        return phase switch
        {
            FailurePhase.Input => "input",
            FailurePhase.MsbuildEnvironment => "msbuild-environment",
            FailurePhase.WorkspaceLoad => "workspace-load",
            FailurePhase.Compilation => "compilation",
            FailurePhase.SymbolIdentity => "symbol-identity",
            FailurePhase.Serialization => "serialization",
            _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null),
        };
    }
}
