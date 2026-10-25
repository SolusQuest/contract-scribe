using System.Text.Json;
using ContractScribe.Agent.Runtime;
using ContractScribe.Cli;
using ContractScribe.Core;
using ContractScribe.Roslyn;

namespace ContractScribe.Tests;

public sealed partial class DocumentationScribeRuntimeTests
{
    [Fact]
    public void Tool_failure_reference_is_first_wins_and_bound_to_the_active_original_tool_round()
    {
        var state = State(Request(), new ManualTimeProvider());
        state.SetToolBoundary(2);
        state.ObserveToolFailure(1, "repository.read-excerpt", DocumentationScribeToolFailureReason.InvalidRequest);
        state.ObserveToolFailure(2, "repository.search-text", DocumentationScribeToolFailureReason.InvalidCursor);
        state.ObserveToolFailure(2, "repository.read-excerpt", DocumentationScribeToolFailureReason.InvalidRequest);
        state.SetToolBoundary(3);
        state.ObserveToolFailure(3, "get-target-evidence", DocumentationScribeToolFailureReason.Stale);
        var result = state.CreateToolProtocolFailure("sensitive-fallback-not-for-reporting", 0);
        Assert.Equal("campaign.tool-failure.search-text.invalid-cursor", DocumentationScribeToolFailureDiagnostics.FromResult(result));

        var other = State(Request(), new ManualTimeProvider());
        other.SetToolBoundary(3);
        other.ObserveToolFailure(2, "repository.search-text", DocumentationScribeToolFailureReason.InvalidCursor);
        Assert.Null(DocumentationScribeToolFailureDiagnostics.FromResult(other.CreateToolProtocolFailure("tool", 0)));
    }

    [Fact]
    public void An_unknown_first_tool_failure_is_not_overwritten_by_a_later_known_category()
    {
        var state = State(Request(), new ManualTimeProvider()); state.SetToolBoundary(1);
        state.ObserveToolFailure(1, "repository.read-excerpt", DocumentationScribeToolFailureReason.Unknown);
        state.ObserveToolFailure(1, "repository.search-text", DocumentationScribeToolFailureReason.InvalidCursor);
        Assert.Null(DocumentationScribeToolFailureDiagnostics.FromResult(state.CreateToolProtocolFailure("tool", 0)));
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("budget")]
    [InlineData("deadline")]
    [InlineData("settlement-internal")]
    public void A_captured_tool_reason_does_not_override_the_actual_primary_stop(string stop)
    {
        var clock = new ManualTimeProvider();
        var state = State(Request(root => root["limits"]!["maximumElapsedMilliseconds"] = 50), clock);
        state.SetToolBoundary(1);
        state.ObserveToolFailure(1, "repository.search-text", DocumentationScribeToolFailureReason.InvalidCursor);
        using var cancellation = new CancellationTokenSource();
        if (stop == "cancel") cancellation.Cancel();
        if (stop == "deadline") clock.AdvanceMilliseconds(50);
        var reducer = new DocumentationScribeTerminalReducer();
        var result = stop switch
        {
            "budget" => reducer.CommitFailure(state, default, DocumentationScribeFailureCode.Budget),
            "settlement-internal" => reducer.CommitInternal(state, default),
            _ => reducer.CommitToolProtocol(state, cancellation.Token, "tool"),
        };
        Assert.Null(DocumentationScribeToolFailureDiagnostics.FromResult(result));
        if (stop == "cancel") Assert.Equal(DocumentationScribeTerminalKind.Cancelled, result.Stop.Kind);
        else Assert.Equal(stop == "budget" ? DocumentationScribeFailureCode.Budget
            : stop == "deadline" ? DocumentationScribeFailureCode.Timeout : DocumentationScribeFailureCode.Internal, FailureCode(result));
    }

    [Fact]
    public void Tool_failure_codes_contain_only_fixed_vocabulary_and_unknown_is_not_a_guessed_reason()
    {
        foreach (var reason in Enum.GetValues<DocumentationScribeToolFailureReason>())
        {
            var state = State(Request(), new ManualTimeProvider()); state.SetToolBoundary(1);
            state.ObserveToolFailure(1, "secret/path?cursor=private-marker", reason);
            var result = state.CreateToolProtocolFailure("not-emitted", 0);
            var reference = Assert.Single(result.RunEnvelope.Diagnostics).ReferenceId!;
            Assert.StartsWith("tool-failure.unknown.", reference, StringComparison.Ordinal);
            Assert.DoesNotContain("private-marker", reference, StringComparison.Ordinal);
            var code = DocumentationScribeToolFailureDiagnostics.FromResult(result);
            if (reason == DocumentationScribeToolFailureReason.Unknown) Assert.Null(code);
            else
            {
                Assert.NotNull(code); Assert.InRange(code.Length, 1, 96);
                Assert.True(DocumentationScribeToolFailureDiagnostics.IsKnownCode(code));
            }
        }
        Assert.False(DocumentationScribeToolFailureDiagnostics.IsKnownCode("campaign.tool-failure.read-excerpt.secret-marker"));
        Assert.False(DocumentationScribeToolFailureDiagnostics.IsKnownCode("campaign.tool-failure.secret-path.invalid-request"));
        Assert.False(DocumentationScribeToolFailureDiagnostics.IsKnownCode("campaign." + new string('x', 1024)));
    }

    [Fact]
    public void Typed_repository_failure_category_is_inert_and_does_not_add_a_tool_JSON_field()
    {
        var result = new DocumentationScribeRepositoryReadExcerptResult(DocumentationScribeToolOutcome.Failure,
            DocumentationScribeRepositoryToolFailureCodes.InvalidRequest, null, null, []);
        Assert.Equal(DocumentationScribeToolFailureReason.InvalidRequest, ((IDocumentationScribeToolResult)result).FailureReasonCategory);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));
        Assert.False(json.RootElement.TryGetProperty("FailureReasonCategory", out _));
        Assert.False(json.RootElement.TryGetProperty("failureReasonCategory", out _));
        using var throughInterface = JsonDocument.Parse(JsonSerializer.Serialize<IDocumentationScribeToolResult>(result));
        Assert.False(throughInterface.RootElement.TryGetProperty("FailureReasonCategory", out _));
        Assert.False(throughInterface.RootElement.TryGetProperty("failureReasonCategory", out _));
    }

    [Theory]
    [InlineData("campaign.provider-terminal", true)]
    [InlineData("campaign.cancelled", false)]
    [InlineData("campaign.timeout", false)]
    [InlineData("campaign.state-conflict", false)]
    [InlineData("campaign.complete", false)]
    public void Fixed_tool_observation_keeps_the_original_single_stderr_record_and_primary_outcome(string outcome, bool expected)
    {
        const string code = "campaign.tool-failure.search-text.invalid-cursor";
        var terminal = new CampaignTerminal("execution", CampaignOperation.Start, outcome, 20, code);
        var identity = CliBuildIdentity.Current;
        foreach (var result in new[] { CampaignCliPresentation.Present(identity, terminal), GitHubProposalPresentation.Campaign(identity, terminal) })
        {
            using var json = JsonDocument.Parse(result.StandardOutput);
            var codes = json.RootElement.GetProperty("diagnosticCodes").EnumerateArray().Select(value => value.GetString()).ToArray();
            Assert.Equal(expected, codes.Contains(code, StringComparer.Ordinal));
            Assert.InRange(codes.Length, 0, 2);
            Assert.InRange(result.Diagnostics.Count, 0, 1);
        }
    }
}
