using System.Collections.Immutable;
using ContractScribe.Core;
using ContractScribe.Roslyn;

namespace ContractScribe.Patching.Resolution;

public enum DocumentationPatchResolutionStatus
{
    Resolved,
    Stale,
    Rejected,
}

public sealed record ResolvedDocumentationPatchTarget
{
    internal ResolvedDocumentationPatchTarget(
        string blockId,
        SymbolRef symbolRef,
        string projectIdentity,
        string repositoryPath,
        string sourceSha256,
        DocumentationPatchRepositoryEncoding encoding,
        Utf16Span declarationSpan,
        Utf16Span ownerSpan,
        Utf16Span? documentationSpan,
        ImmutableArray<DocumentationPatchApplicableComponentFact> applicableComponents)
    {
        BlockId = blockId;
        SymbolRef = symbolRef;
        ProjectIdentity = projectIdentity;
        RepositoryPath = repositoryPath;
        SourceSha256 = sourceSha256;
        Encoding = encoding;
        DeclarationSpan = declarationSpan;
        OwnerSpan = ownerSpan;
        DocumentationSpan = documentationSpan;
        ApplicableComponents = applicableComponents;
    }

    public string BlockId { get; }

    public SymbolRef SymbolRef { get; }

    public string ProjectIdentity { get; }

    public string RepositoryPath { get; }

    public string SourceSha256 { get; }

    public DocumentationPatchRepositoryEncoding Encoding { get; }

    public Utf16Span DeclarationSpan { get; }

    public Utf16Span OwnerSpan { get; }

    public Utf16Span? DocumentationSpan { get; }

    public ImmutableArray<DocumentationPatchApplicableComponentFact> ApplicableComponents { get; }
}

public sealed record DocumentationPatchApplicableComponentFact
{
    internal DocumentationPatchApplicableComponentFact(
        DocumentationPatchComponentKind kind,
        string identity,
        string? name)
    {
        Kind = kind;
        Identity = identity;
        Name = name;
    }

    public DocumentationPatchComponentKind Kind { get; }

    public string Identity { get; }

    public string? Name { get; }
}

public sealed record DocumentationPatchResolutionResult
{
    internal DocumentationPatchResolutionResult(
        DocumentationPatchResolutionStatus status,
        string? primaryCode,
        string? primaryBlockId,
        ImmutableArray<ResolvedDocumentationPatchTarget> targets)
    {
        Status = status;
        PrimaryCode = primaryCode;
        PrimaryBlockId = primaryBlockId;
        Targets = targets;
    }

    public DocumentationPatchResolutionStatus Status { get; }

    public string? PrimaryCode { get; }

    public string? PrimaryBlockId { get; }

    public ImmutableArray<ResolvedDocumentationPatchTarget> Targets { get; }
}

public sealed class DocumentationPatchResolver
{
    private readonly DocumentationPatchDeclarationResolver declarationResolver;

    public DocumentationPatchResolver()
        : this(new DocumentationPatchDeclarationResolver())
    {
    }

    internal DocumentationPatchResolver(
        DocumentationPatchDeclarationResolver declarationResolver)
    {
        this.declarationResolver = declarationResolver
            ?? throw new ArgumentNullException(nameof(declarationResolver));
    }

