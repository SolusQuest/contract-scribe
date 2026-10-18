using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Agent.Prompting;
using ContractScribe.Agent.Runtime;
using ContractScribe.Cli;
using ContractScribe.Core;

namespace ContractScribe.Tests;

public sealed partial class DocumentationScribeRuntimeTests
{
    [Fact]
    public void Incremental_content_commitment_is_independent_of_equivalent_JSON_string_escapes()
    {
        var request = IncrementalRequest(); var id = request.ExecutableTargetIds[0];
        var plain = IncrementalProposal(request, id).ToJsonString();
        var escaped = plain.Replace("Describes", "\\u0044escribes", StringComparison.Ordinal);
        Assert.NotEqual(plain, escaped);
        var first = DocumentationScribeValidation.ValidateTargetSubmission(request, id, [], Encoding.UTF8.GetBytes(plain));
        var second = DocumentationScribeValidation.ValidateTargetSubmission(request, id, [], Encoding.UTF8.GetBytes(escaped));
        Assert.NotNull(first.Submission); Assert.NotNull(second.Submission);
        Assert.Equal(first.Submission.ContentSha256, second.Submission.ContentSha256);
        Assert.Equal(first.Submission.ResultUtf8Json.ToArray(), second.Submission.ResultUtf8Json.ToArray());
    }

    [Fact]
    public async Task Incremental_result_rejects_uncited_dynamic_overlay_even_when_the_row_is_trusted()
    {
        var request = Request();
        var result = await ExecuteIncremental(request, Script(SubmitResponse(ReadTerminal("proposal-result.json"), request), FinishResponse(request)), EmptyRegistry());
        Assert.True(DocumentationScribeValidation.TryCreateDynamicEvidenceReference(request, SyntheticDynamicEvidenceInput(Sha256("uncited")), out var reference));
        var json = JsonNode.Parse(DocumentationScribeRunResultWriter.Write(request, result).AsSpan())!.AsObject();
        json["dynamicEvidenceReferences"] = JsonNode.Parse(CanonicalJson.Serialize(new[] { reference! }).AsSpan());
        var parsed = DocumentationScribeValidation.ParseRunResult(request, Attempt(), [reference!], Encoding.UTF8.GetBytes(json.ToJsonString()));
        Assert.False(parsed.IsValid);
    }

