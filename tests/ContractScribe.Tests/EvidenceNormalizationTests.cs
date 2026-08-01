using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Core;
using Json.Schema;

namespace ContractScribe.Tests;

public sealed class EvidenceNormalizationTests
{
    [Fact]
    public void AllLocatorVariants_ProduceSchemaValidCanonicalItems()
    {
        var subject = EvidenceInput.TargetSubject("synthetic.v1", "T:Example.Widget");
        var producerId = "sgp." + new string('1', 64);
        var outputId = "sgo." + new string('2', 64);
        var sourceSha = new string('3', 64);
        var candidates = new[]
        {
            EvidenceInput.Candidate(
                "evidence.z",
                subject,
                EvidenceKind.PublicContract,
                EvidenceRelation.Constrains,
                "contract",
                EvidenceInput.SyntheticLocator("fixture.contract")),
            EvidenceInput.Candidate(
                "evidence.a",
                subject,
                EvidenceKind.SourceDeclaration,
                EvidenceRelation.Declares,
                "class Widget {}",
                EvidenceInput.RepositoryLocator("src/Widget.cs", 0, 15)),
            EvidenceInput.Candidate(
                "evidence.m",
                subject,
                EvidenceKind.RepositoryDocumentation,
                EvidenceRelation.References,
                "metadata",
                EvidenceInput.MetadataLocator("system.runtime", "T:System.String")),
            EvidenceInput.Candidate(
                "evidence.g",
                subject,
                EvidenceKind.SourceXmlDocumentation,
                EvidenceRelation.Documents,
                "/// docs",
                EvidenceInput.GeneratedOutputLocator(
                    GeneratedOutputKind.SourceGenerator,
                    producerId,
                    outputId,
                    sourceSha,
                    10,
                    18)),
        };

        var outcome = EvidenceNormalizer.Normalize(candidates);

        Assert.Equal(EvidenceRunStatus.Success, outcome.Status);
        var bundle = outcome.Bundle!;
        Assert.Equal(EvidenceAvailabilityStatus.Complete, bundle.AvailabilityStatus);
        Assert.Null(bundle.OmissionReason);
        Assert.Equal(
            ["evidence.a", "evidence.g", "evidence.m", "evidence.z"],
            bundle.Items.Select(item => item.EvidenceId));
        Assert.All(bundle.Items, item =>
        {
            Assert.False(item.IsTruncated);
            Assert.Equal(item.OriginalUtf8ByteCount, item.IncludedUtf8ByteCount);
            Assert.Equal(0, item.OmittedUtf8ByteCount);
            Assert.Equal(Sha256(item.Excerpt), item.Sha256);
        });

        var root = FindRepositoryRoot();
        var schemaDocument = JsonNode.Parse(File.ReadAllText(Path.Combine(
            root,
            "schemas",
            "symbol-evidence-taxonomy",
            "v1.schema.json")))!.AsObject();
        schemaDocument.Remove("$id");
        var schema = JsonSchema.FromText(schemaDocument.ToJsonString());
        Assert.True(schema.Evaluate(Project(bundle)).IsValid);
    }

    [Fact]
    public void UnicodeTruncation_UsesScalarBoundariesAndCompleteOriginalHash()
    {
        var original = string.Concat(Enumerable.Repeat("😀", 1024)) + "x";
        var subject = EvidenceInput.TargetSubject("synthetic.v1", "T:Example.Widget");
        var outcome = EvidenceNormalizer.Normalize(
        [
            EvidenceInput.Candidate(
                "evidence.unicode",
                subject,
                EvidenceKind.SourceDeclaration,
                EvidenceRelation.Declares,
                original,
                EvidenceInput.RepositoryLocator(
                    "src/Widget.cs",
                    20,
                    20 + original.Length))
        ]);

        Assert.Equal(EvidenceRunStatus.Success, outcome.Status);
        var bundle = outcome.Bundle!;
        Assert.Equal(EvidenceAvailabilityStatus.Partial, bundle.AvailabilityStatus);
        Assert.Equal(EvidenceOmissionReason.BudgetExhausted, bundle.OmissionReason);
        var item = Assert.Single(bundle.Items);
        Assert.Equal(string.Concat(Enumerable.Repeat("😀", 1024)), item.Excerpt);
        Assert.Equal(4097, item.OriginalUtf8ByteCount);
        Assert.Equal(4096, item.IncludedUtf8ByteCount);
        Assert.Equal(1, item.OmittedUtf8ByteCount);
        Assert.True(item.IsTruncated);
        Assert.Equal(Sha256(original), item.Sha256);
    }

