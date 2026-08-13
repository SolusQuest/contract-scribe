using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ContractScribe.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ContractScribe.Roslyn;

internal enum DocumentationObservationStage
{
    ContextBinding,
    SymbolBinding,
    DeclarationEnumeration,
    SourceAccess,
    TriviaExtraction,
    XmlAnalysis,
    CoreNormalization,
    TerminalConstruction,
}

public sealed class DocumentationObserver
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Func<string, Compilation, ImmutableArray<ISymbol>> symbolResolver;
    private readonly Action<DocumentationObservationStage>? stageObserver;

    public DocumentationObserver()
        : this(null, null)
    {
    }

    internal DocumentationObserver(
        Func<string, Compilation, ImmutableArray<ISymbol>>? symbolResolver,
        Action<DocumentationObservationStage>? stageObserver)
    {
        this.symbolResolver = symbolResolver ?? ResolveSymbols;
        this.stageObserver = stageObserver;
    }

    public ObservedRepositorySession Observe(
        ClassifiedRepositorySession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return ObservedRepositorySession.Bind(
            session,
            ObserveCore(session, cancellationToken));
    }

    private DocumentationObservationOutcome ObserveCore(
        ClassifiedRepositorySession session,
        CancellationToken cancellationToken)
    {
        if (!session.IsBoundToClassificationSession
            || session.Classification.Status != ClassificationRunStatus.Success
            || session.Classification.ClassificationSet is not { } classifications)
        {
            return DocumentationObservationOutcome.Failure();
        }

        try
        {
            ObserveStage(
                DocumentationObservationStage.ContextBinding,
                cancellationToken);
            var projects = session.RepositorySession.Projects
                .GroupBy(project => project.CompilationContextRef, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Single(),
                    StringComparer.Ordinal);
            var cache = new ObserverCache();
            var buffer = new DocumentationObservationCandidateBuffer(classifications);
            var supportedTargets = classifications.Targets
                .Where(target => target.SupportStatus == SupportStatus.Supported)
                .ToDictionary(target => target.SymbolRef);

            foreach (var target in supportedTargets.Values
                .OrderBy(target => SubjectKey(target.SymbolRef), StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var authorities = ObserveAuthorities(
                    target.SymbolRef,
                    null,
                    null,
                    target.Traits.Contains(SymbolTrait.Partial),
                    projects,
                    cache,
                    cancellationToken);
                if (authorities.Status == AuthorityResolutionStatus.Unrepresentable)
                {
                    return DocumentationObservationOutcome.Failure();
                }

                buffer.AddTarget(target, authorities.Complete, authorities.Declarations);
            }

            foreach (var component in classifications.Components
                .Where(IsObservableComponent)
                .OrderBy(
                    component => SubjectKey(component.ParentSymbolRef)
                        + "\u001f"
                        + ClassificationVocabulary.GetId(component.ComponentKind)
                        + "\u001f"
                        + component.Identity,
                    StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!supportedTargets.TryGetValue(
                    component.ParentSymbolRef,
                    out var parentTarget))
                {
                    return DocumentationObservationOutcome.Failure();
                }

                var authorities = ObserveAuthorities(
                    component.ParentSymbolRef,
                    component.ComponentKind,
                    component.Identity,
                    parentTarget.Traits.Contains(SymbolTrait.Partial),
                    projects,
                    cache,
                    cancellationToken);
                if (authorities.Status == AuthorityResolutionStatus.Unrepresentable)
                {
                    return DocumentationObservationOutcome.Failure();
                }

                buffer.AddComponent(
                    component,
                    authorities.Complete,
                    authorities.Declarations);
            }

            ObserveStage(
                DocumentationObservationStage.CoreNormalization,
                cancellationToken);
            var outcome = buffer.Normalize(cancellationToken: cancellationToken);
            ObserveStage(
                DocumentationObservationStage.TerminalConstruction,
                cancellationToken);
            return outcome;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return DocumentationObservationOutcome.Cancelled();
        }
        catch (Exception)
        {
            return DocumentationObservationOutcome.Failure();
        }
    }

    private AuthorityResult ObserveAuthorities(
        SymbolRef symbolRef,
        ComponentKind? componentKind,
        string? componentIdentity,
        bool classifiedPartial,
        IReadOnlyDictionary<string, LoadedProject> projects,
        ObserverCache cache,
        CancellationToken cancellationToken)
    {
        if (!projects.TryGetValue(symbolRef.CompilationContextRef, out var project))
        {
            return AuthorityResult.Unrepresentable;
        }

        ObserveStage(
            DocumentationObservationStage.SymbolBinding,
            cancellationToken);
        var symbols = cache.GetSymbols(
                symbolRef.DocumentationCommentId,
                project.Compilation,
                symbolResolver,
                cancellationToken)
            .Select(CanonicalPartialMember)
            .Distinct(SymbolEqualityComparer.Default)
            .ToImmutableArray();
        if (symbols.Length != 1)
        {
            return AuthorityResult.Unrepresentable;
        }

        var symbol = symbols[0];
        var definition = CanonicalPartialMember(symbol);
        var implementation = GetPartialImplementation(definition);
        if (implementation is not null)
        {
            var implementing = ReadDeclarations(
                project,
                implementation,
                DocumentationAuthorityRole.PartialMemberImplementing,
                componentKind,
                componentIdentity,
                cache,
                cancellationToken);
            if (!implementing.Complete
                || implementing.Declarations.Any(HasAttachedDocumentation))
            {
                return implementing;
            }

            return ReadDeclarations(
                project,
                definition,
                DocumentationAuthorityRole.PartialMemberDefiningFallback,
                componentKind,
                componentIdentity,
                cache,
                cancellationToken);
        }

        var role = definition is INamedTypeSymbol
            && classifiedPartial
            ? DocumentationAuthorityRole.PartialTypePart
            : DocumentationAuthorityRole.Ordinary;
        return ReadDeclarations(
            project,
            definition,
            role,
            componentKind,
            componentIdentity,
            cache,
            cancellationToken);
    }

    private AuthorityResult ReadDeclarations(
        LoadedProject project,
        ISymbol symbol,
        DocumentationAuthorityRole role,
        ComponentKind? componentKind,
        string? componentIdentity,
        ObserverCache cache,
        CancellationToken cancellationToken)
    {
        var complete = true;
        var declarations = new List<DocumentationDeclarationInput>();
        var references = symbol.DeclaringSyntaxReferences
            .OrderBy(reference => reference.SyntaxTree.FilePath, StringComparer.Ordinal)
            .ThenBy(reference => reference.Span.Start)
            .ThenBy(reference => reference.Span.Length)
            .ToArray();
        if (references.Length == 0)
        {
            return AuthorityResult.Unrepresentable;
        }

        foreach (var reference in references)
        {
            ObserveStage(
                DocumentationObservationStage.DeclarationEnumeration,
                cancellationToken);
            var syntax = reference.GetSyntax(cancellationToken);
            var owner = GetDocumentationOwner(syntax);
            if (owner is null)
            {
                return AuthorityResult.Unrepresentable;
            }

            if (!project.SourceTrees.TryGetValue(owner.SyntaxTree, out var loadedSource))
            {
                complete = false;
                continue;
            }

            ObserveStage(
                DocumentationObservationStage.SourceAccess,
                cancellationToken);
            var sourceSnapshot = cache.GetSourceSnapshot(
                owner.SyntaxTree,
                cancellationToken);
            var sourceText = sourceSnapshot.Text;
            cancellationToken.ThrowIfCancellationRequested();
            var sourceSha256 = sourceSnapshot.Sha256;
            cancellationToken.ThrowIfCancellationRequested();
            var sourceFactory = CreateSource(
                project,
                loadedSource,
                sourceText,
                sourceSha256);

            ObserveStage(
                DocumentationObservationStage.TriviaExtraction,
                cancellationToken);
            var directBlock = cache.GetDirectBlock(owner, cancellationToken);
            var leading = directBlock.Leading;
            var attached = directBlock.Attached;
            var analysis = directBlock.Analysis;
            ObserveStage(
                DocumentationObservationStage.XmlAnalysis,
                cancellationToken);
            var componentLocalName = GetComponentLocalName(
                owner,
                componentKind,
                componentIdentity);
            if (componentKind is ComponentKind.Parameter
                    or ComponentKind.TypeParameter
                && componentLocalName is null)
            {
                return AuthorityResult.Unrepresentable;
            }

            DocumentationComponentMatch? componentMatch = componentKind is null
                    || analysis.BlockState == DocumentationBlockState.Malformed
                ? null
                : AnalyzeComponent(
                    attached,
                    componentKind.Value,
                    componentLocalName);
            var declarationText = owner.ToFullString();
            var leadingText = leading.ToFullString();
            Utf16Span? documentationSpan = attached.IsDefaultOrEmpty
                ? null
                : DocumentationObservationInput.Span(
                    attached.Min(trivia => trivia.FullSpan.Start),
                    attached.Max(trivia => trivia.FullSpan.End));
            var documentationText = documentationSpan is { } actualDocumentationSpan
                ? sourceText[
                    actualDocumentationSpan.Start..actualDocumentationSpan.End]
                : null;
            var declarationSpan = DocumentationObservationInput.Span(
                owner.FullSpan.Start,
                owner.FullSpan.End);
            var leadingSpan = leading.Count == 0
                ? DocumentationObservationInput.Span(
                    owner.FullSpan.Start,
                    owner.FullSpan.Start)
                : DocumentationObservationInput.Span(
                    leading.FullSpan.Start,
                    leading.FullSpan.End);
            var declarationId = "decl." + DomainSeparatedHash(
                "contract-scribe/documentation-declaration/v1",
                project.ProjectIdentity,
                sourceFactory.IdentityKey,
                declarationSpan.Start.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                declarationSpan.End.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                Sha256(declarationText));
            cancellationToken.ThrowIfCancellationRequested();

            declarations.Add(sourceFactory.Create(
                declarationId,
                role,
                declarationSpan,
                declarationText,
                leadingSpan,
                leadingText,
                documentationSpan,
                documentationText,
                analysis.BlockState,
                analysis.ParentSubstantive,
                componentLocalName,
                componentMatch));
        }

        return complete
            ? AuthorityResult.Resolved(declarations.ToImmutableArray())
            : AuthorityResult.SourceUnavailable(declarations.ToImmutableArray());
    }

    private static SourceFactory CreateSource(
        LoadedProject project,
        LoadedSourceTree loadedSource,
        string actualSourceText,
        string actualSourceSha256)
    {
        switch (loadedSource.Kind)
        {
            case LoadedSourceKind.Repository
                when loadedSource.RepositoryPath is { } path:
                return SourceFactory.Repository(
                    project.ProjectIdentity,
                    path.Replace('\\', '/'),
                    actualSourceSha256);
            case LoadedSourceKind.SourceGenerator or LoadedSourceKind.ToolGenerated
                when loadedSource.GeneratedSource is { } generated
                && string.Equals(
                    generated.ProjectIdentity,
                    project.ProjectIdentity,
                    StringComparison.Ordinal)
                && string.Equals(
                    generated.CompilationContextRef,
                    project.CompilationContextRef,
                    StringComparison.Ordinal)
                && string.Equals(
                    generated.SourceSha256,
                    actualSourceSha256,
                    StringComparison.Ordinal)
                && string.Equals(
                    generated.SourceText,
                    actualSourceText,
                    StringComparison.Ordinal):
                return SourceFactory.Generated(
                    project.ProjectIdentity,
                    loadedSource.Kind == LoadedSourceKind.SourceGenerator
                        ? DocumentationSourceKind.SourceGenerator
                        : DocumentationSourceKind.ToolGenerated,
                    generated.ProducerId,
                    generated.OutputId,
                    actualSourceSha256);
            default:
                throw new InvalidOperationException(
                    "The loaded source identity does not match its active syntax tree.");
        }
    }

    private static SyntaxNode? GetDocumentationOwner(SyntaxNode syntax) =>
        syntax switch
        {
            VariableDeclaratorSyntax variable =>
                variable.FirstAncestorOrSelf<BaseFieldDeclarationSyntax>(),
            ParameterSyntax parameter
                when parameter.Parent?.Parent is TypeDeclarationSyntax type =>
                type,
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

    private static DocumentationAnalysis Analyze(
        ImmutableArray<SyntaxTrivia> attached)
    {
        if (attached.IsDefaultOrEmpty)
        {
            return new DocumentationAnalysis(
                DocumentationBlockState.NoBlock,
                false);
        }

        var structures = attached
            .Select(trivia => (DocumentationCommentTriviaSyntax)trivia.GetStructure()!)
            .ToImmutableArray();
        var malformed = structures.Any(structure => structure.ContainsDiagnostics);
        var substantive = structures.Any(HasSubstantiveContent);
        if (malformed)
        {
            return new DocumentationAnalysis(
                DocumentationBlockState.Malformed,
                substantive || HasNonWhitespaceDocumentationText(attached));
        }

        return new DocumentationAnalysis(
            substantive
                ? DocumentationBlockState.WellFormed
                : DocumentationBlockState.WhitespaceOnly,
            substantive);
    }

    private static bool HasSubstantiveContent(
        DocumentationCommentTriviaSyntax documentation) =>
        documentation.Content.Any(node => node switch
        {
            XmlElementSyntax => true,
            XmlEmptyElementSyntax => true,
            XmlCDataSectionSyntax cdata => cdata.TextTokens.Any(
                token => !string.IsNullOrWhiteSpace(token.ValueText)),
            XmlTextSyntax text => text.TextTokens.Any(token =>
                !token.IsKind(SyntaxKind.XmlTextLiteralNewLineToken)
                && !string.IsNullOrWhiteSpace(token.ValueText)),
            XmlCommentSyntax or XmlProcessingInstructionSyntax => false,
            _ => !string.IsNullOrWhiteSpace(node.ToString()),
        });

    private static bool HasNonWhitespaceDocumentationText(
        ImmutableArray<SyntaxTrivia> attached)
    {
        foreach (var documentation in attached
            .Select(trivia => (DocumentationCommentTriviaSyntax)trivia.GetStructure()!))
        {
            foreach (var node in documentation.Content)
            {
                if (node is XmlCommentSyntax or XmlProcessingInstructionSyntax)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(node.ToString()))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static DocumentationComponentMatch AnalyzeComponent(
        ImmutableArray<SyntaxTrivia> attached,
        ComponentKind componentKind,
        string? componentLocalName)
    {
        var expectedTag = componentKind switch
        {
            ComponentKind.Parameter => "param",
            ComponentKind.TypeParameter => "typeparam",
            ComponentKind.Return => "returns",
            ComponentKind.Value => "value",
            _ => throw new ArgumentOutOfRangeException(nameof(componentKind)),
        };
        foreach (var element in attached
            .Select(trivia => (DocumentationCommentTriviaSyntax)trivia.GetStructure()!)
            .SelectMany(documentation => documentation.Content)
            .OfType<XmlElementSyntax>())
        {
            if (element.ContainsDiagnostics
                || element.StartTag.Name.Prefix is not null
                || element.EndTag.Name.Prefix is not null
                || !string.Equals(
                    element.StartTag.Name.LocalName.ValueText,
                    expectedTag,
                    StringComparison.Ordinal)
                || !string.Equals(
                    element.EndTag.Name.LocalName.ValueText,
                    expectedTag,
                    StringComparison.Ordinal)
                || componentKind is ComponentKind.Parameter
                        or ComponentKind.TypeParameter
                    && !HasExactName(element.StartTag.Attributes, componentLocalName!))
            {
                continue;
            }

            if (element.Content.Any(IsSubstantiveComponentContent))
            {
                return DocumentationComponentMatch.Present;
            }
        }

        return DocumentationComponentMatch.Absent;
    }

    private static bool HasExactName(
        SyntaxList<XmlAttributeSyntax> attributes,
        string expectedName) =>
        attributes.OfType<XmlNameAttributeSyntax>().Any(attribute =>
            attribute.Name.Prefix is null
            && string.Equals(
                attribute.Name.LocalName.ValueText,
                "name",
                StringComparison.Ordinal)
            && string.Equals(
                attribute.Identifier.Identifier.ValueText,
                expectedName,
                StringComparison.Ordinal));

    private static bool IsSubstantiveComponentContent(XmlNodeSyntax node) =>
        node switch
        {
            XmlElementSyntax or XmlEmptyElementSyntax => true,
            XmlCDataSectionSyntax cdata => cdata.TextTokens.Any(
                token => !string.IsNullOrWhiteSpace(token.ValueText)),
            XmlTextSyntax text => text.TextTokens.Any(token =>
                !token.IsKind(SyntaxKind.XmlTextLiteralNewLineToken)
                && !string.IsNullOrWhiteSpace(token.ValueText)),
            XmlCommentSyntax or XmlProcessingInstructionSyntax => false,
            _ => !string.IsNullOrWhiteSpace(node.ToString()),
        };

    private static string? GetComponentLocalName(
        SyntaxNode owner,
        ComponentKind? componentKind,
        string? componentIdentity)
    {
        if (componentKind is null
            || componentKind is ComponentKind.Return or ComponentKind.Value)
        {
            return null;
        }

        if (!TryGetOrdinal(componentIdentity, out var ordinal))
        {
            return null;
        }

        if (componentKind == ComponentKind.TypeParameter)
        {
            var typeParameterList = owner switch
            {
                TypeDeclarationSyntax type => type.TypeParameterList?.Parameters,
                DelegateDeclarationSyntax @delegate =>
                    @delegate.TypeParameterList?.Parameters,
                MethodDeclarationSyntax method => method.TypeParameterList?.Parameters,
                _ => null,
            };
            return typeParameterList is { } typeParameters
                && ordinal < typeParameters.Count
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
        return valueParameters is { } parameters
            && ordinal < parameters.Count
                ? parameters[ordinal].Identifier.ValueText
                : null;
    }

    private static bool TryGetOrdinal(string? identity, out int ordinal)
    {
        ordinal = -1;
        var separator = identity?.LastIndexOf('/') ?? -1;
        return separator >= 0
            && int.TryParse(
                identity![(separator + 1)..],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out ordinal)
            && ordinal >= 0;
    }

    private static bool HasAttachedDocumentation(
        DocumentationDeclarationInput declaration) =>
        declaration.BlockState != DocumentationBlockState.NoBlock;

    internal static bool IsObservableComponent(ComponentClassification component) =>
        component.SupportStatus == SupportStatus.Supported
        && component.ComponentKind is ComponentKind.Parameter
            or ComponentKind.TypeParameter
            or ComponentKind.Return
            or ComponentKind.Value;

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
            IMethodSymbol method => method.PartialImplementationPart,
            IPropertySymbol property => property.PartialImplementationPart,
            IEventSymbol @event => @event.PartialImplementationPart,
            _ => null,
        };

    private static string SubjectKey(SymbolRef symbolRef) =>
        symbolRef.CompilationContextRef
        + "\u001f"
        + symbolRef.DocumentationCommentId;

    private static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(text)))
            .ToLowerInvariant();

    private static string DomainSeparatedHash(
        string domain,
        params string[] fields)
    {
        var preimage = new StringBuilder(domain);
        foreach (var field in fields)
        {
            preimage.Append('\n');
            preimage.Append(
                field.Length.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            preimage.Append(':');
            preimage.Append(field);
        }

        return Sha256(preimage.ToString());
    }

    private void ObserveStage(
        DocumentationObservationStage stage,
        CancellationToken cancellationToken)
    {
        stageObserver?.Invoke(stage);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static ImmutableArray<ISymbol> ResolveSymbols(
        string documentationCommentId,
        Compilation compilation) =>
        DocumentationCommentId.GetSymbolsForDeclarationId(
                documentationCommentId,
                compilation)
            .ToImmutableArray();

    private enum AuthorityResolutionStatus
    {
        Resolved,
        SourceUnavailable,
        Unrepresentable,
    }

    private sealed record AuthorityResult(
        AuthorityResolutionStatus Status,
        ImmutableArray<DocumentationDeclarationInput> Declarations)
    {
        public bool Complete => Status == AuthorityResolutionStatus.Resolved;

        public static AuthorityResult Unrepresentable { get; } =
            new(AuthorityResolutionStatus.Unrepresentable, []);

        public static AuthorityResult Resolved(
            ImmutableArray<DocumentationDeclarationInput> declarations) =>
            new(AuthorityResolutionStatus.Resolved, declarations);

        public static AuthorityResult SourceUnavailable(
            ImmutableArray<DocumentationDeclarationInput> declarations) =>
            new(AuthorityResolutionStatus.SourceUnavailable, declarations);
    }

    private sealed record DocumentationAnalysis(
        DocumentationBlockState BlockState,
        bool ParentSubstantive);

    private sealed record SourceSnapshot(string Text, string Sha256);

    private sealed record DirectBlockSnapshot(
        SyntaxTriviaList Leading,
        ImmutableArray<SyntaxTrivia> Attached,
        DocumentationAnalysis Analysis);

    private readonly record struct OwnerKey(
        SyntaxTree SyntaxTree,
        int Start,
        int Length,
        int RawKind);

    private sealed class ObserverCache
    {
        private readonly Dictionary<SyntaxTree, SourceSnapshot> sources =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<OwnerKey, DirectBlockSnapshot> blocks = [];
        private readonly Dictionary<
            (Compilation Compilation, string DocumentationCommentId),
            ImmutableArray<ISymbol>> symbols = [];

        public ImmutableArray<ISymbol> GetSymbols(
            string documentationCommentId,
            Compilation compilation,
            Func<string, Compilation, ImmutableArray<ISymbol>> resolver,
            CancellationToken cancellationToken)
        {
            var key = (compilation, documentationCommentId);
            if (symbols.TryGetValue(key, out var cached))
            {
                return cached;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var resolved = resolver(documentationCommentId, compilation);
            cancellationToken.ThrowIfCancellationRequested();
            symbols.Add(key, resolved);
            return resolved;
        }

        public SourceSnapshot GetSourceSnapshot(
            SyntaxTree syntaxTree,
            CancellationToken cancellationToken)
        {
            if (sources.TryGetValue(syntaxTree, out var cached))
            {
                return cached;
            }

            var text = syntaxTree.GetText(cancellationToken).ToString();
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = new SourceSnapshot(text, Sha256(text));
            sources.Add(syntaxTree, snapshot);
            return snapshot;
        }

        public DirectBlockSnapshot GetDirectBlock(
            SyntaxNode owner,
            CancellationToken cancellationToken)
        {
            var key = new OwnerKey(
                owner.SyntaxTree,
                owner.FullSpan.Start,
                owner.FullSpan.Length,
                owner.RawKind);
            if (blocks.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var leading = owner.GetLeadingTrivia();
            cancellationToken.ThrowIfCancellationRequested();
            var attached = GetAttachedDocumentation(leading);
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = new DirectBlockSnapshot(
                leading,
                attached,
                Analyze(attached));
            cancellationToken.ThrowIfCancellationRequested();
            blocks.Add(key, snapshot);
            return snapshot;
        }
    }

    private sealed class SourceFactory
    {
        private readonly Func<
            string,
            DocumentationAuthorityRole,
            Utf16Span,
            string,
            Utf16Span,
            string,
            Utf16Span?,
            string?,
            DocumentationBlockState,
            bool,
            string?,
            DocumentationComponentMatch?,
            DocumentationDeclarationInput> create;

        private SourceFactory(
            string identityKey,
            Func<
                string,
                DocumentationAuthorityRole,
                Utf16Span,
                string,
                Utf16Span,
                string,
                Utf16Span?,
                string?,
                DocumentationBlockState,
                bool,
                string?,
                DocumentationComponentMatch?,
                DocumentationDeclarationInput> create)
        {
            IdentityKey = identityKey;
            this.create = create;
        }

        public string IdentityKey { get; }

        public DocumentationDeclarationInput Create(
            string declarationId,
            DocumentationAuthorityRole role,
            Utf16Span declarationSpan,
            string declarationText,
            Utf16Span leadingTriviaSpan,
            string leadingTriviaText,
            Utf16Span? documentationSpan,
            string? documentationText,
            DocumentationBlockState blockState,
            bool parentSubstantive,
            string? componentLocalName,
            DocumentationComponentMatch? componentMatch) =>
            create(
                declarationId,
                role,
                declarationSpan,
                declarationText,
                leadingTriviaSpan,
                leadingTriviaText,
                documentationSpan,
                documentationText,
                blockState,
                parentSubstantive,
                componentLocalName,
                componentMatch);

        public static SourceFactory Repository(
            string projectIdentity,
            string path,
            string sourceSha256) =>
            new(
                "repository\u001f" + projectIdentity + "\u001f" + path,
                (
                    declarationId,
                    role,
                    declarationSpan,
                    declarationText,
                    leadingTriviaSpan,
                    leadingTriviaText,
                    documentationSpan,
                    documentationText,
                    blockState,
                    parentSubstantive,
                    componentLocalName,
                    componentMatch) =>
                    DocumentationObservationInput.RepositoryDeclaration(
                        declarationId,
                        role,
                        projectIdentity,
                        path,
                        sourceSha256,
                        declarationSpan,
                        declarationText,
                        leadingTriviaSpan,
                        leadingTriviaText,
                        documentationSpan,
                        documentationText,
                        blockState,
                        parentSubstantive,
                        componentLocalName,
                        componentMatch));

        public static SourceFactory Generated(
            string projectIdentity,
            DocumentationSourceKind sourceKind,
            string producerId,
            string outputId,
            string sourceSha256) =>
            new(
                "generated\u001f"
                    + projectIdentity
                    + "\u001f"
                    + producerId
                    + "\u001f"
                    + outputId,
                (
                    declarationId,
                    role,
                    declarationSpan,
                    declarationText,
                    leadingTriviaSpan,
                    leadingTriviaText,
                    documentationSpan,
                    documentationText,
                    blockState,
                    parentSubstantive,
                    componentLocalName,
                    componentMatch) =>
                    DocumentationObservationInput.GeneratedDeclaration(
                        declarationId,
                        role,
                        projectIdentity,
                        sourceKind,
                        producerId,
                        outputId,
                        sourceSha256,
                        declarationSpan,
                        declarationText,
                        leadingTriviaSpan,
                        leadingTriviaText,
                        documentationSpan,
                        documentationText,
                        blockState,
                        parentSubstantive,
                        componentLocalName,
                        componentMatch));
    }
}