    [Fact]
    public async Task Incremental_exact_replay_is_idempotent_and_changed_replay_cannot_overwrite_content()
    {
        var request = IncrementalRequest(); var ids = request.ExecutableTargetIds; var host = new IncrementalHost();
        var first = SubmitBatch(request, 1, (ids[0], IncrementalProposal(request, ids[0])));
        var changed = IncrementalProposal(request, ids[0]); changed["contentUnits"]![0]!["lines"]![0] = "Different generated documentation.";
        var exchange = Script(first, WithCall(first, "call.replay"),
            WithCall(SubmitBatch(request, 1, (ids[0], changed)), "call.changed"),
            SubmitBatch(request, 2, (ids[2], IncrementalProposal(request, ids[2])), (ids[1], IncrementalProposal(request, ids[1]))),
            FinishConversation(request));
        var result = await ExecuteIncremental(request, exchange, EmptyRegistry(), host);
        Assert.Equal(3, host.Calls); Assert.Equal(1, result.RepairCount); Assert.Equal(3, result.NextSequence);
        Assert.Equal(DocumentationScribeFinishReason.Completed, Assert.IsType<DocumentationScribeFinishedTerminal>(result.Stop).Reason);
        var history = exchange.Requests[^1].CompletedToolExchanges;
        Assert.Equal(history[0].ResultUtf8Json.ToArray(), history[1].ResultUtf8Json.ToArray());
        Assert.Contains("scribe.submission.changed-replay", Encoding.UTF8.GetString(history[2].ResultUtf8Json.Span), StringComparison.Ordinal);
        Assert.DoesNotContain(result.Submissions, item => Encoding.UTF8.GetString(item.Validation.ResultUtf8Json.AsSpan()).Contains("Different generated", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Incremental_foreign_and_out_of_order_rejections_preserve_valid_siblings_and_semantic_order()
    {
        var request = IncrementalRequest(); var ids = request.ExecutableTargetIds; var host = new IncrementalHost();
        var exchange = Script(SubmitBatch(request, 1, ("target.foreign", IncrementalProposal(request, ids[1])),
                (ids[0], IncrementalProposal(request, ids[0]))),
            SubmitBatch(request, 3, (ids[1], IncrementalProposal(request, ids[1]))),
            SubmitBatch(request, 2, (ids[2], IncrementalProposal(request, ids[2])), (ids[1], IncrementalProposal(request, ids[1]))),
            FinishConversation(request));
        var result = await ExecuteIncremental(request, exchange, EmptyRegistry(), host);
        Assert.Equal(3, host.Calls); Assert.Equal(2, result.RepairCount);
        Assert.Equal(DocumentationScribeFinishReason.Completed, Assert.IsType<DocumentationScribeFinishedTerminal>(result.Stop).Reason);
        var acks = exchange.Requests[^1].CompletedToolExchanges.Select(item => Encoding.UTF8.GetString(item.ResultUtf8Json.Span)).ToArray();
        Assert.Contains("scribe.submission.foreign-target", acks[0], StringComparison.Ordinal);
        Assert.DoesNotContain("target.foreign", acks[0], StringComparison.Ordinal);
        Assert.Contains("scribe.submission.out-of-order", acks[1], StringComparison.Ordinal);
        Assert.Contains("\"nextSequence\":2", acks[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Incremental_duplicate_target_and_skip_do_not_overwrite_progress_or_hide_omissions()
    {
        var request = IncrementalRequest(); var ids = request.ExecutableTargetIds; var host = new IncrementalHost();
        var skip = JsonNode.Parse("{\"kind\":\"skip\",\"reason\":\"scribe.skip.insufficient-evidence\",\"evidenceReferenceIds\":[]}")!.AsObject();
        var exchange = Script(SubmitBatch(request, 1, (ids[0], IncrementalProposal(request, ids[0])),
                (ids[0], IncrementalProposal(request, ids[0]))),
            SubmitBatch(request, 2, (ids[1], skip)),
            SubmitBatch(request, 3, (ids[1], IncrementalProposal(request, ids[1]))), FinishConversation(request));
        var result = await ExecuteIncremental(request, exchange, EmptyRegistry(), host);
        Assert.Equal(2, host.Calls); Assert.Equal(2, result.RepairCount);
        Assert.Equal(DocumentationScribeFinishReason.Unresolved, Assert.IsType<DocumentationScribeFinishedTerminal>(result.Stop).Reason);
        Assert.Equal(new[] { "durably-recorded", "skipped", "remaining" }, DocumentationScribeValidation.GetCoverage(request, result).Select(item => item.Status));
        Assert.IsType<DocumentationScribeSkipTerminal>(result.Submissions.Single(item => item.TargetId == ids[1]).Result);
        var omitted = await ExecuteIncremental(request, Script(FinishConversation(request)), EmptyRegistry());
        Assert.Empty(omitted.Submissions);
        Assert.Equal(DocumentationScribeFinishReason.Unresolved, Assert.IsType<DocumentationScribeFinishedTerminal>(omitted.Stop).Reason);
        Assert.All(DocumentationScribeValidation.GetCoverage(request, omitted), item => Assert.Equal("remaining", item.Status));
    }

    [Fact]
    public async Task Incremental_invalid_first_item_does_not_discard_later_valid_items_and_can_be_repaired()
    {
        var request = IncrementalRequest(); var ids = request.ExecutableTargetIds;
        var invalid = IncrementalProposal(request, ids[0]); invalid["target"]!["sourceCommitment"]!["contentSha256"] = new string('f', 64);
        var exchange = Script(SubmitBatch(request, 1, (ids[0], invalid), (ids[1], IncrementalProposal(request, ids[1])),
                (ids[2], IncrementalProposal(request, ids[2]))),
            SubmitBatch(request, 2, (ids[0], IncrementalProposal(request, ids[0]))), FinishConversation(request));
        var result = await ExecuteIncremental(request, exchange, EmptyRegistry());
        Assert.Equal(3, result.Submissions.Length); Assert.Empty(result.TargetFailures); Assert.Equal(1, result.RepairCount);
        Assert.Equal(DocumentationScribeFinishReason.Completed, Assert.IsType<DocumentationScribeFinishedTerminal>(result.Stop).Reason);
        Assert.Empty(exchange.Requests[1].CompletedToolExchanges.Single().EvidenceReferences);
        Assert.Equal(2, JsonNode.Parse(exchange.Requests[1].Messages.Single(item => item.Kind == DocumentationScribeMessageKind.TargetEvidence).Content)!["progress"]!.AsArray().Count);
    }

    [Fact]
    public async Task Incremental_bound_dynamic_evidence_restores_without_source_body_transcript_or_regeneration()
    {
        var request = Request(); var host = new IncrementalHost();
        const string marker = "private-evidence-body-never-restored";
        var registry = RegistryWithCodec(new SyntheticPort(DocumentationScribeToolOutcome.Complete),
            new SyntheticCodec(resultContentMarker: marker, referenceSpecificDynamicEvidence: true));
        var first = await ExecuteIncremental(request, new DynamicCitationExchange("proposal-result.json", marker), registry, host);
        Assert.Single(first.DynamicEvidenceReferences); Assert.Equal(1, host.Calls);
        var bytes = DocumentationScribeRunResultWriter.Write(request, first);
        Assert.DoesNotContain(marker, Encoding.UTF8.GetString(bytes.AsSpan()), StringComparison.Ordinal);
        var parsed = DocumentationScribeValidation.ParseRunResult(request, Attempt(), first.DynamicEvidenceReferences, bytes.AsMemory());
        Assert.True(parsed.IsValid);
        var exchange = Script(FinishConversation(request));
        var restored = await ExecuteIncremental(request, exchange, EmptyRegistry(), host, parsed.Result);
        Assert.Empty(exchange.Requests); Assert.Equal(1, host.Calls);
        Assert.Equal(first.Submissions[0].ContentSha256, restored.Submissions[0].ContentSha256);
        Assert.Equal(0, restored.RunEnvelope.ProviderRequestCount); Assert.Null(restored.RunEnvelope.Usage);
        Assert.Single(restored.DynamicEvidenceReferences);
        var changed = Request(root => root["manifest"]![0]!["target"]!["sourceCommitment"]!["contentSha256"] = new string('f', 64));
        await Assert.ThrowsAsync<ArgumentException>(() => ExecuteIncremental(changed, exchange, EmptyRegistry(), restored: first));
        Assert.Empty(exchange.Requests);
    }

    [Fact]
    public async Task Incremental_uncertain_partial_batch_requires_Host_reconciliation_before_any_replay()
    {
        var request = IncrementalRequest(); var ids = request.ExecutableTargetIds; var host = new InterruptedSubmissionHost();
        var exchange = Script(SubmitBatch(request, 1, (ids[0], IncrementalProposal(request, ids[0])), (ids[1], IncrementalProposal(request, ids[1]))));
        var first = await ExecuteIncremental(request, exchange, EmptyRegistry(), host);
        Assert.Equal(DocumentationScribeFailureCode.Internal, Assert.IsType<DocumentationScribeFailureTerminal>(first.Stop).Code);
        Assert.Equal(DocumentationScribeAcknowledgementStage.DurablyRecorded, first.Submissions.Single(item => item.TargetId == ids[0]).Stage);
        Assert.Equal(DocumentationScribeAcknowledgementStage.Received, first.Submissions.Single(item => item.TargetId == ids[1]).Stage);
        var resumed = Script(FinishConversation(request));
        var second = await ExecuteIncremental(request, resumed, EmptyRegistry(), host, first);
        Assert.Empty(resumed.Requests); Assert.Equal(2, host.Calls); Assert.Equal(2, second.Submissions.Length);
        Assert.Equal(DocumentationScribeFailureCode.Internal, Assert.IsType<DocumentationScribeFailureTerminal>(second.Stop).Code);
    }

    [Fact]
    public async Task Incremental_cancelled_prepared_restore_keeps_cancellation_priority_without_model_work()
    {
        var request = IncrementalRequest();
        var first = await ExecuteIncremental(request, Script(SubmitBatch(request, 1,
            request.ExecutableTargetIds.Select(id => (id, IncrementalProposal(request, id))).ToArray()), FinishConversation(request)), EmptyRegistry());
        var exchange = Script(FinishConversation(request));
        var session = DocumentationScribeExecution.Prepare(request, Attempt(), Prompt(request), EmptyRegistry(), exchange,
            new("provider.synthetic.v1", "model.synthetic.v1", "scribe-protocol.v1"), TimeProvider.System, restoredProgress: first);
        using var caller = new CancellationTokenSource(); caller.Cancel();
        var result = await DocumentationScribeExecution.RunAsync(session, caller.Token);
        Assert.IsType<DocumentationScribeCancelledTerminal>(result.Stop);
        Assert.Equal(3, result.Submissions.Length); Assert.Empty(exchange.Requests);
    }

    [Theory]
    [InlineData("scalar")]
    [InlineData("string-sequence")]
    public async Task Incremental_wrong_JSON_value_kinds_are_finite_model_rejections(string shape)
    {
        var request = IncrementalRequest(); var id = request.ExecutableTargetIds[0];
        var invalid = shape == "scalar" ? "[]" : JsonSerializer.Serialize(new
        { kind = "submit", scribeRequestSha256 = request.ArtifactSha256, sequence = "two", items = new[] { new { targetId = id, result = IncrementalProposal(request, id) } } });
        DocumentationScribeModelResponse Rejection(int number) => new([], [new(Encoding.UTF8.GetBytes(invalid), "call.invalid." + number)], usage: new(0, 0, 0, 0, 0));
        var host = new IncrementalHost();
        var result = await ExecuteIncremental(request, Script(SubmitBatch(request, 1, (id, IncrementalProposal(request, id))),
            Rejection(1), Rejection(2), Rejection(3)), EmptyRegistry(), host);
        Assert.Equal(1, host.Calls); Assert.Single(result.Submissions); Assert.Equal(3, result.RepairCount);
        Assert.Equal(DocumentationScribeFailureCode.Validation, Assert.IsType<DocumentationScribeFailureTerminal>(result.Stop).Code);
    }

    [Fact]
    public async Task Incremental_full_result_capacity_is_checked_before_any_unrepresentable_Host_receipt()
    {
        var request = IncrementalRequest(root =>
        {
            root["limits"]!["maximumProviderRequests"] = 128; root["limits"]!["maximumToolCalls"] = 128;
            root["limits"]!["maximumEvidenceReferences"] = 64;
            foreach (var member in root["manifest"]!.AsArray()) member!["styleProfile"]!["summary"]!["maximumScalars"] = 16_384;
        }, count: 20);
        var responses = request.ExecutableTargetIds.Select((id, index) =>
        {
            var proposal = IncrementalProposal(request, id); proposal["contentUnits"]![0]!["lines"] = new JsonArray(Enumerable.Range(0, 8).Select(_ => (JsonNode?)JsonValue.Create(new string('"', 2_048))).ToArray());
            return SubmitBatch(request, index + 1, (id, proposal));
        }).Append(FinishConversation(request)).ToArray();
        var host = new IncrementalHost(); var exchange = Script(responses);
        var result = await ExecuteIncremental(request, exchange, EmptyRegistry(), host);
        Assert.Equal(DocumentationScribeFailureCode.Budget, Assert.IsType<DocumentationScribeFailureTerminal>(result.Stop).Code);
        Assert.InRange(result.Submissions.Length, 5, 19); Assert.Equal(result.Submissions.Length, host.Calls);
        Assert.Equal(host.Calls + 1, exchange.Requests.Length);
        var bytes = DocumentationScribeRunResultWriter.Write(request, result);
        Assert.InRange(bytes.Length, 500_000, DocumentationScribeContract.MaximumArtifactUtf8Bytes);
        Assert.True(DocumentationScribeValidation.ParseRunResult(request, Attempt(), [], bytes.AsMemory()).IsValid);
        Assert.Equal(20 - host.Calls, DocumentationScribeValidation.GetCoverage(request, result).Count(item => item.Status == "remaining"));
    }

    private static DocumentationScribeModelResponse WithCall(DocumentationScribeModelResponse response, string callId) =>
        new([], [new(response.TerminalSubmissions.Single().TerminalUtf8Json, callId)], response.Failure, response.Usage,
            response.Cache, response.Cost, response.AssistantContinuation, response.ContinuationObservation);

    private sealed class InterruptedSubmissionHost : IDocumentationScribeSubmissionHost
    {
        internal int Calls { get; private set; }
        public ValueTask<DocumentationScribeSubmissionAcknowledgement?> AcknowledgeAsync(
            DocumentationScribeValidatedSubmission submission, CancellationToken cancellationToken)
        {
            if (++Calls == 2) throw new IOException("synthetic-unknown-receipt-outcome");
            return ValueTask.FromResult<DocumentationScribeSubmissionAcknowledgement?>(new(submission.ScribeRequestSha256,
                submission.TargetId, submission.ContentSha256, DocumentationScribeAcknowledgementStage.DurablyRecorded));
        }
    }
}