    [Fact]
    public void ItemAndBundleBudgets_AreExactAndDeterministic()
    {
        var subject = EvidenceInput.TargetSubject("synthetic.v1", "T:Example.Widget");
        var candidates = Enumerable.Range(0, 33)
            .Reverse()
            .Select(index => EvidenceInput.Candidate(
                $"evidence.item-{index:D2}",
                subject,
                EvidenceKind.SourceDeclaration,
                EvidenceRelation.Declares,
                "x",
                EvidenceInput.SyntheticLocator($"fixture.item-{index:D2}")))
            .ToArray();

        var first = EvidenceNormalizer.Normalize(candidates);
        var second = EvidenceNormalizer.Normalize(candidates.Reverse());

        Assert.Equal(EvidenceRunStatus.Success, first.Status);
        Assert.Equal(EvidenceAvailabilityStatus.Partial, first.Bundle!.AvailabilityStatus);
        Assert.Equal(EvidenceOmissionReason.BudgetExhausted, first.Bundle.OmissionReason);
        Assert.Equal(32, first.Bundle.Items.Length);
        Assert.Equal(
            first.Bundle.Items.Select(ProjectItem).Select(node => node.ToJsonString()),
            second.Bundle!.Items.Select(ProjectItem).Select(node => node.ToJsonString()));

        var totalBoundary = Enumerable.Range(0, 9)
            .Select(index => EvidenceInput.Candidate(
                $"evidence.total-{index}",
                subject,
                EvidenceKind.SourceDeclaration,
                EvidenceRelation.Declares,
                new string('x', 4096),
                EvidenceInput.SyntheticLocator($"fixture.total-{index}")));
        var total = EvidenceNormalizer.Normalize(totalBoundary);
        Assert.Equal(EvidenceAvailabilityStatus.Partial, total.Bundle!.AvailabilityStatus);
        Assert.Equal(32768, total.Bundle.Items.Sum(item => item.IncludedUtf8ByteCount));
        Assert.Equal(8, total.Bundle.Items.Length);
        Assert.All(total.Bundle.Items, item => Assert.False(item.IsTruncated));
    }

    [Fact]
    public void OmissionPrecedenceAndUnavailableShape_AreClosed()
    {
        var subject = EvidenceInput.TargetSubject("synthetic.v1", "T:Example.Widget");
        var candidate = EvidenceInput.Candidate(
            "evidence.one",
            subject,
            EvidenceKind.SourceDeclaration,
            EvidenceRelation.Declares,
            "x",
            EvidenceInput.SyntheticLocator("fixture.one"));
        var partial = EvidenceNormalizer.Normalize(
            [candidate],
            [
                EvidenceOmissionReason.NotProvided,
                EvidenceOmissionReason.BinaryContent,
                EvidenceOmissionReason.SourceUnavailable,
                EvidenceOmissionReason.AccessNotPermitted,
            ]);
        Assert.Equal(EvidenceAvailabilityStatus.Partial, partial.Bundle!.AvailabilityStatus);
        Assert.Equal(EvidenceOmissionReason.AccessNotPermitted, partial.Bundle.OmissionReason);

        var unavailable = EvidenceNormalizer.Normalize(
            [],
            [EvidenceOmissionReason.SourceUnavailable]);
        Assert.Equal(
            EvidenceAvailabilityStatus.Unavailable,
            unavailable.Bundle!.AvailabilityStatus);
        Assert.Equal(EvidenceOmissionReason.SourceUnavailable, unavailable.Bundle.OmissionReason);
        Assert.Empty(unavailable.Bundle.Items);
    }

