using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ContractScribe.Core;
using Microsoft.CodeAnalysis;

namespace ContractScribe.Roslyn;

public enum PolicyEvidenceExtractionStatus
{
    Success,
    Failure,
    Cancelled,
}

public sealed record PolicyEvidenceSubjectBinding
{
    internal PolicyEvidenceSubjectBinding(
        DocumentationObservationSubject subject,
        PolicyContributionSet policyContributions,
        BoundObservationEvidence evidence)
    {
        Subject = subject;
        PolicyContributions = policyContributions;
        Evidence = evidence;
    }

    public DocumentationObservationSubject Subject { get; }

    public PolicyContributionSet PolicyContributions { get; }

    public BoundObservationEvidence Evidence { get; }
}

public sealed class PolicyEvidenceExtractionOutcome
{
    private PolicyEvidenceExtractionOutcome(
        PolicyEvidenceExtractionStatus status,
        ImmutableArray<PolicyEvidenceSubjectBinding> bindings)
    {
        Status = status;
        Bindings = bindings;
    }

    public PolicyEvidenceExtractionStatus Status { get; }

    public ImmutableArray<PolicyEvidenceSubjectBinding> Bindings { get; }

    internal static PolicyEvidenceExtractionOutcome Success(
        ImmutableArray<PolicyEvidenceSubjectBinding> bindings) =>
        new(PolicyEvidenceExtractionStatus.Success, bindings);

    public static PolicyEvidenceExtractionOutcome Failure() =>
        new(PolicyEvidenceExtractionStatus.Failure, []);

    public static PolicyEvidenceExtractionOutcome Cancelled() =>
        new(PolicyEvidenceExtractionStatus.Cancelled, []);
}

