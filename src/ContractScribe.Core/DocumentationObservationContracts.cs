using System.Collections.Immutable;

namespace ContractScribe.Core;

public enum DocumentationObservationValue
{
    Present,
    Absent,
    Unavailable,
}

public enum DocumentationObservationRunStatus
{
    Success,
    Failure,
    Cancelled,
}

public enum DocumentationAuthorityRole
{
    Ordinary,
    PartialTypePart,
    PartialMemberImplementing,
    PartialMemberDefiningFallback,
}

public enum DocumentationBlockState
{
    NoBlock,
    WhitespaceOnly,
    WellFormed,
    Malformed,
}

public enum DocumentationComponentMatch
{
    Present,
    Absent,
}

public enum DocumentationAuthorityCompleteness
{
    Complete,
    PositiveOnly,
    Incomplete,
}

public enum DocumentationUnavailableCause
{
    None,
    SourceUnavailable,
    MalformedXml,
}

public enum DocumentationSourceKind
{
    Repository,
    SourceGenerator,
    ToolGenerated,
}

public static class DocumentationObservationVocabulary
{
    public const string UnrepresentableRunFailure =
        "run.documentation-observation.unrepresentable";

    public static string GetId(DocumentationObservationValue value) =>
        value switch
        {
            DocumentationObservationValue.Present => "documentation.present",
            DocumentationObservationValue.Absent => "documentation.absent",
            DocumentationObservationValue.Unavailable =>
                "documentation.unavailable",
            _ => throw Unknown(value),
        };

    public static string GetId(DocumentationAuthorityRole value) =>
        value switch
        {
            DocumentationAuthorityRole.Ordinary => "authority.ordinary",
            DocumentationAuthorityRole.PartialTypePart =>
                "authority.partial-type-part",
            DocumentationAuthorityRole.PartialMemberImplementing =>
                "authority.partial-member-implementing",
            DocumentationAuthorityRole.PartialMemberDefiningFallback =>
                "authority.partial-member-defining-fallback",
            _ => throw Unknown(value),
        };

    public static string GetId(DocumentationBlockState value) =>
        value switch
        {
            DocumentationBlockState.NoBlock => "block.no-block",
            DocumentationBlockState.WhitespaceOnly =>
                "block.whitespace-comment-or-processing-instruction-only",
            DocumentationBlockState.WellFormed => "block.well-formed",
            DocumentationBlockState.Malformed => "block.malformed",
            _ => throw Unknown(value),
        };

    public static string GetId(DocumentationComponentMatch value) =>
        value switch
        {
            DocumentationComponentMatch.Present => "component-match.present",
            DocumentationComponentMatch.Absent => "component-match.absent",
            _ => throw Unknown(value),
        };

    public static string GetId(DocumentationAuthorityCompleteness value) =>
        value switch
        {
            DocumentationAuthorityCompleteness.Complete =>
                "authority-completeness.complete",
            DocumentationAuthorityCompleteness.PositiveOnly =>
                "authority-completeness.positive-only",
            DocumentationAuthorityCompleteness.Incomplete =>
                "authority-completeness.incomplete",
            _ => throw Unknown(value),
        };

    public static string GetId(DocumentationUnavailableCause value) =>
        value switch
        {
            DocumentationUnavailableCause.None => "unavailable-cause.none",
            DocumentationUnavailableCause.SourceUnavailable =>
                "unavailable-cause.source-unavailable",
            DocumentationUnavailableCause.MalformedXml =>
                "unavailable-cause.malformed-xml",
            _ => throw Unknown(value),
        };

    public static string GetId(DocumentationSourceKind value) =>
        value switch
        {
            DocumentationSourceKind.Repository => "source.repository",
            DocumentationSourceKind.SourceGenerator =>
                "source.source-generator",
            DocumentationSourceKind.ToolGenerated =>
                "source.tool-generated",
            _ => throw Unknown(value),
        };

    private static ArgumentOutOfRangeException Unknown<T>(T value)
        where T : struct, Enum =>
        new(
            nameof(value),
            value,
            "The value is outside the closed documentation-observation vocabulary.");
}

