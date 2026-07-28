using System.Collections.Immutable;
namespace ContractScribe.Core;

internal sealed record TargetClassificationCandidate(
    string CompilationContextRef,
    string? DocumentationCommentId,
    PrimarySymbolKind PrimaryKind,
    ImmutableArray<SymbolTrait> Traits,
    ClassificationOrigin Origin,
    ImmutableArray<CandidateLocator> CandidateLocators,
    bool GeneratedProvenanceAvailable = true,
    bool SemanticContextAvailable = true,
    bool PartialAmbiguous = false);

internal sealed record ComponentClassificationCandidate(
    SymbolRef ParentSymbolRef,
    ComponentKind ComponentKind,
    string? Identity,
    ClassificationOrigin Origin,
    CandidateLocator? CandidateLocator = null,
    bool GeneratedProvenanceAvailable = true,
    bool SemanticContextAvailable = true,
    bool PartialAmbiguous = false);

internal sealed record RelationObservationCandidate(
    RelationObservation Observation,
    PrimarySymbolKind SourceKind,
    PrimarySymbolKind TargetKind);

internal sealed record ClassificationCandidateBatch(
    IReadOnlyList<TargetClassificationCandidate> Targets,
    IReadOnlyList<ComponentClassificationCandidate> Components,
    IReadOnlyList<RelationObservationCandidate> Relations,
    IReadOnlyList<UnresolvedClassification> Unresolved);

internal sealed class ClassificationUnrepresentableException : Exception;

internal static class ClassificationNormalization
{
    public static ClassificationSet Normalize(
        TargetProfile profile,
        ClassificationCandidateBatch candidates,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(profile))
        {
            throw new ArgumentOutOfRangeException(
                nameof(profile),
                profile,
                "Classification requires a validated closed target profile.");
        }

        var targets = new List<TargetClassification>();
        var unresolved = new List<UnresolvedClassification>(candidates.Unresolved);
        foreach (var candidate in candidates.Targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            NormalizeTarget(candidate, targets, unresolved);
        }

        var normalizedTargets = NormalizeTargets(targets);
        var supportedParents = normalizedTargets
            .Where(target => target.SupportStatus == SupportStatus.Supported)
            .ToDictionary(target => target.SymbolRef, target => target.PrimaryKind);
        var components = NormalizeComponentCandidates(
            candidates.Components,
            supportedParents,
            cancellationToken);
        var normalizedUnresolved = NormalizeUnresolved(unresolved);
        var relations = NormalizeRelations(candidates.Relations);

