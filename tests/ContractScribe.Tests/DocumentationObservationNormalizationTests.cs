using System.Collections.Immutable;
using ContractScribe.Core;

namespace ContractScribe.Tests;

public sealed class DocumentationObservationNormalizationTests
{
    [Theory]
    [InlineData(true, true, DocumentationObservationValue.Present,
        DocumentationAuthorityCompleteness.Complete,
        DocumentationUnavailableCause.None)]
    [InlineData(true, false, DocumentationObservationValue.Present,
        DocumentationAuthorityCompleteness.PositiveOnly,
        DocumentationUnavailableCause.None)]
    [InlineData(false, true, DocumentationObservationValue.Absent,
        DocumentationAuthorityCompleteness.Complete,
        DocumentationUnavailableCause.None)]
    [InlineData(false, false, DocumentationObservationValue.Unavailable,
        DocumentationAuthorityCompleteness.Incomplete,
        DocumentationUnavailableCause.SourceUnavailable)]
    public void ParentPrecedenceIsDeterministic(
        bool substantive,
        bool complete,
        DocumentationObservationValue expectedValue,
        DocumentationAuthorityCompleteness expectedCompleteness,
        DocumentationUnavailableCause expectedCause)
    {
        var (set, target, _) = Classification(componentKind: null);
        var buffer = new DocumentationObservationCandidateBuffer(set);
        buffer.AddTarget(
            target,
            complete,
            [Declaration(
                blockState: substantive
                    ? DocumentationBlockState.WellFormed
                    : DocumentationBlockState.WhitespaceOnly,
                parentSubstantive: substantive)]);

        var observation = AssertSuccess(buffer.Normalize());

        Assert.Equal(expectedValue, observation.Value);
        Assert.Equal(expectedCompleteness, observation.Completeness);
        Assert.Equal(expectedCause, observation.UnavailableCause);
    }

    [Theory]
    [InlineData(
        null,
        false,
        DocumentationBlockState.Malformed,
        DocumentationObservationValue.Unavailable,
        DocumentationAuthorityCompleteness.Incomplete,
        DocumentationUnavailableCause.SourceUnavailable)]
    [InlineData(
        null,
        true,
        DocumentationBlockState.Malformed,
        DocumentationObservationValue.Unavailable,
        DocumentationAuthorityCompleteness.Complete,
        DocumentationUnavailableCause.MalformedXml)]
    [InlineData(
        DocumentationComponentMatch.Absent,
        true,
        DocumentationBlockState.WellFormed,
        DocumentationObservationValue.Absent,
        DocumentationAuthorityCompleteness.Complete,
        DocumentationUnavailableCause.None)]
    public void ComponentPrecedenceKeepsPositiveThenSourceThenMalformed(
        DocumentationComponentMatch? match,
        bool complete,
        DocumentationBlockState blockState,
        DocumentationObservationValue expectedValue,
        DocumentationAuthorityCompleteness expectedCompleteness,
        DocumentationUnavailableCause expectedCause)
    {
        var (set, target, component) = Classification(ComponentKind.Parameter);
        var buffer = new DocumentationObservationCandidateBuffer(set);
        buffer.AddTarget(
            target,
            true,
            [Declaration(
                blockState: DocumentationBlockState.NoBlock,
                parentSubstantive: false,
                documentationText: null)]);
        buffer.AddComponent(
            component!,
            complete,
            [Declaration(
                blockState,
                parentSubstantive: blockState != DocumentationBlockState.NoBlock,
                componentLocalName: "value",
                componentMatch: match)]);

        var observation = AssertSuccess(buffer.Normalize(), component: true);

        Assert.Equal(expectedValue, observation.Value);
        Assert.Equal(expectedCompleteness, observation.Completeness);
        Assert.Equal(expectedCause, observation.UnavailableCause);
    }

    [Fact]
    public void ComponentPositiveWinsOverSeparateMalformedIncompleteAuthority()
    {
        var (set, target, component) = Classification(ComponentKind.Parameter);
        var buffer = new DocumentationObservationCandidateBuffer(set);
        buffer.AddTarget(target, true, [Declaration()]);
        buffer.AddComponent(
            component!,
            false,
            [
                Declaration(
                    DocumentationBlockState.WellFormed,
                    componentLocalName: "value",
                    componentMatch: DocumentationComponentMatch.Present),
                Declaration(
                    DocumentationBlockState.Malformed,
                    componentLocalName: "value",
                    componentMatch: null,
                    repositoryPath: "src/Other.cs",
                    declarationIdCharacter: 'e'),
            ]);

        var observation = AssertSuccess(buffer.Normalize(), component: true);

        Assert.Equal(DocumentationObservationValue.Present, observation.Value);
        Assert.Equal(
            DocumentationAuthorityCompleteness.PositiveOnly,
            observation.Completeness);
        Assert.Equal(
            DocumentationUnavailableCause.None,
            observation.UnavailableCause);
    }

