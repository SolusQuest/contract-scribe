using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
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
    private static readonly JsonSerializerOptions CanonicalJsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

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
        Assert.DoesNotMatch(@"(?i)([a-z]:\\users\\|/users/[^/]+/|password\s*[=:]|access[_-]?token\s*[=:]|api[_-]?key\s*[=:]|client[_-]?secret\s*[=:]|-----begin [a-z ]+private key-----)", text);
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
        AssertRows(root, "observationCases", ["subject", "input", "outcome", "completeness", "evidence", "failClosed", "evidenceType", "representation"]);

        var assembly = root.GetProperty("profiles").EnumerateArray().Single(row => row.GetProperty("id").GetString() == "profile.assembly-visible");
        var external = root.GetProperty("profiles").EnumerateArray().Single(row => row.GetProperty("id").GetString() == "profile.external-api");
        Assert.Equal(new[] { "internal", "protected-internal", "public" }, Strings(assembly, "declaredAccessibilities"));
        Assert.Equal(new[] { "private-protected", "protected" }, Strings(assembly, "conditionalAccessibilities"));
        Assert.Equal(new[] { "public" }, Strings(external, "declaredAccessibilities"));
        Assert.Equal(new[] { "protected", "protected-internal" }, Strings(external, "conditionalAccessibilities"));
        foreach (var profile in new[] { assembly, external })
        {
            var declared = Strings(profile, "declaredAccessibilities");
            var conditional = Strings(profile, "conditionalAccessibilities");
            var excluded = Strings(profile, "excludedAccessibilities");
            Assert.Empty(declared.Intersect(conditional, StringComparer.Ordinal));
            Assert.Empty(declared.Intersect(excluded, StringComparer.Ordinal));
            Assert.Empty(conditional.Intersect(excluded, StringComparer.Ordinal));
            Assert.Equal(new[] { "file", "internal", "private", "private-protected", "protected", "protected-internal", "public" }, declared.Concat(conditional).Concat(excluded).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        }

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

        ValidateAll(root);
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
            "conditional.active",
            "conditional.removed",
            "generated.source.absent",
            "generated.source.present",
            "generated.source.unavailable",
            "generated.tool.absent",
            "generated.tool.present",
            "generated.tool.unavailable",
            "inheritance.interface-direct-only",
            "inheritance.override-direct-only",
            "parent.inheritdoc",
            "parent.malformed",
            "parent.missing",
            "parent.positive-plus-unreadable",
            "parent.unreadable",
            "parent.whitespace",
            "partial.ambiguity",
            "partial.ambiguity-plus-mixed",
            "partial.defining-fallback",
            "partial.implementing-authority"
        ];
        Assert.Equal(requiredObservationCases.Order(StringComparer.Ordinal), observations.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("documentation.unavailable", observations["component.malformed-readable"].GetProperty("outcome").GetString());
        Assert.Equal("audit.reason.documentation-unavailable.malformed-xml", observations["component.malformed-readable"].GetProperty("failClosed").GetString());
        Assert.Equal("documentation.present", observations["parent.positive-plus-unreadable"].GetProperty("outcome").GetString());

        var relationCases = root.GetProperty("relationCases").EnumerateArray().ToArray();
        foreach (var kind in root.GetProperty("relationKinds").EnumerateArray().Select(row => row.GetProperty("id").GetString()!))
        {
            var cases = relationCases.Where(row => row.GetProperty("relationKind").GetString() == kind).ToArray();
            Assert.Equal(new[] { "ambiguous", "available", "unavailable" }, cases.Select(row => row.GetProperty("endpoint").GetString()).Order(StringComparer.Ordinal));
            Assert.Contains(cases, row => row.GetProperty("evidenceType").GetString() == "compiled");
            Assert.Contains(cases, row => row.GetProperty("evidenceType").GetString() == "synthetic-classification");
        }

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
        var profileCases = root.GetProperty("profileCases").EnumerateArray().ToDictionary(row => row.GetProperty("caseId").GetString()!, StringComparer.Ordinal);
        Assert.Equal("invalid", profileCases["profile.mismatch"].GetProperty("disposition").GetString());
        Assert.Equal("run-level-policy-error", profileCases["profile.missing"].GetProperty("disposition").GetString());
        Assert.Equal("run-level-policy-error", profileCases["profile.unknown"].GetProperty("disposition").GetString());
    }

    [Fact]
    public void GeneratedIdentityGoldens_AreByteExactCanonicalAndPlatformIndependent()
    {
        using var vectors = LoadVectors();
        var root = vectors.RootElement;
        var cases = root.GetProperty("generatedIdentityCases").EnumerateArray().ToDictionary(row => row.GetProperty("caseId").GetString()!, StringComparer.Ordinal);
        foreach (var row in cases.Values)
        {
            var fields = row.GetProperty("fields").EnumerateArray().Select(value => value.GetString()!).ToArray();
            var (preimage, id) = DeriveIdentity(row.GetProperty("prefix").GetString()!, row.GetProperty("domain").GetString()!, fields);
            Assert.Equal(row.GetProperty("preimageHex").GetString(), Convert.ToHexString(preimage).ToLowerInvariant());
            Assert.Equal(row.GetProperty("expectedId").GetString(), id);
            Assert.Equal("accepted", row.GetProperty("outcome").GetString());
        }

        Assert.Equal(cases["identity.sgo.nfc"].GetProperty("expectedId").GetString(), cases["identity.sgo.nfd"].GetProperty("expectedId").GetString());
        Assert.NotEqual(cases["identity.sgo.case-upper"].GetProperty("expectedId").GetString(), cases["identity.sgo.case-lower"].GetProperty("expectedId").GetString());

        var hash = root.GetProperty("generatedHashCases").EnumerateArray().Single();
        Assert.Equal(hash.GetProperty("fullSourceSha256").GetString(), Sha256(Encoding.UTF8.GetBytes(hash.GetProperty("fullText").GetString()!)));
        Assert.Equal(hash.GetProperty("evidenceRegionSha256").GetString(), Sha256(Encoding.UTF8.GetBytes(hash.GetProperty("regionText").GetString()!)));
        Assert.NotEqual(hash.GetProperty("fullSourceSha256").GetString(), hash.GetProperty("evidenceRegionSha256").GetString());

        var locators = root.GetProperty("generatedLocatorCases").EnumerateArray().ToDictionary(row => row.GetProperty("caseId").GetString()!, StringComparer.Ordinal);
        AssertLocator(locators, "locator.candidate.repository", "candidate", "repository", ["path"], """{"repository":{"path":"src/A.cs"}}""", 1, """["src/A.cs",null]""");
        AssertLocator(locators, "locator.candidate.generated-source", "candidate", "generatedSource", ["generatorId", "hintNameId"], """{"generatedSource":{"generatorId":"sgp.a","hintNameId":"sgo.a"}}""", 2, """["sgp.a","sgo.a",null]""");
        AssertLocator(locators, "locator.candidate.tool-generated", "candidate", "toolGenerated", ["producerId", "outputId"], """{"toolGenerated":{"producerId":"tgp.a","outputId":"tgo.a"}}""", 3, """["tgp.a","tgo.a",null]""");
        AssertLocator(locators, "locator.candidate.synthetic", "candidate", "synthetic", ["fixtureId"], """{"synthetic":{"fixtureId":"fixture.a"}}""", 4, """["fixture.a"]""");
        AssertLocator(locators, "locator.evidence.generated-output", "evidence", "generatedOutput", ["producerKind", "producerId", "outputId", "sourceSha256"], """{"generatedOutput":{"producerKind":"source-generator","producerId":"sgp.a","outputId":"sgo.a","sourceSha256":"48f2a8fc6db9009662be60e5f3b4787ba54f159cac71278f47c905a4d86229ae"}}""", 3, """["source-generator","sgp.a","sgo.a","48f2a8fc6db9009662be60e5f3b4787ba54f159cac71278f47c905a4d86229ae",null]""");
        Assert.DoesNotContain(
            locators.Values.Where(row => row.GetProperty("surface").GetString() == "candidate"),
            row => row.GetProperty("canonicalJson").GetString()!.StartsWith("""{"generatedOutput":""", StringComparison.Ordinal));

        var invalid = root.GetProperty("generatedInvalidIdentityCases").EnumerateArray().ToDictionary(row => row.GetProperty("caseId").GetString()!, StringComparer.Ordinal);
        AssertInvalidIdentity(invalid, "identity.invalid.empty", "tool", "reject", "empty");
        AssertInvalidIdentity(invalid, "identity.invalid.oversized", "source-generator", "reject", "oversized");
        AssertInvalidIdentity(invalid, "identity.source.secret-like", "source-generator", "accept-opaque", "no-secret-heuristic");
        AssertInvalidIdentity(invalid, "identity.source.unix-like", "source-generator", "accept-opaque", "no-path-heuristic");
        AssertInvalidIdentity(invalid, "identity.source.windows-like", "source-generator", "accept-opaque", "no-path-heuristic");
        AssertInvalidIdentity(invalid, "identity.tool.path-like", "tool", "reject", "closed-tool-grammar");
        AssertInvalidIdentity(invalid, "identity.tool.unicode", "tool", "reject", "closed-tool-grammar");
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
        AssertInvalidMutation(original, node => node["primaryKinds"]!.AsArray().First(item => item!["id"]!.GetValue<string>() == "symbol.member.constructor")!["support"] = "support.not-applicable");
        AssertInvalidMutation(original, node => node["primaryKinds"]!.AsArray().First(item => item!["id"]!.GetValue<string>() == "symbol.member.method")!["skip"] = "skip.unsupported.symbol-kind");
        AssertInvalidMutation(original, node => node["relationKinds"]![0]!["targetEffect"] = "creates-target");
        AssertInvalidMutation(original, node => node["observationCases"]!.AsArray().First(item => item!["caseId"]!.GetValue<string>() == "parent.missing")!["outcome"] = "documentation.present");
        AssertInvalidMutation(original, node => node["generatedCases"]![0]!["representation"]!["surfaces"]![0] = "unknown.surface");
        AssertInvalidMutation(original, node => node["observationCases"]!.AsArray().First(item => item!["caseId"]!.GetValue<string>() == "parent.whitespace")!["outcome"] = "documentation.maybe");
        AssertInvalidMutation(original, node => node["relationKinds"]!.AsArray().First(item => item!["id"]!.GetValue<string>() == "relation.overrides")!["documentation"] = "inherits-base-documentation");
        AssertInvalidMutation(original, node => node["relationKinds"]!.AsArray().First(item => item!["id"]!.GetValue<string>() == "relation.explicit-interface-implementation")!["profileInteraction"] = "all-implementations");
        AssertInvalidMutation(original, node => node["failureCases"]!.AsArray().First(item => item!["caseId"]!.GetValue<string>() == "failure.semantic-context")!["skipOrError"] = "run.classification.unrepresentable");
        AssertInvalidMutation(original, node => node["generatedCases"]!.AsArray().First(item => item!["caseId"]!.GetValue<string>() == "generated.project-rule")!["policy"] = "inapplicable");
        AssertInvalidMutation(original, node => node["observationCases"]!.AsArray().First(item => item!["caseId"]!.GetValue<string>() == "parent.inheritdoc")!["subject"] = "component");
        AssertInvalidMutation(original, node => node["observationCases"]!.AsArray().First(item => item!["caseId"]!.GetValue<string>() == "parent.inheritdoc")!["input"] = "all-parts-no-block");
        AssertInvalidMutation(original, node => node["observationCases"]!.AsArray().First(item => item!["caseId"]!.GetValue<string>() == "parent.inheritdoc")!["representation"]!["surfaces"]![0] = "audit-result.component-evidence");
        AssertInvalidMutation(original, node =>
        {
            var locators = node["generatedLocatorCases"]!.AsArray();
            var repository = locators.First(item => item!["caseId"]!.GetValue<string>() == "locator.candidate.repository")!;
            var tool = locators.First(item => item!["caseId"]!.GetValue<string>() == "locator.candidate.tool-generated")!;
            (repository["order"], tool["order"]) = (tool["order"]!.DeepClone(), repository["order"]!.DeepClone());
        });
        AssertInvalidMutation(original, node => node["generatedLocatorCases"]!.AsArray().First(item => item!["caseId"]!.GetValue<string>() == "locator.candidate.repository")!["equalityKey"] = new JsonArray("unrelated", null));
        AssertInvalidMutation(original, node => node["generatedLocatorCases"]!.AsArray().First(item => item!["caseId"]!.GetValue<string>() == "locator.candidate.repository")!["canonicalJson"] = """{"wrong":{}}""");
        AssertInvalidMutation(original, node => node["generatedLocatorCases"]!.AsArray().First(item => item!["caseId"]!.GetValue<string>() == "locator.candidate.generated-source")!["canonicalJson"] = """{"generatedSource":{"hintNameId":"sgo.a"}}""");
        AssertInvalidMutation(original, node => node["generatedLocatorCases"]!.AsArray().First(item => item!["caseId"]!.GetValue<string>() == "locator.evidence.generated-output")!["equalityKey"]!.AsArray().RemoveAt(3));
        AssertInvalidMutation(original, node => node["generatedInvalidIdentityCases"]!.AsArray().First(item => item!["caseId"]!.GetValue<string>() == "identity.invalid.oversized")!["outcome"] = "accept-opaque");
        AssertInvalidMutation(original, node => node["groundedExistingConcepts"]![0]!["id"] = "observation.direct-only");
    }

    [Fact]
    public void RepresentativeCSharp_CompilesAndExposesEveryConcretePrimaryKind()
    {
        const string source = """
            #nullable enable
            using System;
            namespace DecisionVectors;
            public interface I { void M(); }
            public interface IBase { void Inherited(); }
            public interface IDerived : IBase { }
            public delegate int D<T>(T value);
            public enum E { A }
            public struct S { public int Field; }
            public class Base { public virtual void V() { } }
            public class C : Base, I
            {
                public C() { }
                ~C() { }
                public int Field;
                public event Action? Changed;
                public int Property { get; set; }
                public int this[int index] => index;
                public void M() => Changed?.Invoke();
                void I.M() { }
                public override void V() { }
                public static C operator +(C left, C right) => left;
                public static implicit operator int(C value) => value.Field;
            }
            public partial class Partial { partial void P(string definingName); }
            public partial class Partial
            {
                /// <param name="implementingName">Value.</param>
                partial void P(string implementingName) { }
            }
            public record R(int Value);
            public readonly record struct RS(int Value);
            public class Outer
            {
                internal class InternalNested { }
                private class PrivateNested { }
                public class PublicNested { }
                protected class ProtectedNested { }
                private protected class PrivateProtectedNested { }
                protected internal class ProtectedInternalNested { }
            }
            file class FileLocal { }
            #if INCLUDED
            public class ConditionalIncluded { }
            #endif
            """;

        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview, DocumentationMode.Diagnose, preprocessorSymbols: ["INCLUDED"]);
        var compilation = CSharpCompilation.Create(
            "Adr0003Vectors",
            [CSharpSyntaxTree.ParseText(source, parseOptions)],
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        Assert.Empty(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var ns = compilation.GlobalNamespace.GetNamespaceMembers().Single(member => member.Name == "DecisionVectors");
        Assert.NotNull(ns.GetTypeMembers("ConditionalIncluded").SingleOrDefault());
        Assert.True(ns.GetTypeMembers("R").Single().IsRecord);
        Assert.True(ns.GetTypeMembers("RS").Single().IsRecord);
        Assert.Contains(ns.GetTypeMembers("C").Single().GetMembers().OfType<IMethodSymbol>(), method => method.ExplicitInterfaceImplementations.Length == 1);
        Assert.Contains(ns.GetTypeMembers("C").Single().GetMembers().OfType<IMethodSymbol>(), method => method.IsOverride);

        var withoutConditional = CSharpCompilation.Create(
            "Adr0003WithoutConditional",
            [CSharpSyntaxTree.ParseText(source, parseOptions.WithPreprocessorSymbols(Array.Empty<string>()))],
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        Assert.Null(withoutConditional.GetTypeByMetadataName("DecisionVectors.ConditionalIncluded"));
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
        using var taxonomy = JsonDocument.Parse(File.ReadAllText(TaxonomyRegistryPath));
        using var document = JsonDocument.Parse(root.ToJsonString());
        var vectors = document.RootElement;
        foreach (var section in new[] { "primaryKinds", "componentKinds", "relationKinds" })
        {
            var expected = RegistryIds(taxonomy.RootElement, section);
            var actualRows = vectors.GetProperty(section).EnumerateArray().ToArray();
            var actual = actualRows.Select(row => row.GetProperty("id").GetString()!).ToArray();
            if (!expected.Order(StringComparer.Ordinal).SequenceEqual(actual.Order(StringComparer.Ordinal)) || actual.Length != actual.Distinct(StringComparer.Ordinal).Count())
            {
                throw new InvalidOperationException(section);
            }

        }
        ValidateAll(vectors);
    }

    private static void ValidateAll(JsonElement root)
    {
        using var taxonomy = JsonDocument.Parse(File.ReadAllText(TaxonomyRegistryPath));
        using var audit = JsonDocument.Parse(File.ReadAllText(AuditRegistryPath));
        var registryIds = AllRegistryIds(taxonomy.RootElement).Concat(AllRegistryIds(audit.RootElement)).ToHashSet(StringComparer.Ordinal);
        var existingConcepts = ValidateGroundedExistingConcepts(root);
        var surfaceArray = Strings(root, "amendmentSurfaces");
        var surfaces = surfaceArray.ToHashSet(StringComparer.Ordinal);
        Assert.Equal(
            [
                "audit-result.component-evidence",
                "audit-result.documentation-observation",
                "audit-result.generated-contribution",
                "audit-result.generated-evidence-locator",
                "audit-result.malformed-xml",
                "audit-result.profile",
                "audit-result.target-evidence",
                "policy.generated-contribution",
                "policy.input-error",
                "policy.target-profile",
                "taxonomy.failure-contract",
                "taxonomy.generated-candidate-locator",
                "taxonomy.generated-evidence-locator",
                "taxonomy.profile-membership",
                "taxonomy.profile-vocabulary"
            ],
            surfaceArray);
        ValidateExactNormativeRows(root);

        string[] representedArrays =
        [
            "accessibilityCases", "componentKinds", "failureCases", "generatedCases",
            "generatedHashCases", "generatedIdentityCases", "generatedInvalidIdentityCases",
            "generatedLocatorCases", "lifecycleCases", "observationCases", "primaryKinds",
            "profileCases", "profiles", "relationCases", "relationKinds"
        ];
        foreach (var arrayName in representedArrays)
        {
            foreach (var row in root.GetProperty(arrayName).EnumerateArray())
            {
                ValidateRepresentation(row.GetProperty("representation"), registryIds, existingConcepts, surfaces);
            }
        }

        ValidateClassificationRows(root.GetProperty("primaryKinds"), taxonomy.RootElement, "primaryKinds", "TargetClassification");
        ValidateClassificationRows(root.GetProperty("componentKinds"), taxonomy.RootElement, "componentKinds", "ComponentClassification");
        foreach (var row in root.GetProperty("primaryKinds").EnumerateArray())
        {
            Require(row.GetProperty("externalApi").GetString(), ["derive-selected-enum", "if-otherwise-selected", "profile-predicate"], "primary externalApi");
            Require(row.GetProperty("assemblyVisible").GetString(), ["derive-selected-enum", "if-otherwise-selected", "profile-predicate"], "primary assemblyVisible");
            Require(row.GetProperty("selected").GetString(), ["skipped-result", "target-result"], "primary selected");
            Require(row.GetProperty("excluded").GetString(), ["no-record"], "primary excluded");
        }
        foreach (var row in root.GetProperty("componentKinds").EnumerateArray())
        {
            Require(row.GetProperty("result").GetString(), ["component-result", "component-result-nonvoid-only", "not-applicable", "skipped-result"], "component result");
            Require(row.GetProperty("origin").GetString(), ["origin.compiler-synthesized", "parent"], "component origin");
            Require(row.GetProperty("observationSubject").GetString(), ["matching-param-on-parent-block", "matching-returns-on-parent-block", "matching-typeparam-on-parent-block", "matching-value-on-parent-block", "none"], "component observationSubject");
            Require(row.GetProperty("evidenceSubject").GetString(), ["none", "parent-symbol-ref-plus-component-identity"], "component evidenceSubject");
        }

        var relationTargetEffects = new[] { "no-duplicate-inherited-target", "no-promotion", "relation-only-source-not-target" };
        foreach (var row in root.GetProperty("relationKinds").EnumerateArray())
        {
            Require(row.GetProperty("targetEffect").GetString(), relationTargetEffects, "relation targetEffect");
            if (row.GetProperty("resultEffect").GetString() != "none" ||
                row.GetProperty("evidenceSubject").GetString() != "none" ||
                row.GetProperty("supportSkipEffect").GetString() != "none" ||
                row.GetProperty("ordinaryEvidenceEffect").GetString() != "none" ||
                row.GetProperty("authority").GetString() != "semantic-compilation")
            {
                throw new InvalidOperationException("relation invariant");
            }
        }

        var expectedOutcomes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["component.complete-absence"] = "documentation.absent",
            ["component.inheritdoc-only"] = "documentation.absent",
            ["component.malformed-readable"] = "documentation.unavailable",
            ["component.match-plus-other-malformed"] = "documentation.present",
            ["component.no-block-partial"] = "documentation.absent",
            ["component.wrong-name"] = "documentation.absent",
            ["conditional.active"] = "documentation.present",
            ["conditional.removed"] = "no-target",
            ["generated.source.absent"] = "documentation.absent",
            ["generated.source.present"] = "documentation.present",
            ["generated.source.unavailable"] = "documentation.unavailable",
            ["generated.tool.absent"] = "documentation.absent",
            ["generated.tool.present"] = "documentation.present",
            ["generated.tool.unavailable"] = "documentation.unavailable",
            ["inheritance.interface-direct-only"] = "documentation.absent",
            ["inheritance.override-direct-only"] = "documentation.absent",
            ["parent.inheritdoc"] = "documentation.present",
            ["parent.malformed"] = "documentation.present",
            ["parent.missing"] = "documentation.absent",
            ["parent.positive-plus-unreadable"] = "documentation.present",
            ["parent.unreadable"] = "documentation.unavailable",
            ["parent.whitespace"] = "documentation.absent",
            ["partial.ambiguity"] = "classification-skipped",
            ["partial.ambiguity-plus-mixed"] = "classification-skipped",
            ["partial.defining-fallback"] = "documentation.present",
            ["partial.implementing-authority"] = "documentation.present"
        };
        var observationRows = root.GetProperty("observationCases").EnumerateArray().ToArray();
        if (!expectedOutcomes.Keys.Order(StringComparer.Ordinal).SequenceEqual(observationRows.Select(row => row.GetProperty("caseId").GetString()!).Order(StringComparer.Ordinal)))
        {
            throw new InvalidOperationException("observation case set");
        }
        foreach (var row in observationRows)
        {
            var id = row.GetProperty("caseId").GetString()!;
            if (row.GetProperty("outcome").GetString() != expectedOutcomes[id])
            {
                throw new InvalidOperationException($"observation outcome {id}");
            }
            Require(row.GetProperty("evidenceType").GetString(), ["compiled", "synthetic-classification", "synthetic-generated", "synthetic-observation"], "evidenceType");
            Require(row.GetProperty("completeness").GetString(), ["absent-from-compilation", "active-compilation-only", "all-active-declarations-leading-trivia", "all-declarations-leading-trivia-and-blocks", "all-leading-trivia-including-no-block", "authoritative-local-name-map", "classification-stage", "complete", "complete-generated-source", "complete-readable-malformed-block", "complete-well-formed-block", "defining-local-name", "existential-positive", "implementation-direct-trivia", "implementing-local-name", "incomplete", "override-direct-trivia"], "observation completeness");
            Require(row.GetProperty("evidence").GetString(), ["complete-malformed-item-in-evidenceIds", "complete-parent-block", "complete-untruncated-parent-subject", "declaration-leading-trivia", "defining-block", "generatedOutput-full-source-and-region", "implementation-leading-trivia", "implementing-block", "none", "one-declaration-item-per-part", "override-leading-trivia", "positive-block-only", "unavailable-bundle-source-unavailable", "untruncated-direct-block", "untruncated-matching-block"], "observation evidence");
            Require(row.GetProperty("failClosed").GetString(), ["audit.reason.documentation-unavailable.malformed-xml", "evidence-incomplete-if-truncated", "evidence-incomplete-if-unpublishable", "no-inheritance", "no-ordinary-judgment", "no-record", "partial-precedence", "skip.ambiguous.partial-declaration", "stale-defining-name-does-not-match", "stale-implementing-name-does-not-match", "unavailable-if-any-missing", "unavailable-if-incomplete", "unavailable-if-malformed"], "observation failClosed");
        }

        foreach (var row in root.GetProperty("accessibilityCases").EnumerateArray())
        {
            Require(row.GetProperty("declared").GetString(), ["file", "internal", "private", "private-protected", "protected", "protected-internal", "public"], "accessibility declared");
            Require(row.GetProperty("externalApi").GetString(), ["excluded", "selected", "selected-if-derivable"], "accessibility externalApi");
            Require(row.GetProperty("assemblyVisible").GetString(), ["excluded", "selected", "selected-if-derivable"], "accessibility assemblyVisible");
            Require(row.GetProperty("evidenceType").GetString(), ["compiled"], "accessibility evidenceType");
        }

        foreach (var row in root.GetProperty("relationCases").EnumerateArray())
        {
            Require(row.GetProperty("endpoint").GetString(), ["ambiguous", "available", "unavailable"], "relation endpoint");
            Require(row.GetProperty("relationEmission").GetString(), ["omitted", "one"], "relation emission");
            Require(row.GetProperty("sourceResult").GetString(), ["derived-interface-unchanged", "direct-source-unchanged", "none-explicit-source"], "relation sourceResult");
            Require(row.GetProperty("evidenceType").GetString(), ["compiled", "synthetic-classification"], "relation evidenceType");
            var endpoint = row.GetProperty("endpoint").GetString();
            var emission = row.GetProperty("relationEmission").GetString();
            if ((endpoint == "available") != (emission == "one"))
            {
                throw new InvalidOperationException("relation endpoint/emission");
            }
        }

        foreach (var row in root.GetProperty("generatedCases").EnumerateArray())
        {
            Require(row.GetProperty("category").GetString(), ["source-generator", "tool-generated"], "generated category");
            Require(row.GetProperty("policy").GetString(), ["applies", "inapplicable", "ordinary-repository-selectors", "project-global-default-only"], "generated policy");
            if (row.TryGetProperty("selector", out var selector))
            {
                Require(selector.GetString(), ["global", "project-only", "sourcePaths-exclude-only", "sourcePaths-include"], "generated selector");
                Require(row.GetProperty("contribution").GetString(), ["none-from-rule", "tagged-generated"], "generated contribution");
            }
        }

        foreach (var row in root.GetProperty("failureCases").EnumerateArray())
        {
            if (row.TryGetProperty("origin", out var origin) && origin.ValueKind == JsonValueKind.String && origin.GetString() == "origin.unknown" && row.GetProperty("skipOrError").GetString() == "skip.unavailable.documentation-comment-id")
            {
                throw new InvalidOperationException("illegal origin/skip");
            }
            Require(row.GetProperty("stage").GetString(), ["classification", "policy"], "failure stage");
            Require(row.GetProperty("record").GetString(), ["TargetClassification", "UnresolvedClassification", "none"], "failure record");
        }
    }

    private static void ValidateExactNormativeRows(JsonElement root)
    {
        // These independent full-row digests pin every field, row order, and representation
        // combination. Broad vocabulary checks remain below for readable diagnostics.
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["accessibilityCases"] = "dfe22feb400fd5de304628a6ae237e81ad6daa86cd359813aa4aa5acc0c778c4",
            ["componentKinds"] = "2b7e5a2df4fd277c33fa7136d20f4ac9de0218152e6eb6087caec1d2c9713786",
            ["failureCases"] = "014f0545181e037b65f01bd8be70bb5c1b35a6cdef9344df0674813a2d599fd5",
            ["generatedCases"] = "922c1fed6bf1846f28494a6cffcdec13a028984b3ed4ceb9f50c1fbb6a050116",
            ["generatedHashCases"] = "8cae086fd633939d6e0bc5196c46c46c84034d842cb5e04c3b5970af432dd61c",
            ["generatedIdentityCases"] = "41e6bd4d8d074d1f6f0c0380738026363d5df28eb212ed3abdb463cbcd3ff398",
            ["generatedInvalidIdentityCases"] = "18f3f330643dbe45604a926f4097f602df8e823023202e9ec32938aa6fdb7273",
            ["generatedLocatorCases"] = "e9eba02e4719f2a135bdd69b400c5ad7c40235508c523d57626f3373873aca64",
            ["lifecycleCases"] = "922800af659154d94e1bf4f73d0472282adddb0590ff6592a36716c3adddf274",
            ["observationCases"] = "169b169bc2d3a85ea27afa9a76580008e10e51004deea9b5170338b356cde363",
            ["primaryKinds"] = "d4a0d1b16012d3923ab82a427ac580d3ec7019dd34b01e1fd3225bceddbbcbbb",
            ["profileCases"] = "1b89af80a9f11c441413a4a1c767adbd72f3b4562b9da96d1a6017c52bb1848a",
            ["profiles"] = "e090168870df556dc7b37fb72f0724cea85e11c5f6e1350ed6292f13399dfd00",
            ["relationCases"] = "b91c8188615ec0eca9d92c106e92bc36382ef4ddb1036cf70ed5c95d42abd329",
            ["relationKinds"] = "063fbf9baa566eef46742b098aecb2b681a14ef7e193210ae3ee3f1fbfeae4d4"
        };

        foreach (var (section, digest) in expected)
        {
            var canonical = JsonSerializer.Serialize(root.GetProperty(section), CanonicalJsonOptions);
            if (Sha256(Encoding.UTF8.GetBytes(canonical)) != digest)
            {
                throw new InvalidOperationException($"exact normative rows: {section}");
            }
        }
    }

    private static HashSet<string> ValidateGroundedExistingConcepts(JsonElement root)
    {
        var expected = new Dictionary<string, (string Contract, string Digest, string[] RequiredTexts)>(StringComparer.Ordinal)
        {
            ["candidate.locator.generated-source"] = (
                "docs/20_architecture/contracts/symbol-evidence-taxonomy-v1.md",
                "d1983390d90515f88ccfdaeb9027690817e5eccf48b19d3e7fe26d466383163c",
                ["Candidate locators are repository, generated-source, or synthetic; their order is repository, generated-source, synthetic."]),
            ["contract-lifecycle.pre-release-v1"] = (
                "docs/00_project/contract-lifecycle.md",
                "35fea95726468bd52997c3efb375f227f69120e96d9be22ad81c5f81ccb44420",
                [
                    "The repository revision is authoritative for pre-release draft semantics. A version number alone never identifies an un-released draft precisely.",
                    "The prior baseline remains valid historical evidence for its exact commit.",
                    "reject a missing or mismatched identity"
                ]),
            ["policy.repository-contribution"] = (
                "docs/20_architecture/contracts/policy-configuration-v1.md",
                "82e352397d6b2e9996333f6819cb4e8f95662ebf60c6c4cadfb63508c8568ef5",
                [
                    "The caller supplies `projectPath` and `sourcePath`, both required non-empty lexical paths.",
                    "A rule applies only when every declared selector accepts. The applicable rule with the greatest priority wins; otherwise `defaultDecision` applies."
                ])
        };

        var rows = root.GetProperty("groundedExistingConcepts").EnumerateArray().ToDictionary(row => row.GetProperty("id").GetString()!, StringComparer.Ordinal);
        if (!expected.Keys.Order(StringComparer.Ordinal).SequenceEqual(rows.Keys.Order(StringComparer.Ordinal)))
        {
            throw new InvalidOperationException("grounded concept set");
        }

        foreach (var (id, grounding) in expected)
        {
            var row = rows[id];
            if (row.GetProperty("contract").GetString() != grounding.Contract ||
                row.GetProperty("contractSha256").GetString() != grounding.Digest ||
                !Strings(row, "requiredTexts").SequenceEqual(grounding.RequiredTexts, StringComparer.Ordinal))
            {
                throw new InvalidOperationException($"grounded concept metadata: {id}");
            }

            var path = Path.GetFullPath(Path.Join(Root, grounding.Contract.Replace('/', Path.DirectorySeparatorChar)));
            var text = File.ReadAllText(path);
            if (!path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                Sha256(path) != grounding.Digest ||
                grounding.RequiredTexts.Any(requiredText => !text.Contains(requiredText, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException($"grounded concept source: {id}");
            }
        }

        return expected.Keys.ToHashSet(StringComparer.Ordinal);
    }

    private static void AssertLocator(
        IReadOnlyDictionary<string, JsonElement> locators,
        string caseId,
        string surface,
        string discriminator,
        string[] propertyOrder,
        string canonicalJson,
        int order,
        string equalityKeyJson)
    {
        var row = locators[caseId];
        Assert.Equal(surface, row.GetProperty("surface").GetString());
        Assert.Equal(canonicalJson, row.GetProperty("canonicalJson").GetString());
        Assert.Equal(order, row.GetProperty("order").GetInt32());
        Assert.Equal(equalityKeyJson, JsonSerializer.Serialize(row.GetProperty("equalityKey"), CanonicalJsonOptions));

        using var canonical = JsonDocument.Parse(canonicalJson);
        var variant = canonical.RootElement.EnumerateObject().Single();
        Assert.Equal(discriminator, variant.Name);
        Assert.Equal(propertyOrder, variant.Value.EnumerateObject().Select(property => property.Name));
    }

    private static void AssertInvalidIdentity(
        IReadOnlyDictionary<string, JsonElement> rows,
        string caseId,
        string category,
        string outcome,
        string reason)
    {
        var row = rows[caseId];
        Assert.Equal(category, row.GetProperty("category").GetString());
        Assert.Equal(outcome, row.GetProperty("outcome").GetString());
        Assert.Equal(reason, row.GetProperty("reason").GetString());
    }

    private static void ValidateRepresentation(JsonElement representation, HashSet<string> registryIds, HashSet<string> concepts, HashSet<string> surfaces)
    {
        var mode = representation.GetProperty("mode").GetString();
        if (mode == "existing-v1")
        {
            var ids = Strings(representation, "ids");
            if (ids.Length == 0 || ids.Any(id => !registryIds.Contains(id) && !concepts.Contains(id)))
            {
                throw new InvalidOperationException("unknown existing-v1 representation");
            }
            if (representation.TryGetProperty("surfaces", out _))
            {
                throw new InvalidOperationException("mixed representation mode");
            }
        }
        else if (mode == "coordinated-amendment")
        {
            if (representation.GetProperty("issue").GetInt32() != 35)
            {
                throw new InvalidOperationException("amendment owner");
            }
            var named = Strings(representation, "surfaces");
            if (named.Length == 0 || named.Any(surface => !surfaces.Contains(surface)))
            {
                throw new InvalidOperationException("unknown amendment surface");
            }
            if (representation.TryGetProperty("ids", out _))
            {
                throw new InvalidOperationException("mixed representation mode");
            }
        }
        else
        {
            throw new InvalidOperationException("representation mode");
        }
    }

    private static void ValidateClassificationRows(JsonElement rows, JsonElement registry, string section, string recordType)
    {
        var registryRows = registry.GetProperty("sections").GetProperty(section).EnumerateArray().ToDictionary(row => row.GetProperty("id").GetString()!, StringComparer.Ordinal);
        var skips = registry.GetProperty("sections").GetProperty("skipReasons").EnumerateArray().ToDictionary(row => row.GetProperty("id").GetString()!, StringComparer.Ordinal);
        foreach (var row in rows.EnumerateArray())
        {
            var definition = registryRows[row.GetProperty("id").GetString()!];
            var support = row.GetProperty("support").GetString()!;
            if (!Strings(definition, "allowedSupportStatuses").Contains(support, StringComparer.Ordinal))
            {
                throw new InvalidOperationException("illegal support");
            }

            var skip = row.GetProperty("skip");
            if (definition.TryGetProperty("requiredSkip", out var requiredSkip) && (skip.ValueKind != JsonValueKind.String || skip.GetString() != requiredSkip.GetString()))
            {
                throw new InvalidOperationException("required skip");
            }
            if (skip.ValueKind == JsonValueKind.String)
            {
                if (!skips.TryGetValue(skip.GetString()!, out var skipDefinition) ||
                    !Strings(skipDefinition, "recordTypes").Contains(recordType, StringComparer.Ordinal) ||
                    !Strings(skipDefinition, "allowedSupportStatuses").Contains(support, StringComparer.Ordinal))
                {
                    throw new InvalidOperationException("illegal support/skip");
                }
            }
            else if (support != "support.supported")
            {
                throw new InvalidOperationException("missing skip");
            }
        }
    }

    private static HashSet<string> AllRegistryIds(JsonElement registry)
        => registry.GetProperty("sections").EnumerateObject().SelectMany(section => section.Value.EnumerateArray()).Where(row => row.ValueKind == JsonValueKind.Object && row.TryGetProperty("id", out _)).Select(row => row.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal);

    private static void Require(string? value, string[] allowed, string field)
    {
        if (value is null || !allowed.Contains(value, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"Unknown {field}: {value}");
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

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string[] Strings(JsonElement element, string property)
        => element.GetProperty(property).EnumerateArray().Select(value => value.GetString()!).ToArray();

    private static (byte[] Preimage, string Id) DeriveIdentity(string prefix, string domain, string[] fields)
    {
        using var stream = new MemoryStream();
        stream.Write(Encoding.ASCII.GetBytes(domain));
        stream.WriteByte(0);
        var strictUtf8 = new UTF8Encoding(false, true);
        foreach (var field in fields)
        {
            var bytes = strictUtf8.GetBytes(field.Normalize(NormalizationForm.FormC));
            var length = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)bytes.Length));
            stream.Write(length);
            stream.Write(bytes);
        }
        var preimage = stream.ToArray();
        return (preimage, $"{prefix}.{Sha256(preimage)}");
    }

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
