using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContractScribe.Core;
using ContractScribe.Patching.Resolution;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ContractScribe.Roslyn.IntegrationTests;

[Collection("Integration process lane 1")]
public sealed class DocumentationPatchResolutionTests
{
    [Fact]
    public async Task ResolvesThroughRealLoaderAndClassifierSession()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var load = await new RepositoryLoader().LoadAsync(
            new RepositoryLoadRequest(fixture.Root, "App/App.csproj"));
        await using var repository = Assert.IsType<LoadedRepositorySession>(load.Session);
        var classified = new SymbolClassifier().ClassifySession(
            repository,
            TargetProfile.ExternalApi);
        var target = Assert.Single(
            classified.Classification.ClassificationSet!.Targets,
            candidate => candidate.SymbolRef.DocumentationCommentId == "T:App"
                && candidate.SupportStatus == SupportStatus.Supported);
        var project = Assert.Single(repository.Projects, candidate =>
            candidate.CompilationContextRef == target.SymbolRef.CompilationContextRef);
        var symbol = Assert.Single(DocumentationCommentId.GetSymbolsForDeclarationId(
            target.SymbolRef.DocumentationCommentId,
            project.Compilation));
        var reference = Assert.Single(symbol.DeclaringSyntaxReferences);
        var source = project.SourceTrees[reference.SyntaxTree];
        var path = Assert.IsType<string>(source.RepositoryPath);
        var bytes = await File.ReadAllBytesAsync(Path.Join(
            fixture.Root,
            path.Replace('/', Path.DirectorySeparatorChar)));
        var locator = new DocumentationPatchRepositoryLocator(
            path,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            DocumentationPatchRepositoryEncoding.Utf8,
            DocumentationObservationInput.Span(reference.Span.Start, reference.Span.End));
        var request = new DocumentationPatchRequest(
            new string('0', 64),
            new DocumentationPatchContext(
                repository.RepositoryContextRef,
                repository.InputIdentity,
                TargetProfile.ExternalApi),
            [],
            [new DocumentationPatchBlockRequest(
                "block-1",
                target.SymbolRef,
                locator,
                DocumentationPatchEditKind.Insert,
                [],
                new DocumentationPatchInheritDocContent(),
                [])]);

        var result = new DocumentationPatchResolver().Resolve(classified, request);

