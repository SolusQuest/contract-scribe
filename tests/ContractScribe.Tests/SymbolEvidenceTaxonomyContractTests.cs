using System.Text.Json;
using Json.Schema;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ContractScribe.Tests;

public sealed class SymbolEvidenceTaxonomyContractTests
{
    private static readonly Lazy<JsonSchema> EvidenceSchema = new(() => JsonSchema.FromText(File.ReadAllText(Path.Combine(FindRepositoryRoot(), "schemas", "symbol-evidence-taxonomy", "v1.schema.json"))));
    [Fact]
    public void Registry_UsesClosedUniqueDottedIdentifiers()
    {
        var root = FindRepositoryRoot();
        using var registry = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "schemas", "symbol-evidence-taxonomy", "v1.registry.json")));
        Assert.Equal(1, registry.RootElement.GetProperty("registryVersion").GetInt32());
        var identifiers = registry.RootElement.GetProperty("sections").EnumerateObject().SelectMany(section => section.Value.EnumerateArray().Select(value => value.GetString()!)).ToArray();
        Assert.Equal(identifiers.Length, identifiers.Distinct(StringComparer.Ordinal).Count());
        Assert.All(identifiers, id => Assert.Matches("^[a-z][a-z0-9-]*(\\.[a-z][a-z0-9-]*)*$", id));
    }

    [Fact]
    public void Schema_ValidatesABoundedEvidenceBundle()
    {
        using var valid = JsonDocument.Parse("{\"evidenceBundleVersion\":1,\"availabilityStatus\":\"evidence.bundle.unavailable\",\"omissionReason\":\"evidence.omission.not-provided\",\"items\":[]}");
        Assert.True(EvidenceSchema.Value.Evaluate(valid.RootElement).IsValid);
    }

    [Theory]
    [InlineData("{\"evidenceBundleVersion\":1,\"availabilityStatus\":\"evidence.bundle.unavailable\",\"omissionReason\":\"evidence.omission.not-provided\",\"items\":[],\"unexpected\":true}")]
    [InlineData("{\"evidenceBundleVersion\":2,\"availabilityStatus\":\"evidence.bundle.unavailable\",\"omissionReason\":\"evidence.omission.not-provided\",\"items\":[]}")]
    [InlineData("{\"evidenceBundleVersion\":1,\"availabilityStatus\":\"evidence.bundle.unavailable\",\"omissionReason\":\"evidence.omission.not-provided\",\"items\":[{\"evidenceId\":\"evidence.one\",\"subject\":{\"compilationContextRef\":\"synthetic.v1\",\"documentationCommentId\":\"T:Example\"},\"kind\":\"evidence.source.declaration\",\"relation\":\"evidence.declares\",\"excerpt\":\"x\",\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"originalUtf8ByteCount\":1,\"includedUtf8ByteCount\":1,\"omittedUtf8ByteCount\":0,\"isTruncated\":false,\"locator\":{\"repository\":{\"path\":\"a.cs\"},\"synthetic\":{\"fixtureId\":\"x\"}}}]}")]
    public void Schema_RejectsUnknownVersionsPropertiesAndAmbiguousLocators(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        Assert.False(EvidenceSchema.Value.Evaluate(document.RootElement).IsValid);
    }

    [Fact]
    public void EvidenceBundle_InvariantsCoverAvailabilityCountsAndTruncation()
    {
        Assert.Throws<InvalidOperationException>(() => ValidateBundle("evidence.bundle.complete", null, []));
        Assert.Throws<InvalidOperationException>(() => ValidateBundle("evidence.bundle.unavailable", "evidence.omission.not-provided", [CreateEvidence("x", 1, 1, 0, false)]));
        Assert.Throws<InvalidOperationException>(() => ValidateBundle("evidence.bundle.partial", "evidence.omission.access-not-permitted", [CreateEvidence("x", 2, 1, 1, true)]));
        ValidateBundle("evidence.bundle.complete", null, [CreateEvidence("a", 1, 1, 0, false)]);
        ValidateBundle("evidence.bundle.partial", "evidence.omission.budget-exhausted", [CreateEvidence("a", 2, 1, 1, true)]);
        ValidateBundle("evidence.bundle.unavailable", "evidence.omission.not-provided", []);
    }

    [Fact]
    public void SyntheticCorpus_CompilesAndExposesManifestSymbols()
    {
        var root = FindRepositoryRoot();
        var fixture = Path.Combine(root, "tests", "fixtures", "symbol-evidence-taxonomy", "v1");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture, "manifest.json")));
        var source = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(fixture, "SampleSymbols.cs")), new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create("taxonomy-fixture", [source], [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)], new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Empty(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var ids = manifest.RootElement.GetProperty("expectedDocumentationCommentIds").EnumerateArray().Select(value => value.GetString()!).ToHashSet(StringComparer.Ordinal);
        var seen = compilation.Assembly.GlobalNamespace.GetNamespaceMembers().SelectMany(namespaceSymbol => namespaceSymbol.GetTypeMembers()).Select(symbol => symbol.GetDocumentationCommentId()).OfType<string>().ToHashSet(StringComparer.Ordinal);
        Assert.True(ids.Overlaps(seen));
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ContractScribe.slnx"))) return directory.FullName;
        }
        throw new InvalidOperationException("Repository root not found.");
    }

    private static Evidence CreateEvidence(string id, int original, int included, int omitted, bool truncated) => new(id, original, included, omitted, truncated);

    private static void ValidateBundle(string status, string? omissionReason, IReadOnlyList<Evidence> items)
    {
        if (status == "evidence.bundle.complete" && (items.Count == 0 || omissionReason is not null || items.Any(item => item.Truncated))) throw new InvalidOperationException();
        if (status == "evidence.bundle.partial" && (items.Count == 0 || omissionReason is null)) throw new InvalidOperationException();
        if (status == "evidence.bundle.unavailable" && (items.Count != 0 || omissionReason is null)) throw new InvalidOperationException();
        if (items.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != items.Count) throw new InvalidOperationException();
        foreach (var item in items)
        {
            if (item.Original != item.Included + item.Omitted || (item.Original == 0 && (item.Included != 0 || item.Omitted != 0 || item.Truncated)) || (item.Original > 0 && item.Included == 0) || item.Truncated != (item.Omitted > 0)) throw new InvalidOperationException();
            if (item.Truncated && (status != "evidence.bundle.partial" || omissionReason != "evidence.omission.budget-exhausted")) throw new InvalidOperationException();
        }
    }

    private sealed record Evidence(string Id, int Original, int Included, int Omitted, bool Truncated);
}
