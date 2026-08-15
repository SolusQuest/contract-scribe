using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Agent.Runtime;
using ContractScribe.Core;

namespace ContractScribe.Tests;

public sealed class DocumentationScribeRuntimeTests
{
    private const string AttemptId = "scribe-attempt.0123456789abcdef0123456789abcdef";
    private const string ToolPolicyId = "tool-policy.read-only.v1";
    private static readonly byte[] ToolSchema = """
        {"additionalProperties":false,"properties":{"referenceId":{"type":"string"}},"required":["referenceId"],"type":"object"}
        """u8.ToArray();

    [Theory]
    [InlineData("proposal-result.json", DocumentationScribeTerminalKind.Proposal)]
    [InlineData("skip-result.json", DocumentationScribeTerminalKind.Skip)]
    public async Task Direct_terminal_is_reconstructed_and_validated(
        string fixture,
        DocumentationScribeTerminalKind expectedKind)
    {
        var response = TerminalResponse(ReadTerminal(fixture));
        var exchange = Script(response);

        var result = await CreateRuntime(exchange, EmptyRegistry()).RunAsync(
            Request(), Attempt(), Prompt());

        Assert.Equal(expectedKind, result.Terminal.Kind);
        Assert.Equal(1, result.RunEnvelope.ProviderRequestCount);
        Assert.Equal(0, result.RunEnvelope.ToolRoundCount);
        Assert.Equal(0, result.RunEnvelope.ToolCallCount);
        Assert.Empty(result.RunEnvelope.Diagnostics);
        Assert.Single(exchange.Requests);
    }

    [Fact]
    public async Task Two_tool_rounds_preserve_response_order_and_return_one_proposal()
    {
        var port = new SyntheticPort(DocumentationScribeToolOutcome.Complete);
        var registry = Registry(("tool.zeta", port), ("tool.alpha", port));
        var exchange = Script(
            ToolResponse(Call(0, "call.z", "tool.zeta", "z"), Call(1, "call.a", "tool.alpha", "a")),
            ToolResponse(Call(0, "call.a2", "tool.alpha", "a2")),
            TerminalResponse(ReadTerminal("proposal-result.json")));

        var result = await CreateRuntime(exchange, registry).RunAsync(Request(), Attempt(), Prompt());

        Assert.Equal(DocumentationScribeTerminalKind.Proposal, result.Terminal.Kind);
        Assert.Equal(3, result.RunEnvelope.ProviderRequestCount);
        Assert.Equal(2, result.RunEnvelope.ToolRoundCount);
        Assert.Equal(3, result.RunEnvelope.ToolCallCount);
        Assert.Equal(new[] { "z", "a", "a2" }, port.References.ToArray());
        Assert.Equal(new[] { "tool.alpha", "tool.zeta" }, exchange.Requests[0].Tools.Select(tool => tool.OperationId));
        Assert.Equal(new[] { "call.z", "call.a" }, exchange.Requests[1].CompletedToolExchanges.Select(item => item.CallId));
        Assert.Equal(new[] { "call.z", "call.a", "call.a2" }, exchange.Requests[2].CompletedToolExchanges.Select(item => item.CallId));
    }

    [Theory]
    [MemberData(nameof(ToolOutcomes))]
    public async Task Every_core_tool_outcome_has_one_closed_mapping(
        DocumentationScribeToolOutcome outcome,
        DocumentationScribeTerminalKind expectedKind,
        DocumentationScribeFailureCode? expectedFailure)
    {
        var registry = Registry(("tool.read", new SyntheticPort(outcome)));
        var steps = outcome == DocumentationScribeToolOutcome.Complete
            || outcome == DocumentationScribeToolOutcome.Incomplete
            || outcome == DocumentationScribeToolOutcome.Unavailable
                ? new[]
                {
                    ToolResponse(Call(0, "call.one", "tool.read", "one")),
                    TerminalResponse(ReadTerminal("proposal-result.json")),
                }
                : new[] { ToolResponse(Call(0, "call.one", "tool.read", "one")) };

        var result = await CreateRuntime(Script(steps), registry).RunAsync(Request(), Attempt(), Prompt());

        Assert.Equal(expectedKind, result.Terminal.Kind);
        if (expectedFailure is not null)
        {
            Assert.Equal(expectedFailure, Assert.IsType<DocumentationScribeFailureTerminal>(result.Terminal).Code);
        }
    }

