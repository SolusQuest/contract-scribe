using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.ContractBaselineProbe;
using ContractScribe.Core;
using ContractScribe.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed class ClassificationTests
{
    [Fact]
    public void ProfilesSelectExactAccessibilityAndExcludeDependencyTargets()
    {
        const string dependencySource = """
            public interface IDependency
            {
                void Call(int value);
            }

            public class DependencyBase
            {
                public virtual int Value { get; set; }
            }
            """;
        const string rootSource = """
            public class PublicApi : DependencyBase, IDependency
            {
                public override int Value { get; set; }
                public void Call(int value) { }
                internal void Internal() { }
                protected void Protected() { }
                protected internal void ProtectedInternal() { }
                private protected void PrivateProtected() { }
                private void Private() { }

                public class PublicNested { }
                internal class InternalNested { }
                private class PrivateNested { }
            }

            internal class InternalApi
            {
                internal void Internal() { }
            }

            public sealed class SealedApi
            {
                protected void ProtectedButNotDerivable() { }
                protected internal void ProtectedInternal() { }
            }

            file class FileLocal { }
            """;

        var dependency = Compile("Dependency", [Source("Library/Library.cs", dependencySource)]);
        var root = Compile(
            "Root",
            [Source("App/App.cs", rootSource)],
            [dependency.Compilation.ToMetadataReference()]);
        using var session = CreateSession(
            new ProjectFixture(
                "App/App.csproj",
                "ctx-app",
                LoadedProjectRole.AuditRoot,
                root,
                ["Library/Library.csproj"]),
            new ProjectFixture(
                "Library/Library.csproj",
                "ctx-library",
                LoadedProjectRole.DependencyOnly,
                dependency));
        var classifier = new SymbolClassifier();

        var external = AssertSuccess(classifier.Classify(
            session,
            TargetProfile.ExternalApi));
        var assemblyVisible = AssertSuccess(classifier.Classify(
            session,
            TargetProfile.AssemblyVisible));

        Assert.Contains(external.Targets, target =>
            target.SymbolRef.DocumentationCommentId == "T:PublicApi");
        Assert.Contains(external.Targets, target =>
            target.SymbolRef.DocumentationCommentId == "M:PublicApi.Protected");
        Assert.DoesNotContain(external.Targets, target =>
            target.SymbolRef.DocumentationCommentId is "M:PublicApi.Internal"
                or "T:InternalApi"
                or "M:InternalApi.Internal"
                or "T:PublicApi.InternalNested");
        Assert.DoesNotContain(external.Targets, target =>
            target.SymbolRef.DocumentationCommentId.Contains(
                "PrivateProtected",
                StringComparison.Ordinal));
        Assert.DoesNotContain(external.Targets, target =>
            target.SymbolRef.DocumentationCommentId.Contains(
                "ProtectedButNotDerivable",
                StringComparison.Ordinal));
        Assert.DoesNotContain(external.Targets, target =>
            target.SymbolRef.CompilationContextRef == "ctx-library");
        Assert.DoesNotContain(external.Components, component =>
            component.ParentSymbolRef.CompilationContextRef == "ctx-library");
        Assert.DoesNotContain(external.Unresolved, unresolved =>
            unresolved.CompilationContextRef == "ctx-library");
        Assert.Contains(external.Relations, relation =>
            relation.RelationKind == RelationKind.Overrides
            && relation.SourceSymbolRef.DocumentationCommentId
                == "P:PublicApi.Value"
            && relation.TargetSymbolRef.DocumentationCommentId
                == "P:DependencyBase.Value");
        Assert.Contains(external.Relations, relation =>
            relation.RelationKind == RelationKind.ImplicitInterfaceImplementation
            && relation.SourceSymbolRef.DocumentationCommentId
                == "M:PublicApi.Call(System.Int32)"
            && relation.TargetSymbolRef.DocumentationCommentId
                == "M:IDependency.Call(System.Int32)");
        Assert.All(external.Relations, relation =>
        {
            Assert.Equal("ctx-app", relation.SourceSymbolRef.CompilationContextRef);
            Assert.Equal("ctx-app", relation.TargetSymbolRef.CompilationContextRef);
        });
        AssertOracleConforms(
            ClassificationConformanceOracle.Load(FindRepositoryRoot()),
            TargetProfile.ExternalApi,
            external,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [Frame("ctx-app") + Frame("P:PublicApi.Value")] =
                    "symbol.member.property",
                [Frame("ctx-app") + Frame("P:DependencyBase.Value")] =
                    "symbol.member.property",
                [Frame("ctx-app") + Frame("M:PublicApi.Call(System.Int32)")] =
                    "symbol.member.method",
                [Frame("ctx-app") + Frame("M:IDependency.Call(System.Int32)")] =
                    "symbol.member.method",
            });

        Assert.Contains(assemblyVisible.Targets, target =>
            target.SymbolRef.DocumentationCommentId == "T:InternalApi");
        Assert.Contains(assemblyVisible.Targets, target =>
            target.SymbolRef.DocumentationCommentId
                == "M:PublicApi.PrivateProtected");
        Assert.Contains(assemblyVisible.Targets, target =>
            target.SymbolRef.DocumentationCommentId
                == "M:SealedApi.ProtectedInternal");
        Assert.DoesNotContain(assemblyVisible.Targets, target =>
            target.SymbolRef.DocumentationCommentId.Contains(
                "ProtectedButNotDerivable",
                StringComparison.Ordinal));
        Assert.DoesNotContain(assemblyVisible.Targets, target =>
            target.SymbolRef.DocumentationCommentId.Contains(
                "PrivateNested",
                StringComparison.Ordinal));
        Assert.DoesNotContain(assemblyVisible.Targets, target =>
            target.SymbolRef.CompilationContextRef == "ctx-library");
    }

    [Fact]
    public void EmitsClosedKindsTraitsComponentsAndConstructorForms()
    {
        const string source = """
            using System;
            using System.Collections.Generic;
            using System.Threading.Tasks;

            public interface IContract<T>
            {
                T Convert(T value);
            }

            public enum Choice { First }
            public delegate int Transformer<T>(T value);
            public struct Point(int x)
            {
                public int X = x;
            }
            public struct EmptyStruct { }

            public record Person(string Name)
            {
                protected Person(Person other) { Name = other.Name; }
            }
            public record AutoRecord(string Value);

            public class Surface<T>(int primary) : IContract<T>
            {
                public int Field;
                public event Action? Changed;
                public required string Required { get; init; }
                public virtual int this[int index] { get => index; set { } }
                public T Convert(T value) => value;
                public static Surface<T> operator +(Surface<T> left, Surface<T> right) => left;
                public static implicit operator int(Surface<T> value) => 0;
                public async Task<int> Async() { await Task.Yield(); return 1; }
                public IEnumerable<int> Iterator() { yield return 1; }
                public Surface() : this(0) { }
                ~Surface() { }
            }
            """;
        var compilation = Compile("Kinds", [Source("Kinds.cs", source)]);
        using var session = CreateSession(new ProjectFixture(
            "Kinds.csproj",
            "ctx-kinds",
            LoadedProjectRole.AuditRoot,
            compilation));

        var set = AssertSuccess(new SymbolClassifier().Classify(
            session,
            TargetProfile.ExternalApi));

        Assert.Equal(
            Enum.GetValues<PrimarySymbolKind>()
                .Where(kind => kind != PrimarySymbolKind.Unknown)
                .OrderBy(ClassificationVocabulary.GetId, StringComparer.Ordinal),
            set.Targets
                .Select(target => target.PrimaryKind)
                .Distinct()
                .OrderBy(ClassificationVocabulary.GetId, StringComparer.Ordinal));
        Assert.Contains(set.Targets, target =>
            target.Traits.Contains(SymbolTrait.Generic));
        Assert.Contains(set.Targets, target =>
            target.Traits.Contains(SymbolTrait.RecordClass));
        Assert.Contains(set.Targets, target =>
            target.Traits.Contains(SymbolTrait.Required));
        Assert.Contains(set.Targets, target =>
            target.Traits.Contains(SymbolTrait.InitOnly));
        Assert.Contains(set.Targets, target =>
            target.Traits.Contains(SymbolTrait.Async));
        Assert.Contains(set.Targets, target =>
            target.Traits.Contains(SymbolTrait.Iterator));

        Assert.Contains(set.Targets, target =>
            target.PrimaryKind == PrimarySymbolKind.Constructor
            && target.SymbolRef.DocumentationCommentId.Contains(
                "Surface",
                StringComparison.Ordinal)
            && target.SymbolRef.DocumentationCommentId.Contains(
                "System.Int32",
                StringComparison.Ordinal));
        Assert.Contains(set.Targets, target =>
            target.PrimaryKind == PrimarySymbolKind.Constructor
            && target.SymbolRef.DocumentationCommentId.Contains(
                "Person.#ctor(Person)",
                StringComparison.Ordinal));
        Assert.Contains(set.Targets, target =>
            target.PrimaryKind == PrimarySymbolKind.Constructor
            && target.SymbolRef.DocumentationCommentId.Contains(
                "Point.#ctor(System.Int32)",
                StringComparison.Ordinal));
        Assert.DoesNotContain(set.Targets, target =>
            target.SymbolRef.DocumentationCommentId.Contains(
                "Invoke",
                StringComparison.Ordinal));
        Assert.DoesNotContain(set.Targets, target =>
            target.SymbolRef.DocumentationCommentId.Contains(
                "get_",
                StringComparison.Ordinal));

        var componentKinds = set.Components
            .Select(component => component.ComponentKind)
            .ToHashSet();
        Assert.Contains(ComponentKind.Parameter, componentKinds);
        Assert.Contains(ComponentKind.TypeParameter, componentKinds);
        Assert.Contains(ComponentKind.Return, componentKinds);
        Assert.Contains(ComponentKind.Value, componentKinds);
        Assert.Contains(ComponentKind.AccessorGet, componentKinds);
        Assert.Contains(ComponentKind.AccessorSet, componentKinds);
        Assert.Contains(ComponentKind.AccessorInit, componentKinds);
        Assert.Contains(ComponentKind.AccessorAdd, componentKinds);
        Assert.Contains(ComponentKind.AccessorRemove, componentKinds);
        Assert.Contains(ComponentKind.BackingField, componentKinds);
        Assert.Contains(ComponentKind.SynthesizedRecordPositionalProperty, componentKinds);
        Assert.Contains(ComponentKind.SynthesizedImplicitConstructor, componentKinds);
        Assert.Contains(ComponentKind.SynthesizedDelegateInvoke, componentKinds);
        Assert.Equal(
            Enum.GetValues<ComponentKind>()
                .Where(kind => kind != ComponentKind.Unknown)
                .OrderBy(ClassificationVocabulary.GetId, StringComparer.Ordinal),
            componentKinds.OrderBy(
                ClassificationVocabulary.GetId,
                StringComparer.Ordinal));
        Assert.Contains(set.Components, component =>
            component.ComponentKind == ComponentKind.Parameter
            && component.ParentSymbolRef.DocumentationCommentId.Contains(
                "Surface",
                StringComparison.Ordinal)
            && component.ParentSymbolRef.DocumentationCommentId.Contains(
                "System.Int32",
                StringComparison.Ordinal)
            && component.Identity == "parameter/0");
        Assert.Contains(set.Components, component =>
            component.ComponentKind == ComponentKind.SynthesizedRecordCopyConstructor
            && component.ParentSymbolRef.DocumentationCommentId == "T:AutoRecord");
        Assert.DoesNotContain(set.Components, component =>
            component.ComponentKind == ComponentKind.SynthesizedRecordCopyConstructor
            && component.ParentSymbolRef.DocumentationCommentId == "T:Person");
        Assert.Contains(set.Components, component =>
            component.ComponentKind == ComponentKind.SynthesizedRecordPositionalProperty
            && component.Origin == ClassificationOrigin.CompilerSynthesized);
        Assert.All(
            set.Components.Where(component =>
                component.ComponentKind is ComponentKind.AccessorGet
                    or ComponentKind.AccessorSet
                    or ComponentKind.AccessorInit
                    or ComponentKind.AccessorAdd
                    or ComponentKind.AccessorRemove
                    or ComponentKind.BackingField),
            component =>
            {
                Assert.Equal(SupportStatus.NotApplicable, component.SupportStatus);
                Assert.Equal(
                    SkipReason.NotApplicableNonDocumentationComponent,
                    component.SkipReason);
                Assert.Equal(ClassificationOrigin.Source, component.Origin);
            });
    }

    [Fact]
    public void PositionalRecordAndIteratorTraitsFollowTheDeclaringSyntaxOnly()
    {
        const string source = """
            using System.Collections.Generic;

            public record ExplicitPropertyRecord(int Value)
            {
                public int Value { get; init; } = Value;
            }

            public record SynthesizedPropertyRecord(int Value);

            public class IteratorSurface
            {
                public IEnumerable<int> Direct()
                {
                    yield return 1;
                }

                public IEnumerable<int> NestedOnly()
                {
                    return Local();

                    static IEnumerable<int> Local()
                    {
                        yield return 1;
                    }
                }

                public static IEnumerable<int> operator +(
                    IteratorSurface left,
                    IteratorSurface right)
                {
                    yield return 1;
                }

                public static IEnumerable<int> operator -(
                    IteratorSurface left,
                    IteratorSurface right)
                {
                    return Local();

                    static IEnumerable<int> Local()
                    {
                        yield return 1;
                    }
                }
            }
            """;
        var compilation = Compile("SyntaxOwnership", [Source("SyntaxOwnership.cs", source)]);
        using var session = CreateSession(new ProjectFixture(
            "SyntaxOwnership.csproj",
            "ctx-syntax-ownership",
            LoadedProjectRole.AuditRoot,
            compilation));

        var set = AssertSuccess(new SymbolClassifier().Classify(
            session,
            TargetProfile.ExternalApi));

        Assert.DoesNotContain(set.Components, component =>
            component.ParentSymbolRef.DocumentationCommentId
                == "T:ExplicitPropertyRecord"
            && component.ComponentKind
                == ComponentKind.SynthesizedRecordPositionalProperty);
        Assert.Contains(set.Targets, target =>
            target.SymbolRef.DocumentationCommentId
                == "P:ExplicitPropertyRecord.Value");
        Assert.Contains(set.Components, component =>
            component.ParentSymbolRef.DocumentationCommentId
                == "T:SynthesizedPropertyRecord"
            && component.ComponentKind
                == ComponentKind.SynthesizedRecordPositionalProperty);

        var direct = Assert.Single(set.Targets, target =>
            target.SymbolRef.DocumentationCommentId
                == "M:IteratorSurface.Direct");
        var nestedOnly = Assert.Single(set.Targets, target =>
            target.SymbolRef.DocumentationCommentId
                == "M:IteratorSurface.NestedOnly");
        var directOperator = Assert.Single(set.Targets, target =>
            target.SymbolRef.DocumentationCommentId.Contains(
                "IteratorSurface.op_Addition",
                StringComparison.Ordinal));
        var nestedOnlyOperator = Assert.Single(set.Targets, target =>
            target.SymbolRef.DocumentationCommentId.Contains(
                "IteratorSurface.op_Subtraction",
                StringComparison.Ordinal));
        Assert.Contains(SymbolTrait.Iterator, direct.Traits);
        Assert.DoesNotContain(SymbolTrait.Iterator, nestedOnly.Traits);
        Assert.Contains(SymbolTrait.Iterator, directOperator.Traits);
        Assert.DoesNotContain(SymbolTrait.Iterator, nestedOnlyOperator.Traits);
    }

    [Fact]
    public void PartialMembersAreOneLogicalTargetWithAggregatedProvenance()
    {
        const string definitions = """
            using System;
            using System.Threading.Tasks;

            public partial class PartialSurface
            {
                public partial PartialSurface(int value);
                public partial void Method();
                public partial Task<int> Async();
                public partial int Value { get; set; }
                public partial int this[int index] { get; set; }
                public partial event Action? Changed;
            }

            internal partial class InternalPartialSurface
            {
                internal partial InternalPartialSurface(int value);
            }
            """;
        const string implementations = """
            using System;
            using System.Threading.Tasks;

            public partial class PartialSurface
            {
                public partial PartialSurface(int value) { }
                public partial void Method() { }
                public async partial Task<int> Async()
                {
                    await Task.Yield();
                    return 1;
                }
                public partial int Value
                {
                    get => field;
                    set => field = value;
                }
                public partial int this[int index] { get => index; set { } }
                public partial event Action? Changed
                {
                    add { }
                    remove { }
                }
            }

            internal partial class InternalPartialSurface
            {
                internal partial InternalPartialSurface(int value) { }
            }
            """;
        var repository = Compile(
            "PartialRepository",
            [
                Source("Definitions.cs", definitions),
                Source("Implementations.cs", implementations),
            ]);
        var mixed = Compile(
            "PartialMixed",
            [
                Source("Definitions.cs", definitions),
                Source(
                    "Generated/Implementations.g.cs",
                    implementations,
                    LoadedSourceKind.SourceGenerator,
                    new GeneratedSourceFact(
                        "PartialMixed.csproj",
                        "ctx-partial-mixed",
                        Opaque("sgp.", '8'),
                        Opaque("sgo.", '9'),
                        new string('a', 64),
                        implementations)),
            ]);

        using var repositorySession = CreateSession(new ProjectFixture(
            "PartialRepository.csproj",
            "ctx-partial-repository",
            LoadedProjectRole.AuditRoot,
            repository));
        using var mixedSession = CreateSession(new ProjectFixture(
            "PartialMixed.csproj",
            "ctx-partial-mixed",
            LoadedProjectRole.AuditRoot,
            mixed));

        var repositorySet = AssertSuccess(new SymbolClassifier().Classify(
            repositorySession,
            TargetProfile.ExternalApi));
        var mixedSet = AssertSuccess(new SymbolClassifier().Classify(
            mixedSession,
            TargetProfile.ExternalApi));
        var repositoryAssemblySet = AssertSuccess(
            new SymbolClassifier().Classify(
                repositorySession,
                TargetProfile.AssemblyVisible));
        var memberIds = new[]
        {
            "M:PartialSurface.#ctor(System.Int32)",
            "M:PartialSurface.Method",
            "M:PartialSurface.Async",
            "P:PartialSurface.Value",
            "P:PartialSurface.Item(System.Int32)",
            "E:PartialSurface.Changed",
        };

        foreach (var documentationId in memberIds)
        {
            var repositoryTarget = Assert.Single(repositorySet.Targets, target =>
                target.SymbolRef.DocumentationCommentId == documentationId);
            Assert.Equal(ClassificationOrigin.Source, repositoryTarget.Origin);
            Assert.Equal(SupportStatus.Supported, repositoryTarget.SupportStatus);
            Assert.Contains(SymbolTrait.Partial, repositoryTarget.Traits);

            var mixedTarget = Assert.Single(mixedSet.Targets, target =>
                target.SymbolRef.DocumentationCommentId == documentationId);
            Assert.Equal(ClassificationOrigin.Mixed, mixedTarget.Origin);
            Assert.Equal(SupportStatus.Ambiguous, mixedTarget.SupportStatus);
            Assert.Equal(
                SkipReason.AmbiguousMixedOrigin,
                mixedTarget.SkipReason);
            Assert.Contains(SymbolTrait.Partial, mixedTarget.Traits);
            Assert.DoesNotContain(mixedSet.Components, component =>
                component.ParentSymbolRef == mixedTarget.SymbolRef);
        }

        Assert.Contains(
            SymbolTrait.Async,
            Assert.Single(repositorySet.Targets, target =>
                target.SymbolRef.DocumentationCommentId
                    == "M:PartialSurface.Async").Traits);
        Assert.Contains(
            SymbolTrait.Async,
            Assert.Single(mixedSet.Targets, target =>
                target.SymbolRef.DocumentationCommentId
                    == "M:PartialSurface.Async").Traits);
        Assert.Contains(repositorySet.Components, component =>
            component.ParentSymbolRef.DocumentationCommentId
                == "P:PartialSurface.Value"
            && component.ComponentKind == ComponentKind.BackingField);
        Assert.DoesNotContain(repositorySet.Targets, target =>
            target.SymbolRef.DocumentationCommentId.Contains(
                "InternalPartialSurface",
                StringComparison.Ordinal));
        Assert.Contains(
            SymbolTrait.Partial,
            Assert.Single(repositoryAssemblySet.Targets, target =>
                target.SymbolRef.DocumentationCommentId
                    == "M:InternalPartialSurface.#ctor(System.Int32)").Traits);
    }

    [Fact]
    public void ComponentDiscoveryIndexesScaleLinearlyAndObserveCancellation()
    {
        static (int Operations, ClassificationOutcome Outcome) Classify(
            int count,
            int? cancelAt = null)
        {
            var properties = string.Join(
                Environment.NewLine,
                Enumerable.Range(0, count)
                    .Select(index =>
                        $"public int P{index} {{ get; set; }}"));
            var parameters = string.Join(
                ", ",
                Enumerable.Range(0, count)
                    .Select(index => $"int P{index}"));
            var source = $$"""
                public class Dto
                {
                    {{properties}}
                }

                public record Row({{parameters}});
                """;
            var compilation = Compile(
                $"ComponentScale{count}",
                [Source($"ComponentScale{count}.cs", source)]);
            using var session = CreateSession(new ProjectFixture(
                $"ComponentScale{count}.csproj",
                $"ctx-component-scale-{count}",
                LoadedProjectRole.AuditRoot,
                compilation));
            using var cancellation = new CancellationTokenSource();
            var operations = 0;
            var classifier = new SymbolClassifier(
                null,
                null,
                null,
                null,
                () =>
                {
                    operations++;
                    if (operations == cancelAt)
                    {
                        cancellation.Cancel();
                    }
                });
            var outcome = classifier.Classify(
                session,
                TargetProfile.ExternalApi,
                cancellation.Token);
            return (operations, outcome);
        }

        var small = Classify(32);
        var large = Classify(64);
        Assert.Equal(ClassificationRunStatus.Success, small.Outcome.Status);
        Assert.Equal(ClassificationRunStatus.Success, large.Outcome.Status);
        Assert.True(
            large.Operations <= small.Operations * 2 + 16,
            $"component discovery grew from {small.Operations} to {large.Operations}");

        var cancelled = Classify(64, 10);
        Assert.Equal(
            ClassificationRunStatus.Cancelled,
            cancelled.Outcome.Status);
        Assert.Null(cancelled.Outcome.ClassificationSet);

        var cachedProperties = string.Join(
            ", ",
            Enumerable.Range(0, 64)
                .Select(index => $"int P{index}"));
        var cachedCompilation = Compile(
            "CachedComponentCancellation",
            [Source(
                "CachedComponentCancellation.cs",
                $"public record CachedRow({cachedProperties});")]);
        using var cachedSession = CreateSession(new ProjectFixture(
            "CachedComponentCancellation.csproj",
            "ctx-cached-component-cancellation",
            LoadedProjectRole.AuditRoot,
            cachedCompilation));
        using var cachedCancellation = new CancellationTokenSource();
        var componentPhase = false;
        var cachedComponentOperations = 0;
        var cachedClassifier = new SymbolClassifier(
            null,
            null,
            stage =>
            {
                if (stage == ClassificationStage.TargetDiscovery)
                {
                    componentPhase = true;
                }
            },
            null,
            () =>
            {
                if (!componentPhase)
                {
                    return;
                }

                cachedComponentOperations++;
                cachedCancellation.Cancel();
            });

        var cachedCancelled = cachedClassifier.Classify(
            cachedSession,
            TargetProfile.ExternalApi,
            cachedCancellation.Token);

        Assert.Equal(
            ClassificationRunStatus.Cancelled,
            cachedCancelled.Status);
        Assert.Null(cachedCancelled.ClassificationSet);
        Assert.Null(cachedCancelled.PrimaryFailure);
        Assert.Equal(1, cachedComponentOperations);
    }

    [Fact]
    public void RelationsUseContextBoundOriginalDefinitionsWithoutPromotingSources()
    {
        const string source = """
            using System;

            public interface IBase
            {
                void Method();
                int Property { get; }
                int this[int index] { get; }
                event Action Event;
            }

            public interface IDerived : IBase { }
            public interface ILeft : IBase { }
            public interface IRight : IBase { }
            public interface IDiamond : ILeft, IRight { }

            public class Base
            {
                public virtual void Method() { }
                public virtual int Property => 0;
                public virtual int this[int index] => index;
                public virtual event Action? Event;
            }

            public class Implementation : Base, IBase
            {
                public override void Method() { }
                public override int Property => 1;
                public override int this[int index] => index;
                public override event Action? Event;
            }

            public class Explicit : IBase
            {
                void IBase.Method() { }
                int IBase.Property => 1;
                int IBase.this[int index] => index;
                event Action IBase.Event { add { } remove { } }
            }
            """;
        var compilation = Compile("Relations", [Source("Relations.cs", source)]);
        using var session = CreateSession(new ProjectFixture(
            "Relations.csproj",
            "ctx-relations",
            LoadedProjectRole.AuditRoot,
            compilation));

        var set = AssertSuccess(new SymbolClassifier().Classify(
            session,
            TargetProfile.ExternalApi));

        Assert.Contains(set.Relations, relation =>
            relation.RelationKind == RelationKind.Overrides
            && relation.SourceSymbolRef.DocumentationCommentId
                == "M:Implementation.Method"
            && relation.TargetSymbolRef.DocumentationCommentId == "M:Base.Method");
        Assert.Contains(set.Relations, relation =>
            relation.RelationKind == RelationKind.ImplicitInterfaceImplementation
            && relation.SourceSymbolRef.DocumentationCommentId
                == "M:Implementation.Method"
            && relation.TargetSymbolRef.DocumentationCommentId == "M:IBase.Method");
        Assert.Contains(set.Relations, relation =>
            relation.RelationKind == RelationKind.ExplicitInterfaceImplementation
            && relation.SourceSymbolRef.DocumentationCommentId
                == "M:Explicit.IBase#Method"
            && relation.TargetSymbolRef.DocumentationCommentId == "M:IBase.Method");
        Assert.Contains(set.Relations, relation =>
            relation.RelationKind == RelationKind.InheritedInterfaceMember
            && relation.SourceSymbolRef.DocumentationCommentId == "T:IDerived"
            && relation.TargetSymbolRef.DocumentationCommentId == "M:IBase.Method");
        Assert.Single(set.Relations, relation =>
            relation.RelationKind == RelationKind.InheritedInterfaceMember
            && relation.SourceSymbolRef.DocumentationCommentId == "T:IDiamond"
            && relation.TargetSymbolRef.DocumentationCommentId == "M:IBase.Method");
        Assert.All(set.Relations, relation =>
        {
            Assert.Equal("ctx-relations", relation.SourceSymbolRef.CompilationContextRef);
            Assert.Equal("ctx-relations", relation.TargetSymbolRef.CompilationContextRef);
        });
        Assert.DoesNotContain(set.Targets, target =>
            target.SymbolRef.DocumentationCommentId.Contains(
                "Explicit.IBase",
                StringComparison.Ordinal));
        Assert.DoesNotContain(set.Components, component =>
            component.ParentSymbolRef.DocumentationCommentId.Contains(
                "Explicit.IBase",
                StringComparison.Ordinal));
        Assert.Equal(set.Relations.Length, set.Relations.Distinct().Count());
    }

    [Fact]
    public void StaticInterfaceOperatorsAndConversionsStayInRelationDomain()
    {
        const string source = """
            public interface IValue<TSelf> where TSelf : IValue<TSelf>
            {
                static abstract TSelf operator +(TSelf left, TSelf right);
                static abstract implicit operator int(TSelf value);
            }

            public interface IDerivedValue : IValue<Value> { }

            public readonly struct Value : IValue<Value>
            {
                public static Value operator +(Value left, Value right) => left;
                public static implicit operator int(Value value) => 0;
            }

            public readonly struct ExplicitValue : IValue<ExplicitValue>
            {
                static ExplicitValue IValue<ExplicitValue>.operator +(
                    ExplicitValue left,
                    ExplicitValue right) => left;

                static implicit IValue<ExplicitValue>.operator int(
                    ExplicitValue value) => 0;
            }

            internal interface IInternalValue<TSelf>
                where TSelf : IInternalValue<TSelf>
            {
                static abstract TSelf operator +(TSelf left, TSelf right);
                static abstract implicit operator int(TSelf value);
            }

            internal readonly struct InternalExplicitValue
                : IInternalValue<InternalExplicitValue>
            {
                static InternalExplicitValue
                    IInternalValue<InternalExplicitValue>.operator +(
                        InternalExplicitValue left,
                        InternalExplicitValue right) => left;

                static implicit
                    IInternalValue<InternalExplicitValue>.operator int(
                        InternalExplicitValue value) => 0;
            }
            """;
        var compilation = Compile("StaticRelations", [Source("StaticRelations.cs", source)]);
        using var session = CreateSession(new ProjectFixture(
            "StaticRelations.csproj",
            "ctx-static-relations",
            LoadedProjectRole.AuditRoot,
            compilation));

        var set = AssertSuccess(new SymbolClassifier().Classify(
            session,
            TargetProfile.ExternalApi));
        var assemblySet = AssertSuccess(new SymbolClassifier().Classify(
            session,
            TargetProfile.AssemblyVisible));

        Assert.Contains(set.Relations, relation =>
            relation.RelationKind == RelationKind.ImplicitInterfaceImplementation
            && relation.SourceSymbolRef.DocumentationCommentId.Contains(
                "Value.op_Addition",
                StringComparison.Ordinal)
            && relation.TargetSymbolRef.DocumentationCommentId.Contains(
                "IValue`1.op_Addition",
                StringComparison.Ordinal));
        Assert.Contains(set.Relations, relation =>
            relation.RelationKind == RelationKind.ImplicitInterfaceImplementation
            && relation.SourceSymbolRef.DocumentationCommentId.Contains(
                "Value.op_Implicit",
                StringComparison.Ordinal)
            && relation.TargetSymbolRef.DocumentationCommentId.Contains(
                "IValue`1.op_Implicit",
                StringComparison.Ordinal));
        Assert.Contains(set.Relations, relation =>
            relation.RelationKind == RelationKind.InheritedInterfaceMember
            && relation.SourceSymbolRef.DocumentationCommentId == "T:IDerivedValue"
            && relation.TargetSymbolRef.DocumentationCommentId.Contains(
                "op_Addition",
                StringComparison.Ordinal));
        Assert.Contains(set.Relations, relation =>
            relation.RelationKind == RelationKind.InheritedInterfaceMember
            && relation.SourceSymbolRef.DocumentationCommentId == "T:IDerivedValue"
            && relation.TargetSymbolRef.DocumentationCommentId.Contains(
                "op_Implicit",
                StringComparison.Ordinal));
        var explicitOperator = Assert.Single(set.Relations, relation =>
            relation.RelationKind
                == RelationKind.ExplicitInterfaceImplementation
            && relation.SourceSymbolRef.DocumentationCommentId.Contains(
                "ExplicitValue",
                StringComparison.Ordinal)
            && relation.SourceSymbolRef.DocumentationCommentId.Contains(
                "op_Addition",
                StringComparison.Ordinal));
        var explicitConversion = Assert.Single(set.Relations, relation =>
            relation.RelationKind
                == RelationKind.ExplicitInterfaceImplementation
            && relation.SourceSymbolRef.DocumentationCommentId.Contains(
                "ExplicitValue",
                StringComparison.Ordinal)
            && relation.SourceSymbolRef.DocumentationCommentId.Contains(
                "op_Implicit",
                StringComparison.Ordinal));
        Assert.DoesNotContain(set.Targets, target =>
            target.SymbolRef == explicitOperator.SourceSymbolRef
            || target.SymbolRef == explicitConversion.SourceSymbolRef);
        Assert.DoesNotContain(set.Components, component =>
            component.ParentSymbolRef == explicitOperator.SourceSymbolRef
            || component.ParentSymbolRef == explicitConversion.SourceSymbolRef);
        AssertOracleConforms(
            ClassificationConformanceOracle.Load(FindRepositoryRoot()),
            TargetProfile.ExternalApi,
            set,
            RelationEndpointKinds(set));
        Assert.DoesNotContain(set.Relations, relation =>
            relation.SourceSymbolRef.DocumentationCommentId.Contains(
                "InternalExplicitValue",
                StringComparison.Ordinal));
        Assert.Single(assemblySet.Relations, relation =>
            relation.RelationKind
                == RelationKind.ExplicitInterfaceImplementation
            && relation.SourceSymbolRef.DocumentationCommentId.Contains(
                "InternalExplicitValue",
                StringComparison.Ordinal)
            && relation.SourceSymbolRef.DocumentationCommentId.Contains(
                "op_Addition",
                StringComparison.Ordinal));
        Assert.Single(assemblySet.Relations, relation =>
            relation.RelationKind
                == RelationKind.ExplicitInterfaceImplementation
            && relation.SourceSymbolRef.DocumentationCommentId.Contains(
                "InternalExplicitValue",
                StringComparison.Ordinal)
            && relation.SourceSymbolRef.DocumentationCommentId.Contains(
                "op_Implicit",
                StringComparison.Ordinal));
        AssertOracleConforms(
            ClassificationConformanceOracle.Load(FindRepositoryRoot()),
            TargetProfile.AssemblyVisible,
            assemblySet,
            RelationEndpointKinds(assemblySet));
    }

    [Fact]
    public void AuthoritativeTreeBindingsDriveOriginsAndTypedUnresolvedLocators()
    {
        var sourceFact = new GeneratedSourceFact(
            "Generated.csproj",
            "ctx-generated",
            Opaque("sgp.", '1'),
            Opaque("sgo.", '2'),
            new string('3', 64),
            "public class GeneratedOnly { }");
        var toolFact = new GeneratedSourceFact(
            "Generated.csproj",
            "ctx-generated",
            Opaque("tgp.", '4'),
            Opaque("tgo.", '5'),
            new string('6', 64),
            "public class ToolOnly { }");
        var compilation = Compile(
            "Generated",
            [
                Source("Repository.cs", "public partial class Mixed { }"),
                Source(
                    "generator://opaque",
                    "public class GeneratedOnly { } public partial class Mixed { }",
                    LoadedSourceKind.SourceGenerator,
                    sourceFact),
                Source(
                    "tool-generated://opaque",
                    "public class ToolOnly { }",
                    LoadedSourceKind.ToolGenerated,
                    toolFact),
            ]);
        using var session = CreateSession(new ProjectFixture(
            "Generated.csproj",
            "ctx-generated",
            LoadedProjectRole.AuditRoot,
            compilation));

        var normal = AssertSuccess(new SymbolClassifier().Classify(
            session,
            TargetProfile.ExternalApi));
        Assert.Contains(normal.Targets, target =>
            target.SymbolRef.DocumentationCommentId == "T:GeneratedOnly"
            && target.Origin == ClassificationOrigin.SourceGenerator);
        Assert.Contains(normal.Targets, target =>
            target.SymbolRef.DocumentationCommentId == "T:ToolOnly"
            && target.Origin == ClassificationOrigin.ToolGenerated);
        Assert.Contains(normal.Targets, target =>
            target.SymbolRef.DocumentationCommentId == "T:Mixed"
            && target.Origin == ClassificationOrigin.Mixed
            && target.SupportStatus == SupportStatus.Ambiguous
            && target.SkipReason == SkipReason.AmbiguousMixedOrigin);
        Assert.DoesNotContain(normal.Components, component =>
            component.ParentSymbolRef.DocumentationCommentId == "T:Mixed");

        var unresolvedClassifier = new SymbolClassifier(
            symbol => symbol.Name is "GeneratedOnly" or "ToolOnly"
                ? null
                : symbol.GetDocumentationCommentId(),
            null,
            null,
            null);
        var unresolved = AssertSuccess(unresolvedClassifier.Classify(
            session,
            TargetProfile.ExternalApi));
        Assert.Contains(unresolved.Unresolved, record =>
            record.CandidateLocator is GeneratedSourceCandidateLocator locator
            && locator.GeneratorId == sourceFact.ProducerId
            && locator.HintNameId == sourceFact.OutputId);
        Assert.Contains(unresolved.Unresolved, record =>
            record.CandidateLocator is ToolGeneratedCandidateLocator locator
            && locator.ProducerId == toolFact.ProducerId
            && locator.OutputId == toolFact.OutputId);
        Assert.DoesNotContain(unresolved.Targets, target =>
            target.SymbolRef.DocumentationCommentId is "T:GeneratedOnly" or "T:ToolOnly");
    }

    [Fact]
    public void EndpointFailureOmitsOnlyRelationAndProducesBoundedDiagnostics()
    {
        const string source = """
            using System;

            public interface IContract
            {
                void Method();
                int Value { get; }
                string this[int index] { get; }
                event Action Changed;
            }
            public interface IDerivedContract : IContract { }
            public class Base { public virtual void Method() { } }
            public class Derived : Base { public override void Method() { } }
            public class Implicit : IContract
            {
                public void Method() { }
                public int Value => 0;
                public string this[int index] => "";
                public event Action? Changed;
            }
            public class Explicit : IContract
            {
                void IContract.Method() { }
                int IContract.Value => 0;
                string IContract.this[int index] => "";
                event Action IContract.Changed
                {
                    add { }
                    remove { }
                }
            }
            """;
        var compilation = Compile("Endpoint", [Source("Endpoint.cs", source)]);
        using var session = CreateSession(new ProjectFixture(
            "Endpoint.csproj",
            "ctx-endpoint",
            LoadedProjectRole.AuditRoot,
            compilation));
        var baseline = AssertSuccess(new SymbolClassifier().Classify(
            session,
            TargetProfile.ExternalApi));
        Assert.All(Enum.GetValues<RelationKind>(), kind =>
            Assert.Contains(baseline.Relations, relation =>
                relation.RelationKind == kind));
        Assert.Contains(baseline.Relations, relation =>
            relation.RelationKind
                == RelationKind.ExplicitInterfaceImplementation
            && relation.SourceSymbolRef.DocumentationCommentId
                .StartsWith("P:", StringComparison.Ordinal)
            && !relation.SourceSymbolRef.DocumentationCommentId.Contains(
                "Item",
                StringComparison.Ordinal));
        Assert.Contains(baseline.Relations, relation =>
            relation.RelationKind
                == RelationKind.ExplicitInterfaceImplementation
            && relation.SourceSymbolRef.DocumentationCommentId.Contains(
                "Item",
                StringComparison.Ordinal));
        Assert.Contains(baseline.Relations, relation =>
            relation.RelationKind
                == RelationKind.ExplicitInterfaceImplementation
            && relation.SourceSymbolRef.DocumentationCommentId
                .StartsWith("E:", StringComparison.Ordinal));
        AssertOracleConforms(
            ClassificationConformanceOracle.Load(FindRepositoryRoot()),
            TargetProfile.ExternalApi,
            baseline,
            RelationEndpointKinds(baseline));

        foreach (var blockedKind in Enum.GetValues<RelationKind>())
        {
            foreach (var blockedStatus in new[]
                     {
                         RelationEndpointStatus.Ambiguous,
                         RelationEndpointStatus.Unavailable,
                     })
            {
                var classifier = new SymbolClassifier(
                    null,
                    (kind, symbol, isTarget, context) =>
                        kind == blockedKind && isTarget
                            ? new RelationEndpointResolution(
                                blockedStatus,
                                null,
                                null)
                            : new RelationEndpointResolution(
                                RelationEndpointStatus.Available,
                                context,
                                symbol.GetDocumentationCommentId()!),
                    null,
                    null);

                var outcome = classifier.Classify(session, TargetProfile.ExternalApi);
                var set = AssertSuccess(outcome);

                Assert.Equal(
                    baseline.Targets.Select(TargetKey),
                    set.Targets.Select(TargetKey));
                Assert.Equal(
                    baseline.Components.Select(ComponentKey),
                    set.Components.Select(ComponentKey));
                Assert.Equal(
                    baseline.Unresolved.Select(UnresolvedKey),
                    set.Unresolved.Select(UnresolvedKey));
                Assert.Equal(
                    baseline.Relations
                        .Where(relation => relation.RelationKind != blockedKind),
                    set.Relations);
                var diagnostic = Assert.Single(outcome.Diagnostics);
                Assert.Equal("relation", diagnostic.Stage);
                Assert.Equal(
                    blockedStatus == RelationEndpointStatus.Ambiguous
                        ? "classification.relation-endpoint-ambiguous"
                        : "classification.relation-endpoint-unavailable",
                    diagnostic.Code);
                Assert.Equal("warning", diagnostic.Severity);
            }
        }
    }

    [Fact]
    public void NormalizationIsTransactionalAndOrdersTypedUnresolvedCandidates()
    {
        var target = new TargetClassificationCandidate(
            "ctx-normalize",
            "T:Example",
            PrimarySymbolKind.Class,
            [],
            ClassificationOrigin.Source,
            []);
        var missing = new TargetClassificationCandidate(
            "ctx-normalize",
            null,
            PrimarySymbolKind.Class,
            [],
            ClassificationOrigin.Source,
            [
                new ToolGeneratedCandidateLocator(
                    Opaque("tgp.", '2'),
                    Opaque("tgo.", '3'),
                    new Utf16Span(2, 2)),
                new RepositoryCandidateLocator(
                    "src/Example.cs",
                    new Utf16Span(0, 7)),
                new GeneratedSourceCandidateLocator(
                    Opaque("sgp.", '4'),
                    Opaque("sgo.", '5')),
            ]);
        var success = ClassificationNormalization.Normalize(
            TargetProfile.ExternalApi,
            new ClassificationCandidateBatch([missing, target], [], [], []),
            CancellationToken.None);

        Assert.Collection(
            success.Unresolved,
            item => Assert.IsType<RepositoryCandidateLocator>(item.CandidateLocator),
            item => Assert.IsType<GeneratedSourceCandidateLocator>(item.CandidateLocator),
            item => Assert.IsType<ToolGeneratedCandidateLocator>(item.CandidateLocator));

        var conflicting = target with { Origin = ClassificationOrigin.ToolGenerated };
        Assert.Throws<ClassificationUnrepresentableException>(() =>
            ClassificationNormalization.Normalize(
                TargetProfile.ExternalApi,
                new ClassificationCandidateBatch(
                    [target, conflicting],
                    [],
                    [],
                    []),
                CancellationToken.None));
    }

    [Fact]
    public void PublicCoreInputBoundaryValidatesLocatorsAndReturnsOnlyTerminalOutcomes()
    {
        Assert.Throws<ArgumentException>(() =>
            ClassificationInput.RepositoryLocator("../outside.cs"));
        Assert.Throws<ArgumentException>(() =>
            ClassificationInput.RepositoryLocator("src/A.cs", 2, 1));

        var invalid = new ClassificationCandidateBuffer();
        invalid.AddTarget(
            string.Empty,
            "T:Invalid",
            PrimarySymbolKind.Class,
            [],
            ClassificationOrigin.Source,
            []);
        var failure = invalid.Normalize(TargetProfile.ExternalApi);
        Assert.Equal(ClassificationRunStatus.Failure, failure.Status);
        Assert.Null(failure.ClassificationSet);
        Assert.Equal(
            ClassificationVocabulary.UnrepresentableRunFailure,
            failure.PrimaryFailure?.Code);

        var valid = new ClassificationCandidateBuffer();
        valid.AddTarget(
            "ctx-public-boundary",
            "T:Valid",
            PrimarySymbolKind.Class,
            [],
            ClassificationOrigin.Source,
            []);
        var success = valid.Normalize(TargetProfile.ExternalApi);
        var set = AssertSuccess(success);
        Assert.Equal(
            "T:Valid",
            Assert.Single(set.Targets)
                .SymbolRef.DocumentationCommentId);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var emptyCancelled = new ClassificationCandidateBuffer().Normalize(
            TargetProfile.ExternalApi,
            cancellationToken: cancellation.Token);
        Assert.Equal(
            ClassificationRunStatus.Cancelled,
            emptyCancelled.Status);
        Assert.Null(emptyCancelled.ClassificationSet);
        Assert.Null(emptyCancelled.PrimaryFailure);

        var bufferedCancelled = valid.Normalize(
            TargetProfile.ExternalApi,
            cancellationToken: cancellation.Token);
        Assert.Equal(
            ClassificationRunStatus.Cancelled,
            bufferedCancelled.Status);
        Assert.Null(bufferedCancelled.ClassificationSet);
        Assert.Null(bufferedCancelled.PrimaryFailure);
    }

    [Fact]
    public void ClassifierRejectsUndefinedProfile()
    {
        var compilation = Compile(
            "Outcomes",
            [Source("Outcomes.cs", "public class Example { public void Method() { } }")]);
        using var session = CreateSession(new ProjectFixture(
            "Outcomes.csproj",
            "ctx-outcomes",
            LoadedProjectRole.AuditRoot,
            compilation));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SymbolClassifier().Classify(session, (TargetProfile)999));

    }

    [Theory]
    [InlineData((int)ClassificationStage.TargetDiscovery)]
    [InlineData((int)ClassificationStage.ComponentDiscovery)]
    [InlineData((int)ClassificationStage.RelationDiscovery)]
    [InlineData((int)ClassificationStage.UnresolvedDiscovery)]
    [InlineData((int)ClassificationStage.CandidateBufferingComplete)]
    [InlineData((int)ClassificationStage.TerminalValidation)]
    public void CancellationAtEveryBufferedAndTerminalStageIsTransactional(
        int cancellationStageValue)
    {
        var cancellationStage = (ClassificationStage)cancellationStageValue;
        var compilation = Compile(
            "CancellationStages",
            [Source(
                "CancellationStages.cs",
                "public class Example { public void Method() { } }")]);
        using var session = CreateSession(new ProjectFixture(
            "CancellationStages.csproj",
            "ctx-cancellation-stages",
            LoadedProjectRole.AuditRoot,
            compilation));
        using var cancellation = new CancellationTokenSource();
        var classifier = new SymbolClassifier(
            null,
            null,
            stage =>
            {
                if (stage == cancellationStage)
                {
                    cancellation.Cancel();
                }
            },
            null);

        var outcome = classifier.Classify(
            session,
            TargetProfile.ExternalApi,
            cancellation.Token);

        Assert.Equal(ClassificationRunStatus.Cancelled, outcome.Status);
        Assert.Null(outcome.ClassificationSet);
        Assert.Null(outcome.PrimaryFailure);
    }

    [Theory]
    [InlineData((int)ClassificationStage.TargetDiscovery)]
    [InlineData((int)ClassificationStage.ComponentDiscovery)]
    [InlineData((int)ClassificationStage.RelationDiscovery)]
    [InlineData((int)ClassificationStage.UnresolvedDiscovery)]
    public void InvalidCandidatesAfterEveryBufferedFamilyFailTransactionally(
        int failureStageValue)
    {
        var failureStage = (ClassificationStage)failureStageValue;
        var compilation = Compile(
            "FailureStages",
            [Source(
                "FailureStages.cs",
                "public class Example { public void Method() { } }")]);
        using var session = CreateSession(new ProjectFixture(
            "FailureStages.csproj",
            "ctx-failure-stages",
            LoadedProjectRole.AuditRoot,
            compilation));
        var repositoryLocator = new RepositoryCandidateLocator(
            "FailureStages.cs");
        var classifier = new SymbolClassifier(
            null,
            null,
            null,
            (stage, buffer) =>
            {
                if (stage != failureStage)
                {
                    return;
                }

                switch (stage)
                {
                    case ClassificationStage.TargetDiscovery:
                        var first = buffer.Targets[0];
                        buffer.Targets.Add(first with
                        {
                            Origin = ClassificationOrigin.ToolGenerated,
                        });
                        break;
                    case ClassificationStage.ComponentDiscovery:
                        buffer.Components.Add(
                            new ComponentClassificationCandidate(
                                new SymbolRef(
                                    "ctx-failure-stages",
                                    "T:Example"),
                                ComponentKind.Return,
                                "return",
                                ClassificationOrigin.Source));
                        break;
                    case ClassificationStage.RelationDiscovery:
                        buffer.Relations.Add(new RelationObservationCandidate(
                            new RelationObservation(
                                RelationKind.Overrides,
                                new SymbolRef(
                                    "ctx-failure-stages",
                                    "T:Example"),
                                new SymbolRef(
                                    "ctx-failure-stages",
                                    "M:Example.Method")),
                            PrimarySymbolKind.Class,
                            PrimarySymbolKind.Method));
                        break;
                    case ClassificationStage.UnresolvedDiscovery:
                        buffer.Unresolved.Add(
                            new UnresolvedClassificationCandidate(
                                "ctx-failure-stages",
                                ClassificationOrigin.Source,
                                [repositoryLocator]));
                        buffer.Unresolved.Add(
                            new UnresolvedClassificationCandidate(
                                "ctx-failure-stages",
                                ClassificationOrigin.ToolGenerated,
                                [repositoryLocator]));
                        break;
                    default:
                        throw new InvalidOperationException();
                }
            });

        var failed = classifier.Classify(
            session,
            TargetProfile.ExternalApi);

        Assert.Equal(ClassificationRunStatus.Failure, failed.Status);
        Assert.Null(failed.ClassificationSet);
        Assert.Equal(
            ClassificationVocabulary.UnrepresentableRunFailure,
            failed.PrimaryFailure?.Code);
    }

    [Fact]
    public void SkipPrecedenceCollisionMatrixIsOrderIndependent()
    {
        var supported = new TargetClassificationCandidate(
            "ctx-collisions",
            "T:Parent",
            PrimarySymbolKind.Class,
            [],
            ClassificationOrigin.Source,
            []);
        var repositoryLocator = new RepositoryCandidateLocator(
            "src/Parent.cs",
            new Utf16Span(0, 4));
        var cases = new[]
        {
            new CollisionCase(
                "unresolved.documentation-and-generated.documentation-selection.accept",
                supported with
                {
                    DocumentationCommentId = null,
                    CandidateLocators = [repositoryLocator],
                    GeneratedProvenanceAvailable = false,
                },
                false,
                null,
                ClassificationOrigin.Source,
                SupportStatus.UnavailableContext,
                SkipReason.UnavailableDocumentationCommentId),
            new CollisionCase(
                "unresolved.documentation-and-semantic.documentation-selection.accept",
                supported with
                {
                    DocumentationCommentId = null,
                    CandidateLocators = [repositoryLocator],
                    SemanticContextAvailable = false,
                },
                false,
                null,
                ClassificationOrigin.Source,
                SupportStatus.UnavailableContext,
                SkipReason.UnavailableDocumentationCommentId),
            new CollisionCase(
                "missing-id-beats-provenance-and-context",
                supported with
                {
                    DocumentationCommentId = null,
                    CandidateLocators = [repositoryLocator],
                    GeneratedProvenanceAvailable = false,
                    SemanticContextAvailable = false,
                },
                false,
                null,
                ClassificationOrigin.Source,
                SupportStatus.UnavailableContext,
                SkipReason.UnavailableDocumentationCommentId),
            new CollisionCase(
                "unknown-kind-beats-partial-and-mixed",
                supported with
                {
                    DocumentationCommentId = "T:Unknown",
                    PrimaryKind = PrimarySymbolKind.Unknown,
                    Origin = ClassificationOrigin.Mixed,
                    PartialAmbiguous = true,
                },
                false,
                PrimarySymbolKind.Unknown,
                ClassificationOrigin.Mixed,
                SupportStatus.Unsupported,
                SkipReason.UnsupportedSymbolKind),
            new CollisionCase(
                "partial-beats-mixed",
                supported with
                {
                    DocumentationCommentId = "T:Partial",
                    Origin = ClassificationOrigin.Mixed,
                    PartialAmbiguous = true,
                },
                false,
                PrimarySymbolKind.Class,
                ClassificationOrigin.Mixed,
                SupportStatus.Ambiguous,
                SkipReason.AmbiguousPartialDeclaration),
            new CollisionCase(
                "generated-provenance-beats-semantic-context",
                supported with
                {
                    DocumentationCommentId = "T:Unavailable",
                    GeneratedProvenanceAvailable = false,
                    SemanticContextAvailable = false,
                },
                false,
                PrimarySymbolKind.Class,
                ClassificationOrigin.Unknown,
                SupportStatus.UnavailableContext,
                SkipReason.UnavailableGeneratedProvenance),
            new CollisionCase(
                "unresolved-requires-proven-origin",
                supported with
                {
                    DocumentationCommentId = null,
                    Origin = ClassificationOrigin.Unknown,
                    CandidateLocators = [repositoryLocator],
                },
                true,
                null,
                null,
                null,
                null),
            new CollisionCase(
                "unresolved-requires-legal-locator",
                supported with
                {
                    DocumentationCommentId = null,
                    CandidateLocators = [],
                },
                true,
                null,
                null,
                null,
                null),
        };

        foreach (var item in cases)
        {
            var batch = new ClassificationCandidateBatch(
                [item.Candidate, supported],
                [],
                [],
                []);
            if (item.Fails)
            {
                Assert.Throws<ClassificationUnrepresentableException>(() =>
                    ClassificationNormalization.Normalize(
                        TargetProfile.ExternalApi,
                        batch,
                        CancellationToken.None));
                continue;
            }

            var forward = ClassificationNormalization.Normalize(
                TargetProfile.ExternalApi,
                batch,
                CancellationToken.None);
            var reverse = ClassificationNormalization.Normalize(
                TargetProfile.ExternalApi,
                batch with { Targets = batch.Targets.Reverse().ToArray() },
                CancellationToken.None);
            Assert.Equal(
                forward.Targets.Select(TargetKey),
                reverse.Targets.Select(TargetKey));
            Assert.Equal(
                forward.Unresolved.Select(UnresolvedKey),
                reverse.Unresolved.Select(UnresolvedKey));
            if (item.PrimaryKind is null)
            {
                var unresolved = Assert.Single(forward.Unresolved);
                Assert.Equal(item.Origin, unresolved.Origin);
                Assert.Equal(item.Status, unresolved.SupportStatus);
                Assert.Equal(item.Skip, unresolved.SkipReason);
            }
            else
            {
                var target = Assert.Single(forward.Targets, target =>
                    target.SymbolRef.DocumentationCommentId
                        == item.Candidate.DocumentationCommentId);
                Assert.Equal(item.PrimaryKind, target.PrimaryKind);
                Assert.Equal(item.Origin, target.Origin);
                Assert.Equal(item.Status, target.SupportStatus);
                Assert.Equal(item.Skip, target.SkipReason);
            }
        }
    }

    [Fact]
    public void ComponentUnavailableSelectionUsesGeneratedProvenanceBeforeSemanticContext()
    {
        var componentParent = new SymbolRef(
            "ctx-collisions",
            "M:Parent.Run(System.String)");
        var componentBatch = new ClassificationCandidateBatch(
            [
                new TargetClassificationCandidate(
                    componentParent.CompilationContextRef,
                    componentParent.DocumentationCommentId,
                    PrimarySymbolKind.Method,
                    [],
                    ClassificationOrigin.Source,
                    []),
            ],
            [
                new ComponentClassificationCandidate(
                    componentParent,
                    ComponentKind.Parameter,
                    "parameter/0",
                    ClassificationOrigin.Source,
                    GeneratedProvenanceAvailable: false,
                    SemanticContextAvailable: false),
            ],
            [],
            []);

        var componentResult = ClassificationNormalization.Normalize(
            TargetProfile.ExternalApi,
            componentBatch,
            CancellationToken.None);
        var component = Assert.Single(componentResult.Components);
        Assert.Equal(ClassificationOrigin.Unknown, component.Origin);
        Assert.Equal(
            SupportStatus.UnavailableContext,
            component.SupportStatus);
        Assert.Equal(
            SkipReason.UnavailableGeneratedProvenance,
            component.SkipReason);
    }

    [Fact]
    public void ComponentNormalizationValidatesDomainsDeduplicatesUnknownsAndSuppressesUnsupportedParents()
    {
        var parent = new SymbolRef("ctx-components", "T:Parent");
        var supportedTarget = new TargetClassificationCandidate(
            parent.CompilationContextRef,
            parent.DocumentationCommentId,
            PrimarySymbolKind.Property,
            [],
            ClassificationOrigin.Source,
            []);
        var unknown = new ComponentClassificationCandidate(
            parent,
            ComponentKind.Unknown,
            null,
            ClassificationOrigin.Mixed,
            new RepositoryCandidateLocator("src/Parent.cs"));
        var mixedAccessor = new ComponentClassificationCandidate(
            parent,
            ComponentKind.AccessorGet,
            "accessor/get",
            ClassificationOrigin.Mixed);
        var normalized = ClassificationNormalization.Normalize(
            TargetProfile.ExternalApi,
            new ClassificationCandidateBatch(
                [supportedTarget],
                [mixedAccessor, unknown, unknown],
                [],
                []),
            CancellationToken.None);

        var unknownResult = Assert.Single(normalized.Components, component =>
            component.ComponentKind == ComponentKind.Unknown);
        Assert.Equal("unknown/0", unknownResult.Identity);
        Assert.Equal(SupportStatus.Unsupported, unknownResult.SupportStatus);
        Assert.Equal(
            SkipReason.UnsupportedComponentKind,
            unknownResult.SkipReason);
        Assert.Equal(ClassificationOrigin.Mixed, unknownResult.Origin);
        var accessor = Assert.Single(normalized.Components, component =>
            component.ComponentKind == ComponentKind.AccessorGet);
        Assert.Equal(SupportStatus.Ambiguous, accessor.SupportStatus);
        Assert.Equal(SkipReason.AmbiguousMixedOrigin, accessor.SkipReason);

        var conflictingUnknown = unknown with
        {
            Origin = ClassificationOrigin.Source,
        };
        Assert.Throws<ClassificationUnrepresentableException>(() =>
            ClassificationNormalization.Normalize(
                TargetProfile.ExternalApi,
                new ClassificationCandidateBatch(
                    [supportedTarget],
                    [unknown, conflictingUnknown],
                    [],
                    []),
                CancellationToken.None));
        Assert.Throws<ClassificationUnrepresentableException>(() =>
            ClassificationNormalization.Normalize(
                TargetProfile.ExternalApi,
                new ClassificationCandidateBatch(
                    [supportedTarget],
                    [
                        new ComponentClassificationCandidate(
                            parent,
                            ComponentKind.Return,
                            "return",
                            ClassificationOrigin.Source),
                    ],
                    [],
                    []),
                CancellationToken.None));
        Assert.Throws<ClassificationUnrepresentableException>(() =>
            ClassificationNormalization.Normalize(
                TargetProfile.ExternalApi,
                new ClassificationCandidateBatch(
                    [supportedTarget],
                    [
                        mixedAccessor with
                        {
                            PartialAmbiguous = true,
                        },
                    ],
                    [],
                    []),
                CancellationToken.None));
        Assert.Throws<ClassificationUnrepresentableException>(() =>
            ClassificationNormalization.Normalize(
                TargetProfile.ExternalApi,
                new ClassificationCandidateBatch(
                    [supportedTarget],
                    [],
                    [
                        new RelationObservationCandidate(
                            new RelationObservation(
                                RelationKind.Overrides,
                                parent,
                                new SymbolRef(
                                    "ctx-components",
                                    "P:Other.Value")),
                            PrimarySymbolKind.Class,
                            PrimarySymbolKind.Property),
                    ],
                    []),
                CancellationToken.None));

        var skippedParent = supportedTarget with
        {
            Origin = ClassificationOrigin.Mixed,
        };
        var suppressed = ClassificationNormalization.Normalize(
            TargetProfile.ExternalApi,
            new ClassificationCandidateBatch(
                [skippedParent],
                [mixedAccessor, unknown],
                [],
                []),
            CancellationToken.None);
        Assert.Empty(suppressed.Components);
    }

    [Fact]
    public async Task RealLoaderKeepsGeneratedTreeAuthorityAcrossIdentityCollisions()
    {
        await using var fixture = await LoaderFixture.CreateAsync(withGenerator: true);
        var request = new RepositoryLoadRequest(
            fixture.Root,
            "App/App.csproj",
            [
                new ToolGeneratedSourceInput(
                    "App/App.csproj",
                    "ContractScribe",
                    "ToolA",
                    "SharedOutput",
                    "public class ToolGeneratedA { }"),
                new ToolGeneratedSourceInput(
                    "App/App.csproj",
                    "ContractScribe",
                    "ToolB",
                    "SharedOutput",
                    "public class ToolGeneratedB { }"),
                new ToolGeneratedSourceInput(
                    "App/App.csproj",
                    "ContractScribe",
                    "ToolA",
                    "EmptyA",
                    "// identical generated text"),
                new ToolGeneratedSourceInput(
                    "App/App.csproj",
                    "ContractScribe",
                    "ToolB",
                    "EmptyB",
                    "// identical generated text"),
            ]);
        var loaded = await new RepositoryLoader().LoadAsync(request);
        Assert.True(
            loaded.Status == RepositoryLoadStatus.Success,
            $"{loaded.PrimaryFailure?.Stage}:{loaded.PrimaryFailure?.Code}");
        await using var session = Assert.IsType<LoadedRepositorySession>(loaded.Session);
        var app = Assert.Single(session.Projects, project =>
            project.Role == LoadedProjectRole.AuditRoot);
        Assert.Equal(app.Compilation.SyntaxTrees.Count(), app.SourceTrees.Count);

        var toolA = Assert.Single(session.GeneratedSources, fact =>
            fact.SourceText.Contains("ToolGeneratedA", StringComparison.Ordinal));
        var toolB = Assert.Single(session.GeneratedSources, fact =>
            fact.SourceText.Contains("ToolGeneratedB", StringComparison.Ordinal));
        Assert.Equal(toolA.OutputId, toolB.OutputId);
        Assert.NotEqual(toolA.ProducerId, toolB.ProducerId);
        var identical = session.GeneratedSources
            .Where(fact => fact.SourceText == "// identical generated text")
            .ToArray();
        Assert.Equal(2, identical.Length);
        Assert.Equal(identical[0].SourceSha256, identical[1].SourceSha256);
        Assert.NotEqual(
            (identical[0].ProducerId, identical[0].OutputId),
            (identical[1].ProducerId, identical[1].OutputId));

        var classifier = new SymbolClassifier(
            symbol => symbol.Name is "FixtureGenerated"
                    or "ToolGeneratedA"
                    or "ToolGeneratedB"
                ? null
                : symbol.GetDocumentationCommentId(),
            null,
            null,
            null);
        var set = AssertSuccess(classifier.Classify(
            session,
            TargetProfile.ExternalApi));
        var toolLocators = set.Unresolved
            .Select(record => record.CandidateLocator)
            .OfType<ToolGeneratedCandidateLocator>()
            .ToArray();
        Assert.Contains(toolLocators, locator =>
            locator.ProducerId == toolA.ProducerId
            && locator.OutputId == toolA.OutputId);
        Assert.Contains(toolLocators, locator =>
            locator.ProducerId == toolB.ProducerId
            && locator.OutputId == toolB.OutputId);
        var generatorFact = Assert.Single(session.GeneratedSources, fact =>
            fact.SourceText.Contains("FixtureGenerated", StringComparison.Ordinal));
        Assert.Contains(set.Unresolved, record =>
            record.CandidateLocator is GeneratedSourceCandidateLocator locator
            && locator.GeneratorId == generatorFact.ProducerId
            && locator.HintNameId == generatorFact.OutputId);
        Assert.All(
            app.SourceTrees.Where(pair =>
                pair.Value.Kind is LoadedSourceKind.SourceGenerator
                    or LoadedSourceKind.ToolGenerated),
            pair =>
            {
                Assert.NotNull(pair.Value.GeneratedSource);
                Assert.Same(
                    pair.Key,
                    app.Compilation.SyntaxTrees.Single(tree =>
                        ReferenceEquals(tree, pair.Key)));
            });
    }

    [Fact]
    public async Task RealLoaderRolesControlIndependentProjectEnumeration()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Root, "Library", "Library.cs"),
            """
            public interface ILibraryContract { string Read(); }
            public class LibraryBase
            {
                public virtual string Read() => "library";
            }
            """);
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Root, "App", "App.cs"),
            """
            public class App : LibraryBase, ILibraryContract
            {
                public override string Read() => "app";
            }
            """);
        var loader = new RepositoryLoader();
        var direct = await loader.LoadAsync(new RepositoryLoadRequest(
            fixture.Root,
            "App/App.csproj"));
        Assert.Equal(RepositoryLoadStatus.Success, direct.Status);
        await using (var directSession =
            Assert.IsType<LoadedRepositorySession>(direct.Session))
        {
            var directSet = AssertSuccess(new SymbolClassifier().Classify(
                directSession,
                TargetProfile.ExternalApi));
            Assert.Contains(directSet.Targets, target =>
                target.SymbolRef.DocumentationCommentId == "T:App");
            Assert.DoesNotContain(directSet.Targets, target =>
                target.SymbolRef.DocumentationCommentId is "T:LibraryBase"
                    or "T:ILibraryContract");
            Assert.DoesNotContain(directSet.Components, component =>
                component.ParentSymbolRef.DocumentationCommentId.StartsWith(
                    "T:Library",
                    StringComparison.Ordinal));
            Assert.DoesNotContain(directSet.Unresolved, unresolved =>
                unresolved.CompilationContextRef
                    == directSession.Projects.Single(project =>
                        project.Role == LoadedProjectRole.DependencyOnly)
                        .CompilationContextRef);
            Assert.Contains(directSet.Relations, relation =>
                relation.RelationKind == RelationKind.Overrides
                && relation.SourceSymbolRef.DocumentationCommentId == "M:App.Read"
                && relation.TargetSymbolRef.DocumentationCommentId
                    == "M:LibraryBase.Read");
            Assert.Contains(directSet.Relations, relation =>
                relation.RelationKind == RelationKind.ImplicitInterfaceImplementation
                && relation.SourceSymbolRef.DocumentationCommentId == "M:App.Read"
                && relation.TargetSymbolRef.DocumentationCommentId
                    == "M:ILibraryContract.Read");
            Assert.Single(directSession.Projects, project =>
                project.Role == LoadedProjectRole.AuditRoot);
        }

        var solution = await loader.LoadAsync(new RepositoryLoadRequest(
            fixture.Root,
            "Fixture.slnx"));
        Assert.Equal(RepositoryLoadStatus.Success, solution.Status);
        await using var solutionSession =
            Assert.IsType<LoadedRepositorySession>(solution.Session);
        var solutionSet = AssertSuccess(new SymbolClassifier().Classify(
            solutionSession,
            TargetProfile.ExternalApi));
        Assert.Contains(solutionSet.Targets, target =>
            target.SymbolRef.DocumentationCommentId == "T:App");
        Assert.Contains(solutionSet.Targets, target =>
            target.SymbolRef.DocumentationCommentId == "T:LibraryBase");
        Assert.Contains(solutionSet.Targets, target =>
            target.SymbolRef.DocumentationCommentId == "T:ILibraryContract");
        Assert.Contains(solutionSet.Relations, relation =>
            relation.RelationKind == RelationKind.Overrides
            && relation.SourceSymbolRef.DocumentationCommentId == "M:App.Read"
            && relation.TargetSymbolRef.DocumentationCommentId
                == "M:LibraryBase.Read");
        Assert.Contains(solutionSet.Relations, relation =>
            relation.RelationKind == RelationKind.ImplicitInterfaceImplementation
            && relation.SourceSymbolRef.DocumentationCommentId == "M:App.Read"
            && relation.TargetSymbolRef.DocumentationCommentId
                == "M:ILibraryContract.Read");
        Assert.Equal(
            2,
            solutionSession.Projects.Count(project =>
                project.Role == LoadedProjectRole.AuditRoot));
        Assert.Equal(
            solutionSet.Targets
                .Select(target => target.SymbolRef.CompilationContextRef)
                .Distinct()
                .Order(StringComparer.Ordinal),
            solutionSession.Projects
                .Where(project => project.Role == LoadedProjectRole.AuditRoot)
                .Select(project => project.CompilationContextRef)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ProductionRecordsConformToSchemaAndClosedRegistry()
    {
        const string source = """
            public interface IContract { int Value { get; } }
            public class Base { public virtual int Value => 0; }
            public class Derived : Base, IContract
            {
                public override int Value => 1;
            }
            internal class InternalOnly
            {
                internal void AssemblyMethod() { }
            }
            """;
        var compilation = Compile("Conformance", [Source("Conformance.cs", source)]);
        using var session = CreateSession(new ProjectFixture(
            "Conformance.csproj",
            "ctx-conformance",
            LoadedProjectRole.AuditRoot,
            compilation));
        var oracle = ClassificationConformanceOracle.Load(
            FindRepositoryRoot());
        foreach (var profile in Enum.GetValues<TargetProfile>())
        {
            var set = AssertSuccess(new SymbolClassifier().Classify(
                session,
                profile));
            var unresolved = AssertSuccess(new SymbolClassifier(
                symbol => symbol.Name == "Derived"
                    ? null
                    : symbol.GetDocumentationCommentId(),
                null,
                null,
                null).Classify(session, profile));
            var endpointKinds = new Dictionary<string, string>(
                StringComparer.Ordinal)
            {
                [Frame("ctx-conformance") + Frame("P:Base.Value")] =
                    "symbol.member.property",
                [Frame("ctx-conformance") + Frame("P:Derived.Value")] =
                    "symbol.member.property",
                [Frame("ctx-conformance") + Frame("P:IContract.Value")] =
                    "symbol.member.property",
            };

            AssertOracleConforms(
                oracle,
                profile,
                set,
                endpointKinds);
            AssertOracleConforms(
                oracle,
                profile,
                unresolved,
                endpointKinds);
            AssertExactConformanceSet(profile, set, unresolved, source);
        }

        using var registry = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "schemas",
            "symbol-evidence-taxonomy",
            "v1.registry.json")));
        var sections = registry.RootElement.GetProperty("sections");
        Assert.Equal(
            sections.GetProperty("targetProfiles")
                .EnumerateArray()
                .Select(entry => entry.GetProperty("id").GetString())
                .Order(StringComparer.Ordinal),
            Enum.GetValues<TargetProfile>()
                .Select(ClassificationVocabulary.GetId)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            sections.GetProperty("primaryKinds")
                .EnumerateArray()
                .Select(entry => entry.GetProperty("id").GetString())
                .Order(StringComparer.Ordinal),
            Enum.GetValues<PrimarySymbolKind>()
                .Select(ClassificationVocabulary.GetId)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            sections.GetProperty("componentKinds")
                .EnumerateArray()
                .Select(entry => entry.GetProperty("id").GetString())
                .Order(StringComparer.Ordinal),
            Enum.GetValues<ComponentKind>()
                .Select(ClassificationVocabulary.GetId)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            sections.GetProperty("relationKinds")
                .EnumerateArray()
                .Select(entry => entry.GetProperty("id").GetString())
                .Order(StringComparer.Ordinal),
            Enum.GetValues<RelationKind>()
                .Select(ClassificationVocabulary.GetId)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            sections.GetProperty("origins")
                .EnumerateArray()
                .Select(entry => entry.GetProperty("id").GetString())
                .Order(StringComparer.Ordinal),
            Enum.GetValues<ClassificationOrigin>()
                .Select(ClassificationVocabulary.GetId)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            sections.GetProperty("supportStatuses")
                .EnumerateArray()
                .Select(entry => entry.GetProperty("id").GetString())
                .Order(StringComparer.Ordinal),
            Enum.GetValues<SupportStatus>()
                .Select(ClassificationVocabulary.GetId)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            sections.GetProperty("skipReasons")
                .EnumerateArray()
                .Select(entry => entry.GetProperty("id").GetString())
                .Order(StringComparer.Ordinal),
            Enum.GetValues<SkipReason>()
                .Select(ClassificationVocabulary.GetId)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void BothProfilesAcceptACompleteEmptyProductionSet()
    {
        var compilation = Compile(
            "EmptyProfiles",
            [Source("EmptyProfiles.cs", "file class PrivateOnly { }")]);
        using var session = CreateSession(new ProjectFixture(
            "EmptyProfiles.csproj",
            "ctx-empty",
            LoadedProjectRole.AuditRoot,
            compilation));
        var oracle = ClassificationConformanceOracle.Load(
            FindRepositoryRoot());

        foreach (var profile in Enum.GetValues<TargetProfile>())
        {
            var set = AssertSuccess(new SymbolClassifier().Classify(
                session,
                profile));

            Assert.Empty(set.Targets);
            Assert.Empty(set.Components);
            Assert.Empty(set.Relations);
            Assert.Empty(set.Unresolved);
            AssertOracleConforms(oracle, profile, set);
        }
    }

    [Fact]
    public void SemanticOracleRejectsSkippedParentsTypedUnresolvedDuplicatesAndBadRelationDomains()
    {
        var oracle = ClassificationConformanceOracle.Load(
            FindRepositoryRoot());
        var skippedParentRecords = new[]
        {
            ParseRecord("""
                {
                  "recordType": "TargetClassification",
                  "symbolRef": {
                    "compilationContextRef": "ctx-oracle",
                    "documentationCommentId": "T:Parent"
                  },
                  "primaryKind": "symbol.type.class",
                  "traits": [],
                  "origin": "origin.mixed",
                  "supportStatus": "support.ambiguous",
                  "skipReason": "skip.ambiguous.mixed-origin"
                }
                """),
            ParseRecord("""
                {
                  "recordType": "ComponentClassification",
                  "parentSymbolRef": {
                    "compilationContextRef": "ctx-oracle",
                    "documentationCommentId": "T:Parent"
                  },
                  "componentKind": "component.type-parameter",
                  "identity": "type-parameter/0",
                  "origin": "origin.source",
                  "supportStatus": "support.supported"
                }
                """),
        };
        Assert.False(oracle.TryValidateSet(
            "profile.external-api",
            skippedParentRecords,
            null,
            out var skippedParentError));
        Assert.Contains(
            "component parent domain mismatch",
            skippedParentError,
            StringComparison.Ordinal);

        var duplicateTypedUnresolved = new[]
        {
            ParseRecord("""
                {
                  "recordType": "UnresolvedClassification",
                  "compilationContextRef": "ctx-oracle",
                  "origin": "origin.source",
                  "supportStatus": "support.unavailable-context",
                  "skipReason": "skip.unavailable.documentation-comment-id",
                  "candidateLocator": {
                    "repository": {
                      "path": "src/A.cs",
                      "span": { "start": 0, "end": 1 }
                    }
                  }
                }
                """),
            ParseRecord("""
                {
                  "candidateLocator": {
                    "repository": {
                      "span": { "end": 1, "start": 0 },
                      "path": "src/A.cs"
                    }
                  },
                  "skipReason": "skip.unavailable.documentation-comment-id",
                  "supportStatus": "support.unavailable-context",
                  "origin": "origin.source",
                  "compilationContextRef": "ctx-oracle",
                  "recordType": "UnresolvedClassification"
                }
                """),
        };
        Assert.False(oracle.TryValidateSet(
            "profile.external-api",
            duplicateTypedUnresolved,
            null,
            out var duplicateError));
        Assert.Contains(
            "duplicate classification key",
            duplicateError,
            StringComparison.Ordinal);

        var invalidRelation = new[]
        {
            ParseRecord("""
                {
                  "recordType": "RelationObservation",
                  "relationKind": "relation.overrides",
                  "sourceSymbolRef": {
                    "compilationContextRef": "ctx-oracle",
                    "documentationCommentId": "T:Source"
                  },
                  "targetSymbolRef": {
                    "compilationContextRef": "ctx-oracle",
                    "documentationCommentId": "T:Target"
                  }
                }
                """),
        };
        Assert.False(oracle.TryValidateSet(
            "profile.external-api",
            invalidRelation,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [Frame("ctx-oracle") + Frame("T:Source")] = "symbol.type.class",
                [Frame("ctx-oracle") + Frame("T:Target")] = "symbol.type.class",
            },
            out var relationError));
        Assert.Contains(
            "relation endpoint domain mismatch",
            relationError,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SemanticOracleEnforcesEveryStatusSkipAndOriginBranch()
    {
        static JsonElement Target(
            string kind,
            string origin,
            string status,
            string? skip = null)
        {
            var record = new JsonObject
            {
                ["recordType"] = "TargetClassification",
                ["symbolRef"] = new JsonObject
                {
                    ["compilationContextRef"] = "ctx-truth-table",
                    ["documentationCommentId"] = $"T:{kind}.{status}.{origin}",
                },
                ["primaryKind"] = kind,
                ["traits"] = new JsonArray(),
                ["origin"] = origin,
                ["supportStatus"] = status,
            };
            if (skip is not null)
            {
                record["skipReason"] = skip;
            }

            return Element(record);
        }

        static JsonElement Component(
            string kind,
            string identity,
            string origin,
            string status,
            string? skip = null)
        {
            var record = new JsonObject
            {
                ["recordType"] = "ComponentClassification",
                ["parentSymbolRef"] = new JsonObject
                {
                    ["compilationContextRef"] = "ctx-truth-table",
                    ["documentationCommentId"] = "T:Parent",
                },
                ["componentKind"] = kind,
                ["identity"] = identity,
                ["origin"] = origin,
                ["supportStatus"] = status,
            };
            if (skip is not null)
            {
                record["skipReason"] = skip;
            }

            return Element(record);
        }

        static JsonElement Unresolved(string origin, string skip) =>
            Element(new JsonObject
            {
                ["recordType"] = "UnresolvedClassification",
                ["compilationContextRef"] = "ctx-truth-table",
                ["origin"] = origin,
                ["supportStatus"] = "support.unavailable-context",
                ["skipReason"] = skip,
                ["candidateLocator"] = new JsonObject
                {
                    ["synthetic"] = new JsonObject
                    {
                        ["fixtureId"] = "truth-table",
                    },
                },
            });

        var oracle = ClassificationConformanceOracle.Load(
            FindRepositoryRoot());
        var valid = new[]
        {
            Target(
                "symbol.type.class",
                "origin.source",
                "support.supported"),
            Target(
                "symbol.unknown",
                "origin.mixed",
                "support.unsupported",
                "skip.unsupported.symbol-kind"),
            Target(
                "symbol.type.class",
                "origin.source",
                "support.ambiguous",
                "skip.ambiguous.partial-declaration"),
            Target(
                "symbol.type.class",
                "origin.mixed",
                "support.ambiguous",
                "skip.ambiguous.mixed-origin"),
            Target(
                "symbol.type.class",
                "origin.unknown",
                "support.unavailable-context",
                "skip.unavailable.generated-provenance"),
            Target(
                "symbol.type.class",
                "origin.mixed",
                "support.unavailable-context",
                "skip.unavailable.semantic-context"),
            Component(
                "component.parameter",
                "parameter/0",
                "origin.source",
                "support.supported"),
            Component(
                "component.unknown",
                "unknown/0",
                "origin.mixed",
                "support.unsupported",
                "skip.unsupported.component-kind"),
            Component(
                "component.parameter",
                "parameter/0",
                "origin.mixed",
                "support.ambiguous",
                "skip.ambiguous.mixed-origin"),
            Component(
                "component.synthesized.implicit-constructor",
                "synthesized/implicit-constructor",
                "origin.compiler-synthesized",
                "support.not-applicable",
                "skip.not-applicable.synthesized-non-target"),
            Component(
                "component.accessor.get",
                "accessor/get",
                "origin.source",
                "support.not-applicable",
                "skip.not-applicable.non-documentation-component"),
            Component(
                "component.parameter",
                "parameter/0",
                "origin.unknown",
                "support.unavailable-context",
                "skip.unavailable.generated-provenance"),
            Component(
                "component.parameter",
                "parameter/0",
                "origin.mixed",
                "support.unavailable-context",
                "skip.unavailable.semantic-context"),
            Unresolved(
                "origin.source",
                "skip.unavailable.documentation-comment-id"),
            Unresolved(
                "origin.unknown",
                "skip.unavailable.generated-provenance"),
            Unresolved(
                "origin.mixed",
                "skip.unavailable.semantic-context"),
        };
        Assert.All(valid, record => Assert.True(oracle.IsValidRecord(record)));

        var invalid = new[]
        {
            Target(
                "symbol.type.class",
                "origin.mixed",
                "support.supported"),
            Target(
                "symbol.type.class",
                "origin.source",
                "support.ambiguous",
                "skip.ambiguous.mixed-origin"),
            Target(
                "symbol.type.class",
                "origin.source",
                "support.unavailable-context",
                "skip.unavailable.generated-provenance"),
            Target(
                "symbol.type.class",
                "origin.unknown",
                "support.unavailable-context",
                "skip.unavailable.semantic-context"),
            Component(
                "component.accessor.get",
                "accessor/get",
                "origin.mixed",
                "support.not-applicable",
                "skip.not-applicable.non-documentation-component"),
            Component(
                "component.parameter",
                "parameter/0",
                "origin.source",
                "support.ambiguous",
                "skip.ambiguous.mixed-origin"),
            Unresolved(
                "origin.source",
                "skip.unavailable.generated-provenance"),
            Unresolved(
                "origin.unknown",
                "skip.unavailable.documentation-comment-id"),
        };
        Assert.All(invalid, record => Assert.False(oracle.IsValidRecord(record)));
    }

    [Fact]
    public void SemanticOraclePreservesTypedLocatorIdentityAndExactPathGrammar()
    {
        static JsonElement RepositoryRecord(
            string path,
            bool includeSpan)
        {
            var repository = new JsonObject
            {
                ["path"] = path,
            };
            if (includeSpan)
            {
                repository["span"] = new JsonObject
                {
                    ["start"] = 0,
                    ["end"] = 0,
                };
            }

            return Element(new JsonObject
            {
                ["recordType"] = "UnresolvedClassification",
                ["compilationContextRef"] = "ctx-locator-key",
                ["origin"] = "origin.source",
                ["supportStatus"] = "support.unavailable-context",
                ["skipReason"] = "skip.unavailable.documentation-comment-id",
                ["candidateLocator"] = new JsonObject
                {
                    ["repository"] = repository,
                },
            });
        }

        var oracle = ClassificationConformanceOracle.Load(
            FindRepositoryRoot());
        Assert.True(
            oracle.TryValidateSet(
                "profile.external-api",
                [
                    RepositoryRecord("a|1|0", false),
                    RepositoryRecord("a", true),
                ],
                null,
                out var identityError),
            identityError);

        foreach (var invalidPath in new[]
                 {
                     @"src\A.cs",
                     "src//A.cs",
                     "src/./A.cs",
                     "src/A.cs/",
                 })
        {
            Assert.False(oracle.IsValidRecord(
                RepositoryRecord(invalidPath, false)));
        }
    }

    [Fact]
    public void RepositoryLocatorsPreserveLexicalIdentityAndUtf16Spans()
    {
        const string source = "// 😀 before declaration\npublic class UnicodeName { }";
        var compilation = Compile(
            "Locators",
            [Source("src/Éxample.cs", source)]);
        using var session = CreateSession(new ProjectFixture(
            "Locators.csproj",
            "ctx-locators",
            LoadedProjectRole.AuditRoot,
            compilation));
        var set = AssertSuccess(new SymbolClassifier(
            symbol => symbol.Name == "UnicodeName"
                ? null
                : symbol.GetDocumentationCommentId(),
            null,
            null,
            null).Classify(session, TargetProfile.ExternalApi));

        var unresolved = Assert.Single(set.Unresolved);
        var locator = Assert.IsType<RepositoryCandidateLocator>(
            unresolved.CandidateLocator);
        Assert.Equal("src/Éxample.cs", locator.Path);
        var span = Assert.IsType<Utf16Span>(locator.Span);
        Assert.Equal(
            source.IndexOf("UnicodeName", StringComparison.Ordinal),
            span.Start);
        Assert.Equal(span.Start + "UnicodeName".Length, span.End);

        var missingA = new TargetClassificationCandidate(
            "ctx-a",
            null,
            PrimarySymbolKind.Class,
            [],
            ClassificationOrigin.Source,
            [
                new RepositoryCandidateLocator("src/a.cs", new Utf16Span(0, 0)),
                new RepositoryCandidateLocator("src/a.cs"),
                new RepositoryCandidateLocator("src/A.cs"),
                new RepositoryCandidateLocator("src/é.cs"),
                new RepositoryCandidateLocator("src/é.cs"),
            ]);
        var missingB = missingA with
        {
            CompilationContextRef = "ctx-b",
            CandidateLocators = [new RepositoryCandidateLocator("src/a.cs")],
        };
        var normalized = ClassificationNormalization.Normalize(
            TargetProfile.ExternalApi,
            new ClassificationCandidateBatch([missingB, missingA], [], [], []),
            CancellationToken.None);
        Assert.Equal(6, normalized.Unresolved.Length);
        var samePath = normalized.Unresolved
            .Where(record =>
                record.CompilationContextRef == "ctx-a"
                && record.CandidateLocator is RepositoryCandidateLocator
                { Path: "src/a.cs" })
            .ToArray();
        Assert.Collection(
            samePath,
            item => Assert.Null(
                Assert.IsType<RepositoryCandidateLocator>(
                    item.CandidateLocator).Span),
            item => Assert.Equal(
                new Utf16Span(0, 0),
                Assert.IsType<RepositoryCandidateLocator>(
                    item.CandidateLocator).Span));
        Assert.Contains(normalized.Unresolved, record =>
            record.CompilationContextRef == "ctx-b"
            && record.CandidateLocator
                == new RepositoryCandidateLocator("src/a.cs"));
    }

    [Fact]
    public void IdenticalDocumentationIdsRemainDistinctAcrossContexts()
    {
        var first = Compile(
            "First",
            [Source("First/Same.cs", "public class Same { }")]);
        var second = Compile(
            "Second",
            [Source("Second/Same.cs", "public class Same { }")]);
        using var session = CreateSession(
            new ProjectFixture(
                "Second/Second.csproj",
                "ctx-second",
                LoadedProjectRole.AuditRoot,
                second),
            new ProjectFixture(
                "First/First.csproj",
                "ctx-first",
                LoadedProjectRole.AuditRoot,
                first));

        var set = AssertSuccess(new SymbolClassifier().Classify(
            session,
            TargetProfile.ExternalApi));
        var same = set.Targets
            .Where(target =>
                target.SymbolRef.DocumentationCommentId == "T:Same")
            .ToArray();
        Assert.Collection(
            same,
            target => Assert.Equal(
                "ctx-first",
                target.SymbolRef.CompilationContextRef),
            target => Assert.Equal(
                "ctx-second",
                target.SymbolRef.CompilationContextRef));
    }

    [Fact]
    public void InvalidClosedValuesLocatorsAndUnicodeFailClosed()
    {
        var baseCandidate = new TargetClassificationCandidate(
            "ctx-invalid",
            null,
            PrimarySymbolKind.Class,
            [],
            ClassificationOrigin.Source,
            [new RepositoryCandidateLocator("src/Valid.cs")]);
        var invalid = new[]
        {
            baseCandidate with
            {
                CandidateLocators =
                [
                    new RepositoryCandidateLocator("../secret.cs"),
                ],
            },
            baseCandidate with
            {
                CandidateLocators =
                [
                    new RepositoryCandidateLocator("C:secret.cs"),
                ],
            },
            baseCandidate with
            {
                CandidateLocators =
                [
                    new RepositoryCandidateLocator("C:/secret.cs"),
                ],
            },
            baseCandidate with
            {
                CandidateLocators =
                [
                    new RepositoryCandidateLocator("src/\0secret.cs"),
                ],
            },
            baseCandidate with
            {
                CandidateLocators =
                [
                    new RepositoryCandidateLocator(@"src\secret.cs"),
                ],
            },
            baseCandidate with
            {
                CandidateLocators =
                [
                    new RepositoryCandidateLocator("/src/secret.cs"),
                ],
            },
            baseCandidate with
            {
                CandidateLocators =
                [
                    new ToolGeneratedCandidateLocator(
                        Opaque("sgp.", '1'),
                        Opaque("sgo.", '2')),
                ],
            },
            baseCandidate with
            {
                CandidateLocators =
                [
                    new RepositoryCandidateLocator(
                        "src/Valid.cs",
                        new Utf16Span(4, 3)),
                ],
            },
            baseCandidate with
            {
                DocumentationCommentId = "T:\ud800",
                CandidateLocators = [],
            },
            baseCandidate with
            {
                DocumentationCommentId = "T:Unknown",
                PrimaryKind = (PrimarySymbolKind)999,
                CandidateLocators = [],
            },
        };

        foreach (var candidate in invalid)
        {
            Assert.Throws<ClassificationUnrepresentableException>(() =>
                ClassificationNormalization.Normalize(
                    TargetProfile.ExternalApi,
                    new ClassificationCandidateBatch([candidate], [], [], []),
                    CancellationToken.None));
        }
    }

    private static ClassificationSet AssertSuccess(ClassificationOutcome outcome)
    {
        Assert.Equal(ClassificationRunStatus.Success, outcome.Status);
        Assert.Null(outcome.PrimaryFailure);
        return Assert.IsType<ClassificationSet>(outcome.ClassificationSet);
    }

    private static CompilationFixture Compile(
        string assemblyName,
        IReadOnlyList<SourceFixture> sources,
        IReadOnlyList<MetadataReference>? additionalReferences = null)
    {
        var trees = sources
            .Select(source => CSharpSyntaxTree.ParseText(
                source.Text,
                CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview),
                source.Path))
            .ToArray();
        var references = PlatformReferences()
            .Concat(additionalReferences ?? [])
            .ToArray();
        var compilation = CSharpCompilation.Create(
            assemblyName,
            trees,
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
        var errors = compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Assert.True(
            errors.Length == 0,
            string.Join(Environment.NewLine, errors.Select(error => error.ToString())));
        var bindings = new Dictionary<SyntaxTree, LoadedSourceTree>(
            ReferenceEqualityComparer.Instance);
        for (var index = 0; index < sources.Count; index++)
        {
            var source = sources[index];
            bindings.Add(
                trees[index],
                source.Kind == LoadedSourceKind.Repository
                     ? new LoadedSourceTree(
                         LoadedSourceKind.Repository,
                         source.Path,
                         source.Path,
                         null)
                     : new LoadedSourceTree(
                         source.Kind,
                         null,
                         null,
                         source.GeneratedSource));
        }

        return new CompilationFixture(compilation, bindings, sources);
    }

    private static LoadedRepositorySession CreateSession(
        params ProjectFixture[] fixtures)
    {
        var workspace = new AdhocWorkspace();
        var projects = new List<LoadedProject>();
        var generated = new List<GeneratedSourceFact>();
        foreach (var fixture in fixtures)
        {
            var project = workspace.AddProject(
                fixture.ProjectIdentity,
                LanguageNames.CSharp);
            projects.Add(new LoadedProject(
                fixture.ProjectIdentity,
                "net10.0",
                fixture.Context,
                fixture.Role,
                fixture.ProjectReferences,
                project,
                fixture.Compilation.Compilation,
                fixture.Compilation.Bindings));
            generated.AddRange(fixture.Compilation.Sources
                .Select(source => source.GeneratedSource)
                .Where(fact => fact is not null)!);
        }

        Assert.True(RepositoryContextRef.TryParse(
            "repoctx-00000000000000000000000000000000",
            out var repositoryContextRef));
        return new LoadedRepositorySession(
            repositoryContextRef,
            Path.GetFullPath("."),
            fixtures[0].ProjectIdentity,
            new ToolchainIdentity("test", "test", "test", "test"),
            projects,
            generated,
            workspace);
    }

    private static IReadOnlyList<MetadataReference> PlatformReferences() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();

    private static SourceFixture Source(
        string path,
        string text,
        LoadedSourceKind kind = LoadedSourceKind.Repository,
        GeneratedSourceFact? generatedSource = null) =>
        new(path, text, kind, generatedSource);

    private static string Opaque(string prefix, char value) =>
        prefix + new string(value, 64);

    private static JsonObject Project(TargetClassification target)
    {
        var result = new JsonObject
        {
            ["recordType"] = "TargetClassification",
            ["symbolRef"] = Project(target.SymbolRef),
            ["primaryKind"] = ClassificationVocabulary.GetId(target.PrimaryKind),
            ["traits"] = new JsonArray(target.Traits
                .Select(trait => JsonValue.Create(
                    ClassificationVocabulary.GetId(trait)))
                .ToArray()),
            ["origin"] = ClassificationVocabulary.GetId(target.Origin),
            ["supportStatus"] = ClassificationVocabulary.GetId(target.SupportStatus),
        };
        if (target.SkipReason is { } skip)
        {
            result["skipReason"] = ClassificationVocabulary.GetId(skip);
        }

        return result;
    }

    private static JsonObject Project(ComponentClassification component)
    {
        var result = new JsonObject
        {
            ["recordType"] = "ComponentClassification",
            ["parentSymbolRef"] = Project(component.ParentSymbolRef),
            ["componentKind"] = ClassificationVocabulary.GetId(component.ComponentKind),
            ["identity"] = component.Identity,
            ["origin"] = ClassificationVocabulary.GetId(component.Origin),
            ["supportStatus"] = ClassificationVocabulary.GetId(component.SupportStatus),
        };
        if (component.SkipReason is { } skip)
        {
            result["skipReason"] = ClassificationVocabulary.GetId(skip);
        }

        return result;
    }

    private static JsonObject Project(RelationObservation relation) => new()
    {
        ["recordType"] = "RelationObservation",
        ["relationKind"] = ClassificationVocabulary.GetId(relation.RelationKind),
        ["sourceSymbolRef"] = Project(relation.SourceSymbolRef),
        ["targetSymbolRef"] = Project(relation.TargetSymbolRef),
    };

    private static JsonObject Project(UnresolvedClassification unresolved) => new()
    {
        ["recordType"] = "UnresolvedClassification",
        ["compilationContextRef"] = unresolved.CompilationContextRef,
        ["origin"] = ClassificationVocabulary.GetId(unresolved.Origin),
        ["supportStatus"] = ClassificationVocabulary.GetId(unresolved.SupportStatus),
        ["skipReason"] = ClassificationVocabulary.GetId(unresolved.SkipReason),
        ["candidateLocator"] = Project(unresolved.CandidateLocator),
    };

    private static JsonObject Project(SymbolRef symbolRef) => new()
    {
        ["compilationContextRef"] = symbolRef.CompilationContextRef,
        ["documentationCommentId"] = symbolRef.DocumentationCommentId,
    };

    private static JsonObject Project(CandidateLocator locator)
    {
        static JsonObject WithSpan(JsonObject value, Utf16Span? span)
        {
            if (span is { } actual)
            {
                value["span"] = new JsonObject
                {
                    ["start"] = actual.Start,
                    ["end"] = actual.End,
                };
            }

            return value;
        }

        return locator switch
        {
            RepositoryCandidateLocator repository => new JsonObject
            {
                ["repository"] = WithSpan(
                    new JsonObject { ["path"] = repository.Path },
                    repository.Span),
            },
            GeneratedSourceCandidateLocator generated => new JsonObject
            {
                ["generatedSource"] = WithSpan(
                    new JsonObject
                    {
                        ["generatorId"] = generated.GeneratorId,
                        ["hintNameId"] = generated.HintNameId,
                    },
                    generated.Span),
            },
            ToolGeneratedCandidateLocator generated => new JsonObject
            {
                ["toolGenerated"] = WithSpan(
                    new JsonObject
                    {
                        ["producerId"] = generated.ProducerId,
                        ["outputId"] = generated.OutputId,
                    },
                    generated.Span),
            },
            SyntheticCandidateLocator synthetic => new JsonObject
            {
                ["synthetic"] = new JsonObject
                {
                    ["fixtureId"] = synthetic.FixtureId,
                },
            },
            _ => throw new InvalidOperationException("Unknown candidate locator."),
        };
    }

    private static JsonElement Element(JsonNode node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    private static JsonElement ParseRecord(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static void AssertOracleConforms(
        ClassificationConformanceOracle oracle,
        TargetProfile profile,
        ClassificationSet set,
        IReadOnlyDictionary<string, string>? independentEndpointKinds = null)
    {
        var records = set.Targets.Select(Project)
            .Concat(set.Components.Select(Project))
            .Concat(set.Relations.Select(Project))
            .Concat(set.Unresolved.Select(Project))
            .Select(Element)
            .ToArray();

        Assert.True(
            oracle.TryValidateSet(
                ClassificationVocabulary.GetId(profile),
                records,
                independentEndpointKinds,
                out var error),
            error);
    }

    private static IReadOnlyDictionary<string, string> RelationEndpointKinds(
        ClassificationSet set)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var relation in set.Relations)
        {
            result[SymbolKey(relation.SourceSymbolRef)] =
                EndpointKind(relation.SourceSymbolRef.DocumentationCommentId);
            result[SymbolKey(relation.TargetSymbolRef)] =
                EndpointKind(relation.TargetSymbolRef.DocumentationCommentId);
        }

        return result;
    }

    private static string EndpointKind(string documentationCommentId) =>
        documentationCommentId switch
        {
            ['T', ':', ..] => "symbol.type.interface",
            ['M', ':', ..] when documentationCommentId.Contains(
                "op_Implicit",
                StringComparison.Ordinal)
                || documentationCommentId.Contains(
                    "op_Explicit",
                    StringComparison.Ordinal) =>
                "symbol.member.conversion",
            ['M', ':', ..] when documentationCommentId.Contains(
                "op_",
                StringComparison.Ordinal) =>
                "symbol.member.operator",
            ['M', ':', ..] => "symbol.member.method",
            ['E', ':', ..] => "symbol.member.event",
            ['P', ':', ..] when documentationCommentId.Contains(
                "Item(",
                StringComparison.Ordinal) =>
                "symbol.member.indexer",
            ['P', ':', ..] => "symbol.member.property",
            _ => throw new InvalidOperationException(
                $"No independent endpoint kind for {documentationCommentId}."),
        };

    private static string SymbolKey(SymbolRef symbolRef) =>
        Frame(symbolRef.CompilationContextRef)
        + Frame(symbolRef.DocumentationCommentId);

    private static string Frame(string value) =>
        value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + ":"
        + value;

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

    private static void AssertExactConformanceSet(
        TargetProfile profile,
        ClassificationSet set,
        ClassificationSet unresolvedSet,
        string source)
    {
        var externalTargets = new[]
        {
            "ctx-conformance|P:Base.Value|symbol.member.property|trait.virtual|origin.source|support.supported|",
            "ctx-conformance|P:Derived.Value|symbol.member.property||origin.source|support.supported|",
            "ctx-conformance|P:IContract.Value|symbol.member.property|trait.abstract|origin.source|support.supported|",
            "ctx-conformance|T:Base|symbol.type.class||origin.source|support.supported|",
            "ctx-conformance|T:Derived|symbol.type.class||origin.source|support.supported|",
            "ctx-conformance|T:IContract|symbol.type.interface|trait.abstract|origin.source|support.supported|",
        };
        var assemblyOnlyTargets = new[]
        {
            "ctx-conformance|M:InternalOnly.AssemblyMethod|symbol.member.method||origin.source|support.supported|",
            "ctx-conformance|T:InternalOnly|symbol.type.class||origin.source|support.supported|",
        };
        var externalComponents = new[]
        {
            "ctx-conformance|P:Base.Value|component.accessor.get|accessor/get|origin.source|support.not-applicable|skip.not-applicable.non-documentation-component",
            "ctx-conformance|P:Base.Value|component.value|value|origin.source|support.supported|",
            "ctx-conformance|P:Derived.Value|component.accessor.get|accessor/get|origin.source|support.not-applicable|skip.not-applicable.non-documentation-component",
            "ctx-conformance|P:Derived.Value|component.value|value|origin.source|support.supported|",
            "ctx-conformance|P:IContract.Value|component.accessor.get|accessor/get|origin.source|support.not-applicable|skip.not-applicable.non-documentation-component",
            "ctx-conformance|P:IContract.Value|component.value|value|origin.source|support.supported|",
            "ctx-conformance|T:Base|component.synthesized.implicit-constructor|synthesized/implicit-constructor|origin.compiler-synthesized|support.not-applicable|skip.not-applicable.synthesized-non-target",
            "ctx-conformance|T:Derived|component.synthesized.implicit-constructor|synthesized/implicit-constructor|origin.compiler-synthesized|support.not-applicable|skip.not-applicable.synthesized-non-target",
        };
        var assemblyOnlyComponents = new[]
        {
            "ctx-conformance|T:InternalOnly|component.synthesized.implicit-constructor|synthesized/implicit-constructor|origin.compiler-synthesized|support.not-applicable|skip.not-applicable.synthesized-non-target",
        };
        var expectedTargets = profile == TargetProfile.ExternalApi
            ? externalTargets
            : externalTargets
                .Concat(assemblyOnlyTargets)
                .Order(StringComparer.Ordinal)
                .ToArray();
        var expectedComponents = profile == TargetProfile.ExternalApi
            ? externalComponents
            : externalComponents
                .Concat(assemblyOnlyComponents)
                .Order(StringComparer.Ordinal)
                .ToArray();

        AssertExactSequence(expectedTargets, set.Targets.Select(TargetKey));
        AssertExactSequence(
            expectedComponents,
            set.Components.Select(ComponentKey));
        AssertExactSequence(
            [
                "ctx-conformance|P:Derived.Value|relation.implicit-interface-implementation|ctx-conformance|P:IContract.Value",
                "ctx-conformance|P:Derived.Value|relation.overrides|ctx-conformance|P:Base.Value",
            ],
            set.Relations.Select(RelationKey));

        var unresolved = Assert.Single(unresolvedSet.Unresolved);
        Assert.Equal("ctx-conformance", unresolved.CompilationContextRef);
        Assert.Equal(ClassificationOrigin.Source, unresolved.Origin);
        Assert.Equal(
            SupportStatus.UnavailableContext,
            unresolved.SupportStatus);
        Assert.Equal(
            SkipReason.UnavailableDocumentationCommentId,
            unresolved.SkipReason);
        var locator = Assert.IsType<RepositoryCandidateLocator>(
            unresolved.CandidateLocator);
        Assert.Equal("Conformance.cs", locator.Path);
        Assert.Equal(
            new Utf16Span(
                source.IndexOf("Derived", StringComparison.Ordinal),
                source.IndexOf("Derived", StringComparison.Ordinal)
                    + "Derived".Length),
            locator.Span);
    }

    private static void AssertExactSequence(
        IEnumerable<string> expected,
        IEnumerable<string> actual)
    {
        var expectedArray = expected.ToArray();
        var actualArray = actual.ToArray();
        Assert.True(
            expectedArray.SequenceEqual(actualArray, StringComparer.Ordinal),
            $"""
            Expected:
            {string.Join(Environment.NewLine, expectedArray)}

            Actual:
            {string.Join(Environment.NewLine, actualArray)}
            """);
    }

    private static string TargetKey(TargetClassification target) =>
        string.Join(
            "|",
            target.SymbolRef.CompilationContextRef,
            target.SymbolRef.DocumentationCommentId,
            ClassificationVocabulary.GetId(target.PrimaryKind),
            string.Join(",", target.Traits.Select(ClassificationVocabulary.GetId)),
            ClassificationVocabulary.GetId(target.Origin),
            ClassificationVocabulary.GetId(target.SupportStatus),
            target.SkipReason is { } skip
                ? ClassificationVocabulary.GetId(skip)
                : string.Empty);

    private static string RelationKey(RelationObservation relation) =>
        string.Join(
            "|",
            relation.SourceSymbolRef.CompilationContextRef,
            relation.SourceSymbolRef.DocumentationCommentId,
            ClassificationVocabulary.GetId(relation.RelationKind),
            relation.TargetSymbolRef.CompilationContextRef,
            relation.TargetSymbolRef.DocumentationCommentId);

    private static string ComponentKey(ComponentClassification component) =>
        string.Join(
            "|",
            component.ParentSymbolRef.CompilationContextRef,
            component.ParentSymbolRef.DocumentationCommentId,
            ClassificationVocabulary.GetId(component.ComponentKind),
            component.Identity,
            ClassificationVocabulary.GetId(component.Origin),
            ClassificationVocabulary.GetId(component.SupportStatus),
            component.SkipReason is { } skip
                ? ClassificationVocabulary.GetId(skip)
                : string.Empty);

    private static string UnresolvedKey(UnresolvedClassification unresolved) =>
        string.Join(
            "|",
            unresolved.CompilationContextRef,
            ClassificationVocabulary.GetId(unresolved.Origin),
            ClassificationVocabulary.GetId(unresolved.SupportStatus),
            ClassificationVocabulary.GetId(unresolved.SkipReason),
            unresolved.CandidateLocator.ToString());

    private sealed record SourceFixture(
        string Path,
        string Text,
        LoadedSourceKind Kind,
        GeneratedSourceFact? GeneratedSource);

    private sealed record CompilationFixture(
        CSharpCompilation Compilation,
        IReadOnlyDictionary<SyntaxTree, LoadedSourceTree> Bindings,
        IReadOnlyList<SourceFixture> Sources);

    private sealed record ProjectFixture(
        string ProjectIdentity,
        string Context,
        LoadedProjectRole Role,
        CompilationFixture Compilation,
        IReadOnlyList<string>? References = null)
    {
        public IReadOnlyList<string> ProjectReferences { get; } = References ?? [];
    }

    private sealed record CollisionCase(
        string Name,
        TargetClassificationCandidate Candidate,
        bool Fails,
        PrimarySymbolKind? PrimaryKind,
        ClassificationOrigin? Origin,
        SupportStatus? Status,
        SkipReason? Skip);
}