    public DocumentationPatchResolutionResult Resolve(
        ClassifiedRepositorySession session,
        DocumentationPatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var batch = declarationResolver.Resolve(session, request, cancellationToken);
        if (batch.RootFailureCode is { } rootFailure)
        {
            return Failed(
                DocumentationPatchResolutionStatus.Stale,
                rootFailure,
                null);
        }

        var failures = batch.Blocks
            .SelectMany((block, blockIndex) => block.Failures.Select(failure =>
                new DocumentationPatchResolutionFailure(
                    blockIndex,
                    failure.BlockId,
                    failure.Code)))
            .ToList();
        var declarations = batch.Blocks
            .Where(block => block.Declaration is not null)
            .Select(block => block.Declaration!)
            .ToImmutableArray();

        foreach (var declaration in declarations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var blockIndex = FindBlockIndex(request, declaration.BlockId);
            var requestBlock = request.Blocks[blockIndex];
            if (!IsEditStateValid(requestBlock.EditKind, declaration.BlockState))
            {
                failures.Add(new DocumentationPatchResolutionFailure(
                    blockIndex,
                    declaration.BlockId,
                    "patch.rejected.edit-state"));
            }

            if (!ComponentsMatch(
                requestBlock.ApplicableComponents,
                declaration.ApplicableComponents))
            {
                failures.Add(new DocumentationPatchResolutionFailure(
                    blockIndex,
                    declaration.BlockId,
                    "patch.rejected.unsafe-change"));
            }

            if (declaration.IsPrimaryConstructor
                || declaration.HasPrimaryConstructorAlias)
            {
                failures.Add(new DocumentationPatchResolutionFailure(
                    blockIndex,
                    declaration.BlockId,
                    "patch.rejected.unsupported-target"));
            }
            else if (declaration.IsMultiDeclarator
                || declaration.OwnerSymbolRefs.Length != 1
                || declaration.OwnerSymbolRefs[0] != declaration.SymbolRef)
            {
                failures.Add(new DocumentationPatchResolutionFailure(
                    blockIndex,
                    declaration.BlockId,
                    "patch.rejected.ambiguous-target"));
            }
        }

        foreach (var group in declarations.GroupBy(
            declaration => new OwnerKey(
                declaration.PhysicalSourceIdentity,
                declaration.OwnerSpan.Start,
                declaration.OwnerSpan.End)))
        {
            if (group.Count() <= 1)
            {
                continue;
            }

            foreach (var declaration in group)
            {
                failures.Add(new DocumentationPatchResolutionFailure(
                    FindBlockIndex(request, declaration.BlockId),
                    declaration.BlockId,
                    "patch.rejected.ambiguous-target"));
            }
        }

        if (failures.Count != 0)
        {
            var primary = SelectPrimary(failures);
            return Failed(
                primary.Code.StartsWith("patch.stale.", StringComparison.Ordinal)
                    ? DocumentationPatchResolutionStatus.Stale
                    : DocumentationPatchResolutionStatus.Rejected,
                primary.Code,
                primary.BlockId);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new DocumentationPatchResolutionResult(
            DocumentationPatchResolutionStatus.Resolved,
            null,
            null,
            declarations.Select(declaration => new ResolvedDocumentationPatchTarget(
                declaration.BlockId,
                declaration.SymbolRef,
                declaration.ProjectIdentity,
                declaration.RepositoryPath,
                declaration.SourceSha256,
                declaration.Encoding,
                declaration.CanonicalDeclarationSpan,
                declaration.OwnerSpan,
                declaration.DocumentationSpan,
                declaration.ApplicableComponents.Select(component =>
                    new DocumentationPatchApplicableComponentFact(
                        component.Kind,
                        component.Identity,
                        component.Name)).ToImmutableArray())).ToImmutableArray());
    }

    internal static DocumentationPatchResolutionFailure SelectPrimary(
        IEnumerable<DocumentationPatchResolutionFailure> failures) =>
        failures
                .Distinct()
                .OrderBy(failure =>
                    failure.Code.StartsWith("patch.stale.", StringComparison.Ordinal) ? 0 : 1)
                .ThenBy(failure => failure.BlockIndex)
                .ThenBy(failure => DiagnosticOrder(failure.Code))
                .First();

    private static bool IsEditStateValid(
        DocumentationPatchEditKind editKind,
        DocumentationBlockState blockState) =>
        editKind switch
        {
            DocumentationPatchEditKind.Insert =>
                blockState == DocumentationBlockState.NoBlock,
            DocumentationPatchEditKind.Replace =>
                blockState is DocumentationBlockState.WhitespaceOnly
                    or DocumentationBlockState.WellFormed,
            _ => false,
        };

    private static bool ComponentsMatch(
        ImmutableArray<DocumentationPatchApplicableComponent> requested,
        ImmutableArray<DocumentationPatchResolvedComponent> actual) =>
        requested.Length == actual.Length
        && requested.Zip(actual).All(pair =>
            pair.First.Kind == pair.Second.Kind
            && string.Equals(pair.First.Identity, pair.Second.Identity, StringComparison.Ordinal)
            && string.Equals(pair.First.Name, pair.Second.Name, StringComparison.Ordinal));

    private static int FindBlockIndex(
        DocumentationPatchRequest request,
        string blockId)
    {
        for (var index = 0; index < request.Blocks.Length; index++)
        {
            if (string.Equals(request.Blocks[index].BlockId, blockId, StringComparison.Ordinal))
            {
                return index;
            }
        }

        throw new InvalidOperationException("The declaration batch contains an unknown block.");
    }

    private static int DiagnosticOrder(string code) => code switch
    {
        "patch.stale.compilation-context" => 0,
        "patch.stale.source-encoding" => 1,
        "patch.stale.source-bytes" => 2,
        "patch.stale.source-span" => 3,
        "patch.rejected.unsupported-target" => 0,
        "patch.rejected.ambiguous-target" => 1,
        "patch.rejected.non-writable-target" => 2,
        "patch.rejected.edit-state" => 3,
        "patch.rejected.unsafe-change" => 4,
        "patch.rejected.no-effective-change" => 5,
        _ => int.MaxValue,
    };

    private static DocumentationPatchResolutionResult Failed(
        DocumentationPatchResolutionStatus status,
        string code,
        string? blockId) =>
        new(status, code, blockId, []);

    private readonly record struct OwnerKey(
        string PhysicalSourceIdentity,
        int Start,
        int End);
}

internal sealed record DocumentationPatchResolutionFailure(
    int BlockIndex,
    string BlockId,
    string Code);
