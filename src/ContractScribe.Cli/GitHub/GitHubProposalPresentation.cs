using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ContractScribe.Core;
using ContractScribe.GitHub.Publication;

namespace ContractScribe.Cli;

internal static class GitHubProposalPresentation
{
    internal static CliExecutionResult Campaign(CliBuildIdentity identity, CampaignTerminal terminal)
    {
        var outcome = terminal.Outcome switch
        {
            "campaign.complete" or "campaign.no-work" => "no-op",
            "campaign.provider-retryable" or "campaign.budget-exhausted" or "campaign.attempt-ambiguous" => "conflict",
            "campaign.invalid-configuration" or "campaign.state-missing" or "campaign.state-present"
                or "campaign.state-corrupt" or "campaign.state-unsafe" or "campaign.state-conflict"
                or "campaign.lease-conflict" or "campaign.lease-unverifiable" or "campaign.unsupported-revision" => "local-invalid",
            "campaign.incompatible-snapshot" or "campaign.patch-stale" => "stale",
            "campaign.load-failure" or "campaign.target-terminal" or "campaign.provider-terminal"
                or "campaign.proposal-invalid" or "campaign.patch-rejected" or "campaign.patch-host-failure"
                or "campaign.state-publication-failure" or "campaign.host-contract-error" => "host-failure",
            "campaign.cancelled" => "cancelled",
            "campaign.timeout" => "timeout",
            _ => null,
        };
        if (outcome is null) return ContractError(identity, terminal.Operation, terminal.CheckpointRevision);
        var diagnostics = outcome == "no-op" ? Array.Empty<CliDiagnostic>() :
            [new CliDiagnostic(terminal.Outcome, "github proposal stopped before publication: " + terminal.Outcome)];
        return Result(identity, terminal.Operation, "campaign", outcome, terminal.CheckpointRevision, diagnostics);
    }

    internal static CliExecutionResult ContractError(CliBuildIdentity identity, CampaignOperation? operation, long? revision) =>
        Result(identity, operation, "presentation", "host-failure", revision,
            [new("github-proposal.campaign-contract-error",
                "github proposal stopped before publication: github-proposal.campaign-contract-error")]);

    internal static CliExecutionResult Usage(CliBuildIdentity identity, CampaignUsageFailure failure) =>
        Result(identity, failure.Operation, "usage", "local-invalid", null,
            [CliDiagnostics.Create(failure.Code)], exitOverride: 2);

    internal static CliExecutionResult Local(CliBuildIdentity identity, CampaignOperation? operation,
        string outcome, long? revision = null) =>
        Result(identity, operation, "preflight", outcome, revision,
            [new("github-proposal." + outcome, "github proposal stopped before publication: github-proposal." + outcome)]);

    internal static CliExecutionResult Publication(CliBuildIdentity identity, CampaignOperation operation,
        long revision, GitHubPublicationObservation observation)
    {
        var outcome = observation.Result.Kind switch
        {
            GitHubPublicationResultKind.LocalInvalid => "local-invalid",
            GitHubPublicationResultKind.ReplayNoOp => "replayed",
            GitHubPublicationResultKind.Admitted or GitHubPublicationResultKind.RecoveredContentPartial
                or GitHubPublicationResultKind.RecoveredRefPartial => "conflict",
            GitHubPublicationResultKind.Published => "published",
            GitHubPublicationResultKind.AwaitingReview => "awaiting-review",
            GitHubPublicationResultKind.Merged => "merged",
            GitHubPublicationResultKind.ClosedUnmerged => "closed-unmerged",
            GitHubPublicationResultKind.StaleBaseAfterCreate => "stale-base-after-create",
            GitHubPublicationResultKind.Stale => "stale",
            GitHubPublicationResultKind.HumanChange => "human-change",
            GitHubPublicationResultKind.Conflict => "conflict",
            GitHubPublicationResultKind.Permission => "permission",
            GitHubPublicationResultKind.RateLimit => "rate-limit",
            GitHubPublicationResultKind.Cancelled => "cancelled",
            GitHubPublicationResultKind.Timeout => "timeout",
            _ => "host-failure",
        };
        var code = "github-proposal." + (observation.Result.Kind switch
        {
            GitHubPublicationResultKind.Admitted => "admitted",
            GitHubPublicationResultKind.RecoveredContentPartial => "recovered-content-partial",
            GitHubPublicationResultKind.RecoveredRefPartial => "recovered-ref-partial",
            _ => outcome,
        });
        return Result(identity, operation, "publication", outcome, revision,
            Exit(outcome) == 0 ? [] : [new(code, "github proposal publication stopped: " + code)],
            observation.OperationId, observation.GenerationId, observation.PullRequestUrl) with
        { IsPublication = true };
    }

    private static int Exit(string outcome) => outcome switch
    {
        "published" or "replayed" or "no-op" or "awaiting-review" or "merged" => 0,
        "stale-base-after-create" or "rate-limit" or "conflict" => 3,
        "local-invalid" or "stale" or "human-change" or "permission" or "closed-unmerged" => 4,
        "cancelled" => 6,
        "timeout" => 7,
        _ => 5,
    };

    private static CliExecutionResult Result(CliBuildIdentity identity, CampaignOperation? operation,
        string layer, string outcome, long? revision, IReadOnlyList<CliDiagnostic> diagnostics,
        string? publicationOperation = null, string? generation = null, string? url = null, int? exitOverride = null)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("githubProposalEnvelopeVersion", 1);
            writer.WriteString("terminalLayer", layer);
            writer.WriteString("cliContractBaseline", identity.CliContractBaseline);
            writer.WriteString("toolVersion", identity.ToolVersion);
            writer.WriteString("campaignOperation", operation switch
            {
                CampaignOperation.Start => "start",
                CampaignOperation.Resume => "resume",
                _ => null,
            });
            writer.WriteString("publicationOperationId", publicationOperation);
            writer.WriteString("generationId", generation);
            writer.WriteString("outcome", "github-proposal." + outcome);
            writer.WriteStartArray("diagnosticCodes");
            foreach (var diagnostic in diagnostics) writer.WriteStringValue(diagnostic.Code);
            writer.WriteEndArray();
            if (revision is null) writer.WriteNull("checkpointRevision");
            else writer.WriteNumber("checkpointRevision", revision.Value);
            writer.WriteString("pullRequestUrl", url);
            writer.WriteEndObject();
        }
        return new CliExecutionResult(exitOverride ?? Exit(outcome), Encoding.UTF8.GetString(buffer.WrittenSpan) + "\n", diagnostics)
        { AuthoritativeCheckpointRevision = revision };
    }

    internal static void Write(CliExecutionResult result, TextWriter output, TextWriter error)
    {
        foreach (var diagnostic in result.Diagnostics)
            error.Write(diagnostic.Code.StartsWith("cli.usage.", StringComparison.Ordinal)
                ? diagnostic.ToLine() : diagnostic.Message + "\n");
        output.Write(result.StandardOutput);
    }
}
