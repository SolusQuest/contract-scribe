using System.Collections.Immutable;

namespace ContractScribe.Core;

public enum TargetProfile
{
    ExternalApi,
    AssemblyVisible,
}

public enum PrimarySymbolKind
{
    Class,
    Struct,
    Interface,
    Enum,
    Delegate,
    Constructor,
    Method,
    Operator,
    Conversion,
    Property,
    Indexer,
    Field,
    EnumMember,
    Event,
    Unknown,
}

public enum SymbolTrait
{
    Generic,
    RecordClass,
    RecordStruct,
    RefStruct,
    Static,
    Abstract,
    Virtual,
    Sealed,
    Extension,
    Async,
    Iterator,
    Required,
    InitOnly,
    Partial,
}

public enum ComponentKind
{
    Parameter,
    TypeParameter,
    Return,
    Value,
    AccessorGet,
    AccessorSet,
    AccessorInit,
    AccessorAdd,
    AccessorRemove,
    BackingField,
    SynthesizedRecordPositionalProperty,
    SynthesizedImplicitConstructor,
    SynthesizedRecordCopyConstructor,
    SynthesizedDelegateInvoke,
    SynthesizedDelegateBeginInvoke,
    SynthesizedDelegateEndInvoke,
    Unknown,
}

public enum RelationKind
{
    Overrides,
    ImplicitInterfaceImplementation,
    ExplicitInterfaceImplementation,
    InheritedInterfaceMember,
}

public enum ClassificationOrigin
{
    Source,
    SourceGenerator,
    ToolGenerated,
    CompilerSynthesized,
    Mixed,
    Unknown,
}

public enum SupportStatus
{
    Supported,
    Unsupported,
    Ambiguous,
    NotApplicable,
    UnavailableContext,
}

public enum SkipReason
{
    UnsupportedSymbolKind,
    UnsupportedComponentKind,
    AmbiguousPartialDeclaration,
    AmbiguousMixedOrigin,
    NotApplicableSynthesizedNonTarget,
    NotApplicableNonDocumentationComponent,
    UnavailableDocumentationCommentId,
    UnavailableGeneratedProvenance,
    UnavailableSemanticContext,
}

public static class ClassificationVocabulary
{
    public const string UnrepresentableRunFailure = "run.classification.unrepresentable";

    public static string GetId(TargetProfile value) => value switch
    {
        TargetProfile.ExternalApi => "profile.external-api",
        TargetProfile.AssemblyVisible => "profile.assembly-visible",
        _ => throw Unknown(value),
    };

    public static string GetId(PrimarySymbolKind value) => value switch
    {
        PrimarySymbolKind.Class => "symbol.type.class",
        PrimarySymbolKind.Struct => "symbol.type.struct",
        PrimarySymbolKind.Interface => "symbol.type.interface",
        PrimarySymbolKind.Enum => "symbol.type.enum",
        PrimarySymbolKind.Delegate => "symbol.type.delegate",
        PrimarySymbolKind.Constructor => "symbol.member.constructor",
        PrimarySymbolKind.Method => "symbol.member.method",
        PrimarySymbolKind.Operator => "symbol.member.operator",
        PrimarySymbolKind.Conversion => "symbol.member.conversion",
        PrimarySymbolKind.Property => "symbol.member.property",
        PrimarySymbolKind.Indexer => "symbol.member.indexer",
        PrimarySymbolKind.Field => "symbol.member.field",
        PrimarySymbolKind.EnumMember => "symbol.member.enum-member",
        PrimarySymbolKind.Event => "symbol.member.event",
        PrimarySymbolKind.Unknown => "symbol.unknown",
        _ => throw Unknown(value),
    };

    public static string GetId(SymbolTrait value) => value switch
    {
        SymbolTrait.Generic => "trait.generic",
        SymbolTrait.RecordClass => "trait.record-class",
        SymbolTrait.RecordStruct => "trait.record-struct",
        SymbolTrait.RefStruct => "trait.ref-struct",
        SymbolTrait.Static => "trait.static",
        SymbolTrait.Abstract => "trait.abstract",
        SymbolTrait.Virtual => "trait.virtual",
        SymbolTrait.Sealed => "trait.sealed",
        SymbolTrait.Extension => "trait.extension",
        SymbolTrait.Async => "trait.async",
        SymbolTrait.Iterator => "trait.iterator",
        SymbolTrait.Required => "trait.required",
        SymbolTrait.InitOnly => "trait.init-only",
        SymbolTrait.Partial => "trait.partial",
        _ => throw Unknown(value),
    };

