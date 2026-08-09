using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ContractScribe.Core.Hosting;

namespace ContractScribe.Cli;

internal sealed record CliExecutionResult(
    int ExitCode,
    string StandardOutput,
    IReadOnlyList<CliDiagnostic> Diagnostics);

internal sealed record AuditCounts(int Compliant, int Violation, int Skipped);

internal static class CliPresentation
{
    public static CliExecutionResult Usage(
        CliBuildIdentity identity,
        AuditUsageFailure failure)
    {
        var diagnostic = CliDiagnostics.Create(failure.Code);
        return Result(2, [diagnostic], writer =>
        {
            WriteCommon(writer, identity, "usage", [diagnostic]);
            writer.WriteString("usageClass", failure.UsageClass);
        });
    }

    public static CliExecutionResult Preflight(CliBuildIdentity identity, string code)
    {
        var diagnostic = CliDiagnostics.Create(code);
        return Result(4, [diagnostic], writer =>
        {
            WriteCommon(writer, identity, "preflight", [diagnostic]);
            writer.WriteString("executionClass", "invalid-input");
        });
    }

    public static CliExecutionResult Execution(
        CliBuildIdentity identity,
        HostTerminalRecord terminal,
        int exitCode,
        string executionClass,
        IReadOnlyList<CliDiagnostic> diagnostics)
    {
        return Result(exitCode, diagnostics, writer =>
        {
            WriteCommon(writer, identity, "execution", diagnostics);
            writer.WriteString("terminalState", "committed");
            writer.WriteString("sourceRevision", terminal.Provenance.SourceRevision);
            WriteToolchain(writer, terminal.Toolchain);
            writer.WriteString("executionClass", executionClass);
        });
    }

    public static CliExecutionResult Audit(
        CliBuildIdentity identity,
        HostTerminalRecord terminal,
        int exitCode,
        string disposition,
        AuditCounts counts,
        IReadOnlyList<CliDiagnostic> diagnostics)
    {
        return Result(exitCode, diagnostics, writer =>
        {
            WriteCommon(writer, identity, "audit", diagnostics);
            writer.WriteString("terminalState", "committed");
            writer.WriteString("sourceRevision", terminal.Provenance.SourceRevision);
            WriteToolchain(writer, terminal.Toolchain);
            writer.WriteString("disposition", disposition);
            writer.WriteStartObject("counts");
            writer.WriteNumber("compliant", counts.Compliant);
            writer.WriteNumber("violation", counts.Violation);
            writer.WriteNumber("skipped", counts.Skipped);
            writer.WriteEndObject();
            writer.WriteString("resultDigest", terminal.OutputCommit.Sha256);
            writer.WriteStartObject("outputCommit");
            writer.WriteString("status", "committed");
            writer.WriteString("identity", terminal.OutputCommit.Sha256);
            writer.WriteEndObject();
        });
    }

    public static CliExecutionResult HostContractError(CliBuildIdentity identity)
    {
        var diagnostic = CliDiagnostics.Create("cli.host.unknown-terminal");
        return Result(5, [diagnostic], writer =>
            WriteCommon(writer, identity, "host-contract-error", [diagnostic]));
    }

    private static CliExecutionResult Result(
        int exitCode,
        IReadOnlyList<CliDiagnostic> diagnostics,
        Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(
                   buffer,
                   new JsonWriterOptions
                   {
                       Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                       Indented = false,
                   }))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }
        return new CliExecutionResult(
            exitCode,
            Encoding.UTF8.GetString(buffer.WrittenSpan) + "\n",
            diagnostics);
    }

    private static void WriteCommon(
        Utf8JsonWriter writer,
        CliBuildIdentity identity,
        string terminalLayer,
        IReadOnlyList<CliDiagnostic> diagnostics)
    {
        writer.WriteNumber("envelopeVersion", 1);
        writer.WriteString("terminalLayer", terminalLayer);
        writer.WriteString("cliContractBaseline", identity.CliContractBaseline);
        writer.WriteString("toolVersion", identity.ToolVersion);
        writer.WriteStartArray("diagnosticCodes");
        foreach (var diagnostic in diagnostics)
        {
            writer.WriteStringValue(diagnostic.Code);
        }
        writer.WriteEndArray();
    }

    private static void WriteToolchain(
        Utf8JsonWriter writer,
        HostToolchainFact toolchain)
    {
        if (toolchain.SelectionState != HostToolchainSelectionState.Selected)
        {
            return;
        }
        writer.WriteString(
            "toolchain",
            $"sdk={toolchain.SdkVersion};runtime={toolchain.RuntimeVersion};msbuild={toolchain.MsbuildVersion};architecture={toolchain.Architecture}");
    }
}
