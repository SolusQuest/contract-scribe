using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace ContractScribe.Core;

internal enum DocumentationObservationNormalizationStage
{
    ExpectedSubjectConstruction,
    CandidateSorting,
    DeclarationValidation,
    Hashing,
    Derivation,
    TerminalConstruction,
}

public sealed class DocumentationDeclarationInput
{
    internal DocumentationDeclarationInput(
        string declarationId,
        DocumentationAuthorityRole authorityRole,
        DocumentationSourceIdentity source,
        Utf16Span declarationSpan,
        string declarationText,
        Utf16Span leadingTriviaSpan,
        string leadingTriviaText,
        Utf16Span? documentationSpan,
        string? documentationText,
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
        LeadingTriviaSpan = leadingTriviaSpan;
        LeadingTriviaText = leadingTriviaText;
        DocumentationSpan = documentationSpan;
        DocumentationText = documentationText;
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
    public Utf16Span LeadingTriviaSpan { get; }
    public string LeadingTriviaText { get; }
    public Utf16Span? DocumentationSpan { get; }
    public string? DocumentationText { get; }
    public DocumentationBlockState BlockState { get; }
    public bool ParentSubstantive { get; }
    public string? ComponentLocalName { get; }
    public DocumentationComponentMatch? ComponentMatch { get; }
}

public static class DocumentationObservationInput
{
    public static Utf16Span Span(int start, int end) => new(start, end);

    public static DocumentationDeclarationInput RepositoryDeclaration(
        string declarationId,
        DocumentationAuthorityRole authorityRole,
        string projectIdentity,
        string repositoryPath,
        string sourceSha256,
        Utf16Span declarationSpan,
        string declarationText,
        Utf16Span leadingTriviaSpan,
        string leadingTriviaText,
        Utf16Span? documentationSpan,
        string? documentationText,
        DocumentationBlockState blockState,
        bool parentSubstantive,
        string? componentLocalName = null,
        DocumentationComponentMatch? componentMatch = null) =>
        new(
            declarationId,
            authorityRole,
            new RepositoryDocumentationSourceIdentity(
                projectIdentity,
                repositoryPath,
                sourceSha256),
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

    public static DocumentationDeclarationInput GeneratedDeclaration(
        string declarationId,
        DocumentationAuthorityRole authorityRole,
        string projectIdentity,
        DocumentationSourceKind sourceKind,
        string producerId,
        string outputId,
        string sourceSha256,
        Utf16Span declarationSpan,
        string declarationText,
        Utf16Span leadingTriviaSpan,
        string leadingTriviaText,
        Utf16Span? documentationSpan,
        string? documentationText,
        DocumentationBlockState blockState,
        bool parentSubstantive,
        string? componentLocalName = null,
        DocumentationComponentMatch? componentMatch = null) =>
        new(
            declarationId,
            authorityRole,
            new GeneratedDocumentationSourceIdentity(
                projectIdentity,
                sourceKind,
                producerId,
                outputId,
                sourceSha256),
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
}

public sealed class DocumentationObservationCandidateBuffer
{
    private readonly ClassificationSet classificationSet;
    private readonly Action<DocumentationObservationNormalizationStage>? stageObserver;
    private readonly List<Candidate> candidates = [];

    public DocumentationObservationCandidateBuffer(
        ClassificationSet classificationSet)
        : this(classificationSet, null)
    {
    }

    internal DocumentationObservationCandidateBuffer(
        ClassificationSet classificationSet,
        Action<DocumentationObservationNormalizationStage>? stageObserver)
    {
        this.classificationSet =
            classificationSet ?? throw new ArgumentNullException(nameof(classificationSet));
        this.stageObserver = stageObserver;
    }

    public void AddTarget(
        TargetClassification target,
        bool authorityComplete,
        IEnumerable<DocumentationDeclarationInput> declarations)
    {
        ArgumentNullException.ThrowIfNull(target);
        candidates.Add(new Candidate(
            new DocumentationObservationSubject(target.SymbolRef, null, null),
            authorityComplete,
            Materialize(declarations)));
    }

    public void AddComponent(
        ComponentClassification component,
        bool authorityComplete,
        IEnumerable<DocumentationDeclarationInput> declarations)
    {
        ArgumentNullException.ThrowIfNull(component);
        candidates.Add(new Candidate(
            new DocumentationObservationSubject(
                component.ParentSymbolRef,
                component.ComponentKind,
                component.Identity),
            authorityComplete,
            Materialize(declarations)));
    }

    public DocumentationObservationOutcome Normalize(
        IEnumerable<DocumentationObservationDiagnostic>? diagnostics = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            Observe(
                DocumentationObservationNormalizationStage.ExpectedSubjectConstruction,
                cancellationToken);
            var expected = ExpectedSubjects(classificationSet);
            var targets = classificationSet.Targets
                .Where(target => target.SupportStatus == SupportStatus.Supported)
                .ToDictionary(target => target.SymbolRef);
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = new List<DocumentationObservation>();
            var seen = new HashSet<DocumentationObservationSubject>();
            Observe(
                DocumentationObservationNormalizationStage.CandidateSorting,
                cancellationToken);
            var orderedCandidates = candidates
                .OrderBy(candidate => SubjectKey(candidate.Subject), StringComparer.Ordinal)
                .ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var candidate in orderedCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!expected.Contains(candidate.Subject)
                    || !seen.Add(candidate.Subject)
                    || !targets.TryGetValue(
                         candidate.Subject.ParentSymbolRef,
                         out var target)
                    || !TryNormalize(
                        candidate,
                        target,
                        cancellationToken,
                        out var observation))
                {
                    return DocumentationObservationOutcome.Failure(diagnostics);
                }

                normalized.Add(observation!);
            }

            if (!seen.SetEquals(expected))
            {
                return DocumentationObservationOutcome.Failure(diagnostics);
            }

            Observe(
                DocumentationObservationNormalizationStage.TerminalConstruction,
                cancellationToken);
            return DocumentationObservationOutcome.Success(
                new DocumentationObservationSet(normalized.ToImmutableArray()),
                diagnostics);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return DocumentationObservationOutcome.Cancelled(diagnostics);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return DocumentationObservationOutcome.Failure(diagnostics);
        }
    }

