using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Core;
using ContractScribe.Roslyn;
using Json.Schema;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed class PolicyEvidenceExtractorTests
{
    private const string Context = "context." + Sha;
    private const string ProjectPath = "projects/App/App.csproj";
    private const string Sha =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly Lazy<JsonSchema> EvidenceSchema = new(() =>
    {
        var schema = JsonNode.Parse(File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "schemas",
            "symbol-evidence-taxonomy",
            "v1.schema.json")))!.AsObject();
        schema.Remove("$id");
        return JsonSchema.FromText(schema.ToJsonString());
    });

    [Fact]
    public void ProductionPipeline_BindsRepositoryAndGeneratedPolicyEvidence()
    {
        const string repositorySource = """
            public class RepositoryType
            {
                public void Run(string value) { }
            }
            """;
        const string generatedSource = """
            /// <summary>Generated documentation.</summary>
            public class GeneratedType { }
            """;
        var generated = new GeneratedSourceFact(
            ProjectPath,
            Context,
            "sgp." + new string('b', 64),
            "sgo." + new string('c', 64),
            Hash(generatedSource),
            generatedSource);
        using var session = CreateSession(
            new SourceInput(
                "src/RepositoryType.cs",
                repositorySource,
                LoadedSourceKind.Repository,
                null),
            new SourceInput(
                "generator://GeneratedType.g.cs",
                generatedSource,
                LoadedSourceKind.SourceGenerator,
                generated));
        var classified = new SymbolClassifier().ClassifySession(
            session,
            TargetProfile.ExternalApi);
        var observed = new DocumentationObserver().Observe(classified);

        var outcome = new PolicyEvidenceExtractor().Extract(
            classified,
            observed,
            ParsePolicy(TargetProfile.ExternalApi));

        Assert.Equal(PolicyEvidenceExtractionStatus.Success, outcome.Status);
        var repository = FindTarget(outcome, "T:RepositoryType");
        var repositoryContribution = Assert.IsType<RepositoryPolicyContribution>(
            Assert.Single(repository.PolicyContributions.Contributions));
        Assert.Equal(ProjectPath, repositoryContribution.ProjectPath);
        Assert.Equal("src/RepositoryType.cs", repositoryContribution.SourcePath);
        Assert.Equal(PolicyExpectation.Required, repositoryContribution.Expectation);
        Assert.Equal("repository-required", repositoryContribution.MatchedRuleId);
        Assert.Equal(DocumentationObservationValue.Absent, repository.Evidence.ObservationValue);
        Assert.True(repository.Evidence.SupportsOrdinaryResult);
        Assert.Equal(
            EvidenceAuthorityCompleteness.Complete,
            repository.Evidence.Authority!.Completeness);
        var repositoryEvidence = Assert.Single(repository.Evidence.Bundle.Items);
        Assert.Equal(EvidenceKind.SourceDeclaration, repositoryEvidence.Kind);
        Assert.Equal(EvidenceRelation.Declares, repositoryEvidence.Relation);
        Assert.Equal(repositorySource, repositoryEvidence.Excerpt);
        var repositoryLocator = Assert.IsType<RepositoryEvidenceLocator>(
            repositoryEvidence.Locator);
        Assert.Equal("src/RepositoryType.cs", repositoryLocator.Path);
        Assert.Equal(repositorySource.Length, repositoryLocator.Span!.Value.End);
        AssertSchemaValid(repository.Evidence.Bundle);

        var component = Assert.Single(outcome.Bindings, binding =>
            binding.Subject.ParentSymbolRef.DocumentationCommentId
                == "M:RepositoryType.Run(System.String)"
            && binding.Subject.ComponentKind == ComponentKind.Parameter
            && binding.Subject.ComponentIdentity == "parameter/0");
        var componentSubject = Assert.IsType<ComponentEvidenceSubject>(
            Assert.Single(component.Evidence.Bundle.Items).Subject);
        Assert.Equal(component.Subject.ParentSymbolRef, componentSubject.ParentSymbolRef);
        Assert.Equal(ComponentKind.Parameter, componentSubject.ComponentKind);
        Assert.Equal("parameter/0", componentSubject.Identity);

        var generatedBinding = FindTarget(outcome, "T:GeneratedType");
        var generatedContribution = Assert.IsType<GeneratedPolicyContribution>(
            Assert.Single(generatedBinding.PolicyContributions.Contributions));
        Assert.Equal(PolicyExpectation.Forbidden, generatedContribution.Expectation);
        Assert.Equal("generated-forbidden", generatedContribution.MatchedRuleId);
        Assert.Equal(generated.ProducerId, generatedContribution.GeneratedOutput.ProducerId);
        Assert.Equal(generated.OutputId, generatedContribution.GeneratedOutput.OutputId);
        Assert.Equal(DocumentationObservationValue.Present, generatedBinding.Evidence.ObservationValue);
        Assert.True(generatedBinding.Evidence.SupportsOrdinaryResult);
        var documentationEvidence = Assert.Single(
            generatedBinding.Evidence.Bundle.Items,
            item => item.Kind == EvidenceKind.SourceXmlDocumentation);
        Assert.Equal(EvidenceRelation.Documents, documentationEvidence.Relation);
        var generatedLocator = Assert.IsType<GeneratedOutputEvidenceLocator>(
            documentationEvidence.Locator);
        Assert.Equal(GeneratedOutputKind.SourceGenerator, generatedLocator.ProducerKind);
        Assert.Equal(generated.ProducerId, generatedLocator.ProducerId);
        Assert.Equal(generated.OutputId, generatedLocator.OutputId);
        Assert.Equal(generated.SourceSha256, generatedLocator.SourceSha256);
        AssertSchemaValid(generatedBinding.Evidence.Bundle);

        Assert.Equal(
            observed.ObservationSet!.Observations.Length,
            outcome.Bindings.Length);
        Assert.All(outcome.Bindings, binding =>
        {
            Assert.NotNull(binding.Evidence.Bundle.ObservationSubject);
            Assert.Equal(
                binding.Subject.ParentSymbolRef.CompilationContextRef,
                binding.Evidence.Bundle.ObservationSubject!.CompilationContextRef);
        });
    }

    [Fact]
    public void BudgetLimitedDeclaration_PreservesPolicyContributionWithoutOrdinarySupport()
    {
        var source = "public class Huge { private readonly string value = \""
            + new string('x', 5000)
            + "\"; }";
        using var session = CreateSession(new SourceInput(
            "src/Huge.cs",
            source,
            LoadedSourceKind.Repository,
            null));
        var classified = new SymbolClassifier().ClassifySession(
            session,
            TargetProfile.ExternalApi);
        var observed = new DocumentationObserver().Observe(classified);

        var outcome = new PolicyEvidenceExtractor().Extract(
            classified,
            observed,
            ParsePolicy(TargetProfile.ExternalApi));

        Assert.Equal(PolicyEvidenceExtractionStatus.Success, outcome.Status);
        var target = FindTarget(outcome, "T:Huge");
        var contribution = Assert.IsType<RepositoryPolicyContribution>(
            Assert.Single(target.PolicyContributions.Contributions));
        Assert.Equal(PolicyExpectation.Required, contribution.Expectation);
        Assert.Equal("repository-required", contribution.MatchedRuleId);
        Assert.Equal(
            EvidenceAvailabilityStatus.Partial,
            target.Evidence.Bundle.AvailabilityStatus);
        Assert.Equal(
            EvidenceOmissionReason.BudgetExhausted,
            target.Evidence.Bundle.OmissionReason);
        Assert.False(target.Evidence.SupportsOrdinaryResult);
        Assert.Empty(target.Evidence.EvidenceIds);
        Assert.Null(target.Evidence.Authority);
        Assert.True(Assert.Single(target.Evidence.Bundle.Items).IsTruncated);
    }

    [Fact]
    public void Extractor_FailsClosedForProfileDriftSourceDriftAndCancellation()
    {
        const string source = "public class Fixture { }";
        using var session = CreateSession(new SourceInput(
            "src/Fixture.cs",
            source,
            LoadedSourceKind.Repository,
            null));
        var classified = new SymbolClassifier().ClassifySession(
            session,
            TargetProfile.ExternalApi);
        var observed = new DocumentationObserver().Observe(classified);
        var extractor = new PolicyEvidenceExtractor();

        var profileDrift = extractor.Extract(
            classified,
            observed,
            ParsePolicy(TargetProfile.AssemblyVisible));
        Assert.Equal(PolicyEvidenceExtractionStatus.Failure, profileDrift.Status);
        Assert.Empty(profileDrift.Bindings);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = extractor.Extract(
            classified,
            observed,
            ParsePolicy(TargetProfile.ExternalApi),
            cancellation.Token);
        Assert.Equal(PolicyEvidenceExtractionStatus.Cancelled, cancelled.Status);
        Assert.Empty(cancelled.Bindings);

        var project = Assert.Single(session.Projects);
        var mutableSources = Assert.IsType<Dictionary<SyntaxTree, LoadedSourceTree>>(
            project.SourceTrees);
        mutableSources.Clear();
        var sourceDrift = extractor.Extract(
            classified,
            observed,
            ParsePolicy(TargetProfile.ExternalApi));
        Assert.Equal(PolicyEvidenceExtractionStatus.Failure, sourceDrift.Status);
        Assert.Empty(sourceDrift.Bindings);
    }

    [Fact]
    public void Binder_RejectsDanglingDuplicateCrossSubjectAndContradictoryEvidence()
    {
        const string source = "public class Fixture { }";
        using var session = CreateSession(new SourceInput(
            "src/Fixture.cs",
            source,
            LoadedSourceKind.Repository,
            null));
        var classified = new SymbolClassifier().ClassifySession(
            session,
            TargetProfile.ExternalApi);
        var observed = new DocumentationObserver().Observe(classified);
        var observation = Assert.Single(observed.ObservationSet!.Observations, candidate =>
            candidate.Subject.ComponentKind is null
            && candidate.Subject.ParentSymbolRef.DocumentationCommentId == "T:Fixture");
        var extracted = FindTarget(
            new PolicyEvidenceExtractor().Extract(
                classified,
                observed,
                ParsePolicy(TargetProfile.ExternalApi)),
            "T:Fixture");
        var authorityRow = Assert.Single(extracted.Evidence.Authority!.Declarations);
        var validBinding = EvidenceBindingInput.Declaration(
            authorityRow.DeclarationId,
            authorityRow.EvidenceId,
            null);

        AssertBindingFailure(EvidenceObservationBinder.Bind(
            observation,
            extracted.Evidence.Bundle,
            [EvidenceBindingInput.Declaration(
                authorityRow.DeclarationId,
                "evidence.dangling",
                null)]));
        AssertBindingFailure(EvidenceObservationBinder.Bind(
            observation,
            extracted.Evidence.Bundle,
            [validBinding, validBinding]));

        var original = Assert.Single(extracted.Evidence.Bundle.Items);
        var crossSubject = EvidenceInput.TargetSubject(Context, "T:Other");
        var crossBundle = EvidenceNormalizer.Normalize(
        [
            EvidenceInput.Candidate(
                original.EvidenceId,
                crossSubject,
                original.Kind,
                original.Relation,
                original.Excerpt,
                original.Locator,
                original.Sha256)
        ]).Bundle!;
        AssertBindingFailure(EvidenceObservationBinder.Bind(
            observation,
            crossBundle,
            [validBinding]));

        const string mismatchedDeclaration = "public class Other { }";
        var mismatchedBundle = EvidenceNormalizer.Normalize(
        [
            EvidenceInput.Candidate(
                original.EvidenceId,
                original.Subject,
                original.Kind,
                original.Relation,
                mismatchedDeclaration,
                EvidenceInput.RepositoryLocator(
                    "src/Fixture.cs",
                    0,
                    mismatchedDeclaration.Length))
        ]).Bundle!;
        AssertBindingFailure(EvidenceObservationBinder.Bind(
            observation,
            mismatchedBundle,
            [validBinding]));

        var mismatchedLocatorBundle = EvidenceNormalizer.Normalize(
        [
            EvidenceInput.Candidate(
                original.EvidenceId,
                original.Subject,
                original.Kind,
                original.Relation,
                original.Excerpt,
                EvidenceInput.RepositoryLocator(
                    "src/Other.cs",
                    0,
                    original.Excerpt.Length),
                original.Sha256)
        ]).Bundle!;
        AssertBindingFailure(EvidenceObservationBinder.Bind(
            observation,
            mismatchedLocatorBundle,
            [validBinding]));

        var declaration = Assert.Single(observation.Declarations);
        var contradictoryDeclaration = new DocumentationDeclarationFact(
            declaration.DeclarationId,
            declaration.AuthorityRole,
            declaration.Source,
            declaration.DeclarationSpan,
            declaration.DeclarationText,
            declaration.DeclarationSha256,
            declaration.LeadingTriviaSpan,
            declaration.LeadingTriviaText,
            declaration.LeadingTriviaSha256,
            declaration.DocumentationSpan,
            declaration.DocumentationText,
            declaration.DocumentationSha256,
            declaration.BlockState,
            parentSubstantive: true,
            declaration.ComponentLocalName,
            declaration.ComponentMatch);
        var contradictoryObservation = new DocumentationObservation(
            observation.Subject,
            DocumentationObservationValue.Absent,
            DocumentationAuthorityCompleteness.Complete,
            DocumentationUnavailableCause.None,
            [contradictoryDeclaration]);
        AssertBindingFailure(EvidenceObservationBinder.Bind(
            contradictoryObservation,
            extracted.Evidence.Bundle,
            [validBinding]));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = EvidenceObservationBinder.Bind(
            observation,
            extracted.Evidence.Bundle,
            [validBinding],
            cancellation.Token);
        Assert.Equal(EvidenceRunStatus.Cancelled, cancelled.Status);
        Assert.Null(cancelled.Binding);
        Assert.Null(cancelled.PrimaryFailure);
    }

    [Fact]
    public void Binder_PreservesUnavailableAndPositiveOnlyNonFabricationRules()
    {
        var subject = new DocumentationObservationSubject(
            new SymbolRef(Context, "T:Unavailable"),
            null,
            null);
        var unavailableObservation = new DocumentationObservation(
            subject,
            DocumentationObservationValue.Unavailable,
            DocumentationAuthorityCompleteness.Incomplete,
            DocumentationUnavailableCause.SourceUnavailable,
            []);
        var unavailableBundle = EvidenceNormalizer.Normalize(
            [],
            [EvidenceOmissionReason.SourceUnavailable]).Bundle!;

        var unavailable = EvidenceObservationBinder.Bind(
            unavailableObservation,
            unavailableBundle,
            []);

        Assert.Equal(EvidenceRunStatus.Success, unavailable.Status);
        Assert.Equal(
            EvidenceAvailabilityStatus.Unavailable,
            unavailable.Binding!.Bundle.AvailabilityStatus);
        Assert.False(unavailable.Binding.SupportsOrdinaryResult);
        Assert.Empty(unavailable.Binding.EvidenceIds);
        Assert.Null(unavailable.Binding.Authority);
        Assert.Null(unavailable.Binding.Bundle.ObservationSubject);

        var normalizedUnavailable = EvidenceObservationBinder.Bind(
            unavailableObservation,
            new EvidenceBundle(
                EvidenceAvailabilityStatus.Complete,
                null,
                [new EvidenceItem(
                    "evidence.synthetic",
                    EvidenceInput.TargetSubject(Context, "T:Unavailable"),
                    EvidenceKind.SourceDeclaration,
                    EvidenceRelation.Declares,
                    "x",
                    Hash("x"),
                    1,
                    1,
                    0,
                    false,
                    EvidenceInput.SyntheticLocator("fixture.synthetic"))],
                null),
            []);
        Assert.Equal(EvidenceRunStatus.Success, normalizedUnavailable.Status);
        Assert.Equal(
            EvidenceAvailabilityStatus.Unavailable,
            normalizedUnavailable.Binding!.Bundle.AvailabilityStatus);
        Assert.Equal(
            EvidenceOmissionReason.SourceUnavailable,
            normalizedUnavailable.Binding.Bundle.OmissionReason);
        Assert.Empty(normalizedUnavailable.Binding.Bundle.Items);

        const string generatedSource = """
            /// <summary>Generated documentation.</summary>
            public class GeneratedType { }
            """;
        var generated = new GeneratedSourceFact(
            ProjectPath,
            Context,
            "sgp." + new string('d', 64),
            "sgo." + new string('e', 64),
            Hash(generatedSource),
            generatedSource);
        using var session = CreateSession(new SourceInput(
            "generator://GeneratedType.g.cs",
            generatedSource,
            LoadedSourceKind.SourceGenerator,
            generated));
        var classified = new SymbolClassifier().ClassifySession(
            session,
            TargetProfile.ExternalApi);
        var observed = new DocumentationObserver().Observe(classified);
        var presentObservation = Assert.Single(
            observed.ObservationSet!.Observations,
            candidate => candidate.Subject.ComponentKind is null
                && candidate.Subject.ParentSymbolRef.DocumentationCommentId
                    == "T:GeneratedType");
        var extracted = FindTarget(
            new PolicyEvidenceExtractor().Extract(
                classified,
                observed,
                ParsePolicy(TargetProfile.ExternalApi)),
            "T:GeneratedType");
        var row = Assert.Single(extracted.Evidence.Authority!.Declarations);
        var positiveOnly = new DocumentationObservation(
            presentObservation.Subject,
            DocumentationObservationValue.Present,
            DocumentationAuthorityCompleteness.PositiveOnly,
            DocumentationUnavailableCause.None,
            presentObservation.Declarations);

        var rebound = EvidenceObservationBinder.Bind(
            positiveOnly,
            extracted.Evidence.Bundle,
            [EvidenceBindingInput.Declaration(row.DeclarationId, null, row.EvidenceId)]);

        Assert.Equal(EvidenceRunStatus.Success, rebound.Status);
        Assert.True(rebound.Binding!.SupportsOrdinaryResult);
        Assert.Equal(
            EvidenceAuthorityCompleteness.PositiveOnly,
            rebound.Binding.Authority!.Completeness);
        Assert.Equal(row.EvidenceId, Assert.Single(rebound.Binding.EvidenceIds));
    }

    private static PolicyEvidenceSubjectBinding FindTarget(
        PolicyEvidenceExtractionOutcome outcome,
        string documentationId) =>
        Assert.Single(outcome.Bindings, binding =>
            binding.Subject.ComponentKind is null
            && binding.Subject.ParentSymbolRef.DocumentationCommentId
                == documentationId);

    private static void AssertBindingFailure(EvidenceBindingOutcome outcome)
    {
        Assert.Equal(EvidenceRunStatus.Failure, outcome.Status);
        Assert.Null(outcome.Binding);
        Assert.NotNull(outcome.PrimaryFailure);
    }

    private static void AssertSchemaValid(EvidenceBundle bundle)
    {
        Assert.True(EvidenceSchema.Value.Evaluate(ProjectBundle(bundle)).IsValid);
    }

    private static JsonElement ProjectBundle(EvidenceBundle bundle)
    {
        var node = new JsonObject
        {
            ["evidenceBundleVersion"] = bundle.EvidenceBundleVersion,
            ["availabilityStatus"] = EvidenceVocabulary.GetId(bundle.AvailabilityStatus),
            ["items"] = new JsonArray(bundle.Items.Select(ProjectItem).ToArray()),
        };
        if (bundle.OmissionReason is { } omission)
        {
            node["omissionReason"] = EvidenceVocabulary.GetId(omission);
        }

        if (bundle.ObservationSubject is { } observation)
        {
            node["observationSubject"] = new JsonObject
            {
                ["observationSubjectRef"] = observation.ObservationSubjectRef,
                ["compilationContextRef"] = observation.CompilationContextRef,
                ["subject"] = ProjectSubject(observation.Subject),
                ["authoritativeDeclarationSetDigest"] =
                    observation.AuthoritativeDeclarationSetDigest,
                ["authoritativeDeclarationCount"] =
                    observation.AuthoritativeDeclarationCount,
            };
        }

        return JsonSerializer.SerializeToElement(node);
    }

    private static JsonObject ProjectItem(EvidenceItem item) =>
        new()
        {
            ["evidenceId"] = item.EvidenceId,
            ["subject"] = ProjectSubject(item.Subject),
            ["kind"] = EvidenceVocabulary.GetId(item.Kind),
            ["relation"] = EvidenceVocabulary.GetId(item.Relation),
            ["excerpt"] = item.Excerpt,
            ["sha256"] = item.Sha256,
            ["originalUtf8ByteCount"] = item.OriginalUtf8ByteCount,
            ["includedUtf8ByteCount"] = item.IncludedUtf8ByteCount,
            ["omittedUtf8ByteCount"] = item.OmittedUtf8ByteCount,
            ["isTruncated"] = item.IsTruncated,
            ["locator"] = ProjectLocator(item.Locator),
        };

    private static JsonObject ProjectSubject(EvidenceSubject subject) =>
        subject is ComponentEvidenceSubject component
            ? new JsonObject
            {
                ["parentSymbolRef"] = ProjectSymbolRef(component.ParentSymbolRef),
                ["componentKind"] = ClassificationVocabulary.GetId(
                    component.ComponentKind),
                ["identity"] = component.Identity,
            }
            : ProjectSymbolRef(subject.ParentSymbolRef);

    private static JsonObject ProjectSymbolRef(SymbolRef symbolRef) =>
        new()
        {
            ["compilationContextRef"] = symbolRef.CompilationContextRef,
            ["documentationCommentId"] = symbolRef.DocumentationCommentId,
        };

    private static JsonObject ProjectLocator(EvidenceLocator locator) => locator switch
    {
        RepositoryEvidenceLocator repository => new JsonObject
        {
            ["repository"] = WithSpan(
                new JsonObject { ["path"] = repository.Path },
                repository.Span),
        },
        MetadataEvidenceLocator metadata => new JsonObject
        {
            ["metadata"] = new JsonObject
            {
                ["assemblyIdentity"] = metadata.AssemblyIdentity,
                ["documentationCommentId"] = metadata.DocumentationCommentId,
            },
        },
        GeneratedOutputEvidenceLocator generated => new JsonObject
        {
            ["generatedOutput"] = WithSpan(
                new JsonObject
                {
                    ["producerKind"] = PolicyConfigurationVocabulary.GetId(
                        generated.ProducerKind),
                    ["producerId"] = generated.ProducerId,
                    ["outputId"] = generated.OutputId,
                    ["sourceSha256"] = generated.SourceSha256,
                },
                generated.Span),
        },
        SyntheticEvidenceLocator synthetic => new JsonObject
        {
            ["synthetic"] = new JsonObject { ["fixtureId"] = synthetic.FixtureId },
        },
        _ => throw new InvalidOperationException("Unknown evidence locator."),
    };

    private static JsonObject WithSpan(JsonObject locator, Utf16Span? span)
    {
        if (span is { } value)
        {
            locator["span"] = new JsonObject
            {
                ["start"] = value.Start,
                ["end"] = value.End,
            };
        }

        return locator;
    }

    private static PolicyDocumentV1 ParsePolicy(TargetProfile targetProfile)
    {
        var profile = targetProfile == TargetProfile.ExternalApi
            ? "profile.external-api"
            : "profile.assembly-visible";
        var outcome = PolicyConfigurationEvaluator.Parse(Encoding.UTF8.GetBytes($$"""
            {
              "schemaVersion": 1,
              "targetProfile": "{{profile}}",
              "defaultDecision": "optional",
              "rules": [
                {
                  "id": "repository-required",
                  "priority": 2,
                  "decision": "required",
                  "sourcePaths": { "include": ["src/**"] }
                },
                {
                  "id": "generated-forbidden",
                  "priority": 1,
                  "decision": "forbidden",
                  "projectPaths": { "include": ["projects/**"] }
                }
              ]
            }
            """));
        Assert.Equal(PolicyRunStatus.Success, outcome.Status);
        return outcome.Document!;
    }

    private static LoadedRepositorySession CreateSession(params SourceInput[] sources)
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
        var errors = compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Assert.True(
            errors.Length == 0,
            string.Join(Environment.NewLine, errors.Select(error => error.ToString())));
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
            ProjectPath,
            "net10.0",
            Context,
            LoadedProjectRole.AuditRoot,
            [],
            project,
            compilation,
            bindings);
        return new LoadedRepositorySession(
            ".",
            ProjectPath,
            new ToolchainIdentity("test", "test", "test", "test"),
            [loadedProject],
            sources
                .Select(source => source.GeneratedSource)
                .Where(fact => fact is not null)
                .Cast<GeneratedSourceFact>()
                .ToArray(),
            workspace);
    }

    private static IReadOnlyList<MetadataReference> PlatformReferences() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false, true)
            .GetBytes(value)))
            .ToLowerInvariant();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "ContractScribe.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Repository root not found.");
    }

    private sealed record SourceInput(
        string Path,
        string Text,
        LoadedSourceKind Kind,
        GeneratedSourceFact? GeneratedSource);
}