    public static string GetId(ComponentKind value) => value switch
    {
        ComponentKind.Parameter => "component.parameter",
        ComponentKind.TypeParameter => "component.type-parameter",
        ComponentKind.Return => "component.return",
        ComponentKind.Value => "component.value",
        ComponentKind.AccessorGet => "component.accessor.get",
        ComponentKind.AccessorSet => "component.accessor.set",
        ComponentKind.AccessorInit => "component.accessor.init",
        ComponentKind.AccessorAdd => "component.accessor.add",
        ComponentKind.AccessorRemove => "component.accessor.remove",
        ComponentKind.BackingField => "component.backing-field",
        ComponentKind.SynthesizedRecordPositionalProperty => "component.synthesized.record-positional-property",
        ComponentKind.SynthesizedImplicitConstructor => "component.synthesized.implicit-constructor",
        ComponentKind.SynthesizedRecordCopyConstructor => "component.synthesized.record-copy-constructor",
        ComponentKind.SynthesizedDelegateInvoke => "component.synthesized.delegate-invoke",
        ComponentKind.SynthesizedDelegateBeginInvoke => "component.synthesized.delegate-begin-invoke",
        ComponentKind.SynthesizedDelegateEndInvoke => "component.synthesized.delegate-end-invoke",
        ComponentKind.Unknown => "component.unknown",
        _ => throw Unknown(value),
    };

    public static string GetId(RelationKind value) => value switch
    {
        RelationKind.Overrides => "relation.overrides",
        RelationKind.ImplicitInterfaceImplementation => "relation.implicit-interface-implementation",
        RelationKind.ExplicitInterfaceImplementation => "relation.explicit-interface-implementation",
        RelationKind.InheritedInterfaceMember => "relation.inherited-interface-member",
        _ => throw Unknown(value),
    };

    public static string GetId(ClassificationOrigin value) => value switch
    {
        ClassificationOrigin.Source => "origin.source",
        ClassificationOrigin.SourceGenerator => "origin.source-generator",
        ClassificationOrigin.ToolGenerated => "origin.tool-generated",
        ClassificationOrigin.CompilerSynthesized => "origin.compiler-synthesized",
        ClassificationOrigin.Mixed => "origin.mixed",
        ClassificationOrigin.Unknown => "origin.unknown",
        _ => throw Unknown(value),
    };

    public static string GetId(SupportStatus value) => value switch
    {
        SupportStatus.Supported => "support.supported",
        SupportStatus.Unsupported => "support.unsupported",
        SupportStatus.Ambiguous => "support.ambiguous",
        SupportStatus.NotApplicable => "support.not-applicable",
        SupportStatus.UnavailableContext => "support.unavailable-context",
        _ => throw Unknown(value),
    };

    public static string GetId(SkipReason value) => value switch
    {
        SkipReason.UnsupportedSymbolKind => "skip.unsupported.symbol-kind",
        SkipReason.UnsupportedComponentKind => "skip.unsupported.component-kind",
        SkipReason.AmbiguousPartialDeclaration => "skip.ambiguous.partial-declaration",
        SkipReason.AmbiguousMixedOrigin => "skip.ambiguous.mixed-origin",
        SkipReason.NotApplicableSynthesizedNonTarget => "skip.not-applicable.synthesized-non-target",
        SkipReason.NotApplicableNonDocumentationComponent => "skip.not-applicable.non-documentation-component",
        SkipReason.UnavailableDocumentationCommentId => "skip.unavailable.documentation-comment-id",
        SkipReason.UnavailableGeneratedProvenance => "skip.unavailable.generated-provenance",
        SkipReason.UnavailableSemanticContext => "skip.unavailable.semantic-context",
        _ => throw Unknown(value),
    };

    private static ArgumentOutOfRangeException Unknown<T>(T value) where T : struct, Enum =>
        new(nameof(value), value, "The value is outside the closed classification vocabulary.");
}

public readonly record struct SymbolRef
{
    internal SymbolRef(
        string compilationContextRef,
        string documentationCommentId)
    {
        CompilationContextRef = compilationContextRef;
        DocumentationCommentId = documentationCommentId;
    }

    public string CompilationContextRef { get; }

    public string DocumentationCommentId { get; }
}

public readonly record struct Utf16Span
{
    internal Utf16Span(int start, int end)
    {
        Start = start;
        End = end;
    }

    public int Start { get; }

    public int End { get; }
}

public abstract class CandidateLocator : IEquatable<CandidateLocator>
{
    private protected CandidateLocator()
    {
    }

    public bool Equals(CandidateLocator? other) =>
        ReferenceEquals(this, other)
        || other is not null
            && GetType() == other.GetType()
            && EqualsCore(other);

    public override bool Equals(object? obj) =>
        obj is CandidateLocator other && Equals(other);

    public abstract override int GetHashCode();

    protected abstract bool EqualsCore(CandidateLocator other);

