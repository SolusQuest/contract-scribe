using System.Collections.Immutable;
using ContractScribe.Core;

namespace ContractScribe.Roslyn;

public sealed record DocumentationScribeManifestContextBootstrapResult(
    DocumentationScribeContextBootstrapStatus Status,
    DocumentationScribeLoadedManifestContext? Context,
    DocumentationScribeContextFailure? Failure);

public sealed class DocumentationScribeLoadedManifestContext
{
    private const long MaximumCapturedUtf8Bytes = 64 * 1024 * 1024;
    private readonly ImmutableDictionary<string, DocumentationScribeLoadedContext> contexts;

    private DocumentationScribeLoadedManifestContext(
        DocumentationScribeRequest request,
        ImmutableDictionary<string, DocumentationScribeLoadedContext> contexts)
    {
        Request = request;
        this.contexts = contexts;
    }

    public DocumentationScribeRequest Request { get; }

    public DocumentationScribeLoadedContext GetContext(string targetId) => contexts[targetId];

    public IEnumerable<DocumentationScribeLoadedContext> Contexts =>
        Request.Manifest.Select(member => contexts[member.TargetId]);

    public bool VerifyFreshness(CancellationToken cancellationToken = default) =>
        Contexts.All(context => context.VerifyFreshness(cancellationToken));

    internal bool VerifyFreshness(CancellationToken cancellationToken, Action checkpoint) =>
        Contexts.All(context => context.VerifyFreshness(cancellationToken, checkpoint));

    public static DocumentationScribeManifestContextBootstrapResult Bootstrap(
        ClassifiedRepositorySession session,
        DocumentationScribeRequest request,
        ImmutableArray<DocumentationScribeContextBootstrapSelection> acceptedSelections,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        if (acceptedSelections.IsDefault || acceptedSelections.Length != request.Manifest.Length)
            return Reject(DocumentationScribeContextBootstrapStatus.Failed, "context.correlation.request");

        // The caller supplies this selection independently; the request cannot add a member.
        for (var index = 0; index < acceptedSelections.Length; index++)
        {
            var accepted = acceptedSelections[index];
            var member = request.Manifest[index];
            if (accepted.RepositoryContextRef != request.Context.RepositoryContextRef
                || accepted.InputIdentity != request.Context.InputIdentity
                || accepted.TargetProfile != request.Context.TargetProfile
                || accepted.SymbolRef != member.Target.SymbolRef
                || accepted.SourceLocator != member.Target.SourceLocator
                || accepted.SourceSha256 != member.Target.SourceSha256)
                return Reject(DocumentationScribeContextBootstrapStatus.Failed, "context.correlation.request");
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(
            Math.Min(30_000, request.Limits.MaximumElapsedMilliseconds)));
        using var boundedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var boundedToken = boundedCancellation.Token;
        var started = Environment.TickCount64;
        var contexts = ImmutableDictionary.CreateBuilder<string, DocumentationScribeLoadedContext>(StringComparer.Ordinal);
        var captured = new Dictionary<(string Path, string Sha256), long>();
        var incomplete = false;
        try
        {
            for (var index = 0; index < acceptedSelections.Length; index++)
            {
                boundedToken.ThrowIfCancellationRequested();
                if (Environment.TickCount64 - started > Math.Min(30_000, request.Limits.MaximumElapsedMilliseconds))
                    return Reject(DocumentationScribeContextBootstrapStatus.TimedOut, "context.timeout.operation");
                var accepted = acceptedSelections[index];
                var member = request.Manifest[index];
                var seedBytes = request.EvidenceReferences.Where(reference =>
                        member.EvidenceReferenceIds.Contains(reference.EvidenceReferenceId, StringComparer.Ordinal)
                        && reference.Kind == EvidenceKind.SourceDeclaration
                        && reference.Locator == accepted.SourceLocator)
                    .Select(reference => reference.IncludedUtf8ByteCount).DefaultIfEmpty(0).Max();
                var original = accepted.Limits;
                var limits = DocumentationScribeContextValidation.CreateLimits(
                    original.MaximumInstructionFiles, original.MaximumInstructionDepth,
                    original.MaximumInstructionFileUtf8Bytes, original.MaximumDeclarationReferences,
                    original.MaximumDeclarationFiles, original.MaximumInspectedSourceUtf8Bytes,
                    original.MaximumSourceFileUtf8Bytes,
                    Math.Max(1, Math.Min(original.MaximumIncludedSourceUtf8Bytes, seedBytes)),
                    original.MaximumTotalContextUtf8Bytes, original.MaximumElapsedMilliseconds);
                var selection = DocumentationScribeContextValidation.CreateBootstrapSelection(
                    accepted.RepositoryContextRef, accepted.InputIdentity, accepted.TargetProfile,
                    accepted.SymbolRef, accepted.SourceLocator, accepted.SourceSha256,
                    accepted.ConfiguredAgentEntrypoint, limits);
                var result = new DocumentationScribeContextBootstrapper().Bootstrap(session, selection, boundedToken);
                if (result.Context is not { } context)
                    return deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested
                        ? Reject(DocumentationScribeContextBootstrapStatus.TimedOut, "context.timeout.operation")
                        : new(result.Status, null, result.Failure);
                if (!context.ValidateRequestBinding(request).IsValid)
                    return Reject(DocumentationScribeContextBootstrapStatus.Failed, "context.correlation.request");

                foreach (var file in context.CapturedFiles)
                    captured.TryAdd((file.Path.RepositoryPath, file.ContentSha256), file.Identity.Length);
                if (captured.Values.Sum() > MaximumCapturedUtf8Bytes)
                    return Reject(DocumentationScribeContextBootstrapStatus.BudgetExhausted, "context.budget.total-bytes");
                incomplete |= result.Status == DocumentationScribeContextBootstrapStatus.Incomplete;
                contexts.Add(member.TargetId, context);
            }

            var loaded = new DocumentationScribeLoadedManifestContext(request, contexts.ToImmutable());
            if (!loaded.VerifyFreshness(boundedToken))
                return Reject(DocumentationScribeContextBootstrapStatus.Failed, "context.stale.publication");
            return new(incomplete ? DocumentationScribeContextBootstrapStatus.Incomplete
                : DocumentationScribeContextBootstrapStatus.Succeeded, loaded, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(DocumentationScribeContextBootstrapStatus.Cancelled, null, null);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return Reject(DocumentationScribeContextBootstrapStatus.TimedOut, "context.timeout.operation");
        }
    }

    private static DocumentationScribeManifestContextBootstrapResult Reject(
        DocumentationScribeContextBootstrapStatus status, string code) =>
        new(status, null, DocumentationScribeContextValidation.CreateFailure(
            code.StartsWith("context.correlation.", StringComparison.Ordinal)
                ? DocumentationScribeContextFailureCategory.Correlation
                : code.StartsWith("context.stale.", StringComparison.Ordinal)
                    ? DocumentationScribeContextFailureCategory.Stale
                    : DocumentationScribeContextFailureCategory.Internal,
            code));

    public override string ToString() =>
        $"{nameof(DocumentationScribeLoadedManifestContext)} {{ Members = {contexts.Count} }}";
}