    private static ImmutableArray<DocumentationDeclarationInput> Materialize(
        IEnumerable<DocumentationDeclarationInput> declarations) =>
        declarations?.ToImmutableArray()
        ?? throw new ArgumentNullException(nameof(declarations));

    private static HashSet<DocumentationObservationSubject> ExpectedSubjects(
        ClassificationSet set)
    {
        var expected = set.Targets
            .Where(target => target.SupportStatus == SupportStatus.Supported)
            .Select(target => new DocumentationObservationSubject(
                target.SymbolRef,
                null,
                null))
            .ToHashSet();
        foreach (var component in set.Components.Where(IsObservableComponent))
        {
            expected.Add(new DocumentationObservationSubject(
                component.ParentSymbolRef,
                component.ComponentKind,
                component.Identity));
        }

        return expected;
    }

    private static bool IsObservableComponent(ComponentClassification component) =>
        component.SupportStatus == SupportStatus.Supported
        && component.ComponentKind is ComponentKind.Parameter
            or ComponentKind.TypeParameter
            or ComponentKind.Return
            or ComponentKind.Value;

    private bool TryNormalize(
        Candidate candidate,
        TargetClassification target,
        CancellationToken cancellationToken,
        out DocumentationObservation? observation)
    {
        observation = null;
        if (candidate.Declarations.IsDefaultOrEmpty)
        {
            if (candidate.AuthorityComplete)
            {
                return false;
            }

            observation = new DocumentationObservation(
                candidate.Subject,
                DocumentationObservationValue.Unavailable,
                DocumentationAuthorityCompleteness.Incomplete,
                DocumentationUnavailableCause.SourceUnavailable,
                []);
            return true;
        }

        var facts = new List<DocumentationDeclarationFact>();
        var declarationsById = new Dictionary<
            string,
            DocumentationDeclarationInput>(StringComparer.Ordinal);
        var declarationsByLocation = new Dictionary<
            string,
            DocumentationDeclarationInput>(StringComparer.Ordinal);
        Observe(
            DocumentationObservationNormalizationStage.CandidateSorting,
            cancellationToken);
        var orderedDeclarations = candidate.Declarations
            .OrderBy(DeclarationKey, StringComparer.Ordinal)
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var declaration in orderedDeclarations)
        {
            Observe(
                DocumentationObservationNormalizationStage.DeclarationValidation,
                cancellationToken);
            if (!Validate(candidate.Subject, target, declaration))
            {
                return false;
            }

            var locationKey = DeclarationLocationKey(declaration);
            if (declarationsById.TryGetValue(
                    declaration.DeclarationId,
                    out var existingById)
                || declarationsByLocation.TryGetValue(
                    locationKey,
                    out existingById))
            {
                if (!Equivalent(existingById, declaration))
                {
                    return false;
                }

                continue;
            }

            declarationsById.Add(declaration.DeclarationId, declaration);
            declarationsByLocation.Add(locationKey, declaration);
            Observe(
                DocumentationObservationNormalizationStage.Hashing,
                cancellationToken);
            var declarationSha256 = Sha256(declaration.DeclarationText);
            cancellationToken.ThrowIfCancellationRequested();
            var leadingTriviaSha256 = Sha256(declaration.LeadingTriviaText);
            cancellationToken.ThrowIfCancellationRequested();
            var documentationSha256 = declaration.DocumentationText is null
                ? null
                : Sha256(declaration.DocumentationText);
            cancellationToken.ThrowIfCancellationRequested();
            facts.Add(new DocumentationDeclarationFact(
                declaration.DeclarationId,
                declaration.AuthorityRole,
                declaration.Source,
                declaration.DeclarationSpan,
                declaration.DeclarationText,
                declarationSha256,
                declaration.LeadingTriviaSpan,
                declaration.LeadingTriviaText,
                leadingTriviaSha256,
                declaration.DocumentationSpan,
                declaration.DocumentationText,
                documentationSha256,
                declaration.BlockState,
                declaration.ParentSubstantive,
                declaration.ComponentLocalName,
                declaration.ComponentMatch));
        }

