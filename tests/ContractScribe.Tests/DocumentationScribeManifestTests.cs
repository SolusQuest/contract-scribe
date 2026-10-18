using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Agent.Runtime;
using ContractScribe.Cli;
using ContractScribe.Core;
using ContractScribe.Roslyn;

namespace ContractScribe.Tests;

public sealed partial class DocumentationScribeRuntimeTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(100)]
    public async Task Complete_manifest_reaches_the_Solus_runtime_with_one_truthful_partial_terminal(int count)
    {
        var request = Request(root => AddManifestMembers(root, count));
        var exchange = Script(TerminalResponse(ReadTerminal("skip-result.json")));
        var result = await CreateRuntime(exchange, EmptyRegistry()).RunAsync(request, Attempt(), Prompt(request));
        var sent = Assert.Single(exchange.Requests);
        using var startup = JsonDocument.Parse(sent.Messages.Single(message =>
            message.Kind == DocumentationScribeMessageKind.TargetEvidence).Content);
        Assert.Equal(count, startup.RootElement.GetProperty("manifest").GetArrayLength());
        Assert.Equal("target.selected", startup.RootElement.GetProperty("executionTargetId").GetString());
        Assert.Equal(1, startup.RootElement.GetProperty("executableTargetIds").GetArrayLength());
        Assert.Equal(DocumentationScribeTerminalKind.Skip, result.Terminal.Kind);
        Assert.Equal(1, result.RunEnvelope.ProviderRequestCount);
        Assert.True(sent.DeterministicUtf8.Length < 1_048_576);
        Assert.Equal(request.ArtifactSha256, result.ScribeRequestSha256);
    }

    [Fact]
    public void Manifest_metadata_preserves_null_styles_and_rejects_dangling_or_widened_execution()
    {
        var request = Request(root => AddManifestMembers(root, 12));
        Assert.All(request.Manifest.Skip(1), member => Assert.Null(member.StyleProfile));
        foreach (var mutation in new Action<JsonObject>[]
        {
            root => root["executableTargetIds"]!.AsArray().Add("target.unselected"),
            root => root["executableTargetIds"]!.AsArray().Add("target.member.0001"),
            root => root["manifest"]![1]!["contextReferenceIds"]!.AsArray().Add("context.unselected"),
            root => root["manifest"]![1]!["evidenceReferenceIds"]!.AsArray().Add("evidence.summary"),
            root => root["manifest"]![1]!["targetId"] = "target.selected",
            root => root["manifest"]![1]!["target"]!["symbolRef"] = root["manifest"]![0]!["target"]!["symbolRef"]!.DeepClone(),
        })
        {
            var root = JsonNode.Parse(ReadFixture("request.json"))!.AsObject();
            AddManifestMembers(root, 12);
            mutation(root);
            Assert.Null(DocumentationScribeValidation.ParseRequest(Encoding.UTF8.GetBytes(root.ToJsonString())).Request);
        }
        var metadataOnly = Request(root =>
        {
            AddManifestMembers(root, 12);
            root["executableTargetIds"] = new JsonArray();
        });
        Assert.False(metadataOnly.HasExecutableTargets);
        Assert.Throws<ArgumentException>(() => new ScribeSessionTestDriver(metadataOnly, Attempt(), Prompt(metadataOnly),
            EmptyRegistry(), Script(), DocumentationScribeExecutionScope.ForStandalone(metadataOnly)));
    }

    [Fact]
    public void Old_single_target_shape_is_rejected_by_the_current_draft()
    {
        var root = JsonNode.Parse(ReadFixture("request.json"))!.AsObject();
        root["target"] = root["manifest"]![0]!["target"]!.DeepClone();
        root["styleProfile"] = root["manifest"]![0]!["styleProfile"]!.DeepClone();
        root.Remove("manifest");
        root.Remove("executableTargetIds");
        Assert.Null(DocumentationScribeValidation.ParseRequest(Encoding.UTF8.GetBytes(root.ToJsonString())).Request);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shared_source_bytes_are_visible_once_with_separate_target_evidence_and_exact_continuation(bool retry)
    {
        const string source = "synthetic common source bytes";
        var request = Request(root =>
        {
            AddManifestMembers(root, 2);
            var first = root["manifest"]![0]!;
            var second = root["manifest"]![1]!;
            second["styleProfile"] = first["styleProfile"]!.DeepClone();
            second["target"]!["applicableComponents"] = first["target"]!["applicableComponents"]!.DeepClone();
            second["target"]!["symbolRef"]!["documentationCommentId"] = "M:Synthetic.Widget.Other(System.String)";
            foreach (var member in root["manifest"]!.AsArray())
            {
                member!["target"]!["sourceCommitment"]!["contentSha256"] = Sha256(source);
                member["evidenceReferenceIds"] = new JsonArray();
            }
            root["evidenceReferences"] = new JsonArray();
        });
        var port = new SharedManifestSourcePort(request, source);
        var schema = JsonNode.Parse(DocumentationScribeRepositoryToolSchemas.ReadExcerptInputUtf8Json.Span)!.AsObject();
        schema["properties"]!["scopeId"]!["enum"] = new JsonArray("target.selected", "target.member.0001");
        var registry = new DocumentationScribeToolRegistryBuilder(request.ToolPolicyId).Add(
            DocumentationScribeRepositoryToolBundle.ReadExcerptDescriptor, port,
            new DocumentationScribeComposition.ReadCodec(request), "Read selected source.",
            Encoding.UTF8.GetBytes(schema.ToJsonString()), 16).Build();
        var calls = request.Manifest.Select((member, index) => new DocumentationScribeModelToolCall(index,
            "call." + index, DocumentationScribeRepositoryToolOperationIds.ReadExcerpt,
            JsonSerializer.SerializeToUtf8Bytes(new { scopeId = member.TargetId }))).ToArray();
        var round = ToolResponseWithContinuation("exact assistant", "exact opaque reasoning", calls);
        var terminal = TerminalResponse("""{"kind":"skip","reason":"scribe.skip.insufficient-evidence","evidenceReferenceIds":[]}"""u8.ToArray());
        var responses = retry
            ? new[] { round, FailureResponse(DocumentationScribeModelFailureCode.TransientUnavailable), round,
                terminal }
            : new[] { round, terminal };
        var exchange = Script(responses);
        var result = await CreateRuntime(exchange, registry).RunAsync(request, Attempt(), Prompt(request));
        Assert.True(result.Terminal.Kind == DocumentationScribeTerminalKind.Skip, JsonSerializer.Serialize(result.RunEnvelope.Diagnostics));
        var visible = exchange.Requests[^1].CompletedToolExchanges;
        Assert.Equal(2, visible.Length);
        using var firstResult = JsonDocument.Parse(visible[0].ResultUtf8Json);
        using var secondResult = JsonDocument.Parse(visible[1].ResultUtf8Json);
        var firstExcerpt = firstResult.RootElement.GetProperty("excerpt");
        var secondExcerpt = secondResult.RootElement.GetProperty("excerpt");
        Assert.Equal(source, firstExcerpt.GetProperty("content").GetString());
        Assert.False(secondExcerpt.TryGetProperty("content", out _));
        Assert.Equal(firstExcerpt.GetProperty("contentId").GetString(), secondExcerpt.GetProperty("contentRef").GetString());
        Assert.Equal(request.Manifest[0].Target.SymbolRef, Assert.Single(visible[0].EvidenceReferences).Subject.ParentSymbolRef);
        Assert.Equal(request.Manifest[1].Target.SymbolRef, Assert.Single(visible[1].EvidenceReferences).Subject.ParentSymbolRef);
        Assert.Equal("exact assistant", visible[0].AssistantContinuation!.Content);
        Assert.Equal("exact opaque reasoning", visible[0].AssistantContinuation!.ReasoningContent);
        Assert.Null(visible[1].AssistantContinuation);
        if (retry) Assert.Empty(exchange.Requests[2].CompletedToolExchanges);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Readable_sibling_evidence_and_source_do_not_grant_terminal_permission(bool proposal)
    {
        const string siblingId = "M:Synthetic.Widget.Other(System.String)";
        var request = Request(root =>
        {
            AddManifestMembers(root, 2);
            var first = root["manifest"]![0]!;
            var second = root["manifest"]![1]!;
            second["styleProfile"] = first["styleProfile"]!.DeepClone();
            second["target"]!["applicableComponents"] = first["target"]!["applicableComponents"]!.DeepClone();
            second["target"]!["symbolRef"]!["documentationCommentId"] = siblingId;
            var siblingEvidence = root["evidenceReferences"]![2]!.DeepClone();
            siblingEvidence["evidenceReferenceId"] = "evidence.zsummary";
            siblingEvidence["subject"]!["symbolRef"]!["documentationCommentId"] = siblingId;
            root["evidenceReferences"]!.AsArray().Add(siblingEvidence);
            second["evidenceReferenceIds"] = new JsonArray("evidence.zsummary");
        });
        var terminal = JsonNode.Parse(ReadTerminal(proposal ? "proposal-result.json" : "skip-result.json"))!;
        if (proposal) terminal["target"]!["symbolRef"]!["documentationCommentId"] = siblingId;
        else terminal["evidenceReferenceIds"] = new JsonArray("evidence.zsummary");
        var exchange = Script(TerminalResponse(Encoding.UTF8.GetBytes(terminal.ToJsonString())));
        var result = await CreateRuntime(exchange, EmptyRegistry()).RunAsync(request, Attempt(), Prompt(request));
        Assert.Equal(DocumentationScribeFailureCode.Validation, Assert.IsType<DocumentationScribeFailureTerminal>(result.Terminal).Code);
        Assert.Single(exchange.Requests);
    }

    private static void AddManifestMembers(JsonObject root, int count)
    {
        var manifest = root["manifest"]!.AsArray();
        for (var index = 1; index < count; index++)
        {
            var member = manifest[0]!.DeepClone();
            member["targetId"] = $"target.member.{index:D4}";
            member["target"]!["symbolRef"]!["documentationCommentId"] = $"P:Synthetic.Widget.P{index}";
            member["target"]!["applicableComponents"] = new JsonArray();
            member["styleProfile"] = null;
            member["evidenceReferenceIds"] = new JsonArray();
            manifest.Add(member);
        }
    }

    private sealed class SharedManifestSourcePort(DocumentationScribeRequest request, string source)
        : IDocumentationScribeToolPort<DocumentationScribeRepositoryReadExcerptRequest, DocumentationScribeRepositoryReadExcerptResult>
    {
        public ValueTask<DocumentationScribeRepositoryReadExcerptResult> InvokeAsync(
            DocumentationScribeRepositoryReadExcerptRequest input, CancellationToken cancellationToken)
        {
            var member = request.Manifest.Single(item => item.TargetId == input.ScopeId);
            var path = ((RepositoryEvidenceLocator)member.Target.SourceLocator).Path;
            var dynamicEvidence = new DocumentationScribeDynamicEvidenceInput(new TargetEvidenceSubject(member.Target.SymbolRef),
                EvidenceKind.SourceDeclaration, EvidenceRelation.Declares, DocumentationScribeEvidenceAuthority.SourceDeclaration,
                EvidenceInput.RepositoryLocator(path), Sha256(source), source.Length, source.Length, false, ["claim.purpose"]);
            return ValueTask.FromResult(new DocumentationScribeRepositoryReadExcerptResult(DocumentationScribeToolOutcome.Complete,
                null, new(path, source, 0, source.Length, Sha256(source), source.Length, source.Length, false), null, [dynamicEvidence]));
        }
    }
}