    public static bool operator ==(
        CandidateLocator? left,
        CandidateLocator? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(
        CandidateLocator? left,
        CandidateLocator? right) =>
        !(left == right);
}

public sealed class RepositoryCandidateLocator : CandidateLocator
{
    internal RepositoryCandidateLocator(
        string path,
        Utf16Span? span = null)
    {
        Path = path;
        Span = span;
    }

    public string Path { get; }

    public Utf16Span? Span { get; }

    protected override bool EqualsCore(CandidateLocator other)
    {
        var repository = (RepositoryCandidateLocator)other;
        return string.Equals(Path, repository.Path, StringComparison.Ordinal)
            && Span == repository.Span;
    }

    public override int GetHashCode() =>
        HashCode.Combine(StringComparer.Ordinal.GetHashCode(Path), Span);

    public override string ToString() =>
        $"RepositoryCandidateLocator {{ Path = {Path}, Span = {Span} }}";
}

public sealed class GeneratedSourceCandidateLocator : CandidateLocator
{
    internal GeneratedSourceCandidateLocator(
        string generatorId,
        string hintNameId,
        Utf16Span? span = null)
    {
        GeneratorId = generatorId;
        HintNameId = hintNameId;
        Span = span;
    }

    public string GeneratorId { get; }

    public string HintNameId { get; }

    public Utf16Span? Span { get; }

    protected override bool EqualsCore(CandidateLocator other)
    {
        var generated = (GeneratedSourceCandidateLocator)other;
        return string.Equals(
                GeneratorId,
                generated.GeneratorId,
                StringComparison.Ordinal)
            && string.Equals(
                HintNameId,
                generated.HintNameId,
                StringComparison.Ordinal)
            && Span == generated.Span;
    }

    public override int GetHashCode() =>
        HashCode.Combine(
            StringComparer.Ordinal.GetHashCode(GeneratorId),
            StringComparer.Ordinal.GetHashCode(HintNameId),
            Span);

    public override string ToString() =>
        $"GeneratedSourceCandidateLocator {{ GeneratorId = {GeneratorId}, HintNameId = {HintNameId}, Span = {Span} }}";
}

public sealed class ToolGeneratedCandidateLocator : CandidateLocator
{
    internal ToolGeneratedCandidateLocator(
        string producerId,
        string outputId,
        Utf16Span? span = null)
    {
        ProducerId = producerId;
        OutputId = outputId;
        Span = span;
    }

    public string ProducerId { get; }

    public string OutputId { get; }

    public Utf16Span? Span { get; }

    protected override bool EqualsCore(CandidateLocator other)
    {
        var generated = (ToolGeneratedCandidateLocator)other;
        return string.Equals(
                ProducerId,
                generated.ProducerId,
                StringComparison.Ordinal)
            && string.Equals(
                OutputId,
                generated.OutputId,
                StringComparison.Ordinal)
            && Span == generated.Span;
    }

    public override int GetHashCode() =>
        HashCode.Combine(
            StringComparer.Ordinal.GetHashCode(ProducerId),
            StringComparer.Ordinal.GetHashCode(OutputId),
            Span);

    public override string ToString() =>
        $"ToolGeneratedCandidateLocator {{ ProducerId = {ProducerId}, OutputId = {OutputId}, Span = {Span} }}";
}

public sealed class SyntheticCandidateLocator : CandidateLocator
{
    internal SyntheticCandidateLocator(string fixtureId)
    {
        FixtureId = fixtureId;
    }

    public string FixtureId { get; }

    protected override bool EqualsCore(CandidateLocator other) =>
        string.Equals(
            FixtureId,
            ((SyntheticCandidateLocator)other).FixtureId,
            StringComparison.Ordinal);

    public override int GetHashCode() =>
        StringComparer.Ordinal.GetHashCode(FixtureId);

    public override string ToString() =>
        $"SyntheticCandidateLocator {{ FixtureId = {FixtureId} }}";
}

public sealed record TargetClassification
{
    internal TargetClassification(
        SymbolRef symbolRef,
        PrimarySymbolKind primaryKind,
        ImmutableArray<SymbolTrait> traits,
        ClassificationOrigin origin,
        SupportStatus supportStatus,
        SkipReason? skipReason = null)
    {
        SymbolRef = symbolRef;
        PrimaryKind = primaryKind;
        Traits = traits;
        Origin = origin;
        SupportStatus = supportStatus;
        SkipReason = skipReason;
    }

    public SymbolRef SymbolRef { get; }

    public PrimarySymbolKind PrimaryKind { get; }

    public ImmutableArray<SymbolTrait> Traits { get; }

    public ClassificationOrigin Origin { get; }

    public SupportStatus SupportStatus { get; }

