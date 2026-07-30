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
    public void PairedPartialPropertyAndEventFollowImplementingAuthority()
    {
        var source = """
            public partial class Paired
            {
                /// <summary>Property definition fallback.</summary>
                public partial int Number { get; }

                /// <summary>Event definition must not win.</summary>
                public partial event System.Action Changed;
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
        var classified = new ClassifiedRepositorySession(
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
        var observeMethods = typeof(DocumentationObserver)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.Name == nameof(DocumentationObserver.Observe))
            .ToArray();
        var observe = Assert.Single(observeMethods);
        Assert.Equal(
            typeof(ClassifiedRepositorySession),
            observe.GetParameters()[0].ParameterType);
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
        return new LoadedRepositorySession(
            ".",
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
}