public abstract class DocumentationSourceIdentity
    : IEquatable<DocumentationSourceIdentity>
{
    private protected DocumentationSourceIdentity(
        string projectIdentity,
        DocumentationSourceKind kind,
        string sourceSha256)
    {
        ProjectIdentity = projectIdentity;
        Kind = kind;
        SourceSha256 = sourceSha256;
    }

    public string ProjectIdentity { get; }

    public DocumentationSourceKind Kind { get; }

    public string SourceSha256 { get; }

    public bool Equals(DocumentationSourceIdentity? other) =>
        ReferenceEquals(this, other)
        || other is not null
            && GetType() == other.GetType()
            && string.Equals(
                ProjectIdentity,
                other.ProjectIdentity,
                StringComparison.Ordinal)
            && Kind == other.Kind
            && string.Equals(
                SourceSha256,
                other.SourceSha256,
                StringComparison.Ordinal)
            && EqualsCore(other);

    public override bool Equals(object? obj) =>
        obj is DocumentationSourceIdentity other && Equals(other);

    public abstract override int GetHashCode();

    protected abstract bool EqualsCore(DocumentationSourceIdentity other);
}

public sealed class RepositoryDocumentationSourceIdentity
    : DocumentationSourceIdentity
{
    internal RepositoryDocumentationSourceIdentity(
        string projectIdentity,
        string path,
        string sourceSha256)
        : base(projectIdentity, DocumentationSourceKind.Repository, sourceSha256)
    {
        Path = path;
    }

    public string Path { get; }

    protected override bool EqualsCore(DocumentationSourceIdentity other) =>
        string.Equals(
            Path,
            ((RepositoryDocumentationSourceIdentity)other).Path,
            StringComparison.Ordinal);

    public override int GetHashCode() =>
        HashCode.Combine(
            StringComparer.Ordinal.GetHashCode(ProjectIdentity),
            Kind,
            StringComparer.Ordinal.GetHashCode(SourceSha256),
            StringComparer.Ordinal.GetHashCode(Path));
}

public sealed class GeneratedDocumentationSourceIdentity
    : DocumentationSourceIdentity
{
    internal GeneratedDocumentationSourceIdentity(
        string projectIdentity,
        DocumentationSourceKind kind,
        string producerId,
        string outputId,
        string sourceSha256)
        : base(projectIdentity, kind, sourceSha256)
    {
        ProducerId = producerId;
        OutputId = outputId;
    }

    public string ProducerId { get; }

    public string OutputId { get; }

    protected override bool EqualsCore(DocumentationSourceIdentity other)
    {
        var generated = (GeneratedDocumentationSourceIdentity)other;
        return string.Equals(
                ProducerId,
                generated.ProducerId,
                StringComparison.Ordinal)
            && string.Equals(
                OutputId,
                generated.OutputId,
                StringComparison.Ordinal);
    }

    public override int GetHashCode() =>
        HashCode.Combine(
            StringComparer.Ordinal.GetHashCode(ProjectIdentity),
            Kind,
            StringComparer.Ordinal.GetHashCode(SourceSha256),
            StringComparer.Ordinal.GetHashCode(ProducerId),
            StringComparer.Ordinal.GetHashCode(OutputId));
}

public sealed record DocumentationObservationSubject
{
    internal DocumentationObservationSubject(
        SymbolRef parentSymbolRef,
        ComponentKind? componentKind,
        string? componentIdentity)
    {
        ParentSymbolRef = parentSymbolRef;
        ComponentKind = componentKind;
        ComponentIdentity = componentIdentity;
    }

    public SymbolRef ParentSymbolRef { get; }

    public ComponentKind? ComponentKind { get; }

    public string? ComponentIdentity { get; }
}

public sealed record DocumentationDeclarationFact
{
    internal DocumentationDeclarationFact(
        string declarationId,
        DocumentationAuthorityRole authorityRole,
        DocumentationSourceIdentity source,
        Utf16Span declarationSpan,
        string declarationText,
        string declarationSha256,
        Utf16Span leadingTriviaSpan,
        string leadingTriviaText,
        string leadingTriviaSha256,
        Utf16Span? documentationSpan,
        string? documentationText,
        string? documentationSha256,
        DocumentationBlockState blockState,
        bool parentSubstantive,
        string? componentLocalName,
        DocumentationComponentMatch? componentMatch)
    {
        DeclarationId = declarationId;
        AuthorityRole = authorityRole;
        Source = source;
        DeclarationSpan = declarationSpan;
        DeclarationText = declarationText;
        DeclarationSha256 = declarationSha256;
        LeadingTriviaSpan = leadingTriviaSpan;
        LeadingTriviaText = leadingTriviaText;
        LeadingTriviaSha256 = leadingTriviaSha256;
        DocumentationSpan = documentationSpan;
        DocumentationText = documentationText;
        DocumentationSha256 = documentationSha256;
        BlockState = blockState;
        ParentSubstantive = parentSubstantive;
        ComponentLocalName = componentLocalName;
        ComponentMatch = componentMatch;
    }

