using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Agent.Providers;
using ContractScribe.Agent.Runtime;
using ContractScribe.Cli;
using ContractScribe.Core;
using ContractScribe.Roslyn;
using SolusAgent.Api.Candidates;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using SolusAgent.Tools.Api;

namespace ContractScribe.Tests;

public sealed partial class DocumentationScribeRepositoryToolTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Solus_runtime_repository_prompt_and_tool_bindings_survive_the_public_runtime(bool badMember, bool retry)
    {
        // Exercise the same committed prompt as the native repository cases on every platform.
        var request = RepositoryToolFixture.ParseRequest();
        var port = new ControlledReadPort();
        var registry = new DocumentationScribeToolRegistryBuilder(request.ToolPolicyId).Add(
            DocumentationScribeRepositoryToolBundle.ReadExcerptDescriptor, port,
            new DocumentationScribeComposition.ReadCodec(request.EvidenceReferences[0]),
            DocumentationScribeRepositoryToolSchemas.ReadExcerptDescription,
            BoundReadSchema(request), maximumCallsPerRun: 16).Build();
        var calls = ImmutableArray.CreateBuilder<DocumentationScribeModelToolCall>();
        calls.Add(new(0, "call.good", DocumentationScribeRepositoryToolOperationIds.ReadExcerpt,
            Encoding.UTF8.GetBytes("{\"scopeId\":\"context.instructions\",\"repositoryPath\":\"docs/guide.md\"}")));
        if (badMember) calls.Add(new(1, "call.bad", DocumentationScribeRepositoryToolOperationIds.ReadExcerpt,
            Encoding.UTF8.GetBytes("{\"scopeId\":\"context.instructions\",\"startLine\":\"invalid\"}")));
        var first = new DocumentationScribeModelResponse(calls.ToImmutable(), [], usage: new(0, 0, 0, 0));
        var steps = new List<ScriptedDocumentationScribeStep> { ScriptedDocumentationScribeStep.Return(first) };
        if (retry)
        {
            steps.Add(ScriptedDocumentationScribeStep.Return(new([], [], new(DocumentationScribeModelFailureCode.TransientUnavailable), usage: new(0, 0, 0, 0))));
            steps.Add(ScriptedDocumentationScribeStep.Return(first));
        }
        steps.Add(ScriptedDocumentationScribeStep.Return(new([], [new(Encoding.UTF8.GetBytes(
            "{\"kind\":\"skip\",\"reason\":\"scribe.skip.insufficient-evidence\",\"evidenceReferenceIds\":[]}"))], usage: new(0, 0, 0, 0))));
        var exchange = new ScriptedDocumentationScribeModelExchange(steps.ToImmutableArray());
        var session = ToolSession(request, registry, exchange);
        var result = await session.RunAsync();
        Assert.Equal(DocumentationScribeRepositoryToolOperationIds.ReadExcerpt, Assert.Single(session.Requests[0].Tools).OperationId);
        AssertBoundReadSchema(request, Assert.Single(session.Requests[0].Tools).InputSchemaJson);
        Assert.Equal(badMember ? 0 : retry ? 2 : 1, port.Calls);
        Assert.Equal(badMember ? 1 : retry ? 4 : 2, exchange.Requests.Length);
        if (badMember) Assert.NotEqual(DocumentationScribeTerminalKind.Proposal, result.Terminal.Kind);
        else
        {
            Assert.Equal(DocumentationScribeTerminalKind.Skip, result.Terminal.Kind);
            Assert.Equal(DocumentationScribeRepositoryToolOperationIds.ReadExcerpt,
                Assert.Single(session.Requests[^1].CompletedToolExchanges).OperationId);
            if (retry) Assert.Empty(session.Requests[2].CompletedToolExchanges);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Solus_runtime_real_repository_result_evidence_and_continuation_survive_the_public_runtime(bool retry)
    {
        using var fixture = RepositoryToolFixture.Create();
        File.WriteAllText(Path.Join(fixture.Root, "docs", "guide.md"), new string('a', 32_768), new UTF8Encoding(false));
        var bundle = fixture.Bundle();
        var registry = new DocumentationScribeToolRegistryBuilder(fixture.Request.ToolPolicyId).Add(
            DocumentationScribeRepositoryToolBundle.ReadExcerptDescriptor, bundle.ReadExcerpt,
            new DocumentationScribeComposition.ReadCodec(fixture.Request.EvidenceReferences[0]),
            DocumentationScribeRepositoryToolSchemas.ReadExcerptDescription,
            BoundReadSchema(fixture.Request), maximumCallsPerRun: 16).Build();
        var first = new DocumentationScribeModelResponse(
            [new(0, "call.read", DocumentationScribeRepositoryToolOperationIds.ReadExcerpt, Encoding.UTF8.GetBytes("{\"scopeId\":\"context.instructions\",\"repositoryPath\":\"docs/guide.md\"}"))], [],
            failure: null, usage: new(0, 0, 0, 0), cache: null, cost: null,
            assistantContinuation: new("synthetic assistant", "synthetic opaque continuation"),
            continuationObservation: DocumentationScribeContinuationObservation.None);
        var terminal = new DocumentationScribeModelResponse([], [new(Encoding.UTF8.GetBytes(
            "{\"kind\":\"skip\",\"reason\":\"scribe.skip.insufficient-evidence\",\"evidenceReferenceIds\":[]}"))], usage: new(0, 0, 0, 0));
        var steps = new List<ScriptedDocumentationScribeStep> { ScriptedDocumentationScribeStep.Return(first) };
        if (retry)
        {
            steps.Add(ScriptedDocumentationScribeStep.Return(new([], [], new(DocumentationScribeModelFailureCode.TransientUnavailable), usage: new(0, 0, 0, 0))));
            steps.Add(ScriptedDocumentationScribeStep.Return(first));
        }
        steps.Add(ScriptedDocumentationScribeStep.Return(terminal));
        var scripted = new ScriptedDocumentationScribeModelExchange(steps.ToImmutableArray());
        var handler = new ScribeToolHttpHandler(async (message, token, number) =>
        {
            using var wire = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
            var definition = Assert.Single(wire.RootElement.GetProperty("tools").EnumerateArray(),
                tool => tool.GetProperty("function").GetProperty("name").GetString() == "cs_tool_000").GetProperty("function");
            AssertBoundReadSchema(fixture.Request, definition.GetProperty("parameters").GetRawText());
            if (number == 2)
            {
                var messages = wire.RootElement.GetProperty("messages").EnumerateArray().ToArray();
                var assistant = Assert.Single(messages, entry => entry.GetProperty("role").GetString() == "assistant");
                Assert.Equal("synthetic assistant", assistant.GetProperty("content").GetString());
                Assert.Equal("synthetic opaque continuation", assistant.GetProperty("reasoning_content").GetString());
                var tool = Assert.Single(messages, entry => entry.GetProperty("role").GetString() == "tool");
                Assert.Contains(new string('a', 32_768), tool.GetProperty("content").GetString(), StringComparison.Ordinal);
            }
            var response = new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["index"] = 0,
                    ["finish_reason"] = "tool_calls",
                    ["message"] = new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = "synthetic assistant",
                        ["reasoning_content"] = "synthetic opaque continuation",
                        ["tool_calls"] = new JsonArray(new JsonObject
                        {
                            ["id"] = number == 1 ? "call.read" : "call.terminal",
                            ["type"] = "function",
                            ["function"] = new JsonObject
                            {
                                ["name"] = number == 1 ? "cs_tool_000" : "cs_terminal",
                                ["arguments"] = number == 1 ? "{\"scopeId\":\"context.instructions\",\"repositoryPath\":\"docs/guide.md\"}"
                                    : Encoding.UTF8.GetString(terminal.TerminalSubmissions[0].TerminalUtf8Json.Span),
                            },
                        }),
                    },
                }),
                ["usage"] = new JsonObject
                {
                    ["prompt_tokens"] = 0,
                    ["completion_tokens"] = 0,
                    ["prompt_cache_hit_tokens"] = 0,
                    ["prompt_cache_miss_tokens"] = 0
                },
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response.ToJsonString(), Encoding.UTF8, "application/json") };
        });
        using var transport = new OpenAiCompatibleHttpModelExchange(
            new(new Uri("https://example.test/v1/chat/completions"), "deepseek-v4-flash",
                new(OpenAiCompatibleThinkingMode.Enabled, OpenAiCompatibleReasoningEffort.High, OpenAiCompatibleToolChoice.Required,
                    OpenAiCompatibleContinuationPolicy.RequiredForToolCalls, OpenAiCompatibleOutputTokenField.MaxTokens), networkEnabled: true), handler);
        IDocumentationScribeModelExchange exchange = retry ? scripted : transport;
        var session = ToolSession(fixture.Request, registry, exchange);
        var result = await session.RunAsync();
        Assert.Equal(DocumentationScribeTerminalKind.Skip, result.Terminal.Kind);
        var completed = Assert.Single(session.Requests[1].CompletedToolExchanges);
        Assert.True(completed.ResultUtf8Json.Length > 16_384);
        // The actual CLI codec does not promote maintained-documentation ranges as target source evidence.
        Assert.Empty(completed.EvidenceReferences);
        Assert.Equal("synthetic opaque continuation", completed.AssistantContinuation!.ReasoningContent);
        using var payload = JsonDocument.Parse(completed.ResultUtf8Json);
        Assert.Equal(new string('a', 32_768), payload.RootElement.GetProperty("excerpt").GetProperty("content").GetString());
        if (retry)
        {
            Assert.Empty(session.Requests[2].CompletedToolExchanges);
            Assert.Empty(session.State.ActiveDynamicEvidenceReferences);
            Assert.Equal(2, session.Requests[2].AttemptNumber);
            Assert.Equal("call.read", Assert.Single(session.Requests[3].CompletedToolExchanges).CallId);
        }
        else Assert.Empty(result.DynamicEvidenceReferences);
        if (!retry) Assert.Equal(2, handler.Count);
    }

    [Fact]
    public async Task Solus_runtime_bad_member_in_a_real_read_batch_produces_zero_repository_effects()
    {
        using var fixture = RepositoryToolFixture.Create();
        var entered = 0;
        var bundle = fixture.Bundle(checkpoint: _ => entered++);
        var registry = new DocumentationScribeToolRegistryBuilder(fixture.Request.ToolPolicyId).Add(
            DocumentationScribeRepositoryToolBundle.ReadExcerptDescriptor, bundle.ReadExcerpt,
            new DocumentationScribeComposition.ReadCodec(fixture.Request.EvidenceReferences[0]),
            DocumentationScribeRepositoryToolSchemas.ReadExcerptDescription,
            BoundReadSchema(fixture.Request), maximumCallsPerRun: 16).Build();
        var response = new DocumentationScribeModelResponse(
        [
            new(0, "call.good", DocumentationScribeRepositoryToolOperationIds.ReadExcerpt, Encoding.UTF8.GetBytes("{\"scopeId\":\"context.instructions\",\"repositoryPath\":\"docs/guide.md\"}")),
            new(1, "call.bad", DocumentationScribeRepositoryToolOperationIds.ReadExcerpt, Encoding.UTF8.GetBytes("{\"scopeId\":\"context.instructions\",\"startLine\":\"invalid\"}")),
        ], [], usage: new(0, 0, 0, 0));
        var exchange = new ScriptedDocumentationScribeModelExchange([ScriptedDocumentationScribeStep.Return(response)]);
        var session = ToolSession(fixture.Request, registry, exchange);
        var result = await session.RunAsync();
        Assert.NotEqual(DocumentationScribeTerminalKind.Proposal, result.Terminal.Kind);
        Assert.Equal(0, entered);
        Assert.Single(exchange.Requests);
        Assert.Equal(0, session.State.Scope.Allowance.ToolCallCount);
    }

    [Fact]
    public void Solus_runtime_preserves_opt_in_capacities_without_changing_small_defaults()
    {
        Assert.Equal(16_384, new ProviderExchangeBounds().MaximumTextBytes);
        Assert.Equal(64, new RuntimeOptions().MaximumAttempts);
        Assert.Equal(65_536, new CandidateExecutionBounds(1, 0, 0).MaximumPayloadBytes);
        var bounds = DocumentationScribeSession.Bounds();
        var scope = new ProviderScope("deepseek", "deepseek-v4-flash");
        var attempt = new ProviderAttempt(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var block = new string('b', 4_194_304);
        var request = new ProviderRequest(scope, attempt, [ProviderInput.Instruction("Host policy"), ProviderInput.Data(block)], bounds: bounds);
        Assert.Equal(block, request.Inputs[1].Text);
        var candidate = new string('c', DocumentationScribeContract.MaximumArtifactUtf8Bytes);
        var response = new ProviderResponse(scope, attempt, ProviderFinish.Final, candidate, []);
        response.ValidateFor(request);
        Assert.Equal(candidate, new CandidateSubmission(attempt.ExecutionId, Guid.NewGuid(), candidate,
            maximumPayloadBytes: DocumentationScribeContract.MaximumArtifactUtf8Bytes).Payload);
        var schema = ToolSchema.Parse("{\"type\":\"object\",\"additionalProperties\":false,\"required\":[\"payload\"],\"properties\":{\"payload\":{\"type\":\"string\"}}}");
        var escaped = JsonSerializer.Serialize(new { payload = new string('\u0001', DocumentationScribeContract.MaximumArtifactUtf8Bytes) });
        Assert.True(Encoding.UTF8.GetByteCount(escaped) > DocumentationScribeContract.MaximumArtifactUtf8Bytes);
        Assert.Equal(ToolError.None, schema.Validate(escaped, ToolLimits.MaximumPayloadBytes));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SolusAgent.Api.Usage.AgentRetryPolicy(129));
        Assert.Equal(128, new SolusAgent.Api.Usage.AgentRetryPolicy(128).MaximumAttemptsPerLogicalCall);
    }

    private static ReadOnlyMemory<byte> BoundReadSchema(DocumentationScribeRequest request) =>
        DocumentationScribeComposition.BindRepositoryToolScopes(DocumentationScribeRepositoryToolSchemas.ReadExcerptInputUtf8Json,
            RepositoryToolFixture.ScopesFor(request), DocumentationScribeRepositoryToolOperations.ReadExcerpt);

    private static void AssertBoundReadSchema(DocumentationScribeRequest request, string json)
    {
        using var schema = JsonDocument.Parse(json);
        Assert.Equal(["context.instructions"], schema.RootElement.GetProperty("properties").GetProperty("scopeId")
            .GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(BoundReadSchema(request).Span), JsonNode.Parse(json)));
    }

    private sealed class ControlledReadPort : IDocumentationScribeToolPort<DocumentationScribeRepositoryReadExcerptRequest, DocumentationScribeRepositoryReadExcerptResult>
    {
        internal int Calls { get; private set; }
        public ValueTask<DocumentationScribeRepositoryReadExcerptResult> InvokeAsync(DocumentationScribeRepositoryReadExcerptRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            const string text = "synthetic excerpt";
            return ValueTask.FromResult(new DocumentationScribeRepositoryReadExcerptResult(DocumentationScribeToolOutcome.Complete, null,
                new("docs/guide.md", text, 0, text.Length, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant(),
                    text.Length, text.Length, false), null, []));
        }
    }

    private static ScribeSessionTestDriver ToolSession(DocumentationScribeRequest request,
        DocumentationScribeToolRegistry registry, IDocumentationScribeModelExchange exchange)
    {
        Assert.True(DocumentationScribeAttemptId.TryParse("scribe-attempt.11111111111111111111111111111111", out var attempt));
        return new(request, attempt, Prompt(request), registry, exchange, DocumentationScribeExecutionScope.ForStandalone(request));
    }

    private sealed class ScribeToolHttpHandler(Func<HttpRequestMessage, CancellationToken, int, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken token) => send(message, token, ++Count);
    }
}
