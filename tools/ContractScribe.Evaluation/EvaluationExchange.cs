using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Agent.Runtime;
using ContractScribe.Core;
using ContractScribe.Roslyn;

namespace ContractScribe.Evaluation;

internal sealed class ScriptedEvaluationExchange : IDocumentationScribeModelExchange
{
    private readonly PreparedEvaluationCase prepared;

    internal ScriptedEvaluationExchange(PreparedEvaluationCase prepared) =>
        this.prepared = prepared ?? throw new ArgumentNullException(nameof(prepared));

    public ValueTask<DocumentationScribeModelResponse> SendAsync(
        DocumentationScribeModelRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(prepared.Scenario.Script switch
        {
            "tool-proposal" => ToolProposal(request),
            "proposal" => Proposal(),
            "skip" => Skip(),
            "invalid-tool" => InvalidTool(),
            "malformed-output" => MalformedOutput(),
            "rate-limited" => Failure(DocumentationScribeModelFailureCode.RateLimited),
            "unavailable" => Failure(DocumentationScribeModelFailureCode.PermanentUnavailable),
            "budget-exhausted" => BudgetExhausted(),
            _ => throw new InvalidOperationException("evaluation.script.unknown"),
        });
    }

    private DocumentationScribeModelResponse ToolProposal(DocumentationScribeModelRequest request)
    {
        if (request.ProviderRequestNumber == 1)
        {
            return new DocumentationScribeModelResponse(
                [
                    new DocumentationScribeModelToolCall(
                        0,
                        "call.evaluation-search",
                        DocumentationScribeRepositoryToolOperationIds.SearchText,
                        JsonSerializer.SerializeToUtf8Bytes(new
                        {
                            scopeId = "evidence.source",
                            literal = "public void Run",
                            pageSize = 1,
                        })),
                ],
                [],
                usage: new DocumentationScribeModelUsage(
                    inputTokens: 100,
                    outputTokens: 20,
                    cachedInputTokens: 40,
                    uncachedInputTokens: 60),
                cache: DocumentationScribeCacheObservation.Mixed);
        }

        if (request.ProviderRequestNumber != 2 || request.CompletedToolExchanges.Length != 1)
        {
            return Failure(DocumentationScribeModelFailureCode.MalformedResponse);
        }

        return new DocumentationScribeModelResponse(
            [],
            [new DocumentationScribeModelTerminalSubmission(ProposalTerminal())],
            usage: new DocumentationScribeModelUsage(
                inputTokens: 120,
                outputTokens: 40,
                cachedInputTokens: 40,
                uncachedInputTokens: 80),
            cache: DocumentationScribeCacheObservation.Mixed);
    }

    private DocumentationScribeModelResponse Proposal() => new(
        [],
        [new DocumentationScribeModelTerminalSubmission(ProposalTerminal())],
        usage: new DocumentationScribeModelUsage(
            inputTokens: 80,
            outputTokens: 24,
            uncachedInputTokens: 80),
        cache: DocumentationScribeCacheObservation.Miss);

    private static DocumentationScribeModelResponse Skip() => new(
        [],
        [new DocumentationScribeModelTerminalSubmission(Encoding.UTF8.GetBytes(
            "{\"kind\":\"skip\",\"reason\":\"scribe.skip.insufficient-evidence\",\"evidenceReferenceIds\":[]}"))],
        usage: new DocumentationScribeModelUsage(inputTokens: 64, outputTokens: 8));

    private static DocumentationScribeModelResponse InvalidTool() => new(
        [
            new DocumentationScribeModelToolCall(
                0,
                "call.invalid-tool",
                "tool.unsupported",
                Encoding.UTF8.GetBytes("{}")),
        ],
        []);

    private static DocumentationScribeModelResponse MalformedOutput() => new(
        [],
        [
            new DocumentationScribeModelTerminalSubmission(Encoding.UTF8.GetBytes(
                "{\"kind\":\"skip\",\"reason\":\"scribe.skip.insufficient-evidence\",\"evidenceReferenceIds\":[],\"rawResponse\":\"forbidden\"}")),
        ]);

    private static DocumentationScribeModelResponse Failure(DocumentationScribeModelFailureCode code) =>
        new([], [], new DocumentationScribeModelFailure(code));

    private static DocumentationScribeModelResponse BudgetExhausted()
    {
        var calls = ImmutableArray.CreateBuilder<DocumentationScribeModelToolCall>(17);
        for (var index = 0; index < 17; index++)
        {
            calls.Add(new DocumentationScribeModelToolCall(
                index,
                $"call.budget-{index:D2}",
                DocumentationScribeRepositoryToolOperationIds.SearchText,
                JsonSerializer.SerializeToUtf8Bytes(new
                {
                    scopeId = "evidence.source",
                    literal = "public",
                    pageSize = 1,
                })));
        }

        return new DocumentationScribeModelResponse(calls.ToImmutable(), []);
    }

    private ReadOnlyMemory<byte> ProposalTerminal()
    {
        var request = prepared.Request;
        var locator = (RepositoryEvidenceLocator)request.Target.SourceLocator;
        return JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["kind"] = "proposal",
            ["target"] = new JsonObject
            {
                ["repositoryContextRef"] = request.Context.RepositoryContextRef.Value,
                ["symbolRef"] = new JsonObject
                {
                    ["compilationContextRef"] = request.Target.SymbolRef.CompilationContextRef,
                    ["documentationCommentId"] = request.Target.SymbolRef.DocumentationCommentId,
                },
                ["sourceCommitment"] = new JsonObject
                {
                    ["locator"] = new JsonObject
                    {
                        ["repository"] = new JsonObject
                        {
                            ["path"] = locator.Path,
                            ["span"] = new JsonObject
                            {
                                ["start"] = locator.Span!.Value.Start,
                                ["end"] = locator.Span.Value.End,
                            },
                        },
                    },
                    ["contentSha256"] = request.Target.SourceSha256,
                },
            },
            ["contentUnits"] = new JsonArray
            {
                new JsonObject
                {
                    ["kind"] = "content.summary",
                    ["lines"] = new JsonArray(
                        prepared.Scenario.ProposalLine ?? "Runs the selected operation."),
                    ["claimCategoryId"] = "claim.purpose",
                    ["evidenceReferenceIds"] = new JsonArray("evidence.source"),
                },
            },
        });
    }

    public override string ToString() => nameof(ScriptedEvaluationExchange);
}
