using System.Collections.Immutable;

namespace ContractScribe.Core;

public enum EvidenceKind
{
    SourceDeclaration,
    SourceImplementation,
    SourceXmlDocumentation,
    SourceAttribute,
    Test,
    RepositoryDocumentation,
    PublicContract,
}

public enum EvidenceRelation
{
    Declares,
    Documents,
    Tests,
    References,
    Constrains,
}

public enum EvidenceAvailabilityStatus
{
    Complete,
    Partial,
    Unavailable,
}

public enum EvidenceOmissionReason
{
    AccessNotPermitted,
    SourceUnavailable,
    BinaryContent,
    BudgetExhausted,
    NotProvided,
}

public enum EvidenceRunStatus
{
    Success,
    Failure,
    Cancelled,
}

public enum EvidenceAuthorityCompleteness
{
    Complete,
    PositiveOnly,
}

public static class EvidenceVocabulary
{
    public const int BundleVersion = 1;

    public static string GetId(EvidenceKind value) => value switch
    {
        EvidenceKind.SourceDeclaration => "evidence.source.declaration",
        EvidenceKind.SourceImplementation => "evidence.source.implementation",
        EvidenceKind.SourceXmlDocumentation => "evidence.source.xml-documentation",
        EvidenceKind.SourceAttribute => "evidence.source.attribute",
        EvidenceKind.Test => "evidence.test",
        EvidenceKind.RepositoryDocumentation => "evidence.repository-documentation",
        EvidenceKind.PublicContract => "evidence.public-contract",
        _ => throw Unknown(value),
    };

    public static string GetId(EvidenceRelation value) => value switch
    {
        EvidenceRelation.Declares => "evidence.declares",
        EvidenceRelation.Documents => "evidence.documents",
        EvidenceRelation.Tests => "evidence.tests",
        EvidenceRelation.References => "evidence.references",
        EvidenceRelation.Constrains => "evidence.constrains",
        _ => throw Unknown(value),
    };

    public static string GetId(EvidenceAvailabilityStatus value) => value switch
    {
        EvidenceAvailabilityStatus.Complete => "evidence.bundle.complete",
        EvidenceAvailabilityStatus.Partial => "evidence.bundle.partial",
        EvidenceAvailabilityStatus.Unavailable => "evidence.bundle.unavailable",
        _ => throw Unknown(value),
    };

    public static string GetId(EvidenceOmissionReason value) => value switch
    {
        EvidenceOmissionReason.AccessNotPermitted =>
            "evidence.omission.access-not-permitted",
        EvidenceOmissionReason.SourceUnavailable =>
            "evidence.omission.source-unavailable",
        EvidenceOmissionReason.BinaryContent =>
            "evidence.omission.binary-content",
        EvidenceOmissionReason.BudgetExhausted =>
            "evidence.omission.budget-exhausted",
        EvidenceOmissionReason.NotProvided =>
            "evidence.omission.not-provided",
        _ => throw Unknown(value),
    };

    private static ArgumentOutOfRangeException Unknown<T>(T value)
        where T : struct, Enum =>
        new(
            nameof(value),
            value,
            "The value is outside the closed evidence vocabulary.");
}

public sealed record EvidenceFailure(string Code);

public abstract record EvidenceSubject
{
    private protected EvidenceSubject(SymbolRef parentSymbolRef)
    {
        ParentSymbolRef = parentSymbolRef;
    }

    public SymbolRef ParentSymbolRef { get; }
}

public sealed record TargetEvidenceSubject : EvidenceSubject
{
    internal TargetEvidenceSubject(SymbolRef symbolRef)
        : base(symbolRef)
    {
    }
}

public sealed record ComponentEvidenceSubject : EvidenceSubject
{
    internal ComponentEvidenceSubject(
        SymbolRef parentSymbolRef,
        ComponentKind componentKind,
        string identity)
        : base(parentSymbolRef)
    {
        ComponentKind = componentKind;
        Identity = identity;
    }

    public ComponentKind ComponentKind { get; }

    public string Identity { get; }
}

public abstract record EvidenceLocator
{
    private protected EvidenceLocator()
    {
    }
}

public sealed record RepositoryEvidenceLocator : EvidenceLocator
{
    internal RepositoryEvidenceLocator(string path, Utf16Span? span)
    {
        Path = path;
        Span = span;
    }

