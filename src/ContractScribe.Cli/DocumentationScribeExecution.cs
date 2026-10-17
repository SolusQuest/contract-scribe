using ContractScribe.Agent.Prompting;
using ContractScribe.Agent.Runtime;
using ContractScribe.Core;
using SolusAgent.Api.Candidates;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Startup;

namespace ContractScribe.Cli;

internal static class DocumentationScribeExecution
{
    internal static DocumentationScribeSession Prepare(DocumentationScribeRequest request, DocumentationScribeAttemptId attempt,
        DocumentationScribePromptInput prompt, DocumentationScribeToolRegistry registry, IDocumentationScribeModelExchange exchange,
        DocumentationScribeRuntimeOptions options, TimeProvider clock, CancellationToken token = default,
        DocumentationScribeExecutionScope? scope = null) => new(request, attempt, prompt, registry, exchange, options, clock, token, scope);

    internal static Task<DocumentationScribeRunResult> RunAsync(DocumentationScribeSession session, CancellationToken token = default)
    {
        if (session.Result is { } early) return Task.FromResult(early);
        var runtime = (ICandidateAgent)RuntimeAgentFactory.Create(session.Configuration!,
            new RuntimeOptions(session.State.Scope.Allowance.Clock, maximumAttempts: 128, maximumRecords: 2048,
                maximumRetainedBytes: RuntimeOptions.MaximumRetainedCapacityBytes));
        return session.RunAsync(runtime, token);
    }

    internal static Task<DocumentationScribeRunResult> RunAsync(DocumentationScribeRequest request, DocumentationScribeAttemptId attempt,
        DocumentationScribePromptInput prompt, DocumentationScribeToolRegistry registry, IDocumentationScribeModelExchange exchange,
        DocumentationScribeRuntimeOptions options, TimeProvider clock, CancellationToken token = default,
        DocumentationScribeExecutionScope? scope = null) =>
        RunAsync(Prepare(request, attempt, prompt, registry, exchange, options, clock, token, scope), token);
}
