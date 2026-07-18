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
    private static readonly Lazy<JsonSchema> ManifestSchema = new(() => JsonSchema.FromText(File.ReadAllText(Path.Combine(FindRepositoryRoot(), "schemas", "symbol-evidence-taxonomy", "v1.manifest.schema.json"))));
    private static readonly Lazy<Dictionary<string, HashSet<string>>> RegistryIds = new(() => JsonDocument.Parse(File.ReadAllText(Path.Combine(FindRepositoryRoot(), "schemas", "symbol-evidence-taxonomy", "v1.registry.json"))).RootElement.GetProperty("sections").EnumerateObject().ToDictionary(section => section.Name, section => section.Value.EnumerateArray().Select(entry => entry.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal));
    private static readonly Lazy<Dictionary<string, JsonElement>> RegistryEntries = new(() => JsonDocument.Parse(File.ReadAllText(Path.Combine(FindRepositoryRoot(), "schemas", "symbol-evidence-taxonomy", "v1.registry.json"))).RootElement.GetProperty("sections").EnumerateObject().SelectMany(section => section.Value.EnumerateArray()).ToDictionary(entry => entry.GetProperty("id").GetString()!, entry => entry.Clone(), StringComparer.Ordinal));
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
            Assert.Equal(JsonValueKind.Array, entry.GetProperty("applicability").ValueKind);
            Assert.Equal(JsonValueKind.Array, entry.GetProperty("recordTypes").ValueKind);
            Assert.NotEmpty(entry.GetProperty("recordTypes").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, entry.GetProperty("deprecated").ValueKind);
            Assert.Equal(JsonValueKind.Null, entry.GetProperty("replacementId").ValueKind);
        });
        var synthesized = registry.RootElement.GetProperty("sections").GetProperty("componentKinds").EnumerateArray().Where(entry => entry.GetProperty("id").GetString()!.StartsWith("component.synthesized.", StringComparison.Ordinal));
        Assert.All(synthesized, entry =>
        {
            Assert.Equal("origin.compiler-synthesized", entry.GetProperty("requiredOrigin").GetString());
            Assert.Equal("skip.not-applicable.synthesized-non-target", entry.GetProperty("requiredSkip").GetString());
            Assert.Equal(new[] { "support.not-applicable" }, entry.GetProperty("allowedSupportStatuses").EnumerateArray().Select(value => value.GetString()));
        });
        var statuses = registry.RootElement.GetProperty("sections").GetProperty("supportStatuses").EnumerateArray().ToDictionary(entry => entry.GetProperty("id").GetString()!, StringComparer.Ordinal);
        Assert.Equal(new[] { "TargetClassification", "ComponentClassification", "UnresolvedClassification" }, statuses["support.unavailable-context"].GetProperty("recordTypes").EnumerateArray().Select(value => value.GetString()));
        Assert.DoesNotContain("UnresolvedClassification", statuses["support.supported"].GetProperty("recordTypes").EnumerateArray().Select(value => value.GetString()));
        var skips = registry.RootElement.GetProperty("sections").GetProperty("skipReasons").EnumerateArray().ToDictionary(entry => entry.GetProperty("id").GetString()!, StringComparer.Ordinal);
        Assert.Equal(1, skips["skip.unavailable.documentation-comment-id"].GetProperty("precedence").GetInt32());
        Assert.Equal(8, skips["skip.not-applicable.non-documentation-component"].GetProperty("precedence").GetInt32());
        Assert.Equal(new[] { "UnresolvedClassification" }, skips["skip.unavailable.documentation-comment-id"].GetProperty("recordTypes").EnumerateArray().Select(value => value.GetString()));
        Assert.DoesNotContain(entries, entry => entry.GetRawText().Contains("registry-defined", StringComparison.Ordinal));
    }

    [Fact]
    public void Manifest_UsesPinnedProfileAndClosedClassificationRecordShapes()
    {
        var root = FindRepositoryRoot();
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "tests", "fixtures", "symbol-evidence-taxonomy", "v1", "manifest.json")));
        Assert.True(ManifestSchema.Value.Evaluate(manifest.RootElement).IsValid);
        var profile = manifest.RootElement.GetProperty("compilationProfile");
        Assert.Equal("10.0.2", profile.GetProperty("referencePackVersion").GetString());
        foreach (var record in manifest.RootElement.GetProperty("classificationRecords").EnumerateArray())
        {
            Assert.True(IsValidClassificationRecord(record));
        }
    }

    [Fact]
    public void ClassificationRecords_RejectForbiddenStatusOriginAndSkipPairs()
    {
        using var knownUnsupported = JsonDocument.Parse("{\"recordType\":\"TargetClassification\",\"symbolRef\":{\"compilationContextRef\":\"synthetic.v1\",\"documentationCommentId\":\"T:Example\"},\"primaryKind\":\"symbol.type.class\",\"traits\":[],\"origin\":\"origin.source\",\"supportStatus\":\"support.unsupported\",\"skipReason\":\"skip.unsupported.symbol-kind\"}");
        using var unknownSupported = JsonDocument.Parse("{\"recordType\":\"TargetClassification\",\"symbolRef\":{\"compilationContextRef\":\"synthetic.v1\",\"documentationCommentId\":\"T:Example\"},\"primaryKind\":\"symbol.unknown\",\"traits\":[],\"origin\":\"origin.source\",\"supportStatus\":\"support.supported\"}");
        using var unknownOrigin = JsonDocument.Parse("{\"recordType\":\"UnresolvedClassification\",\"compilationContextRef\":\"synthetic.v1\",\"origin\":\"origin.unknown\",\"supportStatus\":\"support.unavailable-context\",\"skipReason\":\"skip.unavailable.documentation-comment-id\",\"candidateLocator\":{\"synthetic\":{\"fixtureId\":\"x\"}}}");
        Assert.False(IsValidClassificationRecord(knownUnsupported.RootElement));
        Assert.False(IsValidClassificationRecord(unknownSupported.RootElement));
        Assert.False(IsValidClassificationRecord(unknownOrigin.RootElement));
    }

    [Fact]
    public void CanonicalRecords_RespectRegisteredComponentAndRelationDomains()
    {
        var root = FindRepositoryRoot();
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "tests", "fixtures", "symbol-evidence-taxonomy", "v1", "manifest.json")));
        var kindsByDocumentationId = EnumerateSymbols(CreateFixtureCompilation().Assembly.GlobalNamespace)
            .Select(symbol => (Id: symbol.GetDocumentationCommentId(), Kind: ClassifyPrimaryKind(symbol)))
            .Where(value => value.Id is not null && value.Kind is not null)
            .ToDictionary(value => value.Id!, value => value.Kind!, StringComparer.Ordinal);
        foreach (var record in manifest.RootElement.GetProperty("classificationRecords").EnumerateArray())
        {
            if (record.GetProperty("recordType").GetString() == "ComponentClassification")
            {
                var parent = record.GetProperty("parentSymbolRef").GetProperty("documentationCommentId").GetString()!;
                var kind = record.GetProperty("componentKind").GetString()!;
                Assert.Contains(kindsByDocumentationId[parent], RegistryEntries.Value[kind].GetProperty("parentKinds").EnumerateArray().Select(value => value.GetString()));
            }
            if (record.GetProperty("recordType").GetString() == "RelationObservation")
            {
                var relation = RegistryEntries.Value[record.GetProperty("relationKind").GetString()!];
                var source = record.GetProperty("sourceSymbolRef").GetProperty("documentationCommentId").GetString()!;
                var target = record.GetProperty("targetSymbolRef").GetProperty("documentationCommentId").GetString()!;
                Assert.Contains(kindsByDocumentationId.GetValueOrDefault(source, "symbol.member.method"), relation.GetProperty("sourceDomain").EnumerateArray().Select(value => value.GetString()));
                Assert.Contains(kindsByDocumentationId[target], relation.GetProperty("targetDomain").EnumerateArray().Select(value => value.GetString()));
            }
        }
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
    public void EvidenceBundle_RejectsBudgetExhaustedOutsidePartialState()
    {
        using var unavailable = JsonDocument.Parse("{\"evidenceBundleVersion\":1,\"availabilityStatus\":\"evidence.bundle.unavailable\",\"omissionReason\":\"evidence.omission.budget-exhausted\",\"items\":[]}");
        Assert.False(IsSemanticallyValid(unavailable.RootElement));
    }

    [Fact]
    public void EvidenceBundle_EnforcesExactItemAndUtf8ByteBoundaries()
    {
        Assert.True(IsSemanticallyValid(CreateBundle(Enumerable.Range(0, 32).Select(index => CreateEvidenceItem($"evidence.a{index:D2}", "x")))));
        Assert.False(IsSemanticallyValid(CreateBundle(Enumerable.Range(0, 33).Select(index => CreateEvidenceItem($"evidence.a{index:D2}", "x")))));
        Assert.True(IsSemanticallyValid(CreateBundle([CreateEvidenceItem("evidence.a", new string('x', 4095))])));
        Assert.True(IsSemanticallyValid(CreateBundle([CreateEvidenceItem("evidence.a", new string('x', 4096))])));
        Assert.False(IsSemanticallyValid(CreateBundle([CreateEvidenceItem("evidence.a", new string('x', 4097))])));
        Assert.True(IsSemanticallyValid(CreateBundle(Enumerable.Range(0, 7).Select(index => CreateEvidenceItem($"evidence.a{index}", new string('x', 4096))).Append(CreateEvidenceItem("evidence.z", new string('x', 4095))))));
        Assert.True(IsSemanticallyValid(CreateBundle(Enumerable.Range(0, 8).Select(index => CreateEvidenceItem($"evidence.a{index}", new string('x', 4096))))));
        Assert.False(IsSemanticallyValid(CreateBundle(Enumerable.Range(0, 8).Select(index => CreateEvidenceItem($"evidence.a{index}", new string('x', 4096))).Append(CreateEvidenceItem("evidence.z", "x")))));
        Assert.True(IsSemanticallyValid(CreateBundle([CreateEvidenceItem("evidence.a", "é")])));
        Assert.True(IsLexicalRepositoryPath("./src//Contract.cs"));
        Assert.True(IsLexicalRepositoryPath("src\\Contract.cs"));
        Assert.False(IsLexicalRepositoryPath("C:Contract.cs"));
        Assert.False(IsLexicalRepositoryPath("src/\0Contract.cs"));
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
            var originals = item.TryGetProperty("originalEvidenceTexts", out var texts)
                ? texts.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.GetString()!, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
            var semanticValid = IsSemanticallyValid(bundle, originals);
            Assert.True(item.GetProperty("valid").GetBoolean() == (schemaValid && semanticValid), $"{item.GetProperty("caseId").GetString()}: schema={schemaValid}, semantic={semanticValid}");
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
        Assert.DoesNotContain(productionCode, content => content.Contains("PolicySelector", StringComparison.Ordinal));
        Assert.DoesNotContain(productionCode, content => content.Contains("AuditResult", StringComparison.Ordinal));
        Assert.DoesNotContain(productionCode, content => content.Contains("EvidenceDiscovery", StringComparison.Ordinal));
        Assert.DoesNotContain(productionCode, content => content.Contains("EvidenceRanking", StringComparison.Ordinal));
        Assert.DoesNotContain(productionCode, content => content.Contains("ProposalGenerator", StringComparison.Ordinal));
    }

    [Fact]
    public void TestOnlyClassifier_EmitsDeterministicCanonicalClassificationRecords()
    {
        var root = FindRepositoryRoot();
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "tests", "fixtures", "symbol-evidence-taxonomy", "v1", "manifest.json")));
        var first = SerializeCanonicalRecords(ClassifyCanonicalRecords(CreateFixtureCompilation(), manifest.RootElement));
        var second = SerializeCanonicalRecords(ClassifyCanonicalRecords(CreateFixtureCompilation(), manifest.RootElement));
        Assert.Equal(first, second);
        var expected = manifest.RootElement.GetProperty("classificationRecords").EnumerateArray()
            .Select(record => JsonSerializer.Deserialize<Dictionary<string, object>>(record.GetRawText())!)
            .OrderBy(record => record["recordType"].ToString(), StringComparer.Ordinal).ThenBy(record => JsonSerializer.Serialize(record), StringComparer.Ordinal).ToArray();
        Assert.Equal(SerializeCanonicalRecords(expected), first);
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
        var compilation = CreateFixtureCompilation();
        Assert.Empty(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
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
        var fixture = Path.Combine(root, "tests", "fixtures", "symbol-evidence-taxonomy", "v1");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture, "manifest.json")));
        var profile = manifest.RootElement.GetProperty("compilationProfile");
        var languageVersion = Enum.Parse<LanguageVersion>(profile.GetProperty("languageVersion").GetString()!, ignoreCase: false);
        var sourceEncoding = profile.GetProperty("sourceEncoding").GetString();
        if (sourceEncoding != "utf-8" || profile.GetProperty("targetFramework").GetString() != "net10.0" || profile.GetProperty("nullable").GetString() != "enable") throw new InvalidOperationException("The fixture profile must pin V1 compilation settings.");
        var parseOptions = new CSharpParseOptions(languageVersion, preprocessorSymbols: profile.GetProperty("preprocessorSymbols").EnumerateArray().Select(value => value.GetString()!));
        var trees = manifest.RootElement.GetProperty("sources").EnumerateArray().Select(value => CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(fixture, value.GetString()!)), parseOptions, encoding: System.Text.Encoding.UTF8)).ToArray();
        var referencePackVersion = profile.GetProperty("referencePackVersion").GetString()!;
        var dotnetRoot = Directory.GetParent(typeof(object).Assembly.Location)?.Parent?.Parent?.Parent?.FullName ?? throw new InvalidOperationException("The dotnet root is unavailable.");
        var referenceDirectory = Path.Combine(dotnetRoot, "packs", "Microsoft.NETCore.App.Ref", referencePackVersion, "ref", profile.GetProperty("targetFramework").GetString()!);
        if (!Directory.Exists(referenceDirectory)) throw new InvalidOperationException($"Pinned reference pack is unavailable: {referenceDirectory}");
        var references = Directory.GetFiles(referenceDirectory, "*.dll", SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.Ordinal).Select(path => MetadataReference.CreateFromFile(path));
        return CSharpCompilation.Create("taxonomy-fixture", trees, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable, warningLevel: profile.GetProperty("warningLevel").GetInt32()));
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
        IMethodSymbol { MethodKind: MethodKind.Destructor } => "symbol.member.method",
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
            .Where(symbol => !symbol.IsImplicitlyDeclared)
            .Where(IsDocumentationTarget)
            .Select(symbol => new TargetRecord(symbol.GetDocumentationCommentId()!, ClassifyPrimaryKind(symbol)!, "origin.source", "support.supported"))
            .OrderBy(record => record.SymbolId, StringComparer.Ordinal);
    }

    private static IEnumerable<ComponentRecord> ClassifyComponents(CSharpCompilation compilation)
    {
        foreach (var method in EnumerateSymbols(compilation.Assembly.GlobalNamespace).OfType<IMethodSymbol>().Where(method => !method.IsImplicitlyDeclared && IsDocumentationTarget(method)))
        {
            foreach (var parameter in method.Parameters) yield return new ComponentRecord("component.parameter", method.GetDocumentationCommentId()!, $"parameter/{parameter.Ordinal}");
            if (method.MethodKind is MethodKind.Ordinary or MethodKind.UserDefinedOperator or MethodKind.Conversion) yield return new ComponentRecord("component.return", method.GetDocumentationCommentId()!, "return");
        }
        foreach (var property in EnumerateSymbols(compilation.Assembly.GlobalNamespace).OfType<IPropertySymbol>().Where(property => !property.IsImplicitlyDeclared && IsDocumentationTarget(property)))
        {
            yield return new ComponentRecord("component.value", property.GetDocumentationCommentId()!, "value");
            if (property.GetMethod is not null) yield return new ComponentRecord("component.accessor.get", property.GetDocumentationCommentId()!, "accessor/get", SupportStatus: "support.not-applicable", SkipReason: "skip.not-applicable.non-documentation-component");
            if (property.SetMethod is not null) yield return new ComponentRecord(property.SetMethod.IsInitOnly ? "component.accessor.init" : "component.accessor.set", property.GetDocumentationCommentId()!, property.SetMethod.IsInitOnly ? "accessor/init" : "accessor/set", SupportStatus: "support.not-applicable", SkipReason: "skip.not-applicable.non-documentation-component");
        }
        foreach (var @event in EnumerateSymbols(compilation.Assembly.GlobalNamespace).OfType<IEventSymbol>().Where(@event => !@event.IsImplicitlyDeclared && IsDocumentationTarget(@event)))
        {
            yield return new ComponentRecord("component.accessor.add", @event.GetDocumentationCommentId()!, "accessor/add", SupportStatus: "support.not-applicable", SkipReason: "skip.not-applicable.non-documentation-component");
            yield return new ComponentRecord("component.accessor.remove", @event.GetDocumentationCommentId()!, "accessor/remove", SupportStatus: "support.not-applicable", SkipReason: "skip.not-applicable.non-documentation-component");
        }
        foreach (var type in EnumerateSymbols(compilation.Assembly.GlobalNamespace).OfType<INamedTypeSymbol>().Where(type => !type.IsImplicitlyDeclared && IsDocumentationTarget(type)))
        {
            var parentId = type.GetDocumentationCommentId()!;
            if (type.IsRecord)
                foreach (var positional in type.GetMembers().OfType<IPropertySymbol>().Where(property => property.IsImplicitlyDeclared).Select((property, ordinal) => (property, ordinal)))
                    yield return Synthesized("component.synthesized.record-positional-property", parentId, $"synthesized/record-positional-property/{positional.ordinal}");
            foreach (var constructor in type.InstanceConstructors.Where(constructor => constructor.IsImplicitlyDeclared && (type.TypeKind is TypeKind.Class or TypeKind.Struct)))
                yield return Synthesized(type.IsRecord && constructor.Parameters.Length == 1 && SymbolEqualityComparer.Default.Equals(constructor.Parameters[0].Type, type)
                    ? "component.synthesized.record-copy-constructor" : "component.synthesized.implicit-constructor", parentId,
                    type.IsRecord && constructor.Parameters.Length == 1 && SymbolEqualityComparer.Default.Equals(constructor.Parameters[0].Type, type) ? "synthesized/record-copy-constructor" : "synthesized/implicit-constructor");
            if (type.TypeKind == TypeKind.Delegate)
                foreach (var method in type.GetMembers().OfType<IMethodSymbol>().Where(method => method.IsImplicitlyDeclared && method.Name is "Invoke" or "BeginInvoke" or "EndInvoke"))
                    yield return Synthesized($"component.synthesized.delegate-{method.Name.Replace("Invoke", "invoke", StringComparison.Ordinal).Replace("Begininvoke", "begin-invoke", StringComparison.Ordinal).Replace("Endinvoke", "end-invoke", StringComparison.Ordinal)}", parentId, $"synthesized/delegate-{method.Name.Replace("Invoke", "invoke", StringComparison.Ordinal).Replace("Begininvoke", "begin-invoke", StringComparison.Ordinal).Replace("Endinvoke", "end-invoke", StringComparison.Ordinal)}");
        }
    }

    private static ComponentRecord Synthesized(string kind, string parentId, string identity) => new(kind, parentId, identity, "origin.compiler-synthesized", "support.not-applicable", "skip.not-applicable.synthesized-non-target");

    private static IReadOnlyList<Dictionary<string, object>> ClassifyCanonicalRecords(CSharpCompilation compilation, JsonElement manifest)
    {
        var context = manifest.GetProperty("compilationContextRef").GetString()!;
        var records = new List<Dictionary<string, object>>();
        foreach (var target in ClassifyTargets(compilation))
        {
            var symbol = EnumerateSymbols(compilation.Assembly.GlobalNamespace).Single(candidate => candidate.GetDocumentationCommentId() == target.SymbolId);
            records.Add(new Dictionary<string, object>
            {
                ["recordType"] = "TargetClassification",
                ["symbolRef"] = SymbolRef(context, target.SymbolId),
                ["primaryKind"] = target.PrimaryKind,
                ["traits"] = ClassifyTraits(symbol).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                ["origin"] = target.Origin,
                ["supportStatus"] = target.SupportStatus
            });
        }
        foreach (var component in ClassifyComponents(compilation).OrderBy(component => component.ParentSymbolId, StringComparer.Ordinal).ThenBy(component => component.Kind, StringComparer.Ordinal).ThenBy(component => component.Identity, StringComparer.Ordinal))
        {
            records.Add(new Dictionary<string, object>
            {
                ["recordType"] = "ComponentClassification",
                ["parentSymbolRef"] = SymbolRef(context, component.ParentSymbolId),
                ["componentKind"] = component.Kind,
                ["identity"] = component.Identity,
                ["origin"] = component.Origin,
                ["supportStatus"] = component.SupportStatus
            });
            if (component.SkipReason is not null) records[^1]["skipReason"] = component.SkipReason;
        }
        foreach (var relation in ClassifyRelationRecords(compilation, context)) records.Add(relation);
        foreach (var unresolved in manifest.GetProperty("classificationRecords").EnumerateArray().Where(record => record.GetProperty("recordType").GetString() == "UnresolvedClassification"))
        {
            records.Add(JsonSerializer.Deserialize<Dictionary<string, object>>(unresolved.GetRawText())!);
        }
        return records.OrderBy(record => record["recordType"].ToString(), StringComparer.Ordinal).ThenBy(record => JsonSerializer.Serialize(record), StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<Dictionary<string, object>> ClassifyRelationRecords(CSharpCompilation compilation, string context)
    {
        foreach (var type in EnumerateSymbols(compilation.Assembly.GlobalNamespace).OfType<INamedTypeSymbol>())
        {
            foreach (var method in type.GetMembers().OfType<IMethodSymbol>().Where(method => !method.IsImplicitlyDeclared))
            {
                if (method.OverriddenMethod is { } overridden && method.GetDocumentationCommentId() is { } source && overridden.GetDocumentationCommentId() is { } target) yield return Relation("relation.overrides", source, target, context);
                foreach (var implemented in method.ExplicitInterfaceImplementations)
                    if (method.GetDocumentationCommentId() is { } explicitSource && implemented.GetDocumentationCommentId() is { } explicitTarget) yield return Relation("relation.explicit-interface-implementation", explicitSource, explicitTarget, context);
            }
            if (type.TypeKind == TypeKind.Interface)
                foreach (var inherited in type.Interfaces.SelectMany(@interface => @interface.GetMembers()).OfType<IMethodSymbol>())
                    if (type.GetDocumentationCommentId() is { } source && inherited.GetDocumentationCommentId() is { } target) yield return Relation("relation.inherited-interface-member", source, target, context);
            foreach (var interfaceMember in type.AllInterfaces.SelectMany(@interface => @interface.GetMembers()).OfType<IMethodSymbol>())
                if (interfaceMember.Locations.Any(location => location.IsInSource) && type.FindImplementationForInterfaceMember(interfaceMember) is IMethodSymbol implementation && !implementation.IsImplicitlyDeclared && implementation.ExplicitInterfaceImplementations.Length == 0 && implementation.GetDocumentationCommentId() is { } source && interfaceMember.GetDocumentationCommentId() is { } target) yield return Relation("relation.implicit-interface-implementation", source, target, context);
        }
    }

    private static Dictionary<string, object> Relation(string kind, string source, string target, string context) => new()
    {
        ["recordType"] = "RelationObservation",
        ["relationKind"] = kind,
        ["sourceSymbolRef"] = SymbolRef(context, source),
        ["targetSymbolRef"] = SymbolRef(context, target)
    };

    private static Dictionary<string, object> SymbolRef(string context, string documentationCommentId) => new()
    {
        ["compilationContextRef"] = context,
        ["documentationCommentId"] = documentationCommentId
    };

    private static string SerializeCanonicalRecords(IReadOnlyList<Dictionary<string, object>> records) => JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = false });

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

    private static bool IsSemanticallyValid(JsonElement bundle, IReadOnlyDictionary<string, string>? originalEvidenceTexts = null)
    {
        var status = bundle.GetProperty("availabilityStatus").GetString();
        var hasOmission = bundle.TryGetProperty("omissionReason", out var omission);
        var items = bundle.GetProperty("items").EnumerateArray().ToArray();
        if (items.Length > 32) return false;
        if (!Known("bundleAvailabilityStatuses", status) || hasOmission && !Known("bundleOmissionReasons", omission.GetString())) return false;
        if (hasOmission && omission.GetString() == "evidence.omission.budget-exhausted" && status != "evidence.bundle.partial") return false;
        if ((status == "evidence.bundle.complete" && (items.Length == 0 || hasOmission)) || (status == "evidence.bundle.partial" && (items.Length == 0 || !hasOmission)) || (status == "evidence.bundle.unavailable" && (items.Length != 0 || !hasOmission))) return false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var total = 0;
        string? previousId = null;
        foreach (var item in items)
        {
            var id = item.GetProperty("evidenceId").GetString()!;
            if (!ids.Add(id) || previousId is not null && string.CompareOrdinal(previousId, id) >= 0) return false;
            previousId = id;
            if (!Known("evidenceKinds", item.GetProperty("kind").GetString()) || !Known("evidenceRelations", item.GetProperty("relation").GetString())) return false;
            if (!IsValidLocator(item.GetProperty("locator"), originalEvidenceTexts)) return false;
            var excerpt = item.GetProperty("excerpt").GetString()!;
            var included = System.Text.Encoding.UTF8.GetByteCount(excerpt);
            var original = item.GetProperty("originalUtf8ByteCount").GetInt32();
            var omitted = item.GetProperty("omittedUtf8ByteCount").GetInt32();
            var truncated = item.GetProperty("isTruncated").GetBoolean();
            if (included != item.GetProperty("includedUtf8ByteCount").GetInt32() || original != included + omitted || truncated != (omitted > 0) || included > 4096 || original > 0 && included == 0) return false;
            if (truncated && (status != "evidence.bundle.partial" || omission.GetString() != "evidence.omission.budget-exhausted")) return false;
            var originalText = originalEvidenceTexts is not null && originalEvidenceTexts.TryGetValue(id, out var text) ? text : excerpt;
            if (!originalText.StartsWith(excerpt, StringComparison.Ordinal) || originalText.Length > excerpt.Length && excerpt.Length > 0 && char.IsHighSurrogate(excerpt[^1])) return false;
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(originalText))).ToLowerInvariant(), item.GetProperty("sha256").GetString(), StringComparison.Ordinal)) return false;
            if (System.Text.Encoding.UTF8.GetByteCount(originalText) != original) return false;
            total += included;
        }
        return total <= 32768;
    }

    private static JsonElement CreateBundle(IEnumerable<Dictionary<string, object>> items)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["evidenceBundleVersion"] = 1,
            ["availabilityStatus"] = "evidence.bundle.complete",
            ["items"] = items.ToArray()
        }));
        return document.RootElement.Clone();
    }

    private static Dictionary<string, object> CreateEvidenceItem(string id, string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        return new Dictionary<string, object>
        {
            ["evidenceId"] = id,
            ["subject"] = new Dictionary<string, object> { ["compilationContextRef"] = "synthetic.v1", ["documentationCommentId"] = "T:TaxonomyFixtures.IContract" },
            ["kind"] = "evidence.source.declaration",
            ["relation"] = "evidence.declares",
            ["excerpt"] = text,
            ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            ["originalUtf8ByteCount"] = bytes.Length,
            ["includedUtf8ByteCount"] = bytes.Length,
            ["omittedUtf8ByteCount"] = 0,
            ["isTruncated"] = false,
            ["locator"] = new Dictionary<string, object> { ["repository"] = new Dictionary<string, object> { ["path"] = "src/Contract.cs" } }
        };
    }

    private static bool Known(string section, string? id) => id is not null && RegistryIds.Value[section].Contains(id);

    private static bool IsValidClassificationRecord(JsonElement record)
    {
        if (record.ValueKind != JsonValueKind.Object || !record.TryGetProperty("recordType", out var type)) return false;
        var recordType = type.GetString();
        return recordType switch
        {
            "TargetClassification" => HasOnlyProperties(record, "recordType", "symbolRef", "primaryKind", "traits", "origin", "supportStatus", "skipReason") && record.TryGetProperty("symbolRef", out var target) && IsSymbolRef(target)
                && Known("primaryKinds", record.GetProperty("primaryKind").GetString())
                && record.GetProperty("traits").EnumerateArray().All(value => Known("traits", value.GetString()))
                && IsValidStatusAndSkip(record, "TargetClassification", record.GetProperty("primaryKind").GetString()!),
            "ComponentClassification" => HasOnlyProperties(record, "recordType", "parentSymbolRef", "componentKind", "identity", "origin", "supportStatus", "skipReason") && record.TryGetProperty("parentSymbolRef", out var parent) && IsSymbolRef(parent)
                && Known("componentKinds", record.GetProperty("componentKind").GetString())
                && IsValidComponentIdentity(record.GetProperty("componentKind").GetString()!, record.GetProperty("identity").GetString())
                && IsValidStatusAndSkip(record, "ComponentClassification", record.GetProperty("componentKind").GetString()!),
            "RelationObservation" => HasOnlyProperties(record, "recordType", "relationKind", "sourceSymbolRef", "targetSymbolRef") && Known("relationKinds", record.GetProperty("relationKind").GetString())
                && IsSymbolRef(record.GetProperty("sourceSymbolRef")) && IsSymbolRef(record.GetProperty("targetSymbolRef")),
            "UnresolvedClassification" => HasOnlyProperties(record, "recordType", "compilationContextRef", "origin", "supportStatus", "skipReason", "candidateLocator") && record.GetProperty("supportStatus").GetString() == "support.unavailable-context"
                && Known("origins", record.GetProperty("origin").GetString()) && Known("skipReasons", record.GetProperty("skipReason").GetString())
                && (record.GetProperty("origin").GetString() != "origin.unknown" || record.GetProperty("skipReason").GetString() is "skip.unavailable.generated-provenance" or "skip.unavailable.semantic-context")
                && IsValidCandidateLocator(record.GetProperty("candidateLocator")),
            _ => false
        };
    }

    private static bool IsValidStatusAndSkip(JsonElement record, string recordType, string classifiedId)
    {
        var status = record.GetProperty("supportStatus").GetString();
        var origin = record.GetProperty("origin").GetString();
        if (!Known("supportStatuses", status) || !Known("origins", origin) || !AllowsRecord(RegistryEntries.Value[status!], recordType) || !AllowsRecord(RegistryEntries.Value[origin!], recordType)) return false;
        if ((classifiedId == "symbol.unknown" || classifiedId == "component.unknown") != (status == "support.unsupported")) return false;
        if (RegistryEntries.Value[classifiedId].TryGetProperty("allowedSupportStatuses", out var statuses) && !statuses.EnumerateArray().Select(value => value.GetString()).Contains(status, StringComparer.Ordinal)) return false;
        if (RegistryEntries.Value[classifiedId].TryGetProperty("requiredOrigin", out var requiredOrigin) && origin != requiredOrigin.GetString()) return false;
        if (status == "support.supported") return !record.TryGetProperty("skipReason", out _);
        if (!record.TryGetProperty("skipReason", out var skip) || !Known("skipReasons", skip.GetString()) || !AllowsRecord(RegistryEntries.Value[skip.GetString()!], recordType)) return false;
        if (origin == "origin.unknown" && (status != "support.unavailable-context" || skip.GetString() is not ("skip.unavailable.generated-provenance" or "skip.unavailable.semantic-context"))) return false;
        if (origin == "origin.mixed" && (status != "support.ambiguous" || skip.GetString() != "skip.ambiguous.mixed-origin")) return false;
        if (RegistryEntries.Value[classifiedId].TryGetProperty("requiredSkip", out var requiredSkip) && skip.GetString() != requiredSkip.GetString()) return false;
        return !RegistryEntries.Value[skip.GetString()!].TryGetProperty("allowedSupportStatuses", out var allowed) || allowed.EnumerateArray().Select(value => value.GetString()).Contains(status, StringComparer.Ordinal);
    }

    private static bool AllowsRecord(JsonElement entry, string recordType) => entry.GetProperty("recordTypes").EnumerateArray().Select(value => value.GetString()).Contains(recordType, StringComparer.Ordinal);

    private static bool HasOnlyProperties(JsonElement value, params string[] properties) => value.EnumerateObject().All(property => properties.Contains(property.Name, StringComparer.Ordinal));

    private static bool IsValidComponentIdentity(string kind, string? identity) => identity is not null && kind switch
    {
        "component.parameter" => System.Text.RegularExpressions.Regex.IsMatch(identity, "^parameter/[0-9]+$"),
        "component.type-parameter" => System.Text.RegularExpressions.Regex.IsMatch(identity, "^type-parameter/[0-9]+$"),
        "component.return" => identity == "return",
        "component.value" => identity == "value",
        "component.accessor.get" => identity == "accessor/get",
        "component.accessor.set" => identity == "accessor/set",
        "component.accessor.init" => identity == "accessor/init",
        "component.accessor.add" => identity == "accessor/add",
        "component.accessor.remove" => identity == "accessor/remove",
        "component.backing-field" => identity == "backing-field",
        "component.synthesized.record-positional-property" => System.Text.RegularExpressions.Regex.IsMatch(identity, "^synthesized/record-positional-property/[0-9]+$"),
        "component.synthesized.implicit-constructor" => identity == "synthesized/implicit-constructor",
        "component.synthesized.record-copy-constructor" => identity == "synthesized/record-copy-constructor",
        "component.synthesized.delegate-invoke" => identity == "synthesized/delegate-invoke",
        "component.synthesized.delegate-begin-invoke" => identity == "synthesized/delegate-begin-invoke",
        "component.synthesized.delegate-end-invoke" => identity == "synthesized/delegate-end-invoke",
        "component.unknown" => System.Text.RegularExpressions.Regex.IsMatch(identity, "^unknown/[0-9]+$"),
        _ => false
    };

    private static bool IsSymbolRef(JsonElement symbolRef) => symbolRef.ValueKind == JsonValueKind.Object
        && symbolRef.TryGetProperty("compilationContextRef", out var context) && System.Text.RegularExpressions.Regex.IsMatch(context.GetString() ?? string.Empty, "^[a-z0-9][a-z0-9._-]{0,127}$")
        && symbolRef.TryGetProperty("documentationCommentId", out var id) && !string.IsNullOrEmpty(id.GetString());

    private static bool IsValidCandidateLocator(JsonElement locator)
    {
        var kinds = new[] { "repository", "generatedSource", "synthetic" }.Where(name => locator.TryGetProperty(name, out _)).ToArray();
        return kinds.Length == 1 && kinds[0] switch
        {
            "repository" => IsValidRepositoryLocator(locator.GetProperty("repository")),
            "generatedSource" => locator.GetProperty("generatedSource").TryGetProperty("generatorId", out var generator) && locator.GetProperty("generatedSource").TryGetProperty("hintNameId", out var hint)
                && System.Text.RegularExpressions.Regex.IsMatch(generator.GetString() ?? string.Empty, "^[a-z0-9][a-z0-9._-]{0,127}$") && System.Text.RegularExpressions.Regex.IsMatch(hint.GetString() ?? string.Empty, "^[a-z0-9][a-z0-9._-]{0,127}$")
                && (!locator.GetProperty("generatedSource").TryGetProperty("span", out var span) || span.GetProperty("start").GetInt32() <= span.GetProperty("end").GetInt32()),
            "synthetic" => locator.GetProperty("synthetic").TryGetProperty("fixtureId", out var fixture) && System.Text.RegularExpressions.Regex.IsMatch(fixture.GetString() ?? string.Empty, "^[a-z0-9][a-z0-9._-]{0,127}$"),
            _ => false
        };
    }

    private static bool IsValidLocator(JsonElement locator, IReadOnlyDictionary<string, string>? originalEvidenceTexts = null)
    {
        var kinds = new[] { "repository", "metadata", "synthetic" }.Where(name => locator.TryGetProperty(name, out _)).ToArray();
        if (kinds.Length != 1) return false;
        return kinds[0] switch
        {
            "repository" => IsValidRepositoryLocator(locator.GetProperty("repository"), originalEvidenceTexts),
            "metadata" => locator.GetProperty("metadata").GetProperty("assemblyIdentity").GetString() is { } assembly && System.Text.RegularExpressions.Regex.IsMatch(assembly, "^[a-z0-9][a-z0-9._-]{0,127}$"),
            "synthetic" => locator.GetProperty("synthetic").GetProperty("fixtureId").GetString() is { } fixture && System.Text.RegularExpressions.Regex.IsMatch(fixture, "^[a-z0-9][a-z0-9._-]{0,127}$"),
            _ => false
        };
    }

    private static bool IsLexicalRepositoryPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('\0') || path.StartsWith('/') || path.StartsWith('\\') || System.Text.RegularExpressions.Regex.IsMatch(path, "^[A-Za-z]:")) return false;
        var normalized = path.Replace('\\', '/').Split('/').Where(segment => segment is not "" and not ".").ToArray();
        return normalized.Length > 0 && normalized.All(segment => segment != "..");
    }

    private static bool IsValidRepositoryLocator(JsonElement repository, IReadOnlyDictionary<string, string>? originalEvidenceTexts = null)
    {
        if (!IsLexicalRepositoryPath(repository.GetProperty("path").GetString())) return false;
        if (!repository.TryGetProperty("span", out var span)) return true;
        var start = span.GetProperty("start").GetInt32();
        var end = span.GetProperty("end").GetInt32();
        if (start > end) return false;
        return originalEvidenceTexts is not null && originalEvidenceTexts.TryGetValue($"path:{repository.GetProperty("path").GetString()}", out var payload) && end <= payload.Length;
    }

    private sealed record Evidence(string Id, int Original, int Included, int Omitted, bool Truncated);
    private sealed record TargetRecord(string SymbolId, string PrimaryKind, string Origin, string SupportStatus);
    private sealed record ComponentRecord(string Kind, string ParentSymbolId, string Identity, string Origin = "origin.source", string SupportStatus = "support.supported", string? SkipReason = null);
}
