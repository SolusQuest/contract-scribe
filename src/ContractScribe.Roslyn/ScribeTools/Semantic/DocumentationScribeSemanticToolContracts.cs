using System.Collections.Immutable;
using ContractScribe.Core;

namespace ContractScribe.Roslyn;

public static class DocumentationScribeSemanticToolSelection
{
    public const string SelectionId = "semantic-selection.method-evidence-v1";
    public const string OperationId = "get-target-evidence";
    public const string VocabularyId = "semantic-vocabulary.method-evidence-v1";
    public const string OrderingId = "semantic-order.core-doc-relation-test-usage-v1";

    public static ImmutableArray<string> OperationIds { get; } = [OperationId];

    public static ImmutableArray<PrimarySymbolKind> SupportedTargetKinds { get; } =
        [PrimarySymbolKind.Method];

    public static ImmutableArray<RelationKind> SupportedRelations { get; } =
    [
        RelationKind.Overrides,
        RelationKind.ImplicitInterfaceImplementation,
        RelationKind.ExplicitInterfaceImplementation,
        RelationKind.InheritedInterfaceMember,
    ];

    public static ImmutableArray<DocumentationScribeSemanticTestMarker> TestMarkers { get; } =
    [
        new("Xunit.FactAttribute", "xunit.core"),
        new("Xunit.TheoryAttribute", "xunit.core"),
        new("Xunit.FactAttribute", "xunit.v3.core"),
        new("Xunit.TheoryAttribute", "xunit.v3.core"),
        new("NUnit.Framework.TestAttribute", "nunit.framework"),
        new(
            "Microsoft.VisualStudio.TestTools.UnitTesting.TestMethodAttribute",
            "Microsoft.VisualStudio.TestPlatform.TestFramework"),
    ];
}

public sealed record DocumentationScribeSemanticTestMarker(
    string AttributeMetadataName,
    string AssemblySimpleName);

public sealed record DocumentationScribeSemanticToolLimits
{
    internal DocumentationScribeSemanticToolLimits(
        int maximumPageSize,
        int maximumOptionalItems,
        int maximumResultUtf8Bytes,
        int maximumSourceFileUtf8Bytes,
        int maximumIncludedSourceUtf8Bytes,
        int maximumCompilations,
        int maximumSourceTrees,
        int maximumSyntaxNodes,
        int maximumElapsedMilliseconds)
    {
        if (maximumPageSize is < 1 or > 100
            || maximumOptionalItems is < 1 or > 4096
            || maximumResultUtf8Bytes is < 1024 or > 16_777_216
            || maximumSourceFileUtf8Bytes is < 1024 or > 33_554_432
            || maximumIncludedSourceUtf8Bytes is < 32 or > 65_536
            || maximumCompilations is < 1 or > 256
            || maximumSourceTrees is < 1 or > 4096
            || maximumSyntaxNodes is < 1 or > 5_000_000
            || maximumElapsedMilliseconds is < 1 or > 300_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPageSize));
        }

        MaximumPageSize = maximumPageSize;
        MaximumOptionalItems = maximumOptionalItems;
        MaximumResultUtf8Bytes = maximumResultUtf8Bytes;
        MaximumSourceFileUtf8Bytes = maximumSourceFileUtf8Bytes;
        MaximumIncludedSourceUtf8Bytes = maximumIncludedSourceUtf8Bytes;
        MaximumCompilations = maximumCompilations;
        MaximumSourceTrees = maximumSourceTrees;
        MaximumSyntaxNodes = maximumSyntaxNodes;
        MaximumElapsedMilliseconds = maximumElapsedMilliseconds;
    }

    public int MaximumPageSize { get; }

    public int MaximumOptionalItems { get; }

    public int MaximumResultUtf8Bytes { get; }

    public int MaximumSourceFileUtf8Bytes { get; }

    public int MaximumIncludedSourceUtf8Bytes { get; }

    public int MaximumCompilations { get; }

    public int MaximumSourceTrees { get; }

    public int MaximumSyntaxNodes { get; }

    public int MaximumElapsedMilliseconds { get; }

    public static DocumentationScribeSemanticToolLimits Production { get; } = new(
        maximumPageSize: 20,
        maximumOptionalItems: 256,
        maximumResultUtf8Bytes: 262_144,
        maximumSourceFileUtf8Bytes: 4_194_304,
        maximumIncludedSourceUtf8Bytes: 8_192,
        maximumCompilations: 32,
        maximumSourceTrees: 512,
        maximumSyntaxNodes: 500_000,
        maximumElapsedMilliseconds: 10_000);

    public override string ToString() =>
        $"{nameof(DocumentationScribeSemanticToolLimits)} {{ PageSize = {MaximumPageSize}, OptionalItems = {MaximumOptionalItems}, ResultBytes = {MaximumResultUtf8Bytes}, SourceBytes = {MaximumSourceFileUtf8Bytes}, Compilations = {MaximumCompilations}, SourceTrees = {MaximumSourceTrees}, SyntaxNodes = {MaximumSyntaxNodes}, ElapsedMilliseconds = {MaximumElapsedMilliseconds} }}";
}