    public static TheoryData<DocumentationScribeToolOutcome, DocumentationScribeTerminalKind, DocumentationScribeFailureCode?> ToolOutcomes => new()
    {
        { DocumentationScribeToolOutcome.Complete, DocumentationScribeTerminalKind.Proposal, null },
        { DocumentationScribeToolOutcome.Incomplete, DocumentationScribeTerminalKind.Proposal, null },
        { DocumentationScribeToolOutcome.Unavailable, DocumentationScribeTerminalKind.Proposal, null },
        { DocumentationScribeToolOutcome.Failure, DocumentationScribeTerminalKind.Failure, DocumentationScribeFailureCode.ToolProtocol },
        { DocumentationScribeToolOutcome.Cancelled, DocumentationScribeTerminalKind.Failure, DocumentationScribeFailureCode.ToolProtocol },
        { DocumentationScribeToolOutcome.TimedOut, DocumentationScribeTerminalKind.Failure, DocumentationScribeFailureCode.Timeout },
        { DocumentationScribeToolOutcome.BudgetExhausted, DocumentationScribeTerminalKind.Failure, DocumentationScribeFailureCode.Budget },
    };

    [Fact]
    public async Task Whole_round_is_rejected_before_any_tool_is_invoked()
    {
        var port = new SyntheticPort(DocumentationScribeToolOutcome.Complete);
        var exchange = Script(ToolResponse(
            Call(0, "call.one", "tool.read", "one"),
            Call(1, "call.one", "tool.read", "two")));

        var result = await CreateRuntime(exchange, Registry(("tool.read", port))).RunAsync(
            Request(), Attempt(), Prompt());

        Assert.Equal(DocumentationScribeFailureCode.ToolProtocol, FailureCode(result));
        Assert.Empty(port.References);
        Assert.Equal(0, result.RunEnvelope.ToolRoundCount);
        Assert.Equal(0, result.RunEnvelope.ToolCallCount);
    }

    [Theory]
    [InlineData("unknown-operation")]
    [InlineData("conflicting-terminal")]
    [InlineData("duplicate-terminal")]
    [InlineData("bad-response-index")]
    [InlineData("malformed-arguments")]
    public async Task Provider_protocol_conflicts_fail_closed(string scenario)
    {
        var terminal = new DocumentationScribeModelTerminalSubmission(ReadTerminal("skip-result.json"));
        var response = scenario switch
        {
            "unknown-operation" => ToolResponse(Call(0, "call.one", "tool.unknown", "one")),
            "conflicting-terminal" => new DocumentationScribeModelResponse(
                [Call(0, "call.one", "tool.read", "one")], [terminal]),
            "duplicate-terminal" => new DocumentationScribeModelResponse([], [terminal, terminal]),
            "bad-response-index" => ToolResponse(Call(1, "call.one", "tool.read", "one")),
            "malformed-arguments" => ToolResponse(new DocumentationScribeModelToolCall(
                0, "call.one", "tool.read", "{\"unexpected\":true}"u8.ToArray())),
            _ => throw new InvalidOperationException(),
        };

        var result = await CreateRuntime(
            Script(response),
            Registry(("tool.read", new SyntheticPort(DocumentationScribeToolOutcome.Complete))))
            .RunAsync(Request(), Attempt(), Prompt());

        Assert.Equal(DocumentationScribeFailureCode.ToolProtocol, FailureCode(result));
    }