    [Fact]
    public void IncompleteAuthorityMayPublishUnavailableWithoutReadableFacts()
    {
        var (set, target, _) = Classification(componentKind: null);
        var buffer = new DocumentationObservationCandidateBuffer(set);
        buffer.AddTarget(target, false, []);

        var observation = AssertSuccess(buffer.Normalize());

        Assert.Equal(DocumentationObservationValue.Unavailable, observation.Value);
        Assert.Empty(observation.Declarations);
    }

    [Fact]
    public void IdenticalFactsDeduplicateWhileConflictsAndMachinePathsFailClosed()
    {
        var (set, target, _) = Classification(componentKind: null);
        var duplicateFact = new DocumentationObservationCandidateBuffer(set);
        duplicateFact.AddTarget(
            target,
            true,
            [Declaration(), Declaration()]);
        var deduplicated = AssertSuccess(duplicateFact.Normalize());
        Assert.Single(deduplicated.Declarations);

        var conflict = new DocumentationObservationCandidateBuffer(set);
        conflict.AddTarget(
            target,
            true,
            [
                Declaration(),
                Declaration(
                    blockState: DocumentationBlockState.WhitespaceOnly,
                    parentSubstantive: false),
            ]);
        Assert.Equal(
            DocumentationObservationRunStatus.Failure,
            conflict.Normalize().Status);

        var duplicateSubject = new DocumentationObservationCandidateBuffer(set);
        duplicateSubject.AddTarget(target, true, [Declaration()]);
        duplicateSubject.AddTarget(target, true, [Declaration()]);
        Assert.Equal(
            DocumentationObservationRunStatus.Failure,
            duplicateSubject.Normalize().Status);

        var invalid = new DocumentationObservationCandidateBuffer(set);
        invalid.AddTarget(
            target,
            true,
            [Declaration(repositoryPath: @"C:\machine\A.cs")]);
        Assert.Equal(
            DocumentationObservationRunStatus.Failure,
            invalid.Normalize().Status);
    }

    [Fact]
    public void DiagnosticsAreBoundedDeduplicatedOrderedAndCancellationPublishesNoSet()
    {
        var (set, target, _) = Classification(componentKind: null);
        var buffer = new DocumentationObservationCandidateBuffer(set);
        buffer.AddTarget(target, true, [Declaration()]);
        var diagnostics = Enumerable.Range(0, 40)
            .Reverse()
            .Select(index => new DocumentationObservationDiagnostic(
                "stage",
                $"code-{index:D2}",
                "warning"))
            .Append(new DocumentationObservationDiagnostic(
                "stage",
                "code-00",
                "warning"));

        var outcome = buffer.Normalize(diagnostics);
        Assert.Equal(32, outcome.Diagnostics.Length);
        Assert.Equal(
            outcome.Diagnostics.OrderBy(item => item.Code, StringComparer.Ordinal),
            outcome.Diagnostics);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = buffer.Normalize(cancellationToken: cancellation.Token);
        Assert.Equal(DocumentationObservationRunStatus.Cancelled, cancelled.Status);
        Assert.Null(cancelled.ObservationSet);
    }

    [Fact]
    public void NonObservableClassificationStatesNeverBecomeObservations()
    {
        var classifications = new ClassificationCandidateBuffer();
        classifications.AddTarget(
            "context." + new string('a', 64),
            "M:Fixture.Run",
            PrimarySymbolKind.Method,
            ImmutableArray<SymbolTrait>.Empty,
            ClassificationOrigin.Source,
            [ClassificationInput.RepositoryLocator("src/Fixture.cs")]);
        classifications.AddTarget(
            "context." + new string('a', 64),
            "M:Fixture.Unknown",
            PrimarySymbolKind.Method,
            ImmutableArray<SymbolTrait>.Empty,
            ClassificationOrigin.Source,
            [ClassificationInput.RepositoryLocator("src/Unknown.cs")],
            semanticContextAvailable: false);
        classifications.AddUnresolvedDocumentationCandidate(
            "context." + new string('a', 64),
            ClassificationOrigin.Source,
            [ClassificationInput.RepositoryLocator("src/Unresolved.cs")]);
        var set = Assert.IsType<ClassificationSet>(
            classifications.Normalize(TargetProfile.ExternalApi)
                .ClassificationSet);
        var supported = Assert.Single(set.Targets, target =>
            target.SupportStatus == SupportStatus.Supported);
        var observationCandidates =
            new DocumentationObservationCandidateBuffer(set);
        observationCandidates.AddTarget(supported, true, [Declaration()]);

        var outcome = observationCandidates.Normalize();

        Assert.Equal(DocumentationObservationRunStatus.Success, outcome.Status);
        Assert.Single(outcome.ObservationSet!.Observations);

        var forbidden = new DocumentationObservationCandidateBuffer(set);
        forbidden.AddTarget(supported, true, [Declaration()]);
        forbidden.AddTarget(
            Assert.Single(set.Targets, target =>
                target.SupportStatus != SupportStatus.Supported),
            true,
            [Declaration(repositoryPath: "src/Unknown.cs")]);
        Assert.Equal(
            DocumentationObservationRunStatus.Failure,
            forbidden.Normalize().Status);
    }