    public SkipReason? SkipReason { get; }
}

public sealed record ComponentClassification
{
    internal ComponentClassification(
        SymbolRef parentSymbolRef,
        ComponentKind componentKind,
        string identity,
        ClassificationOrigin origin,
        SupportStatus supportStatus,
        SkipReason? skipReason = null)
    {
        ParentSymbolRef = parentSymbolRef;
        ComponentKind = componentKind;
        Identity = identity;
        Origin = origin;
        SupportStatus = supportStatus;
        SkipReason = skipReason;
    }

    public SymbolRef ParentSymbolRef { get; }

    public ComponentKind ComponentKind { get; }

    public string Identity { get; }

    public ClassificationOrigin Origin { get; }

    public SupportStatus SupportStatus { get; }

    public SkipReason? SkipReason { get; }
}

public sealed record RelationObservation
{
    internal RelationObservation(
        RelationKind relationKind,
        SymbolRef sourceSymbolRef,
        SymbolRef targetSymbolRef)
    {
        RelationKind = relationKind;
        SourceSymbolRef = sourceSymbolRef;
        TargetSymbolRef = targetSymbolRef;
    }

    public RelationKind RelationKind { get; }

    public SymbolRef SourceSymbolRef { get; }

    public SymbolRef TargetSymbolRef { get; }
}

public sealed record UnresolvedClassification
{
    internal UnresolvedClassification(
        string compilationContextRef,
        ClassificationOrigin origin,
        SupportStatus supportStatus,
        SkipReason skipReason,
        CandidateLocator candidateLocator)
    {
        CompilationContextRef = compilationContextRef;
        Origin = origin;
        SupportStatus = supportStatus;
        SkipReason = skipReason;
        CandidateLocator = candidateLocator;
    }

    public string CompilationContextRef { get; }

    public ClassificationOrigin Origin { get; }

    public SupportStatus SupportStatus { get; }

    public SkipReason SkipReason { get; }

    public CandidateLocator CandidateLocator { get; }
}

public sealed record ClassificationSet
{
    internal ClassificationSet(
        TargetProfile targetProfile,
        ImmutableArray<TargetClassification> targets,
        ImmutableArray<ComponentClassification> components,
        ImmutableArray<RelationObservation> relations,
        ImmutableArray<UnresolvedClassification> unresolved)
    {
        TargetProfile = targetProfile;
        Targets = targets;
        Components = components;
        Relations = relations;
        Unresolved = unresolved;
    }

    public TargetProfile TargetProfile { get; }

    public ImmutableArray<TargetClassification> Targets { get; }

    public ImmutableArray<ComponentClassification> Components { get; }

    public ImmutableArray<RelationObservation> Relations { get; }

    public ImmutableArray<UnresolvedClassification> Unresolved { get; }
}

public enum ClassificationRunStatus
{
    Success,
    Failure,
    Cancelled,
}

public sealed record ClassificationRunFailure(string Stage, string Code);

public sealed record ClassificationDiagnostic(string Stage, string Code, string Severity);

public sealed class ClassificationOutcome
{
    private ClassificationOutcome(
        ClassificationRunStatus status,
        ClassificationSet? classificationSet,
        ClassificationRunFailure? primaryFailure,
        ImmutableArray<ClassificationDiagnostic> diagnostics)
    {
        Status = status;
        ClassificationSet = classificationSet;
        PrimaryFailure = primaryFailure;
        Diagnostics = diagnostics;
    }

    public ClassificationRunStatus Status { get; }

    public ClassificationSet? ClassificationSet { get; }

    public ClassificationRunFailure? PrimaryFailure { get; }

    public ImmutableArray<ClassificationDiagnostic> Diagnostics { get; }

    internal static ClassificationOutcome Success(
        ClassificationSet classificationSet,
        IEnumerable<ClassificationDiagnostic>? diagnostics = null) =>
        new(
            ClassificationRunStatus.Success,
            classificationSet ?? throw new ArgumentNullException(nameof(classificationSet)),
            null,
            diagnostics?.ToImmutableArray() ?? []);

    public static ClassificationOutcome Failure(
        IEnumerable<ClassificationDiagnostic>? diagnostics = null) =>
        new(
            ClassificationRunStatus.Failure,
            null,
            new ClassificationRunFailure(
                "classification-normalization",
                ClassificationVocabulary.UnrepresentableRunFailure),
            diagnostics?.ToImmutableArray() ?? []);

    public static ClassificationOutcome Cancelled(
        IEnumerable<ClassificationDiagnostic>? diagnostics = null) =>
        new(
            ClassificationRunStatus.Cancelled,
            null,
            null,
            diagnostics?.ToImmutableArray() ?? []);
}