    public string DeclarationId { get; }

    public DocumentationAuthorityRole AuthorityRole { get; }

    public DocumentationSourceIdentity Source { get; }

    public Utf16Span DeclarationSpan { get; }

    public string DeclarationText { get; }

    public string DeclarationSha256 { get; }

    public Utf16Span LeadingTriviaSpan { get; }

    public string LeadingTriviaText { get; }

    public string LeadingTriviaSha256 { get; }

    public Utf16Span? DocumentationSpan { get; }

    public string? DocumentationText { get; }

    public string? DocumentationSha256 { get; }

    public DocumentationBlockState BlockState { get; }

    public bool ParentSubstantive { get; }

    public string? ComponentLocalName { get; }

    public DocumentationComponentMatch? ComponentMatch { get; }
}

public sealed record DocumentationObservation
{
    internal DocumentationObservation(
        DocumentationObservationSubject subject,
        DocumentationObservationValue value,
        DocumentationAuthorityCompleteness completeness,
        DocumentationUnavailableCause unavailableCause,
        ImmutableArray<DocumentationDeclarationFact> declarations)
    {
        Subject = subject;
        Value = value;
        Completeness = completeness;
        UnavailableCause = unavailableCause;
        Declarations = declarations;
    }

    public DocumentationObservationSubject Subject { get; }

    public DocumentationObservationValue Value { get; }

    public DocumentationAuthorityCompleteness Completeness { get; }

    public DocumentationUnavailableCause UnavailableCause { get; }

    public ImmutableArray<DocumentationDeclarationFact> Declarations { get; }
}

public sealed record DocumentationObservationSet
{
    internal DocumentationObservationSet(
        ImmutableArray<DocumentationObservation> observations)
    {
        Observations = observations;
    }

    public ImmutableArray<DocumentationObservation> Observations { get; }
}

public sealed record DocumentationObservationFailure(string Stage, string Code);

public sealed record DocumentationObservationDiagnostic(
    string Stage,
    string Code,
    string Severity);

public sealed class DocumentationObservationOutcome
{
    private DocumentationObservationOutcome(
        DocumentationObservationRunStatus status,
        DocumentationObservationSet? observationSet,
        DocumentationObservationFailure? primaryFailure,
        ImmutableArray<DocumentationObservationDiagnostic> diagnostics)
    {
        Status = status;
        ObservationSet = observationSet;
        PrimaryFailure = primaryFailure;
        Diagnostics = diagnostics;
    }

    public DocumentationObservationRunStatus Status { get; }

    public DocumentationObservationSet? ObservationSet { get; }

    public DocumentationObservationFailure? PrimaryFailure { get; }

    public ImmutableArray<DocumentationObservationDiagnostic> Diagnostics { get; }

    internal static DocumentationObservationOutcome Success(
        DocumentationObservationSet observationSet,
        IEnumerable<DocumentationObservationDiagnostic>? diagnostics = null) =>
        new(
            DocumentationObservationRunStatus.Success,
            observationSet,
            null,
            NormalizeDiagnostics(diagnostics));

    public static DocumentationObservationOutcome Failure(
        IEnumerable<DocumentationObservationDiagnostic>? diagnostics = null) =>
        new(
            DocumentationObservationRunStatus.Failure,
            null,
            new DocumentationObservationFailure(
                "documentation-observation-normalization",
                DocumentationObservationVocabulary.UnrepresentableRunFailure),
            NormalizeDiagnostics(diagnostics));

    public static DocumentationObservationOutcome Cancelled(
        IEnumerable<DocumentationObservationDiagnostic>? diagnostics = null) =>
        new(
            DocumentationObservationRunStatus.Cancelled,
            null,
            null,
            NormalizeDiagnostics(diagnostics));

    private static ImmutableArray<DocumentationObservationDiagnostic> NormalizeDiagnostics(
        IEnumerable<DocumentationObservationDiagnostic>? diagnostics) =>
        diagnostics?
            .Distinct()
            .OrderBy(diagnostic => diagnostic.Stage, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Severity, StringComparer.Ordinal)
            .Take(32)
            .ToImmutableArray()
        ?? [];
}
