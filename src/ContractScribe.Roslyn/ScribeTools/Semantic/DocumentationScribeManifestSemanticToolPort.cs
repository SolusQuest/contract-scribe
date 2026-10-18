using System.Collections.Immutable;
using ContractScribe.Core;

namespace ContractScribe.Roslyn;

public sealed class DocumentationScribeManifestSemanticToolPort
    : IDocumentationScribeToolPort<DocumentationScribeSemanticToolRequest, DocumentationScribeSemanticToolResult>
{
    private readonly DocumentationScribeLoadedManifestContext manifest;
    private readonly ImmutableDictionary<string, DocumentationScribeSemanticToolPort> ports;

    public DocumentationScribeManifestSemanticToolPort(DocumentationScribeLoadedManifestContext manifest)
    {
        this.manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        ports = manifest.Request.Manifest.ToImmutableDictionary(member => member.TargetId,
            member => new DocumentationScribeSemanticToolPort(manifest.GetContext(member.TargetId), manifest.Request),
            StringComparer.Ordinal);
    }

    public async ValueTask<DocumentationScribeSemanticToolResult> InvokeAsync(
        DocumentationScribeSemanticToolRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ports.TryGetValue(request.TargetId, out var port))
            return new(DocumentationScribeToolOutcome.Failure, null, DocumentationScribeSemanticFailureReason.InvalidRequest);
        if (!manifest.VerifyFreshness(cancellationToken))
            return new(DocumentationScribeToolOutcome.Failure, null, DocumentationScribeSemanticFailureReason.StaleContext);
        var result = await port.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
        return manifest.VerifyFreshness(cancellationToken) ? result
            : new(DocumentationScribeToolOutcome.Failure, null, DocumentationScribeSemanticFailureReason.StaleContext);
    }

    public override string ToString() =>
        $"{nameof(DocumentationScribeManifestSemanticToolPort)} {{ Targets = {ports.Count} }}";
}