        var authorityRoles = facts
            .Select(fact => fact.AuthorityRole)
            .Distinct()
            .ToArray();
        if (authorityRoles.Length != 1
            || !HasLegalAuthorityCardinality(authorityRoles[0], facts.Count))
        {
            return false;
        }

        Observe(
            DocumentationObservationNormalizationStage.Derivation,
            cancellationToken);
        var isComponent = candidate.Subject.ComponentKind is not null;
        var positive = isComponent
            ? facts.Any(fact =>
                fact.ComponentMatch == DocumentationComponentMatch.Present)
            : facts.Any(fact => fact.ParentSubstantive);

        DocumentationObservationValue value;
        DocumentationAuthorityCompleteness completeness;
        DocumentationUnavailableCause cause;
        if (positive)
        {
            value = DocumentationObservationValue.Present;
            completeness = candidate.AuthorityComplete
                ? DocumentationAuthorityCompleteness.Complete
                : DocumentationAuthorityCompleteness.PositiveOnly;
            cause = DocumentationUnavailableCause.None;
        }
        else if (!candidate.AuthorityComplete)
        {
            value = DocumentationObservationValue.Unavailable;
            completeness = DocumentationAuthorityCompleteness.Incomplete;
            cause = DocumentationUnavailableCause.SourceUnavailable;
        }
        else if (facts.Any(fact =>
                fact.BlockState == DocumentationBlockState.Malformed))
        {
            value = DocumentationObservationValue.Unavailable;
            completeness = DocumentationAuthorityCompleteness.Complete;
            cause = DocumentationUnavailableCause.MalformedXml;
        }
        else
        {
            value = DocumentationObservationValue.Absent;
            completeness = DocumentationAuthorityCompleteness.Complete;
            cause = DocumentationUnavailableCause.None;
        }

