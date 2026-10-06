using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Core;

namespace ContractScribe.Tests;

public sealed partial class CampaignStateContractTests
{
    public static IEnumerable<object?[]> ClosedOutcomeSourceCases()
    {
        var shapes = new (string Stage, string Code, string? Disposition)[]
        {
            ("planning", "planning-terminal", null),
            ("patch", "patch-rejected", null),
            ("scribe", "insufficient-evidence", null),
            ("scribe", "unsupported-domain", null),
            ("scribe", "provider-failure", "retryable"),
            ("scribe", "provider-failure", "terminal"),
            ("scribe", "tool-protocol-failure", null),
            ("scribe", "validation-failure", null),
            ("scribe", "internal-failure", null),
            ("scribe", "cancelled-by-caller", null),
            ("scribe", "cancelled-by-shutdown", null),
            ("scribe", "timeout", null),
            ("scribe", "budget-exhausted", null),
            ("scribe", "completed-over-bound", null),
        };
        foreach (var shape in shapes)
        {
            foreach (var source in new string?[] { null, "scribe-result", "recovered-dispatch-failure" })
            {
                foreach (var hasFailureCommitment in new[] { false, true })
                {
                    var valid = shape.Stage == "scribe"
                        ? source == "scribe-result" && !hasFailureCommitment
                            || source == "recovered-dispatch-failure"
                                && shape.Code == "provider-failure" && hasFailureCommitment
                        : source is null && !hasFailureCommitment;
                    yield return new object?[]
                    {
                        shape.Stage, shape.Code, shape.Disposition, source, hasFailureCommitment, valid,
                    };
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(ClosedOutcomeSourceCases))]
    public void Published_closed_outcome_source_matrix_matches_runtime(
        string stage, string code, string? disposition, string? source, bool hasFailureCommitment, bool valid)
    {
        var state = CreateClosedOutcomeSourceState(stage, code, disposition);
        var root = Assert.IsType<JsonObject>(JsonNode.Parse(CampaignStateJson.Write(state)));
        var outcome = root["workItems"]![0]!["closedOutcome"]!;
        outcome["scribeCompletionSource"] = source;
        outcome["acceptedDispatchFailureCommitmentSha256"] = hasFailureCommitment ? Hash('d') : null;

        var bytes = Encoding.UTF8.GetBytes(root.ToJsonString() + "\n");
        var parsed = CampaignStateJson.Parse(bytes);
        Assert.Equal(valid, parsed.IsValid);
        if (!valid)
        {
            Assert.Equal(CampaignStateValidationCode.InvalidCorrelation, parsed.FailureCode);
        }
        else
        {
            AssertPublishedCampaignRoundTrip(parsed.Artifact!.State);
        }

        using var document = JsonDocument.Parse(bytes);
        var evaluation = EvaluateCampaignSchema(document.RootElement);
        Assert.True(evaluation.IsValid == valid, DescribeSchemaFailures(evaluation));
    }

    [Theory]
    [InlineData("unknown-source", null)]
    [InlineData("recovered-dispatch-failure", "short")]
    [InlineData("recovered-dispatch-failure", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("recovered-dispatch-failure", "ddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd")]
    [InlineData("recovered-dispatch-failure", "ddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd")]
    [InlineData("recovered-dispatch-failure", "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd\n")]
    public void Published_closed_outcome_rejects_unknown_source_and_malformed_failure_commitment(
        string source, string? failureCommitment)
    {
        var state = CreateClosedOutcomeSourceState("scribe", "provider-failure", "terminal");
        var root = Assert.IsType<JsonObject>(JsonNode.Parse(CampaignStateJson.Write(state)));
        var outcome = root["workItems"]![0]!["closedOutcome"]!;
        outcome["scribeCompletionSource"] = source;
        outcome["acceptedDispatchFailureCommitmentSha256"] = failureCommitment;
        var bytes = Encoding.UTF8.GetBytes(root.ToJsonString() + "\n");
        Assert.False(CampaignStateJson.Parse(bytes).IsValid);
        using var document = JsonDocument.Parse(bytes);
        Assert.False(EvaluateCampaignSchema(document.RootElement).IsValid);
    }

    private static CampaignCheckpointState CreateClosedOutcomeSourceState(
        string stage, string code, string? disposition)
    {
        var scenario = CreateProposalScenario();
        var exchange = CreateScribeExchange(scenario.Plan.WorkItems[0], scenario: scenario);
        return MutateValidState(scenario.InitialState, root =>
        {
            var work = root["workItems"]![0]!;
            work["status"] = "closed";
            work["trustedProposal"] = null;
            if (code == "completed-over-bound")
            {
                root["terminalOutcome"] = new JsonObject { ["kind"] = "exhausted", ["reason"] = "budget" };
            }
            work["closedOutcome"] = new JsonObject
            {
                ["stage"] = stage,
                ["code"] = code,
                ["providerDisposition"] = disposition,
                ["scribeCompletionSource"] = stage == "scribe" ? "scribe-result" : null,
                ["acceptedDispatchFailureCommitmentSha256"] = null,
                ["scribeRequestSha256"] = stage == "scribe" ? exchange.Request.ArtifactSha256 : null,
                ["scribeResultCommitmentSha256"] = code == "completed-over-bound" ? Hash('c') : null,
                ["attemptId"] = stage == "scribe" ? exchange.Result.AttemptId.Value : null,
                ["patchRequestSha256"] = stage == "patch" ? Hash('a') : null,
                ["patchResultCommitmentSha256"] = stage == "patch" ? Hash('b') : null,
            };
        });
    }

    private static void AssertPublishedCampaignRoundTrip(CampaignCheckpointState state)
    {
        var artifact = CampaignStateJson.CreateArtifact(state);
        using var document = JsonDocument.Parse(artifact.ExactUtf8Json.ToArray());
        var evaluation = EvaluateCampaignSchema(document.RootElement);
        Assert.True(evaluation.IsValid, DescribeSchemaFailures(evaluation));
        var parsed = CampaignStateJson.Parse(artifact.ExactUtf8Json.ToArray());
        Assert.True(parsed.IsValid, parsed.FailureCode?.ToString());
        Assert.Equal(artifact.ExactUtf8Json.ToArray(), CampaignStateJson.CreateArtifact(parsed.Artifact!.State).ExactUtf8Json.ToArray());
        var work = state.WorkItems.First(item => item.ClosedOutcome is not null);
        var before = work.ClosedOutcome!;
        var after = parsed.Artifact.State.WorkItems.Single(item => item.WorkItemKey == work.WorkItemKey).ClosedOutcome!;
        Assert.Equal(before.ScribeCompletionSource, after.ScribeCompletionSource);
        Assert.Equal(before.AcceptedDispatchFailureCommitmentSha256, after.AcceptedDispatchFailureCommitmentSha256);
        Assert.Equal(before.ScribeResultCommitmentSha256, after.ScribeResultCommitmentSha256);
    }
}
