using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ContractScribe.Tests;

public sealed class M1TargetObservationDecisionTests
{
    private const string BaselineCommit = "beada966b3c06e1b823e488472a9f515b87b0760";
    private static readonly string Root = FindRepositoryRoot();
    private static readonly string VectorPath = Path.Join(Root, "tests", "fixtures", "m1-target-observation", "adr-0003-vectors.json");
    private static readonly string TaxonomyRegistryPath = Path.Join(Root, "schemas", "symbol-evidence-taxonomy", "v1.registry.json");
    private static readonly string AuditRegistryPath = Path.Join(Root, "schemas", "audit-result", "v1.registry.json");

    [Fact]
    public void Annex_IsStrictPublicSafeAndPinnedToExactRegistries()
    {
        var bytes = File.ReadAllBytes(VectorPath);
        AssertNoDuplicateProperties(bytes);
        using var vectors = JsonDocument.Parse(bytes);
        var root = vectors.RootElement;

        Assert.Equal("ADR-0003", root.GetProperty("decisionId").GetString());
        Assert.Equal(BaselineCommit, root.GetProperty("baselineCommit").GetString());
        Assert.Equal(Sha256(TaxonomyRegistryPath), root.GetProperty("registryDigests").GetProperty("taxonomyV1Sha256").GetString());
        Assert.Equal(Sha256(AuditRegistryPath), root.GetProperty("registryDigests").GetProperty("auditResultV1Sha256").GetString());

        var text = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotMatch(@"(?i)([a-z]:\\|/users/|\\users\\|/tmp/|\\temp\\|password\s*[=:]|token\s*[=:]|secret\s*[=:]|-----begin [a-z ]+private key-----)", text);
        Assert.DoesNotContain("file://", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Annex_ExhaustivelyMatchesCurrentClosedKindRegistries()
    {
        using var vectors = LoadVectors();
        using var registry = JsonDocument.Parse(File.ReadAllText(TaxonomyRegistryPath));

        AssertExactIds(registry.RootElement, vectors.RootElement, "primaryKinds");
        AssertExactIds(registry.RootElement, vectors.RootElement, "componentKinds");
        AssertExactIds(registry.RootElement, vectors.RootElement, "relationKinds");
    }

    [Fact]
    public void Annex_RequiresEveryDecisionDimensionAndLegalRepresentationMode()
    {
        using var vectors = LoadVectors();
        var root = vectors.RootElement;

        AssertRows(root, "primaryKinds", ["externalApi", "assemblyVisible", "selected", "excluded", "support", "representation"]);
        AssertRows(root, "componentKinds", ["parentProfile", "result", "support", "origin", "observationSubject", "evidenceSubject", "representation"]);
        AssertRows(root, "relationKinds", ["profileInteraction", "targetEffect", "resultEffect", "documentation", "evidenceSubject", "authority", "unavailable", "supportSkipEffect", "ordinaryEvidenceEffect", "representation"]);
        AssertRows(root, "observationCases", ["subject", "input", "outcome", "completeness", "evidence", "failClosed"]);

        foreach (var row in root.GetProperty("componentKinds").EnumerateArray())
        {
            Assert.Equal("selected-supported-parent", row.GetProperty("parentProfile").GetString());
        }

        foreach (var row in root.GetProperty("relationKinds").EnumerateArray())
        {
            Assert.Equal("none", row.GetProperty("resultEffect").GetString());
            Assert.Equal("none", row.GetProperty("evidenceSubject").GetString());
            Assert.Equal("none", row.GetProperty("supportSkipEffect").GetString());
            Assert.Equal("semantic-compilation", row.GetProperty("authority").GetString());
        }

        foreach (var row in root.GetProperty("primaryKinds").EnumerateArray().Concat(root.GetProperty("componentKinds").EnumerateArray()).Concat(root.GetProperty("relationKinds").EnumerateArray()))
        {
            var representation = row.GetProperty("representation");
            var mode = representation.GetProperty("mode").GetString();
            Assert.Contains(mode, new[] { "existing-v1", "coordinated-amendment" });
            if (mode == "existing-v1")
            {
                Assert.NotEmpty(representation.GetProperty("ids").EnumerateArray());
            }
            else
            {
                Assert.Equal(35, representation.GetProperty("issue").GetInt32());
                Assert.NotEmpty(representation.GetProperty("surfaces").EnumerateArray());
            }
        }
    }

    [Fact]
    public void Annex_CoversObservationFailureLifecycleAndImpactBoundaries()
    {
        using var vectors = LoadVectors();
        var root = vectors.RootElement;
        var observations = root.GetProperty("observationCases").EnumerateArray().ToDictionary(row => row.GetProperty("caseId").GetString()!, StringComparer.Ordinal);

        string[] requiredObservationCases =
        [
            "component.complete-absence",
            "component.inheritdoc-only",
            "component.malformed-readable",
            "component.match-plus-other-malformed",
            "component.no-block-partial",
            "component.wrong-name",
            "parent.inheritdoc",
            "parent.malformed",
            "parent.missing",
            "parent.positive-plus-unreadable",
            "parent.unreadable",
            "parent.whitespace",
            "partial.defining-fallback",
            "partial.implementing-authority"
        ];
        Assert.Equal(requiredObservationCases.Order(StringComparer.Ordinal), observations.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("documentation.unavailable", observations["component.malformed-readable"].GetProperty("outcome").GetString());
        Assert.Equal("audit.reason.documentation-unavailable.malformed-xml", observations["component.malformed-readable"].GetProperty("failClosed").GetString());
        Assert.Equal("documentation.present", observations["parent.positive-plus-unreadable"].GetProperty("outcome").GetString());

        var failures = root.GetProperty("failureCases").EnumerateArray().ToDictionary(row => row.GetProperty("caseId").GetString()!, StringComparer.Ordinal);
        Assert.Equal("origin.unknown", failures["failure.generated-provenance"].GetProperty("origin").GetString());
        Assert.Equal("skip.unavailable.generated-provenance", failures["failure.generated-provenance"].GetProperty("skipOrError").GetString());
        Assert.Equal(JsonValueKind.Null, failures["failure.documentation-id-unknown-origin"].GetProperty("origin").ValueKind);
        Assert.DoesNotContain(failures.Values, row => row.TryGetProperty("origin", out var origin) && origin.ValueKind == JsonValueKind.String && origin.GetString() == "origin.unknown" && row.GetProperty("skipOrError").GetString() == "skip.unavailable.documentation-comment-id");

        Assert.Equal(Enumerable.Range(24, 3).Concat([30]).Concat(Enumerable.Range(35, 6)), root.GetProperty("impactIssues").EnumerateArray().Select(value => value.GetInt32()));
        var lifecycle = root.GetProperty("lifecycleCases").EnumerateArray().ToDictionary(row => row.GetProperty("caseId").GetString()!, StringComparer.Ordinal);
        Assert.Equal("historical-valid", lifecycle["lifecycle.m0-pinned"].GetProperty("disposition").GetString());
        Assert.Equal("invalid-no-default-no-migration", lifecycle["lifecycle.m1-no-profile"].GetProperty("disposition").GetString());
        Assert.Equal("reject", lifecycle["lifecycle.mismatched-baseline"].GetProperty("disposition").GetString());
    }

    [Fact]
    public void Annex_RejectsRepresentativeMutations()
    {
        var original = JsonNode.Parse(File.ReadAllText(VectorPath))!.AsObject();

        AssertInvalidMutation(original, node => node["primaryKinds"]!.AsArray().RemoveAt(0));
        AssertInvalidMutation(original, node => node["relationKinds"]![0]!["evidenceSubject"] = "source");
        AssertInvalidMutation(original, node => node["componentKinds"]![0]!.AsObject().Remove("evidenceSubject"));
        AssertInvalidMutation(original, node => node["profiles"]![0]!["representation"]!["mode"] = "existing-v1");
        AssertInvalidMutation(original, node => node["observationCases"]!.AsArray().First(item => item!["caseId"]!.GetValue<string>() == "component.malformed-readable")!["outcome"] = "documentation.present");
        AssertInvalidMutation(original, node => node["failureCases"]!.AsArray().First(item => item!["caseId"]!.GetValue<string>() == "failure.documentation-id-known-origin")!["origin"] = "origin.unknown");
    }

    [Fact]
    public void RepresentativeCSharp_CompilesAndExposesEveryConcretePrimaryKind()
    {
        const string source = """
            #nullable enable
            using System;
            namespace DecisionVectors;
            public interface I { void M(); }
            public delegate int D<T>(T value);
            public enum E { A }
            public struct S { public int Field; }
            public class C : I
            {
                public C() { }
                ~C() { }
                public int Field;
                public event Action? Changed;
                public int Property { get; set; }
                public int this[int index] => index;
                public void M() => Changed?.Invoke();
                public static C operator +(C left, C right) => left;
                public static implicit operator int(C value) => value.Field;
            }
            public partial class Partial { partial void P(string definingName); }
            public partial class Partial
            {
                /// <param name="implementingName">Value.</param>
                partial void P(string implementingName) { }
            }
            """;

        var compilation = CSharpCompilation.Create(
            "Adr0003Vectors",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview, DocumentationMode.Diagnose))],
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        Assert.Empty(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var ns = compilation.GlobalNamespace.GetNamespaceMembers().Single(member => member.Name == "DecisionVectors");
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in ns.GetTypeMembers())
        {
            found.Add(type.TypeKind switch
            {
                TypeKind.Class => "symbol.type.class",
                TypeKind.Struct => "symbol.type.struct",
                TypeKind.Interface => "symbol.type.interface",
                TypeKind.Enum => "symbol.type.enum",
                TypeKind.Delegate => "symbol.type.delegate",
                _ => throw new InvalidOperationException(type.TypeKind.ToString())
            });

            foreach (var member in type.GetMembers().Where(member => !member.IsImplicitlyDeclared))
            {
                switch (member)
                {
                    case IMethodSymbol { MethodKind: MethodKind.Constructor }:
                        found.Add("symbol.member.constructor");
                        break;
                    case IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator }:
                        found.Add("symbol.member.operator");
                        break;
                    case IMethodSymbol { MethodKind: MethodKind.Conversion }:
                        found.Add("symbol.member.conversion");
                        break;
                    case IMethodSymbol:
                        found.Add("symbol.member.method");
                        break;
                    case IPropertySymbol { IsIndexer: true }:
                        found.Add("symbol.member.indexer");
                        break;
                    case IPropertySymbol:
                        found.Add("symbol.member.property");
                        break;
                    case IEventSymbol:
                        found.Add("symbol.member.event");
                        break;
                    case IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum, HasConstantValue: true }:
                        found.Add("symbol.member.enum-member");
                        break;
                    case IFieldSymbol:
                        found.Add("symbol.member.field");
                        break;
                }
            }
        }

