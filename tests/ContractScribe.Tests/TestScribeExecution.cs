using ContractScribe.Agent.Prompting;
using ContractScribe.Agent.Runtime;
using ContractScribe.Cli;
using ContractScribe.Core;

namespace ContractScribe.Tests;

// Test syntax only: all execution and concrete SDK construction use the production CLI owner.
internal sealed class TestScribeExecution(IDocumentationScribeModelExchange exchange,
    DocumentationScribeToolRegistry registry, DocumentationScribeRuntimeOptions options, TimeProvider? clock = null)
{
    internal Task<DocumentationScribeRunResult> RunAsync(DocumentationScribeRequest request, DocumentationScribeAttemptId attempt,
        DocumentationScribePromptInput prompt, CancellationToken cancellationToken = default,
        DocumentationScribeExecutionScope? executionScope = null) =>
        DocumentationScribeExecution.RunAsync(request, attempt, prompt, registry, exchange, options,
            clock ?? TimeProvider.System, cancellationToken, executionScope);
}