public sealed record DocumentationScribeSemanticToolRequest
    : IDocumentationScribeToolRequest<DocumentationScribeSemanticToolResult>
{
    internal DocumentationScribeSemanticToolRequest(
        int pageSize,
        DocumentationScribeContextCursor? cursor)
    {
        PageSize = pageSize;
        Cursor = cursor;
    }

    public int PageSize { get; }

    public DocumentationScribeContextCursor? Cursor { get; }

    public static DocumentationScribeSemanticToolRequest Create(
        int pageSize,
        DocumentationScribeContextCursor? cursor = null)
    {
        if (pageSize is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        return new(pageSize, cursor);
    }

    public override string ToString() =>
        $"{nameof(DocumentationScribeSemanticToolRequest)} {{ PageSize = {PageSize}, HasCursor = {Cursor is not null} }}";
}

public sealed class DocumentationScribeSemanticToolDescriptor
    : IDocumentationScribeToolDescriptor<
        DocumentationScribeSemanticToolRequest,
        DocumentationScribeSemanticToolResult>
{
    public string OperationId => DocumentationScribeSemanticToolSelection.OperationId;

    public override string ToString() => nameof(DocumentationScribeSemanticToolDescriptor);
}

public enum DocumentationScribeSemanticFailureReason
{
    InvalidRequest,
    RequestBindingMismatch,
    StaleContext,
    UnsupportedTargetKind,
    UnsupportedTargetStatus,
    UnsupportedSignature,
    AmbiguousSymbol,
    UnsafeSource,
    SourceDrift,
    IdentityCollision,
    InvalidCursor,
    InternalFailure,
    Cancelled,
    TimedOut,
    BudgetExhausted,
}

public enum DocumentationScribeSemanticIncompleteReason
{
    ItemLimit,
    ResultByteLimit,
    CompilationLimit,
    SourceTreeLimit,
    SyntaxNodeLimit,
    SourceByteLimit,
    UnsupportedEncoding,
    RelationSourceUnavailable,
    DocumentationExcerptUnavailable,
}

public enum DocumentationScribeSemanticEvidenceKind
{
    Documentation,
    Relation,
    Usage,
    TestUsage,
}

public enum DocumentationScribeSemanticUsageKind
{
    NameOf,
    Invocation,
    MemberReference,
}

public enum DocumentationScribeSemanticRelationDirection
{
    Incoming,
    Outgoing,
}

public enum DocumentationScribeSemanticAccessibility
{
    Private,
    PrivateProtected,
    Internal,
    Protected,
    ProtectedInternal,
    Public,
    NotApplicable,
}

public enum DocumentationScribeSemanticRefKind
{
    None,
    Ref,
    Out,
    In,
    RefReadOnly,
}

public enum DocumentationScribeSemanticTypeKind
{
    Named,
    Array,
    Pointer,
    TypeParameter,
    Dynamic,
}

public enum DocumentationScribeSemanticNullability
{
    NotApplicable,
    Oblivious,
    NotAnnotated,
    Annotated,
}

public enum DocumentationScribeSemanticTypeParameterOwner
{
    Method,
    Type,
}

public sealed record DocumentationScribeSemanticTypeFact
{
    internal DocumentationScribeSemanticTypeFact(
        DocumentationScribeSemanticTypeKind kind,
        DocumentationScribeSemanticNullability nullability,
        string? assemblyName,
        string? metadataName,
        ImmutableArray<DocumentationScribeSemanticTypeFact> typeArguments,
        DocumentationScribeSemanticTypeFact? elementType,
        int? arrayRank,
        DocumentationScribeSemanticTypeParameterOwner? typeParameterOwner,
        int? ordinal,
        string? name)
    {
        Kind = kind;
        Nullability = nullability;
        AssemblyName = assemblyName;
        MetadataName = metadataName;
        TypeArguments = typeArguments;
        ElementType = elementType;
        ArrayRank = arrayRank;
        TypeParameterOwner = typeParameterOwner;
        Ordinal = ordinal;
        Name = name;
    }

    public DocumentationScribeSemanticTypeKind Kind { get; }

    public DocumentationScribeSemanticNullability Nullability { get; }

    public string? AssemblyName { get; }

    public string? MetadataName { get; }

    public ImmutableArray<DocumentationScribeSemanticTypeFact> TypeArguments { get; }

    public DocumentationScribeSemanticTypeFact? ElementType { get; }

    public int? ArrayRank { get; }

    public DocumentationScribeSemanticTypeParameterOwner? TypeParameterOwner { get; }

    public int? Ordinal { get; }

    public string? Name { get; }
}

public sealed record DocumentationScribeSemanticParameterFact(
    int Ordinal,
    string Name,
    DocumentationScribeSemanticTypeFact Type,
    DocumentationScribeSemanticRefKind RefKind,
    bool IsParams,
    bool IsOptional);

public sealed record DocumentationScribeSemanticTypeParameterFact(
    int Ordinal,
    string Name,
    bool HasReferenceTypeConstraint,
    bool HasValueTypeConstraint,
    bool HasUnmanagedConstraint,
    bool HasNotNullConstraint,
    bool HasConstructorConstraint,
    DocumentationScribeSemanticNullability ReferenceTypeConstraintNullability,
    ImmutableArray<DocumentationScribeSemanticTypeFact> ConstraintTypes);

public sealed record DocumentationScribeSemanticMethodSummary
{
    internal DocumentationScribeSemanticMethodSummary(
        SymbolRef symbolRef,
        ImmutableArray<SymbolTrait> traits,
        ClassificationOrigin origin,
        string metadataName,
        string containingNamespace,
        SymbolRef containingTypeSymbolRef,
        DocumentationScribeSemanticAccessibility declaredAccessibility,
        DocumentationScribeSemanticAccessibility effectiveAccessibility,
        DocumentationScribeSemanticRefKind returnRefKind,
        DocumentationScribeSemanticTypeFact returnType,
        ImmutableArray<DocumentationScribeSemanticParameterFact> parameters,
        ImmutableArray<DocumentationScribeSemanticTypeParameterFact> typeParameters)
    {
        SymbolRef = symbolRef;
        Traits = traits;
        Origin = origin;
        MetadataName = metadataName;
        ContainingNamespace = containingNamespace;
        ContainingTypeSymbolRef = containingTypeSymbolRef;
        DeclaredAccessibility = declaredAccessibility;
        EffectiveAccessibility = effectiveAccessibility;
        ReturnRefKind = returnRefKind;
        ReturnType = returnType;
        Parameters = parameters;
        TypeParameters = typeParameters;
    }

    public SymbolRef SymbolRef { get; }

    public PrimarySymbolKind PrimaryKind => PrimarySymbolKind.Method;

    public ImmutableArray<SymbolTrait> Traits { get; }

    public ClassificationOrigin Origin { get; }

    public string MetadataName { get; }

    public string ContainingNamespace { get; }

    public SymbolRef ContainingTypeSymbolRef { get; }

    public DocumentationScribeSemanticAccessibility DeclaredAccessibility { get; }

    public DocumentationScribeSemanticAccessibility EffectiveAccessibility { get; }

    public DocumentationScribeSemanticRefKind ReturnRefKind { get; }

    public DocumentationScribeSemanticTypeFact ReturnType { get; }

    public ImmutableArray<DocumentationScribeSemanticParameterFact> Parameters { get; }

    public ImmutableArray<DocumentationScribeSemanticTypeParameterFact> TypeParameters { get; }
}

public sealed record DocumentationScribeSemanticDocumentationState(
    DocumentationObservationValue Value,
    DocumentationAuthorityCompleteness Completeness,
    DocumentationUnavailableCause UnavailableCause);

public abstract class DocumentationScribeSemanticSourceEvidence
{
    private protected DocumentationScribeSemanticSourceEvidence(
        string contentSha256,
        string includedContentSha256,
        int originalUtf8ByteCount,
        int includedUtf8ByteCount,
        bool isTruncated,
        bool hasUtf8Bom,
        Utf16Span range,
        Utf16Span? includedRange,
        string content)
    {
        ContentSha256 = contentSha256;
        IncludedContentSha256 = includedContentSha256;
        OriginalUtf8ByteCount = originalUtf8ByteCount;
        IncludedUtf8ByteCount = includedUtf8ByteCount;
        IsTruncated = isTruncated;
        HasUtf8Bom = hasUtf8Bom;
        Range = range;
        IncludedRange = includedRange;
        Content = content;
    }

    public string ContentSha256 { get; }

    public string IncludedContentSha256 { get; }

    public int OriginalUtf8ByteCount { get; }

    public int IncludedUtf8ByteCount { get; }

    public bool IsTruncated { get; }

    public bool HasUtf8Bom { get; }

    public Utf16Span Range { get; }

    public Utf16Span? IncludedRange { get; }

    public string Content { get; }

    public override string ToString() =>
        $"{GetType().Name} {{ OriginalUtf8ByteCount = {OriginalUtf8ByteCount}, IncludedUtf8ByteCount = {IncludedUtf8ByteCount}, IsTruncated = {IsTruncated}, Content = <authorized-content> }}";
}

public sealed class DocumentationScribeSemanticRepositoryEvidence
    : DocumentationScribeSemanticSourceEvidence
{
    internal DocumentationScribeSemanticRepositoryEvidence(
        string repositoryPath,
        string contentSha256,
        string includedContentSha256,
        int originalUtf8ByteCount,
        int includedUtf8ByteCount,
        bool isTruncated,
        bool hasUtf8Bom,
        Utf16Span range,
        Utf16Span? includedRange,
        string content)
        : base(
            contentSha256,
            includedContentSha256,
            originalUtf8ByteCount,
            includedUtf8ByteCount,
            isTruncated,
            hasUtf8Bom,
            range,
            includedRange,
            content) => RepositoryPath = repositoryPath;

    public string RepositoryPath { get; }
}

public sealed class DocumentationScribeSemanticSourceGeneratorEvidence
    : DocumentationScribeSemanticSourceEvidence
{
    internal DocumentationScribeSemanticSourceGeneratorEvidence(
        string producerId,
        string outputId,
        string contentSha256,
        string includedContentSha256,
        int originalUtf8ByteCount,
        int includedUtf8ByteCount,
        bool isTruncated,
        Utf16Span range,
        Utf16Span? includedRange,
        string content)
        : base(
            contentSha256,
            includedContentSha256,
            originalUtf8ByteCount,
            includedUtf8ByteCount,
            isTruncated,
            false,
            range,
            includedRange,
            content)
    {
        ProducerId = producerId;
        OutputId = outputId;
    }

    public string ProducerId { get; }

    public string OutputId { get; }
}

public sealed class DocumentationScribeSemanticToolGeneratedEvidence
    : DocumentationScribeSemanticSourceEvidence
{
    internal DocumentationScribeSemanticToolGeneratedEvidence(
        string producerId,
        string outputId,
        string contentSha256,
        string includedContentSha256,
        int originalUtf8ByteCount,
        int includedUtf8ByteCount,
        bool isTruncated,
        Utf16Span range,
        Utf16Span? includedRange,
        string content)
        : base(
            contentSha256,
            includedContentSha256,
            originalUtf8ByteCount,
            includedUtf8ByteCount,
            isTruncated,
            false,
            range,
            includedRange,
            content)
    {
        ProducerId = producerId;
        OutputId = outputId;
    }

    public string ProducerId { get; }

    public string OutputId { get; }
}

public sealed record DocumentationScribeSemanticApplicableComponent(
    DocumentationPatchComponentKind Kind,
    string Identity,
    string? Name);

public sealed record DocumentationScribeSemanticTargetCore
{
    internal DocumentationScribeSemanticTargetCore(
        string contentIdentity,
        string correlationIdentity,
        DocumentationScribeSemanticMethodSummary method,
        ImmutableArray<DocumentationScribeSemanticApplicableComponent> applicableComponents,
        DocumentationScribeSemanticDocumentationState documentation,
        DocumentationScribeSemanticSourceEvidence declaration)
    {
        ContentIdentity = contentIdentity;
        CorrelationIdentity = correlationIdentity;
        Method = method;
        ApplicableComponents = applicableComponents;
        Documentation = documentation;
        Declaration = declaration;
    }

    public string ContentIdentity { get; }

    public string CorrelationIdentity { get; }

    public DocumentationScribeSemanticMethodSummary Method { get; }

    public ImmutableArray<DocumentationScribeSemanticApplicableComponent> ApplicableComponents { get; }

    public DocumentationScribeSemanticDocumentationState Documentation { get; }

    public DocumentationScribeSemanticSourceEvidence Declaration { get; }

    public override string ToString() =>
        $"{nameof(DocumentationScribeSemanticTargetCore)} {{ Components = {ApplicableComponents.Length}, Documentation = {Documentation.Value}, Declaration = <authorized-content> }}";
}

public sealed record DocumentationScribeSemanticEvidenceItem
{
    internal DocumentationScribeSemanticEvidenceItem(
        string itemIdentity,
        DocumentationScribeSemanticEvidenceKind kind,
        DocumentationScribeSemanticSourceEvidence source,
        DocumentationScribeSemanticUsageKind? usageKind,
        RelationKind? relationKind,
        DocumentationScribeSemanticRelationDirection? relationDirection,
        SymbolRef? relatedSymbolRef)
    {
        ItemIdentity = itemIdentity;
        Kind = kind;
        Source = source;
        UsageKind = usageKind;
        RelationKind = relationKind;
        RelationDirection = relationDirection;
        RelatedSymbolRef = relatedSymbolRef;
    }

    public string ItemIdentity { get; }

    public DocumentationScribeSemanticEvidenceKind Kind { get; }

    public DocumentationScribeSemanticSourceEvidence Source { get; }

    public DocumentationScribeSemanticUsageKind? UsageKind { get; }

    public RelationKind? RelationKind { get; }

    public DocumentationScribeSemanticRelationDirection? RelationDirection { get; }

    public SymbolRef? RelatedSymbolRef { get; }

    public override string ToString() =>
        $"{nameof(DocumentationScribeSemanticEvidenceItem)} {{ Kind = {Kind}, UsageKind = {UsageKind}, RelationKind = {RelationKind}, Content = <authorized-content> }}";
}

public sealed record DocumentationScribeSemanticIncomplete(
    DocumentationScribeSemanticIncompleteReason Reason,
    int OmittedCount);

public sealed record DocumentationScribeSemanticEvidencePage
{
    internal DocumentationScribeSemanticEvidencePage(
        DocumentationScribeSemanticTargetCore core,
        ImmutableArray<DocumentationScribeSemanticEvidenceItem> items,
        ImmutableArray<DocumentationScribeSemanticIncomplete> incomplete,
        DocumentationScribeContextCursor? nextCursor)
    {
        Core = core;
        Items = items;
        Incomplete = incomplete;
        NextCursor = nextCursor;
    }

    public DocumentationScribeSemanticTargetCore Core { get; }

    public ImmutableArray<DocumentationScribeSemanticEvidenceItem> Items { get; }

    public ImmutableArray<DocumentationScribeSemanticIncomplete> Incomplete { get; }

    public DocumentationScribeContextCursor? NextCursor { get; }

    public override string ToString() =>
        $"{nameof(DocumentationScribeSemanticEvidencePage)} {{ Items = {Items.Length}, Incomplete = {Incomplete.Length}, HasNextCursor = {NextCursor is not null}, Content = <authorized-content> }}";
}

public sealed record DocumentationScribeSemanticToolResult : IDocumentationScribeToolResult
{
    internal DocumentationScribeSemanticToolResult(
        DocumentationScribeToolOutcome outcome,
        DocumentationScribeSemanticEvidencePage? page,
        DocumentationScribeSemanticFailureReason? failureReason)
    {
        Outcome = outcome;
        Page = page;
        FailureReason = failureReason;
    }

    public DocumentationScribeToolOutcome Outcome { get; }

    public DocumentationScribeSemanticEvidencePage? Page { get; }

    public DocumentationScribeSemanticFailureReason? FailureReason { get; }

    public override string ToString() =>
        $"{nameof(DocumentationScribeSemanticToolResult)} {{ Outcome = {Outcome.Id}, HasPage = {Page is not null}, FailureReason = {FailureReason?.ToString() ?? "none"} }}";
}
