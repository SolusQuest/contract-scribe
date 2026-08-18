using ContractScribe.Core;

namespace ContractScribe.Roslyn;

internal sealed class DocumentationScribeRepositoryReadExcerptTool(
    DocumentationScribeRepositoryToolSession session)
    : IDocumentationScribeToolPort<DocumentationScribeRepositoryReadExcerptRequest, DocumentationScribeRepositoryReadExcerptResult>
{
    public ValueTask<DocumentationScribeRepositoryReadExcerptResult> InvokeAsync(
        DocumentationScribeRepositoryReadExcerptRequest request,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(session.ReadExcerpt(request, cancellationToken));
}
