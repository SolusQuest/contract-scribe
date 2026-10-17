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
    public async Task Solus_qualification_preserves_typed_evidence_rejection_and_batch_publication(
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
        var terminal = TerminalResponse(ReadTerminal("skip-result.json"));
        var legacy = await CreateRuntime(Script(response, terminal), RegistryForCase()).RunAsync(request, Attempt(), Prompt(request));
        Assert.Equal(expected, FailureCode(legacy));
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
    public async Task Solus_qualification_publishes_valid_evidence_once_and_reuses_its_charge_after_retry(bool retry)
    {
        var request = Request();
        var tool = ToolResponse(Call(0, "call.one", "tool.read", "valid"));
        var terminal = TerminalResponse(ReadTerminal("skip-result.json"));
        var exchange = retry ? Script(tool, FailureResponse(DocumentationScribeModelFailureCode.TransientUnavailable), tool, terminal)
            : Script(tool, terminal);
        var session = EvidenceQualification(request,
            RegistryWithCodec(new SyntheticPort(DocumentationScribeToolOutcome.Complete), new SyntheticCodec()), exchange);
        var result = await session.RunAsync();
        Assert.Equal(DocumentationScribeTerminalKind.Skip, result.Terminal.Kind);
        Assert.Single(result.DynamicEvidenceReferences);
        Assert.Single(session.State.ChargedDynamicEvidenceById);
        Assert.Single(session.State.ActiveDynamicEvidenceReferences);
        Assert.Single(session.State.CompletedToolExchanges);
        if (retry) Assert.Empty(exchange.Requests[2].CompletedToolExchanges);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Solus_qualification_preserves_original_evidence_reuse_and_metadata_conflict(bool exact)
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
            Assert.Equal(DocumentationScribeTerminalKind.Proposal, result.Terminal.Kind);
            Assert.Empty(result.DynamicEvidenceReferences);
            Assert.Empty(session.State.ChargedDynamicEvidenceById);
        }
        else Assert.Equal(DocumentationScribeFailureCode.ToolProtocol, FailureCode(result));
    }

    [Theory]
    [InlineData(false, 3, DocumentationScribeFailureCode.Budget)]
    [InlineData(true, 3, DocumentationScribeFailureCode.Budget)]
    [InlineData(true, 1, DocumentationScribeFailureCode.Provider)]
    public async Task Solus_qualification_preserves_request_budget_and_tied_retry_failure_priority(
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
        var legacy = await CreateRuntime(Script(response), RegistryForCase()).RunAsync(request, Attempt(), Prompt(request));
        Assert.Equal(expected, FailureCode(legacy));
        var exchange = Script(response);
        var result = await EvidenceQualification(request, RegistryForCase(), exchange).RunAsync();
        Assert.Equal(expected, FailureCode(result));
        Assert.Equal(1, result.RunEnvelope.ProviderRequestCount);
        Assert.Single(exchange.Requests);
        Assert.Equal(Assert.IsType<DocumentationScribeFailureTerminal>(legacy.Terminal).ProviderFinalDisposition,
            Assert.IsType<DocumentationScribeFailureTerminal>(result.Terminal).ProviderFinalDisposition);
    }

    private static SolusScribeQualificationSession EvidenceQualification(DocumentationScribeRequest request,
        DocumentationScribeToolRegistry registry, IDocumentationScribeModelExchange exchange) =>
        new(request, Attempt(), Prompt(request), registry, exchange, DocumentationScribeExecutionScope.ForStandalone(request));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Solus_qualification_preserves_expired_start_and_cancellation_priority_without_effects(bool cancelled)
    {
        var request = Request(root => root["limits"]!["maximumElapsedMilliseconds"] = 10);
        var clock = new ManualTimeProvider();
        var exchange = Script(TerminalResponse(ReadTerminal("skip-result.json")));
        var session = new SolusScribeQualificationSession(request, Attempt(), Prompt(request), EmptyRegistry(), exchange,
            DocumentationScribeExecutionScope.ForStandalone(request, clock));
        clock.AdvanceMilliseconds(10);
        using var cancellation = new CancellationTokenSource();
        if (cancelled) cancellation.Cancel();
        var result = await session.RunAsync(cancellation.Token);
        if (cancelled) Assert.Equal(DocumentationScribeTerminalKind.Cancelled, result.Terminal.Kind);
        else Assert.Equal(DocumentationScribeFailureCode.Timeout, FailureCode(result));
        Assert.Empty(exchange.Requests);
        Assert.Equal(0, result.RunEnvelope.ProviderRequestCount);
    }
}
