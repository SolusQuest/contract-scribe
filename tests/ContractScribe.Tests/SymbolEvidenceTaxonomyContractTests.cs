using System.Text.Json;
using System.Security.Cryptography;
using Json.Schema;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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
        var entries = registry.RootElement.GetProperty("sections").EnumerateObject().SelectMany(section => section.Value.EnumerateArray()).ToArray();
        var identifiers = entries.Select(entry => entry.GetProperty("id").GetString()!).ToArray();
        Assert.Equal(identifiers.Length, identifiers.Distinct(StringComparer.Ordinal).Count());
        Assert.All(identifiers, id => Assert.Matches("^[a-z][a-z0-9-]*(\\.[a-z][a-z0-9-]*)*$", id));
        Assert.All(entries, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("definition").GetString()));
            Assert.NotEqual(JsonValueKind.Undefined, entry.GetProperty("applicability").ValueKind);
            Assert.Equal(JsonValueKind.Null, entry.GetProperty("deprecated").ValueKind);
            Assert.Equal(JsonValueKind.Null, entry.GetProperty("replacementId").ValueKind);
        });
    }

    [Fact]
    public void Manifest_ProvidesOnePublicCoverageVectorForEveryRegistryEntry()
    {
        var root = FindRepositoryRoot();
        using var registry = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "schemas", "symbol-evidence-taxonomy", "v1.registry.json")));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "tests", "fixtures", "symbol-evidence-taxonomy", "v1", "manifest.json")));
        var registered = registry.RootElement.GetProperty("sections").EnumerateObject().SelectMany(section => section.Value.EnumerateArray()).Select(entry => entry.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal);
        var coverage = manifest.RootElement.GetProperty("coverage").EnumerateArray().ToArray();
        var caseIds = coverage.Select(entry => entry.GetProperty("caseId").GetString()!).ToArray();
        var covered = coverage.Select(entry => entry.GetProperty("registryId").GetString()!).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(caseIds.Length, caseIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(registered, covered);
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
    public void PublicEvidenceVectors_ValidateSchemaAndSemanticInvariants()
    {
        var root = FindRepositoryRoot();
        using var cases = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "tests", "fixtures", "symbol-evidence-taxonomy", "v1", "evidence-cases.json")));
        foreach (var item in cases.RootElement.GetProperty("cases").EnumerateArray())
        {
            var bundle = item.GetProperty("bundle");
            var schemaValid = EvidenceSchema.Value.Evaluate(bundle).IsValid;
            var semanticValid = IsSemanticallyValid(bundle);
            Assert.Equal(item.GetProperty("valid").GetBoolean(), schemaValid && semanticValid);
        }
    }

    [Fact]
    public void ProductionProjects_DoNotReferenceRoslynOrTaxonomyRuntimeTypes()
    {
        var root = FindRepositoryRoot();
        var productionProjects = Directory.GetFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories);
        Assert.All(productionProjects, project => Assert.DoesNotContain("Microsoft.CodeAnalysis", File.ReadAllText(project), StringComparison.Ordinal));
        var productionCode = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText);
        Assert.DoesNotContain(productionCode, content => content.Contains("SymbolEvidenceTaxonomy", StringComparison.Ordinal));
    }

    [Fact]
    public void TestOnlyClassifier_MapsEveryPrimaryKindInTheSyntheticCorpus()
    {
        var compilation = CreateFixtureCompilation();
        var root = FindRepositoryRoot();
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "tests", "fixtures", "symbol-evidence-taxonomy", "v1", "manifest.json")));
        var primaryKinds = EnumerateSymbols(compilation.Assembly.GlobalNamespace)
            .Where(symbol => symbol.Locations.Any(location => location.IsInSource))
            .Select(ClassifyPrimaryKind)
            .Where(kind => kind is not null)
            .ToHashSet(StringComparer.Ordinal);
        var expected = manifest.RootElement.GetProperty("expectedPrimaryKinds").EnumerateArray().Select(value => value.GetString()!).OrderBy(value => value, StringComparer.Ordinal);
        Assert.Equal(expected, primaryKinds.OrderBy(value => value, StringComparer.Ordinal));
    }

    [Fact]
    public void TestOnlyClassifier_MapsEveryManifestTraitInTheSyntheticCorpus()
    {
        var compilation = CreateFixtureCompilation();
        var root = FindRepositoryRoot();
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "tests", "fixtures", "symbol-evidence-taxonomy", "v1", "manifest.json")));
        var actual = EnumerateSymbols(compilation.Assembly.GlobalNamespace).SelectMany(ClassifyTraits).ToHashSet(StringComparer.Ordinal);
        var expected = manifest.RootElement.GetProperty("expectedTraits").EnumerateArray().Select(value => value.GetString()!).OrderBy(value => value, StringComparer.Ordinal);
        Assert.Equal(expected, actual.OrderBy(value => value, StringComparer.Ordinal));
    }

    [Fact]
    public void TestOnlyClassifier_MapsEveryManifestRelationKindInTheSyntheticCorpus()
    {
        var compilation = CreateFixtureCompilation();
        var root = FindRepositoryRoot();
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "tests", "fixtures", "symbol-evidence-taxonomy", "v1", "manifest.json")));
        var actual = ClassifyRelations(compilation).ToHashSet(StringComparer.Ordinal);
        var expected = manifest.RootElement.GetProperty("expectedRelationKinds").EnumerateArray().Select(value => value.GetString()!).OrderBy(value => value, StringComparer.Ordinal);
        Assert.Equal(expected, actual.OrderBy(value => value, StringComparer.Ordinal));
    }

    [Fact]
    public void TestOnlyClassifier_EmitsExactTargetOrAbsenceVectors()
    {
        var root = FindRepositoryRoot();
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "tests", "fixtures", "symbol-evidence-taxonomy", "v1", "manifest.json")));
        var actual = ClassifyTargets(CreateFixtureCompilation()).ToDictionary(record => record.SymbolId, StringComparer.Ordinal);
        foreach (var vector in manifest.RootElement.GetProperty("classificationVectors").EnumerateArray())
        {
            var symbolId = vector.GetProperty("symbolId").GetString()!;
            if (vector.TryGetProperty("expectedAbsent", out var expectedAbsent))
            {
                Assert.True(expectedAbsent.GetBoolean());
                Assert.DoesNotContain(symbolId, actual.Keys);
                continue;
            }
            var record = Assert.IsType<TargetRecord>(actual[symbolId]);
            Assert.Equal(vector.GetProperty("primaryKind").GetString(), record.PrimaryKind);
            Assert.Equal(vector.GetProperty("origin").GetString(), record.Origin);
            Assert.Equal(vector.GetProperty("supportStatus").GetString(), record.SupportStatus);
        }
    }

    [Fact]
    public void TestOnlyClassifier_MapsManifestComponentKinds()
    {
        var root = FindRepositoryRoot();
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "tests", "fixtures", "symbol-evidence-taxonomy", "v1", "manifest.json")));
        var actual = ClassifyComponents(CreateFixtureCompilation()).Select(component => component.Kind).ToHashSet(StringComparer.Ordinal);
        var expected = manifest.RootElement.GetProperty("expectedComponentKinds").EnumerateArray().Select(value => value.GetString()!).OrderBy(value => value, StringComparer.Ordinal);
        Assert.Equal(expected, actual.OrderBy(value => value, StringComparer.Ordinal));
    }

    [Fact]
    public void SyntheticCorpus_CompilesAndExposesManifestSymbols()
    {
        var root = FindRepositoryRoot();
        var fixture = Path.Combine(root, "tests", "fixtures", "symbol-evidence-taxonomy", "v1");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture, "manifest.json")));
        var profile = manifest.RootElement.GetProperty("compilationProfile");
        Assert.Equal("Preview", profile.GetProperty("languageVersion").GetString());
        Assert.Equal("enable", profile.GetProperty("nullable").GetString());
        Assert.Equal("4.14.0", profile.GetProperty("roslynPackageVersion").GetString());
        Assert.Contains("Microsoft.CodeAnalysis.CSharp\" Version=\"4.14.0", File.ReadAllText(Path.Combine(root, "Directory.Packages.props")), StringComparison.Ordinal);
        var source = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(fixture, "SampleSymbols.cs")), new CSharpParseOptions(LanguageVersion.Preview, preprocessorSymbols: profile.GetProperty("preprocessorSymbols").EnumerateArray().Select(value => value.GetString()!)));
        var compilation = CSharpCompilation.Create("taxonomy-fixture", [source], [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)], new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Empty(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var ids = manifest.RootElement.GetProperty("expectedDocumentationCommentIds").EnumerateArray().Select(value => value.GetString()!).ToHashSet(StringComparer.Ordinal);
        var seen = EnumerateSymbols(compilation.Assembly.GlobalNamespace).Select(symbol => symbol.GetDocumentationCommentId()).OfType<string>().ToHashSet(StringComparer.Ordinal);
        Assert.True(ids.IsSubsetOf(seen));
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ContractScribe.slnx"))) return directory.FullName;
        }
        throw new InvalidOperationException("Repository root not found.");
    }

    private static IEnumerable<ISymbol> EnumerateSymbols(INamespaceSymbol @namespace)
    {
        foreach (var nestedNamespace in @namespace.GetNamespaceMembers()) foreach (var symbol in EnumerateSymbols(nestedNamespace)) yield return symbol;
        foreach (var type in @namespace.GetTypeMembers()) foreach (var symbol in EnumerateSymbols(type)) yield return symbol;
    }

    private static CSharpCompilation CreateFixtureCompilation()
    {
        var root = FindRepositoryRoot();
        var sourcePath = Path.Combine(root, "tests", "fixtures", "symbol-evidence-taxonomy", "v1", "SampleSymbols.cs");
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(sourcePath), new CSharpParseOptions(LanguageVersion.Preview));
        var references = AppDomain.CurrentDomain.GetAssemblies().Where(assembly => !assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location)).Select(assembly => MetadataReference.CreateFromFile(assembly.Location));
        return CSharpCompilation.Create("taxonomy-fixture", [tree], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static string? ClassifyPrimaryKind(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol { TypeKind: TypeKind.Class } => "symbol.type.class",
        INamedTypeSymbol { TypeKind: TypeKind.Struct } => "symbol.type.struct",
        INamedTypeSymbol { TypeKind: TypeKind.Interface } => "symbol.type.interface",
        INamedTypeSymbol { TypeKind: TypeKind.Enum } => "symbol.type.enum",
        INamedTypeSymbol { TypeKind: TypeKind.Delegate } => "symbol.type.delegate",
        IMethodSymbol { MethodKind: MethodKind.Constructor } => "symbol.member.constructor",
        IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator } => "symbol.member.operator",
        IMethodSymbol { MethodKind: MethodKind.Conversion } => "symbol.member.conversion",
        IMethodSymbol { MethodKind: MethodKind.Ordinary } => "symbol.member.method",
        IPropertySymbol { IsIndexer: true } => "symbol.member.indexer",
        IPropertySymbol => "symbol.member.property",
        IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } => "symbol.member.enum-member",
        IFieldSymbol => "symbol.member.field",
        IEventSymbol => "symbol.member.event",
        _ => null
    };

    private static IEnumerable<string> ClassifyTraits(ISymbol symbol)
    {
        if (symbol is INamedTypeSymbol type)
        {
            if (type.TypeParameters.Length > 0) yield return "trait.generic";
            if (type.IsRecord && type.TypeKind == TypeKind.Class) yield return "trait.record-class";
            if (type.IsRecord && type.TypeKind == TypeKind.Struct) yield return "trait.record-struct";
            if (type.IsRefLikeType) yield return "trait.ref-struct";
        }
        if (symbol is IMethodSymbol method)
        {
            if (method.TypeParameters.Length > 0) yield return "trait.generic";
            if (method.IsExtensionMethod) yield return "trait.extension";
            if (method.IsAsync) yield return "trait.async";
            if (method.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax() is MethodDeclarationSyntax declaration && declaration.DescendantNodes().OfType<YieldStatementSyntax>().Any())) yield return "trait.iterator";
        }
        if (symbol.IsStatic) yield return "trait.static";
        if (symbol.IsAbstract) yield return "trait.abstract";
        if (symbol.IsVirtual) yield return "trait.virtual";
        if (symbol.IsSealed) yield return "trait.sealed";
        if (symbol is IPropertySymbol property && property.IsRequired) yield return "trait.required";
        if (symbol is IPropertySymbol { SetMethod.IsInitOnly: true }) yield return "trait.init-only";
        if (symbol.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax() is TypeDeclarationSyntax declaration && declaration.Modifiers.Any(SyntaxKind.PartialKeyword))) yield return "trait.partial";
    }

    private static IEnumerable<string> ClassifyRelations(CSharpCompilation compilation)
    {
        var types = EnumerateSymbols(compilation.Assembly.GlobalNamespace).OfType<INamedTypeSymbol>().ToArray();
        foreach (var type in types)
        {
            if (type.AllInterfaces.SelectMany(@interface => @interface.GetMembers()).Any(member => type.FindImplementationForInterfaceMember(member) is IMethodSymbol implementation && implementation.ExplicitInterfaceImplementations.Length == 0)) yield return "relation.implicit-interface-implementation";
            if (type.AllInterfaces.SelectMany(@interface => @interface.GetMembers()).Any(member => type.GetMembers().OfType<IMethodSymbol>().Any(method => method.ExplicitInterfaceImplementations.Any(implementation => SymbolEqualityComparer.Default.Equals(implementation, member))))) yield return "relation.explicit-interface-implementation";
            if (type.TypeKind == TypeKind.Interface && type.Interfaces.Any()) yield return "relation.inherited-interface-member";
            if (type.GetMembers().OfType<IMethodSymbol>().Any(method => method.OverriddenMethod is not null)) yield return "relation.overrides";
        }
    }

    private static IEnumerable<TargetRecord> ClassifyTargets(CSharpCompilation compilation)
    {
        return EnumerateSymbols(compilation.Assembly.GlobalNamespace)
            .Where(symbol => symbol.Locations.Any(location => location.IsInSource))
            .Where(IsDocumentationTarget)
            .Select(symbol => new TargetRecord(symbol.GetDocumentationCommentId()!, ClassifyPrimaryKind(symbol)!, "origin.source", "support.supported"))
            .OrderBy(record => record.SymbolId, StringComparer.Ordinal);
    }

    private static IEnumerable<ComponentRecord> ClassifyComponents(CSharpCompilation compilation)
    {
        foreach (var method in EnumerateSymbols(compilation.Assembly.GlobalNamespace).OfType<IMethodSymbol>().Where(method => method.Locations.Any(location => location.IsInSource)))
        {
            foreach (var parameter in method.Parameters) yield return new ComponentRecord("component.parameter", method.GetDocumentationCommentId()!, $"parameter/{parameter.Ordinal}");
            if (method.MethodKind is MethodKind.Ordinary or MethodKind.UserDefinedOperator or MethodKind.Conversion) yield return new ComponentRecord("component.return", method.GetDocumentationCommentId()!, "return");
        }
        foreach (var property in EnumerateSymbols(compilation.Assembly.GlobalNamespace).OfType<IPropertySymbol>().Where(property => property.Locations.Any(location => location.IsInSource)))
        {
            yield return new ComponentRecord("component.value", property.GetDocumentationCommentId()!, "value");
            if (property.GetMethod is not null) yield return new ComponentRecord("component.accessor.get", property.GetDocumentationCommentId()!, "accessor/get");
            if (property.SetMethod is not null) yield return new ComponentRecord(property.SetMethod.IsInitOnly ? "component.accessor.init" : "component.accessor.set", property.GetDocumentationCommentId()!, property.SetMethod.IsInitOnly ? "accessor/init" : "accessor/set");
        }
        foreach (var @event in EnumerateSymbols(compilation.Assembly.GlobalNamespace).OfType<IEventSymbol>().Where(@event => @event.Locations.Any(location => location.IsInSource)))
        {
            yield return new ComponentRecord("component.accessor.add", @event.GetDocumentationCommentId()!, "accessor/add");
            yield return new ComponentRecord("component.accessor.remove", @event.GetDocumentationCommentId()!, "accessor/remove");
        }
    }

    private static bool IsDocumentationTarget(ISymbol symbol)
    {
        if (symbol.GetDocumentationCommentId() is null || ClassifyPrimaryKind(symbol) is null || symbol is IMethodSymbol { MethodKind: MethodKind.StaticConstructor }) return false;
        if (symbol is IMethodSymbol { ExplicitInterfaceImplementations.Length: > 0 }) return false;
        if (symbol is INamedTypeSymbol type) return type.DeclaredAccessibility == Accessibility.Public && ContainingTypesReachable(type);
        var containing = symbol.ContainingType;
        if (containing is null || !ContainingTypesReachable(containing)) return false;
        return symbol.DeclaredAccessibility == Accessibility.Public
            || symbol.DeclaredAccessibility is Accessibility.Protected or Accessibility.ProtectedOrInternal && containing.TypeKind == TypeKind.Class && !containing.IsSealed;
    }

    private static bool ContainingTypesReachable(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility != Accessibility.Public) return false;
        }
        return true;
    }

    private static IEnumerable<ISymbol> EnumerateSymbols(INamedTypeSymbol type)
    {
        yield return type;
        foreach (var member in type.GetMembers())
        {
            yield return member;
            if (member is INamedTypeSymbol nested) foreach (var symbol in EnumerateSymbols(nested)) yield return symbol;
        }
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

    private static bool IsSemanticallyValid(JsonElement bundle)
    {
        var status = bundle.GetProperty("availabilityStatus").GetString();
        var hasOmission = bundle.TryGetProperty("omissionReason", out var omission);
        var items = bundle.GetProperty("items").EnumerateArray().ToArray();
        if ((status == "evidence.bundle.complete" && (items.Length == 0 || hasOmission)) || (status == "evidence.bundle.partial" && (items.Length == 0 || !hasOmission)) || (status == "evidence.bundle.unavailable" && (items.Length != 0 || !hasOmission))) return false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var total = 0;
        foreach (var item in items)
        {
            var id = item.GetProperty("evidenceId").GetString()!;
            if (!ids.Add(id)) return false;
            var excerpt = item.GetProperty("excerpt").GetString()!;
            var included = System.Text.Encoding.UTF8.GetByteCount(excerpt);
            var original = item.GetProperty("originalUtf8ByteCount").GetInt32();
            var omitted = item.GetProperty("omittedUtf8ByteCount").GetInt32();
            var truncated = item.GetProperty("isTruncated").GetBoolean();
            if (included != item.GetProperty("includedUtf8ByteCount").GetInt32() || original != included + omitted || truncated != (omitted > 0) || included > 4096 || original > 0 && included == 0) return false;
            if (truncated && (status != "evidence.bundle.partial" || omission.GetString() != "evidence.omission.budget-exhausted")) return false;
            if (!truncated && !string.Equals(Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(excerpt))).ToLowerInvariant(), item.GetProperty("sha256").GetString(), StringComparison.Ordinal)) return false;
            total += included;
        }
        return total <= 32768;
    }

    private sealed record Evidence(string Id, int Original, int Included, int Omitted, bool Truncated);
    private sealed record TargetRecord(string SymbolId, string PrimaryKind, string Origin, string SupportStatus);
    private sealed record ComponentRecord(string Kind, string ParentSymbolId, string Identity);
}
