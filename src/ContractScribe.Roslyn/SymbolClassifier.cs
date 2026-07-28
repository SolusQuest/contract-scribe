using System.Collections.Immutable;
using ContractScribe.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ContractScribe.Roslyn;

public sealed class SymbolClassifier
{
    private readonly Func<ISymbol, string?> documentationId;
    private readonly RelationEndpointResolver relationEndpointResolver;
    private readonly Action<ClassificationStage>? observer;
    private readonly Func<ClassificationCandidateBatch, ClassificationCandidateBatch>? beforeNormalization;

    public SymbolClassifier()
        : this(null, null, null, null)
    {
    }

    internal SymbolClassifier(
        Func<ISymbol, string?>? documentationId,
        RelationEndpointResolver? relationEndpointResolver,
        Action<ClassificationStage>? observer,
        Func<ClassificationCandidateBatch, ClassificationCandidateBatch>? beforeNormalization)
    {
        this.documentationId = documentationId ?? DefaultDocumentationId;
        this.relationEndpointResolver = relationEndpointResolver ?? DefaultRelationEndpoint;
        this.observer = observer;
        this.beforeNormalization = beforeNormalization;
    }

    public ClassificationOutcome Classify(
        LoadedRepositorySession session,
        TargetProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!Enum.IsDefined(profile))
        {
            throw new ArgumentOutOfRangeException(
                nameof(profile),
                profile,
                "Classification requires a validated closed target profile.");
        }

