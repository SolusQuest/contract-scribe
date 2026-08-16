using ContractScribe.Core;

namespace ContractScribe.Roslyn;

internal sealed class DocumentationScribeRepositoryListFilesTool(
    DocumentationScribeRepositoryToolSession session)
    : IDocumentationScribeToolPort<DocumentationScribeRepositoryListFilesRequest, DocumentationScribeRepositoryListFilesResult>
{
    public ValueTask<DocumentationScribeRepositoryListFilesResult> InvokeAsync(
        DocumentationScribeRepositoryListFilesRequest request,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(session.ListFiles(request, cancellationToken));
}
