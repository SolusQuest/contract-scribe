using ContractScribe.Agent.Prompting;
using ContractScribe.Agent.Runtime;
using ContractScribe.Cli;
using ContractScribe.Core;
using SolusAgent.Api.Candidates;

namespace ContractScribe.Tests;

// Records the real exchange and observes the actual SDK task; it owns no adapter or execution loop.
internal sealed class ScribeSessionTestDriver
{
    private readonly DocumentationScribeSession session;
    private readonly RecordingExchange exchange;
    internal ScribeSessionTestDriver(DocumentationScribeRequest request, DocumentationScribeAttemptId attempt,
        DocumentationScribePromptInput prompt, DocumentationScribeToolRegistry registry,
        IDocumentationScribeModelExchange exchange, DocumentationScribeExecutionScope scope)
    {
        this.exchange = new(exchange);
        session = DocumentationScribeExecution.Prepare(request, attempt, prompt, registry, this.exchange,
            new("provider.synthetic.v1", "model.synthetic.v1", "scribe-protocol.v1"), scope.Allowance.Clock, scope: scope);
    }
    internal RunState State => session.State;
    internal IReadOnlyList<DocumentationScribeModelRequest> Requests => exchange.Requests;
    internal Task<CandidateExecutionResult>? RuntimeTask => session.RuntimeTask;
    internal CandidateExecutionResult? RuntimeResult => RuntimeTask?.IsCompletedSuccessfully == true ? RuntimeTask.Result : null;
    internal Task ProviderCompletion => session.ProviderCompletion;
    internal Task HostCompletion => session.HostCompletion;
    internal Task<DocumentationScribeRunResult> RunAsync(CancellationToken token = default) =>
        DocumentationScribeExecution.RunAsync(session, token);

    private sealed class RecordingExchange(IDocumentationScribeModelExchange inner) : IDocumentationScribeModelExchange
    {
        internal List<DocumentationScribeModelRequest> Requests { get; } = [];
        public ValueTask<DocumentationScribeModelResponse> SendAsync(DocumentationScribeModelRequest request, CancellationToken token) =>
            inner.SendAsync(request, token);
        public ValueTask<DocumentationScribeModelResponse> SendAsync(DocumentationScribeModelRequest request,
            DocumentationScribeExecutionScope scope, CancellationToken token)
        {
            Requests.Add(request);
            return inner.SendAsync(request, scope, token);
        }
    }
}
