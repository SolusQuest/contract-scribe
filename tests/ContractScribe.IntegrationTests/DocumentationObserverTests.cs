using ContractScribe.Core;
using ContractScribe.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed class DocumentationObserverTests
{
    private const string Context = "context." + Sha;
    private const string ProjectIdentity = "project." + Sha;
    private const string Sha =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void ProductionObserverUsesDirectDeclarationsAndComponentSemantics()
    {
        var source = """
            public class Base
            {
                /// <summary>Base only.</summary>
                public virtual void Overridden() { }
            }

            public interface IApi
            {
                /// <summary>Interface only.</summary>
                void Implemented();

                /// <summary>Explicit interface only.</summary>
                void Explicit();
            }

            public interface IBase
            {
                /// <summary>Inherited interface member.</summary>
                void Inherited();
            }

            public interface IDerived : IBase { }

            /// <summary>First partial part.</summary>
            public partial class Fixture<T> : Base, IApi
            {
                public override void Overridden() { }

                public void Implemented() { }

                void IApi.Explicit() { }

                /// <summary>Good.</summary>
                /// <param name="value">Value docs.</param>
                /// <returns>Return docs.</returns>
                public int Good(string value) => value.Length;

                /// <param name="stale">Wrong name.</param>
                public void Wrong(string value) { }

                /// <param name="value"></param>
                public void Empty(string value) { }

                /// <summary><param name="value">Nested is not applicable.</param></summary>
                public void Nested(string value) { }

                /// <param name="value">broken
                public void Malformed(string value) { }

                /// <summary>Definition fallback.</summary>
                /// <param name="defining">Definition name.</param>
                public partial void Fallback(string defining);

                /// <summary>Definition must not win.</summary>
                public partial void Exclusive(string defining);
            }

            /// <summary>Second partial part.</summary>
            public partial class Fixture<T>
            {
                public partial void Fallback(string implementing) { }

                /// <whitespace-only>
                public partial void Exclusive(string implementing) { }

                /// <summary>Before attribute.</summary>
                [System.Obsolete]
                [System.CLSCompliant(true)]
                public void Attributed() { }
            }

            #if NEVER
            /// <summary>Inactive.</summary>
            public class Inactive { }
            #endif
            """.Replace(
                "/// <whitespace-only>",
                "///   ",
                StringComparison.Ordinal);

        using var session = CreateSession(source);
        var classified = new SymbolClassifier().ClassifySession(
            session,
            TargetProfile.ExternalApi);
        var outcome = new DocumentationObserver().Observe(classified);

        Assert.Equal(DocumentationObservationRunStatus.Success, outcome.Status);
        var observations = Assert.IsType<DocumentationObservationSet>(
            outcome.ObservationSet).Observations;

        AssertTarget(observations, "M:Fixture`1.Overridden", DocumentationObservationValue.Absent);
        AssertTarget(observations, "M:Fixture`1.Implemented", DocumentationObservationValue.Absent);
        var explicitInterface = AssertTarget(
            observations,
            "M:IApi.Explicit",
            DocumentationObservationValue.Present);
        Assert.Single(explicitInterface.Declarations);
        Assert.DoesNotContain(
            "IApi.Explicit",
            explicitInterface.Declarations[0].DocumentationText,
            StringComparison.Ordinal);
        Assert.DoesNotContain(observations, observation =>
            observation.Subject.ParentSymbolRef.DocumentationCommentId
                .StartsWith("M:Fixture", StringComparison.Ordinal)
            && observation.Subject.ParentSymbolRef.DocumentationCommentId
                .Contains("Explicit", StringComparison.Ordinal));
        AssertTarget(observations, "M:Fixture`1.Good(System.String)", DocumentationObservationValue.Present);
        AssertTarget(observations, "M:Fixture`1.Malformed(System.String)", DocumentationObservationValue.Present);
        AssertTarget(observations, "M:Fixture`1.Attributed", DocumentationObservationValue.Present);
        Assert.DoesNotContain(observations, observation =>
            observation.Subject.ParentSymbolRef.DocumentationCommentId
                is "T:Inactive" or "M:IDerived.Inherited");

        AssertComponent(
            observations,
            "M:Fixture`1.Good(System.String)",
            ComponentKind.Parameter,
            DocumentationObservationValue.Present,
            "value");
        AssertComponent(
            observations,
            "M:Fixture`1.Good(System.String)",
            ComponentKind.Return,
            DocumentationObservationValue.Present,
            null);
        AssertComponent(
            observations,
            "M:Fixture`1.Wrong(System.String)",
            ComponentKind.Parameter,
            DocumentationObservationValue.Absent,
            "value");
        AssertComponent(
            observations,
            "M:Fixture`1.Empty(System.String)",
            ComponentKind.Parameter,
            DocumentationObservationValue.Absent,
            "value");
        AssertComponent(
            observations,
            "M:Fixture`1.Nested(System.String)",
            ComponentKind.Parameter,
            DocumentationObservationValue.Absent,
            "value");
        var malformed = AssertComponent(
            observations,
            "M:Fixture`1.Malformed(System.String)",
            ComponentKind.Parameter,
            DocumentationObservationValue.Unavailable,
            "value");
        Assert.Equal(
            DocumentationUnavailableCause.MalformedXml,
            malformed.UnavailableCause);

        var partialType = AssertTarget(
            observations,
            "T:Fixture`1",
            DocumentationObservationValue.Present);
        Assert.Equal(2, partialType.Declarations.Length);
        Assert.All(
            partialType.Declarations,
            fact => Assert.Equal(
                DocumentationAuthorityRole.PartialTypePart,
                fact.AuthorityRole));

        var fallback = AssertTarget(
            observations,
            "M:Fixture`1.Fallback(System.String)",
            DocumentationObservationValue.Present);
        Assert.All(
            fallback.Declarations,
            fact => Assert.Equal(
                DocumentationAuthorityRole.PartialMemberDefiningFallback,
                fact.AuthorityRole));
        AssertComponent(
            observations,
            "M:Fixture`1.Fallback(System.String)",
            ComponentKind.Parameter,
            DocumentationObservationValue.Present,
            "defining");

        var exclusive = AssertTarget(
            observations,
            "M:Fixture`1.Exclusive(System.String)",
            DocumentationObservationValue.Absent);
        Assert.All(
            exclusive.Declarations,
            fact => Assert.Equal(
                DocumentationAuthorityRole.PartialMemberImplementing,
                fact.AuthorityRole));
        AssertComponent(
            observations,
            "M:Fixture`1.Exclusive(System.String)",
            ComponentKind.Parameter,
            DocumentationObservationValue.Absent,
            "implementing");

        Assert.All(
            observations.SelectMany(observation => observation.Declarations),
            fact =>
            {
                var sourceIdentity =
                    Assert.IsType<RepositoryDocumentationSourceIdentity>(fact.Source);
                Assert.Equal("src/Fixture.cs", sourceIdentity.Path);
                Assert.False(Path.IsPathRooted(sourceIdentity.Path));
                Assert.Equal(ProjectIdentity, sourceIdentity.ProjectIdentity);
                Assert.Equal(
                    Hash(fact.DeclarationText),
                    fact.DeclarationSha256);
                Assert.Equal(
                    Hash(fact.LeadingTriviaText),
                    fact.LeadingTriviaSha256);
                Assert.Equal(
                    fact.DeclarationText.Length,
                    fact.DeclarationSpan.End - fact.DeclarationSpan.Start);
                Assert.Equal(
                    fact.LeadingTriviaText.Length,
                    fact.LeadingTriviaSpan.End - fact.LeadingTriviaSpan.Start);
                if (fact.DocumentationText is { } documentationText)
                {
                    Assert.Equal(
                        Hash(documentationText),
                        fact.DocumentationSha256);
                    Assert.Equal(
                        documentationText.Length,
                        fact.DocumentationSpan!.Value.End
                            - fact.DocumentationSpan.Value.Start);
                }
            });
    }

    [Fact]
    public void CancellationPublishesNoPartialObservationSet()
    {
        using var session = CreateSession(
            "public class Fixture { public void Run() { } }");
        var classified = new SymbolClassifier().ClassifySession(
            session,
            TargetProfile.ExternalApi);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var outcome = new DocumentationObserver().Observe(
            classified,
            cancellation.Token);

        Assert.Equal(DocumentationObservationRunStatus.Cancelled, outcome.Status);
        Assert.Null(outcome.ObservationSet);
    }

    [Fact]
    public void GeneratedDeclarationsKeepDistinctAcceptedProvenance()
    {
        const string generatorSource = """
            /// <summary>Generator docs.</summary>
            public class GeneratorTarget { }
            """;
        const string toolSource = """
            /// <summary>Tool docs.</summary>
            public class ToolTarget { }
            """;
        var generatorFact = new GeneratedSourceFact(
            ProjectIdentity,
            Context,
            "sgp." + new string('b', 64),
            "sgo." + new string('c', 64),
            Hash(generatorSource),
            generatorSource);
        var toolFact = new GeneratedSourceFact(
            ProjectIdentity,
            Context,
            "tgp." + new string('d', 64),
            "tgo." + new string('e', 64),
            Hash(toolSource),
            toolSource);
        using var session = CreateSession(
            new SourceInput(
                "generator://opaque",
                generatorSource,
                LoadedSourceKind.SourceGenerator,
                generatorFact),
            new SourceInput(
                "tool://opaque",
                toolSource,
                LoadedSourceKind.ToolGenerated,
                toolFact));

        var outcome = new DocumentationObserver().Observe(
            new SymbolClassifier().ClassifySession(
                session,
                TargetProfile.ExternalApi));

        Assert.Equal(DocumentationObservationRunStatus.Success, outcome.Status);
        var observations = outcome.ObservationSet!.Observations;
        var generator = AssertTarget(
            observations,
            "T:GeneratorTarget",
            DocumentationObservationValue.Present);
        var generatorIdentity =
            Assert.IsType<GeneratedDocumentationSourceIdentity>(
                Assert.Single(generator.Declarations).Source);
        Assert.Equal(
            DocumentationSourceKind.SourceGenerator,
            generatorIdentity.Kind);
        Assert.Equal(generatorFact.ProducerId, generatorIdentity.ProducerId);
        Assert.Equal(generatorFact.OutputId, generatorIdentity.OutputId);

        var tool = AssertTarget(
            observations,
            "T:ToolTarget",
            DocumentationObservationValue.Present);
        var toolIdentity =
            Assert.IsType<GeneratedDocumentationSourceIdentity>(
                Assert.Single(tool.Declarations).Source);
        Assert.Equal(DocumentationSourceKind.ToolGenerated, toolIdentity.Kind);
        Assert.Equal(toolFact.ProducerId, toolIdentity.ProducerId);
        Assert.Equal(toolFact.OutputId, toolIdentity.OutputId);
        Assert.NotEqual(generatorIdentity, toolIdentity);
    }

    [Fact]
    public void ParentMatrixTreatsDirectMarkersAsPresenceWithoutRetrieval()
    {
        var source = """
            /// <summary>Line 😀.</summary>
            public class LineDocs { }

            /** <summary>Block.</summary> */
            public class BlockDocs { }

            /// <summary/>
            public class EmptyElement { }

            /// <inheritdoc/>
            public class InheritdocMarker { }

            /// <include file="missing.xml" path="/docs/member"/>
            public class IncludeMarker { }

            /// <see cref="Missing.Type"/>
            public class UnresolvedCref { }

            /// <!DOCTYPE doc [<!ENTITY external SYSTEM "file:///missing.xml">]>
            /// <summary>&external;</summary>
            public class ExternalEntityLikeSyntax { }

            /// <!-- comment only -->
            public class CommentOnly { }

            /// <?direct-only processing?>
            public class ProcessingInstructionOnly { }

            /// <whitespace-only>
            public class WhitespaceOnly { }

            /// <summary>broken
            public class MalformedPayload { }

            public class NoBlock { }
            """.Replace(
                "/// <whitespace-only>",
                "///   ",
                StringComparison.Ordinal);
        using var session = CreateSession(source);

        var outcome = new DocumentationObserver().Observe(
            new SymbolClassifier().ClassifySession(
                session,
                TargetProfile.ExternalApi));

        Assert.Equal(DocumentationObservationRunStatus.Success, outcome.Status);
        var observations = outcome.ObservationSet!.Observations;
        foreach (var documentationId in new[]
        {
            "T:LineDocs",
            "T:BlockDocs",
            "T:EmptyElement",
            "T:InheritdocMarker",
            "T:IncludeMarker",
            "T:UnresolvedCref",
            "T:ExternalEntityLikeSyntax",
            "T:MalformedPayload",
        })
        {
            AssertTarget(
                observations,
                documentationId,
                DocumentationObservationValue.Present);
        }

        foreach (var documentationId in new[]
        {
            "T:CommentOnly",
            "T:ProcessingInstructionOnly",
            "T:WhitespaceOnly",
            "T:NoBlock",
        })
        {
            AssertTarget(
                observations,
                documentationId,
                DocumentationObservationValue.Absent);
        }
    }

    [Fact]
    public void ClosedDocumentationOwnerShapesUseTheirOwningDeclarations()
    {
        var source = """
            /// <summary>Primary constructor.</summary>
            /// <param name="name">Name.</param>
            public class Primary(string name);

            public class Shapes
            {
                /// <summary>Two fields.</summary>
                public int First, Second;

                /// <summary>Field event.</summary>
                public event System.Action? FieldEvent;

                /// <summary>Ordinary event.</summary>
                public event System.Action OrdinaryEvent
                {
                    add { }
                    remove { }
                }

                /// <value>Property value.</value>
                public int Value { get; set; }
            }

            public enum Choices
            {
                /// <summary>Enum member.</summary>
                One,
            }

            /// <typeparam name="T">Type parameter.</typeparam>
            public delegate int Transformer<T>(T value);
            """;
        using var session = CreateSession(source);

        var outcome = new DocumentationObserver().Observe(
            new SymbolClassifier().ClassifySession(
                session,
                TargetProfile.ExternalApi));

        Assert.Equal(DocumentationObservationRunStatus.Success, outcome.Status);
        var observations = outcome.ObservationSet!.Observations;
        foreach (var documentationId in new[]
        {
            "M:Primary.#ctor(System.String)",
            "F:Shapes.First",
            "F:Shapes.Second",
            "E:Shapes.FieldEvent",
            "E:Shapes.OrdinaryEvent",
            "F:Choices.One",
        })
        {
            AssertTarget(
                observations,
                documentationId,
                DocumentationObservationValue.Present);
        }

        AssertComponent(
            observations,
            "M:Primary.#ctor(System.String)",
            ComponentKind.Parameter,
            DocumentationObservationValue.Present,
            "name");
        AssertComponent(
            observations,
            "P:Shapes.Value",
            ComponentKind.Value,
            DocumentationObservationValue.Present,
            null);
        AssertComponent(
            observations,
            "T:Transformer`1",
            ComponentKind.TypeParameter,
            DocumentationObservationValue.Present,
            "T");
    }

    [Fact]
    public void ReorderingSourcesDoesNotChangeNormalizedProjection()
    {
        var first = new SourceInput(
            "src/A.cs",
            "/// <summary>A.</summary>\npublic class A { }",
            LoadedSourceKind.Repository,
            null);
        var second = new SourceInput(
            "src/B.cs",
            "public class B { }",
            LoadedSourceKind.Repository,
            null);
        using var forward = CreateSession(first, second);
        using var reverse = CreateSession(second, first);

        var forwardOutcome = new DocumentationObserver().Observe(
            new SymbolClassifier().ClassifySession(
                forward,
                TargetProfile.ExternalApi));
        var reverseOutcome = new DocumentationObserver().Observe(
            new SymbolClassifier().ClassifySession(
                reverse,
                TargetProfile.ExternalApi));

        Assert.Equal(
            Project(forwardOutcome),
            Project(reverseOutcome));
    }

    [Fact]
    public void ReorderingPartialPartsAndGeneratedResultsDoesNotChangeProjection()
    {
        const string generatedText =
            "/// <summary>Generated.</summary>\npublic class Generated { }";
        var generatedFact = new GeneratedSourceFact(
            ProjectIdentity,
            Context,
            "sgp." + new string('b', 64),
            "sgo." + new string('c', 64),
            Hash(generatedText),
            generatedText);
        var inputs = new[]
        {
            new SourceInput(
                "src/A.cs",
                "/// <summary>First.</summary>\npublic partial class Partial { }",
                LoadedSourceKind.Repository,
                null),
            new SourceInput(
                "src/B.cs",
                "public partial class Partial { }",
                LoadedSourceKind.Repository,
                null),
            new SourceInput(
                "generator://opaque",
                generatedText,
                LoadedSourceKind.SourceGenerator,
                generatedFact),
        };
        using var forward = CreateSession(inputs);
        using var reverse = CreateSession(inputs.Reverse().ToArray());

        var forwardOutcome = new DocumentationObserver().Observe(
            new SymbolClassifier().ClassifySession(
                forward,
                TargetProfile.ExternalApi));
        var reverseOutcome = new DocumentationObserver().Observe(
            new SymbolClassifier().ClassifySession(
                reverse,
                TargetProfile.ExternalApi));

        Assert.Equal(Project(forwardOutcome), Project(reverseOutcome));
    }

    [Fact]
    public void ReorderingProjectsDoesNotChangeNormalizedProjection()
    {
        using var forward = CreateMultiProjectSession(reverse: false);
        using var reverse = CreateMultiProjectSession(reverse: true);

        var forwardOutcome = new DocumentationObserver().Observe(
            new SymbolClassifier().ClassifySession(
                forward,
                TargetProfile.ExternalApi));
        var reverseOutcome = new DocumentationObserver().Observe(
            new SymbolClassifier().ClassifySession(
                reverse,
                TargetProfile.ExternalApi));

        Assert.Equal(Project(forwardOutcome), Project(reverseOutcome));
    }

    [Fact]
    public void PairedPartialPropertyAndEventFollowImplementingAuthority()
    {
        var source = """
            public partial class Paired
            {
                /// <summary>Property definition fallback.</summary>
                public partial int Number { get; }

                /// <summary>Event definition must not win.</summary>
                public partial event System.Action Changed;

                /// <summary>Malformed implementation is exclusive.</summary>
                public partial void Malformed();
            }

            public partial class Paired
            {
                public partial int Number { get => 1; }

                /// <whitespace-only>
                public partial event System.Action Changed
                {
                    add { }
                    remove { }
                }

                /// <summary>broken
                public partial void Malformed() { }
            }
            """.Replace(
                "/// <whitespace-only>",
                "///   ",
                StringComparison.Ordinal);
        using var session = CreateSession(source);

        var outcome = new DocumentationObserver().Observe(
            new SymbolClassifier().ClassifySession(
                session,
                TargetProfile.ExternalApi));

        Assert.Equal(DocumentationObservationRunStatus.Success, outcome.Status);
        var observations = outcome.ObservationSet!.Observations;
        var property = AssertTarget(
            observations,
            "P:Paired.Number",
            DocumentationObservationValue.Present);
        Assert.All(
            property.Declarations,
            declaration => Assert.Equal(
                DocumentationAuthorityRole.PartialMemberDefiningFallback,
                declaration.AuthorityRole));
        var @event = AssertTarget(
            observations,
            "E:Paired.Changed",
            DocumentationObservationValue.Absent);
        Assert.All(
            @event.Declarations,
            declaration => Assert.Equal(
                DocumentationAuthorityRole.PartialMemberImplementing,
                declaration.AuthorityRole));
        var malformed = AssertTarget(
            observations,
            "M:Paired.Malformed",
            DocumentationObservationValue.Present);
        Assert.All(
            malformed.Declarations,
            declaration => Assert.Equal(
                DocumentationAuthorityRole.PartialMemberImplementing,
                declaration.AuthorityRole));
    }

    [Fact]
    public void UnreadableDirectSourceBecomesUnavailableInsteadOfAbsence()
    {
        using var session = CreateSession(
            "public class Fixture { public void Run() { } }");
        var classified = new SymbolClassifier().ClassifySession(
            session,
            TargetProfile.ExternalApi);
        var project = Assert.Single(session.Projects);
        var mutableSources =
            Assert.IsType<Dictionary<SyntaxTree, LoadedSourceTree>>(
                project.SourceTrees);
        mutableSources.Clear();

        var outcome = new DocumentationObserver().Observe(classified);

        Assert.Equal(DocumentationObservationRunStatus.Success, outcome.Status);
        Assert.All(
            outcome.ObservationSet!.Observations,
            observation =>
            {
                Assert.Equal(
                    DocumentationObservationValue.Unavailable,
                    observation.Value);
                Assert.Equal(
                    DocumentationUnavailableCause.SourceUnavailable,
                    observation.UnavailableCause);
                Assert.Empty(observation.Declarations);
            });
    }

    [Fact]
    public void ConflictingGeneratedFactFailsTheRunWithoutPartialObservations()
    {
        const string source = """
            /// <summary>Generated.</summary>
            public class GeneratedTarget { }
            """;
        var conflictingFact = new GeneratedSourceFact(
            ProjectIdentity,
            Context,
            "sgp." + new string('b', 64),
            "sgo." + new string('c', 64),
            new string('f', 64),
            source);
        using var session = CreateSession(new SourceInput(
            "generator://opaque",
            source,
            LoadedSourceKind.SourceGenerator,
            conflictingFact));
        var classified = new SymbolClassifier().ClassifySession(
            session,
            TargetProfile.ExternalApi);

        var outcome = new DocumentationObserver().Observe(classified);

        Assert.Equal(DocumentationObservationRunStatus.Failure, outcome.Status);
        Assert.Null(outcome.ObservationSet);
    }

    [Fact]
    public void ObserverRejectsAnUnsuccessfulClassification()
    {
        using var session = CreateSession(
            "public class Fixture { public void Run() { } }");
        var classified = ClassifiedRepositorySession.Bind(
            session,
            ClassificationOutcome.Failure());

        var outcome = new DocumentationObserver().Observe(classified);

        Assert.Equal(DocumentationObservationRunStatus.Failure, outcome.Status);
        Assert.Null(outcome.ObservationSet);
    }

    [Fact]
    public void ClassifiedSessionBindingCannotBeForgedThroughThePublicApi()
    {
        Assert.Empty(typeof(ClassifiedRepositorySession).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public));
        Assert.Empty(typeof(ObservedRepositorySession).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public));
        var observeMethods = typeof(DocumentationObserver)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.Name == nameof(DocumentationObserver.Observe))
            .ToArray();
        var observe = Assert.Single(observeMethods);
        Assert.Equal(
            typeof(ClassifiedRepositorySession),
            observe.GetParameters()[0].ParameterType);
        Assert.Equal(typeof(ObservedRepositorySession), observe.ReturnType);

        var extractMethods = typeof(PolicyEvidenceExtractor)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.Name == nameof(PolicyEvidenceExtractor.Extract))
            .ToArray();
        var extract = Assert.Single(extractMethods);
        Assert.Equal(
            typeof(ClassifiedRepositorySession),
            extract.GetParameters()[0].ParameterType);
        Assert.Equal(
            typeof(ObservedRepositorySession),
            extract.GetParameters()[1].ParameterType);
    }

    [Fact]
    public async Task RealLoaderSessionFeedsTheBoundObserver()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Root, "App", "App.cs"),
            """
            /// <summary>Loaded directly.</summary>
            public class Fixture { }
            """);
        var loaded = await new RepositoryLoader().LoadAsync(
            new RepositoryLoadRequest(fixture.Root, "App/App.csproj"));
        Assert.Equal(RepositoryLoadStatus.Success, loaded.Status);
        await using var session = Assert.IsType<LoadedRepositorySession>(
            loaded.Session);

        var outcome = new DocumentationObserver().Observe(
            new SymbolClassifier().ClassifySession(
                session,
                TargetProfile.ExternalApi));

        Assert.Equal(
            DocumentationObservationRunStatus.Success,
            outcome.Status);
        var observation = AssertTarget(
            outcome.ObservationSet!.Observations,
            "T:Fixture",
            DocumentationObservationValue.Present);
        var source = Assert.IsType<RepositoryDocumentationSourceIdentity>(
            Assert.Single(observation.Declarations).Source);
        Assert.Equal("App/App.cs", source.Path);
        Assert.False(Path.IsPathRooted(source.Path));
    }

    [Fact]
    public void NonSubstantiveMalformedTargetBlocksAreUnavailable()
    {
        var source = """
            /// <!-- broken
            public class MalformedComment { }

            /// <?broken
            public class MalformedProcessingInstruction { }
            """;
        using var session = CreateSession(source);

        var outcome = new DocumentationObserver().Observe(
            new SymbolClassifier().ClassifySession(
                session,
                TargetProfile.ExternalApi));

        Assert.Equal(DocumentationObservationRunStatus.Success, outcome.Status);
        foreach (var documentationId in new[]
        {
            "T:MalformedComment",
            "T:MalformedProcessingInstruction",
        })
        {
            var observation = AssertTarget(
                outcome.ObservationSet!.Observations,
                documentationId,
                DocumentationObservationValue.Unavailable);
            Assert.Equal(
                DocumentationUnavailableCause.MalformedXml,
                observation.UnavailableCause);
        }
    }

    [Fact]
    public void OneActivePartPartialTypesUsePartialTypeAuthority()
    {
        var source = """
            /// <summary>Class.</summary>
            /// <typeparam name="T">Type parameter.</typeparam>
            /// <param name="value">Value.</param>
            public partial class PartialClass<T>(string value) { }

            #if NEVER
            public partial class PartialClass<T> { }
            #endif

            /// <summary>Struct.</summary>
            public partial struct PartialStruct { }

            /// <summary>Interface.</summary>
            /// <typeparam name="T">Type parameter.</typeparam>
            public partial interface IPartial<T> { }

            /// <summary>Record.</summary>
            /// <param name="value">Value.</param>
            public partial record PartialRecord(string value);
            """;
        using var session = CreateSession(source);

        var outcome = new DocumentationObserver().Observe(
            new SymbolClassifier().ClassifySession(
                session,
                TargetProfile.ExternalApi));

        Assert.Equal(DocumentationObservationRunStatus.Success, outcome.Status);
        var observations = outcome.ObservationSet!.Observations;
        foreach (var documentationId in new[]
        {
            "T:PartialClass`1",
            "T:PartialStruct",
            "T:IPartial`1",
            "T:PartialRecord",
        })
        {
            var observation = AssertTarget(
                observations,
                documentationId,
                DocumentationObservationValue.Present);
            Assert.All(
                observation.Declarations,
                declaration => Assert.Equal(
                    DocumentationAuthorityRole.PartialTypePart,
                    declaration.AuthorityRole));
        }

        AssertComponent(
            observations,
            "T:PartialClass`1",
            ComponentKind.TypeParameter,
            DocumentationObservationValue.Present,
            "T");
        AssertComponent(
            observations,
            "M:PartialClass`1.#ctor(System.String)",
            ComponentKind.Parameter,
            DocumentationObservationValue.Present,
            "value");
        AssertComponent(
            observations,
            "T:IPartial`1",
            ComponentKind.TypeParameter,
            DocumentationObservationValue.Present,
            "T");
        AssertComponent(
            observations,
            "M:PartialRecord.#ctor(System.String)",
            ComponentKind.Parameter,
            DocumentationObservationValue.Present,
            "value");
    }

    [Fact]
    public void ForgedSessionAndSubjectBindingsFailInsteadOfBecomingUnavailable()
    {
        using var first = CreateSession(
            """
            /// <summary>First session.</summary>
            public class Fixture { public void Run(string value) { } }
            """);
        using var second = CreateSession(
            """
            /// <summary>Second session.</summary>
            public class Fixture { public void Run(string value) { } }
            """);
        var firstClassification = new SymbolClassifier().Classify(
            first,
            TargetProfile.ExternalApi);

        var crossSession = new DocumentationObserver().Observe(
            new ClassifiedRepositorySession(second, firstClassification));
        Assert.Equal(
            DocumentationObservationRunStatus.Failure,
            crossSession.Status);
        Assert.Null(crossSession.ObservationSet);

        var missingContextCandidates = new ClassificationCandidateBuffer();
        missingContextCandidates.AddTarget(
            "context." + new string('9', 64),
            "T:Fixture",
            PrimarySymbolKind.Class,
            [],
            ClassificationOrigin.Source,
            [ClassificationInput.RepositoryLocator("src/Fixture.cs")]);
        var missingContextSet = Assert.IsType<ClassificationSet>(
            missingContextCandidates.Normalize(TargetProfile.ExternalApi)
                .ClassificationSet);
        var missingContext = new DocumentationObserver().Observe(
            ClassifiedRepositorySession.Bind(
                second,
                ClassificationOutcome.Success(missingContextSet)));
        Assert.Equal(
            DocumentationObservationRunStatus.Failure,
            missingContext.Status);

        var impossibleComponentCandidates = new ClassificationCandidateBuffer();
        impossibleComponentCandidates.AddTarget(
            Context,
            "M:Fixture.Run(System.String)",
            PrimarySymbolKind.Method,
            [],
            ClassificationOrigin.Source,
            [ClassificationInput.RepositoryLocator("src/Fixture.cs")]);
        impossibleComponentCandidates.AddComponent(
            Context,
            "M:Fixture.Run(System.String)",
            ComponentKind.Parameter,
            "parameter/99",
            ClassificationOrigin.Source);
        var impossibleComponentSet = Assert.IsType<ClassificationSet>(
            impossibleComponentCandidates.Normalize(TargetProfile.ExternalApi)
                .ClassificationSet);
        var impossibleComponent = new DocumentationObserver().Observe(
            ClassifiedRepositorySession.Bind(
                second,
                ClassificationOutcome.Success(impossibleComponentSet)));
        Assert.Equal(
            DocumentationObservationRunStatus.Failure,
            impossibleComponent.Status);
    }

    [Fact]
    public void NonUniqueAndUnsupportedSymbolBindingsFailTheRun()
    {
        using var session = CreateSession(
            "public class First { } public class Second { }");
        var classified = new SymbolClassifier().ClassifySession(
            session,
            TargetProfile.ExternalApi);
        var compilation = Assert.Single(session.Projects).Compilation;
        var first = Assert.Single(
            DocumentationCommentId.GetSymbolsForDeclarationId(
                "T:First",
                compilation));
        var second = Assert.Single(
            DocumentationCommentId.GetSymbolsForDeclarationId(
                "T:Second",
                compilation));

        var nonUnique = new DocumentationObserver(
            (_, _) => [first, second],
            null).Observe(classified);
        Assert.Equal(
            DocumentationObservationRunStatus.Failure,
            nonUnique.Status);

        var unsupportedOwner = new DocumentationObserver(
            (_, _) => [compilation.Assembly.GlobalNamespace],
            null).Observe(classified);
        Assert.Equal(
            DocumentationObservationRunStatus.Failure,
            unsupportedOwner.Status);

        var unrelatedCancellation = new DocumentationObserver(
            (_, _) => throw new OperationCanceledException(),
            null).Observe(classified);
        Assert.Equal(
            DocumentationObservationRunStatus.Failure,
            unrelatedCancellation.Status);
    }

    [Fact]
    public void CancellationAtEveryObserverStagePublishesNoSuccess()
    {
        foreach (var stage in Enum.GetValues<DocumentationObservationStage>())
        {
            using var session = CreateSession(
                "/// <summary>Docs.</summary>\npublic class Fixture { }");
            var classified = new SymbolClassifier().ClassifySession(
                session,
                TargetProfile.ExternalApi);
            using var cancellation = new CancellationTokenSource();
            var observer = new DocumentationObserver(
                null,
                current =>
                {
                    if (current == stage)
                    {
                        cancellation.Cancel();
                    }
                });

            var outcome = observer.Observe(classified, cancellation.Token);

            Assert.Equal(
                DocumentationObservationRunStatus.Cancelled,
                outcome.Status);
            Assert.Null(outcome.ObservationSet);
        }
    }

    [Fact]
    public void CancellationAtEveryCoreNormalizationStagePublishesNoSuccess()
    {
        using var session = CreateSession(
            "/// <summary>Docs.</summary>\npublic class Fixture { }");
        var classifications = Assert.IsType<ClassificationSet>(
            new SymbolClassifier().Classify(
                session,
                TargetProfile.ExternalApi).ClassificationSet);
        var target = Assert.Single(
            classifications.Targets,
            candidate => candidate.SymbolRef.DocumentationCommentId == "T:Fixture");
        const string documentationText = "/// <summary>Docs.</summary>\n";
        const string bodyText = "public class Fixture { }";
        var declarationText = documentationText + bodyText;
        var declaration = DocumentationObservationInput.RepositoryDeclaration(
            "decl." + new string('d', 64),
            DocumentationAuthorityRole.Ordinary,
            ProjectIdentity,
            "src/Fixture.cs",
            Sha,
            DocumentationObservationInput.Span(0, declarationText.Length),
            declarationText,
            DocumentationObservationInput.Span(0, documentationText.Length),
            documentationText,
            DocumentationObservationInput.Span(0, documentationText.Length),
            documentationText,
            DocumentationBlockState.WellFormed,
            true);

        foreach (var stage in Enum
            .GetValues<DocumentationObservationNormalizationStage>())
        {
            using var cancellation = new CancellationTokenSource();
            var buffer = new DocumentationObservationCandidateBuffer(
                classifications,
                current =>
                {
                    if (current == stage)
                    {
                        cancellation.Cancel();
                    }
                });
            buffer.AddTarget(target, true, [declaration]);

            var outcome = buffer.Normalize(cancellationToken: cancellation.Token);

            Assert.Equal(
                DocumentationObservationRunStatus.Cancelled,
                outcome.Status);
            Assert.Null(outcome.ObservationSet);
        }
    }

    [Fact]
    public void EveryComponentFamilyCoversPositiveNegativeAndMalformedProductionCases()
    {
        var source = """
            public class Components
            {
                /// <param name="value">Documented.</param>
                public void ParameterPositive(string value) { }

                /// <summary>No parameter tag.</summary>
                public void ParameterNegative(string value) { }

                /// <param name="value">broken
                public void ParameterMalformed(string value) { }

                /// <inheritdoc/>
                public void ParameterInheritdocOnly(string value) { }

                /// <x:param xmlns:x="urn:custom" name="value">Custom.</x:param>
                public void ParameterQualifiedElement(string value) { }

                /// <param xmlns:x="urn:custom" x:name="value">Custom.</param>
                public void ParameterQualifiedName(string value) { }

                /// <typeparam name="T">Documented.</typeparam>
                public void TypeParameterPositive<T>() { }

                /// <summary>No type parameter tag.</summary>
                public void TypeParameterNegative<T>() { }

                /// <typeparam name="T">broken
                public void TypeParameterMalformed<T>() { }

                /// <x:typeparam xmlns:x="urn:custom" name="T">Custom.</x:typeparam>
                public void TypeParameterQualifiedElement<T>() { }

                /// <typeparam xmlns:x="urn:custom" x:name="T">Custom.</typeparam>
                public void TypeParameterQualifiedName<T>() { }

                /// <returns>Documented.</returns>
                public int ReturnPositive() => 1;

                /// <summary>No returns tag.</summary>
                public int ReturnNegative() => 1;

                /// <returns>broken
                public int ReturnMalformed() => 1;

                /// <x:returns xmlns:x="urn:custom">Custom.</x:returns>
                public int ReturnQualified() => 1;

                /// <value>Documented.</value>
                public int ValuePositive { get; set; }

                /// <summary>No value tag.</summary>
                public int ValueNegative { get; set; }

                /// <value>broken
                public int ValueMalformed { get; set; }

                /// <x:value xmlns:x="urn:custom">Custom.</x:value>
                public int ValueQualified { get; set; }
            }
            """;
        using var session = CreateSession(source);

        var outcome = new DocumentationObserver().Observe(
            new SymbolClassifier().ClassifySession(
                session,
                TargetProfile.ExternalApi));

        Assert.Equal(DocumentationObservationRunStatus.Success, outcome.Status);
        var observations = outcome.ObservationSet!.Observations;
        foreach (var vector in new[]
        {
            ("M:Components.ParameterPositive(System.String)",
                ComponentKind.Parameter, DocumentationObservationValue.Present, "value"),
            ("M:Components.ParameterNegative(System.String)",
                ComponentKind.Parameter, DocumentationObservationValue.Absent, "value"),
            ("M:Components.ParameterMalformed(System.String)",
                ComponentKind.Parameter, DocumentationObservationValue.Unavailable, "value"),
            ("M:Components.ParameterInheritdocOnly(System.String)",
                ComponentKind.Parameter, DocumentationObservationValue.Absent, "value"),
            ("M:Components.ParameterQualifiedElement(System.String)",
                ComponentKind.Parameter, DocumentationObservationValue.Absent, "value"),
            ("M:Components.ParameterQualifiedName(System.String)",
                ComponentKind.Parameter, DocumentationObservationValue.Absent, "value"),
            ("M:Components.TypeParameterPositive``1",
                ComponentKind.TypeParameter, DocumentationObservationValue.Present, "T"),
            ("M:Components.TypeParameterNegative``1",
                ComponentKind.TypeParameter, DocumentationObservationValue.Absent, "T"),
            ("M:Components.TypeParameterMalformed``1",
                ComponentKind.TypeParameter, DocumentationObservationValue.Unavailable, "T"),
            ("M:Components.TypeParameterQualifiedElement``1",
                ComponentKind.TypeParameter, DocumentationObservationValue.Absent, "T"),
            ("M:Components.TypeParameterQualifiedName``1",
                ComponentKind.TypeParameter, DocumentationObservationValue.Absent, "T"),
            ("M:Components.ReturnPositive",
                ComponentKind.Return, DocumentationObservationValue.Present, (string?)null),
            ("M:Components.ReturnNegative",
                ComponentKind.Return, DocumentationObservationValue.Absent, (string?)null),
            ("M:Components.ReturnMalformed",
                ComponentKind.Return, DocumentationObservationValue.Unavailable, (string?)null),
            ("M:Components.ReturnQualified",
                ComponentKind.Return, DocumentationObservationValue.Absent, (string?)null),
            ("P:Components.ValuePositive",
                ComponentKind.Value, DocumentationObservationValue.Present, (string?)null),
            ("P:Components.ValueNegative",
                ComponentKind.Value, DocumentationObservationValue.Absent, (string?)null),
            ("P:Components.ValueMalformed",
                ComponentKind.Value, DocumentationObservationValue.Unavailable, (string?)null),
            ("P:Components.ValueQualified",
                ComponentKind.Value, DocumentationObservationValue.Absent, (string?)null),
        })
        {
            AssertComponent(
                observations,
                vector.Item1,
                vector.Item2,
                vector.Item3,
                vector.Item4);
        }
    }

    [Fact]
    public void GeneratedAbsenceAndUnavailableUseTheirOwnGeneratedSources()
    {
        const string generatorSource = "public class GeneratorAbsent { }";
        const string toolSource = "public class ToolAbsent { }";
        var generatorFact = new GeneratedSourceFact(
            ProjectIdentity,
            Context,
            "sgp." + new string('b', 64),
            "sgo." + new string('c', 64),
            Hash(generatorSource),
            generatorSource);
        var toolFact = new GeneratedSourceFact(
            ProjectIdentity,
            Context,
            "tgp." + new string('d', 64),
            "tgo." + new string('e', 64),
            Hash(toolSource),
            toolSource);
        using var session = CreateSession(
            new SourceInput(
                "generator://opaque",
                generatorSource,
                LoadedSourceKind.SourceGenerator,
                generatorFact),
            new SourceInput(
                "tool://opaque",
                toolSource,
                LoadedSourceKind.ToolGenerated,
                toolFact));
        var classified = new SymbolClassifier().ClassifySession(
            session,
            TargetProfile.ExternalApi);

        var complete = new DocumentationObserver().Observe(classified);
        Assert.Equal(DocumentationObservationRunStatus.Success, complete.Status);
        AssertTarget(
            complete.ObservationSet!.Observations,
            "T:GeneratorAbsent",
            DocumentationObservationValue.Absent);
        AssertTarget(
            complete.ObservationSet.Observations,
            "T:ToolAbsent",
            DocumentationObservationValue.Absent);

        var project = Assert.Single(session.Projects);
        var mutableSources =
            Assert.IsType<Dictionary<SyntaxTree, LoadedSourceTree>>(
                project.SourceTrees);
        var generatorTree = Assert.Single(
            mutableSources,
            pair => pair.Value.Kind == LoadedSourceKind.SourceGenerator).Key;
        mutableSources.Remove(generatorTree);

        var incomplete = new DocumentationObserver().Observe(classified);
        Assert.Equal(DocumentationObservationRunStatus.Success, incomplete.Status);
        var unavailable = AssertTarget(
            incomplete.ObservationSet!.Observations,
            "T:GeneratorAbsent",
            DocumentationObservationValue.Unavailable);
        Assert.Equal(
            DocumentationUnavailableCause.SourceUnavailable,
            unavailable.UnavailableCause);
        AssertTarget(
            incomplete.ObservationSet.Observations,
            "T:ToolAbsent",
            DocumentationObservationValue.Absent);
    }

    [Fact]
    public void ToolGeneratedComponentsCoverPresentAbsentAndUnavailable()
    {
        const string presentSource = """
            public class ToolPresent
            {
                /// <param name="value">Direct tool documentation.</param>
                public void Run(string value) { }
            }
            """;
        const string absentSource = """
            public class ToolAbsentComponent
            {
                /// <summary>No component tag.</summary>
                public void Run(string value) { }
            }
            """;
        const string unavailableSource = """
            public class ToolUnavailable
            {
                /// <param name="value">Unavailable binding.</param>
                public void Run(string value) { }
            }
            """;
        var presentFact = ToolGeneratedFact(presentSource, 'b', 'c');
        var absentFact = ToolGeneratedFact(absentSource, 'd', 'e');
        var unavailableFact = ToolGeneratedFact(unavailableSource, 'f', '1');
        using var session = CreateSession(
            new SourceInput(
                "tool://present",
                presentSource,
                LoadedSourceKind.ToolGenerated,
                presentFact),
            new SourceInput(
                "tool://absent",
                absentSource,
                LoadedSourceKind.ToolGenerated,
                absentFact),
            new SourceInput(
                "tool://unavailable",
                unavailableSource,
                LoadedSourceKind.ToolGenerated,
                unavailableFact));
        var classified = new SymbolClassifier().ClassifySession(
            session,
            TargetProfile.ExternalApi);
        RemoveSourceBinding(session, "tool://unavailable");

        var outcome = new DocumentationObserver().Observe(classified);

        Assert.Equal(DocumentationObservationRunStatus.Success, outcome.Status);
        var observations = outcome.ObservationSet!.Observations;
        var present = AssertComponent(
            observations,
            "M:ToolPresent.Run(System.String)",
            ComponentKind.Parameter,
            DocumentationObservationValue.Present,
            "value");
        var absent = AssertComponent(
            observations,
            "M:ToolAbsentComponent.Run(System.String)",
            ComponentKind.Parameter,
            DocumentationObservationValue.Absent,
            "value");
        var unavailable = AssertComponent(
            observations,
            "M:ToolUnavailable.Run(System.String)",
            ComponentKind.Parameter,
            DocumentationObservationValue.Unavailable,
            "value");
        Assert.All(
            present.Declarations.Concat(absent.Declarations),
            declaration => Assert.Equal(
                DocumentationSourceKind.ToolGenerated,
                declaration.Source.Kind));
        Assert.Empty(unavailable.Declarations);
        Assert.Equal(
            DocumentationUnavailableCause.SourceUnavailable,
            unavailable.UnavailableCause);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DependencyAndMetadataEndpointDocumentationDoesNotPromote(
        bool includeDependencyProject)
    {
        using var baselineSession = CreateReferencedRelationSession(
            endpointDocumentation: false,
            includeDependencyProject);
        using var documentedSession = CreateReferencedRelationSession(
            endpointDocumentation: true,
            includeDependencyProject);
        var baselineClassification = new SymbolClassifier().ClassifySession(
            baselineSession,
            TargetProfile.ExternalApi);
        var documentedClassification = new SymbolClassifier().ClassifySession(
            documentedSession,
            TargetProfile.ExternalApi);
        var documentedSet = Assert.IsType<ClassificationSet>(
            documentedClassification.Classification.ClassificationSet);
        Assert.Contains(
            documentedSet.Relations,
            relation => relation.RelationKind == RelationKind.Overrides
                && relation.SourceSymbolRef.DocumentationCommentId
                    == "M:Direct.Read(System.Int32)"
                && relation.TargetSymbolRef.DocumentationCommentId
                    == "M:EndpointBase.Read(System.Int32)");
        Assert.Contains(
            documentedSet.Relations,
            relation => relation.RelationKind
                    == RelationKind.ImplicitInterfaceImplementation
                && relation.SourceSymbolRef.DocumentationCommentId
                    == "M:Direct.Read(System.Int32)"
                && relation.TargetSymbolRef.DocumentationCommentId
                    == "M:IEndpoint.Read(System.Int32)");
        var rootProject = Assert.Single(
            documentedSession.Projects,
            project => project.Role == LoadedProjectRole.AuditRoot);
        var endpoint = Assert.Single(
            rootProject.Compilation.GetTypeByMetadataName("EndpointBase")!
                .GetMembers("Read"));
        if (includeDependencyProject)
        {
            Assert.NotEmpty(endpoint.DeclaringSyntaxReferences);
        }
        else
        {
            Assert.Empty(endpoint.DeclaringSyntaxReferences);
        }

        Assert.Contains(
            "Endpoint documentation.",
            endpoint.GetDocumentationCommentXml(),
            StringComparison.Ordinal);

        var baseline = new DocumentationObserver().Observe(baselineClassification);
        var documented = new DocumentationObserver().Observe(documentedClassification);

        Assert.Equal(
            ProjectSubjects(baseline, "M:Direct.Read(System.Int32)"),
            ProjectSubjects(documented, "M:Direct.Read(System.Int32)"));
        AssertTarget(
            documented.ObservationSet!.Observations,
            "M:Direct.Read(System.Int32)",
            DocumentationObservationValue.Absent);
        Assert.Equal(
            includeDependencyProject ? 1 : 0,
            documentedSession.Projects.Count(
                project => project.Role == LoadedProjectRole.DependencyOnly));
    }

    [Fact]
    public void GeneratedRelationEndpointDocumentationDoesNotPromote()
    {
        using var baselineSession = CreateGeneratedRelationSession(
            endpointDocumentation: false);
        using var documentedSession = CreateGeneratedRelationSession(
            endpointDocumentation: true);
        var baselineClassification = new SymbolClassifier().ClassifySession(
            baselineSession,
            TargetProfile.ExternalApi);
        var documentedClassification = new SymbolClassifier().ClassifySession(
            documentedSession,
            TargetProfile.ExternalApi);
        var documentedSet = Assert.IsType<ClassificationSet>(
            documentedClassification.Classification.ClassificationSet);
        Assert.Contains(
            documentedSet.Targets,
            target => target.SymbolRef.DocumentationCommentId
                    == "T:GeneratedBase"
                && target.Origin == ClassificationOrigin.ToolGenerated);
        Assert.Contains(
            documentedSet.Relations,
            relation => relation.RelationKind == RelationKind.Overrides
                && relation.SourceSymbolRef.DocumentationCommentId
                    == "M:DirectGenerated.Read(System.Int32)"
                && relation.TargetSymbolRef.DocumentationCommentId
                    == "M:GeneratedBase.Read(System.Int32)");

        var baseline = new DocumentationObserver().Observe(baselineClassification);
        var documented = new DocumentationObserver().Observe(documentedClassification);

        Assert.Equal(
            ProjectSubjects(baseline, "M:DirectGenerated.Read(System.Int32)"),
            ProjectSubjects(documented, "M:DirectGenerated.Read(System.Int32)"));
        AssertTarget(
            documented.ObservationSet!.Observations,
            "M:DirectGenerated.Read(System.Int32)",
            DocumentationObservationValue.Absent);
    }

    [Fact]
    public void AmbiguousAndUnavailableRelationsDoNotChangeDirectObservations()
    {
        const string source = """
            public interface IBase
            {
                /// <summary>Interface endpoint.</summary>
                void Implemented();

                /// <summary>Explicit endpoint.</summary>
                void Explicit();

                /// <summary>Inherited endpoint.</summary>
                void Inherited();
            }

            public interface IDerived : IBase { }

            public class Base
            {
                /// <summary>Base endpoint.</summary>
                public virtual void Overridden() { }
            }

            public class Direct : Base, IBase
            {
                public override void Overridden() { }
                public void Implemented() { }
                public void Inherited() { }
                void IBase.Explicit() { }
            }
            """;
        using var session = CreateSession(source);
        var baselineClassification = new SymbolClassifier().ClassifySession(
            session,
            TargetProfile.ExternalApi);
        var baselineSet = Assert.IsType<ClassificationSet>(
            baselineClassification.Classification.ClassificationSet);
        Assert.All(
            Enum.GetValues<RelationKind>(),
            kind => Assert.Contains(
                baselineSet.Relations,
                relation => relation.RelationKind == kind));
        var baseline = new DocumentationObserver().Observe(baselineClassification);
        var baselineProjection = Project(baseline);

        foreach (var blockedKind in Enum.GetValues<RelationKind>())
        {
            foreach (var blockedStatus in new[]
            {
                RelationEndpointStatus.Ambiguous,
                RelationEndpointStatus.Unavailable,
            })
            {
                var classifier = new SymbolClassifier(
                    null,
                    (kind, symbol, isTarget, context) =>
                        kind == blockedKind && isTarget
                            ? new RelationEndpointResolution(
                                blockedStatus,
                                null,
                                null)
                            : new RelationEndpointResolution(
                                RelationEndpointStatus.Available,
                                context,
                                symbol.GetDocumentationCommentId()!),
                    null,
                    null);
                var classified = classifier.ClassifySession(
                    session,
                    TargetProfile.ExternalApi);
                var set = Assert.IsType<ClassificationSet>(
                    classified.Classification.ClassificationSet);
                Assert.DoesNotContain(
                    set.Relations,
                    relation => relation.RelationKind == blockedKind);

                var outcome = new DocumentationObserver().Observe(classified);

                Assert.Equal(baselineProjection, Project(outcome));
            }
        }
    }

    [Fact]
    public void PartialUnreadabilityKeepsPositivePrecedenceAndRejectsFallback()
    {
        using (var positiveSession = CreateSession(
            new SourceInput(
                "src/Positive.cs",
                "/// <summary>Readable.</summary>\npublic partial class Partial { }",
                LoadedSourceKind.Repository,
                null),
            new SourceInput(
                "src/Unreadable.cs",
                "public partial class Partial { }",
                LoadedSourceKind.Repository,
                null)))
        {
            var classified = new SymbolClassifier().ClassifySession(
                positiveSession,
                TargetProfile.ExternalApi);
            RemoveSourceBinding(positiveSession, "src/Unreadable.cs");

            var outcome = new DocumentationObserver().Observe(classified);

            Assert.Equal(DocumentationObservationRunStatus.Success, outcome.Status);
            var observation = AssertTarget(
                outcome.ObservationSet!.Observations,
                "T:Partial",
                DocumentationObservationValue.Present);
            Assert.Equal(
                DocumentationAuthorityCompleteness.PositiveOnly,
                observation.Completeness);
        }

        using (var negativeSession = CreateSession(
            new SourceInput(
                "src/Readable.cs",
                "public partial class Partial { }",
                LoadedSourceKind.Repository,
                null),
            new SourceInput(
                "src/Unreadable.cs",
                "public partial class Partial { }",
                LoadedSourceKind.Repository,
                null)))
        {
            var classified = new SymbolClassifier().ClassifySession(
                negativeSession,
                TargetProfile.ExternalApi);
            RemoveSourceBinding(negativeSession, "src/Unreadable.cs");

            var outcome = new DocumentationObserver().Observe(classified);

            Assert.Equal(DocumentationObservationRunStatus.Success, outcome.Status);
            AssertTarget(
                outcome.ObservationSet!.Observations,
                "T:Partial",
                DocumentationObservationValue.Unavailable);
        }

        using (var malformedSession = CreateSession(
            new SourceInput(
                "src/Readable.cs",
                """
                /// <typeparam name="T">broken
                public partial class Partial<T> { }
                """,
                LoadedSourceKind.Repository,
                null),
            new SourceInput(
                "src/Unreadable.cs",
                "public partial class Partial<T> { }",
                LoadedSourceKind.Repository,
                null)))
        {
            var classified = new SymbolClassifier().ClassifySession(
                malformedSession,
                TargetProfile.ExternalApi);
            RemoveSourceBinding(malformedSession, "src/Unreadable.cs");

            var outcome = new DocumentationObserver().Observe(classified);

            Assert.Equal(DocumentationObservationRunStatus.Success, outcome.Status);
            var component = AssertComponent(
                outcome.ObservationSet!.Observations,
                "T:Partial`1",
                ComponentKind.TypeParameter,
                DocumentationObservationValue.Unavailable,
                "T");
            Assert.Equal(
                DocumentationUnavailableCause.SourceUnavailable,
                component.UnavailableCause);
        }

        using var partialMemberSession = CreateSession(
            new SourceInput(
                "src/Definition.cs",
                """
                public partial class Partial
                {
                    /// <summary>Fallback must not be used.</summary>
                    public partial void Run();
                }
                """,
                LoadedSourceKind.Repository,
                null),
            new SourceInput(
                "src/Implementation.cs",
                """
                public partial class Partial
                {
                    public partial void Run() { }
                }
                """,
                LoadedSourceKind.Repository,
                null));
        var partialMemberClassification = new SymbolClassifier().ClassifySession(
            partialMemberSession,
            TargetProfile.ExternalApi);
        RemoveSourceBinding(partialMemberSession, "src/Implementation.cs");

        var partialMemberOutcome = new DocumentationObserver().Observe(
            partialMemberClassification);

        Assert.Equal(
            DocumentationObservationRunStatus.Success,
            partialMemberOutcome.Status);
        var partialMember = AssertTarget(
            partialMemberOutcome.ObservationSet!.Observations,
            "M:Partial.Run",
            DocumentationObservationValue.Unavailable);
        Assert.Empty(partialMember.Declarations);
    }

    private static DocumentationObservation AssertTarget(
        IEnumerable<DocumentationObservation> observations,
        string documentationId,
        DocumentationObservationValue value)
    {
        var observation = Assert.Single(observations, candidate =>
            candidate.Subject.ComponentKind is null
            && candidate.Subject.ParentSymbolRef.DocumentationCommentId
                == documentationId);
        Assert.Equal(value, observation.Value);
        return observation;
    }

    private static DocumentationObservation AssertComponent(
        IEnumerable<DocumentationObservation> observations,
        string documentationId,
        ComponentKind kind,
        DocumentationObservationValue value,
        string? localName)
    {
        var observation = Assert.Single(observations, candidate =>
            candidate.Subject.ComponentKind == kind
            && candidate.Subject.ParentSymbolRef.DocumentationCommentId
                == documentationId);
        Assert.Equal(value, observation.Value);
        Assert.All(
            observation.Declarations,
            fact => Assert.Equal(localName, fact.ComponentLocalName));
        return observation;
    }

    private static LoadedRepositorySession CreateSession(string source)
        => CreateSession(new SourceInput(
            "src/Fixture.cs",
            source,
            LoadedSourceKind.Repository,
            null));

    private static LoadedRepositorySession CreateSession(
        params SourceInput[] sources)
    {
        var parseOptions = new CSharpParseOptions(
            LanguageVersion.Preview,
            documentationMode: DocumentationMode.Diagnose);
        var syntaxTrees = sources.Select(source => CSharpSyntaxTree.ParseText(
                source.Text,
                parseOptions,
                source.Path,
                Encoding.UTF8))
            .ToArray();
        var compilation = CSharpCompilation.Create(
            "Fixture",
            syntaxTrees,
            PlatformReferences(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                deterministic: true));
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("Fixture", LanguageNames.CSharp);
        var bindings = new Dictionary<SyntaxTree, LoadedSourceTree>(
            ReferenceEqualityComparer.Instance);
        foreach (var pair in syntaxTrees.Zip(sources))
        {
            bindings.Add(
                pair.First,
                new LoadedSourceTree(
                    pair.Second.Kind,
                    pair.Second.Kind == LoadedSourceKind.Repository
                        ? pair.Second.Path
                        : null,
                    pair.Second.GeneratedSource));
        }

        var loadedProject = new LoadedProject(
            ProjectIdentity,
            "net10.0",
            Context,
            LoadedProjectRole.AuditRoot,
            [],
            project,
            compilation,
            bindings);
        Assert.True(RepositoryContextRef.TryParse(
            "repoctx-00000000000000000000000000000000",
            out var repositoryContextRef));
        return new LoadedRepositorySession(
            repositoryContextRef,
            Path.GetFullPath("."),
            ProjectIdentity,
            new ToolchainIdentity("test", "test", "test", "test"),
            [loadedProject],
            sources
                .Select(source => source.GeneratedSource)
                .Where(fact => fact is not null)
                .Cast<GeneratedSourceFact>()
                .ToArray(),
            workspace);
    }

    private static LoadedRepositorySession CreateReferencedRelationSession(
        bool endpointDocumentation,
        bool includeDependencyProject)
    {
        var endpointPrefix = endpointDocumentation
            ? """
                /// <summary>Endpoint documentation.</summary>
                /// <param name="value">Endpoint parameter.</param>
                /// <returns>Endpoint return.</returns>
                """
            : string.Empty;
        var endpointSource = $$"""
            public interface IEndpoint
            {
                {{endpointPrefix}}
                int Read(int value);
            }

            public class EndpointBase
            {
                {{endpointPrefix}}
                public virtual int Read(int value) => value;
            }
            """;
        const string directSource = """
            public class Direct : EndpointBase, IEndpoint
            {
                public override int Read(int value) => value;
            }
            """;
        var parseOptions = new CSharpParseOptions(
            LanguageVersion.Preview,
            documentationMode: DocumentationMode.Diagnose);
        var endpointTree = CSharpSyntaxTree.ParseText(
            endpointSource,
            parseOptions,
            "dependency/Endpoint.cs",
            Encoding.UTF8);
        var endpointCompilation = CSharpCompilation.Create(
            "Endpoint",
            [endpointTree],
            PlatformReferences(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                deterministic: true));
        MetadataReference endpointReference;
        if (includeDependencyProject)
        {
            endpointReference = endpointCompilation.ToMetadataReference();
        }
        else
        {
            using var image = new MemoryStream();
            var emit = endpointCompilation.Emit(image);
            Assert.True(
                emit.Success,
                string.Join(
                    Environment.NewLine,
                    emit.Diagnostics.Select(diagnostic => diagnostic.ToString())));
            endpointReference = MetadataReference.CreateFromImage(
                image.ToArray(),
                documentation: new EndpointDocumentationProvider(
                    endpointDocumentation));
        }

        var directTree = CSharpSyntaxTree.ParseText(
            directSource,
            parseOptions,
            "src/Direct.cs",
            Encoding.UTF8);
        var directCompilation = CSharpCompilation.Create(
            "Direct",
            [directTree],
            PlatformReferences().Concat(
                [endpointReference]),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                deterministic: true));
        AssertNoCompilationErrors(endpointCompilation);
        AssertNoCompilationErrors(directCompilation);
        var workspace = new AdhocWorkspace();
        var directProject = workspace.AddProject("Direct", LanguageNames.CSharp);
        var dependencyIdentity = "project." + new string('2', 64);
        var projects = new List<LoadedProject>
        {
            new(
                ProjectIdentity,
                "net10.0",
                Context,
                LoadedProjectRole.AuditRoot,
                includeDependencyProject ? [dependencyIdentity] : [],
                directProject,
                directCompilation,
                new Dictionary<SyntaxTree, LoadedSourceTree>(
                    ReferenceEqualityComparer.Instance)
                {
                    [directTree] = new(
                        LoadedSourceKind.Repository,
                        "src/Direct.cs",
                        null),
                }),
        };
        if (includeDependencyProject)
        {
            projects.Add(new LoadedProject(
                dependencyIdentity,
                "net10.0",
                "context." + new string('3', 64),
                LoadedProjectRole.DependencyOnly,
                [],
                workspace.AddProject("Endpoint", LanguageNames.CSharp),
                endpointCompilation,
                new Dictionary<SyntaxTree, LoadedSourceTree>(
                    ReferenceEqualityComparer.Instance)
                {
                    [endpointTree] = new(
                        LoadedSourceKind.Repository,
                        "dependency/Endpoint.cs",
                        null),
                }));
        }

        Assert.True(RepositoryContextRef.TryParse(
            "repoctx-00000000000000000000000000000000",
            out var repositoryContextRef));
        return new LoadedRepositorySession(
            repositoryContextRef,
            Path.GetFullPath("."),
            ProjectIdentity,
            new ToolchainIdentity("test", "test", "test", "test"),
            projects,
            [],
            workspace);
    }

    private static LoadedRepositorySession CreateGeneratedRelationSession(
        bool endpointDocumentation)
    {
        var documentation = endpointDocumentation
            ? """
                /// <summary>Generated endpoint documentation.</summary>
                /// <param name="value">Generated parameter.</param>
                /// <returns>Generated return.</returns>
                """
            : string.Empty;
        var generatedSource = $$"""
            public interface IGenerated
            {
                {{documentation}}
                int Read(int value);
            }

            public class GeneratedBase
            {
                {{documentation}}
                public virtual int Read(int value) => value;
            }
            """;
        const string directSource = """
            public class DirectGenerated : GeneratedBase, IGenerated
            {
                public override int Read(int value) => value;
            }
            """;
        var fact = ToolGeneratedFact(generatedSource, '4', '5');
        return CreateSession(
            new SourceInput(
                "src/DirectGenerated.cs",
                directSource,
                LoadedSourceKind.Repository,
                null),
            new SourceInput(
                "tool://relation-endpoints",
                generatedSource,
                LoadedSourceKind.ToolGenerated,
                fact));
    }

    private static LoadedRepositorySession CreateMultiProjectSession(bool reverse)
    {
        var workspace = new AdhocWorkspace();
        var projects = new List<LoadedProject>();
        foreach (var descriptor in new[]
        {
            (
                Name: "Alpha",
                Context: "context." + new string('1', 64),
                Project: "project." + new string('2', 64),
                Path: "src/Alpha.cs",
                Text: "/// <summary>Alpha.</summary>\npublic class Alpha { }"),
            (
                Name: "Beta",
                Context: "context." + new string('3', 64),
                Project: "project." + new string('4', 64),
                Path: "src/Beta.cs",
                Text: "public class Beta { }"),
        })
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(
                descriptor.Text,
                new CSharpParseOptions(
                    LanguageVersion.Preview,
                    documentationMode: DocumentationMode.Diagnose),
                descriptor.Path,
                Encoding.UTF8);
            var compilation = CSharpCompilation.Create(
                descriptor.Name,
                [syntaxTree],
                PlatformReferences(),
                new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    deterministic: true));
            var project = workspace.AddProject(
                descriptor.Name,
                LanguageNames.CSharp);
            projects.Add(new LoadedProject(
                descriptor.Project,
                "net10.0",
                descriptor.Context,
                LoadedProjectRole.AuditRoot,
                [],
                project,
                compilation,
                new Dictionary<SyntaxTree, LoadedSourceTree>(
                    ReferenceEqualityComparer.Instance)
                {
                    [syntaxTree] = new(
                        LoadedSourceKind.Repository,
                        descriptor.Path,
                        null),
                }));
        }

        if (reverse)
        {
            projects.Reverse();
        }

        Assert.True(RepositoryContextRef.TryParse(
            "repoctx-00000000000000000000000000000000",
            out var repositoryContextRef));
        return new LoadedRepositorySession(
            repositoryContextRef,
            Path.GetFullPath("."),
            ProjectIdentity,
            new ToolchainIdentity("test", "test", "test", "test"),
            projects,
            [],
            workspace);
    }

    private static void RemoveSourceBinding(
        LoadedRepositorySession session,
        string filePath)
    {
        var project = Assert.Single(session.Projects);
        var mutableSources =
            Assert.IsType<Dictionary<SyntaxTree, LoadedSourceTree>>(
                project.SourceTrees);
        var syntaxTree = Assert.Single(
            mutableSources.Keys,
            tree => string.Equals(
                tree.FilePath,
                filePath,
                StringComparison.Ordinal));
        mutableSources.Remove(syntaxTree);
    }

    private static GeneratedSourceFact ToolGeneratedFact(
        string source,
        char producerHash,
        char outputHash) =>
        new(
            ProjectIdentity,
            Context,
            "tgp." + new string(producerHash, 64),
            "tgo." + new string(outputHash, 64),
            Hash(source),
            source);

    private static void AssertNoCompilationErrors(Compilation compilation)
    {
        var errors = compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Assert.True(
            errors.Length == 0,
            string.Join(Environment.NewLine, errors.Select(error => error.ToString())));
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false, true)
            .GetBytes(value)))
            .ToLowerInvariant();

    private static string[] Project(DocumentationObservationOutcome outcome)
    {
        Assert.Equal(DocumentationObservationRunStatus.Success, outcome.Status);
        return outcome.ObservationSet!.Observations
            .Select(observation => string.Join(
                "|",
                observation.Subject.ParentSymbolRef.CompilationContextRef,
                observation.Subject.ParentSymbolRef.DocumentationCommentId,
                observation.Subject.ComponentKind?.ToString() ?? "target",
                observation.Subject.ComponentIdentity ?? string.Empty,
                observation.Value,
                observation.Completeness,
                observation.UnavailableCause,
                string.Join(
                    ",",
                    observation.Declarations.Select(fact =>
                        fact.DeclarationId
                        + ":"
                        + fact.Source.SourceSha256))))
            .ToArray();
    }

    private static string[] ProjectSubjects(
        DocumentationObservationOutcome outcome,
        params string[] documentationIds)
    {
        Assert.Equal(DocumentationObservationRunStatus.Success, outcome.Status);
        var selected = outcome.ObservationSet!.Observations
            .Where(observation => documentationIds.Contains(
                observation.Subject.ParentSymbolRef.DocumentationCommentId,
                StringComparer.Ordinal));
        return Project(DocumentationObservationOutcome.Success(
            new DocumentationObservationSet([.. selected])));
    }

    private static IReadOnlyList<MetadataReference> PlatformReferences() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();

    private sealed record SourceInput(
        string Path,
        string Text,
        LoadedSourceKind Kind,
        GeneratedSourceFact? GeneratedSource);

    private sealed class EndpointDocumentationProvider : DocumentationProvider
    {
        private readonly bool documented;

        public EndpointDocumentationProvider(bool documented)
        {
            this.documented = documented;
        }

        protected override string GetDocumentationForSymbol(
            string documentationMemberID,
            System.Globalization.CultureInfo? preferredCulture,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return documented
                ? $"""
                    <member name="{documentationMemberID}">
                    <summary>Endpoint documentation.</summary>
                    <param name="value">Endpoint parameter.</param>
                    <returns>Endpoint return.</returns>
                    </member>
                    """
                : string.Empty;
        }

        public override bool Equals(object? obj) =>
            obj is EndpointDocumentationProvider other
            && documented == other.documented;

        public override int GetHashCode() => documented.GetHashCode();
    }
}
