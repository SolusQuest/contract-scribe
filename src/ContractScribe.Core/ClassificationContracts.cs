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

public readonly record struct SymbolRef(
    string CompilationContextRef,
    string DocumentationCommentId);

public readonly record struct Utf16Span(int Start, int End);

public abstract record CandidateLocator;

public sealed record RepositoryCandidateLocator(
    string Path,
    Utf16Span? Span = null) : CandidateLocator;

public sealed record GeneratedSourceCandidateLocator(
    string GeneratorId,
    string HintNameId,
    Utf16Span? Span = null) : CandidateLocator;

public sealed record ToolGeneratedCandidateLocator(
    string ProducerId,
    string OutputId,
    Utf16Span? Span = null) : CandidateLocator;

public sealed record SyntheticCandidateLocator(
    string FixtureId) : CandidateLocator;

public sealed record TargetClassification(
    SymbolRef SymbolRef,
    PrimarySymbolKind PrimaryKind,
    ImmutableArray<SymbolTrait> Traits,
    ClassificationOrigin Origin,
    SupportStatus SupportStatus,
    SkipReason? SkipReason = null);

public sealed record ComponentClassification(
    SymbolRef ParentSymbolRef,
    ComponentKind ComponentKind,
    string Identity,
    ClassificationOrigin Origin,
    SupportStatus SupportStatus,
    SkipReason? SkipReason = null);

public sealed record RelationObservation(
    RelationKind RelationKind,
    SymbolRef SourceSymbolRef,
    SymbolRef TargetSymbolRef);

public sealed record UnresolvedClassification(
    string CompilationContextRef,
    ClassificationOrigin Origin,
    SupportStatus SupportStatus,
    SkipReason SkipReason,
    CandidateLocator CandidateLocator);

public sealed record ClassificationSet(
    TargetProfile TargetProfile,
    ImmutableArray<TargetClassification> Targets,
    ImmutableArray<ComponentClassification> Components,
    ImmutableArray<RelationObservation> Relations,
    ImmutableArray<UnresolvedClassification> Unresolved);

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

    public static ClassificationOutcome Success(
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
