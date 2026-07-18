using System.Text.Json;
using Json.Schema;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ContractScribe.Tests;

public sealed class SymbolEvidenceTaxonomyContractTests
{
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
        var root = FindRepositoryRoot();
        var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(root, "schemas", "symbol-evidence-taxonomy", "v1.schema.json")));
        using var valid = JsonDocument.Parse("{\"evidenceBundleVersion\":1,\"availabilityStatus\":\"evidence.bundle.unavailable\",\"omissionReason\":\"evidence.omission.not-provided\",\"items\":[]}");
        Assert.True(schema.Evaluate(valid.RootElement).IsValid);
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
}