    public string Path { get; }

    public Utf16Span? Span { get; }
}

public sealed record MetadataEvidenceLocator : EvidenceLocator
{
    internal MetadataEvidenceLocator(
        string assemblyIdentity,
        string documentationCommentId)
    {
        AssemblyIdentity = assemblyIdentity;
        DocumentationCommentId = documentationCommentId;
    }

    public string AssemblyIdentity { get; }

    public string DocumentationCommentId { get; }
}

public sealed record GeneratedOutputEvidenceLocator : EvidenceLocator
{
    internal GeneratedOutputEvidenceLocator(
        GeneratedOutputKind producerKind,
        string producerId,
        string outputId,
        string sourceSha256,
        Utf16Span? span)
    {
        ProducerKind = producerKind;
        ProducerId = producerId;
        OutputId = outputId;
        SourceSha256 = sourceSha256;
        Span = span;
    }

    public GeneratedOutputKind ProducerKind { get; }

    public string ProducerId { get; }

    public string OutputId { get; }

    public string SourceSha256 { get; }

    public Utf16Span? Span { get; }
}

public sealed record SyntheticEvidenceLocator : EvidenceLocator
{
    internal SyntheticEvidenceLocator(string fixtureId)
    {
        FixtureId = fixtureId;
    }

    public string FixtureId { get; }
}

public sealed class EvidenceCandidateInput
{
    internal EvidenceCandidateInput(
        string evidenceId,
        EvidenceSubject subject,
        EvidenceKind kind,
        EvidenceRelation relation,
        string originalRegion,
        string? expectedSha256,
        EvidenceLocator locator)
    {
        EvidenceId = evidenceId;
        Subject = subject;
        Kind = kind;
        Relation = relation;
        OriginalRegion = originalRegion;
        ExpectedSha256 = expectedSha256;
        Locator = locator;
    }

    public string EvidenceId { get; }

    public EvidenceSubject Subject { get; }

    public EvidenceKind Kind { get; }

    public EvidenceRelation Relation { get; }

    public string OriginalRegion { get; }

    public string? ExpectedSha256 { get; }

    public EvidenceLocator Locator { get; }
}

public static class EvidenceInput
{
    public static TargetEvidenceSubject TargetSubject(
        string compilationContextRef,
        string documentationCommentId) =>
        new(new SymbolRef(compilationContextRef, documentationCommentId));

    public static ComponentEvidenceSubject ComponentSubject(
        string compilationContextRef,
        string documentationCommentId,
        ComponentKind componentKind,
        string identity) =>
        new(
            new SymbolRef(compilationContextRef, documentationCommentId),
            componentKind,
            identity);

    public static RepositoryEvidenceLocator RepositoryLocator(
        string path,
        int? spanStart = null,
        int? spanEnd = null) =>
        new(path, CreateOptionalSpan(spanStart, spanEnd));

    public static MetadataEvidenceLocator MetadataLocator(
        string assemblyIdentity,
        string documentationCommentId) =>
        new(assemblyIdentity, documentationCommentId);

    public static GeneratedOutputEvidenceLocator GeneratedOutputLocator(
        GeneratedOutputKind producerKind,
        string producerId,
        string outputId,
        string sourceSha256,
        int? spanStart = null,
        int? spanEnd = null) =>
        new(
            producerKind,
            producerId,
            outputId,
            sourceSha256,
            CreateOptionalSpan(spanStart, spanEnd));

    public static SyntheticEvidenceLocator SyntheticLocator(string fixtureId) =>
        new(fixtureId);

    public static EvidenceCandidateInput Candidate(
        string evidenceId,
        EvidenceSubject subject,
        EvidenceKind kind,
        EvidenceRelation relation,
        string originalRegion,
        EvidenceLocator locator,
        string? expectedSha256 = null) =>
        new(
            evidenceId,
            subject,
            kind,
            relation,
            originalRegion,
            expectedSha256,
            locator);

    public static EvidenceBudgets Budgets(
        int maximumItems,
        int maximumItemUtf8Bytes,
        int maximumBundleUtf8Bytes) =>
        new(maximumItems, maximumItemUtf8Bytes, maximumBundleUtf8Bytes);

