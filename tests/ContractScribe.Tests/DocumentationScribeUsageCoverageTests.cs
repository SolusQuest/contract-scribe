using System.Text;
using System.Text.Json.Nodes;
using ContractScribe.Agent.Prompting;
using ContractScribe.Agent.Runtime;
using ContractScribe.Core;

namespace ContractScribe.Tests;

public sealed partial class DocumentationScribeRuntimeTests
{
    public static IEnumerable<object[]> NonTokenCoverageWithoutReports()
    {
        foreach (var observation in new[] { "cost-zero", "cost-positive", "cache-hit", "cache-miss", "cache-mixed" })
            foreach (var parser in new[] { false, true })
                yield return [observation, parser];
    }

    [Theory]
    [MemberData(nameof(NonTokenCoverageWithoutReports))]
    public void Usage_coverage_rejects_non_token_observations_with_zero_reports(string observation, bool parser)
    {
        var request = Request();
        if (!parser)
        {
            Assert.Throws<ArgumentException>(() => DocumentationScribeValidation.CreateFailureResult(request, Attempt(),
                DocumentationScribeFailureCode.Internal, NonTokenEnvelope(observation, 0)));
            return;
        }
        var valid = DocumentationScribeValidation.CreateFailureResult(request, Attempt(),
            DocumentationScribeFailureCode.Internal, NonTokenEnvelope(observation, 1));
        var root = JsonNode.Parse(DocumentationScribeRunResultWriter.Write(request, valid).AsSpan())!.AsObject();
        root["runEnvelope"]!["usageCoverage"]![observation.StartsWith("cost", StringComparison.Ordinal) ? "costRequests" : "cacheRequests"] = 0;
        var parsed = DocumentationScribeValidation.ParseRunResult(request, Attempt(), [], Encoding.UTF8.GetBytes(root.ToJsonString()));
        Assert.False(parsed.IsValid);
        Assert.Equal("scribe.result.invalid-correlation", parsed.Failure!.Code);
        Assert.Equal("/runEnvelope/usageCoverage", parsed.Failure.Pointer);
    }

    public static IEnumerable<object[]> ValidNonTokenCoverage()
    {
        foreach (var observation in new[] { "cost-zero", "cost-positive", "cache-hit", "cache-miss", "cache-mixed" })
            foreach (var reports in new[] { -1, 1, 3 })
                yield return [observation, reports];
        yield return ["not-reported", 0];
        yield return ["absent", 0];
        yield return ["absent", 1]; // A reporting population does not promise a representable aggregate.
    }

    [Theory]
    [MemberData(nameof(ValidNonTokenCoverage))]
    public void Usage_coverage_preserves_unknown_partial_complete_and_unavailable_non_token_aggregates(string observation, int reports)
    {
        var request = Request(); var envelope = NonTokenEnvelope(observation, reports);
        var result = DocumentationScribeValidation.CreateFailureResult(request, Attempt(), DocumentationScribeFailureCode.Internal, envelope);
        var parsed = DocumentationScribeValidation.ParseRunResult(request, Attempt(), [],
            DocumentationScribeRunResultWriter.Write(request, result).AsMemory());
        Assert.True(parsed.IsValid);
        Assert.Equal(envelope.UsageCoverage, parsed.Result!.RunEnvelope.UsageCoverage);
        Assert.Equal(envelope.Cache, parsed.Result.RunEnvelope.Cache);
        Assert.Equal(envelope.Cost?.AmountMicrounits, parsed.Result.RunEnvelope.Cost?.AmountMicrounits);
    }

