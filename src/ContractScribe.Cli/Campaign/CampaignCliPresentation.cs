using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ContractScribe.Cli;

internal sealed record CampaignTerminal(
    string TerminalLayer,
    CampaignOperation? Operation,
    string Outcome,
    long? CheckpointRevision);

internal static class CampaignCliPresentation
{
    private static readonly HashSet<string> TerminalLayers = new(StringComparer.Ordinal)
    {
        "usage", "preflight", "state", "execution", "campaign",
    };

    private static readonly HashSet<string> Outcomes = new(StringComparer.Ordinal)
    {
        "campaign.complete", "campaign.no-work", "campaign.invalid-command",
        "campaign.provider-retryable", "campaign.budget-exhausted", "campaign.attempt-ambiguous",
        "campaign.invalid-configuration", "campaign.state-missing", "campaign.state-present",
        "campaign.state-corrupt", "campaign.state-unsafe", "campaign.state-conflict",
        "campaign.lease-conflict", "campaign.lease-unverifiable", "campaign.unsupported-revision",
        "campaign.incompatible-snapshot", "campaign.load-failure", "campaign.target-terminal",
        "campaign.provider-terminal", "campaign.proposal-invalid", "campaign.patch-stale",
        "campaign.patch-rejected", "campaign.patch-host-failure",
        "campaign.state-publication-failure", "campaign.host-contract-error",
        "campaign.cancelled", "campaign.timeout",
    };

    internal static CliExecutionResult Usage(
        CliBuildIdentity identity,
        CampaignUsageFailure failure)
    {
        var diagnostic = CliDiagnostics.Create(failure.Code);
        return Result(identity, 2, failure.Operation, "usage", "campaign.invalid-command", null, [diagnostic]);
    }

    internal static CliExecutionResult Present(CliBuildIdentity identity, CampaignTerminal terminal)
    {
        if (!TerminalLayers.Contains(terminal.TerminalLayer) || !Outcomes.Contains(terminal.Outcome))
        {
            terminal = terminal with
            {
                TerminalLayer = "execution",
                Outcome = "campaign.host-contract-error",
            };
        }
        var exitCode = ExitCode(terminal.Outcome);
        IReadOnlyList<CliDiagnostic> diagnostics = exitCode == 0
            ? []
            : [new CliDiagnostic(terminal.Outcome, $"campaign stopped: {terminal.Outcome}")];
        return Result(
            identity,
            exitCode,
            terminal.Operation,
            terminal.TerminalLayer,
            terminal.Outcome,
            terminal.CheckpointRevision,
            diagnostics);
    }

    private static CliExecutionResult Result(
        CliBuildIdentity identity,
        int exitCode,
        CampaignOperation? operation,
        string terminalLayer,
        string outcome,
        long? checkpointRevision,
        IReadOnlyList<CliDiagnostic> diagnostics)
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
            writer.WriteNumber("campaignEnvelopeVersion", 1);
            writer.WriteString("terminalLayer", terminalLayer);
            writer.WriteString("cliContractBaseline", identity.CliContractBaseline);
            writer.WriteString("toolVersion", identity.ToolVersion);
            if (operation is null)
            {
                writer.WriteNull("operation");
            }
            else
            {
                writer.WriteString("operation", operation == CampaignOperation.Start ? "start" : "resume");
            }
            writer.WriteString("outcome", outcome);
            writer.WriteStartArray("diagnosticCodes");
            foreach (var diagnostic in diagnostics)
            {
                writer.WriteStringValue(diagnostic.Code);
            }
            writer.WriteEndArray();
            if (checkpointRevision is null)
            {
                writer.WriteNull("checkpointRevision");
            }
            else
            {
                writer.WriteNumber("checkpointRevision", checkpointRevision.Value);
            }
            writer.WriteEndObject();
        }

        return new CliExecutionResult(
            exitCode,
            Encoding.UTF8.GetString(buffer.WrittenSpan) + "\n",
            diagnostics);
    }

    private static int ExitCode(string outcome) => outcome switch
    {
        "campaign.complete" or "campaign.no-work" => 0,
        "campaign.invalid-command" => 2,
        "campaign.provider-retryable" or "campaign.budget-exhausted" or "campaign.attempt-ambiguous" => 3,
        "campaign.invalid-configuration" or "campaign.state-missing" or "campaign.state-present"
            or "campaign.state-corrupt" or "campaign.state-unsafe" or "campaign.state-conflict"
            or "campaign.lease-conflict" or "campaign.lease-unverifiable"
            or "campaign.unsupported-revision" or "campaign.incompatible-snapshot" => 4,
        "campaign.load-failure" or "campaign.target-terminal" or "campaign.provider-terminal"
            or "campaign.proposal-invalid" or "campaign.patch-stale" or "campaign.patch-rejected"
            or "campaign.patch-host-failure" or "campaign.state-publication-failure"
            or "campaign.host-contract-error" => 5,
        "campaign.cancelled" => 6,
        "campaign.timeout" => 7,
        _ => 5,
    };
}