    [Fact]
    public void InvalidIdentityHashSpanDuplicateAndBudgets_FailClosed()
    {
        var subject = EvidenceInput.TargetSubject("synthetic.v1", "T:Example.Widget");
        var valid = EvidenceInput.Candidate(
            "evidence.one",
            subject,
            EvidenceKind.SourceDeclaration,
            EvidenceRelation.Declares,
            "abc",
            EvidenceInput.RepositoryLocator("src/Widget.cs", 0, 3));
        AssertFailure(EvidenceNormalizer.Normalize([valid, valid]));
        AssertFailure(EvidenceNormalizer.Normalize(
        [
            EvidenceInput.Candidate(
                "Evidence.Invalid",
                subject,
                EvidenceKind.SourceDeclaration,
                EvidenceRelation.Declares,
                "abc",
                EvidenceInput.RepositoryLocator("src/Widget.cs", 0, 3))
        ]));
        AssertFailure(EvidenceNormalizer.Normalize(
        [
            EvidenceInput.Candidate(
                "evidence.hash",
                subject,
                EvidenceKind.SourceDeclaration,
                EvidenceRelation.Declares,
                "abc",
                EvidenceInput.RepositoryLocator("src/Widget.cs", 0, 3),
                new string('0', 64))
        ]));
        AssertFailure(EvidenceNormalizer.Normalize(
        [
            EvidenceInput.Candidate(
                "evidence.span",
                subject,
                EvidenceKind.SourceDeclaration,
                EvidenceRelation.Declares,
                "abc",
                EvidenceInput.RepositoryLocator("src/Widget.cs", 0, 2))
        ]));
        AssertFailure(EvidenceNormalizer.Normalize(
            [valid],
            budgets: EvidenceInput.Budgets(0, 4096, 32768)));
        AssertFailure(EvidenceNormalizer.Normalize(
        [
            EvidenceInput.Candidate(
                "evidence.too-small-for-scalar",
                subject,
                EvidenceKind.SourceDeclaration,
                EvidenceRelation.Declares,
                "😀",
                EvidenceInput.SyntheticLocator("fixture.scalar"))
        ],
            budgets: EvidenceInput.Budgets(32, 1, 32768)));
        AssertFailure(EvidenceNormalizer.Normalize(
            [valid],
            budgets: EvidenceInput.Budgets(33, 4096, 32768)));
        AssertFailure(EvidenceNormalizer.Normalize(
            [valid],
            budgets: EvidenceInput.Budgets(32, 4097, 32768)));
        AssertFailure(EvidenceNormalizer.Normalize(
            [valid],
            budgets: EvidenceInput.Budgets(32, 4096, 32769)));
        AssertFailure(EvidenceNormalizer.Normalize(
        [
            EvidenceInput.Candidate(
                "evidence.unpaired",
                subject,
                EvidenceKind.SourceDeclaration,
                EvidenceRelation.Declares,
                "\ud800",
                EvidenceInput.SyntheticLocator("fixture.unpaired"))
        ]));
        AssertFailure(EvidenceNormalizer.Normalize(
        [
            EvidenceInput.Candidate(
                "evidence.null-region",
                subject,
                EvidenceKind.SourceDeclaration,
                EvidenceRelation.Declares,
                null!,
                EvidenceInput.SyntheticLocator("fixture.null-region"))
        ]));
        AssertFailure(EvidenceNormalizer.Normalize(
        [
            EvidenceInput.Candidate(
                "evidence.null-component",
                EvidenceInput.ComponentSubject(
                    "synthetic.v1",
                    "T:Example.Widget",
                    ComponentKind.Parameter,
                    null!),
                EvidenceKind.SourceDeclaration,
                EvidenceRelation.Declares,
                "abc",
                EvidenceInput.SyntheticLocator("fixture.null-component"))
        ]));
        AssertFailure(EvidenceNormalizer.Normalize(
        [
            EvidenceInput.Candidate(
                "evidence.null-generated",
                subject,
                EvidenceKind.SourceDeclaration,
                EvidenceRelation.Declares,
                "abc",
                EvidenceInput.GeneratedOutputLocator(
                    GeneratedOutputKind.SourceGenerator,
                    null!,
                    null!,
                    null!))
        ]));
    }

    [Fact]
    public void Cancellation_ExposesNoPartialBundle()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var outcome = EvidenceNormalizer.Normalize(
            [],
            cancellationToken: cancellation.Token);
        Assert.Equal(EvidenceRunStatus.Cancelled, outcome.Status);
        Assert.Null(outcome.Bundle);
        Assert.Null(outcome.PrimaryFailure);
    }

    private static void AssertFailure(EvidenceNormalizationOutcome outcome)
    {
        Assert.Equal(EvidenceRunStatus.Failure, outcome.Status);
        Assert.Null(outcome.Bundle);
        Assert.NotNull(outcome.PrimaryFailure);
    }

    private static JsonElement Project(EvidenceBundle bundle)
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
                ["componentKind"] = ClassificationVocabulary.GetId(component.ComponentKind),
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

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
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
}