        cancellationToken.ThrowIfCancellationRequested();
        return new ClassificationSet(
            profile,
            normalizedTargets,
            components,
            relations,
            normalizedUnresolved);
    }

    private static void NormalizeTarget(
        TargetClassificationCandidate candidate,
        List<TargetClassification> targets,
        List<UnresolvedClassification> unresolved)
    {
        RequireContext(candidate.CompilationContextRef);
        RequireEnum(candidate.PrimaryKind);
        RequireEnum(candidate.Origin);
        var traits = candidate.Traits
            .Select(trait =>
            {
                RequireEnum(trait);
                return trait;
            })
            .Distinct()
            .OrderBy(ClassificationVocabulary.GetId, StringComparer.Ordinal)
            .ToImmutableArray();

        if (candidate.DocumentationCommentId is null)
        {
            if (candidate.Origin is ClassificationOrigin.Unknown
                    or ClassificationOrigin.CompilerSynthesized
                || candidate.CandidateLocators.IsDefaultOrEmpty)
            {
                throw Unrepresentable();
            }

            foreach (var locator in candidate.CandidateLocators
                .Distinct()
                .Order(CandidateLocatorComparer.Instance))
            {
                RequireLocator(locator);
                unresolved.Add(new UnresolvedClassification(
                    candidate.CompilationContextRef,
                    candidate.Origin,
                    SupportStatus.UnavailableContext,
                    SkipReason.UnavailableDocumentationCommentId,
                    locator));
            }

            return;
        }

        RequireText(candidate.DocumentationCommentId);
        if (candidate.Origin == ClassificationOrigin.CompilerSynthesized)
        {
            throw Unrepresentable();
        }

        var symbolRef = new SymbolRef(
            candidate.CompilationContextRef,
            candidate.DocumentationCommentId);
        TargetClassification target;
        if (!candidate.GeneratedProvenanceAvailable)
        {
            target = new TargetClassification(
                symbolRef,
                candidate.PrimaryKind,
                traits,
                ClassificationOrigin.Unknown,
                SupportStatus.UnavailableContext,
                SkipReason.UnavailableGeneratedProvenance);
        }
        else if (!candidate.SemanticContextAvailable)
        {
            if (candidate.Origin == ClassificationOrigin.Unknown)
            {
                throw Unrepresentable();
            }

            target = new TargetClassification(
                symbolRef,
                candidate.PrimaryKind,
                traits,
                candidate.Origin,
                SupportStatus.UnavailableContext,
                SkipReason.UnavailableSemanticContext);
        }
        else if (candidate.PrimaryKind == PrimarySymbolKind.Unknown)
        {
            if (candidate.Origin == ClassificationOrigin.Unknown)
            {
                throw Unrepresentable();
            }

            target = new TargetClassification(
                symbolRef,
                PrimarySymbolKind.Unknown,
                traits,
                candidate.Origin,
                SupportStatus.Unsupported,
                SkipReason.UnsupportedSymbolKind);
        }
        else if (candidate.PartialAmbiguous)
        {
            if (candidate.Origin == ClassificationOrigin.Unknown)
            {
                throw Unrepresentable();
            }

            target = new TargetClassification(
                symbolRef,
                candidate.PrimaryKind,
                traits,
                candidate.Origin,
                SupportStatus.Ambiguous,
                SkipReason.AmbiguousPartialDeclaration);
        }
        else if (candidate.Origin == ClassificationOrigin.Mixed)
        {
            target = new TargetClassification(
                symbolRef,
                candidate.PrimaryKind,
                traits,
                ClassificationOrigin.Mixed,
                SupportStatus.Ambiguous,
                SkipReason.AmbiguousMixedOrigin);
        }
        else
        {
            if (candidate.Origin == ClassificationOrigin.Unknown)
            {
                throw Unrepresentable();
            }

            target = new TargetClassification(
                symbolRef,
                candidate.PrimaryKind,
                traits,
                candidate.Origin,
                SupportStatus.Supported);
        }

        RequireTarget(target);
        targets.Add(target);
    }

    private static ImmutableArray<TargetClassification> NormalizeTargets(
        IReadOnlyList<TargetClassification> targets)
    {
        var result = new List<TargetClassification>();
        foreach (var group in targets.GroupBy(target => target.SymbolRef))
        {
            var variants = group
                .GroupBy(TargetSignature, StringComparer.Ordinal)
                .Select(variant => variant.First())
                .ToArray();
            if (variants.Length != 1)
            {
                throw Unrepresentable();
            }

            RequireTarget(variants[0]);
            result.Add(variants[0]);
        }

        return result
            .OrderBy(target => target.SymbolRef.CompilationContextRef, StringComparer.Ordinal)
            .ThenBy(target => target.SymbolRef.DocumentationCommentId, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static ImmutableArray<ComponentClassification> NormalizeComponentCandidates(
        IReadOnlyList<ComponentClassificationCandidate> candidates,
        IReadOnlyDictionary<SymbolRef, PrimarySymbolKind> supportedParents,
        CancellationToken cancellationToken)
    {
        var expanded = new List<ComponentClassificationCandidate>();
        var unknownGroups = candidates
            .Where(candidate => candidate.ComponentKind == ComponentKind.Unknown)
            .GroupBy(candidate => candidate.ParentSymbolRef);
        expanded.AddRange(candidates.Where(candidate =>
            candidate.ComponentKind != ComponentKind.Unknown));
        foreach (var group in unknownGroups)
        {
            var locatorGroups = group
                .Select(candidate =>
                {
                    if (candidate.CandidateLocator is null
                        || candidate.Identity is not null)
                    {
                        throw Unrepresentable();
                    }

                    RequireLocator(candidate.CandidateLocator);
                    return candidate;
                })
                .GroupBy(
                    candidate => LocatorKey(candidate.CandidateLocator!),
                    StringComparer.Ordinal)
                .OrderBy(locatorGroup => locatorGroup.Key, StringComparer.Ordinal)
                .ToArray();
            for (var ordinal = 0; ordinal < locatorGroups.Length; ordinal++)
            {
                var variants = locatorGroups[ordinal]
                    .GroupBy(UnknownCandidateSignature, StringComparer.Ordinal)
                    .Select(variant => variant.First())
                    .ToArray();
                if (variants.Length != 1)
                {
                    throw Unrepresentable();
                }

                expanded.Add(variants[0] with { Identity = $"unknown/{ordinal}" });
            }
        }

        var components = new List<ComponentClassification>();
        foreach (var candidate in expanded)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireSymbolRef(candidate.ParentSymbolRef);
            RequireEnum(candidate.ComponentKind);
            RequireEnum(candidate.Origin);
            if (!supportedParents.TryGetValue(
                candidate.ParentSymbolRef,
                out var parentKind))
            {
                continue;
            }

            if (string.IsNullOrEmpty(candidate.Identity)
                || !IsValidComponentIdentity(candidate.ComponentKind, candidate.Identity))
            {
                throw Unrepresentable();
            }

            if (!AllowsParent(candidate.ComponentKind, parentKind))
            {
                throw Unrepresentable();
            }

            if (IsSynthesized(candidate.ComponentKind)
                && candidate.Origin != ClassificationOrigin.CompilerSynthesized)
            {
                throw Unrepresentable();
            }

            if (candidate.PartialAmbiguous)
            {
                throw Unrepresentable();
            }

            ComponentClassification component;
            if (!candidate.GeneratedProvenanceAvailable)
            {
                component = new ComponentClassification(
                    candidate.ParentSymbolRef,
                    candidate.ComponentKind,
                    candidate.Identity,
                    ClassificationOrigin.Unknown,
                    SupportStatus.UnavailableContext,
                    SkipReason.UnavailableGeneratedProvenance);
            }
            else if (!candidate.SemanticContextAvailable)
            {
                if (candidate.Origin == ClassificationOrigin.Unknown)
                {
                    throw Unrepresentable();
                }

                component = new ComponentClassification(
                    candidate.ParentSymbolRef,
                    candidate.ComponentKind,
                    candidate.Identity,
                    candidate.Origin,
                    SupportStatus.UnavailableContext,
                    SkipReason.UnavailableSemanticContext);
            }
            else if (candidate.ComponentKind == ComponentKind.Unknown)
            {
                if (candidate.Origin == ClassificationOrigin.Unknown)
                {
                    throw Unrepresentable();
                }

                component = new ComponentClassification(
                    candidate.ParentSymbolRef,
                    ComponentKind.Unknown,
                    candidate.Identity,
                    candidate.Origin,
                    SupportStatus.Unsupported,
                    SkipReason.UnsupportedComponentKind);
            }
            else if (candidate.Origin == ClassificationOrigin.Mixed)
            {
                component = new ComponentClassification(
                    candidate.ParentSymbolRef,
                    candidate.ComponentKind,
                    candidate.Identity,
                    ClassificationOrigin.Mixed,
                    SupportStatus.Ambiguous,
                    SkipReason.AmbiguousMixedOrigin);
            }
            else if (IsSynthesized(candidate.ComponentKind))
            {
                component = new ComponentClassification(
                    candidate.ParentSymbolRef,
                    candidate.ComponentKind,
                    candidate.Identity,
                    ClassificationOrigin.CompilerSynthesized,
                    SupportStatus.NotApplicable,
                    SkipReason.NotApplicableSynthesizedNonTarget);
            }
            else if (IsNonDocumentation(candidate.ComponentKind))
            {
                if (candidate.Origin == ClassificationOrigin.Unknown)
                {
                    throw Unrepresentable();
                }

                component = new ComponentClassification(
                    candidate.ParentSymbolRef,
                    candidate.ComponentKind,
                    candidate.Identity,
                    candidate.Origin,
                    SupportStatus.NotApplicable,
                    SkipReason.NotApplicableNonDocumentationComponent);
            }
            else
            {
                if (candidate.Origin is ClassificationOrigin.Unknown
                    or ClassificationOrigin.CompilerSynthesized)
                {
                    throw Unrepresentable();
                }

                component = new ComponentClassification(
                    candidate.ParentSymbolRef,
                    candidate.ComponentKind,
                    candidate.Identity,
                    candidate.Origin,
                    SupportStatus.Supported);
            }

            RequireComponent(component);
            components.Add(component);
        }

        var result = new List<ComponentClassification>();
        foreach (var group in components.GroupBy(component => (
            component.ParentSymbolRef,
            component.ComponentKind,
            component.Identity)))
        {
            var variants = group
                .GroupBy(ComponentSignature, StringComparer.Ordinal)
                .Select(variant => variant.First())
                .ToArray();
            if (variants.Length != 1)
            {
                throw Unrepresentable();
            }

            RequireComponent(variants[0]);
            result.Add(variants[0]);
        }

        return result
            .OrderBy(component => component.ParentSymbolRef.CompilationContextRef, StringComparer.Ordinal)
            .ThenBy(component => component.ParentSymbolRef.DocumentationCommentId, StringComparer.Ordinal)
            .ThenBy(
                component => ClassificationVocabulary.GetId(component.ComponentKind),
                StringComparer.Ordinal)
            .ThenBy(component => component.Identity, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static ImmutableArray<RelationObservation> NormalizeRelations(
        IReadOnlyList<RelationObservationCandidate> candidates)
    {
        foreach (var candidate in candidates)
        {
            var relation = candidate.Observation;
            RequireEnum(relation.RelationKind);
            RequireEnum(candidate.SourceKind);
            RequireEnum(candidate.TargetKind);
            RequireSymbolRef(relation.SourceSymbolRef);
            RequireSymbolRef(relation.TargetSymbolRef);
            if (!AllowsRelationDomain(
                relation.RelationKind,
                candidate.SourceKind,
                candidate.TargetKind))
            {
                throw Unrepresentable();
            }
        }

        var relations = new List<RelationObservation>();
        foreach (var group in candidates.GroupBy(candidate => candidate.Observation))
        {
            if (group
                .Select(candidate => (candidate.SourceKind, candidate.TargetKind))
                .Distinct()
                .Take(2)
                .Count() != 1)
            {
                throw Unrepresentable();
            }

            relations.Add(group.Key);
        }

        return relations
            .OrderBy(
                relation => relation.SourceSymbolRef.CompilationContextRef,
                StringComparer.Ordinal)
            .ThenBy(
                relation => relation.SourceSymbolRef.DocumentationCommentId,
                StringComparer.Ordinal)
            .ThenBy(
                relation => ClassificationVocabulary.GetId(relation.RelationKind),
                StringComparer.Ordinal)
            .ThenBy(
                relation => relation.TargetSymbolRef.CompilationContextRef,
                StringComparer.Ordinal)
            .ThenBy(
                relation => relation.TargetSymbolRef.DocumentationCommentId,
                StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static ImmutableArray<UnresolvedClassification> NormalizeUnresolved(
        IReadOnlyList<UnresolvedClassification> unresolved)
    {
        foreach (var record in unresolved)
        {
            RequireUnresolved(record);
        }

        var result = new List<UnresolvedClassification>();
        foreach (var group in unresolved.GroupBy(record =>
            record.CompilationContextRef + "\0" + LocatorKey(record.CandidateLocator)))
        {
            var variants = group
                .GroupBy(UnresolvedSignature, StringComparer.Ordinal)
                .Select(variant => variant.First())
                .ToArray();
            if (variants.Length != 1)
            {
                throw Unrepresentable();
            }

            RequireUnresolved(variants[0]);
            result.Add(variants[0]);
        }

        return result
            .OrderBy(record => record.CompilationContextRef, StringComparer.Ordinal)
            .ThenBy(record => record.CandidateLocator, CandidateLocatorComparer.Instance)
            .ToImmutableArray();
    }

    private static void RequireTarget(TargetClassification target)
    {
        RequireSymbolRef(target.SymbolRef);
        RequireEnum(target.PrimaryKind);
        RequireEnum(target.Origin);
        RequireEnum(target.SupportStatus);
        if (target.SkipReason is { } skip)
        {
            RequireEnum(skip);
        }

        var traits = target.Traits
            .Select(ClassificationVocabulary.GetId)
            .ToArray();
        if (!traits.SequenceEqual(traits.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)))
        {
            throw Unrepresentable();
        }

        var valid = target.SupportStatus switch
        {
            SupportStatus.Supported =>
                target.SkipReason is null
                && target.PrimaryKind != PrimarySymbolKind.Unknown
                && target.Origin is ClassificationOrigin.Source
                    or ClassificationOrigin.SourceGenerator
                    or ClassificationOrigin.ToolGenerated,
            SupportStatus.Unsupported =>
                target.PrimaryKind == PrimarySymbolKind.Unknown
                && target.SkipReason == SkipReason.UnsupportedSymbolKind
                && target.Origin is not ClassificationOrigin.Unknown
                    and not ClassificationOrigin.CompilerSynthesized,
            SupportStatus.Ambiguous =>
                target.PrimaryKind != PrimarySymbolKind.Unknown
                && target.Origin != ClassificationOrigin.Unknown
                && (target.SkipReason == SkipReason.AmbiguousPartialDeclaration
                    || target.SkipReason == SkipReason.AmbiguousMixedOrigin
                        && target.Origin == ClassificationOrigin.Mixed),
            SupportStatus.UnavailableContext =>
                target.SkipReason == SkipReason.UnavailableGeneratedProvenance
                    && target.Origin == ClassificationOrigin.Unknown
                || target.SkipReason == SkipReason.UnavailableSemanticContext
                    && target.Origin is not ClassificationOrigin.Unknown
                        and not ClassificationOrigin.CompilerSynthesized,
            _ => false,
        };
        if (!valid)
        {
            throw Unrepresentable();
        }
    }

    private static void RequireComponent(ComponentClassification component)
    {
        RequireSymbolRef(component.ParentSymbolRef);
        RequireEnum(component.ComponentKind);
        RequireEnum(component.Origin);
        RequireEnum(component.SupportStatus);
        if (component.SkipReason is { } skip)
        {
            RequireEnum(skip);
        }

        if (!IsValidComponentIdentity(component.ComponentKind, component.Identity))
        {
            throw Unrepresentable();
        }

        if (!AllowsComponentStatus(
            component.ComponentKind,
            component.SupportStatus))
        {
            throw Unrepresentable();
        }

        var valid = component.SupportStatus switch
        {
            SupportStatus.Supported =>
                component.SkipReason is null
                && !IsSynthesized(component.ComponentKind)
                && !IsNonDocumentation(component.ComponentKind)
                && component.ComponentKind != ComponentKind.Unknown
                && component.Origin is ClassificationOrigin.Source
                    or ClassificationOrigin.SourceGenerator
                    or ClassificationOrigin.ToolGenerated,
            SupportStatus.Unsupported =>
                component.ComponentKind == ComponentKind.Unknown
                && component.SkipReason == SkipReason.UnsupportedComponentKind
                && component.Origin is not ClassificationOrigin.Unknown
                    and not ClassificationOrigin.CompilerSynthesized,
            SupportStatus.Ambiguous =>
                component.ComponentKind != ComponentKind.Unknown
                && component.Origin == ClassificationOrigin.Mixed
                && component.SkipReason == SkipReason.AmbiguousMixedOrigin,
            SupportStatus.NotApplicable =>
                IsSynthesized(component.ComponentKind)
                    && component.Origin == ClassificationOrigin.CompilerSynthesized
                    && component.SkipReason == SkipReason.NotApplicableSynthesizedNonTarget
                || IsNonDocumentation(component.ComponentKind)
                    && component.Origin is not ClassificationOrigin.Unknown
                        and not ClassificationOrigin.CompilerSynthesized
                    && component.SkipReason
                        == SkipReason.NotApplicableNonDocumentationComponent,
            SupportStatus.UnavailableContext =>
                component.SkipReason == SkipReason.UnavailableGeneratedProvenance
                    && component.Origin == ClassificationOrigin.Unknown
                || component.SkipReason == SkipReason.UnavailableSemanticContext
                    && component.Origin is not ClassificationOrigin.Unknown
                        and not ClassificationOrigin.CompilerSynthesized,
            _ => false,
        };
        if (!valid)
        {
            throw Unrepresentable();
        }
    }

    private static void RequireUnresolved(UnresolvedClassification record)
    {
        RequireContext(record.CompilationContextRef);
        RequireEnum(record.Origin);
        RequireEnum(record.SupportStatus);
        RequireEnum(record.SkipReason);
        RequireLocator(record.CandidateLocator);
        var valid = record.SupportStatus == SupportStatus.UnavailableContext
            && (record.SkipReason == SkipReason.UnavailableDocumentationCommentId
                && record.Origin is not ClassificationOrigin.Unknown
                    and not ClassificationOrigin.CompilerSynthesized
                || record.SkipReason == SkipReason.UnavailableGeneratedProvenance
                    && record.Origin == ClassificationOrigin.Unknown
                || record.SkipReason == SkipReason.UnavailableSemanticContext
                    && record.Origin is not ClassificationOrigin.Unknown
                        and not ClassificationOrigin.CompilerSynthesized);
        if (!valid)
        {
            throw Unrepresentable();
        }
    }

    private static void RequireSymbolRef(SymbolRef symbolRef)
    {
        RequireContext(symbolRef.CompilationContextRef);
        RequireText(symbolRef.DocumentationCommentId);
    }

    private static void RequireContext(string value)
    {
        if (string.IsNullOrEmpty(value)
            || value.Length > 128
            || !IsLowerAlphaNumeric(value[0])
            || value.Any(character =>
                !IsLowerAlphaNumeric(character)
                && character is not '.' and not '_' and not '-'))
        {
            throw Unrepresentable();
        }
    }

    private static void RequireText(string value)
    {
        if (string.IsNullOrEmpty(value) || !IsWellFormedUnicode(value))
        {
            throw Unrepresentable();
        }
    }

    private static void RequireLocator(CandidateLocator locator)
    {
        switch (locator)
        {
            case RepositoryCandidateLocator repository:
                RequireRepositoryIdentity(repository.Path);
                RequireSpan(repository.Span);
                break;
            case GeneratedSourceCandidateLocator generated:
                RequireOpaqueId(generated.GeneratorId, "sgp.");
                RequireOpaqueId(generated.HintNameId, "sgo.");
                RequireSpan(generated.Span);
                break;
            case ToolGeneratedCandidateLocator generated:
                RequireOpaqueId(generated.ProducerId, "tgp.");
                RequireOpaqueId(generated.OutputId, "tgo.");
                RequireSpan(generated.Span);
                break;
            case SyntheticCandidateLocator synthetic:
                RequireContext(synthetic.FixtureId);
                break;
            default:
                throw Unrepresentable();
        }
    }

    private static void RequireRepositoryIdentity(string path)
    {
        RequireText(path);
        if (path.Contains('\0')
            || path.Contains("\\", StringComparison.Ordinal)
            || path.StartsWith("/", StringComparison.Ordinal)
            || path.EndsWith("/", StringComparison.Ordinal)
            || path.Length >= 2
                && IsAsciiLetter(path[0])
                && path[1] == ':')
        {
            throw Unrepresentable();
        }

        var segments = path.Split('/');
        if (segments.Any(segment => segment is "" or "." or ".."))
        {
            throw Unrepresentable();
        }
    }

    private static void RequireOpaqueId(string value, string prefix)
    {
        if (value.Length != prefix.Length + 64
            || !value.StartsWith(prefix, StringComparison.Ordinal)
            || value.AsSpan(prefix.Length).ContainsAnyExcept(
                "0123456789abcdef".AsSpan()))
        {
            throw Unrepresentable();
        }
    }

    private static void RequireSpan(Utf16Span? span)
    {
        if (span is { Start: < 0 }
            || span is { } value && value.End < value.Start)
        {
            throw Unrepresentable();
        }
    }

    private static void RequireEnum<T>(T value) where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw Unrepresentable();
        }
    }

    private static bool IsValidComponentIdentity(ComponentKind kind, string identity) =>
        kind switch
        {
            ComponentKind.Parameter => IsOrdinalIdentity(identity, "parameter/"),
            ComponentKind.TypeParameter => IsOrdinalIdentity(identity, "type-parameter/"),
            ComponentKind.Return => identity == "return",
            ComponentKind.Value => identity == "value",
            ComponentKind.AccessorGet => identity == "accessor/get",
            ComponentKind.AccessorSet => identity == "accessor/set",
            ComponentKind.AccessorInit => identity == "accessor/init",
            ComponentKind.AccessorAdd => identity == "accessor/add",
            ComponentKind.AccessorRemove => identity == "accessor/remove",
            ComponentKind.BackingField => identity == "backing-field",
            ComponentKind.SynthesizedRecordPositionalProperty =>
                IsOrdinalIdentity(identity, "synthesized/record-positional-property/"),
            ComponentKind.SynthesizedImplicitConstructor =>
                identity == "synthesized/implicit-constructor",
            ComponentKind.SynthesizedRecordCopyConstructor =>
                identity == "synthesized/record-copy-constructor",
            ComponentKind.SynthesizedDelegateInvoke =>
                identity == "synthesized/delegate-invoke",
            ComponentKind.SynthesizedDelegateBeginInvoke =>
                identity == "synthesized/delegate-begin-invoke",
            ComponentKind.SynthesizedDelegateEndInvoke =>
                identity == "synthesized/delegate-end-invoke",
            ComponentKind.Unknown => IsOrdinalIdentity(identity, "unknown/"),
            _ => false,
        };

    private static bool AllowsParent(
        ComponentKind componentKind,
        PrimarySymbolKind parentKind) =>
        componentKind switch
        {
            ComponentKind.Parameter =>
                parentKind is PrimarySymbolKind.Constructor
                    or PrimarySymbolKind.Method
                    or PrimarySymbolKind.Operator
                    or PrimarySymbolKind.Conversion
                    or PrimarySymbolKind.Indexer
                    or PrimarySymbolKind.Delegate,
            ComponentKind.TypeParameter =>
                parentKind is PrimarySymbolKind.Class
                    or PrimarySymbolKind.Struct
                    or PrimarySymbolKind.Interface
                    or PrimarySymbolKind.Delegate
                    or PrimarySymbolKind.Method,
            ComponentKind.Return =>
                parentKind is PrimarySymbolKind.Method
                    or PrimarySymbolKind.Operator
                    or PrimarySymbolKind.Conversion
                    or PrimarySymbolKind.Delegate,
            ComponentKind.Value
                or ComponentKind.AccessorGet
                or ComponentKind.AccessorSet
                or ComponentKind.AccessorInit =>
                parentKind is PrimarySymbolKind.Property
                    or PrimarySymbolKind.Indexer,
            ComponentKind.AccessorAdd
                or ComponentKind.AccessorRemove =>
                parentKind == PrimarySymbolKind.Event,
            ComponentKind.BackingField =>
                parentKind is PrimarySymbolKind.Property
                    or PrimarySymbolKind.Event,
            ComponentKind.SynthesizedRecordPositionalProperty
                or ComponentKind.SynthesizedImplicitConstructor
                or ComponentKind.SynthesizedRecordCopyConstructor =>
                parentKind is PrimarySymbolKind.Class
                    or PrimarySymbolKind.Struct,
            ComponentKind.SynthesizedDelegateInvoke
                or ComponentKind.SynthesizedDelegateBeginInvoke
                or ComponentKind.SynthesizedDelegateEndInvoke =>
                parentKind == PrimarySymbolKind.Delegate,
            ComponentKind.Unknown => true,
            _ => false,
        };

    private static bool AllowsComponentStatus(
        ComponentKind componentKind,
        SupportStatus status) =>
        componentKind switch
        {
            ComponentKind.Parameter
                or ComponentKind.TypeParameter
                or ComponentKind.Return
                or ComponentKind.Value =>
                status is SupportStatus.Supported
                    or SupportStatus.UnavailableContext
                    or SupportStatus.Ambiguous,
            ComponentKind.AccessorGet
                or ComponentKind.AccessorSet
                or ComponentKind.AccessorInit
                or ComponentKind.AccessorAdd
                or ComponentKind.AccessorRemove
                or ComponentKind.BackingField =>
                status is SupportStatus.NotApplicable
                    or SupportStatus.Ambiguous,
            ComponentKind.SynthesizedRecordPositionalProperty
                or ComponentKind.SynthesizedImplicitConstructor
                or ComponentKind.SynthesizedRecordCopyConstructor
                or ComponentKind.SynthesizedDelegateInvoke
                or ComponentKind.SynthesizedDelegateBeginInvoke
                or ComponentKind.SynthesizedDelegateEndInvoke =>
                status == SupportStatus.NotApplicable,
            ComponentKind.Unknown => status == SupportStatus.Unsupported,
            _ => false,
        };

    private static bool AllowsRelationDomain(
        RelationKind relationKind,
        PrimarySymbolKind sourceKind,
        PrimarySymbolKind targetKind)
    {
        var ordinaryMember = sourceKind is PrimarySymbolKind.Method
            or PrimarySymbolKind.Property
            or PrimarySymbolKind.Indexer
            or PrimarySymbolKind.Event;
        var ordinaryTarget = targetKind is PrimarySymbolKind.Method
            or PrimarySymbolKind.Property
            or PrimarySymbolKind.Indexer
            or PrimarySymbolKind.Event;
        var interfaceMember = ordinaryMember
            || sourceKind is PrimarySymbolKind.Operator
                or PrimarySymbolKind.Conversion;
        var interfaceTarget = ordinaryTarget
            || targetKind is PrimarySymbolKind.Operator
                or PrimarySymbolKind.Conversion;
        return relationKind switch
        {
            RelationKind.Overrides => ordinaryMember && ordinaryTarget,
            RelationKind.ImplicitInterfaceImplementation
                or RelationKind.ExplicitInterfaceImplementation =>
                interfaceMember && interfaceTarget,
            RelationKind.InheritedInterfaceMember =>
                sourceKind == PrimarySymbolKind.Interface
                && interfaceTarget,
            _ => false,
        };
    }

    private static bool IsOrdinalIdentity(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.Ordinal)
        && value.Length > prefix.Length
        && value.AsSpan(prefix.Length).IndexOfAnyExceptInRange('0', '9') < 0;

    private static bool IsSynthesized(ComponentKind kind) =>
        kind is ComponentKind.SynthesizedRecordPositionalProperty
            or ComponentKind.SynthesizedImplicitConstructor
            or ComponentKind.SynthesizedRecordCopyConstructor
            or ComponentKind.SynthesizedDelegateInvoke
            or ComponentKind.SynthesizedDelegateBeginInvoke
            or ComponentKind.SynthesizedDelegateEndInvoke;

    private static bool IsNonDocumentation(ComponentKind kind) =>
        kind is ComponentKind.AccessorGet
            or ComponentKind.AccessorSet
            or ComponentKind.AccessorInit
            or ComponentKind.AccessorAdd
            or ComponentKind.AccessorRemove
            or ComponentKind.BackingField;

    private static bool IsLowerAlphaNumeric(char value) =>
        value is >= 'a' and <= 'z' or >= '0' and <= '9';

    private static bool IsAsciiLetter(char value) =>
        value is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    private static bool IsWellFormedUnicode(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (++index >= value.Length || !char.IsLowSurrogate(value[index]))
                {
                    return false;
                }
            }
            else if (char.IsLowSurrogate(value[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static string TargetSignature(TargetClassification target) =>
        string.Join(
            "\0",
            ClassificationVocabulary.GetId(target.PrimaryKind),
            string.Join(",", target.Traits.Select(ClassificationVocabulary.GetId)),
            ClassificationVocabulary.GetId(target.Origin),
            ClassificationVocabulary.GetId(target.SupportStatus),
            target.SkipReason is { } skip ? ClassificationVocabulary.GetId(skip) : string.Empty);

    private static string ComponentSignature(ComponentClassification component) =>
        string.Join(
            "\0",
            ClassificationVocabulary.GetId(component.Origin),
            ClassificationVocabulary.GetId(component.SupportStatus),
            component.SkipReason is { } skip
                ? ClassificationVocabulary.GetId(skip)
                : string.Empty);

    private static string UnknownCandidateSignature(
        ComponentClassificationCandidate candidate) =>
        string.Join(
            "\0",
            ClassificationVocabulary.GetId(candidate.Origin),
            candidate.GeneratedProvenanceAvailable ? "1" : "0",
            candidate.SemanticContextAvailable ? "1" : "0",
            candidate.PartialAmbiguous ? "1" : "0");

    private static string UnresolvedSignature(UnresolvedClassification unresolved) =>
        string.Join(
            "\0",
            ClassificationVocabulary.GetId(unresolved.Origin),
            ClassificationVocabulary.GetId(unresolved.SupportStatus),
            ClassificationVocabulary.GetId(unresolved.SkipReason));

    private static string LocatorKey(CandidateLocator locator) => locator switch
    {
        RepositoryCandidateLocator repository =>
            "0\0" + repository.Path + "\0" + SpanKey(repository.Span),
        GeneratedSourceCandidateLocator generated =>
            "1\0" + generated.GeneratorId + "\0" + generated.HintNameId + "\0"
            + SpanKey(generated.Span),
        ToolGeneratedCandidateLocator generated =>
            "2\0" + generated.ProducerId + "\0" + generated.OutputId + "\0"
            + SpanKey(generated.Span),
        SyntheticCandidateLocator synthetic => "3\0" + synthetic.FixtureId,
        _ => throw Unrepresentable(),
    };

    private static string SpanKey(Utf16Span? span) =>
        span is { } value
            ? $"1\0{value.Start:D10}\0{value.End:D10}"
            : "0";

    private static ClassificationUnrepresentableException Unrepresentable() => new();

    private sealed class CandidateLocatorComparer : IComparer<CandidateLocator>
    {
        public static CandidateLocatorComparer Instance { get; } = new();

        public int Compare(CandidateLocator? left, CandidateLocator? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            return string.CompareOrdinal(LocatorKey(left), LocatorKey(right));
        }
    }
}
