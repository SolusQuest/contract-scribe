using ContractScribe.Core;

namespace ContractScribe.Roslyn;

internal sealed class DocumentationScribeRepositorySearchTextTool(
    DocumentationScribeRepositoryToolSession session)
    : IDocumentationScribeToolPort<DocumentationScribeRepositorySearchTextRequest, DocumentationScribeRepositorySearchTextResult>
{
    public ValueTask<DocumentationScribeRepositorySearchTextResult> InvokeAsync(
        DocumentationScribeRepositorySearchTextRequest request,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(session.SearchText(request, cancellationToken));
}