    [Theory]
    [InlineData("cost-zero", -1)]
    [InlineData("cost-zero", 4)]
    [InlineData("cache-miss", -1)]
    [InlineData("cache-miss", 4)]
    public void Usage_coverage_non_token_populations_remain_bounded_in_factory_and_parser(string observation, int reports)
    {
        var request = Request(); var envelope = NonTokenEnvelope(observation, 1);
        var coverage = observation.StartsWith("cost", StringComparison.Ordinal)
            ? envelope.UsageCoverage! with { CostRequests = reports }
            : envelope.UsageCoverage! with { CacheRequests = reports };
        Assert.Throws<ArgumentException>(() => DocumentationScribeValidation.CreateFailureResult(request, Attempt(),
            DocumentationScribeFailureCode.Internal, envelope with { UsageCoverage = coverage }));
        var valid = DocumentationScribeValidation.CreateFailureResult(request, Attempt(), DocumentationScribeFailureCode.Internal, envelope);
        var root = JsonNode.Parse(DocumentationScribeRunResultWriter.Write(request, valid).AsSpan())!.AsObject();
        root["runEnvelope"]!["usageCoverage"]![observation.StartsWith("cost", StringComparison.Ordinal) ? "costRequests" : "cacheRequests"] = reports;
        Assert.False(DocumentationScribeValidation.ParseRunResult(request, Attempt(), [], Encoding.UTF8.GetBytes(root.ToJsonString())).IsValid);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("not-reported")]
    [InlineData("partial")]
    [InlineData("complete")]
    public async Task Usage_coverage_runtime_counts_zero_cost_and_cache_miss_without_inventing_missing_reports(string mode)
    {
        var request = IncrementalRequest(); var ids = request.ExecutableTargetIds;
        var responses = new[]
        {
            SubmitBatch(request, 1, (ids[0], IncrementalProposal(request, ids[0]))),
            SubmitBatch(request, 2, (ids[1], IncrementalProposal(request, ids[1]))), FinishConversation(request)
        }.Select((response, index) =>
        {
            var reported = mode == "complete" || mode == "partial" && index == 0;
            DocumentationScribeCacheObservation? cache = reported ? DocumentationScribeCacheObservation.Miss
                : mode == "absent" ? null : DocumentationScribeCacheObservation.NotReported;
            return new DocumentationScribeModelResponse(response.ToolCalls, response.TerminalSubmissions, response.Failure,
                new(0, 0, 0, 0, 0), cache, reported ? new("currency.usd", 0) : null,
                response.AssistantContinuation, response.ContinuationObservation);
        }).ToArray();
        var exchange = Script(responses); var result = await ExecuteIncremental(request, exchange, EmptyRegistry());
        Assert.Equal(3, exchange.Requests.Length); Assert.Equal(3, result.RunEnvelope.ProviderRequestCount);
        Assert.Equal(2, result.Submissions.Length); Assert.IsType<DocumentationScribeFinishedTerminal>(result.Stop);
        var reports = mode == "complete" ? 3 : mode == "partial" ? 1 : 0;
        Assert.Equal(reports, result.RunEnvelope.UsageCoverage!.CostRequests);
        Assert.Equal(reports, result.RunEnvelope.UsageCoverage.CacheRequests);
        Assert.Equal(reports == 0 ? (long?)null : 0, result.RunEnvelope.Cost?.AmountMicrounits);
        DocumentationScribeCacheObservation? expectedCache = mode switch
        {
            "complete" => DocumentationScribeCacheObservation.Miss,
            "partial" => DocumentationScribeCacheObservation.Mixed,
            "not-reported" => DocumentationScribeCacheObservation.NotReported,
            _ => null
        };
        Assert.Equal(expectedCache, result.RunEnvelope.Cache);
        Assert.True(DocumentationScribeValidation.ParseRunResult(request, Attempt(), [],
            DocumentationScribeRunResultWriter.Write(request, result).AsMemory()).IsValid);
    }

    private static DocumentationScribeRunEnvelopeInput NonTokenEnvelope(string observation, int reports)
    {
        var cost = observation.StartsWith("cost", StringComparison.Ordinal);
        DocumentationScribeCacheObservation? cache = observation switch
        {
            "cache-hit" => DocumentationScribeCacheObservation.Hit,
            "cache-miss" => DocumentationScribeCacheObservation.Miss,
            "cache-mixed" => DocumentationScribeCacheObservation.Mixed,
            "not-reported" => DocumentationScribeCacheObservation.NotReported,
            _ => null
        };
        return new("provider.synthetic.v1", "model.synthetic.v1", "scribe-protocol.v1", 1, 3, 0, 0, 0, null, cache,
            cost ? new("currency.usd", observation == "cost-zero" ? 0 : 1) : null, [])
        {
            UsageCoverage = reports < 0 ? null : new(0, 0, 0, 0, 0,
                cost || observation == "absent" ? reports : 0, cache.HasValue || observation == "absent" ? reports : 0)
        };
    }
}