        observation = new DocumentationObservation(
            candidate.Subject,
            value,
            completeness,
            cause,
            facts.ToImmutableArray());
        cancellationToken.ThrowIfCancellationRequested();
        return true;
    }

    private static bool Validate(
        DocumentationObservationSubject subject,
        TargetClassification target,
        DocumentationDeclarationInput declaration)
    {
        if (!IsOpaqueId(declaration.DeclarationId, "decl.")
            || !Enum.IsDefined(declaration.AuthorityRole)
            || !IsLegalRole(target, declaration.AuthorityRole)
            || !Enum.IsDefined(declaration.BlockState)
            || !SourceMatchesOrigin(target.Origin, declaration.Source.Kind)
            || !ValidateSource(declaration.Source)
            || !ValidateSpan(declaration.DeclarationSpan, declaration.DeclarationText)
            || !ValidateSpan(declaration.LeadingTriviaSpan, declaration.LeadingTriviaText)
            || declaration.LeadingTriviaSpan.Start
                != declaration.DeclarationSpan.Start
            || declaration.LeadingTriviaSpan.End
                > declaration.DeclarationSpan.End
            || !TextMatchesContainingSpan(
                declaration.DeclarationSpan,
                declaration.DeclarationText,
                declaration.LeadingTriviaSpan,
                declaration.LeadingTriviaText)
            || (declaration.DocumentationSpan is null)
                != (declaration.DocumentationText is null)
            || declaration.DocumentationSpan is { } documentationSpan
                && (!ValidateSpan(
                        documentationSpan,
                        declaration.DocumentationText!)
                    || documentationSpan.Start
                        < declaration.LeadingTriviaSpan.Start
                    || documentationSpan.End
                        > declaration.LeadingTriviaSpan.End
                    || !TextMatchesContainingSpan(
                        declaration.DeclarationSpan,
                        declaration.DeclarationText,
                        documentationSpan,
                        declaration.DocumentationText!)
                    || !TextMatchesContainingSpan(
                        declaration.LeadingTriviaSpan,
                        declaration.LeadingTriviaText,
                        documentationSpan,
                        declaration.DocumentationText!))
            || declaration.BlockState == DocumentationBlockState.NoBlock
                && declaration.DocumentationText is not null
            || declaration.BlockState != DocumentationBlockState.NoBlock
                && declaration.DocumentationText is null
            || declaration.BlockState is DocumentationBlockState.NoBlock
                    or DocumentationBlockState.WhitespaceOnly
                && declaration.ParentSubstantive
            || declaration.BlockState == DocumentationBlockState.WellFormed
                && !declaration.ParentSubstantive)
        {
            return false;
        }

        if (subject.ComponentKind is null)
        {
            return declaration.ComponentLocalName is null
                && declaration.ComponentMatch is null;
        }

        if (declaration.BlockState == DocumentationBlockState.Malformed)
        {
            return declaration.ComponentMatch is null
                && (subject.ComponentKind is ComponentKind.Parameter
                        or ComponentKind.TypeParameter
                    ? IsLocalName(declaration.ComponentLocalName)
                    : declaration.ComponentLocalName is null);
        }

        if (declaration.ComponentMatch is null
            || !Enum.IsDefined(declaration.ComponentMatch.Value))
        {
            return false;
        }

        if (declaration.ComponentMatch == DocumentationComponentMatch.Present
            && declaration.BlockState != DocumentationBlockState.WellFormed)
        {
            return false;
        }

        return subject.ComponentKind is ComponentKind.Parameter
                or ComponentKind.TypeParameter
            ? IsLocalName(declaration.ComponentLocalName)
            : declaration.ComponentLocalName is null;
    }

    private static bool IsLegalRole(
        TargetClassification target,
        DocumentationAuthorityRole role)
    {
        var partial = target.Traits.Contains(SymbolTrait.Partial);
        var partialType = partial
            && target.PrimaryKind is PrimarySymbolKind.Class
                or PrimarySymbolKind.Struct
                or PrimarySymbolKind.Interface;
        return role switch
        {
            DocumentationAuthorityRole.Ordinary => !partial,
            DocumentationAuthorityRole.PartialTypePart => partialType,
            DocumentationAuthorityRole.PartialMemberImplementing
                or DocumentationAuthorityRole.PartialMemberDefiningFallback =>
                partial && !partialType,
            _ => false,
        };
    }

    private static bool HasLegalAuthorityCardinality(
        DocumentationAuthorityRole role,
        int count) =>
        role switch
        {
            DocumentationAuthorityRole.PartialTypePart => count >= 1,
            DocumentationAuthorityRole.Ordinary
                or DocumentationAuthorityRole.PartialMemberImplementing
                or DocumentationAuthorityRole.PartialMemberDefiningFallback =>
                count == 1,
            _ => false,
        };

    private static bool SourceMatchesOrigin(
        ClassificationOrigin origin,
        DocumentationSourceKind sourceKind) =>
        (origin, sourceKind) switch
        {
            (ClassificationOrigin.Source, DocumentationSourceKind.Repository) => true,
            (
                ClassificationOrigin.SourceGenerator,
                DocumentationSourceKind.SourceGenerator) => true,
            (
                ClassificationOrigin.ToolGenerated,
                DocumentationSourceKind.ToolGenerated) => true,
            _ => false,
        };

    private static bool ValidateSource(DocumentationSourceIdentity source)
    {
        if (!IsCanonicalRepositoryPath(source.ProjectIdentity)
            || !IsSha256(source.SourceSha256)
            || !Enum.IsDefined(source.Kind))
        {
            return false;
        }

        return source switch
        {
            RepositoryDocumentationSourceIdentity repository =>
                source.Kind == DocumentationSourceKind.Repository
                && IsCanonicalRepositoryPath(repository.Path),
            GeneratedDocumentationSourceIdentity generated =>
                source.Kind is DocumentationSourceKind.SourceGenerator
                    or DocumentationSourceKind.ToolGenerated
                && IsOpaqueId(
                    generated.ProducerId,
                    source.Kind == DocumentationSourceKind.SourceGenerator
                        ? "sgp."
                        : "tgp.")
                && IsOpaqueId(
                    generated.OutputId,
                    source.Kind == DocumentationSourceKind.SourceGenerator
                        ? "sgo."
                        : "tgo."),
            _ => false,
        };
    }

    private static bool IsCanonicalRepositoryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path[0] is '/' or '\\'
            || path.Contains('\\')
            || path.All(character => !char.IsControl(character)) is false
            || path.Length >= 2
                && path[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z'
                && path[1] == ':')
        {
            return false;
        }

        return path.Split('/').All(segment =>
            segment.Length > 0
            && segment is not "." and not "..");
    }

    private static bool IsLocalName(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.All(character => !char.IsControl(character));

    private static bool IsSha256(string value) =>
        value.Length == 64
        && value.All(character =>
            character is >= '0' and <= '9'
                or >= 'a' and <= 'f');

    private static bool IsOpaqueId(string value, string prefix) =>
        value.Length == prefix.Length + 64
        && value.StartsWith(prefix, StringComparison.Ordinal)
        && IsSha256(value[prefix.Length..]);

    private static bool ValidateSpan(Utf16Span span, string text) =>
        span.Start >= 0
        && span.End >= span.Start
        && span.End - span.Start == text.Length;

    private static bool TextMatchesContainingSpan(
        Utf16Span containingSpan,
        string containingText,
        Utf16Span nestedSpan,
        string nestedText)
    {
        var offset = nestedSpan.Start - containingSpan.Start;
        return offset >= 0
            && nestedSpan.End <= containingSpan.End
            && offset <= containingText.Length - nestedText.Length
            && containingText.AsSpan(offset, nestedText.Length)
                .SequenceEqual(nestedText.AsSpan());
    }

    private static string Sha256(string value)
    {
        var bytes = new UTF8Encoding(false, true).GetBytes(value);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string SubjectKey(DocumentationObservationSubject subject) =>
        string.Join(
            "\u001f",
            subject.ParentSymbolRef.CompilationContextRef,
            subject.ParentSymbolRef.DocumentationCommentId,
            subject.ComponentKind is { } kind
                ? ClassificationVocabulary.GetId(kind)
                : string.Empty,
            subject.ComponentIdentity ?? string.Empty);

    private static string DeclarationKey(DocumentationDeclarationInput declaration) =>
        DeclarationLocationKey(declaration)
        + "\u001f"
        + declaration.DeclarationId;

    private static string DeclarationLocationKey(
        DocumentationDeclarationInput declaration) =>
        string.Join(
            "\u001f",
            declaration.Source.ProjectIdentity,
            ((int)declaration.Source.Kind).ToString("D2"),
            declaration.Source switch
            {
                RepositoryDocumentationSourceIdentity repository =>
                    repository.Path,
                GeneratedDocumentationSourceIdentity generated =>
                    generated.ProducerId + "\u001f" + generated.OutputId,
                _ => string.Empty,
            },
            declaration.DeclarationSpan.Start.ToString("D10"),
            declaration.DeclarationSpan.End.ToString("D10"),
            DocumentationObservationVocabulary.GetId(declaration.AuthorityRole));

    private static bool Equivalent(
        DocumentationDeclarationInput left,
        DocumentationDeclarationInput right) =>
        string.Equals(
            left.DeclarationId,
            right.DeclarationId,
            StringComparison.Ordinal)
        && left.AuthorityRole == right.AuthorityRole
        && left.Source.Equals(right.Source)
        && left.DeclarationSpan == right.DeclarationSpan
        && string.Equals(
            left.DeclarationText,
            right.DeclarationText,
            StringComparison.Ordinal)
        && left.LeadingTriviaSpan == right.LeadingTriviaSpan
        && string.Equals(
            left.LeadingTriviaText,
            right.LeadingTriviaText,
            StringComparison.Ordinal)
        && left.DocumentationSpan == right.DocumentationSpan
        && string.Equals(
            left.DocumentationText,
            right.DocumentationText,
            StringComparison.Ordinal)
        && left.BlockState == right.BlockState
        && left.ParentSubstantive == right.ParentSubstantive
        && string.Equals(
            left.ComponentLocalName,
            right.ComponentLocalName,
            StringComparison.Ordinal)
        && left.ComponentMatch == right.ComponentMatch;

    private void Observe(
        DocumentationObservationNormalizationStage stage,
        CancellationToken cancellationToken)
    {
        stageObserver?.Invoke(stage);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private sealed record Candidate(
        DocumentationObservationSubject Subject,
        bool AuthorityComplete,
        ImmutableArray<DocumentationDeclarationInput> Declarations);
}