public sealed class PolicyEvidenceExtractor
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public PolicyEvidenceExtractionOutcome Extract(
        ClassifiedRepositorySession session,
        DocumentationObservationOutcome observations,
        PolicyDocumentV1 policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(policy);
        if (!session.IsBoundToClassificationSession
            || session.Classification.Status != ClassificationRunStatus.Success
            || session.Classification.ClassificationSet is not { } classifications
            || observations.Status != DocumentationObservationRunStatus.Success
            || observations.ObservationSet is not { } observationSet
            || classifications.TargetProfile != policy.TargetProfile)
        {
            return observations.Status == DocumentationObservationRunStatus.Cancelled
                ? PolicyEvidenceExtractionOutcome.Cancelled()
                : PolicyEvidenceExtractionOutcome.Failure();
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!HasExactObservationSubjectSet(
                    classifications,
                    observationSet.Observations))
            {
                return PolicyEvidenceExtractionOutcome.Failure();
            }

            var projects = session.RepositorySession.Projects
                .GroupBy(project => project.CompilationContextRef, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Single(),
                    StringComparer.Ordinal);
            var contributions = ExtractContributions(
                classifications,
                projects,
                policy,
                cancellationToken);
            if (contributions is null)
            {
                return PolicyEvidenceExtractionOutcome.Failure();
            }

            var output = ImmutableArray.CreateBuilder<PolicyEvidenceSubjectBinding>(
                observationSet.Observations.Length);
            foreach (var observation in observationSet.Observations
                .OrderBy(item => SubjectKey(item.Subject), StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!contributions.TryGetValue(
                        observation.Subject.ParentSymbolRef,
                        out var contributionSet)
                    || !projects.TryGetValue(
                        observation.Subject.ParentSymbolRef.CompilationContextRef,
                        out var project))
                {
                    return PolicyEvidenceExtractionOutcome.Failure();
                }

                var evidence = ExtractEvidence(
                    project,
                    observation,
                    cancellationToken);
                if (evidence is null)
                {
                    return PolicyEvidenceExtractionOutcome.Failure();
                }

                output.Add(new PolicyEvidenceSubjectBinding(
                    observation.Subject,
                    contributionSet,
                    evidence));
            }

            cancellationToken.ThrowIfCancellationRequested();
            return PolicyEvidenceExtractionOutcome.Success(output.MoveToImmutable());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return PolicyEvidenceExtractionOutcome.Cancelled();
        }
        catch (Exception)
        {
            return PolicyEvidenceExtractionOutcome.Failure();
        }
    }

    private static Dictionary<SymbolRef, PolicyContributionSet>? ExtractContributions(
        ClassificationSet classifications,
        IReadOnlyDictionary<string, LoadedProject> projects,
        PolicyDocumentV1 policy,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<SymbolRef, PolicyContributionSet>();
        foreach (var target in classifications.Targets
            .Where(target => target.SupportStatus == SupportStatus.Supported)
            .OrderBy(target => SymbolKey(target.SymbolRef), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!projects.TryGetValue(target.SymbolRef.CompilationContextRef, out var project)
                || !TryResolveSymbol(project, target.SymbolRef, out var symbol))
            {
                return null;
            }

            var inputs = new List<PolicyContributionInput>();
            foreach (var reference in ContributionReferences(symbol!)
                .OrderBy(item => item.SyntaxTree.FilePath, StringComparer.Ordinal)
                .ThenBy(item => item.Span.Start)
                .ThenBy(item => item.Span.Length))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!project.SourceTrees.TryGetValue(reference.SyntaxTree, out var source))
                {
                    return null;
                }

                switch (source.Kind)
                {
                    case LoadedSourceKind.Repository
                        when source.RepositoryIdentity is { } path:
                        inputs.Add(PolicyConfigurationInput.Repository(
                            project.ProjectIdentity,
                            path));
                        break;
                    case LoadedSourceKind.SourceGenerator or LoadedSourceKind.ToolGenerated
                        when source.GeneratedSource is { } generated
                        && GeneratedSourceMatches(project, generated):
                        inputs.Add(PolicyConfigurationInput.Generated(
                            project.ProjectIdentity,
                            source.Kind == LoadedSourceKind.SourceGenerator
                                ? "source-generator"
                                : "tool-generated",
                            generated.ProducerId,
                            generated.OutputId));
                        break;
                    default:
                        return null;
                }
            }

            if (inputs.Count == 0)
            {
                return null;
            }

            var evaluated = PolicyConfigurationEvaluator.Evaluate(
                policy,
                inputs,
                cancellationToken);
            if (evaluated.Status != PolicyRunStatus.Success
                || evaluated.ContributionSet is not { } contributionSet)
            {
                if (evaluated.Status == PolicyRunStatus.Cancelled)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                return null;
            }

            result.Add(target.SymbolRef, contributionSet);
        }

        return result;
    }

    private static BoundObservationEvidence? ExtractEvidence(
        LoadedProject project,
        DocumentationObservation observation,
        CancellationToken cancellationToken)
    {
        if (!TryCreateSubject(observation.Subject, out var subject))
        {
            return null;
        }

        var candidates = new List<EvidenceCandidateInput>();
        var bindings = new List<EvidenceDeclarationBindingInput>();
        foreach (var declaration in observation.Declarations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var useDocumentation = RequiresDocumentationEvidence(
                observation.Subject,
                declaration);
            if (!TryResolveSource(project, declaration.Source, out var syntaxTree))
            {
                return null;
            }

            var sourceText = syntaxTree!.GetText(cancellationToken).ToString();
            cancellationToken.ThrowIfCancellationRequested();
            var sourceSha256 = Sha256(sourceText);
            if (!string.Equals(
                sourceSha256,
                declaration.Source.SourceSha256,
                StringComparison.Ordinal))
            {
                return null;
            }

            if (!TryExtractRegion(
                sourceText,
                declaration.DeclarationSpan,
                declaration.DeclarationText,
                declaration.DeclarationSha256,
                out var declarationRegion))
            {
                return null;
            }

            string? declarationEvidenceId = null;
            if (!useDocumentation)
            {
                declarationEvidenceId = EvidenceId(
                    "declaration",
                    subject!,
                    declaration.DeclarationId,
                    declaration.Source,
                    declaration.DeclarationSpan,
                    declaration.DeclarationSha256);
                candidates.Add(EvidenceInput.Candidate(
                    declarationEvidenceId,
                    subject!,
                    EvidenceKind.SourceDeclaration,
                    EvidenceRelation.Declares,
                    declarationRegion!,
                    CreateLocator(declaration.Source, declaration.DeclarationSpan),
                    declaration.DeclarationSha256));
            }

            string? documentationEvidenceId = null;
            if (declaration.DocumentationSpan is { } documentationSpan
                && declaration.DocumentationText is { } documentationText
                && declaration.DocumentationSha256 is { } documentationSha256)
            {
                if (!TryExtractRegion(
                    sourceText,
                    documentationSpan,
                    documentationText,
                    documentationSha256,
                    out var documentationRegion))
                {
                    return null;
                }

                if (useDocumentation)
                {
                    documentationEvidenceId = EvidenceId(
                        "documentation",
                        subject!,
                        declaration.DeclarationId,
                        declaration.Source,
                        documentationSpan,
                        documentationSha256);
                    candidates.Add(EvidenceInput.Candidate(
                        documentationEvidenceId,
                        subject!,
                        EvidenceKind.SourceXmlDocumentation,
                        EvidenceRelation.Documents,
                        documentationRegion!,
                        CreateLocator(declaration.Source, documentationSpan),
                        documentationSha256));
                }
            }
            else if (declaration.DocumentationSpan is not null
                || declaration.DocumentationText is not null
                || declaration.DocumentationSha256 is not null)
            {
                return null;
            }

            if (useDocumentation && documentationEvidenceId is null)
            {
                return null;
            }

            bindings.Add(EvidenceBindingInput.Declaration(
                declaration.DeclarationId,
                declarationEvidenceId,
                documentationEvidenceId));
        }

        var omissions = observation.Completeness
            == DocumentationAuthorityCompleteness.Incomplete
            ? new[] { EvidenceOmissionReason.SourceUnavailable }
            : [];
        var normalized = EvidenceNormalizer.Normalize(
            candidates,
            omissions,
            cancellationToken: cancellationToken);
        if (normalized.Status == EvidenceRunStatus.Cancelled)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (normalized.Status != EvidenceRunStatus.Success
            || normalized.Bundle is not { } bundle)
        {
            return null;
        }

        var bound = EvidenceObservationBinder.Bind(
            observation,
            bundle,
            bindings,
            cancellationToken);
        if (bound.Status == EvidenceRunStatus.Cancelled)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        return bound.Status == EvidenceRunStatus.Success
            ? bound.Binding
            : null;
    }

    private static IEnumerable<SyntaxReference> ContributionReferences(ISymbol symbol)
    {
        var canonical = CanonicalPartialMember(symbol);
        foreach (var reference in canonical.DeclaringSyntaxReferences)
        {
            yield return reference;
        }

        if (GetPartialImplementation(canonical) is { } implementation)
        {
            foreach (var reference in implementation.DeclaringSyntaxReferences)
            {
                yield return reference;
            }
        }
    }

    private static bool TryResolveSymbol(
        LoadedProject project,
        SymbolRef symbolRef,
        out ISymbol? symbol)
    {
        var symbols = DocumentationCommentId.GetSymbolsForDeclarationId(
                symbolRef.DocumentationCommentId,
                project.Compilation)
            .Select(CanonicalPartialMember)
            .Distinct(SymbolEqualityComparer.Default)
            .ToArray();
        symbol = symbols.Length == 1 ? symbols[0] : null;
        return symbol is not null;
    }

    private static ISymbol CanonicalPartialMember(ISymbol symbol) => symbol switch
    {
        IMethodSymbol { PartialDefinitionPart: { } definition } => definition,
        IPropertySymbol { PartialDefinitionPart: { } definition } => definition,
        IEventSymbol { PartialDefinitionPart: { } definition } => definition,
        _ => symbol,
    };

    private static ISymbol? GetPartialImplementation(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.PartialImplementationPart,
        IPropertySymbol property => property.PartialImplementationPart,
        IEventSymbol @event => @event.PartialImplementationPart,
        _ => null,
    };

    private static bool TryResolveSource(
        LoadedProject project,
        DocumentationSourceIdentity source,
        out SyntaxTree? syntaxTree)
    {
        var matches = project.SourceTrees
            .Where(pair => SourceMatches(project, pair.Value, source))
            .Select(pair => pair.Key)
            .ToArray();
        syntaxTree = matches.Length == 1 ? matches[0] : null;
        return syntaxTree is not null;
    }

    private static bool SourceMatches(
        LoadedProject project,
        LoadedSourceTree loaded,
        DocumentationSourceIdentity source)
    {
        if (!string.Equals(
            source.ProjectIdentity,
            project.ProjectIdentity,
            StringComparison.Ordinal))
        {
            return false;
        }

        return source switch
        {
            RepositoryDocumentationSourceIdentity repository =>
                loaded.Kind == LoadedSourceKind.Repository
                && string.Equals(
                    loaded.RepositoryIdentity,
                    repository.Path,
                    StringComparison.Ordinal),
            GeneratedDocumentationSourceIdentity generated =>
                loaded.GeneratedSource is { } fact
                && GeneratedSourceMatches(project, fact)
                && loaded.Kind == SourceKind(generated.Kind)
                && string.Equals(fact.ProducerId, generated.ProducerId, StringComparison.Ordinal)
                && string.Equals(fact.OutputId, generated.OutputId, StringComparison.Ordinal)
                && string.Equals(
                    fact.SourceSha256,
                    generated.SourceSha256,
                    StringComparison.Ordinal),
            _ => false,
        };
    }

    private static LoadedSourceKind SourceKind(DocumentationSourceKind kind) => kind switch
    {
        DocumentationSourceKind.SourceGenerator => LoadedSourceKind.SourceGenerator,
        DocumentationSourceKind.ToolGenerated => LoadedSourceKind.ToolGenerated,
        _ => throw new InvalidOperationException("Unknown generated source kind."),
    };

    private static bool GeneratedSourceMatches(
        LoadedProject project,
        GeneratedSourceFact generated) =>
        string.Equals(
            generated.ProjectIdentity,
            project.ProjectIdentity,
            StringComparison.Ordinal)
        && string.Equals(
            generated.CompilationContextRef,
            project.CompilationContextRef,
            StringComparison.Ordinal);

    private static bool TryExtractRegion(
        string fullSource,
        Utf16Span span,
        string expectedText,
        string expectedSha256,
        out string? region)
    {
        region = null;
        if (span.Start < 0 || span.End < span.Start || span.End > fullSource.Length)
        {
            return false;
        }

        var actual = fullSource[span.Start..span.End];
        if (!string.Equals(actual, expectedText, StringComparison.Ordinal)
            || !string.Equals(Sha256(actual), expectedSha256, StringComparison.Ordinal))
        {
            return false;
        }

        region = actual;
        return true;
    }

    private static EvidenceLocator CreateLocator(
        DocumentationSourceIdentity source,
        Utf16Span span) =>
        source switch
        {
            RepositoryDocumentationSourceIdentity repository =>
                EvidenceInput.RepositoryLocator(
                    repository.Path,
                    span.Start,
                    span.End),
            GeneratedDocumentationSourceIdentity generated =>
                EvidenceInput.GeneratedOutputLocator(
                    generated.Kind == DocumentationSourceKind.SourceGenerator
                        ? GeneratedOutputKind.SourceGenerator
                        : GeneratedOutputKind.ToolGenerated,
                    generated.ProducerId,
                    generated.OutputId,
                    generated.SourceSha256,
                    span.Start,
                    span.End),
            _ => throw new InvalidOperationException("Unknown source identity."),
        };

    private static bool TryCreateSubject(
        DocumentationObservationSubject observationSubject,
        out EvidenceSubject? subject)
    {
        subject = observationSubject.ComponentKind is { } componentKind
            && observationSubject.ComponentIdentity is { } identity
            ? EvidenceInput.ComponentSubject(
                observationSubject.ParentSymbolRef.CompilationContextRef,
                observationSubject.ParentSymbolRef.DocumentationCommentId,
                componentKind,
                identity)
            : observationSubject.ComponentKind is null
                && observationSubject.ComponentIdentity is null
                ? EvidenceInput.TargetSubject(
                    observationSubject.ParentSymbolRef.CompilationContextRef,
                    observationSubject.ParentSymbolRef.DocumentationCommentId)
                : null;
        return subject is not null;
    }

    private static string EvidenceId(
        string kind,
        EvidenceSubject subject,
        string declarationId,
        DocumentationSourceIdentity source,
        Utf16Span span,
        string regionSha256) =>
        "evidence."
        + kind
        + "-"
        + DomainSeparatedHash(
            "contract-scribe/evidence-id/v1",
            kind,
            SubjectKey(subject),
            declarationId,
            SourceKey(source),
            span.Start.ToString(CultureInfo.InvariantCulture),
            span.End.ToString(CultureInfo.InvariantCulture),
            regionSha256);

    private static string SourceKey(DocumentationSourceIdentity source) => source switch
    {
        RepositoryDocumentationSourceIdentity repository =>
            "repository\0" + repository.ProjectIdentity + "\0" + repository.Path,
        GeneratedDocumentationSourceIdentity generated =>
            "generated\0"
            + generated.ProjectIdentity
            + "\0"
            + DocumentationObservationVocabulary.GetId(generated.Kind)
            + "\0"
            + generated.ProducerId
            + "\0"
            + generated.OutputId,
        _ => throw new InvalidOperationException("Unknown source identity."),
    };

    private static bool HasExactObservationSubjectSet(
        ClassificationSet classifications,
        IEnumerable<DocumentationObservation> observations)
    {
        var expected = classifications.Targets
            .Where(target => target.SupportStatus == SupportStatus.Supported)
            .Select(target => SubjectKey(target.SymbolRef, null, null))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var component in classifications.Components
            .Where(DocumentationObserver.IsObservableComponent))
        {
            expected.Add(SubjectKey(
                component.ParentSymbolRef,
                component.ComponentKind,
                component.Identity));
        }

        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (var observation in observations)
        {
            if (!actual.Add(SubjectKey(observation.Subject)))
            {
                return false;
            }
        }

        return actual.SetEquals(expected);
    }

    private static string SubjectKey(DocumentationObservationSubject subject) =>
        SubjectKey(
            subject.ParentSymbolRef,
            subject.ComponentKind,
            subject.ComponentIdentity);

    private static string SubjectKey(
        SymbolRef parentSymbolRef,
        ComponentKind? componentKind,
        string? componentIdentity) =>
        parentSymbolRef.CompilationContextRef
        + "\0"
        + parentSymbolRef.DocumentationCommentId
        + "\0"
        + (componentKind is { } kind
            ? ClassificationVocabulary.GetId(kind)
            : string.Empty)
        + "\0"
        + (componentIdentity ?? string.Empty);

    private static string SubjectKey(EvidenceSubject subject) => subject switch
    {
        ComponentEvidenceSubject component =>
            component.ParentSymbolRef.CompilationContextRef
            + "\0"
            + component.ParentSymbolRef.DocumentationCommentId
            + "\0"
            + ClassificationVocabulary.GetId(component.ComponentKind)
            + "\0"
            + component.Identity,
        _ => subject.ParentSymbolRef.CompilationContextRef
            + "\0"
            + subject.ParentSymbolRef.DocumentationCommentId,
    };

    private static string SymbolKey(SymbolRef symbolRef) =>
        symbolRef.CompilationContextRef + "\0" + symbolRef.DocumentationCommentId;

    private static bool RequiresDocumentationEvidence(
        DocumentationObservationSubject subject,
        DocumentationDeclarationFact declaration) =>
        declaration.BlockState == DocumentationBlockState.Malformed
        || (subject.ComponentKind is null
            ? declaration.ParentSubstantive
            : declaration.BlockState == DocumentationBlockState.WellFormed
                && declaration.ComponentMatch == DocumentationComponentMatch.Present);

    private static string DomainSeparatedHash(string domain, params string[] fields)
    {
        using var stream = new MemoryStream();
        stream.Write(Encoding.ASCII.GetBytes(domain));
        stream.WriteByte(0);
        Span<byte> length = stackalloc byte[4];
        foreach (var field in fields)
        {
            var bytes = StrictUtf8.GetBytes(field);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
                length,
                checked((uint)bytes.Length));
            stream.Write(length);
            stream.Write(bytes);
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(text)))
            .ToLowerInvariant();
}
