using ContractScribe.Agent.Runtime;
using ContractScribe.Core;

namespace ContractScribe.Tests;

public sealed partial class DocumentationScribeRuntimeTests
{
    [Theory]
    [InlineData("duplicate", DocumentationScribeFailureCode.ToolProtocol)]
    [InlineData("invalid", DocumentationScribeFailureCode.ToolProtocol)]
    [InlineData("null-locator", DocumentationScribeFailureCode.ToolProtocol)]
    [InlineData("truncated", DocumentationScribeFailureCode.ToolProtocol)]
    [InlineData("unavailable", DocumentationScribeFailureCode.ToolProtocol)]
    [InlineData("invalid-second", DocumentationScribeFailureCode.ToolProtocol)]
    [InlineData("failure", DocumentationScribeFailureCode.ToolProtocol)]
    [InlineData("timeout", DocumentationScribeFailureCode.Timeout)]
    public async Task Solus_runtime_preserves_typed_evidence_rejection_and_batch_publication(
        string scenario, DocumentationScribeFailureCode expected)
    {
        // Reuse the existing product-runtime port/codec fault vectors, not a candidate-host stub.
        var second = scenario is "invalid-second" or "failure" or "timeout";
        var response = second ? ToolResponse(Call(0, "call.one", "tool.read", "valid"),
            Call(1, "call.two", "tool.read", scenario == "invalid-second" ? "invalid" : scenario))
            : ToolResponse(Call(0, "call.one", "tool.read", "invalid"));
        DocumentationScribeToolRegistry RegistryForCase() => RegistryWithCodec(
            scenario is "failure" or "timeout" ? new OutcomeByReferencePort()
                : new SyntheticPort(scenario == "unavailable" ? DocumentationScribeToolOutcome.Unavailable : DocumentationScribeToolOutcome.Complete),
            new SyntheticCodec(duplicateDynamicEvidence: scenario == "duplicate", unavailableHasDynamicEvidence: scenario == "unavailable",
                truncatedCompleteEvidence: scenario == "truncated", invalidDynamicReferenceId: scenario is "invalid" or "invalid-second" ? "invalid" : null,
                nullRepositoryLocatorPath: scenario == "null-locator"));
        var request = Request();
        var terminal = SubmitResponse(ReadTerminal("skip-result.json"), request);
        var exchange = Script(response, terminal);
        var session = EvidenceQualification(request, RegistryForCase(), exchange);
        var result = await session.RunAsync();
        Assert.Equal(expected, FailureCode(result));
        Assert.Empty(result.DynamicEvidenceReferences);
        Assert.Empty(session.State.ActiveDynamicEvidenceReferences);
        Assert.Empty(session.State.CompletedToolExchanges);
        Assert.Equal(second ? 2 : 1, result.RunEnvelope.ToolCallCount);
        Assert.Single(exchange.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Solus_runtime_publishes_valid_evidence_once_and_reuses_its_charge_after_retry(bool retry)
    {
        var request = Request();
        var tool = ToolResponse(Call(0, "call.one", "tool.read", "valid"));
        var terminal = SubmitResponse(ReadTerminal("skip-result.json"), request);
        var exchange = retry ? Script(tool, FailureResponse(DocumentationScribeModelFailureCode.TransientUnavailable), tool, terminal, FinishResponse(request))
            : Script(tool, terminal, FinishResponse(request));
        var session = EvidenceQualification(request,
            RegistryWithCodec(new SyntheticPort(DocumentationScribeToolOutcome.Complete), new SyntheticCodec()), exchange);
        var result = await session.RunAsync();
        Assert.Equal(DocumentationScribeTerminalKind.Skip, Assert.Single(result.Submissions).Result.Kind);
        Assert.Empty(result.DynamicEvidenceReferences); // The accepted skip cites no dynamic evidence.
        Assert.Single(session.State.ChargedDynamicEvidenceById);
        Assert.Single(session.State.ActiveDynamicEvidenceReferences);
        Assert.Single(session.State.CompletedToolExchanges.Where(item => item.OperationId != DocumentationScribeBoundary.TerminalOperationId));
        Assert.Single(session.State.CompletedToolExchanges.Where(item => item.OperationId == DocumentationScribeBoundary.TerminalOperationId));
        if (retry) Assert.Empty(exchange.Requests[2].CompletedToolExchanges);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Solus_runtime_preserves_original_evidence_reuse_and_metadata_conflict(bool exact)
    {
        Assert.True(DocumentationScribeValidation.TryCreateDynamicEvidenceReference(Request(),
            SyntheticDynamicEvidenceInput(Sha256("e")), out var derived));
        var request = Request(root => ReplaceSummaryEvidenceWithDerivedId(root, derived!.EvidenceReferenceId, exactMetadata: exact));
        var session = EvidenceQualification(request,
            RegistryWithCodec(new SyntheticPort(DocumentationScribeToolOutcome.Complete), new SyntheticCodec(referenceSpecificDynamicEvidence: true)),
            new DynamicCitationExchange("proposal-result.json", "e"));
        var result = await session.RunAsync();
        if (exact)
        {
            Assert.Equal(DocumentationScribeTerminalKind.Proposal, Assert.Single(result.Submissions).Result.Kind);
            Assert.Empty(result.DynamicEvidenceReferences);
            Assert.Empty(session.State.ChargedDynamicEvidenceById);
        }
        else Assert.Equal(DocumentationScribeFailureCode.ToolProtocol, FailureCode(result));
    }

    [Theory]
    [InlineData(false, 3, DocumentationScribeFailureCode.Budget)]
    [InlineData(true, 3, DocumentationScribeFailureCode.Budget)]
    [InlineData(true, 1, DocumentationScribeFailureCode.Provider)]
    public async Task Solus_runtime_preserves_request_budget_and_tied_retry_failure_priority(
        bool transient, int attempts, DocumentationScribeFailureCode expected)
    {
        var request = Request(root =>
        {
            root["limits"]!["maximumProviderRequests"] = 1;
            root["limits"]!["maximumAttempts"] = attempts;
        });
        var response = transient ? FailureResponse(DocumentationScribeModelFailureCode.TransientUnavailable)
            : ToolResponse(Call(0, "call.one", "tool.read", "valid"));
        DocumentationScribeToolRegistry RegistryForCase() => transient ? EmptyRegistry()
            : RegistryWithCodec(new SyntheticPort(DocumentationScribeToolOutcome.Complete), new SyntheticCodec(suppressDynamicEvidence: true));
        var exchange = Script(response);
        var result = await EvidenceQualification(request, RegistryForCase(), exchange).RunAsync();
        Assert.Equal(expected, FailureCode(result));
        Assert.Equal(1, result.RunEnvelope.ProviderRequestCount);
        Assert.Equal(1, result.RunEnvelope.AttemptNumber);
        Assert.Single(exchange.Requests);
        Assert.Equal(expected == DocumentationScribeFailureCode.Provider ? DocumentationScribeProviderFinalDisposition.Retryable : null,
            Assert.IsType<DocumentationScribeFailureTerminal>(result.Stop).ProviderFinalDisposition);
    }

    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 3)]
    [InlineData(true, 1)]
    public async Task Solus_runtime_does_not_advance_or_clear_history_when_no_retry_can_start(bool transient, int attempts)
    {
        var request = Request(root =>
        {
            root["limits"]!["maximumProviderRequests"] = 2;
            root["limits"]!["maximumAttempts"] = attempts;
        });
        var tool = ToolResponse(Call(0, "call.one", "tool.read", "valid"));
        var failure = FailureResponse(transient ? DocumentationScribeModelFailureCode.TransientUnavailable
            : DocumentationScribeModelFailureCode.PermanentUnavailable);
        DocumentationScribeToolRegistry RegistryForCase() =>
            RegistryWithCodec(new SyntheticPort(DocumentationScribeToolOutcome.Complete), new SyntheticCodec());
        var exchange = Script(tool, failure);
        var session = EvidenceQualification(request, RegistryForCase(), exchange);
        var result = await session.RunAsync();
        Assert.Equal(transient && attempts > 1 ? DocumentationScribeFailureCode.Budget : DocumentationScribeFailureCode.Provider, FailureCode(result));
        Assert.Single(session.State.CompletedToolExchanges);
        Assert.Single(session.State.ActiveDynamicEvidenceReferences);
        Assert.Equal(1, result.RunEnvelope.AttemptNumber);
        Assert.Equal(2, result.RunEnvelope.ProviderRequestCount);
        Assert.Equal(2, exchange.Requests.Length);
        Assert.Empty(result.DynamicEvidenceReferences);
    }

    private static ScribeSessionTestDriver EvidenceQualification(DocumentationScribeRequest request,
        DocumentationScribeToolRegistry registry, IDocumentationScribeModelExchange exchange) =>
        new(request, Attempt(), Prompt(request), registry, exchange, DocumentationScribeExecutionScope.ForStandalone(request));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Solus_runtime_preserves_expired_start_and_cancellation_priority_without_effects(bool cancelled)
    {
        var request = Request(root => root["limits"]!["maximumElapsedMilliseconds"] = 10);
        var clock = new ManualTimeProvider();
        var exchange = Script(SubmitResponse(ReadTerminal("skip-result.json"), request), FinishResponse(request));
        var session = new ScribeSessionTestDriver(request, Attempt(), Prompt(request), EmptyRegistry(), exchange,
            DocumentationScribeExecutionScope.ForStandalone(request, clock));
        clock.AdvanceMilliseconds(10);
        using var cancellation = new CancellationTokenSource();
        if (cancelled) cancellation.Cancel();
        var result = await session.RunAsync(cancellation.Token);
        if (cancelled) Assert.Equal(DocumentationScribeTerminalKind.Cancelled, result.Stop.Kind);
        else Assert.Equal(DocumentationScribeFailureCode.Timeout, FailureCode(result));
        Assert.Empty(exchange.Requests);
        Assert.Equal(0, result.RunEnvelope.ProviderRequestCount);
    }
}
