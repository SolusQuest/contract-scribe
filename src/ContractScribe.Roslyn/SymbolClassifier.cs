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
    private readonly Action<ClassificationStage, ClassificationCandidateBuffer>? candidateObserver;
    private readonly Action? componentDiscoveryOperationObserver;

    public SymbolClassifier()
        : this(null, null, null, null, null)
    {
    }

    internal SymbolClassifier(
        Func<ISymbol, string?>? documentationId,
        RelationEndpointResolver? relationEndpointResolver,
        Action<ClassificationStage>? observer,
        Action<ClassificationStage, ClassificationCandidateBuffer>? candidateObserver,
        Action? componentDiscoveryOperationObserver = null)
    {
        this.documentationId = documentationId ?? DefaultDocumentationId;
        this.relationEndpointResolver = relationEndpointResolver ?? DefaultRelationEndpoint;
        this.observer = observer;
        this.candidateObserver = candidateObserver;
        this.componentDiscoveryOperationObserver = componentDiscoveryOperationObserver;
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
            var candidates = new ClassificationCandidateBuffer();
            var discoveredTargets = new List<DiscoveredTarget>();
            var discoveryIndex = new SymbolDiscoveryIndex(
                componentDiscoveryOperationObserver);
            foreach (var project in session.Projects
                .Where(project => project.Role == LoadedProjectRole.AuditRoot)
                .OrderBy(project => project.CompilationContextRef, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                DiscoverTargets(
                    project,
                    profile,
                    discoveredTargets,
                    discoveryIndex,
                    cancellationToken);
            }

            foreach (var target in discoveredTargets
                .Where(target => target.DocumentationCommentId is not null))
            {
                candidates.AddTarget(
                    target.CompilationContextRef,
                    target.DocumentationCommentId!,
                    target.PrimaryKind,
                    target.Traits,
                    target.Provenance.Origin,
                    target.Provenance.Locators,
                    target.Provenance.GeneratedProvenanceAvailable);
            }

            ObserveCandidates(
                ClassificationStage.TargetDiscovery,
                candidates,
                cancellationToken);
            foreach (var target in discoveredTargets
                .Where(CanHaveComponents))
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddComponents(
                    target.Symbol,
                    target.CompilationContextRef,
                    target.DocumentationCommentId!,
                    target.Provenance.Origin,
                    candidates,
                    discoveryIndex,
                    cancellationToken);
            }

            ObserveCandidates(
                ClassificationStage.ComponentDiscovery,
                candidates,
                cancellationToken);
            foreach (var project in session.Projects
                .Where(project => project.Role == LoadedProjectRole.AuditRoot)
                .OrderBy(project => project.CompilationContextRef, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                DiscoverRelations(
                    project,
                    profile,
                    candidates,
                    diagnostics,
                    discoveryIndex,
                    cancellationToken);
            }

            ObserveCandidates(
                ClassificationStage.RelationDiscovery,
                candidates,
                cancellationToken);
            foreach (var target in discoveredTargets
                .Where(target => target.DocumentationCommentId is null))
            {
                candidates.AddUnresolvedDocumentationCandidate(
                    target.CompilationContextRef,
                    target.Provenance.Origin,
                    target.Provenance.Locators);
            }

            ObserveCandidates(
                ClassificationStage.UnresolvedDiscovery,
                candidates,
                cancellationToken);
            ObserveCandidates(
                ClassificationStage.CandidateBufferingComplete,
                candidates,
                cancellationToken);
            var outcome = candidates.Normalize(
                profile,
                NormalizeDiagnostics(diagnostics),
                cancellationToken);
            if (outcome.Status != ClassificationRunStatus.Success)
            {
                return outcome;
            }

            Observe(ClassificationStage.TerminalValidation, cancellationToken);
            return outcome;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ClassificationOutcome.Cancelled(NormalizeDiagnostics(diagnostics));
        }
    }

    private void DiscoverTargets(
        LoadedProject project,
        TargetProfile profile,
        List<DiscoveredTarget> targets,
        SymbolDiscoveryIndex discoveryIndex,
        CancellationToken cancellationToken)
    {
        foreach (var symbol in EnumerateSymbols(project.Compilation.Assembly.GlobalNamespace))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsIndependentSourceCandidate(
                    symbol,
                    discoveryIndex,
                    cancellationToken)
                || !IsSelectedTargetSurface(symbol, profile))
            {
                continue;
            }

            var original = symbol.OriginalDefinition;
            var provenance = ClassifyProvenance(project, original);
            var kind = ClassifyPrimaryKind(original);
            targets.Add(new DiscoveredTarget(
                original,
                project.CompilationContextRef,
                documentationId(original),
                kind ?? PrimarySymbolKind.Unknown,
                ClassifyTraits(original, cancellationToken),
                provenance));
        }
    }

    private void DiscoverRelations(
        LoadedProject project,
        TargetProfile profile,
        ClassificationCandidateBuffer candidates,
        List<ClassificationDiagnostic> diagnostics,
        SymbolDiscoveryIndex discoveryIndex,
        CancellationToken cancellationToken)
    {
        foreach (var type in EnumerateTypes(project.Compilation.Assembly.GlobalNamespace))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var containingTypeSelected = IsIndependentSourceCandidate(
                    type,
                    discoveryIndex,
                    cancellationToken)
                && IsSelectedTargetSurface(type, profile);
            foreach (var member in EnumerateLogicalMembers(type)
                .Where(IsRelationMember))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsIndependentSourceCandidate(
                        member,
                        discoveryIndex,
                        cancellationToken)
                    && IsSelectedTargetSurface(member, profile)
                    && GetOverriddenMember(member) is { } overridden)
                {
                    TryAddRelation(
                        RelationKind.Overrides,
                        member,
                        overridden.OriginalDefinition,
                        project.CompilationContextRef,
                        candidates,
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
                        candidates,
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
                        candidates,
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
                    || !CanonicalPartialMember(implementation).Locations.Any(
                        location => location.IsInSource)
                    || !IsIndependentSourceCandidate(
                        CanonicalPartialMember(implementation),
                        discoveryIndex,
                        cancellationToken)
                    || !IsSelectedTargetSurface(
                        CanonicalPartialMember(implementation),
                        profile)
                    || GetExplicitInterfaceMembers(
                        CanonicalPartialMember(implementation)).Any())
                {
                    continue;
                }

                TryAddRelation(
                    RelationKind.ImplicitInterfaceImplementation,
                    CanonicalPartialMember(implementation),
                    interfaceMember.OriginalDefinition,
                    project.CompilationContextRef,
                    candidates,
                    diagnostics);
            }
        }
    }

    private void TryAddRelation(
        RelationKind kind,
        ISymbol source,
        ISymbol target,
        string context,
        ClassificationCandidateBuffer candidates,
        List<ClassificationDiagnostic> diagnostics)
    {
        var sourceResolution = relationEndpointResolver(kind, source, false, context);
        var targetResolution = relationEndpointResolver(kind, target, true, context);
        if (sourceResolution.Status != RelationEndpointStatus.Available
            || sourceResolution.CompilationContextRef is null
            || sourceResolution.DocumentationCommentId is null
            || targetResolution.Status != RelationEndpointStatus.Available
            || targetResolution.CompilationContextRef is null
            || targetResolution.DocumentationCommentId is null)
        {
            AddEndpointDiagnostic(sourceResolution.Status, diagnostics);
            AddEndpointDiagnostic(targetResolution.Status, diagnostics);
            return;
        }

        candidates.AddRelation(
            kind,
            sourceResolution.CompilationContextRef,
            sourceResolution.DocumentationCommentId,
            targetResolution.CompilationContextRef,
            targetResolution.DocumentationCommentId,
            ClassifyPrimaryKind(source) ?? PrimarySymbolKind.Unknown,
            ClassifyPrimaryKind(target) ?? PrimarySymbolKind.Unknown);
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
        string compilationContextRef,
        string documentationCommentId,
        ClassificationOrigin origin,
        ClassificationCandidateBuffer candidates,
        SymbolDiscoveryIndex discoveryIndex,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        void Add(ComponentKind kind, string identity, ClassificationOrigin? componentOrigin = null) =>
            candidates.AddComponent(
                compilationContextRef,
                documentationCommentId,
                kind,
                identity,
                componentOrigin ?? origin);

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

            if (discoveryIndex.HasBackingField(property, cancellationToken))
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

            if (discoveryIndex.HasBackingField(@event, cancellationToken))
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
            var positionalOrdinals = new List<int>();
            foreach (var recordProperty in type.GetMembers()
                .OfType<IPropertySymbol>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (discoveryIndex.TryGetRecordPositionalOrdinal(
                    recordProperty,
                    cancellationToken,
                    out var ordinal))
                {
                    positionalOrdinals.Add(ordinal);
                }
            }

            foreach (var ordinal in positionalOrdinals.Distinct().Order())
            {
                Add(
                    ComponentKind.SynthesizedRecordPositionalProperty,
                    $"synthesized/record-positional-property/{ordinal}",
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

    private static bool CanHaveComponents(DiscoveredTarget candidate) =>
        candidate.DocumentationCommentId is not null
        && candidate.Provenance.GeneratedProvenanceAvailable
        && candidate.PrimaryKind != PrimarySymbolKind.Unknown
        && candidate.Provenance.Origin is ClassificationOrigin.Source
            or ClassificationOrigin.SourceGenerator
            or ClassificationOrigin.ToolGenerated;

    private static ProvenanceResult ClassifyProvenance(
        LoadedProject project,
        ISymbol symbol)
    {
        var origins = new HashSet<ClassificationOrigin>();
        var locators = ImmutableArray.CreateBuilder<CandidateLocator>();
        var available = true;
        foreach (var location in EnumerateLogicalDeclarations(symbol)
            .SelectMany(declaration => declaration.Locations)
            .Where(location => location.IsInSource))
        {
            if (location.SourceTree is null
                || !project.SourceTrees.TryGetValue(location.SourceTree, out var source))
            {
                available = false;
                continue;
            }

            switch (source)
            {
                case { Kind: LoadedSourceKind.Repository, RepositoryIdentity: { } path }:
                    origins.Add(ClassificationOrigin.Source);
                    locators.Add(ClassificationInput.RepositoryLocator(
                        path,
                        location.SourceSpan.Start,
                        location.SourceSpan.End));
                    break;
                case
                {
                    Kind: LoadedSourceKind.SourceGenerator,
                    GeneratedSource: { } fact,
                }:
                    origins.Add(ClassificationOrigin.SourceGenerator);
                    locators.Add(ClassificationInput.GeneratedSourceLocator(
                        fact.ProducerId,
                        fact.OutputId,
                        location.SourceSpan.Start,
                        location.SourceSpan.End));
                    break;
                case
                {
                    Kind: LoadedSourceKind.ToolGenerated,
                    GeneratedSource: { } fact,
                }:
                    origins.Add(ClassificationOrigin.ToolGenerated);
                    locators.Add(ClassificationInput.ToolGeneratedLocator(
                        fact.ProducerId,
                        fact.OutputId,
                        location.SourceSpan.Start,
                        location.SourceSpan.End));
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

    private static PrimarySymbolKind? ClassifyPrimaryKind(ISymbol symbol)
    {
        if (symbol is IMethodSymbol
            {
                MethodKind: MethodKind.ExplicitInterfaceImplementation,
            } explicitImplementation)
        {
            var implementedKinds = explicitImplementation
                .ExplicitInterfaceImplementations
                .Select(ClassifyPrimaryKind)
                .Where(kind => kind is not null)
                .Distinct()
                .ToArray();
            if (implementedKinds is [{ } implementedKind])
            {
                return implementedKind;
            }

            return explicitImplementation.DeclaringSyntaxReferences
                .Select(reference => reference.GetSyntax())
                .Select(syntax => syntax switch
                {
                    OperatorDeclarationSyntax => PrimarySymbolKind.Operator,
                    ConversionOperatorDeclarationSyntax => PrimarySymbolKind.Conversion,
                    _ => PrimarySymbolKind.Method,
                })
                .Distinct()
                .SingleOrDefault();
        }

        return symbol switch
        {
            INamedTypeSymbol { TypeKind: TypeKind.Class } => PrimarySymbolKind.Class,
            INamedTypeSymbol { TypeKind: TypeKind.Struct } => PrimarySymbolKind.Struct,
            INamedTypeSymbol { TypeKind: TypeKind.Interface } => PrimarySymbolKind.Interface,
            INamedTypeSymbol { TypeKind: TypeKind.Enum } => PrimarySymbolKind.Enum,
            INamedTypeSymbol { TypeKind: TypeKind.Delegate } => PrimarySymbolKind.Delegate,
            IMethodSymbol { MethodKind: MethodKind.Constructor } => PrimarySymbolKind.Constructor,
            IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator } =>
                PrimarySymbolKind.Operator,
            IMethodSymbol { MethodKind: MethodKind.Conversion } =>
                PrimarySymbolKind.Conversion,
            IMethodSymbol { MethodKind: MethodKind.Destructor } => PrimarySymbolKind.Method,
            IMethodSymbol { MethodKind: MethodKind.Ordinary } => PrimarySymbolKind.Method,
            IPropertySymbol { IsIndexer: true } => PrimarySymbolKind.Indexer,
            IPropertySymbol => PrimarySymbolKind.Property,
            IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } =>
                PrimarySymbolKind.EnumMember,
            IFieldSymbol => PrimarySymbolKind.Field,
            IEventSymbol => PrimarySymbolKind.Event,
            _ => null,
        };
    }

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

            if (EnumerateLogicalDeclarations(method)
                .OfType<IMethodSymbol>()
                .Any(declaration => declaration.IsAsync))
            {
                traits.Add(SymbolTrait.Async);
            }

            if (EnumerateLogicalDeclarations(method)
                .OfType<IMethodSymbol>()
                .SelectMany(declaration => declaration.DeclaringSyntaxReferences)
                .Any(reference =>
                    reference.GetSyntax(cancellationToken)
                        is BaseMethodDeclarationSyntax declaration
                    && ContainsDirectYield(declaration)))
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

    private static bool IsIndependentSourceCandidate(
        ISymbol symbol,
        SymbolDiscoveryIndex discoveryIndex,
        CancellationToken cancellationToken)
    {
        symbol = CanonicalPartialMember(symbol);
        if (!symbol.Locations.Any(location => location.IsInSource)
            || symbol is IMethodSymbol { MethodKind: MethodKind.StaticConstructor }
            || GetExplicitInterfaceMembers(symbol).Any()
            || symbol is IPropertySymbol property
                && discoveryIndex.TryGetRecordPositionalOrdinal(
                    property,
                    cancellationToken,
                    out _))
        {
            return false;
        }

        if (IsUnimplementedPartialDefinition(symbol))
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

    private static bool IsPartialDeclaration(
        ISymbol symbol,
        CancellationToken cancellationToken)
    {
        symbol = CanonicalPartialMember(symbol);
        return symbol switch
        {
            INamedTypeSymbol => symbol.DeclaringSyntaxReferences.Any(reference =>
                reference.GetSyntax(cancellationToken) is TypeDeclarationSyntax declaration
                && declaration.Modifiers.Any(SyntaxKind.PartialKeyword)),
            IMethodSymbol method =>
                method.PartialImplementationPart is not null
                || method.DeclaringSyntaxReferences.Any(reference =>
                    reference.GetSyntax(cancellationToken)
                        is BaseMethodDeclarationSyntax declaration
                    && declaration.Modifiers.Any(SyntaxKind.PartialKeyword)),
            IPropertySymbol property =>
                property.PartialImplementationPart is not null
                || property.DeclaringSyntaxReferences.Any(reference =>
                    reference.GetSyntax(cancellationToken) switch
                    {
                        PropertyDeclarationSyntax declaration =>
                            declaration.Modifiers.Any(SyntaxKind.PartialKeyword),
                        IndexerDeclarationSyntax declaration =>
                            declaration.Modifiers.Any(SyntaxKind.PartialKeyword),
                        _ => false,
                    }),
            IEventSymbol @event =>
                @event.PartialImplementationPart is not null
                || @event.DeclaringSyntaxReferences.Any(reference =>
                    reference.GetSyntax(cancellationToken)
                        is EventDeclarationSyntax declaration
                    && declaration.Modifiers.Any(SyntaxKind.PartialKeyword)),
            _ => false,
        };
    }

    private static bool ContainsDirectYield(
        BaseMethodDeclarationSyntax declaration) =>
        declaration.DescendantNodes()
            .OfType<YieldStatementSyntax>()
            .Any(statement => ReferenceEquals(
                statement.Ancestors().First(node =>
                    node is BaseMethodDeclarationSyntax
                        or LocalFunctionStatementSyntax
                        or AnonymousFunctionExpressionSyntax),
                declaration));

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
            foreach (var member in EnumerateLogicalMembers(type)
                .Where(member => member is not INamedTypeSymbol))
            {
                yield return member;
            }
        }
    }

    private static IEnumerable<ISymbol> EnumerateLogicalMembers(
        INamedTypeSymbol type) =>
        type.GetMembers()
            .Select(CanonicalPartialMember)
            .Distinct(SymbolEqualityComparer.Default);

    private static ISymbol CanonicalPartialMember(ISymbol symbol) =>
        symbol switch
        {
            IMethodSymbol { PartialDefinitionPart: { } definition } => definition,
            IPropertySymbol { PartialDefinitionPart: { } definition } => definition,
            IEventSymbol { PartialDefinitionPart: { } definition } => definition,
            _ => symbol,
        };

    private static IEnumerable<ISymbol> EnumerateLogicalDeclarations(
        ISymbol symbol)
    {
        var definition = CanonicalPartialMember(symbol);
        yield return definition;
        ISymbol? implementation = definition switch
        {
            IMethodSymbol method => method.PartialImplementationPart,
            IPropertySymbol property => property.PartialImplementationPart,
            IEventSymbol @event => @event.PartialImplementationPart,
            _ => null,
        };
        if (implementation is not null)
        {
            yield return implementation;
        }
    }

    private static bool IsUnimplementedPartialDefinition(ISymbol symbol)
    {
        symbol = CanonicalPartialMember(symbol);
        return symbol switch
        {
            IMethodSymbol method =>
                method.PartialImplementationPart is null
                && method.DeclaringSyntaxReferences.Any(reference =>
                    reference.GetSyntax() is BaseMethodDeclarationSyntax declaration
                    && declaration.Modifiers.Any(SyntaxKind.PartialKeyword)),
            IPropertySymbol property =>
                property.PartialImplementationPart is null
                && property.DeclaringSyntaxReferences.Any(reference =>
                    reference.GetSyntax() switch
                    {
                        PropertyDeclarationSyntax declaration =>
                            declaration.Modifiers.Any(SyntaxKind.PartialKeyword),
                        IndexerDeclarationSyntax declaration =>
                            declaration.Modifiers.Any(SyntaxKind.PartialKeyword),
                        _ => false,
                    }),
            IEventSymbol @event =>
                @event.PartialImplementationPart is null
                && @event.DeclaringSyntaxReferences.Any(reference =>
                    reference.GetSyntax() is EventDeclarationSyntax declaration
                    && declaration.Modifiers.Any(SyntaxKind.PartialKeyword)),
            _ => false,
        };
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
            ? new RelationEndpointResolution(
                RelationEndpointStatus.Unavailable,
                null,
                null)
            : new RelationEndpointResolution(
                RelationEndpointStatus.Available,
                context,
                id);
    }

    private void ObserveCandidates(
        ClassificationStage stage,
        ClassificationCandidateBuffer candidates,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        candidateObserver?.Invoke(stage, candidates);
        Observe(stage, cancellationToken);
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

    private sealed class SymbolDiscoveryIndex(Action? operationObserver)
    {
        private readonly Dictionary<ISymbol, TypeDiscoveryData> types =
            new(SymbolEqualityComparer.Default);

        public bool HasBackingField(
            ISymbol member,
            CancellationToken cancellationToken)
        {
            operationObserver?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return GetTypeData(member.ContainingType, cancellationToken)
                .BackingFieldOwners
                .Contains(CanonicalPartialMember(member));
        }

        public bool TryGetRecordPositionalOrdinal(
            IPropertySymbol property,
            CancellationToken cancellationToken,
            out int ordinal)
        {
            operationObserver?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return GetTypeData(property.ContainingType, cancellationToken)
                .RecordPositionalOrdinals
                .TryGetValue(CanonicalPartialMember(property), out ordinal);
        }

        private TypeDiscoveryData GetTypeData(
            INamedTypeSymbol type,
            CancellationToken cancellationToken)
        {
            if (types.TryGetValue(type, out var cached))
            {
                return cached;
            }

            var backingFieldOwners = new HashSet<ISymbol>(
                SymbolEqualityComparer.Default);
            foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                operationObserver?.Invoke();
                if (field.AssociatedSymbol is { } associated)
                {
                    backingFieldOwners.Add(CanonicalPartialMember(associated));
                }
            }

            var positionalOrdinals = new Dictionary<ISymbol, int>(
                SymbolEqualityComparer.Default);
            if (type.IsRecord)
            {
                foreach (var parameter in type.InstanceConstructors
                    .SelectMany(constructor => constructor.Parameters))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    operationObserver?.Invoke();
                    if (!parameter.DeclaringSyntaxReferences.Any(reference =>
                        reference.GetSyntax(cancellationToken) is ParameterSyntax syntax
                        && syntax.Parent?.Parent is RecordDeclarationSyntax))
                    {
                        continue;
                    }

                    foreach (var property in type.GetMembers(parameter.Name)
                        .OfType<IPropertySymbol>())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        operationObserver?.Invoke();
                        if (!property.DeclaringSyntaxReferences.Any(reference =>
                                reference.GetSyntax(cancellationToken)
                                    is PropertyDeclarationSyntax)
                            && SymbolEqualityComparer.Default.Equals(
                                parameter.Type,
                                property.Type))
                        {
                            positionalOrdinals[
                                CanonicalPartialMember(property)] = parameter.Ordinal;
                        }
                    }
                }
            }

            var result = new TypeDiscoveryData(
                backingFieldOwners,
                positionalOrdinals);
            types.Add(type, result);
            return result;
        }
    }

    private sealed record TypeDiscoveryData(
        HashSet<ISymbol> BackingFieldOwners,
        Dictionary<ISymbol, int> RecordPositionalOrdinals);

    private sealed record ProvenanceResult(
        ClassificationOrigin Origin,
        ImmutableArray<CandidateLocator> Locators,
        bool GeneratedProvenanceAvailable);

    private sealed record DiscoveredTarget(
        ISymbol Symbol,
        string CompilationContextRef,
        string? DocumentationCommentId,
        PrimarySymbolKind PrimaryKind,
        ImmutableArray<SymbolTrait> Traits,
        ProvenanceResult Provenance);
}

internal enum ClassificationStage
{
    TargetDiscovery,
    ComponentDiscovery,
    RelationDiscovery,
    UnresolvedDiscovery,
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
    string? CompilationContextRef,
    string? DocumentationCommentId);

internal delegate RelationEndpointResolution RelationEndpointResolver(
    RelationKind kind,
    ISymbol symbol,
    bool isTarget,
    string compilationContextRef);