        Assert.Equal(DocumentationPatchResolutionStatus.Resolved, result.Status);
        Assert.Single(result.Targets);
    }

    [Theory]
    [InlineData(DocumentationPatchRepositoryEncoding.Utf8)]
    [InlineData(DocumentationPatchRepositoryEncoding.Utf8Bom)]
    [InlineData(DocumentationPatchRepositoryEncoding.Utf16LittleEndianBom)]
    [InlineData(DocumentationPatchRepositoryEncoding.Utf16BigEndianBom)]
    public void ResolvesExactRepositoryDeclarationForEverySupportedEncoding(
        DocumentationPatchRepositoryEncoding encoding)
    {
        using var fixture = PatchFixture.Create(
            "namespace N; public class C { public void M() { } }",
            encoding);
        var before = File.ReadAllBytes(fixture.SourcePath);
        Assert.Equal(LoadExpectedBytes(encoding), before);

        var result = new DocumentationPatchResolver().Resolve(
            fixture.ClassifiedSession,
            fixture.Request());

        Assert.Equal(DocumentationPatchResolutionStatus.Resolved, result.Status);
        var target = Assert.Single(result.Targets);
        Assert.Equal("Sample.cs", target.RepositoryPath);
        Assert.Equal(fixture.DeclarationSpan, target.DeclarationSpan);
        Assert.Equal(before, File.ReadAllBytes(fixture.SourcePath));
    }

    [Fact]
    public void RejectsRepositorySessionSubstitutionBeforeBlockLookup()
    {
        using var fixture = PatchFixture.Create(
            "namespace N; public class C { public void M() { } }",
            DocumentationPatchRepositoryEncoding.Utf8);
        Assert.True(RepositoryContextRef.TryParse(
            "repoctx-ffffffffffffffffffffffffffffffff",
            out var otherContext));

        var result = new DocumentationPatchResolver().Resolve(
            fixture.ClassifiedSession,
            fixture.Request(repositoryContextRef: otherContext));

        Assert.Equal(DocumentationPatchResolutionStatus.Stale, result.Status);
        Assert.Equal("patch.stale.repository-context", result.PrimaryCode);
        Assert.Null(result.PrimaryBlockId);
        Assert.Empty(result.Targets);
    }

    [Theory]
    [InlineData("input", "patch.stale.input-identity")]
    [InlineData("profile", "patch.stale.target-profile")]
    public void RejectsOtherRootContextSubstitutionsInRegistryOrder(
        string substitution,
        string expectedCode)
    {
        using var fixture = PatchFixture.Create(
            "namespace N; public class C { public void M() { } }",
            DocumentationPatchRepositoryEncoding.Utf8);

        var result = new DocumentationPatchResolver().Resolve(
            fixture.ClassifiedSession,
            fixture.Request(
                inputIdentity: substitution == "input" ? "Other.csproj" : null,
                targetProfile: substitution == "profile"
                    ? TargetProfile.AssemblyVisible
                    : null));

        Assert.Equal(DocumentationPatchResolutionStatus.Stale, result.Status);
        Assert.Equal(expectedCode, result.PrimaryCode);
        Assert.Null(result.PrimaryBlockId);
    }

    [Fact]
    public void WrongCompilationContextStillValidatesSourceAndWinsWithinBlock()
    {
        using var fixture = PatchFixture.Create(
            "namespace N; public class C { public void M() { } }",
            DocumentationPatchRepositoryEncoding.Utf8);
        var wrongSymbol = new SymbolRef(
            "other.net10.0",
            fixture.SymbolRef.DocumentationCommentId);

        var result = new DocumentationPatchResolver().Resolve(
            fixture.ClassifiedSession,
            fixture.Request(symbolRef: wrongSymbol));

        Assert.Equal(DocumentationPatchResolutionStatus.Stale, result.Status);
        Assert.Equal("patch.stale.compilation-context", result.PrimaryCode);
        Assert.Equal("block-1", result.PrimaryBlockId);
    }

    [Theory]
    [InlineData("namespace N; public class C { public void M() { } }", DocumentationPatchEditKind.Insert, true)]
    [InlineData("namespace N; public class C { /// <summary>Hi</summary>\n public void M() { } }", DocumentationPatchEditKind.Replace, true)]
    [InlineData("namespace N; public class C { ///\n public void M() { } }", DocumentationPatchEditKind.Replace, true)]
    [InlineData("namespace N;\npublic class C\n{\n    /// <!-- broken\n    public void M() { }\n}", DocumentationPatchEditKind.Replace, false)]
    [InlineData("namespace N; public class C { /// <summary>Hi</summary>\n public void M() { } }", DocumentationPatchEditKind.Insert, false)]
    public void EnforcesDirectDocumentationEditState(
        string source,
        DocumentationPatchEditKind editKind,
        bool expectedResolved)
    {
        using var fixture = PatchFixture.Create(
            source,
            DocumentationPatchRepositoryEncoding.Utf8);

        var result = new DocumentationPatchResolver().Resolve(
            fixture.ClassifiedSession,
            fixture.Request(editKind: editKind));

        Assert.Equal(
            expectedResolved
                ? DocumentationPatchResolutionStatus.Resolved
                : DocumentationPatchResolutionStatus.Rejected,
            result.Status);
        if (!expectedResolved)
        {
            Assert.Equal("patch.rejected.edit-state", result.PrimaryCode);
            Assert.Empty(result.Targets);
        }
    }

    [Fact]
    public void SourceBytesDriftPrecedesUnsupportedClassification()
    {
        using var fixture = PatchFixture.Create(
            "namespace N; public class C { public void M() { } }",
            DocumentationPatchRepositoryEncoding.Utf8,
            supportStatus: SupportStatus.Unsupported);
        File.AppendAllText(fixture.SourcePath, " ", new UTF8Encoding(false));

        var result = new DocumentationPatchResolver().Resolve(
            fixture.ClassifiedSession,
            fixture.Request());

        Assert.Equal(DocumentationPatchResolutionStatus.Stale, result.Status);
        Assert.Equal("patch.stale.source-bytes", result.PrimaryCode);
        Assert.Empty(result.Targets);
    }

    [Fact]
    public void EncodingOnlyDriftFailsBeforeHandoff()
    {
        using var fixture = PatchFixture.Create(
            "namespace N; public class C { public void M() { } }",
            DocumentationPatchRepositoryEncoding.Utf8);
        File.WriteAllBytes(
            fixture.SourcePath,
            new UTF8Encoding(true).GetPreamble()
                .Concat(Encoding.UTF8.GetBytes(fixture.SourceText))
                .ToArray());

        var result = new DocumentationPatchResolver().Resolve(
            fixture.ClassifiedSession,
            fixture.Request());

        Assert.Equal(DocumentationPatchResolutionStatus.Stale, result.Status);
        Assert.Equal("patch.stale.source-encoding", result.PrimaryCode);
        Assert.Empty(result.Targets);
    }

    [Theory]
    [InlineData("missing", "patch.stale.source-bytes")]
    [InlineData("malformed", "patch.stale.source-encoding")]
    public void MissingOrMalformedRepositorySourceFailsClosed(
        string mutation,
        string expectedCode)
    {
        using var fixture = PatchFixture.Create(
            "namespace N; public class C { public void M() { } }",
            DocumentationPatchRepositoryEncoding.Utf8);
        if (mutation == "missing")
        {
            File.Delete(fixture.SourcePath);
        }
        else
        {
            File.WriteAllBytes(fixture.SourcePath, [0xc3, 0x28]);
        }

        var result = new DocumentationPatchResolver().Resolve(
            fixture.ClassifiedSession,
            fixture.Request());

        Assert.Equal(DocumentationPatchResolutionStatus.Stale, result.Status);
        Assert.Equal(expectedCode, result.PrimaryCode);
        Assert.Empty(result.Targets);
    }

    [Fact]
    public void MultiDeclaratorFieldIsAmbiguous()
    {
        using var fixture = PatchFixture.Create(
            "namespace N; public class C { public int A, B; }",
            DocumentationPatchRepositoryEncoding.Utf8,
            declarationName: "A",
            primaryKind: PrimarySymbolKind.Field);

        var result = new DocumentationPatchResolver().Resolve(
            fixture.ClassifiedSession,
            fixture.Request());

        Assert.Equal(DocumentationPatchResolutionStatus.Rejected, result.Status);
        Assert.Equal("patch.rejected.ambiguous-target", result.PrimaryCode);
    }

    [Theory]
    [InlineData("namespace N; public class Outer { public class Inner { public void M() { } } }", "M", 0, PrimarySymbolKind.Method)]
    [InlineData("namespace N; public class C { public void M() { } public void M(string value) { } }", "M", 1, PrimarySymbolKind.Method)]
    [InlineData("namespace N; public interface I { void M(); }", "M", 0, PrimarySymbolKind.Method)]
    [InlineData("namespace N; public record R;", "R", 0, PrimarySymbolKind.Class)]
    public void ResolvesOrdinaryNestedOverloadedInterfaceAndRecordDeclarations(
        string source,
        string declarationName,
        int occurrence,
        PrimarySymbolKind primaryKind)
    {
        using var fixture = PatchFixture.Create(
            source,
            DocumentationPatchRepositoryEncoding.Utf8,
            declarationName,
            occurrence,
            primaryKind);

        var result = new DocumentationPatchResolver().Resolve(
            fixture.ClassifiedSession,
            fixture.Request());

        Assert.Equal(DocumentationPatchResolutionStatus.Resolved, result.Status);
        Assert.Single(result.Targets);
    }

    [Theory]
    [InlineData("namespace N; public class C { public int A; }", "A", PrimarySymbolKind.Field)]
    [InlineData("using System; namespace N; public class C { public event Action? E; }", "E", PrimarySymbolKind.Event)]
    public void SingleDeclaratorFieldAndEventOwnersResolve(
        string source,
        string declarationName,
        PrimarySymbolKind primaryKind)
    {
        using var fixture = PatchFixture.Create(
            source,
            DocumentationPatchRepositoryEncoding.Utf8,
            declarationName,
            primaryKind: primaryKind);

        var result = new DocumentationPatchResolver().Resolve(
            fixture.ClassifiedSession,
            fixture.Request());

        Assert.Equal(DocumentationPatchResolutionStatus.Resolved, result.Status);
    }

    [Fact]
    public void MixedOriginTargetIsAmbiguousEvenWithRepositoryDeclaration()
    {
        using var fixture = PatchFixture.Create(
            "namespace N; public class C { public void M() { } }",
            DocumentationPatchRepositoryEncoding.Utf8,
            origin: ClassificationOrigin.Mixed);

        var result = new DocumentationPatchResolver().Resolve(
            fixture.ClassifiedSession,
            fixture.Request());

        Assert.Equal(DocumentationPatchResolutionStatus.Rejected, result.Status);
        Assert.Equal("patch.rejected.ambiguous-target", result.PrimaryCode);
    }

    [Fact]
    public void UnresolvedAmbiguousClassificationRowIsCorrelatedByExactLocator()
    {
        using var fixture = PatchFixture.Create(
            "namespace N; public class C { public void M() { } }",
            DocumentationPatchRepositoryEncoding.Utf8,
            unresolvedAmbiguous: true);

        var result = new DocumentationPatchResolver().Resolve(
            fixture.ClassifiedSession,
            fixture.Request());

        Assert.Equal(DocumentationPatchResolutionStatus.Rejected, result.Status);
        Assert.Equal("patch.rejected.ambiguous-target", result.PrimaryCode);
    }

    [Fact]
    public void WrongPathAndNonDeclarationSpanFailBeforeHandoff()
    {
        using var fixture = PatchFixture.Create(
            "namespace N; public class C { public void M() { } }",
            DocumentationPatchRepositoryEncoding.Utf8);
        var wrongPath = new DocumentationPatchRepositoryLocator(
            "Missing.cs",
            fixture.SourceSha256,
            fixture.Encoding,
            fixture.DeclarationSpan);
        var wrongSpan = new DocumentationPatchRepositoryLocator(
            "Sample.cs",
            fixture.SourceSha256,
            fixture.Encoding,
            DocumentationObservationInput.Span(0, 1));

        var pathResult = new DocumentationPatchResolver().Resolve(
            fixture.ClassifiedSession,
            fixture.Request(locator: wrongPath));
        var spanResult = new DocumentationPatchResolver().Resolve(
            fixture.ClassifiedSession,
            fixture.Request(locator: wrongSpan));

        Assert.Equal("patch.stale.source-bytes", pathResult.PrimaryCode);
        Assert.Equal("patch.stale.source-span", spanResult.PrimaryCode);
        Assert.Empty(pathResult.Targets);
        Assert.Empty(spanResult.Targets);
    }

    [Fact]
    public void ExplicitlyLocatedPartialTypePartResolves()
    {
        using var fixture = PatchFixture.Create(
            "namespace N; public partial class C { } public partial class C { public void M() { } }",
            DocumentationPatchRepositoryEncoding.Utf8,
            declarationName: "C",
            declarationOccurrence: 1,
            primaryKind: PrimarySymbolKind.Class,
            traits: [SymbolTrait.Partial]);

        var result = new DocumentationPatchResolver().Resolve(
            fixture.ClassifiedSession,
            fixture.Request());

        Assert.Equal(DocumentationPatchResolutionStatus.Resolved, result.Status);
        Assert.Equal(fixture.DeclarationSpan, Assert.Single(result.Targets).DeclarationSpan);
    }

    [Fact]
    public void PrimaryConstructorIsRejectedAsSharedTypeOwner()
    {
        using var fixture = PatchFixture.Create(
            "namespace N; public class C(int value) { }",
            DocumentationPatchRepositoryEncoding.Utf8,
            declarationName: "C",
            primaryKind: PrimarySymbolKind.Constructor,
            components: [(ComponentKind.Parameter, "parameter/0")],
            selectPrimaryConstructor: true);

        var result = new DocumentationPatchResolver().Resolve(
            fixture.ClassifiedSession,
            fixture.Request(applicableComponents:
            [
                new DocumentationPatchApplicableComponent(
                    DocumentationPatchComponentKind.Parameter,
                    "parameter/0",
                    "value"),
            ]));

        Assert.Equal(DocumentationPatchResolutionStatus.Rejected, result.Status);
        Assert.Equal("patch.rejected.unsupported-target", result.PrimaryCode);
    }

    [Fact]
    public void TypeSharingPrimaryConstructorOwnerIsAlsoUnsupported()
    {
        using var fixture = PatchFixture.Create(
            "namespace N; public class C(int value) { }",
            DocumentationPatchRepositoryEncoding.Utf8,
            declarationName: "C",
            primaryKind: PrimarySymbolKind.Class,
            includePrimaryConstructorTarget: true);

        var result = new DocumentationPatchResolver().Resolve(
            fixture.ClassifiedSession,
            fixture.Request());

        Assert.Equal(DocumentationPatchResolutionStatus.Rejected, result.Status);
        Assert.Equal("patch.rejected.unsupported-target", result.PrimaryCode);
    }

    [Fact]
    public void ExactApplicableComponentsArePreservedAndMismatchFailsClosed()
    {
        using var fixture = PatchFixture.Create(
            "namespace N; public class C { public T M<T>(T value) => value; }",
            DocumentationPatchRepositoryEncoding.Utf8,
            components:
            [
                (ComponentKind.TypeParameter, "type-parameter/0"),
                (ComponentKind.Parameter, "parameter/0"),
                (ComponentKind.Return, "return"),
            ]);

        var resolved = new DocumentationPatchResolver().Resolve(
            fixture.ClassifiedSession,
            fixture.Request(applicableComponents:
            [
                new DocumentationPatchApplicableComponent(
                    DocumentationPatchComponentKind.TypeParameter,
                    "type-parameter/0",
                    "T"),
                new DocumentationPatchApplicableComponent(
                    DocumentationPatchComponentKind.Parameter,
                    "parameter/0",
                    "value"),
                new DocumentationPatchApplicableComponent(
                    DocumentationPatchComponentKind.Return,
                    "return",
                    null),
            ]));
        var rejected = new DocumentationPatchResolver().Resolve(
            fixture.ClassifiedSession,
            fixture.Request(applicableComponents:
            [
                new DocumentationPatchApplicableComponent(
                    DocumentationPatchComponentKind.Parameter,
                    "parameter/0",
                    "wrong"),
            ]));

        Assert.Equal(DocumentationPatchResolutionStatus.Resolved, resolved.Status);
        Assert.Equal(3, Assert.Single(resolved.Targets).ApplicableComponents.Length);
        Assert.Equal(DocumentationPatchResolutionStatus.Rejected, rejected.Status);
        Assert.Equal("patch.rejected.unsafe-change", rejected.PrimaryCode);
    }

    [Fact]
    public void PartialMethodUsesDocumentedImplementationAndRevalidatesBothParts()
    {
        const string source = """
            namespace N;
            public partial class C
            {
                partial void M();
                /// <summary>implementation</summary>
                partial void M() { }
            }
            """;
        using var fixture = PatchFixture.Create(
            source,
            DocumentationPatchRepositoryEncoding.Utf8,
            declarationOccurrence: 1,
            traits: [SymbolTrait.Partial]);

        var resolved = new DocumentationPatchResolver().Resolve(
            fixture.ClassifiedSession,
            fixture.Request(editKind: DocumentationPatchEditKind.Replace));
        Assert.Equal(DocumentationPatchResolutionStatus.Resolved, resolved.Status);

        var changed = source.Replace(
            "partial void M() { }",
            "partial void M() { int value = 0; }",
            StringComparison.Ordinal);
        File.WriteAllBytes(fixture.SourcePath, Encoding.UTF8.GetBytes(changed));
        var stale = new DocumentationPatchResolver().Resolve(
            fixture.ClassifiedSession,
            fixture.Request(editKind: DocumentationPatchEditKind.Replace));
        Assert.Equal(DocumentationPatchResolutionStatus.Stale, stale.Status);
        Assert.Equal("patch.stale.source-bytes", stale.PrimaryCode);
        Assert.Empty(stale.Targets);
    }

    [Fact]
    public void PartialMethodUsesParameterNameFromDocumentedImplementation()
    {
        const string source = """
            namespace N;
            public partial class C
            {
                public partial void Run(string defining);
                /// <summary>implementation</summary>
                public partial void Run(string implementing) { }
            }
            """;
        using var fixture = PatchFixture.Create(
            source,
            DocumentationPatchRepositoryEncoding.Utf8,
            declarationName: "Run",
            declarationOccurrence: 1,
            components: [(ComponentKind.Parameter, "parameter/0")],
            traits: [SymbolTrait.Partial]);

        var resolved = new DocumentationPatchResolver().Resolve(
            fixture.ClassifiedSession,
            fixture.Request(
                editKind: DocumentationPatchEditKind.Replace,
                applicableComponents:
                [
                    new DocumentationPatchApplicableComponent(
                        DocumentationPatchComponentKind.Parameter,
                        "parameter/0",
                        "implementing"),
                ]));
        var rejected = new DocumentationPatchResolver().Resolve(
            fixture.ClassifiedSession,
            fixture.Request(
                editKind: DocumentationPatchEditKind.Replace,
                applicableComponents:
                [
                    new DocumentationPatchApplicableComponent(
                        DocumentationPatchComponentKind.Parameter,
                        "parameter/0",
                        "defining"),
                ]));

        Assert.Equal(DocumentationPatchResolutionStatus.Resolved, resolved.Status);
        Assert.Equal(
            "implementing",
            Assert.Single(Assert.Single(resolved.Targets).ApplicableComponents).Name);
        Assert.Equal(DocumentationPatchResolutionStatus.Rejected, rejected.Status);
        Assert.Equal("patch.rejected.unsafe-change", rejected.PrimaryCode);
    }

    [Fact]
    public void PartialMethodFallbackUsesParameterNameFromDocumentedDefinition()
    {
        const string source = """
            namespace N;
            public partial class C
            {
                /// <summary>definition</summary>
                public partial void Run(string defining);
                public partial void Run(string implementing) { }
            }
            """;
        using var fixture = PatchFixture.Create(
            source,
            DocumentationPatchRepositoryEncoding.Utf8,
            declarationName: "Run",
            components: [(ComponentKind.Parameter, "parameter/0")],
            traits: [SymbolTrait.Partial]);

        var result = new DocumentationPatchResolver().Resolve(
            fixture.ClassifiedSession,
            fixture.Request(
                editKind: DocumentationPatchEditKind.Replace,
                applicableComponents:
                [
                    new DocumentationPatchApplicableComponent(
                        DocumentationPatchComponentKind.Parameter,
                        "parameter/0",
                        "defining"),
                ]));

        Assert.Equal(DocumentationPatchResolutionStatus.Resolved, result.Status);
        Assert.Equal(
            "defining",
            Assert.Single(Assert.Single(result.Targets).ApplicableComponents).Name);
    }

    [Fact]
    public void PartialMethodUsesTypeParameterNameFromDocumentedImplementation()
    {
        const string source = """
            namespace N;
            public partial class C
            {
                public partial void Map<TDefinition>(TDefinition value);
                /// <summary>implementation</summary>
                public partial void Map<TImplementation>(TImplementation value) { }
            }
            """;
        using var fixture = PatchFixture.Create(
            source,
            DocumentationPatchRepositoryEncoding.Utf8,
            declarationName: "Map",
            declarationOccurrence: 1,
            components: [(ComponentKind.TypeParameter, "type-parameter/0")],
            traits: [SymbolTrait.Partial]);

        var result = new DocumentationPatchResolver().Resolve(
            fixture.ClassifiedSession,
            fixture.Request(
                editKind: DocumentationPatchEditKind.Replace,
                applicableComponents:
                [
                    new DocumentationPatchApplicableComponent(
                        DocumentationPatchComponentKind.TypeParameter,
                        "type-parameter/0",
                        "TImplementation"),
                ]));

        Assert.Equal(DocumentationPatchResolutionStatus.Resolved, result.Status);
        Assert.Equal(
            "TImplementation",
            Assert.Single(Assert.Single(result.Targets).ApplicableComponents).Name);
    }

    [Fact]
    public void GenericDelegateUsesNamesFromItsSelectedDeclaration()
    {
        const string source = "namespace N; public delegate TResult Transform<TValue, TResult>(TValue value);";
        using var fixture = PatchFixture.Create(
            source,
            DocumentationPatchRepositoryEncoding.Utf8,
            declarationName: "Transform",
            primaryKind: PrimarySymbolKind.Delegate,
            useRealClassifier: true);

        var result = new DocumentationPatchResolver().Resolve(
            fixture.ClassifiedSession,
            fixture.Request(applicableComponents:
            [
                new DocumentationPatchApplicableComponent(
                    DocumentationPatchComponentKind.TypeParameter,
                    "type-parameter/0",
                    "TValue"),
                new DocumentationPatchApplicableComponent(
                    DocumentationPatchComponentKind.TypeParameter,
                    "type-parameter/1",
                    "TResult"),
                new DocumentationPatchApplicableComponent(
                    DocumentationPatchComponentKind.Parameter,
                    "parameter/0",
                    "value"),
                new DocumentationPatchApplicableComponent(
                    DocumentationPatchComponentKind.Return,
                    "return",
                    null),
            ]));

        Assert.Equal(DocumentationPatchResolutionStatus.Resolved, result.Status);
        Assert.Equal(4, Assert.Single(result.Targets).ApplicableComponents.Length);
    }

    [Theory]
    [InlineData(
        "namespace N; public partial class C { public partial int Value { get; }\n/// <summary>implementation</summary>\npublic partial int Value { get => 1; } }",
        "Value",
        1,
        PrimarySymbolKind.Property)]
    [InlineData(
        "namespace N; public partial class C { public partial int this[int value] { get; }\n/// <summary>implementation</summary>\npublic partial int this[int value] { get => value; } }",
        "this",
        1,
        PrimarySymbolKind.Indexer)]
    [InlineData(
        "using System; namespace N; public partial class C { public partial event Action Changed;\n/// <summary>implementation</summary>\npublic partial event Action Changed { add { } remove { } } }",
        "Changed",
        1,
        PrimarySymbolKind.Event)]
    public void NonMethodPartialMembersAreAmbiguous(
        string source,
        string declarationName,
        int declarationOccurrence,
        PrimarySymbolKind primaryKind)
    {
        using var fixture = PatchFixture.Create(
            source,
            DocumentationPatchRepositoryEncoding.Utf8,
            declarationName,
            declarationOccurrence,
            primaryKind,
            useRealClassifier: true);

        var result = new DocumentationPatchResolver().Resolve(
            fixture.ClassifiedSession,
            fixture.Request(editKind: DocumentationPatchEditKind.Replace));

        Assert.Equal(DocumentationPatchResolutionStatus.Rejected, result.Status);
        Assert.Equal("patch.rejected.ambiguous-target", result.PrimaryCode);
        Assert.Empty(result.Targets);
    }

    [Theory]
    [InlineData("well-formed")]
    [InlineData("whitespace")]
    [InlineData("malformed")]
    [InlineData("changed")]
    [InlineData("deleted")]
    public void PartialDefinitionFallbackRevalidatesSeparateImplementationFile(
        string mutation)
    {
        const string definition = """
            namespace N;
            public partial class C
            {
                /// <summary>definition</summary>
                partial void M();
            }
            """;
        const string implementation = """
            namespace N;
            public partial class C
            {
                partial void M() { }
            }
            """;
        using var fixture = PatchFixture.Create(
            definition,
            DocumentationPatchRepositoryEncoding.Utf8,
            traits: [SymbolTrait.Partial],
            secondarySource: implementation);
        var initial = new DocumentationPatchResolver().Resolve(
            fixture.ClassifiedSession,
            fixture.Request(editKind: DocumentationPatchEditKind.Replace));
        Assert.Equal(DocumentationPatchResolutionStatus.Resolved, initial.Status);

        var mutated = mutation switch
        {
            "well-formed" => implementation.Replace(
                "partial void M()",
                "/// <summary>implementation</summary>\n    partial void M()",
                StringComparison.Ordinal),
            "whitespace" => implementation.Replace(
                "partial void M()",
                "///\n    partial void M()",
                StringComparison.Ordinal),
            "malformed" => implementation.Replace(
                "partial void M()",
                "/// <!-- broken\n    partial void M()",
                StringComparison.Ordinal),
            "changed" => implementation.Replace("{ }", "{ int value = 0; }", StringComparison.Ordinal),
            "deleted" => null,
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        if (mutated is null)
        {
            File.Delete(fixture.SecondarySourcePath!);
        }
        else
        {
            File.WriteAllBytes(fixture.SecondarySourcePath!, Encoding.UTF8.GetBytes(mutated));
        }

        var stale = new DocumentationPatchResolver().Resolve(
            fixture.ClassifiedSession,
            fixture.Request(editKind: DocumentationPatchEditKind.Replace));

        Assert.Equal(DocumentationPatchResolutionStatus.Stale, stale.Status);
        Assert.Equal("patch.stale.source-bytes", stale.PrimaryCode);
        Assert.Empty(stale.Targets);
    }

    [Fact]
    public void ToolGeneratedTargetIsNonWritableAndStaleHashWins()
    {
        using var fixture = PatchFixture.Create(
            "namespace N; public class C { public void M() { } }",
            DocumentationPatchRepositoryEncoding.Utf8,
            sourceKind: LoadedSourceKind.ToolGenerated);

        var nonWritable = new DocumentationPatchResolver().Resolve(
            fixture.ClassifiedSession,
            fixture.GeneratedRequest());
        var stale = new DocumentationPatchResolver().Resolve(
            fixture.ClassifiedSession,
            fixture.GeneratedRequest(sourceSha256: new string('0', 64)));

        Assert.Equal(DocumentationPatchResolutionStatus.Rejected, nonWritable.Status);
        Assert.Equal("patch.rejected.non-writable-target", nonWritable.PrimaryCode);
        Assert.Equal(DocumentationPatchResolutionStatus.Stale, stale.Status);
        Assert.Equal("patch.stale.source-bytes", stale.PrimaryCode);
    }

    [Fact]
    public void CancellationPublishesNoPartialHandoff()
    {
        using var fixture = PatchFixture.Create(
            "namespace N; public class C { public void M() { } }",
            DocumentationPatchRepositoryEncoding.Utf8);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            new DocumentationPatchResolver().Resolve(
                fixture.ClassifiedSession,
                fixture.Request(),
                cancellation.Token));
    }

    private sealed class PatchFixture : IDisposable
    {
        private readonly LoadedRepositorySession repositorySession;
        private readonly DocumentationPatchBlockRequest block;

        private PatchFixture(
            string root,
            string sourcePath,
            string? secondarySourcePath,
            string sourceText,
            DocumentationPatchRepositoryEncoding encoding,
            RepositoryContextRef repositoryContextRef,
            SymbolRef symbolRef,
            Utf16Span declarationSpan,
            string sourceSha256,
            LoadedRepositorySession repositorySession,
            ClassifiedRepositorySession classifiedSession,
            DocumentationPatchBlockRequest block)
        {
            Root = root;
            SourcePath = sourcePath;
            SecondarySourcePath = secondarySourcePath;
            SourceText = sourceText;
            Encoding = encoding;
            RepositoryContextRef = repositoryContextRef;
            SymbolRef = symbolRef;
            DeclarationSpan = declarationSpan;
            SourceSha256 = sourceSha256;
            this.repositorySession = repositorySession;
            ClassifiedSession = classifiedSession;
            this.block = block;
        }

        public string Root { get; }

        public string SourcePath { get; }

        public string? SecondarySourcePath { get; }

        public string SourceText { get; }

        public DocumentationPatchRepositoryEncoding Encoding { get; }

        public RepositoryContextRef RepositoryContextRef { get; }

        public SymbolRef SymbolRef { get; }

        public Utf16Span DeclarationSpan { get; }

        public string SourceSha256 { get; }

        public ClassifiedRepositorySession ClassifiedSession { get; }

        public static PatchFixture Create(
            string source,
            DocumentationPatchRepositoryEncoding encoding,
            string declarationName = "M",
            int declarationOccurrence = 0,
            PrimarySymbolKind primaryKind = PrimarySymbolKind.Method,
            SupportStatus supportStatus = SupportStatus.Supported,
            ImmutableArray<(ComponentKind Kind, string Identity)> components = default,
            ImmutableArray<SymbolTrait> traits = default,
            LoadedSourceKind sourceKind = LoadedSourceKind.Repository,
            bool selectPrimaryConstructor = false,
            string? secondarySource = null,
            ClassificationOrigin? origin = null,
            bool includePrimaryConstructorTarget = false,
            bool unresolvedAmbiguous = false,
            bool useRealClassifier = false)
        {
            var root = Path.Join(
                Path.GetTempPath(),
                "contract-scribe-patch-resolution-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var sourcePath = Path.Join(root, "Sample.cs");
            var exactBytes = Encode(source, encoding);
            File.WriteAllBytes(sourcePath, exactBytes);
            var secondarySourcePath = secondarySource is null
                ? null
                : Path.Join(root, "Secondary.cs");
            if (secondarySourcePath is not null)
            {
                File.WriteAllBytes(secondarySourcePath, Encode(secondarySource!, encoding));
            }

            var syntaxTrees = ImmutableArray.CreateBuilder<SyntaxTree>();
            syntaxTrees.Add(CSharpSyntaxTree.ParseText(
                source,
                new CSharpParseOptions(
                    LanguageVersion.Preview,
                    documentationMode: DocumentationMode.Diagnose),
                sourcePath,
                System.Text.Encoding.UTF8));
            if (secondarySourcePath is not null)
            {
                syntaxTrees.Add(CSharpSyntaxTree.ParseText(
                    secondarySource!,
                    new CSharpParseOptions(
                        LanguageVersion.Preview,
                        documentationMode: DocumentationMode.Diagnose),
                    secondarySourcePath,
                    System.Text.Encoding.UTF8));
            }

            var compilation = CSharpCompilation.Create(
                "PatchFixture",
                syntaxTrees,
                PlatformReferences,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var candidates = compilation.SyntaxTrees
                .SelectMany(tree => tree.GetRoot().DescendantNodes())
                .Where(node => node switch
                {
                    MethodDeclarationSyntax method =>
                        string.Equals(method.Identifier.ValueText, declarationName, StringComparison.Ordinal),
                    VariableDeclaratorSyntax variable =>
                        string.Equals(variable.Identifier.ValueText, declarationName, StringComparison.Ordinal),
                    TypeDeclarationSyntax type =>
                        string.Equals(type.Identifier.ValueText, declarationName, StringComparison.Ordinal),
                    DelegateDeclarationSyntax @delegate =>
                        string.Equals(@delegate.Identifier.ValueText, declarationName, StringComparison.Ordinal),
                    PropertyDeclarationSyntax property =>
                        string.Equals(property.Identifier.ValueText, declarationName, StringComparison.Ordinal),
                    IndexerDeclarationSyntax =>
                        string.Equals(declarationName, "this", StringComparison.Ordinal),
                    EventDeclarationSyntax @event =>
                        string.Equals(@event.Identifier.ValueText, declarationName, StringComparison.Ordinal),
                    _ => false,
                })
                .ToArray();
            SyntaxNode declaration = candidates[declarationOccurrence];
            var model = compilation.GetSemanticModel(declaration.SyntaxTree);
            ISymbol symbol;
            if (selectPrimaryConstructor)
            {
                var type = Assert.IsAssignableFrom<INamedTypeSymbol>(
                    model.GetDeclaredSymbol(declaration));
                symbol = Assert.Single(type.InstanceConstructors, constructor =>
                    !constructor.IsImplicitlyDeclared
                    && constructor.Parameters.Length != 0);
                declaration = Assert.Single(symbol.DeclaringSyntaxReferences)
                    .GetSyntax();
            }
            else
            {
                symbol = model.GetDeclaredSymbol(declaration)
                    ?? throw new InvalidOperationException("Fixture declaration did not bind.");
            }
            var documentationId = symbol.GetDocumentationCommentId()
                ?? throw new InvalidOperationException("Fixture symbol has no documentation ID.");
            const string compilationContextRef = "fixture.net10.0";
            var symbolRef = new SymbolRef(compilationContextRef, documentationId);
            var reference = symbol.DeclaringSyntaxReferences.Single(candidate =>
                candidate.Span.Start == declaration.Span.Start);
            var declarationSpan = DocumentationObservationInput.Span(
                reference.Span.Start,
                reference.Span.End);
            var selectedBytes = File.ReadAllBytes(reference.SyntaxTree.FilePath);
            var sourceSha256 = Convert.ToHexString(SHA256.HashData(selectedBytes))
                .ToLowerInvariant();
            var generatedFact = sourceKind == LoadedSourceKind.Repository
                ? null
                : new GeneratedSourceFact(
                    "Fixture.csproj",
                    compilationContextRef,
                    "producer",
                    "output",
                    Sha256Utf8(source),
                    source);
            var workspace = new AdhocWorkspace();
            var adhocProject = workspace.AddProject("Fixture", LanguageNames.CSharp);
            var loadedProject = new LoadedProject(
                "Fixture.csproj",
                "net10.0",
                compilationContextRef,
                LoadedProjectRole.AuditRoot,
                [],
                adhocProject,
                compilation,
                compilation.SyntaxTrees.ToDictionary(
                    tree => tree,
                    tree => new LoadedSourceTree(
                        sourceKind,
                        sourceKind == LoadedSourceKind.Repository
                            ? Path.GetFileName(tree.FilePath)
                            : null,
                        generatedFact)));
            Assert.True(RepositoryContextRef.TryParse(
                "repoctx-0123456789abcdef0123456789abcdef",
                out var repositoryContextRef));
            var repositorySession = new LoadedRepositorySession(
                repositoryContextRef,
                root,
                "Fixture.csproj",
                new ToolchainIdentity("test", "test", "test", "test"),
                [loadedProject],
                generatedFact is null ? [] : [generatedFact],
                workspace);
            var targets = ImmutableArray.CreateBuilder<TargetClassification>();
            if (!unresolvedAmbiguous)
            {
                targets.Add(new TargetClassification(
                    symbolRef,
                    primaryKind,
                    traits.IsDefault ? [] : traits,
                    origin ?? sourceKind switch
                    {
                        LoadedSourceKind.Repository => ClassificationOrigin.Source,
                        LoadedSourceKind.SourceGenerator => ClassificationOrigin.SourceGenerator,
                        LoadedSourceKind.ToolGenerated => ClassificationOrigin.ToolGenerated,
                        _ => ClassificationOrigin.Unknown,
                    },
                    supportStatus,
                    supportStatus == SupportStatus.Supported
                        ? null
                        : SkipReason.UnsupportedSymbolKind));
            }
            if (includePrimaryConstructorTarget)
            {
                var named = Assert.IsAssignableFrom<INamedTypeSymbol>(symbol);
                var constructor = Assert.Single(named.InstanceConstructors, candidate =>
                    !candidate.IsImplicitlyDeclared && candidate.Parameters.Length != 0);
                targets.Add(new TargetClassification(
                    new SymbolRef(
                        compilationContextRef,
                        constructor.GetDocumentationCommentId()!),
                    PrimarySymbolKind.Constructor,
                    [],
                    ClassificationOrigin.Source,
                    SupportStatus.Supported));
            }

            var classificationSet = new ClassificationSet(
                TargetProfile.ExternalApi,
                targets.ToImmutable(),
                components.IsDefault
                    ? []
                    : components.Select(component => new ComponentClassification(
                        symbolRef,
                        component.Kind,
                        component.Identity,
                        ClassificationOrigin.Source,
                        SupportStatus.Supported)).ToImmutableArray(),
                [],
                unresolvedAmbiguous
                    ? [new UnresolvedClassification(
                        compilationContextRef,
                        ClassificationOrigin.Source,
                        SupportStatus.Ambiguous,
                        SkipReason.AmbiguousPartialDeclaration,
                        new RepositoryCandidateLocator(
                            Path.GetFileName(reference.SyntaxTree.FilePath),
                            declarationSpan))]
                    : []);
            var classified = useRealClassifier
                ? new SymbolClassifier().ClassifySession(
                    repositorySession,
                    TargetProfile.ExternalApi)
                : ClassifiedRepositorySession.Bind(
                    repositorySession,
                    ClassificationOutcome.Success(classificationSet));
            var locator = new DocumentationPatchRepositoryLocator(
                Path.GetFileName(reference.SyntaxTree.FilePath),
                sourceSha256,
                encoding,
                declarationSpan);
            var block = new DocumentationPatchBlockRequest(
                "block-1",
                symbolRef,
                locator,
                DocumentationPatchEditKind.Insert,
                [],
                new DocumentationPatchInheritDocContent(),
                []);
            return new PatchFixture(
                root,
                sourcePath,
                secondarySourcePath,
                source,
                encoding,
                repositoryContextRef,
                symbolRef,
                declarationSpan,
                sourceSha256,
                repositorySession,
                classified,
                block);
        }

        public DocumentationPatchRequest Request(
            RepositoryContextRef? repositoryContextRef = null,
            DocumentationPatchEditKind? editKind = null,
            ImmutableArray<DocumentationPatchApplicableComponent> applicableComponents = default,
            string? inputIdentity = null,
            TargetProfile? targetProfile = null,
            SymbolRef? symbolRef = null,
            DocumentationPatchSourceLocator? locator = null)
        {
            var actualBlock = new DocumentationPatchBlockRequest(
                block.BlockId,
                symbolRef ?? block.SymbolRef,
                locator ?? block.Locator,
                editKind ?? block.EditKind,
                applicableComponents.IsDefault ? block.ApplicableComponents : applicableComponents,
                block.Content,
                block.ProvenanceRefs);
            return new DocumentationPatchRequest(
                new string('0', 64),
                new DocumentationPatchContext(
                    repositoryContextRef ?? RepositoryContextRef,
                    inputIdentity ?? "Fixture.csproj",
                    targetProfile ?? TargetProfile.ExternalApi),
                [],
                [actualBlock]);
        }

        public DocumentationPatchRequest GeneratedRequest(string? sourceSha256 = null)
        {
            var locator = new DocumentationPatchToolGeneratedLocator(
                "producer",
                "output",
                sourceSha256 ?? Sha256Utf8(SourceText),
                DeclarationSpan);
            return new DocumentationPatchRequest(
                new string('0', 64),
                new DocumentationPatchContext(
                    RepositoryContextRef,
                    "Fixture.csproj",
                    TargetProfile.ExternalApi),
                [],
                [new DocumentationPatchBlockRequest(
                    "block-1",
                    SymbolRef,
                    locator,
                    DocumentationPatchEditKind.Insert,
                    [],
                    new DocumentationPatchInheritDocContent(),
                    [])]);
        }

        public void Dispose()
        {
            repositorySession.Dispose();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private static byte[] Encode(
            string source,
            DocumentationPatchRepositoryEncoding encoding) => encoding switch
            {
                DocumentationPatchRepositoryEncoding.Utf8 =>
                    new UTF8Encoding(false, true).GetBytes(source),
                DocumentationPatchRepositoryEncoding.Utf8Bom =>
                    new UTF8Encoding(true, true).GetPreamble()
                        .Concat(new UTF8Encoding(false, true).GetBytes(source))
                        .ToArray(),
                DocumentationPatchRepositoryEncoding.Utf16LittleEndianBom =>
                    new UnicodeEncoding(false, true, true).GetPreamble()
                        .Concat(new UnicodeEncoding(false, false, true).GetBytes(source))
                        .ToArray(),
                DocumentationPatchRepositoryEncoding.Utf16BigEndianBom =>
                    new UnicodeEncoding(true, true, true).GetPreamble()
                        .Concat(new UnicodeEncoding(true, false, true).GetBytes(source))
                        .ToArray(),
                _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
            };

        private static string Sha256Utf8(string source) =>
            Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source)))
                .ToLowerInvariant();

        private static ImmutableArray<MetadataReference> PlatformReferences { get; } =
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator)
                .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
                .ToImmutableArray();

    }

    private static byte[] LoadExpectedBytes(
        DocumentationPatchRepositoryEncoding encoding)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Join(directory.FullName, "ContractScribe.slnx")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName
            ?? throw new InvalidOperationException("Repository root not found.");
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Join(
            root,
            "tests",
            "fixtures",
            "documentation-patch",
            "resolution",
            "source-byte-vectors.json")));
        var id = encoding switch
        {
            DocumentationPatchRepositoryEncoding.Utf8 => "utf-8",
            DocumentationPatchRepositoryEncoding.Utf8Bom => "utf-8-bom",
            DocumentationPatchRepositoryEncoding.Utf16LittleEndianBom => "utf-16le-bom",
            DocumentationPatchRepositoryEncoding.Utf16BigEndianBom => "utf-16be-bom",
            _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
        };
        var vector = document.RootElement.GetProperty("vectors")
            .EnumerateArray()
            .Single(item => string.Equals(
                item.GetProperty("encoding").GetString(),
                id,
                StringComparison.Ordinal));
        var bytes = Convert.FromBase64String(vector.GetProperty("base64").GetString()!);
        Assert.Equal(vector.GetProperty("expectedLength").GetInt32(), bytes.Length);
        Assert.Equal(
            vector.GetProperty("expectedSha256").GetString(),
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        return bytes;
    }
}
