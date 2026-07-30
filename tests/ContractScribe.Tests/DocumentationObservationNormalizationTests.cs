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
        var (set, target, component) = Classification(
            ComponentKind.TypeParameter,
            PrimarySymbolKind.Class,
            [SymbolTrait.Generic, SymbolTrait.Partial]);
        var buffer = new DocumentationObservationCandidateBuffer(set);
        buffer.AddTarget(
            target,
            true,
            [Declaration(
                authorityRole: DocumentationAuthorityRole.PartialTypePart)]);
        buffer.AddComponent(
            component!,
            false,
            [
                Declaration(
                    DocumentationBlockState.WellFormed,
                    componentLocalName: "T",
                    componentMatch: DocumentationComponentMatch.Present,
                    authorityRole: DocumentationAuthorityRole.PartialTypePart),
                Declaration(
                    DocumentationBlockState.Malformed,
                    componentLocalName: "T",
                    componentMatch: null,
                    repositoryPath: "src/Other.cs",
                    declarationIdCharacter: 'e',
                    authorityRole: DocumentationAuthorityRole.PartialTypePart),
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
        var validDiagnostics = Enum
            .GetValues<DocumentationObservationDiagnosticStage>()
            .SelectMany(stage =>
                Enum.GetValues<DocumentationObservationDiagnosticCode>()
                    .SelectMany(code =>
                        Enum.GetValues<DocumentationObservationDiagnosticSeverity>()
                            .Select(severity =>
                                new DocumentationObservationDiagnostic(
                                    stage,
                                    code,
                                    severity))))
            .Take(40)
            .ToArray();
        var diagnostics = validDiagnostics
            .Reverse()
            .Append(validDiagnostics[0])
            .Append(new DocumentationObservationDiagnostic(
                (DocumentationObservationDiagnosticStage)int.MaxValue,
                DocumentationObservationDiagnosticCode.Unrepresentable,
                DocumentationObservationDiagnosticSeverity.Error));

        var outcome = buffer.Normalize(diagnostics);
        Assert.Equal(32, outcome.Diagnostics.Length);
        Assert.Equal(
            outcome.Diagnostics
                .OrderBy(item => item.Stage)
                .ThenBy(item => item.Code)
                .ThenBy(item => item.Severity),
            outcome.Diagnostics);
        Assert.DoesNotContain(
            outcome.Diagnostics,
            item => !Enum.IsDefined(item.Stage));

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

    [Fact]
    public void CompleteNonSubstantiveMalformedTargetIsUnavailable()
    {
        var (set, target, _) = Classification(componentKind: null);
        var buffer = new DocumentationObservationCandidateBuffer(set);
        buffer.AddTarget(
            target,
            true,
            [Declaration(
                blockState: DocumentationBlockState.Malformed,
                parentSubstantive: false)]);

        var observation = AssertSuccess(buffer.Normalize());

        Assert.Equal(DocumentationObservationValue.Unavailable, observation.Value);
        Assert.Equal(
            DocumentationUnavailableCause.MalformedXml,
            observation.UnavailableCause);
    }

    [Fact]
    public void ParentPositiveWinsOverSeparateMalformedAuthority()
    {
        var (set, target, _) = Classification(
            componentKind: null,
            primaryKind: PrimarySymbolKind.Class,
            traits: [SymbolTrait.Partial]);
        var buffer = new DocumentationObservationCandidateBuffer(set);
        buffer.AddTarget(
            target,
            true,
            [
                Declaration(
                    authorityRole: DocumentationAuthorityRole.PartialTypePart),
                Declaration(
                    blockState: DocumentationBlockState.Malformed,
                    parentSubstantive: false,
                    repositoryPath: "src/Other.cs",
                    declarationIdCharacter: 'e',
                    authorityRole: DocumentationAuthorityRole.PartialTypePart),
            ]);

        var observation = AssertSuccess(buffer.Normalize());

        Assert.Equal(DocumentationObservationValue.Present, observation.Value);
        Assert.Equal(
            DocumentationAuthorityCompleteness.Complete,
            observation.Completeness);
    }

    [Theory]
    [InlineData(DocumentationAuthorityRole.Ordinary, false)]
    [InlineData(DocumentationAuthorityRole.PartialMemberImplementing, true)]
    [InlineData(DocumentationAuthorityRole.PartialMemberDefiningFallback, true)]
    public void SingularAuthorityModesRejectMultipleDistinctFacts(
        DocumentationAuthorityRole role,
        bool partial)
    {
        var (set, target, _) = Classification(
            componentKind: null,
            traits: partial ? [SymbolTrait.Partial] : []);
        var valid = new DocumentationObservationCandidateBuffer(set);
        valid.AddTarget(target, true, [Declaration(authorityRole: role)]);
        Assert.Equal(
            DocumentationObservationRunStatus.Success,
            valid.Normalize().Status);

        var invalid = new DocumentationObservationCandidateBuffer(set);
        invalid.AddTarget(
            target,
            true,
            [
                Declaration(authorityRole: role),
                Declaration(
                    repositoryPath: "src/Other.cs",
                    declarationIdCharacter: 'e',
                    authorityRole: role),
            ]);
        Assert.Equal(
            DocumentationObservationRunStatus.Failure,
            invalid.Normalize().Status);
    }

    [Fact]
    public void PartialTypeAuthorityAcceptsOneOrMoreFactsButNotAnEmptyCompleteUniverse()
    {
        var (set, target, _) = Classification(
            componentKind: null,
            primaryKind: PrimarySymbolKind.Class,
            traits: [SymbolTrait.Partial]);
        foreach (var facts in new[]
        {
            new[]
            {
                Declaration(
                    authorityRole: DocumentationAuthorityRole.PartialTypePart),
            },
            new[]
            {
                Declaration(
                    authorityRole: DocumentationAuthorityRole.PartialTypePart),
                Declaration(
                    repositoryPath: "src/Other.cs",
                    declarationIdCharacter: 'e',
                    authorityRole: DocumentationAuthorityRole.PartialTypePart),
            },
        })
        {
            var valid = new DocumentationObservationCandidateBuffer(set);
            valid.AddTarget(target, true, facts);
            Assert.Equal(
                DocumentationObservationRunStatus.Success,
                valid.Normalize().Status);
        }

        var empty = new DocumentationObservationCandidateBuffer(set);
        empty.AddTarget(target, true, []);
        Assert.Equal(
            DocumentationObservationRunStatus.Failure,
            empty.Normalize().Status);
    }

    [Theory]
    [InlineData("/rooted.cs")]
    [InlineData("\\rooted.cs")]
    [InlineData("C:relative.cs")]
    [InlineData("C:/rooted.cs")]
    [InlineData("//server/share.cs")]
    [InlineData("src/../secret.cs")]
    [InlineData("src//double.cs")]
    public void RepositoryPathsUsePlatformIndependentLexicalValidation(
        string repositoryPath)
    {
        var (set, target, _) = Classification(componentKind: null);
        var buffer = new DocumentationObservationCandidateBuffer(set);
        buffer.AddTarget(target, true, [Declaration(repositoryPath: repositoryPath)]);

        Assert.Equal(
            DocumentationObservationRunStatus.Failure,
            buffer.Normalize().Status);
    }

    [Fact]
    public void NonDriveColonInARepositorySegmentRemainsValid()
    {
        var (set, target, _) = Classification(componentKind: null);
        var buffer = new DocumentationObservationCandidateBuffer(set);
        buffer.AddTarget(
            target,
            true,
            [Declaration(repositoryPath: "src/schema:generated/Fixture.cs")]);

        Assert.Equal(
            DocumentationObservationRunStatus.Success,
            buffer.Normalize().Status);
    }

    [Theory]
    [InlineData(ClassificationOrigin.Source, DocumentationSourceKind.SourceGenerator)]
    [InlineData(ClassificationOrigin.Source, DocumentationSourceKind.ToolGenerated)]
    [InlineData(ClassificationOrigin.SourceGenerator, DocumentationSourceKind.Repository)]
    [InlineData(ClassificationOrigin.SourceGenerator, DocumentationSourceKind.ToolGenerated)]
    [InlineData(ClassificationOrigin.ToolGenerated, DocumentationSourceKind.Repository)]
    [InlineData(ClassificationOrigin.ToolGenerated, DocumentationSourceKind.SourceGenerator)]
    public void SourceKindMustMatchTheClassifiedOrigin(
        ClassificationOrigin origin,
        DocumentationSourceKind sourceKind)
    {
        var (set, target, _) = Classification(
            componentKind: null,
            origin: origin);
        var buffer = new DocumentationObservationCandidateBuffer(set);
        buffer.AddTarget(
            target,
            true,
            [sourceKind == DocumentationSourceKind.Repository
                ? Declaration()
                : GeneratedDeclaration(sourceKind)]);

        Assert.Equal(
            DocumentationObservationRunStatus.Failure,
            buffer.Normalize().Status);
    }

    [Fact]
    public void NestedDeclarationRegionsMustContainTheExactSuppliedText()
    {
        var (set, target, _) = Classification(componentKind: null);
        var inconsistentLeading = DocumentationObservationInput.RepositoryDeclaration(
            "decl." + new string('d', 64),
            DocumentationAuthorityRole.Ordinary,
            "project." + new string('b', 64),
            "src/Fixture.cs",
            new string('c', 64),
            DocumentationObservationInput.Span(0, 7),
            "ABCbody",
            DocumentationObservationInput.Span(0, 3),
            "XYZ",
            null,
            null,
            DocumentationBlockState.NoBlock,
            false);
        var inconsistentDocumentation =
            DocumentationObservationInput.RepositoryDeclaration(
                "decl." + new string('e', 64),
                DocumentationAuthorityRole.Ordinary,
                "project." + new string('b', 64),
                "src/Fixture.cs",
                new string('c', 64),
                DocumentationObservationInput.Span(0, 12),
                "/// abc\nbody",
                DocumentationObservationInput.Span(0, 8),
                "/// abc\n",
                DocumentationObservationInput.Span(0, 8),
                "/// xyz\n",
                DocumentationBlockState.WellFormed,
                true);

        foreach (var declaration in new[]
        {
            inconsistentLeading,
            inconsistentDocumentation,
        })
        {
            var buffer = new DocumentationObservationCandidateBuffer(set);
            buffer.AddTarget(target, true, [declaration]);
            Assert.Equal(
                DocumentationObservationRunStatus.Failure,
                buffer.Normalize().Status);
        }
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
        ComponentKind? componentKind,
        PrimarySymbolKind primaryKind = PrimarySymbolKind.Method,
        IEnumerable<SymbolTrait>? traits = null,
        ClassificationOrigin origin = ClassificationOrigin.Source)
    {
        var buffer = new ClassificationCandidateBuffer();
        CandidateLocator[] locators = origin switch
        {
            ClassificationOrigin.Source =>
                [ClassificationInput.RepositoryLocator("src/Fixture.cs")],
            ClassificationOrigin.SourceGenerator =>
                [ClassificationInput.GeneratedSourceLocator(
                    "sgp." + new string('1', 64),
                    "sgo." + new string('2', 64))],
            ClassificationOrigin.ToolGenerated =>
                [ClassificationInput.ToolGeneratedLocator(
                    "tgp." + new string('3', 64),
                    "tgo." + new string('4', 64))],
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        buffer.AddTarget(
            "context." + new string('a', 64),
            "M:Fixture.Run(System.String)",
            primaryKind,
            traits?.ToImmutableArray() ?? [],
            origin,
            locators);
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
                origin);
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
        char declarationIdCharacter = 'd',
        DocumentationAuthorityRole authorityRole =
            DocumentationAuthorityRole.Ordinary)
    {
        const string bodyText = "public void Run(string value) { }";
        var leadingText = documentationText ?? string.Empty;
        var declarationText = leadingText + bodyText;
        Utf16Span? documentationSpan = documentationText is null
            ? null
            : DocumentationObservationInput.Span(0, documentationText.Length);
        return DocumentationObservationInput.RepositoryDeclaration(
            "decl." + new string(declarationIdCharacter, 64),
            authorityRole,
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

    private static DocumentationDeclarationInput GeneratedDeclaration(
        DocumentationSourceKind sourceKind)
    {
        const string documentationText = "/// <summary>Documented.</summary>\n";
        const string bodyText = "public void Run(string value) { }";
        var declarationText = documentationText + bodyText;
        return DocumentationObservationInput.GeneratedDeclaration(
            "decl." + new string('d', 64),
            DocumentationAuthorityRole.Ordinary,
            "project." + new string('b', 64),
            sourceKind,
            sourceKind == DocumentationSourceKind.SourceGenerator
                ? "sgp." + new string('1', 64)
                : "tgp." + new string('3', 64),
            sourceKind == DocumentationSourceKind.SourceGenerator
                ? "sgo." + new string('2', 64)
                : "tgo." + new string('4', 64),
            new string('c', 64),
            DocumentationObservationInput.Span(0, declarationText.Length),
            declarationText,
            DocumentationObservationInput.Span(0, documentationText.Length),
            documentationText,
            DocumentationObservationInput.Span(0, documentationText.Length),
            documentationText,
            DocumentationBlockState.WellFormed,
            true);
    }
}