    private static Utf16Span? CreateOptionalSpan(int? start, int? end)
    {
        if (start is null && end is null)
        {
            return null;
        }

        return new Utf16Span(start ?? -1, end ?? -1);
    }
}

public sealed record EvidenceBudgets
{
    internal EvidenceBudgets(
        int maximumItems,
        int maximumItemUtf8Bytes,
        int maximumBundleUtf8Bytes)
    {
        MaximumItems = maximumItems;
        MaximumItemUtf8Bytes = maximumItemUtf8Bytes;
        MaximumBundleUtf8Bytes = maximumBundleUtf8Bytes;
    }

    public static EvidenceBudgets Production { get; } = new(32, 4096, 32768);

    public int MaximumItems { get; }

    public int MaximumItemUtf8Bytes { get; }

    public int MaximumBundleUtf8Bytes { get; }
}

public sealed record EvidenceItem
{
    internal EvidenceItem(
        string evidenceId,
        EvidenceSubject subject,
        EvidenceKind kind,
        EvidenceRelation relation,
        string excerpt,
        string sha256,
        int originalUtf8ByteCount,
        int includedUtf8ByteCount,
        int omittedUtf8ByteCount,
        bool isTruncated,
        EvidenceLocator locator)
    {
        EvidenceId = evidenceId;
        Subject = subject;
        Kind = kind;
        Relation = relation;
        Excerpt = excerpt;
        Sha256 = sha256;
        OriginalUtf8ByteCount = originalUtf8ByteCount;
        IncludedUtf8ByteCount = includedUtf8ByteCount;
        OmittedUtf8ByteCount = omittedUtf8ByteCount;
        IsTruncated = isTruncated;
        Locator = locator;
    }

    public string EvidenceId { get; }

    public EvidenceSubject Subject { get; }

    public EvidenceKind Kind { get; }

    public EvidenceRelation Relation { get; }

    public string Excerpt { get; }

    public string Sha256 { get; }

    public int OriginalUtf8ByteCount { get; }

    public int IncludedUtf8ByteCount { get; }

    public int OmittedUtf8ByteCount { get; }

    public bool IsTruncated { get; }

    public EvidenceLocator Locator { get; }
}

public sealed record EvidenceObservationCommitment
{
    internal EvidenceObservationCommitment(
        string observationSubjectRef,
        string compilationContextRef,
        EvidenceSubject subject,
        string authoritativeDeclarationSetDigest,
        int authoritativeDeclarationCount)
    {
        ObservationSubjectRef = observationSubjectRef;
        CompilationContextRef = compilationContextRef;
        Subject = subject;
        AuthoritativeDeclarationSetDigest = authoritativeDeclarationSetDigest;
        AuthoritativeDeclarationCount = authoritativeDeclarationCount;
    }

    public string ObservationSubjectRef { get; }

    public string CompilationContextRef { get; }

    public EvidenceSubject Subject { get; }

    public string AuthoritativeDeclarationSetDigest { get; }

    public int AuthoritativeDeclarationCount { get; }
}

public sealed record EvidenceBundle
{
    internal EvidenceBundle(
        EvidenceAvailabilityStatus availabilityStatus,
        EvidenceOmissionReason? omissionReason,
        ImmutableArray<EvidenceItem> items,
        EvidenceObservationCommitment? observationSubject)
    {
        AvailabilityStatus = availabilityStatus;
        OmissionReason = omissionReason;
        Items = items;
        ObservationSubject = observationSubject;
    }

    public int EvidenceBundleVersion => EvidenceVocabulary.BundleVersion;

    public EvidenceAvailabilityStatus AvailabilityStatus { get; }

    public EvidenceOmissionReason? OmissionReason { get; }

    public ImmutableArray<EvidenceItem> Items { get; }

    public EvidenceObservationCommitment? ObservationSubject { get; }
}

public sealed class EvidenceNormalizationOutcome
{
    private EvidenceNormalizationOutcome(
        EvidenceRunStatus status,
        EvidenceBundle? bundle,
        EvidenceFailure? primaryFailure)
    {
        Status = status;
        Bundle = bundle;
        PrimaryFailure = primaryFailure;
    }

    public EvidenceRunStatus Status { get; }

    public EvidenceBundle? Bundle { get; }

    public EvidenceFailure? PrimaryFailure { get; }