        string[] expected =
        [
            "symbol.member.constructor",
            "symbol.member.conversion",
            "symbol.member.enum-member",
            "symbol.member.event",
            "symbol.member.field",
            "symbol.member.indexer",
            "symbol.member.method",
            "symbol.member.operator",
            "symbol.member.property",
            "symbol.type.class",
            "symbol.type.delegate",
            "symbol.type.enum",
            "symbol.type.interface",
            "symbol.type.struct"
        ];
        Assert.Equal(expected, found.Order(StringComparer.Ordinal));
    }

    private static void AssertInvalidMutation(JsonObject original, Action<JsonObject> mutate)
    {
        var clone = original.DeepClone().AsObject();
        mutate(clone);
        Assert.ThrowsAny<Exception>(() => ValidateMutation(clone));
    }

    private static void ValidateMutation(JsonObject root)
    {
        using var registry = JsonDocument.Parse(File.ReadAllText(TaxonomyRegistryPath));
        using var document = JsonDocument.Parse(root.ToJsonString());
        var vectors = document.RootElement;
        foreach (var section in new[] { "primaryKinds", "componentKinds", "relationKinds" })
        {
            var expected = RegistryIds(registry.RootElement, section);
            var actualRows = vectors.GetProperty(section).EnumerateArray().ToArray();
            var actual = actualRows.Select(row => row.GetProperty("id").GetString()!).ToArray();
            if (!expected.Order(StringComparer.Ordinal).SequenceEqual(actual.Order(StringComparer.Ordinal)) || actual.Length != actual.Distinct(StringComparer.Ordinal).Count())
            {
                throw new InvalidOperationException(section);
            }

            foreach (var row in actualRows)
            {
                if (!row.TryGetProperty("representation", out var representation) || !representation.TryGetProperty("mode", out var mode))
                {
                    throw new InvalidOperationException("representation");
                }

                if (mode.GetString() == "existing-v1" && !representation.TryGetProperty("ids", out _))
                {
                    throw new InvalidOperationException("existing-v1");
                }
                if (mode.GetString() == "coordinated-amendment" && (!representation.TryGetProperty("issue", out var issue) || issue.GetInt32() != 35 || !representation.TryGetProperty("surfaces", out _)))
                {
                    throw new InvalidOperationException("amendment");
                }
            }
        }

        foreach (var row in vectors.GetProperty("relationKinds").EnumerateArray())
        {
            if (row.GetProperty("evidenceSubject").GetString() != "none")
            {
                throw new InvalidOperationException("relation evidence");
            }
        }
        foreach (var row in vectors.GetProperty("componentKinds").EnumerateArray())
        {
            _ = row.GetProperty("evidenceSubject");
        }
        foreach (var row in vectors.GetProperty("profiles").EnumerateArray())
        {
            if (row.GetProperty("representation").GetProperty("mode").GetString() != "coordinated-amendment")
            {
                throw new InvalidOperationException("profile representation");
            }
        }
        foreach (var row in vectors.GetProperty("observationCases").EnumerateArray())
        {
            if (row.GetProperty("caseId").GetString() == "component.malformed-readable" && row.GetProperty("outcome").GetString() != "documentation.unavailable")
            {
                throw new InvalidOperationException("malformed XML");
            }
        }
        foreach (var row in vectors.GetProperty("failureCases").EnumerateArray())
        {
            if (row.TryGetProperty("origin", out var origin) && origin.ValueKind == JsonValueKind.String && origin.GetString() == "origin.unknown" && row.GetProperty("skipOrError").GetString() == "skip.unavailable.documentation-comment-id")
            {
                throw new InvalidOperationException("illegal origin/skip");
            }
        }
    }

    private static void AssertExactIds(JsonElement registry, JsonElement vectors, string section)
    {
        var expected = RegistryIds(registry, section).Order(StringComparer.Ordinal).ToArray();
        var actual = vectors.GetProperty(section).EnumerateArray().Select(row => row.GetProperty("id").GetString()!).ToArray();
        Assert.Equal(actual.Order(StringComparer.Ordinal), actual);
        Assert.Equal(actual.Length, actual.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(expected, actual);
    }

    private static string[] RegistryIds(JsonElement registry, string section)
        => registry.GetProperty("sections").GetProperty(section).EnumerateArray().Select(row => row.GetProperty("id").GetString()!).ToArray();

    private static void AssertRows(JsonElement root, string section, string[] required)
    {
        foreach (var row in root.GetProperty(section).EnumerateArray())
        {
            foreach (var property in required)
            {
                Assert.True(row.TryGetProperty(property, out var value), $"{section}/{row.GetProperty(section == "observationCases" ? "caseId" : "id").GetString()} lacks {property}.");
                Assert.NotEqual(JsonValueKind.Null, value.ValueKind);
            }
        }
    }

    private static void AssertNoDuplicateProperties(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow });
        var scopes = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                scopes.Push(new HashSet<string>(StringComparer.Ordinal));
            }
            else if (reader.TokenType == JsonTokenType.EndObject)
            {
                scopes.Pop();
            }
            else if (reader.TokenType == JsonTokenType.PropertyName && !scopes.Peek().Add(reader.GetString()!))
            {
                throw new InvalidOperationException($"Duplicate JSON property: {reader.GetString()}");
            }
        }
    }

    private static IEnumerable<MetadataReference> PlatformReferences()
    {
        var paths = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?.Split(Path.PathSeparator) ?? throw new InvalidOperationException("Platform references unavailable.");
        return paths.Select(path => MetadataReference.CreateFromFile(path));
    }

    private static JsonDocument LoadVectors() => JsonDocument.Parse(File.ReadAllText(VectorPath));

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Join(directory.FullName, "ContractScribe.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
