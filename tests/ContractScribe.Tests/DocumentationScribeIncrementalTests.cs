using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Agent.Prompting;
using ContractScribe.Agent.Providers;
using ContractScribe.Agent.Runtime;
using ContractScribe.Cli;
using ContractScribe.Core;

namespace ContractScribe.Tests;

public sealed partial class DocumentationScribeRuntimeTests
{
    [Fact]
    public async Task Incremental_class_and_properties_share_real_read_submission_and_ack_history()
    {
        var request = IncrementalRequest();
        var ids = request.ExecutableTargetIds;
        Assert.StartsWith("T:", request.Manifest.Single(item => item.TargetId == ids[0]).Target.SymbolRef.DocumentationCommentId);
        Assert.All(ids.Skip(1), id => Assert.StartsWith("P:", request.Manifest.Single(item => item.TargetId == id).Target.SymbolRef.DocumentationCommentId));
        var registry = RegistryWithCodec(new SyntheticPort(DocumentationScribeToolOutcome.Complete),
            new SyntheticCodec(suppressDynamicEvidence: true, resultContentMarker: "retained-read-result"));
        var exchange = Script(ToolResponseWithContinuation("exact read content", "exact read reasoning",
                Call(0, "call.read", "tool.read", "selected")),
            SubmitBatch(request, 1, (ids[0], IncrementalProposal(request, ids[0]))),
            SubmitBatch(request, 2, (ids[2], IncrementalProposal(request, ids[2])), (ids[1], IncrementalProposal(request, ids[1]))),
            FinishConversation(request));
        var result = await ExecuteIncremental(request, exchange, registry);
        Assert.Equal(DocumentationScribeFinishReason.Completed, Assert.IsType<DocumentationScribeFinishedTerminal>(result.Stop).Reason);
        Assert.Equal(3, result.Submissions.Length);
        Assert.All(DocumentationScribeValidation.GetCoverage(request, result), row => Assert.Equal("validated", row.Status));
        Assert.Equal(4, exchange.Requests.Length);
        Assert.All(exchange.Requests, sent => Assert.Equal(1, sent.AttemptNumber));
        var last = exchange.Requests[^1];
        Assert.Equal(new[] { "call.read", "call.submit.1", "call.submit.2" }, last.CompletedToolExchanges.Select(item => item.CallId));
        Assert.Equal("exact submission reasoning 1", last.CompletedToolExchanges[1].AssistantContinuation!.ReasoningContent);
        var wire = OpenAiCompatibleChatCompletionsCodec.Prepare(last, "synthetic-model").BodyUtf8;
        Assert.Contains("retained-read-result", Encoding.UTF8.GetString(wire), StringComparison.Ordinal);
        using var body = JsonDocument.Parse(wire);
        var assistant = body.RootElement.GetProperty("messages").EnumerateArray().Where(item => item.GetProperty("role").GetString() == "assistant").ToArray();
        Assert.Equal(3, assistant.Length);
        Assert.Equal("exact submission reasoning 1", assistant[1].GetProperty("reasoning_content").GetString());
        var encoded = DocumentationScribeRunResultWriter.Write(request, result);
        Assert.True(DocumentationScribeValidation.ParseRunResult(request, Attempt(), [], encoded.AsMemory()).IsValid);
        Assert.DoesNotContain("exact submission reasoning", Encoding.UTF8.GetString(encoded.AsSpan()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Incremental_mixed_batch_retry_and_restart_preserve_valid_siblings_without_regeneration()
    {
        var request = IncrementalRequest(); var ids = request.ExecutableTargetIds; var host = new IncrementalHost();
        var invalid = IncrementalProposal(request, ids[1]); invalid["contentUnits"]![0]!["evidenceReferenceIds"]![0] = "evidence.foreign";
        var exchange = Script(SubmitBatch(request, 1, (ids[0], IncrementalProposal(request, ids[0]))),
            SubmitBatch(request, 2, (ids[1], invalid), (ids[2], IncrementalProposal(request, ids[2]))),
            FailureResponse(DocumentationScribeModelFailureCode.TransientUnavailable), FinishConversation(request));
        var first = await ExecuteIncremental(request, exchange, EmptyRegistry(), host);
        Assert.Equal(2, first.Submissions.Length); Assert.Equal(2, host.Calls);
        Assert.Equal(1, first.RepairCount); Assert.Single(first.TargetFailures);
        Assert.Equal(ids[1], first.TargetFailures[0].TargetId);
        Assert.Equal(DocumentationScribeFinishReason.Unresolved, Assert.IsType<DocumentationScribeFinishedTerminal>(first.Stop).Reason);
        Assert.Equal(2, exchange.Requests[^1].AttemptNumber);
        Assert.Empty(exchange.Requests[^1].CompletedToolExchanges);
        var resumed = Script(FinishConversation(request));
        var second = await ExecuteIncremental(request, resumed, EmptyRegistry(), host, first);
        Assert.Equal(2, second.Submissions.Length); Assert.Equal(2, host.Calls);
        Assert.Equal(0, second.RunEnvelope.ProviderRequestCount); Assert.Equal(1, second.RepairCount);
        Assert.Empty(resumed.Requests); Assert.Null(second.RunEnvelope.Usage);
        Assert.Equal(new[] { "durably-recorded", "failed", "durably-recorded" }.Order(),
            DocumentationScribeValidation.GetCoverage(request, second).Select(item => item.Status).Order());
    }

    [Fact]
    public async Task Incremental_invalid_repairs_are_finite_and_keep_an_accepted_target()
    {
        var request = IncrementalRequest(); var ids = request.ExecutableTargetIds;
        var invalid = IncrementalProposal(request, ids[1]); invalid["contentUnits"]![0]!["evidenceReferenceIds"]![0] = "evidence.foreign";
        var exchange = Script(SubmitBatch(request, 1, (ids[0], IncrementalProposal(request, ids[0]))),
            SubmitBatch(request, 2, (ids[1], invalid)), SubmitBatch(request, 3, (ids[1], invalid)), SubmitBatch(request, 4, (ids[1], invalid)));
        var result = await ExecuteIncremental(request, exchange, EmptyRegistry());
        Assert.Equal(DocumentationScribeFailureCode.Validation, Assert.IsType<DocumentationScribeFailureTerminal>(result.Stop).Code);
        Assert.Equal(3, result.RepairCount); Assert.Equal(4, exchange.Requests.Length);
        Assert.Equal(ids[0], Assert.Single(result.Submissions).TargetId);
        Assert.All(exchange.Requests, sent => Assert.Equal(1, sent.AttemptNumber));
        var lastWire = OpenAiCompatibleChatCompletionsCodec.Prepare(exchange.Requests[^1], "synthetic-model");
        using var json = JsonDocument.Parse(lastWire.BodyUtf8);
        Assert.DoesNotContain(json.RootElement.GetProperty("messages").EnumerateArray(), message =>
            message.GetProperty("role").GetString() == "system" && message.GetProperty("content").GetString()!.Contains("scribe.result.invalid-evidence", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Incremental_output_exhaustion_keeps_progress_and_sends_no_finish_request()
    {
        var request = IncrementalRequest(root => root["limits"]!["maximumOutputTokens"] = 2);
        var id = request.ExecutableTargetIds[0];
        var exchange = Script(SubmitBatch(request, 1, new DocumentationScribeModelUsage(2, 2, 1, 1, 1), (id, IncrementalProposal(request, id))));
        var result = await ExecuteIncremental(request, exchange, EmptyRegistry());
        Assert.Single(exchange.Requests); Assert.Single(result.Submissions);
        Assert.Equal(DocumentationScribeFailureCode.Budget, Assert.IsType<DocumentationScribeFailureTerminal>(result.Stop).Code);
        Assert.Equal(2, exchange.Requests[0].OutputLimits.MaximumOutputTokens);
        Assert.Equal(2, result.RunEnvelope.Usage!.OutputTokens); Assert.Equal(1, result.RunEnvelope.Usage.ReasoningTokens);
        Assert.Equal(2, DocumentationScribeValidation.GetCoverage(request, result).Count(item => item.Status == "remaining"));
    }

    [Fact]
    public async Task Incremental_confirmed_Host_receipt_survives_caller_cancellation_mid_batch()
    {
        var request = IncrementalRequest(); using var cancellation = new CancellationTokenSource();
        var host = new IncrementalHost(() => cancellation.Cancel()); var ids = request.ExecutableTargetIds;
        var exchange = Script(SubmitBatch(request, 1, (ids[0], IncrementalProposal(request, ids[0])),
            (ids[1], IncrementalProposal(request, ids[1]))));
        var result = await ExecuteIncremental(request, exchange, EmptyRegistry(), host, cancellationToken: cancellation.Token);
        Assert.Equal(DocumentationScribeTerminalKind.Cancelled, result.Stop.Kind);
        Assert.Equal(DocumentationScribeAcknowledgementStage.DurablyRecorded, Assert.Single(result.Submissions).Stage);
        Assert.Equal(1, host.Calls); Assert.Single(exchange.Requests);
    }

    [Fact]
    public async Task Incremental_foreign_Host_receipt_never_claims_validation_or_durability()
    {
        var request = IncrementalRequest(); var id = request.ExecutableTargetIds[0]; var host = new IncrementalHost(foreign: true);
        var result = await ExecuteIncremental(request, Script(SubmitBatch(request, 1, (id, IncrementalProposal(request, id)))), EmptyRegistry(), host);
        Assert.Equal(DocumentationScribeFailureCode.Internal, Assert.IsType<DocumentationScribeFailureTerminal>(result.Stop).Code);
        Assert.Equal(DocumentationScribeAcknowledgementStage.Received, Assert.Single(result.Submissions).Stage);
        Assert.DoesNotContain(DocumentationScribeValidation.GetCoverage(request, result), item => item.Status is "validated" or "durably-recorded" or "patch-accepted");
    }

    [Fact]
    public async Task Incremental_partial_usage_keeps_reporting_populations_and_conservative_charges_distinct()
    {
        var request = IncrementalRequest(); var ids = request.ExecutableTargetIds;
        var exchange = Script(SubmitBatch(request, 1, new DocumentationScribeModelUsage(4, 2, 2, 2, 1),
                (ids[0], IncrementalProposal(request, ids[0]))),
            SubmitBatch(request, 2, new DocumentationScribeModelUsage(cachedInputTokens: 3),
                (ids[1], IncrementalProposal(request, ids[1]))));
        var result = await ExecuteIncremental(request, exchange, EmptyRegistry());
        Assert.Equal(2, exchange.Requests.Length); Assert.Equal(2, result.Submissions.Length);
        var usage = result.RunEnvelope.Usage!; var coverage = result.RunEnvelope.UsageCoverage!;
        Assert.Equal(4, usage.InputTokens); Assert.Equal(5, usage.CachedInputTokens);
        Assert.Equal(2, usage.UncachedInputTokens); Assert.Equal(2, usage.OutputTokens); Assert.Equal(1, usage.ReasoningTokens);
        Assert.Equal(1, coverage.InputRequests); Assert.Equal(2, coverage.CachedInputRequests);
        Assert.Equal(1, coverage.UncachedInputRequests); Assert.Equal(1, coverage.OutputRequests);
        Assert.Equal(2, result.RunEnvelope.ProviderRequestCount);
        Assert.Equal(8190, exchange.Requests[1].OutputLimits.MaximumOutputTokens);
        Assert.Equal(DocumentationScribeFailureCode.Budget, Assert.IsType<DocumentationScribeFailureTerminal>(result.Stop).Code);
        var encoded = DocumentationScribeRunResultWriter.Write(request, result);
        Assert.True(DocumentationScribeValidation.ParseRunResult(request, Attempt(), [], encoded.AsMemory()).IsValid);
    }

    [Fact]
    public async Task Incremental_missing_usage_is_unavailable_and_never_measured_zero()
    {
        var request = IncrementalRequest(); var id = request.ExecutableTargetIds[0];
        var exchange = Script(SubmitBatch(request, 1, (DocumentationScribeModelUsage?)null, (id, IncrementalProposal(request, id))));
        var result = await ExecuteIncremental(request, exchange, EmptyRegistry());
        Assert.Single(exchange.Requests); Assert.Single(result.Submissions); Assert.Null(result.RunEnvelope.Usage);
        Assert.Equal(0, result.RunEnvelope.UsageCoverage!.InputRequests);
        Assert.Equal(1, result.RunEnvelope.ProviderRequestCount);
        Assert.Equal(DocumentationScribeFailureCode.Budget, Assert.IsType<DocumentationScribeFailureTerminal>(result.Stop).Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Incremental_target_limit_retains_the_complete_manifest_and_stops_before_another_target(int maximumTargets)
    {
        var request = IncrementalRequest(); var ids = request.ExecutableTargetIds;
        var scope = new DocumentationScribeExecutionScope(new(DocumentationScribeInvocationLimits.Create(maximumTargets: maximumTargets)), null, 0, 0);
        var exchange = Script(SubmitBatch(request, 1, ids.Select(id => (id, IncrementalProposal(request, id))).ToArray()));
        var result = await DocumentationScribeExecution.RunAsync(request, Attempt(), Prompt(request), EmptyRegistry(), exchange,
            new("provider.synthetic.v1", "model.synthetic.v1", "scribe-protocol.v1"), TimeProvider.System, scope: scope);
        Assert.Equal(maximumTargets, result.Submissions.Length); Assert.Equal(maximumTargets, exchange.Requests.Length);
        Assert.Equal(3, DocumentationScribeValidation.GetCoverage(request, result).Length);
        Assert.Equal(3 - maximumTargets, DocumentationScribeValidation.GetCoverage(request, result).Count(item => item.Status == "remaining"));
        Assert.Equal(DocumentationScribeFailureCode.Budget, Assert.IsType<DocumentationScribeFailureTerminal>(result.Stop).Code);
    }

    [Fact]
    public async Task Incremental_fresh_attempt_restores_documentation_without_transcript_or_historical_usage()
    {
        var request = IncrementalRequest(); var id = request.ExecutableTargetIds[0];
        var scope = new DocumentationScribeExecutionScope(new(DocumentationScribeInvocationLimits.Create(maximumTargets: 1)), null, 0, 0);
        var initial = Script(SubmitBatch(request, 1, new DocumentationScribeModelUsage(7, 2, 3, 4, 1), (id, IncrementalProposal(request, id))));
        var saved = await DocumentationScribeExecution.RunAsync(request, Attempt(), Prompt(request), EmptyRegistry(), initial,
            new("provider.synthetic.v1", "model.synthetic.v1", "scribe-protocol.v1"), TimeProvider.System, scope: scope);
        var resumed = Script(FinishConversation(request));
        var result = await ExecuteIncremental(request, resumed, EmptyRegistry(), restored: saved);
        Assert.Single(result.Submissions); Assert.Equal(1, result.RunEnvelope.ProviderRequestCount);
        Assert.Equal(0, result.RunEnvelope.Usage!.OutputTokens); Assert.Equal(0, result.RunEnvelope.Usage.InputTokens);
        Assert.Empty(Assert.Single(resumed.Requests).CompletedToolExchanges);
        Assert.Equal(DocumentationScribeFinishReason.Unresolved, Assert.IsType<DocumentationScribeFinishedTerminal>(result.Stop).Reason);
    }

    [Fact]
    public async Task Incremental_actual_codec_context_limit_preserves_progress_without_an_oversized_HTTP_dispatch()
    {
        var request = IncrementalRequest(root =>
        { root["limits"]!["maximumProviderRequests"] = 128; root["limits"]!["maximumToolRounds"] = 128; root["limits"]!["maximumToolCalls"] = 128; });
        var registry = new DocumentationScribeToolRegistryBuilder(request.ToolPolicyId).Add(new SyntheticDescriptor("tool.read"),
            new SyntheticPort(DocumentationScribeToolOutcome.Complete), new SyntheticCodec(suppressDynamicEvidence: true),
            "Read bounded synthetic data.", ToolSchema, 128).Build();
        using var handler = new IncrementalContextHandler(request);
        using var transport = new OpenAiCompatibleHttpModelExchange(new(new Uri("https://example.test/v1/chat/completions"),
            "synthetic-model", networkEnabled: true), handler, disposeHandler: false);
        var result = await ExecuteIncremental(request, transport, registry);
        Assert.True(result.Stop is DocumentationScribeFinishedTerminal, "Context result: " + Encoding.UTF8.GetString(DocumentationScribeRunResultWriter.Write(request, result).AsSpan()));
        Assert.Equal(DocumentationScribeFinishReason.Context, Assert.IsType<DocumentationScribeFinishedTerminal>(result.Stop).Reason);
        Assert.Single(result.Submissions); Assert.Equal(handler.Calls, result.RunEnvelope.ProviderRequestCount);
        Assert.InRange(handler.Calls, 30, 50);
        Assert.True(handler.MaximumBodyBytes <= DocumentationScribeBoundary.MaximumLogicalRequestUtf8Bytes);
        Assert.Equal(2, DocumentationScribeValidation.GetCoverage(request, result).Count(item => item.Status == "remaining"));
        Assert.DoesNotContain("synthetic-context-reasoning", Encoding.UTF8.GetString(DocumentationScribeRunResultWriter.Write(request, result).AsSpan()), StringComparison.Ordinal);
    }

    private static DocumentationScribeRequest IncrementalRequest(Action<JsonObject>? mutate = null, int count = 3) => Request(root =>
    {
        AddManifestMembers(root, count);
        var template = root["manifest"]![0]!["styleProfile"]!.DeepClone(); template["componentPolicies"] = new JsonArray();
        var summary = root["evidenceReferences"]!.AsArray().Single(item => item!["evidenceReferenceId"]!.GetValue<string>() == "evidence.summary")!.DeepClone();
        var references = new JsonArray(); var executable = new JsonArray();
        for (var index = 0; index < count; index++)
        {
            var member = root["manifest"]![index]!; member["styleProfile"] = template.DeepClone();
            member["target"]!["applicableComponents"] = new JsonArray();
            member["target"]!["symbolRef"]!["documentationCommentId"] = index == 0 ? "T:Synthetic.Widget" : "P:Synthetic.Widget.P" + index;
            var evidence = summary.DeepClone(); evidence["evidenceReferenceId"] = "evidence.target." + index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
            evidence["subject"]!["symbolRef"] = member["target"]!["symbolRef"]!.DeepClone();
            references.Add(evidence); member["evidenceReferenceIds"] = new JsonArray("evidence.target." + index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture));
            executable.Add(member["targetId"]!.GetValue<string>());
        }
        root["evidenceReferences"] = references; root["executableTargetIds"] = executable; mutate?.Invoke(root);
    });

    private static JsonObject IncrementalProposal(DocumentationScribeRequest request, string targetId)
    {
        var member = request.Manifest.Single(item => item.TargetId == targetId);
        return JsonNode.Parse(CanonicalJson.Serialize(new
        {
            kind = "proposal",
            target = new
            {
                request.Context.RepositoryContextRef,
                member.Target.SymbolRef,
                sourceCommitment = new { locator = member.Target.SourceLocator, contentSha256 = member.Target.SourceSha256 }
            },
            contentUnits = new[] { new { kind = "content.summary", lines = new[] { "Describes the selected target." },
                claimCategoryId = "claim.purpose", evidenceReferenceIds = member.EvidenceReferenceIds } },
        }).AsSpan())!.AsObject();
    }

    private static DocumentationScribeModelResponse SubmitBatch(DocumentationScribeRequest request, int sequence,
        params (string TargetId, JsonObject Result)[] items) => SubmitBatch(request, sequence, new(1, 1, 0, 1, 0), items);

    private static DocumentationScribeModelResponse SubmitBatch(DocumentationScribeRequest request, int sequence,
        DocumentationScribeModelUsage? usage, params (string TargetId, JsonObject Result)[] items)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            kind = "submit",
            scribeRequestSha256 = request.ArtifactSha256,
            sequence,
            items = items.Select(item => new { targetId = item.TargetId, result = item.Result })
        });
        return new([], [new(payload, "call.submit." + sequence)], null, usage, null, null,
            new DocumentationScribeAssistantContinuation("exact submission content " + sequence, "exact submission reasoning " + sequence),
            DocumentationScribeContinuationObservation.Observed);
    }

    private static DocumentationScribeModelResponse FinishConversation(DocumentationScribeRequest request) =>
        new([], [new(JsonSerializer.SerializeToUtf8Bytes(new { kind = "finish", scribeRequestSha256 = request.ArtifactSha256 }), "call.finish")],
            usage: new(0, 0, 0, 0, 0));

    private static Task<DocumentationScribeRunResult> ExecuteIncremental(DocumentationScribeRequest request,
        IDocumentationScribeModelExchange exchange, DocumentationScribeToolRegistry registry,
        IDocumentationScribeSubmissionHost? host = null, DocumentationScribeRunResult? restored = null,
        CancellationToken cancellationToken = default) => DocumentationScribeExecution.RunAsync(request, Attempt(), Prompt(request),
            registry, exchange, new("provider.synthetic.v1", "model.synthetic.v1", "scribe-protocol.v1"), TimeProvider.System,
            cancellationToken, submissionHost: host, restoredProgress: restored);

    private sealed class IncrementalContextHandler(DocumentationScribeRequest request) : HttpMessageHandler
    {
        private readonly string reasoning = "synthetic-context-reasoning" + new string('r', 900_000);
        internal int Calls { get; private set; }
        internal int MaximumBodyBytes { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken token)
        {
            var body = await message.Content!.ReadAsByteArrayAsync(token);
            MaximumBodyBytes = Math.Max(MaximumBodyBytes, body.Length); Calls++;
            string arguments;
            if (Calls == 1)
                arguments = Encoding.UTF8.GetString(SubmitBatch(request, 1,
                    (request.ExecutableTargetIds[0], IncrementalProposal(request, request.ExecutableTargetIds[0]))).TerminalSubmissions[0].TerminalUtf8Json.Span);
            else arguments = "{\"referenceId\":\"small\"}";
            var response = JsonSerializer.SerializeToUtf8Bytes(new
            {
                choices = new[] { new { index = 0, message = new { role = "assistant", content = (string?)null, reasoning_content = reasoning,
                    tool_calls = new[] { new { id = "call.context." + Calls, type = "function", function = new
                    { name = Calls == 1 ? OpenAiCompatibleChatCompletionsCodec.TerminalAlias : "cs_tool_000", arguments } } } }, finish_reason = "tool_calls" } },
                usage = new
                {
                    prompt_tokens = 0,
                    completion_tokens = 0,
                    prompt_cache_hit_tokens = 0,
                    prompt_cache_miss_tokens = 0,
                    completion_tokens_details = new { reasoning_tokens = 0 }
                },
            });
            return new(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(response)
                { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") } }
            };
        }
    }

    private sealed class IncrementalHost(Action? onReceipt = null, bool foreign = false) : IDocumentationScribeSubmissionHost
    {
        public ValueTask<DocumentationScribeProgressAcknowledgement?> ObserveProgressAsync(
            DocumentationScribeProgressNotification progress, CancellationToken cancellationToken) =>
            ValueTask.FromResult<DocumentationScribeProgressAcknowledgement?>(new(progress.ProgressSha256));

        internal int Calls { get; private set; }
        public ValueTask<DocumentationScribeSubmissionAcknowledgement?> AcknowledgeAsync(
            DocumentationScribeValidatedSubmission submission, DocumentationScribeProgressNotification progress,
            CancellationToken cancellationToken)
        {
            Calls++; onReceipt?.Invoke();
            return ValueTask.FromResult<DocumentationScribeSubmissionAcknowledgement?>(new(foreign ? new string('f', 64) : submission.ScribeRequestSha256,
                submission.TargetId, submission.ContentSha256, DocumentationScribeAcknowledgementStage.DurablyRecorded)
            { ProgressSha256 = progress.ProgressSha256 });
        }
    }
}