    internal static EvidenceNormalizationOutcome Success(EvidenceBundle bundle) =>
        new(EvidenceRunStatus.Success, bundle, null);

    internal static EvidenceNormalizationOutcome Failure(string code) =>
        new(EvidenceRunStatus.Failure, null, new EvidenceFailure(code));

    internal static EvidenceNormalizationOutcome Cancelled() =>
        new(EvidenceRunStatus.Cancelled, null, null);
}

public sealed class EvidenceDeclarationBindingInput
{
    internal EvidenceDeclarationBindingInput(
        string declarationId,
        string? declarationEvidenceId,
        string? documentationEvidenceId)
    {
        DeclarationId = declarationId;
        DeclarationEvidenceId = declarationEvidenceId;
        DocumentationEvidenceId = documentationEvidenceId;
    }

    public string DeclarationId { get; }

    public string? DeclarationEvidenceId { get; }

    public string? DocumentationEvidenceId { get; }
}

public static class EvidenceBindingInput
{
    public static EvidenceDeclarationBindingInput Declaration(
        string declarationId,
        string? declarationEvidenceId,
        string? documentationEvidenceId) =>
        new(declarationId, declarationEvidenceId, documentationEvidenceId);
}

public sealed record EvidenceAuthorityRow
{
    internal EvidenceAuthorityRow(
        string declarationId,
        DocumentationAuthorityRole authorityRole,
        DocumentationBlockState blockState,
        string evidenceId,
        string? componentLocalName,
        DocumentationComponentMatch? componentMatch)
    {
        DeclarationId = declarationId;
        AuthorityRole = authorityRole;
        BlockState = blockState;
        EvidenceId = evidenceId;
        ComponentLocalName = componentLocalName;
        ComponentMatch = componentMatch;
    }

    public string DeclarationId { get; }

    public DocumentationAuthorityRole AuthorityRole { get; }

    public DocumentationBlockState BlockState { get; }

    public string EvidenceId { get; }

    public string? ComponentLocalName { get; }

    public DocumentationComponentMatch? ComponentMatch { get; }
}

public sealed record EvidenceAuthoritySet
{
    internal EvidenceAuthoritySet(
        string declarationSetId,
        EvidenceAuthorityCompleteness completeness,
        ImmutableArray<EvidenceAuthorityRow> declarations)
    {
        DeclarationSetId = declarationSetId;
        Completeness = completeness;
        Declarations = declarations;
    }

    public string DeclarationSetId { get; }

    public EvidenceAuthorityCompleteness Completeness { get; }

    public ImmutableArray<EvidenceAuthorityRow> Declarations { get; }
}

public sealed record BoundObservationEvidence
{
    internal BoundObservationEvidence(
        DocumentationObservationValue observationValue,
        EvidenceBundle bundle,
        ImmutableArray<string> evidenceIds,
        EvidenceAuthoritySet? authority,
        bool supportsOrdinaryResult)
    {
        ObservationValue = observationValue;
        Bundle = bundle;
        EvidenceIds = evidenceIds;
        Authority = authority;
        SupportsOrdinaryResult = supportsOrdinaryResult;
    }

    public DocumentationObservationValue ObservationValue { get; }

    public EvidenceBundle Bundle { get; }

    public ImmutableArray<string> EvidenceIds { get; }

    public EvidenceAuthoritySet? Authority { get; }

    public bool SupportsOrdinaryResult { get; }
}

public sealed class EvidenceBindingOutcome
{
    private EvidenceBindingOutcome(
        EvidenceRunStatus status,
        BoundObservationEvidence? binding,
        EvidenceFailure? primaryFailure)
    {
        Status = status;
        Binding = binding;
        PrimaryFailure = primaryFailure;
    }

    public EvidenceRunStatus Status { get; }

    public BoundObservationEvidence? Binding { get; }

    public EvidenceFailure? PrimaryFailure { get; }

    internal static EvidenceBindingOutcome Success(BoundObservationEvidence binding) =>
        new(EvidenceRunStatus.Success, binding, null);

    internal static EvidenceBindingOutcome Failure(string code) =>
        new(EvidenceRunStatus.Failure, null, new EvidenceFailure(code));

    internal static EvidenceBindingOutcome Cancelled() =>
        new(EvidenceRunStatus.Cancelled, null, null);
}