    [Fact]
    public async Task Transient_failures_retry_but_final_transient_is_provider_failure()
    {
        var transient = FailureResponse(DocumentationScribeModelFailureCode.TransientUnavailable);
        var succeeds = Script(transient, TerminalResponse(ReadTerminal("skip-result.json")));
        var success = await CreateRuntime(succeeds, EmptyRegistry()).RunAsync(Request(), Attempt(), Prompt());

        Assert.Equal(DocumentationScribeTerminalKind.Skip, success.Terminal.Kind);
        Assert.Equal(2, success.RunEnvelope.AttemptNumber);
        Assert.Equal(2, success.RunEnvelope.ProviderRequestCount);
        Assert.Empty(succeeds.Requests[1].CompletedToolExchanges);

        var exhausts = Script(transient, transient, transient);
        var exhausted = await CreateRuntime(exhausts, EmptyRegistry()).RunAsync(Request(), Attempt(), Prompt());
        Assert.Equal(DocumentationScribeFailureCode.Provider, FailureCode(exhausted));
        Assert.Equal(3, exhausted.RunEnvelope.AttemptNumber);
        Assert.Equal(3, exhausted.RunEnvelope.ProviderRequestCount);
    }

    [Fact]
    public async Task Permanent_and_exchange_exceptions_do_not_retry_or_leak()
    {
        const string hostile = "credential=super-secret C:\\private\\repository";
        var permanent = Script(FailureResponse(DocumentationScribeModelFailureCode.Authentication));
        var provider = await CreateRuntime(permanent, EmptyRegistry()).RunAsync(Request(), Attempt(), Prompt());
        Assert.Equal(DocumentationScribeFailureCode.Provider, FailureCode(provider));
        Assert.Single(permanent.Requests);

        var throws = new HostileFailureExchange(hostile);
        var internalFailure = await CreateRuntime(throws, EmptyRegistry()).RunAsync(Request(), Attempt(), Prompt());
        Assert.Equal(DocumentationScribeFailureCode.Internal, FailureCode(internalFailure));
        var publicText = JsonSerializer.Serialize(internalFailure) + string.Join(
            " ",
            internalFailure.RunEnvelope.Diagnostics.Select(item => item.ToString()));
        Assert.DoesNotContain(hostile, publicText, StringComparison.Ordinal);
        Assert.DoesNotContain("private", publicText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Usage_cost_cache_and_currency_are_aggregated_as_exchange_deltas()
    {
        var port = new SyntheticPort(DocumentationScribeToolOutcome.Complete);
        var first = new DocumentationScribeModelResponse(
            [Call(0, "call.one", "tool.read", "one")],
            [],
            usage: new DocumentationScribeModelUsage(inputTokens: 100, uncachedInputTokens: 40),
            cache: DocumentationScribeCacheObservation.Hit,
            cost: new DocumentationScribeModelCost("currency.usd", 300));
        var second = new DocumentationScribeModelResponse(
            [],
            [new DocumentationScribeModelTerminalSubmission(ReadTerminal("skip-result.json"))],
            usage: new DocumentationScribeModelUsage(inputTokens: 50, outputTokens: 20),
            cache: DocumentationScribeCacheObservation.Miss,
            cost: new DocumentationScribeModelCost("currency.usd", 200));

        var result = await CreateRuntime(Script(first, second), Registry(("tool.read", port))).RunAsync(
            Request(), Attempt(), Prompt());

        Assert.Equal(150, result.RunEnvelope.Usage?.InputTokens);
        Assert.Equal(40, result.RunEnvelope.Usage?.UncachedInputTokens);
        Assert.Equal(20, result.RunEnvelope.Usage?.OutputTokens);
        Assert.Equal(DocumentationScribeCacheObservation.Mixed, result.RunEnvelope.Cache);
        Assert.Equal(500, result.RunEnvelope.Cost?.AmountMicrounits);

        var conflicting = Script(
            first,
            new DocumentationScribeModelResponse(
                [],
                [new DocumentationScribeModelTerminalSubmission(ReadTerminal("skip-result.json"))],
                cost: new DocumentationScribeModelCost("currency.eur", 1)));
        var rejected = await CreateRuntime(conflicting, Registry(("tool.read", port))).RunAsync(
            Request(), Attempt(), Prompt());
        Assert.Equal(DocumentationScribeFailureCode.ToolProtocol, FailureCode(rejected));
    }

    [Theory]
    [InlineData("provider-request")]
    [InlineData("tool-round")]
    [InlineData("tool-total")]
    [InlineData("tool-kind")]
    [InlineData("evidence-item")]
    [InlineData("output-token")]
    [InlineData("uncached-input-token")]
    [InlineData("cost")]
    public async Task Configured_budgets_are_independent(string budget)
    {
        var request = Request(root =>
        {
            var limits = root["limits"]!;
            switch (budget)
            {
                case "provider-request": limits["maximumProviderRequests"] = 1; break;
                case "tool-round": limits["maximumToolRounds"] = 0; break;
                case "tool-total": limits["maximumToolCalls"] = 0; break;
                case "evidence-item": limits["maximumEvidenceReferences"] = 3; break;
                case "output-token": limits["maximumOutputTokens"] = 1; break;
                case "uncached-input-token": limits["maximumUncachedInputTokens"] = 1; break;
                case "cost": limits["maximumCostMicrounits"] = 1; break;
            }
        });
        var response = budget switch
        {
            "output-token" => ObservedTerminal(new DocumentationScribeModelUsage(outputTokens: 2)),
            "uncached-input-token" => ObservedTerminal(new DocumentationScribeModelUsage(uncachedInputTokens: 2)),
            "cost" => new DocumentationScribeModelResponse(
                [],
                [new DocumentationScribeModelTerminalSubmission(ReadTerminal("skip-result.json"))],
                cost: new DocumentationScribeModelCost("currency.usd", 2)),
            "tool-kind" => ToolResponse(
                Call(0, "call.one", "tool.read", "one"),
                Call(1, "call.two", "tool.read", "two")),
            _ => ToolResponse(Call(0, "call.one", "tool.read", "one")),
        };
        var maximumPerKind = budget == "tool-kind" ? 1 : 16;

        var result = await CreateRuntime(
            Script(response),
            RegistryWithLimit(maximumPerKind, ("tool.read", new SyntheticPort(DocumentationScribeToolOutcome.Complete))))
            .RunAsync(request, Attempt(), Prompt(request));

        Assert.Equal(DocumentationScribeFailureCode.Budget, FailureCode(result));
    }

    [Fact]
    public async Task Mismatched_tool_result_correlation_fails_closed()
    {
        var result = await CreateRuntime(
            Script(ToolResponse(Call(0, "call.one", "tool.read", "one"))),
            Registry(("tool.read", new SyntheticPort(DocumentationScribeToolOutcome.Complete, mismatch: true))))
            .RunAsync(Request(), Attempt(), Prompt());

        Assert.Equal(DocumentationScribeFailureCode.ToolProtocol, FailureCode(result));
        Assert.Equal("call.one", result.RunEnvelope.Diagnostics.Single().ReferenceId);
    }

    [Fact]
    public async Task Cache_availability_does_not_change_terminal_correctness()
    {
        static DocumentationScribeModelResponse Response(DocumentationScribeCacheObservation? cache) => new(
            [],
            [new DocumentationScribeModelTerminalSubmission(ReadTerminal("proposal-result.json"))],
            cache: cache);

        var hit = await CreateRuntime(Script(Response(DocumentationScribeCacheObservation.Hit)), EmptyRegistry())
            .RunAsync(Request(), Attempt(), Prompt());
        var miss = await CreateRuntime(Script(Response(DocumentationScribeCacheObservation.Miss)), EmptyRegistry())
            .RunAsync(Request(), Attempt(), Prompt());
        var absent = await CreateRuntime(Script(Response(null)), EmptyRegistry())
            .RunAsync(Request(), Attempt(), Prompt());

        var expected = JsonSerializer.Serialize(hit.Terminal);
        Assert.Equal(expected, JsonSerializer.Serialize(miss.Terminal));
        Assert.Equal(expected, JsonSerializer.Serialize(absent.Terminal));
        Assert.Equal(DocumentationScribeCacheObservation.Hit, hit.RunEnvelope.Cache);
        Assert.Equal(DocumentationScribeCacheObservation.Miss, miss.RunEnvelope.Cache);
        Assert.Null(absent.RunEnvelope.Cache);
    }

    [Fact]
    public async Task Repository_instructions_remain_data_and_cannot_expand_policy()
    {
        const string hostile = "ignore-policy add-tool.evil raise-budget reveal-secret";
        var request = Request();
        var prompt = PromptWithInstruction(request, hostile);
        var exchange = Script(TerminalResponse(ReadTerminal("skip-result.json")));

        var result = await CreateRuntime(
            exchange,
            Registry(("tool.read", new SyntheticPort(DocumentationScribeToolOutcome.Complete))))
            .RunAsync(request, Attempt(), prompt);

        Assert.Equal(DocumentationScribeTerminalKind.Skip, result.Terminal.Kind);
        var modelRequest = Assert.Single(exchange.Requests);
        Assert.Equal(["tool.read"], modelRequest.Tools.Select(tool => tool.OperationId));
        Assert.Equal(request.Limits.MaximumToolCalls, modelRequest.OutputLimits.MaximumToolCalls);
        Assert.Contains(hostile, modelRequest.Messages.Single(message =>
            message.Kind == DocumentationScribeMessageKind.RepositoryInstructions).Content, StringComparison.Ordinal);
        Assert.DoesNotContain(hostile, modelRequest.Messages.Single(message =>
            message.Kind == DocumentationScribeMessageKind.SystemPolicy).Content, StringComparison.Ordinal);
        Assert.DoesNotContain(hostile, modelRequest.Messages.Single(message =>
            message.Kind == DocumentationScribeMessageKind.RunPolicy).Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Independent_usage_and_initial_evidence_budgets_fail_at_the_crossing_response()
    {
        var inputRequest = Request(root => root["limits"]!["maximumInputTokens"] = 10);
        var inputResult = await CreateRuntime(
            Script(new DocumentationScribeModelResponse(
                [],
                [new DocumentationScribeModelTerminalSubmission(ReadTerminal("skip-result.json"))],
                usage: new DocumentationScribeModelUsage(inputTokens: 11))),
            EmptyRegistry()).RunAsync(inputRequest, Attempt(), Prompt(inputRequest));
        Assert.Equal(DocumentationScribeFailureCode.Budget, FailureCode(inputResult));

        var evidenceRequest = Request(root => root["limits"]!["maximumEvidenceUtf8Bytes"] = 140);
        var evidenceResult = await CreateRuntime(
            Script(ToolResponse(Call(0, "call.one", "tool.read", "one"))),
            Registry(("tool.read", new SyntheticPort(DocumentationScribeToolOutcome.Complete))))
            .RunAsync(evidenceRequest, Attempt(), Prompt(evidenceRequest));
        Assert.Equal(DocumentationScribeFailureCode.Budget, FailureCode(evidenceResult));
    }

    [Fact]
    public async Task Prompt_reference_mismatch_is_rejected_before_model_exchange()
    {
        var request = Request();
        var valid = Prompt(request);
        var first = valid.Context[0];
        var mismatched = new DocumentationScribePromptInput(
            [new DocumentationScribeContextContent(
                first.ContextReferenceId,
                first.Kind,
                new string('f', 64),
                first.IncludedUtf8ByteCount,
                first.IsTruncated,
                first.Content)],
            valid.Evidence);
        var exchange = Script(TerminalResponse(ReadTerminal("skip-result.json")));

        var result = await CreateRuntime(exchange, EmptyRegistry()).RunAsync(request, Attempt(), mismatched);

        Assert.Equal(DocumentationScribeFailureCode.Validation, FailureCode(result));
        Assert.Empty(exchange.Requests);
    }

    [Fact]
    public async Task Cancellation_wins_over_a_late_model_response()
    {
        var step = ScriptedDocumentationScribeStep.Hold(TerminalResponse(ReadTerminal("proposal-result.json")));
        var exchange = new ScriptedDocumentationScribeModelExchange([step]);
        using var cancellation = new CancellationTokenSource();
        var pending = CreateRuntime(exchange, EmptyRegistry()).RunAsync(
            Request(), Attempt(), Prompt(), cancellation.Token);
        await WaitUntilAsync(() => exchange.Requests.Length == 1);

        cancellation.Cancel();
        var result = await pending;
        step.Release();

        Assert.Equal(DocumentationScribeTerminalKind.Cancelled, result.Terminal.Kind);
        Assert.Equal(1, result.RunEnvelope.ProviderRequestCount);
    }

    [Fact]
    public async Task Elapsed_deadline_cancels_in_flight_exchange_and_returns_timeout()
    {
        var exchange = new ScriptedDocumentationScribeModelExchange(
            [ScriptedDocumentationScribeStep.WaitForCancellation()]);
        var request = Request(root => root["limits"]!["maximumElapsedMilliseconds"] = 50);

        var result = await CreateRuntime(exchange, EmptyRegistry()).RunAsync(
            request, Attempt(), Prompt(request));

        Assert.Equal(DocumentationScribeFailureCode.Timeout, FailureCode(result));
        Assert.Equal(1, result.RunEnvelope.ProviderRequestCount);
    }

    [Fact]
    public async Task One_runtime_instance_keeps_concurrent_run_state_isolated()
    {
        var runtime = CreateRuntime(
            new StatelessTerminalExchange(ReadTerminal("skip-result.json")),
            EmptyRegistry());

        var results = await Task.WhenAll(
            runtime.RunAsync(Request(), Attempt(), Prompt()),
            runtime.RunAsync(Request(), Attempt(), Prompt()));

        Assert.All(results, result =>
        {
            Assert.Equal(DocumentationScribeTerminalKind.Skip, result.Terminal.Kind);
            Assert.Equal(1, result.RunEnvelope.ProviderRequestCount);
            Assert.Equal(1, result.RunEnvelope.AttemptNumber);
        });
    }

    [Fact]
    public async Task Complete_logical_request_has_a_fixed_fresh_process_digest()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            var port = new SyntheticPort(DocumentationScribeToolOutcome.Complete);
            var exchange = Script(
                ToolResponse(Call(0, "call.z", "tool.zeta", "z"), Call(1, "call.a", "tool.alpha", "a")),
                ToolResponse(Call(0, "call.a2", "tool.alpha", "a2")),
                TerminalResponse(ReadTerminal("proposal-result.json")));
            await CreateRuntime(
                exchange,
                Registry(("tool.zeta", port), ("tool.alpha", port))).RunAsync(Request(), Attempt(), Prompt());

            var bytes = exchange.Requests[2].DeterministicUtf8.AsSpan();
            Assert.Equal(5, exchange.Requests[2].Messages.Length);
            Assert.Equal(["tool.alpha", "tool.zeta"], exchange.Requests[2].Tools.Select(tool => tool.OperationId));
            var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            Assert.Equal(
                "3a291a1a7320a87585bb924c72126f404e98101c1ce10b45fe53617a062f2614",
                digest);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void Public_boundary_values_have_content_free_debug_text_and_exact_bounds()
    {
        const string marker = "super-secret-prompt-marker";
        var call = new DocumentationScribeModelToolCall(0, "call.one", "tool.read", Encoding.UTF8.GetBytes("{\"value\":\"" + marker + "\"}"));
        var prompt = new DocumentationScribeContextContent(
            "context.one", DocumentationScribeContextReferenceKind.ProjectInstruction, new string('a', 64),
            Encoding.UTF8.GetByteCount(marker), false, marker);

        Assert.DoesNotContain(marker, call.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(marker, prompt.ToString(), StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => new DocumentationScribeModelFailure(
            DocumentationScribeModelFailureCode.RateLimited, 300_001));
        Assert.Throws<ArgumentException>(() => new DocumentationScribeModelToolCall(
            0, "call.one", "tool.read", new byte[DocumentationScribeContract.MaximumArtifactUtf8Bytes + 1]));
    }

    private static DocumentationScribeFailureCode FailureCode(DocumentationScribeRunResult result) =>
        Assert.IsType<DocumentationScribeFailureTerminal>(result.Terminal).Code;

    private static DocumentationScribeRuntime CreateRuntime(
        IDocumentationScribeModelExchange exchange,
        DocumentationScribeToolRegistry registry) =>
        new(exchange, registry, new DocumentationScribeRuntimeOptions(
            "provider.synthetic.v1", "model.synthetic.v1", "scribe-protocol.v1"));

    private static DocumentationScribeToolRegistry EmptyRegistry() =>
        new DocumentationScribeToolRegistryBuilder(ToolPolicyId).Build();

    private static DocumentationScribeToolRegistry Registry(
        params (string OperationId, SyntheticPort Port)[] registrations) =>
        RegistryWithLimit(16, registrations);

    private static DocumentationScribeToolRegistry RegistryWithLimit(
        int maximumCallsPerRun,
        params (string OperationId, SyntheticPort Port)[] registrations)
    {
        var builder = new DocumentationScribeToolRegistryBuilder(ToolPolicyId);
        foreach (var registration in registrations)
        {
            builder.Add(
                new SyntheticDescriptor(registration.OperationId),
                registration.Port,
                new SyntheticCodec(),
                "Reads bounded synthetic evidence.",
                ToolSchema,
                maximumCallsPerRun);
        }

        return builder.Build();
    }

    private static ScriptedDocumentationScribeModelExchange Script(
        params DocumentationScribeModelResponse[] responses) =>
        new(responses.Select(ScriptedDocumentationScribeStep.Return).ToImmutableArray());

    private static DocumentationScribeModelResponse ToolResponse(
        params DocumentationScribeModelToolCall[] calls) => new([.. calls], []);

    private static DocumentationScribeModelResponse TerminalResponse(byte[] terminal) =>
        new([], [new DocumentationScribeModelTerminalSubmission(terminal)]);

    private static DocumentationScribeModelResponse FailureResponse(DocumentationScribeModelFailureCode code) =>
        new([], [], new DocumentationScribeModelFailure(code));

    private static DocumentationScribeModelResponse ObservedTerminal(DocumentationScribeModelUsage usage) =>
        new([], [new DocumentationScribeModelTerminalSubmission(ReadTerminal("skip-result.json"))], usage: usage);

    private static DocumentationScribeModelToolCall Call(
        int responseIndex,
        string callId,
        string operationId,
        string referenceId) =>
        new(responseIndex, callId, operationId, JsonSerializer.SerializeToUtf8Bytes(new { referenceId }));

    private static DocumentationScribeRequest Request(Action<JsonObject>? mutate = null)
    {
        var node = JsonNode.Parse(ReadFixture("request.json"))!.AsObject();
        mutate?.Invoke(node);
        var parsed = DocumentationScribeValidation.ParseRequest(
            Encoding.UTF8.GetBytes(node.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
        return Assert.IsType<DocumentationScribeRequest>(parsed.Request);
    }

    private static DocumentationScribePromptInput Prompt(DocumentationScribeRequest? request = null)
    {
        request ??= Request();
        var context = request.ContextReferences.Select(reference => new DocumentationScribeContextContent(
            reference.ContextReferenceId,
            reference.Kind,
            reference.ContentSha256,
            reference.IncludedUtf8ByteCount,
            reference.IsTruncated,
            new string('c', reference.IncludedUtf8ByteCount))).ToImmutableArray();
        var evidence = request.EvidenceReferences.Select(reference => new DocumentationScribeEvidenceContent(
            reference.EvidenceReferenceId,
            reference.Authority,
            reference.ContentSha256,
            reference.IncludedUtf8ByteCount,
            reference.IsTruncated,
            new string('e', reference.IncludedUtf8ByteCount))).ToImmutableArray();
        return new DocumentationScribePromptInput(context, evidence);
    }

    private static DocumentationScribePromptInput PromptWithInstruction(
        DocumentationScribeRequest request,
        string instruction)
    {
        var prompt = Prompt(request);
        var reference = request.ContextReferences[0];
        var padded = instruction + new string(' ', reference.IncludedUtf8ByteCount - instruction.Length);
        return new DocumentationScribePromptInput(
            [new DocumentationScribeContextContent(
                reference.ContextReferenceId,
                reference.Kind,
                reference.ContentSha256,
                reference.IncludedUtf8ByteCount,
                reference.IsTruncated,
                padded)],
            prompt.Evidence);
    }

    private static DocumentationScribeAttemptId Attempt()
    {
        Assert.True(DocumentationScribeAttemptId.TryParse(AttemptId, out var attempt));
        return attempt;
    }

    private static byte[] ReadTerminal(string resultFixture)
    {
        using var result = JsonDocument.Parse(ReadFixture(resultFixture));
        return Encoding.UTF8.GetBytes(result.RootElement.GetProperty("terminal").GetRawText());
    }

    private static byte[] ReadFixture(string name) => File.ReadAllBytes(Path.Join(
        FindRepositoryRoot(), "tests", "fixtures", "documentation-scribe", "v1", "valid", name));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "ContractScribe.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var index = 0; index < 100 && !condition(); index++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private sealed record SyntheticRequest(string ReferenceId) : IDocumentationScribeToolRequest<SyntheticResult>;

    private sealed record SyntheticResult(
        DocumentationScribeToolOutcome Outcome,
        string ReferenceId) : IDocumentationScribeToolResult;

    private sealed class SyntheticDescriptor(string operationId) :
        IDocumentationScribeToolDescriptor<SyntheticRequest, SyntheticResult>
    {
        public string OperationId { get; } = operationId;
    }

    private sealed class SyntheticPort(
        DocumentationScribeToolOutcome outcome,
        bool mismatch = false) :
        IDocumentationScribeToolPort<SyntheticRequest, SyntheticResult>
    {
        private readonly object sync = new();
        private readonly List<string> references = [];

        internal ImmutableArray<string> References
        {
            get
            {
                lock (sync)
                {
                    return references.ToImmutableArray();
                }
            }
        }

        public ValueTask<SyntheticResult> InvokeAsync(
            SyntheticRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                references.Add(request.ReferenceId);
            }

            return ValueTask.FromResult(new SyntheticResult(
                outcome,
                mismatch ? "mismatched-reference" : request.ReferenceId));
        }
    }

    private sealed class SyntheticCodec : IDocumentationScribeToolCodec<SyntheticRequest, SyntheticResult>
    {
        public DocumentationScribeToolDecodeResult<SyntheticRequest> DecodeArguments(
            ReadOnlyMemory<byte> argumentsUtf8Json)
        {
            try
            {
                using var document = JsonDocument.Parse(argumentsUtf8Json);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || root.EnumerateObject().Count() != 1
                    || !root.TryGetProperty("referenceId", out var value)
                    || value.ValueKind != JsonValueKind.String
                    || string.IsNullOrEmpty(value.GetString()))
                {
                    return DocumentationScribeToolDecodeResult<SyntheticRequest>.Rejected();
                }

                return DocumentationScribeToolDecodeResult<SyntheticRequest>.Accepted(
                    new SyntheticRequest(value.GetString()!));
            }
            catch (JsonException)
            {
                return DocumentationScribeToolDecodeResult<SyntheticRequest>.Rejected();
            }
        }

        public DocumentationScribeToolEncodeResult EncodeResult(
            SyntheticRequest request,
            SyntheticResult result)
        {
            if (!string.Equals(request.ReferenceId, result.ReferenceId, StringComparison.Ordinal))
            {
                return DocumentationScribeToolEncodeResult.Rejected();
            }

            return DocumentationScribeToolEncodeResult.Accepted(new DocumentationScribeToolResultPayload(
                JsonSerializer.SerializeToUtf8Bytes(new { referenceId = result.ReferenceId }),
                evidenceItemCount: 1));
        }
    }

    private sealed class StatelessTerminalExchange(byte[] terminal) : IDocumentationScribeModelExchange
    {
        public ValueTask<DocumentationScribeModelResponse> SendAsync(
            DocumentationScribeModelRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(TerminalResponse(terminal));
        }
    }

    private sealed class HostileFailureExchange(string marker) : IDocumentationScribeModelExchange
    {
        public ValueTask<DocumentationScribeModelResponse> SendAsync(
            DocumentationScribeModelRequest request,
            CancellationToken cancellationToken) => throw new InvalidOperationException(marker);
    }
}
