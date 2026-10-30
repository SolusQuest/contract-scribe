using System.Text;
using System.Text.Json;
using ContractScribe.Cli;
using ContractScribe.Core;
using ContractScribe.Roslyn;

namespace ContractScribe.Tests;

public sealed partial class DocumentationScribeRepositoryToolTests
{
    [Theory]
    [InlineData("null-request", "invalid-request-shape")]
    [InlineData("missing-path", "invalid-request-path")]
    [InlineData("noncanonical-path", "invalid-request-path")]
    [InlineData("unpaired-range", "invalid-request-range")]
    [InlineData("reversed-range", "invalid-request-range")]
    [InlineData("zero-range", "invalid-request-range")]
    [InlineData("past-eof", "invalid-request-eof")]
    public async Task Read_excerpt_rejection_details_are_fixed_and_do_not_change_failure_payloads(
        string scenario, string expectedReason)
    {
        using var fixture = RepositoryToolFixture.Create();
        DocumentationScribeRepositoryReadExcerptRequest request = scenario switch
        {
            "null-request" => null!,
            "missing-path" => new("context.instructions"),
            "noncanonical-path" => new("context.instructions", "docs/../private-marker-key-marker-provider-wire.md"),
            "unpaired-range" => new("context.instructions", "docs/guide.md", 1),
            "reversed-range" => new("context.instructions", "docs/guide.md", 2, 1),
            "zero-range" => new("context.instructions", "docs/guide.md", 0, 1),
            _ => new("context.instructions", "docs/guide.md", 999999, 999999),
        };
        var bundle = fixture.Bundle();
        if (scenario == "past-eof")
        {
            var refused = await bundle.ReadExcerpt.InvokeAsync(request, default);
            Assert.Same(DocumentationScribeToolOutcome.Unavailable, refused.Outcome);
            Assert.Equal(DocumentationScribeRepositoryToolFailureCodes.RangePastEndOfFile, refused.FailureCode);
            Assert.Null(refused.Excerpt); Assert.Empty(refused.DynamicEvidence);
        }
        var result = await bundle.ReadExcerpt.InvokeAsync(request, default);
        Assert.Same(DocumentationScribeToolOutcome.Failure, result.Outcome);
        Assert.Equal(DocumentationScribeRepositoryToolFailureCodes.InvalidRequest, result.FailureCode);
        Assert.Null(result.Excerpt); Assert.Null(result.Route); Assert.Empty(result.DynamicEvidence);
        var reference = DocumentationScribeToolFailureDiagnostics.CreateReference(
            DocumentationScribeRepositoryToolOperationIds.ReadExcerpt,
            ((IDocumentationScribeToolResult)result).FailureReasonCategory);
        Assert.Equal("tool-failure.read-excerpt." + expectedReason, reference);
        Assert.True(DocumentationScribeToolFailureDiagnostics.IsKnownCode("campaign." + reference));

        var serialized = JsonSerializer.Serialize(result);
        using var json = JsonDocument.Parse(serialized);
        Assert.Equal(["Outcome", "FailureCode", "Excerpt", "Route", "DynamicEvidence"],
            json.RootElement.EnumerateObject().Select(property => property.Name));
        foreach (var observation in new[] { reference, serialized, result.ToString() })
        {
            Assert.DoesNotContain("private-marker", observation, StringComparison.Ordinal);
            Assert.DoesNotContain("guide.md", observation, StringComparison.Ordinal);
            Assert.DoesNotContain("key-marker", observation, StringComparison.Ordinal);
            Assert.DoesNotContain("provider-wire", observation, StringComparison.Ordinal);
            Assert.DoesNotContain("bounded evidence.", observation, StringComparison.Ordinal);
            Assert.DoesNotContain("RepositoryPath", observation, StringComparison.Ordinal);
            Assert.DoesNotContain("StartLine", observation, StringComparison.Ordinal);
            Assert.DoesNotContain(fixture.Root, observation, StringComparison.Ordinal);
            Assert.DoesNotContain("999999", observation, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("{\"scopeId\":\"context.instructions\",\"repositoryPath\":\"docs/guide.md\"}")]
    [InlineData("{\"scopeId\":\"context.instructions\",\"repositoryPath\":\"docs/guide.md\",\"startLine\":1,\"endLine\":2}")]
    public async Task Read_excerpt_legal_whole_file_and_exact_range_still_pass_the_actual_codec_and_reader(string arguments)
    {
        using var fixture = RepositoryToolFixture.Create();
        var decoded = new DocumentationScribeComposition.ReadCodec(fixture.Request)
            .DecodeArguments(Encoding.UTF8.GetBytes(arguments));
        Assert.True(decoded.IsValid);
        var result = await fixture.Bundle().ReadExcerpt.InvokeAsync(decoded.Request!, default);
        Assert.Same(DocumentationScribeToolOutcome.Complete, result.Outcome);
        Assert.Null(result.FailureCode);
        Assert.Equal("Guide\r\nbounded evidence.\r\n", result.Excerpt!.Content);
        Assert.Equal(DocumentationScribeToolFailureReason.Unknown,
            ((IDocumentationScribeToolResult)result).FailureReasonCategory);
    }
}