    [Fact]
    public void StrictUtf8RejectsUnpairedSurrogatesWithoutPartialSuccess()
    {
        var (set, target, _) = Classification(componentKind: null);
        var buffer = new DocumentationObservationCandidateBuffer(set);
        buffer.AddTarget(
            target,
            true,
            [Declaration(documentationText: "/// \ud800\n")]);

        var outcome = buffer.Normalize();

        Assert.Equal(DocumentationObservationRunStatus.Failure, outcome.Status);
        Assert.Null(outcome.ObservationSet);
    }

    private static DocumentationObservation AssertSuccess(
        DocumentationObservationOutcome outcome,
        bool component = false)
    {
        Assert.Equal(DocumentationObservationRunStatus.Success, outcome.Status);
        var set = Assert.IsType<DocumentationObservationSet>(outcome.ObservationSet);
        return component
            ? Assert.Single(set.Observations, observation =>
                observation.Subject.ComponentKind is not null)
            : Assert.Single(set.Observations, observation =>
                observation.Subject.ComponentKind is null);
    }

    private static (
        ClassificationSet Set,
        TargetClassification Target,
        ComponentClassification? Component) Classification(
        ComponentKind? componentKind)
    {
        var buffer = new ClassificationCandidateBuffer();
        buffer.AddTarget(
            "context." + new string('a', 64),
            "M:Fixture.Run(System.String)",
            PrimarySymbolKind.Method,
            ImmutableArray<SymbolTrait>.Empty,
            ClassificationOrigin.Source,
            [ClassificationInput.RepositoryLocator("src/Fixture.cs")]);
        if (componentKind is { } kind)
        {
            buffer.AddComponent(
                "context." + new string('a', 64),
                "M:Fixture.Run(System.String)",
                kind,
                kind switch
                {
                    ComponentKind.Parameter => "parameter/0",
                    ComponentKind.TypeParameter => "type-parameter/0",
                    ComponentKind.Return => "return",
                    ComponentKind.Value => "value",
                    _ => "unsupported",
                },
                ClassificationOrigin.Source);
        }

        var outcome = buffer.Normalize(TargetProfile.ExternalApi);
        var set = Assert.IsType<ClassificationSet>(outcome.ClassificationSet);
        return (
            set,
            Assert.Single(set.Targets),
            set.Components.SingleOrDefault());
    }

    private static DocumentationDeclarationInput Declaration(
        DocumentationBlockState blockState = DocumentationBlockState.WellFormed,
        bool parentSubstantive = true,
        string? documentationText = "/// <summary>Documented.</summary>\n",
        string? componentLocalName = null,
        DocumentationComponentMatch? componentMatch = null,
        string repositoryPath = "src/Fixture.cs",
        char declarationIdCharacter = 'd')
    {
        const string bodyText = "public void Run(string value) { }";
        var leadingText = documentationText ?? string.Empty;
        var declarationText = leadingText + bodyText;
        Utf16Span? documentationSpan = documentationText is null
            ? null
            : DocumentationObservationInput.Span(0, documentationText.Length);
        return DocumentationObservationInput.RepositoryDeclaration(
            "decl." + new string(declarationIdCharacter, 64),
            DocumentationAuthorityRole.Ordinary,
            "project." + new string('b', 64),
            repositoryPath,
            new string('c', 64),
            DocumentationObservationInput.Span(0, declarationText.Length),
            declarationText,
            DocumentationObservationInput.Span(0, leadingText.Length),
            leadingText,
            documentationSpan,
            documentationText,
            blockState,
            parentSubstantive,
            componentLocalName,
            componentMatch);
    }
}