        var diagnostics = new List<ClassificationDiagnostic>();
        try
        {
            var targetCandidates = new List<TargetClassificationCandidate>();
            var componentCandidates = new List<ComponentClassificationCandidate>();
            var relations = new List<RelationObservation>();
            foreach (var project in session.Projects
                .Where(project => project.Role == LoadedProjectRole.AuditRoot)
                .OrderBy(project => project.CompilationContextRef, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                DiscoverTargetsAndComponents(
                    project,
                    profile,
                    targetCandidates,
                    componentCandidates,
                    cancellationToken);
            }

            Observe(ClassificationStage.TargetDiscovery, cancellationToken);
            Observe(ClassificationStage.ComponentDiscovery, cancellationToken);
            foreach (var project in session.Projects
                .Where(project => project.Role == LoadedProjectRole.AuditRoot)
                .OrderBy(project => project.CompilationContextRef, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                DiscoverRelations(
                    project,
                    profile,
                    relations,
                    diagnostics,
                    cancellationToken);
            }

            Observe(ClassificationStage.RelationDiscovery, cancellationToken);
            var candidates = new ClassificationCandidateBatch(
                targetCandidates,
                componentCandidates,
                relations,
                []);
            candidates = beforeNormalization?.Invoke(candidates) ?? candidates;
            Observe(ClassificationStage.CandidateBufferingComplete, cancellationToken);
            var set = ClassificationNormalization.Normalize(
                profile,
                candidates,
                cancellationToken);
            Observe(ClassificationStage.TerminalValidation, cancellationToken);
            return ClassificationOutcome.Success(set, NormalizeDiagnostics(diagnostics));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ClassificationOutcome.Cancelled(NormalizeDiagnostics(diagnostics));
        }
        catch (ClassificationUnrepresentableException)
        {
            return ClassificationOutcome.Failure(NormalizeDiagnostics(diagnostics));
        }
    }

    private void DiscoverTargetsAndComponents(
        LoadedProject project,
        TargetProfile profile,
        List<TargetClassificationCandidate> targets,
        List<ComponentClassificationCandidate> components,
        CancellationToken cancellationToken)
    {
        foreach (var symbol in EnumerateSymbols(project.Compilation.Assembly.GlobalNamespace))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsIndependentSourceCandidate(symbol)
                || !IsSelectedTargetSurface(symbol, profile))
            {
                continue;
            }

            var original = symbol.OriginalDefinition;
            var provenance = ClassifyProvenance(project, original);
            var kind = ClassifyPrimaryKind(original);
            var candidate = new TargetClassificationCandidate(
                project.CompilationContextRef,
                documentationId(original),
                kind ?? PrimarySymbolKind.Unknown,
                ClassifyTraits(original, cancellationToken),
                provenance.Origin,
                provenance.Locators,
                provenance.GeneratedProvenanceAvailable);
            targets.Add(candidate);
            if (!CanHaveComponents(candidate))
            {
                continue;
            }

            var parent = new SymbolRef(
                candidate.CompilationContextRef,
                candidate.DocumentationCommentId!);
            AddComponents(
                original,
                parent,
                candidate.Origin,
                components,
                cancellationToken);
        }
    }

    private void DiscoverRelations(
        LoadedProject project,
        TargetProfile profile,
        List<RelationObservation> relations,
        List<ClassificationDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        foreach (var type in EnumerateTypes(project.Compilation.Assembly.GlobalNamespace))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var containingTypeSelected = IsIndependentSourceCandidate(type)
                && IsSelectedTargetSurface(type, profile);
            foreach (var member in type.GetMembers().Where(IsRelationMember))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsIndependentSourceCandidate(member)
                    && IsSelectedTargetSurface(member, profile)
                    && GetOverriddenMember(member) is { } overridden)
                {
                    TryAddRelation(
                        RelationKind.Overrides,
                        member,
                        overridden.OriginalDefinition,
                        project.CompilationContextRef,
                        relations,
                        diagnostics);
                }

                if (!containingTypeSelected)
                {
                    continue;
                }

                foreach (var implemented in GetExplicitInterfaceMembers(member))
                {
                    if (!IsSelectedTargetSurface(implemented.OriginalDefinition, profile))
                    {
                        continue;
                    }

                    TryAddRelation(
                        RelationKind.ExplicitInterfaceImplementation,
                        member,
                        implemented.OriginalDefinition,
                        project.CompilationContextRef,
                        relations,
                        diagnostics);
                }
            }

            if (type.TypeKind == TypeKind.Interface && containingTypeSelected)
            {
                foreach (var inherited in type.AllInterfaces
                    .SelectMany(@interface => @interface.GetMembers())
                    .Where(IsRelationMember)
                    .Where(member => IsSelectedTargetSurface(member.OriginalDefinition, profile)))
                {
                    TryAddRelation(
                        RelationKind.InheritedInterfaceMember,
                        type,
                        inherited.OriginalDefinition,
                        project.CompilationContextRef,
                        relations,
                        diagnostics);
                }
            }

            if (type.TypeKind == TypeKind.Interface || !containingTypeSelected)
            {
                continue;
            }

            foreach (var interfaceMember in type.AllInterfaces
                .SelectMany(@interface => @interface.GetMembers())
                .Where(IsRelationMember)
                .Where(member => IsSelectedTargetSurface(member.OriginalDefinition, profile)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var implementation = type.FindImplementationForInterfaceMember(interfaceMember);
                if (implementation is null
                    || !implementation.Locations.Any(location => location.IsInSource)
                    || !IsIndependentSourceCandidate(implementation)
                    || !IsSelectedTargetSurface(implementation, profile)
                    || GetExplicitInterfaceMembers(implementation).Any())
                {
                    continue;
                }

                TryAddRelation(
                    RelationKind.ImplicitInterfaceImplementation,
                    implementation,
                    interfaceMember.OriginalDefinition,
                    project.CompilationContextRef,
                    relations,
                    diagnostics);
            }
        }
    }

    private void TryAddRelation(
        RelationKind kind,
        ISymbol source,
        ISymbol target,
        string context,
        List<RelationObservation> relations,
        List<ClassificationDiagnostic> diagnostics)
    {
        var sourceResolution = relationEndpointResolver(kind, source, false, context);
        var targetResolution = relationEndpointResolver(kind, target, true, context);
        if (sourceResolution.Status != RelationEndpointStatus.Available
            || sourceResolution.SymbolRef is null
            || targetResolution.Status != RelationEndpointStatus.Available
            || targetResolution.SymbolRef is null)
        {
            AddEndpointDiagnostic(sourceResolution.Status, diagnostics);
            AddEndpointDiagnostic(targetResolution.Status, diagnostics);
            return;
        }

        relations.Add(new RelationObservation(
            kind,
            sourceResolution.SymbolRef.Value,
            targetResolution.SymbolRef.Value));
    }

    private static void AddEndpointDiagnostic(
        RelationEndpointStatus status,
        List<ClassificationDiagnostic> diagnostics)
    {
        var code = status switch
        {
            RelationEndpointStatus.Ambiguous => "classification.relation-endpoint-ambiguous",
            RelationEndpointStatus.Unavailable => "classification.relation-endpoint-unavailable",
            _ => null,
        };
        if (code is not null)
        {
            diagnostics.Add(new ClassificationDiagnostic("relation", code, "warning"));
        }
    }

    private static void AddComponents(
        ISymbol symbol,
        SymbolRef parent,
        ClassificationOrigin origin,
        List<ComponentClassificationCandidate> components,
        CancellationToken cancellationToken)
    {
        void Add(ComponentKind kind, string identity, ClassificationOrigin? componentOrigin = null) =>
            components.Add(new ComponentClassificationCandidate(
                parent,
                kind,
                identity,
                componentOrigin ?? origin));

        if (symbol is IMethodSymbol method)
        {
            foreach (var parameter in method.Parameters)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Add(ComponentKind.Parameter, $"parameter/{parameter.Ordinal}");
            }

            foreach (var typeParameter in method.TypeParameters)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Add(ComponentKind.TypeParameter, $"type-parameter/{typeParameter.Ordinal}");
            }

            if (method.MethodKind is MethodKind.Ordinary
                    or MethodKind.UserDefinedOperator
                    or MethodKind.Conversion
                && !method.ReturnsVoid)
            {
                Add(ComponentKind.Return, "return");
            }
        }

        if (symbol is IPropertySymbol property)
        {
            foreach (var parameter in property.Parameters)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Add(ComponentKind.Parameter, $"parameter/{parameter.Ordinal}");
            }

            Add(ComponentKind.Value, "value");
            if (property.GetMethod is not null)
            {
                Add(ComponentKind.AccessorGet, "accessor/get");
            }

            if (property.SetMethod is { } setter)
            {
                Add(
                    setter.IsInitOnly ? ComponentKind.AccessorInit : ComponentKind.AccessorSet,
                    setter.IsInitOnly ? "accessor/init" : "accessor/set");
            }

            if (property.ContainingType.GetMembers()
                .OfType<IFieldSymbol>()
                .Any(field => SymbolEqualityComparer.Default.Equals(
                    field.AssociatedSymbol,
                    property)))
            {
                Add(ComponentKind.BackingField, "backing-field");
            }
        }

        if (symbol is IEventSymbol @event)
        {
            if (@event.AddMethod is not null)
            {
                Add(ComponentKind.AccessorAdd, "accessor/add");
            }

            if (@event.RemoveMethod is not null)
            {
                Add(ComponentKind.AccessorRemove, "accessor/remove");
            }

            if (@event.ContainingType.GetMembers()
                .OfType<IFieldSymbol>()
                .Any(field => SymbolEqualityComparer.Default.Equals(
                    field.AssociatedSymbol,
                    @event)))
            {
                Add(ComponentKind.BackingField, "backing-field");
            }
        }

        if (symbol is not INamedTypeSymbol type)
        {
            return;
        }

        foreach (var typeParameter in type.TypeParameters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Add(ComponentKind.TypeParameter, $"type-parameter/{typeParameter.Ordinal}");
        }

        if (type.IsRecord)
        {
            foreach (var positionalProperty in type.GetMembers()
                .OfType<IPropertySymbol>()
                .Select(candidateProperty => (
                    Property: candidateProperty,
                    Parameter: GetRecordPositionalParameter(candidateProperty)))
                .Where(pair => pair.Parameter is not null)
                .OrderBy(pair => pair.Parameter!.Ordinal))
            {
                Add(
                    ComponentKind.SynthesizedRecordPositionalProperty,
                    $"synthesized/record-positional-property/{positionalProperty.Parameter!.Ordinal}",
                    ClassificationOrigin.CompilerSynthesized);
            }
        }

        if (type.TypeKind is TypeKind.Class or TypeKind.Struct)
        {
            foreach (var constructor in type.InstanceConstructors
                .Where(constructor =>
                    constructor.IsImplicitlyDeclared
                    && !IsSourceDeclaredPrimaryConstructor(constructor)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var isCopy = type.IsRecord
                    && constructor.Parameters.Length == 1
                    && SymbolEqualityComparer.Default.Equals(
                        constructor.Parameters[0].Type,
                        type);
                Add(
                    isCopy
                        ? ComponentKind.SynthesizedRecordCopyConstructor
                        : ComponentKind.SynthesizedImplicitConstructor,
                    isCopy
                        ? "synthesized/record-copy-constructor"
                        : "synthesized/implicit-constructor",
                    ClassificationOrigin.CompilerSynthesized);
            }
        }

        if (type.TypeKind != TypeKind.Delegate || type.DelegateInvokeMethod is null)
        {
            return;
        }

        foreach (var parameter in type.DelegateInvokeMethod.Parameters)
        {
            Add(ComponentKind.Parameter, $"parameter/{parameter.Ordinal}");
        }

        if (!type.DelegateInvokeMethod.ReturnsVoid)
        {
            Add(ComponentKind.Return, "return");
        }

        foreach (var delegateMethod in type.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(candidateMethod => candidateMethod.IsImplicitlyDeclared))
        {
            var component = delegateMethod.Name switch
            {
                "Invoke" => (
                    ComponentKind.SynthesizedDelegateInvoke,
                    "synthesized/delegate-invoke"),
                "BeginInvoke" => (
                    ComponentKind.SynthesizedDelegateBeginInvoke,
                    "synthesized/delegate-begin-invoke"),
                "EndInvoke" => (
                    ComponentKind.SynthesizedDelegateEndInvoke,
                    "synthesized/delegate-end-invoke"),
                _ => ((ComponentKind Kind, string Identity)?)null,
            };
            if (component is { } value)
            {
                Add(value.Kind, value.Identity, ClassificationOrigin.CompilerSynthesized);
            }
        }
    }

    private static bool CanHaveComponents(TargetClassificationCandidate candidate) =>
        candidate.DocumentationCommentId is not null
        && candidate.GeneratedProvenanceAvailable
        && candidate.SemanticContextAvailable
        && candidate.PrimaryKind != PrimarySymbolKind.Unknown
        && !candidate.PartialAmbiguous
        && candidate.Origin is ClassificationOrigin.Source
            or ClassificationOrigin.SourceGenerator
            or ClassificationOrigin.ToolGenerated;

    private static ProvenanceResult ClassifyProvenance(
        LoadedProject project,
        ISymbol symbol)
    {
        var origins = new HashSet<ClassificationOrigin>();
        var locators = ImmutableArray.CreateBuilder<CandidateLocator>();
        var available = true;
        foreach (var location in symbol.Locations.Where(location => location.IsInSource))
        {
            if (location.SourceTree is null
                || !project.SourceTrees.TryGetValue(location.SourceTree, out var source))
            {
                available = false;
                continue;
            }

            var span = new Utf16Span(
                location.SourceSpan.Start,
                location.SourceSpan.End);
            switch (source)
            {
                case { Kind: LoadedSourceKind.Repository, RepositoryIdentity: { } path }:
                    origins.Add(ClassificationOrigin.Source);
                    locators.Add(new RepositoryCandidateLocator(path, span));
                    break;
                case
                {
                    Kind: LoadedSourceKind.SourceGenerator,
                    GeneratedSource: { } fact,
                }:
                    origins.Add(ClassificationOrigin.SourceGenerator);
                    locators.Add(new GeneratedSourceCandidateLocator(
                        fact.ProducerId,
                        fact.OutputId,
                        span));
                    break;
                case
                {
                    Kind: LoadedSourceKind.ToolGenerated,
                    GeneratedSource: { } fact,
                }:
                    origins.Add(ClassificationOrigin.ToolGenerated);
                    locators.Add(new ToolGeneratedCandidateLocator(
                        fact.ProducerId,
                        fact.OutputId,
                        span));
                    break;
                default:
                    available = false;
                    break;
            }
        }

        if (!available || origins.Count == 0)
        {
            return new ProvenanceResult(
                ClassificationOrigin.Unknown,
                locators.ToImmutable(),
                false);
        }

        return new ProvenanceResult(
            origins.Count == 1 ? origins.Single() : ClassificationOrigin.Mixed,
            locators.Distinct().ToImmutableArray(),
            true);
    }

    private static PrimarySymbolKind? ClassifyPrimaryKind(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol { TypeKind: TypeKind.Class } => PrimarySymbolKind.Class,
        INamedTypeSymbol { TypeKind: TypeKind.Struct } => PrimarySymbolKind.Struct,
        INamedTypeSymbol { TypeKind: TypeKind.Interface } => PrimarySymbolKind.Interface,
        INamedTypeSymbol { TypeKind: TypeKind.Enum } => PrimarySymbolKind.Enum,
        INamedTypeSymbol { TypeKind: TypeKind.Delegate } => PrimarySymbolKind.Delegate,
        IMethodSymbol { MethodKind: MethodKind.Constructor } => PrimarySymbolKind.Constructor,
        IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator } => PrimarySymbolKind.Operator,
        IMethodSymbol { MethodKind: MethodKind.Conversion } => PrimarySymbolKind.Conversion,
        IMethodSymbol { MethodKind: MethodKind.Destructor } => PrimarySymbolKind.Method,
        IMethodSymbol { MethodKind: MethodKind.Ordinary } => PrimarySymbolKind.Method,
        IMethodSymbol { MethodKind: MethodKind.ExplicitInterfaceImplementation } =>
            PrimarySymbolKind.Method,
        IPropertySymbol { IsIndexer: true } => PrimarySymbolKind.Indexer,
        IPropertySymbol => PrimarySymbolKind.Property,
        IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } => PrimarySymbolKind.EnumMember,
        IFieldSymbol => PrimarySymbolKind.Field,
        IEventSymbol => PrimarySymbolKind.Event,
        _ => null,
    };

    private static ImmutableArray<SymbolTrait> ClassifyTraits(
        ISymbol symbol,
        CancellationToken cancellationToken)
    {
        var traits = new HashSet<SymbolTrait>();
        if (symbol is INamedTypeSymbol type)
        {
            if (type.TypeParameters.Length > 0)
            {
                traits.Add(SymbolTrait.Generic);
            }

            if (type.IsRecord && type.TypeKind == TypeKind.Class)
            {
                traits.Add(SymbolTrait.RecordClass);
            }

            if (type.IsRecord && type.TypeKind == TypeKind.Struct)
            {
                traits.Add(SymbolTrait.RecordStruct);
            }

            if (type.IsRefLikeType)
            {
                traits.Add(SymbolTrait.RefStruct);
            }
        }

        if (symbol is IMethodSymbol method)
        {
            if (method.TypeParameters.Length > 0)
            {
                traits.Add(SymbolTrait.Generic);
            }

            if (method.IsExtensionMethod)
            {
                traits.Add(SymbolTrait.Extension);
            }

            if (method.IsAsync)
            {
                traits.Add(SymbolTrait.Async);
            }

            if (method.DeclaringSyntaxReferences.Any(reference =>
                reference.GetSyntax(cancellationToken) is MethodDeclarationSyntax declaration
                && declaration.DescendantNodes()
                    .OfType<YieldStatementSyntax>()
                    .Any()))
            {
                traits.Add(SymbolTrait.Iterator);
            }
        }

        if (symbol.IsStatic)
        {
            traits.Add(SymbolTrait.Static);
        }

        if (symbol.IsAbstract)
        {
            traits.Add(SymbolTrait.Abstract);
        }

        if (symbol.IsVirtual)
        {
            traits.Add(SymbolTrait.Virtual);
        }

        if (symbol.IsSealed)
        {
            traits.Add(SymbolTrait.Sealed);
        }

        if (symbol is IPropertySymbol { IsRequired: true }
            or IFieldSymbol { IsRequired: true })
        {
            traits.Add(SymbolTrait.Required);
        }

        if (symbol is IPropertySymbol { SetMethod.IsInitOnly: true })
        {
            traits.Add(SymbolTrait.InitOnly);
        }

        if (IsPartialDeclaration(symbol, cancellationToken))
        {
            traits.Add(SymbolTrait.Partial);
        }

        return traits
            .OrderBy(ClassificationVocabulary.GetId, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static bool IsIndependentSourceCandidate(ISymbol symbol)
    {
        if (!symbol.Locations.Any(location => location.IsInSource)
            || symbol is IMethodSymbol { MethodKind: MethodKind.StaticConstructor }
            || GetExplicitInterfaceMembers(symbol).Any()
            || symbol is IPropertySymbol property && IsRecordPositionalProperty(property))
        {
            return false;
        }

        if (symbol is IMethodSymbol method
            && method.MethodKind is not (
                MethodKind.Constructor
                or MethodKind.Ordinary
                or MethodKind.Destructor
                or MethodKind.UserDefinedOperator
                or MethodKind.Conversion))
        {
            return false;
        }

        if (!symbol.IsImplicitlyDeclared)
        {
            return true;
        }

        return symbol is IMethodSymbol constructor
            && IsSourceDeclaredPrimaryConstructor(constructor);
    }

    private static bool IsSourceDeclaredPrimaryConstructor(IMethodSymbol method) =>
        method.MethodKind == MethodKind.Constructor
        && method.DeclaringSyntaxReferences.Any(reference =>
            reference.GetSyntax() is ClassDeclarationSyntax { ParameterList: not null }
                or StructDeclarationSyntax { ParameterList: not null });

    private static bool IsSelectedTargetSurface(ISymbol symbol, TargetProfile profile)
    {
        if (symbol is INamedTypeSymbol type)
        {
            return IsReachableType(type, profile);
        }

        if (symbol.ContainingType is not { } containing
            || !IsReachableType(containing, profile))
        {
            return false;
        }

        if (symbol is IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum })
        {
            return true;
        }

        if (symbol is IMethodSymbol method
            && IsSourceDeclaredPrimaryConstructor(method)
            && method.DeclaredAccessibility == Accessibility.NotApplicable)
        {
            return true;
        }

        return IsSelectedAccessibility(
            symbol.DeclaredAccessibility,
            containing,
            profile);
    }

    private static bool IsReachableType(INamedTypeSymbol type, TargetProfile profile)
    {
        if (type.IsFileLocal)
        {
            return false;
        }

        if (type.ContainingType is null)
        {
            return type.DeclaredAccessibility == Accessibility.Public
                || profile == TargetProfile.AssemblyVisible
                    && type.DeclaredAccessibility == Accessibility.Internal;
        }

        return IsReachableType(type.ContainingType, profile)
            && IsSelectedAccessibility(
                type.DeclaredAccessibility,
                type.ContainingType,
                profile);
    }

    private static bool IsSelectedAccessibility(
        Accessibility accessibility,
        INamedTypeSymbol containingType,
        TargetProfile profile)
    {
        if (accessibility == Accessibility.Public)
        {
            return true;
        }

        var derivable = IsDerivableContainer(containingType);
        if (profile == TargetProfile.ExternalApi)
        {
            return accessibility is Accessibility.Protected
                    or Accessibility.ProtectedOrInternal
                && derivable;
        }

        return accessibility is Accessibility.Internal
                or Accessibility.ProtectedOrInternal
            || accessibility is Accessibility.Protected
                    or Accessibility.ProtectedAndInternal
                && derivable;
    }

    private static bool IsDerivableContainer(INamedTypeSymbol type) =>
        type.TypeKind == TypeKind.Interface
        || type.TypeKind == TypeKind.Class && !type.IsSealed && !type.IsStatic;

    private static bool IsRecordPositionalProperty(IPropertySymbol property) =>
        !property.DeclaringSyntaxReferences.Any(reference =>
            reference.GetSyntax() is PropertyDeclarationSyntax)
        && GetRecordPositionalParameter(property) is not null;

    private static IParameterSymbol? GetRecordPositionalParameter(IPropertySymbol property) =>
        property.ContainingType.IsRecord
            ? property.ContainingType.InstanceConstructors
                .SelectMany(constructor => constructor.Parameters)
                .SingleOrDefault(parameter =>
                    parameter.Name == property.Name
                    && SymbolEqualityComparer.Default.Equals(parameter.Type, property.Type)
                    && parameter.DeclaringSyntaxReferences.Any(reference =>
                        reference.GetSyntax() is ParameterSyntax syntax
                        && syntax.Parent?.Parent is RecordDeclarationSyntax))
            : null;

    private static bool IsPartialDeclaration(
        ISymbol symbol,
        CancellationToken cancellationToken) => symbol switch
        {
            INamedTypeSymbol => symbol.DeclaringSyntaxReferences.Any(reference =>
                reference.GetSyntax(cancellationToken) is TypeDeclarationSyntax declaration
                && declaration.Modifiers.Any(SyntaxKind.PartialKeyword)),
            IMethodSymbol { MethodKind: MethodKind.Ordinary } =>
                symbol.DeclaringSyntaxReferences.Any(reference =>
                    reference.GetSyntax(cancellationToken) is MethodDeclarationSyntax declaration
                    && declaration.Modifiers.Any(SyntaxKind.PartialKeyword)),
            _ => false,
        };

    private static bool IsRelationMember(ISymbol symbol) =>
        !symbol.IsImplicitlyDeclared
        && symbol is IMethodSymbol
        {
            MethodKind: MethodKind.Ordinary
                    or MethodKind.Destructor
                    or MethodKind.UserDefinedOperator
                    or MethodKind.Conversion
                    or MethodKind.ExplicitInterfaceImplementation,
        }
            or IPropertySymbol
            or IEventSymbol;

    private static ISymbol? GetOverriddenMember(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.OverriddenMethod,
        IPropertySymbol property => property.OverriddenProperty,
        IEventSymbol @event => @event.OverriddenEvent,
        _ => null,
    };

    private static IEnumerable<ISymbol> GetExplicitInterfaceMembers(ISymbol symbol) =>
        symbol switch
        {
            IMethodSymbol method => method.ExplicitInterfaceImplementations,
            IPropertySymbol property => property.ExplicitInterfaceImplementations,
            IEventSymbol @event => @event.ExplicitInterfaceImplementations,
            _ => [],
        };

    private static IEnumerable<ISymbol> EnumerateSymbols(INamespaceSymbol root)
    {
        foreach (var type in EnumerateTypes(root))
        {
            yield return type;
            foreach (var member in type.GetMembers().Where(member => member is not INamedTypeSymbol))
            {
                yield return member;
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateTypes(INamespaceSymbol root)
    {
        foreach (var member in root.GetMembers())
        {
            if (member is INamespaceSymbol childNamespace)
            {
                foreach (var type in EnumerateTypes(childNamespace))
                {
                    yield return type;
                }
            }
            else if (member is INamedTypeSymbol type)
            {
                foreach (var candidate in EnumerateTypeAndNested(type))
                {
                    yield return candidate;
                }
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateTypeAndNested(
        INamedTypeSymbol type)
    {
        yield return type;
        foreach (var nested in type.GetTypeMembers())
        {
            foreach (var candidate in EnumerateTypeAndNested(nested))
            {
                yield return candidate;
            }
        }
    }

    private static string? DefaultDocumentationId(ISymbol symbol) =>
        symbol.OriginalDefinition.GetDocumentationCommentId();

    private static RelationEndpointResolution DefaultRelationEndpoint(
        RelationKind kind,
        ISymbol symbol,
        bool isTarget,
        string context)
    {
        _ = kind;
        _ = isTarget;
        var id = symbol.OriginalDefinition.GetDocumentationCommentId();
        return id is null
            ? new RelationEndpointResolution(RelationEndpointStatus.Unavailable, null)
            : new RelationEndpointResolution(
                RelationEndpointStatus.Available,
                new SymbolRef(context, id));
    }

    private void Observe(
        ClassificationStage stage,
        CancellationToken cancellationToken)
    {
        observer?.Invoke(stage);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static ImmutableArray<ClassificationDiagnostic> NormalizeDiagnostics(
        IEnumerable<ClassificationDiagnostic> diagnostics) =>
        diagnostics
            .Distinct()
            .OrderBy(diagnostic => diagnostic.Stage, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Severity, StringComparer.Ordinal)
            .Take(32)
            .ToImmutableArray();

    private sealed record ProvenanceResult(
        ClassificationOrigin Origin,
        ImmutableArray<CandidateLocator> Locators,
        bool GeneratedProvenanceAvailable);
}

internal enum ClassificationStage
{
    TargetDiscovery,
    ComponentDiscovery,
    RelationDiscovery,
    CandidateBufferingComplete,
    TerminalValidation,
}

internal enum RelationEndpointStatus
{
    Available,
    Ambiguous,
    Unavailable,
}

internal readonly record struct RelationEndpointResolution(
    RelationEndpointStatus Status,
    SymbolRef? SymbolRef);

internal delegate RelationEndpointResolution RelationEndpointResolver(
    RelationKind kind,
    ISymbol symbol,
    bool isTarget,
    string compilationContextRef);
