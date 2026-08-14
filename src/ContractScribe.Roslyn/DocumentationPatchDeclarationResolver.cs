using System.Collections.Immutable;
using System.Text;
using ContractScribe.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ContractScribe.Roslyn;

public sealed record DocumentationPatchDeclarationFailure
{
    internal DocumentationPatchDeclarationFailure(string code, string blockId)
    {
        Code = code;
        BlockId = blockId;
    }

    public string Code { get; }

    public string BlockId { get; }
}

public sealed record DocumentationPatchResolvedComponent
{
    internal DocumentationPatchResolvedComponent(
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

public sealed record DocumentationPatchResolvedDeclaration
{
    internal DocumentationPatchResolvedDeclaration(
        string blockId,
        SymbolRef symbolRef,
        string projectIdentity,
        string repositoryPath,
        string physicalSourceIdentity,
        string sourceSha256,
        DocumentationPatchRepositoryEncoding encoding,
        Utf16Span requestedDeclarationSpan,
        Utf16Span canonicalDeclarationSpan,
        Utf16Span ownerSpan,
        Utf16Span? documentationSpan,
        DocumentationBlockState blockState,
        ImmutableArray<DocumentationPatchResolvedComponent> applicableComponents,
        ImmutableArray<SymbolRef> ownerSymbolRefs,
        bool isMultiDeclarator,
        bool isPrimaryConstructor,
        bool hasPrimaryConstructorAlias)
    {
        BlockId = blockId;
        SymbolRef = symbolRef;
        ProjectIdentity = projectIdentity;
        RepositoryPath = repositoryPath;
        PhysicalSourceIdentity = physicalSourceIdentity;
        SourceSha256 = sourceSha256;
        Encoding = encoding;
        RequestedDeclarationSpan = requestedDeclarationSpan;
        CanonicalDeclarationSpan = canonicalDeclarationSpan;
        OwnerSpan = ownerSpan;
        DocumentationSpan = documentationSpan;
        BlockState = blockState;
        ApplicableComponents = applicableComponents;
        OwnerSymbolRefs = ownerSymbolRefs;
        IsMultiDeclarator = isMultiDeclarator;
        IsPrimaryConstructor = isPrimaryConstructor;
        HasPrimaryConstructorAlias = hasPrimaryConstructorAlias;
    }

    public string BlockId { get; }

    public SymbolRef SymbolRef { get; }

    public string ProjectIdentity { get; }

    public string RepositoryPath { get; }

    public string PhysicalSourceIdentity { get; }

    public string SourceSha256 { get; }

    public DocumentationPatchRepositoryEncoding Encoding { get; }

    public Utf16Span RequestedDeclarationSpan { get; }

    public Utf16Span CanonicalDeclarationSpan { get; }

    public Utf16Span OwnerSpan { get; }

    public Utf16Span? DocumentationSpan { get; }

    public DocumentationBlockState BlockState { get; }

    public ImmutableArray<DocumentationPatchResolvedComponent> ApplicableComponents { get; }

    public ImmutableArray<SymbolRef> OwnerSymbolRefs { get; }

    public bool IsMultiDeclarator { get; }

    public bool IsPrimaryConstructor { get; }

    public bool HasPrimaryConstructorAlias { get; }
}

public sealed record DocumentationPatchDeclarationBlock
{
    internal DocumentationPatchDeclarationBlock(
        string blockId,
        ImmutableArray<DocumentationPatchDeclarationFailure> failures,
        DocumentationPatchResolvedDeclaration? declaration)
    {
        BlockId = blockId;
        Failures = failures;
        Declaration = declaration;
    }

    public string BlockId { get; }

    public ImmutableArray<DocumentationPatchDeclarationFailure> Failures { get; }

    public DocumentationPatchResolvedDeclaration? Declaration { get; }
}

public sealed record DocumentationPatchDeclarationBatch
{
    internal DocumentationPatchDeclarationBatch(
        string? rootFailureCode,
        ImmutableArray<DocumentationPatchDeclarationBlock> blocks)
    {
        RootFailureCode = rootFailureCode;
        Blocks = blocks;
    }

    public string? RootFailureCode { get; }

    public ImmutableArray<DocumentationPatchDeclarationBlock> Blocks { get; }
}

public sealed class DocumentationPatchDeclarationResolver
{
    private readonly RepositoryPathResolver pathResolver = new();

    public DocumentationPatchDeclarationBatch Resolve(
        ClassifiedRepositorySession session,
        DocumentationPatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!session.IsBoundToClassificationSession
            || session.Classification.Status != ClassificationRunStatus.Success
            || session.Classification.ClassificationSet is not { } classifications)
        {
            return RootFailure(request, "patch.stale.repository-context");
        }

        var repository = session.RepositorySession;
        var rootFailure = SelectRootFailure(request, repository, classifications);
        if (rootFailure is not null)
        {
            return RootFailure(request, rootFailure);
        }

        var projects = repository.Projects
            .GroupBy(project => project.CompilationContextRef, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        var cache = new ResolverCache(repository.PhysicalRepositoryRoot, pathResolver);
        var blocks = ImmutableArray.CreateBuilder<DocumentationPatchDeclarationBlock>(
            request.Blocks.Length);
        foreach (var block in request.Blocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            blocks.Add(ResolveBlock(
                repository,
                classifications,
                projects,
                block,
                cache,
                cancellationToken));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new DocumentationPatchDeclarationBatch(null, blocks.ToImmutable());
    }

    private DocumentationPatchDeclarationBlock ResolveBlock(
        LoadedRepositorySession repository,
        ClassificationSet classifications,
        IReadOnlyDictionary<string, LoadedProject> projects,
        DocumentationPatchBlockRequest block,
        ResolverCache cache,
        CancellationToken cancellationToken)
    {
        var failures = ImmutableArray.CreateBuilder<DocumentationPatchDeclarationFailure>();
        var hasProject = projects.TryGetValue(
            block.SymbolRef.CompilationContextRef,
            out var project);
        if (!hasProject)
        {
            AddFailure(failures, block, "patch.stale.compilation-context");
        }

        var sourceCheck = ValidateCommittedSource(
            repository,
            block.SymbolRef,
            block.Locator,
            cache,
            cancellationToken);
        if (sourceCheck.FailureCode is { } sourceFailure)
        {
            AddFailure(failures, block, sourceFailure);
        }

        var targetRows = classifications.Targets
            .Where(target => target.SymbolRef == block.SymbolRef)
            .ToImmutableArray();
        var supported = targetRows.Length == 1
            && targetRows[0].SupportStatus == SupportStatus.Supported
            && targetRows[0].Origin != ClassificationOrigin.Mixed
            ? targetRows[0]
            : null;
        var unresolvedRows = classifications.Unresolved
            .Where(unresolved => string.Equals(
                    unresolved.CompilationContextRef,
                    block.SymbolRef.CompilationContextRef,
                    StringComparison.Ordinal)
                && Matches(unresolved.CandidateLocator, block.Locator))
            .ToImmutableArray();
        if (supported is null)
        {
            AddFailure(
                failures,
                block,
                targetRows.Any(target =>
                    target.SupportStatus == SupportStatus.Ambiguous
                    || target.Origin == ClassificationOrigin.Mixed)
                || unresolvedRows.Any(unresolved =>
                    unresolved.SupportStatus == SupportStatus.Ambiguous
                    || unresolved.Origin == ClassificationOrigin.Mixed)
                    ? "patch.rejected.ambiguous-target"
                    : "patch.rejected.unsupported-target");
        }

        if (hasProject
            && supported is not null
            && block.Locator is DocumentationPatchGeneratedLocator)
        {
            AddFailure(failures, block, "patch.rejected.non-writable-target");
        }

        DocumentationPatchResolvedDeclaration? resolved = null;
        if (hasProject
            && supported is not null
            && block.Locator is DocumentationPatchRepositoryLocator repositoryLocator
            && sourceCheck.FailureCode is null)
        {
            resolved = ResolveRepositoryDeclaration(
                classifications,
                projects,
                project!,
                block,
                repositoryLocator,
                supported,
                cache,
                failures,
                cancellationToken);
        }

        return new DocumentationPatchDeclarationBlock(
            block.BlockId,
            failures.ToImmutable(),
            failures.Any(failure => failure.Code.StartsWith("patch.stale.", StringComparison.Ordinal))
                ? null
                : resolved);
    }

    private DocumentationPatchResolvedDeclaration? ResolveRepositoryDeclaration(
        ClassificationSet classifications,
        IReadOnlyDictionary<string, LoadedProject> projects,
        LoadedProject project,
        DocumentationPatchBlockRequest block,
        DocumentationPatchRepositoryLocator locator,
        TargetClassification target,
        ResolverCache cache,
        ImmutableArray<DocumentationPatchDeclarationFailure>.Builder failures,
        CancellationToken cancellationToken)
    {
        var symbols = DocumentationCommentId.GetSymbolsForDeclarationId(
                block.SymbolRef.DocumentationCommentId,
                project.Compilation)
            .Select(CanonicalPartialMember)
            .Distinct(SymbolEqualityComparer.Default)
            .ToImmutableArray();
        if (symbols.Length != 1)
        {
            AddFailure(failures, block, "patch.rejected.ambiguous-target");
            return null;
        }

        var symbol = symbols[0];
        var definition = CanonicalPartialMember(symbol);
        if (HasUnauthorizedPartialImplementation(definition))
        {
            AddFailure(failures, block, "patch.rejected.ambiguous-target");
            return null;
        }

        var implementation = GetPartialImplementation(definition);
        ImmutableArray<SyntaxReference> authorityReferences;
        if (implementation is not null)
        {
            var consulted = definition.DeclaringSyntaxReferences
                .Concat(implementation.DeclaringSyntaxReferences)
                .ToImmutableArray();
            if (!ValidateConsultedReferences(project, consulted, cache, cancellationToken))
            {
                AddFailure(failures, block, "patch.stale.source-bytes");
                return null;
            }

            var implementationReferences = implementation.DeclaringSyntaxReferences
                .ToImmutableArray();
            authorityReferences = implementationReferences.Any(reference =>
                    HasAttachedDocumentation(GetDocumentationOwner(
                        reference.GetSyntax(cancellationToken))))
                ? implementationReferences
                : definition.DeclaringSyntaxReferences.ToImmutableArray();
        }
        else
        {
            authorityReferences = definition.DeclaringSyntaxReferences.ToImmutableArray();
        }

        var matching = authorityReferences
            .Where(reference => Matches(reference, project, locator))
            .ToImmutableArray();
        if (matching.Length != 1)
        {
            AddFailure(
                failures,
                block,
                authorityReferences.Length > 1
                    && definition is not INamedTypeSymbol
                    ? "patch.rejected.ambiguous-target"
                    : "patch.stale.source-span");
            return null;
        }

        var reference = matching[0];
        var syntax = reference.GetSyntax(cancellationToken);
        var physicalSourceIdentity = project.SourceTrees[reference.SyntaxTree].PhysicalSourceIdentity
            ?? throw new InvalidOperationException(
                "A repository source must retain its physical source identity.");
        var owner = GetDocumentationOwner(syntax);
        if (owner is null)
        {
            AddFailure(failures, block, "patch.rejected.unsupported-target");
            return null;
        }

        var components = ResolveComponents(
            classifications,
            block.SymbolRef,
            owner);
        var ownerResolution = ResolveOwnerSymbols(
            classifications,
            projects,
            physicalSourceIdentity,
            owner.Span.Start,
            owner.Span.End,
            cache,
            cancellationToken);
        var attached = GetAttachedDocumentation(owner.GetLeadingTrivia());
        var blockState = AnalyzeBlock(attached);
        Utf16Span? documentationSpan = attached.IsDefaultOrEmpty
            ? null
            : Span(
                attached.Min(trivia => trivia.FullSpan.Start),
                attached.Max(trivia => trivia.FullSpan.End));
        var multiDeclarator = owner is BaseFieldDeclarationSyntax field
            && field.Declaration.Variables.Count != 1;
        var primaryConstructor = definition is IMethodSymbol
        {
            MethodKind: MethodKind.Constructor,
            IsImplicitlyDeclared: false,
        }
            && syntax is TypeDeclarationSyntax or ParameterSyntax;

        return new DocumentationPatchResolvedDeclaration(
            block.BlockId,
            block.SymbolRef,
            project.ProjectIdentity,
            locator.Path,
            physicalSourceIdentity,
            locator.OriginalFileSha256,
            locator.Encoding,
            locator.DeclarationSpan,
            Span(reference.Span.Start, reference.Span.End),
            Span(owner.Span.Start, owner.Span.End),
            documentationSpan,
            blockState,
            components,
            ownerResolution.SymbolRefs,
            multiDeclarator,
            primaryConstructor,
            ownerResolution.HasPrimaryConstructor);
    }

    private static ImmutableArray<DocumentationPatchResolvedComponent> ResolveComponents(
        ClassificationSet classifications,
        SymbolRef symbolRef,
        SyntaxNode owner)
    {
        var components = ImmutableArray.CreateBuilder<DocumentationPatchResolvedComponent>();
        foreach (var component in classifications.Components
            .Where(component => component.ParentSymbolRef == symbolRef
                && component.SupportStatus == SupportStatus.Supported
                && component.ComponentKind is ComponentKind.TypeParameter
                    or ComponentKind.Parameter
                    or ComponentKind.Return
                    or ComponentKind.Value)
            .OrderBy(component => component.ComponentKind)
            .ThenBy(component => component.Identity, StringComparer.Ordinal))
        {
            var kind = component.ComponentKind switch
            {
                ComponentKind.TypeParameter => DocumentationPatchComponentKind.TypeParameter,
                ComponentKind.Parameter => DocumentationPatchComponentKind.Parameter,
                ComponentKind.Return => DocumentationPatchComponentKind.Return,
                ComponentKind.Value => DocumentationPatchComponentKind.Value,
                _ => throw new InvalidOperationException("Unexpected patch component kind."),
            };
            components.Add(new DocumentationPatchResolvedComponent(
                kind,
                component.Identity,
                GetComponentName(owner, component.ComponentKind, component.Identity)));
        }

        return components
            .OrderBy(component => component.Kind)
            .ThenBy(component => component.Identity, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static string? GetComponentName(
        SyntaxNode owner,
        ComponentKind kind,
        string identity)
    {
        if (kind is ComponentKind.Return or ComponentKind.Value)
        {
            return null;
        }

        var separator = identity.LastIndexOf('/');
        if (separator < 0
            || !int.TryParse(
                identity.AsSpan(separator + 1),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var ordinal))
        {
            return null;
        }

        if (kind == ComponentKind.TypeParameter)
        {
            var typeParameterSyntax = owner switch
            {
                TypeDeclarationSyntax type => type.TypeParameterList?.Parameters,
                DelegateDeclarationSyntax @delegate => @delegate.TypeParameterList?.Parameters,
                MethodDeclarationSyntax method => method.TypeParameterList?.Parameters,
                _ => null,
            };
            return typeParameterSyntax is { } typeParameters && ordinal < typeParameters.Count
                ? typeParameters[ordinal].Identifier.ValueText
                : null;
        }

        SeparatedSyntaxList<ParameterSyntax>? valueParameters = owner switch
        {
            BaseMethodDeclarationSyntax method => method.ParameterList.Parameters,
            DelegateDeclarationSyntax @delegate => @delegate.ParameterList.Parameters,
            IndexerDeclarationSyntax indexer => indexer.ParameterList.Parameters,
            TypeDeclarationSyntax type => type.ParameterList?.Parameters,
            _ => null,
        };
        return valueParameters is { } parameters && ordinal < parameters.Count
            ? parameters[ordinal].Identifier.ValueText
            : null;
    }

    private static OwnerResolution ResolveOwnerSymbols(
        ClassificationSet classifications,
        IReadOnlyDictionary<string, LoadedProject> projects,
        string physicalSourceIdentity,
        int ownerStart,
        int ownerEnd,
        ResolverCache cache,
        CancellationToken cancellationToken)
    {
        var symbols = ImmutableHashSet.CreateBuilder<SymbolRef>();
        var hasPrimaryConstructor = false;
        foreach (var target in classifications.Targets.Where(
            target => target.SupportStatus == SupportStatus.Supported))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!projects.TryGetValue(target.SymbolRef.CompilationContextRef, out var project))
            {
                continue;
            }

            foreach (var canonical in DocumentationCommentId.GetSymbolsForDeclarationId(
                target.SymbolRef.DocumentationCommentId,
                project.Compilation).Select(CanonicalPartialMember))
            {
                var references = canonical.DeclaringSyntaxReferences
                    .Concat(GetAnyPartialImplementation(canonical)?.DeclaringSyntaxReferences ?? []);
                foreach (var reference in references)
                {
                    if (!project.SourceTrees.TryGetValue(reference.SyntaxTree, out var source)
                        || source.RepositoryPath is not { } sourcePath)
                    {
                        continue;
                    }

                    var current = cache.Read(sourcePath);
                    if (current.FailureCode is not null
                        || !string.Equals(
                            current.PhysicalSourceIdentity,
                            physicalSourceIdentity,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var owner = GetDocumentationOwner(reference.GetSyntax(cancellationToken));
                    if (owner is not null
                        && owner.Span.Start == ownerStart
                        && owner.Span.End == ownerEnd)
                    {
                        symbols.Add(target.SymbolRef);
                        hasPrimaryConstructor |= target.PrimaryKind == PrimarySymbolKind.Constructor
                            && reference.GetSyntax(cancellationToken)
                                is TypeDeclarationSyntax or ParameterSyntax;
                    }
                }
            }
        }

        return new OwnerResolution(
            symbols
                .OrderBy(symbol => symbol.CompilationContextRef, StringComparer.Ordinal)
                .ThenBy(symbol => symbol.DocumentationCommentId, StringComparer.Ordinal)
                .ToImmutableArray(),
            hasPrimaryConstructor);
    }

    private static bool Matches(
        SyntaxReference reference,
        LoadedProject project,
        DocumentationPatchRepositoryLocator locator) =>
        project.SourceTrees.TryGetValue(reference.SyntaxTree, out var source)
        && source.RepositoryPath is { } path
        && string.Equals(path, locator.Path, StringComparison.Ordinal)
        && reference.Span.Start == locator.DeclarationSpan.Start
        && reference.Span.End == locator.DeclarationSpan.End;

    private static bool Matches(
        CandidateLocator candidate,
        DocumentationPatchSourceLocator locator) =>
        (candidate, locator) switch
        {
            (RepositoryCandidateLocator repository,
                DocumentationPatchRepositoryLocator requested) =>
                string.Equals(repository.Path, requested.Path, StringComparison.Ordinal)
                && Matches(repository.Span, requested.DeclarationSpan),
            (GeneratedSourceCandidateLocator generated,
                DocumentationPatchSourceGeneratorLocator requested) =>
                string.Equals(generated.GeneratorId, requested.ProducerId, StringComparison.Ordinal)
                && string.Equals(generated.HintNameId, requested.OutputId, StringComparison.Ordinal)
                && Matches(generated.Span, requested.DeclarationSpan),
            (ToolGeneratedCandidateLocator generated,
                DocumentationPatchToolGeneratedLocator requested) =>
                string.Equals(generated.ProducerId, requested.ProducerId, StringComparison.Ordinal)
                && string.Equals(generated.OutputId, requested.OutputId, StringComparison.Ordinal)
                && Matches(generated.Span, requested.DeclarationSpan),
            _ => false,
        };

    private static bool Matches(Utf16Span? candidate, Utf16Span requested) =>
        candidate is { } span
        && span.Start == requested.Start
        && span.End == requested.End;

    private static bool ValidateConsultedReferences(
        LoadedProject project,
        ImmutableArray<SyntaxReference> references,
        ResolverCache cache,
        CancellationToken cancellationToken)
    {
        foreach (var reference in references)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!project.SourceTrees.TryGetValue(reference.SyntaxTree, out var source)
                || source.RepositoryPath is not { } path)
            {
                return false;
            }

            var current = cache.Read(path);
            if (current.FailureCode is not null
                || !string.Equals(
                    current.PhysicalSourceIdentity,
                    source.PhysicalSourceIdentity,
                    StringComparison.Ordinal)
                || !string.Equals(
                    current.DecodedText,
                    reference.SyntaxTree.GetText(cancellationToken).ToString(),
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static SourceCheck ValidateCommittedSource(
        LoadedRepositorySession repository,
        SymbolRef symbolRef,
        DocumentationPatchSourceLocator locator,
        ResolverCache cache,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (locator is DocumentationPatchRepositoryLocator repositoryLocator)
        {
            var matchingTrees = repository.Projects
                .Where(project => string.Equals(
                    project.CompilationContextRef,
                    symbolRef.CompilationContextRef,
                    StringComparison.Ordinal))
                .SelectMany(project => project.SourceTrees)
                .Where(pair => pair.Value.RepositoryPath is { } path
                    && string.Equals(path, repositoryLocator.Path, StringComparison.Ordinal))
                .ToImmutableArray();
            if (matchingTrees.IsEmpty)
            {
                return SourceCheck.Failure("patch.stale.source-bytes");
            }

            var current = cache.Read(repositoryLocator.Path);
            if (current.FailureCode is { } readFailure)
            {
                return SourceCheck.Failure(readFailure);
            }

            if (matchingTrees.Any(pair => !string.Equals(
                pair.Value.PhysicalSourceIdentity,
                current.PhysicalSourceIdentity,
                StringComparison.Ordinal)))
            {
                return SourceCheck.Failure("patch.stale.source-bytes");
            }

            var validated = DocumentationPatchValidator.ValidateRepositorySource(
                repositoryLocator,
                current.Bytes);
            if (!validated.IsValid)
            {
                return SourceCheck.Failure(validated.Code!);
            }

            if (!matchingTrees.Any(pair => string.Equals(
                pair.Key.GetText(cancellationToken).ToString(),
                validated.DecodedText,
                StringComparison.Ordinal)))
            {
                return SourceCheck.Failure("patch.stale.source-bytes");
            }

            if (!matchingTrees.Any(pair => HasExactDeclarationSpan(
                pair.Key,
                repositoryLocator.DeclarationSpan,
                cancellationToken)))
            {
                return SourceCheck.Failure("patch.stale.source-span");
            }

            return SourceCheck.Success;
        }

        var generatedLocator = (DocumentationPatchGeneratedLocator)locator;
        var sourceKind = locator.Kind == DocumentationPatchSourceKind.SourceGenerator
            ? LoadedSourceKind.SourceGenerator
            : LoadedSourceKind.ToolGenerated;
        var generatedSources = repository.Projects
            .Where(project => string.Equals(
                project.CompilationContextRef,
                symbolRef.CompilationContextRef,
                StringComparison.Ordinal))
            .SelectMany(project => project.SourceTrees)
            .Where(pair => pair.Value.Kind == sourceKind
                && pair.Value.GeneratedSource is { } fact
                && string.Equals(fact.ProducerId, generatedLocator.ProducerId, StringComparison.Ordinal)
                && string.Equals(fact.OutputId, generatedLocator.OutputId, StringComparison.Ordinal))
            .ToImmutableArray();
        var facts = generatedSources
            .Select(pair => pair.Value.GeneratedSource!)
            .Distinct()
            .ToImmutableArray();
        if (facts.Length != 1)
        {
            return SourceCheck.Failure("patch.stale.source-bytes");
        }

        var generatedCheck = DocumentationPatchValidator.ValidateGeneratedSource(
            generatedLocator,
            facts[0].SourceText);
        if (!generatedCheck.IsValid)
        {
            return SourceCheck.Failure(generatedCheck.Code!);
        }

        return generatedSources.Any(pair => HasExactDeclarationSpan(
            pair.Key,
            generatedLocator.DeclarationSpan,
            cancellationToken))
            ? SourceCheck.Success
            : SourceCheck.Failure("patch.stale.source-span");
    }

    private static bool HasExactDeclarationSpan(
        SyntaxTree tree,
        Utf16Span span,
        CancellationToken cancellationToken) =>
        tree.GetRoot(cancellationToken)
            .DescendantNodesAndSelf()
            .Any(node => node.Span.Start == span.Start
                && node.Span.End == span.End
                && node is TypeDeclarationSyntax
                    or DelegateDeclarationSyntax
                    or EnumDeclarationSyntax
                    or EnumMemberDeclarationSyntax
                    or BaseMethodDeclarationSyntax
                    or PropertyDeclarationSyntax
                    or IndexerDeclarationSyntax
                    or EventDeclarationSyntax
                    or VariableDeclaratorSyntax
                    or ParameterSyntax);

    private static string? SelectRootFailure(
        DocumentationPatchRequest request,
        LoadedRepositorySession repository,
        ClassificationSet classifications)
    {
        if (request.Context.RepositoryContextRef != repository.RepositoryContextRef)
        {
            return "patch.stale.repository-context";
        }

        if (!string.Equals(
            request.Context.InputIdentity,
            repository.InputIdentity,
            StringComparison.Ordinal))
        {
            return "patch.stale.input-identity";
        }

        return request.Context.TargetProfile != classifications.TargetProfile
            ? "patch.stale.target-profile"
            : null;
    }

    private static DocumentationPatchDeclarationBatch RootFailure(
        DocumentationPatchRequest request,
        string code) =>
        new(
            code,
            request.Blocks.Select(block => new DocumentationPatchDeclarationBlock(
                block.BlockId,
                [],
                null)).ToImmutableArray());

    private static void AddFailure(
        ImmutableArray<DocumentationPatchDeclarationFailure>.Builder failures,
        DocumentationPatchBlockRequest block,
        string code)
    {
        if (!failures.Any(failure => string.Equals(failure.Code, code, StringComparison.Ordinal)))
        {
            failures.Add(new DocumentationPatchDeclarationFailure(code, block.BlockId));
        }
    }

    private static SyntaxNode? GetDocumentationOwner(SyntaxNode syntax) =>
        syntax switch
        {
            VariableDeclaratorSyntax variable =>
                variable.FirstAncestorOrSelf<BaseFieldDeclarationSyntax>(),
            ParameterSyntax parameter
                when parameter.Parent?.Parent is TypeDeclarationSyntax type => type,
            TypeDeclarationSyntax or DelegateDeclarationSyntax
                or EnumDeclarationSyntax or EnumMemberDeclarationSyntax
                or BaseMethodDeclarationSyntax or PropertyDeclarationSyntax
                or IndexerDeclarationSyntax or EventDeclarationSyntax
                or BaseFieldDeclarationSyntax => syntax,
            _ => null,
        };

    private static ImmutableArray<SyntaxTrivia> GetAttachedDocumentation(
        SyntaxTriviaList leading)
    {
        var result = new List<SyntaxTrivia>();
        var foundDocumentation = false;
        for (var index = leading.Count - 1; index >= 0; index--)
        {
            var trivia = leading[index];
            if (trivia.IsKind(SyntaxKind.WhitespaceTrivia)
                || trivia.IsKind(SyntaxKind.EndOfLineTrivia))
            {
                continue;
            }

            if (trivia.HasStructure
                && trivia.GetStructure() is DocumentationCommentTriviaSyntax)
            {
                foundDocumentation = true;
                result.Add(trivia);
                continue;
            }

            if (foundDocumentation)
            {
                break;
            }

            return [];
        }

        result.Reverse();
        return result.ToImmutableArray();
    }

    private static bool HasAttachedDocumentation(SyntaxNode? owner) =>
        owner is not null
        && !GetAttachedDocumentation(owner.GetLeadingTrivia()).IsDefaultOrEmpty;

    private static DocumentationBlockState AnalyzeBlock(
        ImmutableArray<SyntaxTrivia> attached)
    {
        if (attached.IsDefaultOrEmpty)
        {
            return DocumentationBlockState.NoBlock;
        }

        var structures = attached
            .Select(trivia => (DocumentationCommentTriviaSyntax)trivia.GetStructure()!)
            .ToImmutableArray();
        if (structures.Any(structure => structure.ContainsDiagnostics))
        {
            return DocumentationBlockState.Malformed;
        }

        return structures.Any(structure => structure.Content.Any(node =>
            node is XmlElementSyntax or XmlEmptyElementSyntax
            || node is XmlTextSyntax text && text.TextTokens.Any(token =>
                !token.IsKind(SyntaxKind.XmlTextLiteralNewLineToken)
                && !string.IsNullOrWhiteSpace(token.ValueText))))
            ? DocumentationBlockState.WellFormed
            : DocumentationBlockState.WhitespaceOnly;
    }

    private static ISymbol CanonicalPartialMember(ISymbol symbol) =>
        symbol switch
        {
            IMethodSymbol { PartialDefinitionPart: { } definition } => definition,
            IPropertySymbol { PartialDefinitionPart: { } definition } => definition,
            IEventSymbol { PartialDefinitionPart: { } definition } => definition,
            _ => symbol,
        };

    private static ISymbol? GetPartialImplementation(ISymbol symbol) =>
        symbol switch
        {
            IMethodSymbol { MethodKind: MethodKind.Ordinary } method =>
                method.PartialImplementationPart,
            _ => null,
        };

    private static bool HasUnauthorizedPartialImplementation(ISymbol symbol) =>
        GetAnyPartialImplementation(symbol) is not null
        && GetPartialImplementation(symbol) is null;

    private static ISymbol? GetAnyPartialImplementation(ISymbol symbol) =>
        symbol switch
        {
            IMethodSymbol method => method.PartialImplementationPart,
            IPropertySymbol property => property.PartialImplementationPart,
            IEventSymbol @event => @event.PartialImplementationPart,
            _ => null,
        };

    private static Utf16Span Span(int start, int end) =>
        DocumentationObservationInput.Span(start, end);

    private sealed record SourceCheck(string? FailureCode)
    {
        public static SourceCheck Success { get; } = new((string?)null);

        public static SourceCheck Failure(string code) => new(code);
    }

    private sealed record CurrentSource(
        byte[] Bytes,
        string? DecodedText,
        string? PhysicalSourceIdentity,
        string? FailureCode);

    private sealed record OwnerResolution(
        ImmutableArray<SymbolRef> SymbolRefs,
        bool HasPrimaryConstructor);

    private sealed class ResolverCache(
        string physicalRepositoryRoot,
        RepositoryPathResolver pathResolver)
    {
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);
        private static readonly UnicodeEncoding StrictUtf16Le = new(false, false, true);
        private static readonly UnicodeEncoding StrictUtf16Be = new(true, false, true);
        private readonly Dictionary<string, CurrentSource> sources =
            new(StringComparer.Ordinal);

        public CurrentSource Read(string repositoryPath)
        {
            if (sources.TryGetValue(repositoryPath, out var cached))
            {
                return cached;
            }

            CurrentSource result;
            try
            {
                var relativePath = repositoryPath.Replace(
                    '/',
                    Path.DirectorySeparatorChar);
                if (Path.IsPathRooted(relativePath))
                {
                    throw new ArgumentException(
                        "The repository source identity must be relative.",
                        nameof(repositoryPath));
                }

                var candidate = Path.GetFullPath(Path.Join(
                    physicalRepositoryRoot,
                    relativePath));
                var resolved = pathResolver.ResolveSource(
                    physicalRepositoryRoot,
                    candidate);
                var bytes = File.ReadAllBytes(resolved.PhysicalPath);
                result = new CurrentSource(
                    bytes,
                    Decode(bytes),
                    pathResolver.PhysicalIdentity(
                        physicalRepositoryRoot,
                        resolved.PhysicalPath),
                    null);
            }
            catch (DecoderFallbackException)
            {
                result = new CurrentSource(
                    [],
                    null,
                    null,
                    "patch.stale.source-encoding");
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException
                or LoaderException)
            {
                result = new CurrentSource(
                    [],
                    null,
                    null,
                    "patch.stale.source-bytes");
            }

            sources.Add(repositoryPath, result);
            return result;
        }

        private static string Decode(byte[] bytes)
        {
            if (bytes.Length >= 3
                && bytes[0] == 0xef
                && bytes[1] == 0xbb
                && bytes[2] == 0xbf)
            {
                return StrictUtf8.GetString(bytes.AsSpan(3));
            }

            if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe)
            {
                return StrictUtf16Le.GetString(bytes.AsSpan(2));
            }

            if (bytes.Length >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff)
            {
                return StrictUtf16Be.GetString(bytes.AsSpan(2));
            }

            return StrictUtf8.GetString(bytes);
        }
    }
}
