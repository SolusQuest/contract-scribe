using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ContractScribe.Core;
using ContractScribe.Patching;
using ContractScribe.Roslyn;
using ContractScribe.Roslyn.IntegrationTests;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace ContractScribe.IntegrationTests;

[Collection("Integration process lane 1")]
public sealed class DocumentationPatchEndToEndTests
{
    [Fact]
    public async Task RealLoaderValidatesEverySourceAndAdditionalFileRoleAndRerunsGenerators()
    {
        await using var fixture = await RealLoaderEngineFixture.CreateAsync(
            enableDocumentationSensitiveGenerator: false,
            enableNoOutputToOutputGenerator: false,
            enableSelfObservingGenerator: true,
            enableStableGeneratorDiagnostic: true);
        if (!OperatingSystem.IsLinux())
        {
            Assert.Equal(2, fixture.Repository.Projects.Count);
            Assert.All(fixture.Repository.Projects, project =>
            {
                Assert.Equal(LoadedProjectRole.AuditRoot, project.Role);
                Assert.Empty(project.ProjectReferences);
            });
            var preflightApp = Assert.Single(fixture.Repository.Projects, project =>
                project.ProjectIdentity == "App/App.csproj");
            Assert.Contains(preflightApp.Project.Documents, document =>
                Path.GetFileName(document.FilePath) == "Target.cs");
            Assert.Contains(preflightApp.Project.AdditionalDocuments, document =>
                Path.GetFileName(document.FilePath) == "Target.cs");
            Assert.Contains(preflightApp.Project.AnalyzerConfigDocuments, document =>
                Path.GetFileName(document.FilePath) == "Target.cs");
            Assert.Contains(fixture.Repository.Projects.SelectMany(project =>
                project.SourceTrees.Values), source => source.Kind == LoadedSourceKind.ToolGenerated);
            return;
        }

        var outcome = new DocumentationPatchEngine(
            () => fixture.StagingParent,
            null,
            null).Execute(fixture.ClassifiedSession, fixture.Request);

        var result = Assert.IsType<DocumentationPatchValidationResult>(outcome.Result);
        Assert.Equal(DocumentationPatchOutcome.Accepted, result.Outcome);
        var capability = Assert.IsType<DocumentationPatchAcceptedCandidate>(
            outcome.AcceptedCandidate);
        var targetFacts = capability.Baseline.SemanticInputs.Where(fact =>
                fact.RepositoryPath == "App/Target.cs")
            .ToArray();
        Assert.Equal(3, targetFacts.Length);
        Assert.Single(targetFacts, fact =>
            fact.Role == DocumentationPatchSemanticInputRole.Source);
        Assert.Single(targetFacts, fact =>
            fact.Role == DocumentationPatchSemanticInputRole.AdditionalFile);
        Assert.Single(targetFacts, fact =>
            fact.Role == DocumentationPatchSemanticInputRole.AnalyzerConfig);
        Assert.Contains(targetFacts, fact => fact.LogicalPath == "Target.cs");
        Assert.Contains(targetFacts, fact =>
            fact.LogicalPath == "Logical/Input/Target-copy.cs");
        Assert.Contains(targetFacts, fact =>
            fact.LogicalPath == "Logical/Config/Target-as-config.cs");
        var linkedSourceFacts = capability.Baseline.SemanticInputs.Where(fact =>
                fact.RepositoryPath == "App/App.cs"
                && fact.Role == DocumentationPatchSemanticInputRole.Source)
            .ToArray();
        Assert.Equal(2, linkedSourceFacts.Length);
        Assert.Contains(linkedSourceFacts, fact => fact.LogicalPath == "App.cs");
        Assert.Contains(linkedSourceFacts, fact =>
            fact.LogicalPath == "Logical/Library/App-linked.cs");
        var app = Assert.Single(fixture.Repository.Projects, project =>
            project.ProjectIdentity == "App/App.csproj");
        var library = Assert.Single(fixture.Repository.Projects, project =>
            project.ProjectIdentity == "Library/Library.csproj");
        Assert.Equal(LoadedProjectRole.AuditRoot, app.Role);
        Assert.Equal(LoadedProjectRole.AuditRoot, library.Role);
        Assert.Empty(app.ProjectReferences);
        Assert.Empty(library.ProjectReferences);
        Assert.Contains(
            "APP_CONTEXT",
            Assert.IsType<CSharpParseOptions>(app.Project.ParseOptions).PreprocessorSymbolNames);
        Assert.Contains(
            "LIBRARY_CONTEXT",
            Assert.IsType<CSharpParseOptions>(library.Project.ParseOptions).PreprocessorSymbolNames);
        Assert.Contains(fixture.Repository.GeneratedSources, fact =>
            fact.SourceText.Contains(
                "FixtureSelfAware { public const string Value = \"clean\"; }",
                StringComparison.Ordinal));
        Assert.Contains(fixture.Repository.Projects.SelectMany(project =>
            project.SourceTrees.Values), source => source.Kind == LoadedSourceKind.ToolGenerated);
        Assert.Equal(
            fixture.Repository.GeneratedSources.Count,
            capability.RoslynEvidence.ValidatedGeneratedSourceCount);
        var acceptedSource = Assert.Single(
            capability.Files,
            file => file.RepositoryPath == "App/Target.cs");
        var analyzerConfigEvidence = Assert.Single(
            capability.RoslynEvidence.ValidatedSemanticInputs,
            fact => fact.RepositoryPath == "App/Target.cs"
                && fact.Role == DocumentationPatchSemanticInputRole.AnalyzerConfig);
        Assert.Equal(acceptedSource.Sha256, analyzerConfigEvidence.CandidateSha256);
        Assert.All(result.Invariants, invariant =>
            Assert.Equal(DocumentationPatchInvariantStatus.Passed, invariant.Status));
    }

    [Fact]
    public async Task DocumentationSensitiveAndNoOutputToOutputGeneratorsFailClosed()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        await using var fixture = await RealLoaderEngineFixture.CreateAsync(
            enableDocumentationSensitiveGenerator: true,
            enableNoOutputToOutputGenerator: true);
        Assert.Contains(fixture.Repository.GeneratedSources, fact =>
            fact.SourceText.Contains(
                "FixtureDocumentationSensitive",
                StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Repository.GeneratedSources, fact =>
            fact.SourceText.Contains("FixtureNoOutputToOutput", StringComparison.Ordinal));

        var outcome = new DocumentationPatchEngine(
            () => fixture.StagingParent,
            null,
            null).Execute(fixture.ClassifiedSession, fixture.Request);

        Assert.Equal(DocumentationPatchExecutionStatus.Result, outcome.Status);
        var result = Assert.IsType<DocumentationPatchValidationResult>(outcome.Result);
        Assert.Equal(DocumentationPatchOutcome.Rejected, result.Outcome);
        Assert.All(result.Targets, target =>
            Assert.Equal(DocumentationPatchTargetStatus.Invalid, target.Status));
        Assert.Equal("patch.rejected.unsafe-change", Assert.Single(result.Diagnostics).Code);
        Assert.Null(outcome.AcceptedCandidate);
    }

    [Fact]
    public async Task RequestWideGeneratorFailureMarksEveryResolvedTargetInvalid()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        await using var fixture = await RealLoaderEngineFixture.CreateAsync(
            enableDocumentationSensitiveGenerator: true,
            enableNoOutputToOutputGenerator: false,
            multiTarget: true);

        var outcome = new DocumentationPatchEngine(
            () => fixture.StagingParent,
            null,
            null).Execute(fixture.ClassifiedSession, fixture.Request);

        var result = Assert.IsType<DocumentationPatchValidationResult>(outcome.Result);
        Assert.Equal(DocumentationPatchOutcome.Rejected, result.Outcome);
        Assert.Equal(2, result.Targets.Length);
        Assert.All(result.Targets, target =>
            Assert.Equal(DocumentationPatchTargetStatus.Invalid, target.Status));
        Assert.Equal(
            ["block-1", "block-2"],
            result.Diagnostics.Select(diagnostic => diagnostic.BlockId));
        Assert.All(result.Diagnostics, diagnostic =>
            Assert.Equal("patch.rejected.unsafe-change", diagnostic.Code));
        Assert.Null(outcome.AcceptedCandidate);
    }

    [Fact]
    public async Task AdditionalFileSensitiveGeneratorChangeFailsClosed()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        await using var fixture = await RealLoaderEngineFixture.CreateAsync(
            enableDocumentationSensitiveGenerator: false,
            enableNoOutputToOutputGenerator: false,
            enableAdditionalDocumentationSensitiveGenerator: true);
        Assert.Contains(fixture.Repository.GeneratedSources, fact =>
            fact.SourceText.Contains(
                "FixtureAdditionalDocumentationSensitive { public const int Count = 0; }",
                StringComparison.Ordinal));

        var outcome = new DocumentationPatchEngine(
            () => fixture.StagingParent,
            null,
            null).Execute(fixture.ClassifiedSession, fixture.Request);

        var result = Assert.IsType<DocumentationPatchValidationResult>(outcome.Result);
        Assert.Equal(DocumentationPatchOutcome.Rejected, result.Outcome);
        Assert.All(result.Targets, target =>
            Assert.Equal(DocumentationPatchTargetStatus.Invalid, target.Status));
        Assert.Equal("patch.rejected.unsafe-change", Assert.Single(result.Diagnostics).Code);
        Assert.Null(outcome.AcceptedCandidate);
    }

    public static TheoryData<string, string, string, int> SupportedDeclarationShapeCases => new()
    {
        {
            "partial-type",
            "namespace N;\npublic partial class C { }\npublic partial class C { }\n",
            "T:N.C",
            0
        },
        {
            "partial-method",
            "namespace N;\npublic partial class C\n{\n    public partial void Run(string defining);\n    public partial void Run(string implementing) { }\n}\n",
            "M:N.C.Run(System.String)",
            0
        },
        {
            "record",
            "namespace N;\npublic record Row\n{\n    public int Value { get; init; }\n}\n",
            "T:N.Row",
            0
        },
        {
            "operator",
            "namespace N;\npublic sealed class C\n{\n    public static C operator +(C left, C right) => left;\n}\n",
            "M:N.C.op_Addition(N.C,N.C)",
            0
        },
        {
            "conversion",
            "namespace N;\npublic sealed class C\n{\n    public static implicit operator int(C value) => 0;\n}\n",
            "M:N.C.op_Implicit(N.C)~System.Int32",
            0
        },
        {
            "indexer",
            "namespace N;\npublic sealed class C\n{\n    public int this[int index] => index;\n}\n",
            "P:N.C.Item(System.Int32)",
            0
        },
        {
            "event",
            "using System;\nnamespace N;\npublic sealed class C\n{\n    public event Action? Changed;\n}\n",
            "E:N.C.Changed",
            0
        },
        {
            "delegate",
            "namespace N;\npublic delegate TResult Transform<TValue, TResult>(TValue value);\n",
            "T:N.Transform`2",
            0
        },
        {
            "override-inheritdoc",
            "namespace N;\npublic abstract class Base\n{\n    public virtual void M() { }\n}\npublic sealed class C : Base\n{\n    public override void M() { }\n}\n",
            "M:N.C.M",
            0
        },
        {
            "interface-declaration-with-explicit-implementation-relationship",
            "namespace N;\npublic interface IContract\n{\n    void M();\n}\npublic sealed class C : IContract\n{\n    void IContract.M() { }\n}\n",
            "M:N.IContract.M",
            0
        },
    };

    [Theory]
    [MemberData(nameof(SupportedDeclarationShapeCases))]
    public void FinalEngineAcceptsTheFrozenDeclarationShapeMatrix(
        string caseName,
        string source,
        string documentationCommentId,
        int declarationReferenceIndex)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var fixture = EngineFixture.Create(
            source,
            [new EngineTarget(documentationCommentId, declarationReferenceIndex)]);

        var outcome = new DocumentationPatchEngine(
            () => fixture.StagingParent,
            null,
            null).Execute(fixture.ClassifiedSession, fixture.Request);

        Assert.True(
            outcome.Status == DocumentationPatchExecutionStatus.Result,
            $"{caseName}:status={outcome.Status};failure={outcome.FailureCode}");
        var result = Assert.IsType<DocumentationPatchValidationResult>(outcome.Result);
        Assert.True(
            result.Outcome == DocumentationPatchOutcome.Accepted,
            caseName + ":" + string.Join(',', result.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Code}:{diagnostic.BlockId}")));
        Assert.Single(result.Targets);
        Assert.All(result.Targets, target =>
            Assert.Equal(DocumentationPatchTargetStatus.Valid, target.Status));
        Assert.All(result.Invariants, invariant =>
            Assert.Equal(DocumentationPatchInvariantStatus.Passed, invariant.Status));
        Assert.NotNull(outcome.AcceptedCandidate);
    }

    [Fact]
    public void FinalEngineRejectsExplicitInterfaceImplementationAsOutsideTheSelectedSurface()
    {
        const string source =
            "namespace N;\npublic interface IContract\n{\n    void M();\n}\npublic sealed class C : IContract\n{\n    void IContract.M() { }\n}\n";
        using var fixture = EngineFixture.Create(
            source,
            [new EngineTarget(
                "M:N.C.N#IContract#M",
                RequireSupported: false,
                UseRelationSource: true,
                HydrateComponents: false)]);

        var outcome = new DocumentationPatchEngine(
            () => fixture.StagingParent,
            null,
            null).Execute(fixture.ClassifiedSession, fixture.Request);

        var result = Assert.IsType<DocumentationPatchValidationResult>(outcome.Result);
        Assert.Equal(DocumentationPatchOutcome.Rejected, result.Outcome);
        Assert.Equal(
            DocumentationPatchTargetStatus.Invalid,
            Assert.Single(result.Targets).Status);
        Assert.Equal(
            "patch.rejected.unsupported-target",
            Assert.Single(result.Diagnostics).Code);
        Assert.Null(outcome.AcceptedCandidate);
    }

    [Fact]
    public void FinalEngineRejectsPrimaryConstructorOwnershipWithoutCapability()
    {
        const string source = "namespace N; public class Primary(int value) { }";
        using var fixture = EngineFixture.Create(
            source,
            [new EngineTarget(
                "M:N.Primary.#ctor(System.Int32)",
                RequireSupported: false)]);

        var outcome = new DocumentationPatchEngine(
            () => fixture.StagingParent,
            null,
            null).Execute(fixture.ClassifiedSession, fixture.Request);

        var result = Assert.IsType<DocumentationPatchValidationResult>(outcome.Result);
        Assert.Equal(DocumentationPatchOutcome.Rejected, result.Outcome);
        Assert.Equal(
            DocumentationPatchTargetStatus.Invalid,
            Assert.Single(result.Targets).Status);
        Assert.Equal(
            "patch.rejected.unsupported-target",
            Assert.Single(result.Diagnostics).Code);
        Assert.Null(outcome.AcceptedCandidate);
    }

    [Theory]
    [InlineData((int)LoadedSourceKind.ToolGenerated)]
    [InlineData((int)LoadedSourceKind.SourceGenerator)]
    public void FinalEngineRejectsGeneratedTargetsWithoutCapability(int sourceKindValue)
    {
        var sourceKind = (LoadedSourceKind)sourceKindValue;
        using var fixture = EngineFixture.CreateGenerated(sourceKind);

        var outcome = new DocumentationPatchEngine(
            () => fixture.StagingParent,
            null,
            null).Execute(fixture.ClassifiedSession, fixture.Request);

        var result = Assert.IsType<DocumentationPatchValidationResult>(outcome.Result);
        Assert.Equal(DocumentationPatchOutcome.Rejected, result.Outcome);
        Assert.Equal(
            DocumentationPatchTargetStatus.Invalid,
            Assert.Single(result.Targets).Status);
        Assert.Equal(
            "patch.rejected.non-writable-target",
            Assert.Single(result.Diagnostics).Code);
        Assert.Null(outcome.AcceptedCandidate);
    }

    [Fact]
    public void PositionShiftedUnresolvedDeclarationRemainsAccepted()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        const string source =
            "namespace N;\npublic class C\n{\n    public MissingType? Unresolved;\n    public void M() { }\n}\n";
        using var fixture = EngineFixture.Create(source);

        var outcome = new DocumentationPatchEngine(
            () => fixture.StagingParent,
            null,
            null).Execute(fixture.ClassifiedSession, fixture.Request);

        Assert.Equal(DocumentationPatchExecutionStatus.Result, outcome.Status);
        Assert.Equal(DocumentationPatchOutcome.Accepted, outcome.Result!.Outcome);
        Assert.NotNull(outcome.AcceptedCandidate);
    }

    [Fact]
    public void AcceptedExecutionReturnsImmutableBytesReleasesStagingAndPublicReplayIsStale()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var fixture = EngineFixture.Create();
        string? stagingRoot = null;
        var engine = new DocumentationPatchEngine(
            () => fixture.StagingParent,
            (stage, root) =>
            {
                if (stage == DocumentationPatchApplicationStage.AfterSealBeforeReturn)
                {
                    stagingRoot = root;
                }
            },
            null);

        var outcome = engine.Execute(fixture.ClassifiedSession, fixture.Request);

        Assert.Equal(DocumentationPatchExecutionStatus.Result, outcome.Status);
        var result = Assert.IsType<DocumentationPatchValidationResult>(outcome.Result);
        Assert.Equal(DocumentationPatchOutcome.Accepted, result.Outcome);
        Assert.All(result.Targets, target =>
            Assert.Equal(DocumentationPatchTargetStatus.Valid, target.Status));
        Assert.Empty(result.Diagnostics);
        Assert.All(result.Invariants, invariant =>
            Assert.Equal(DocumentationPatchInvariantStatus.Passed, invariant.Status));
        var capability = Assert.IsType<DocumentationPatchAcceptedCandidate>(
            outcome.AcceptedCandidate);
        Assert.Same(result, capability.Result);
        var source = Assert.Single(capability.Files, file =>
            file.RepositoryPath == "Sample.cs");
        Assert.Contains("/// <inheritdoc/>", Encoding.UTF8.GetString(source.Bytes.AsSpan()));
        Assert.Equal(Sha256(source.Bytes.AsSpan()), source.Sha256);
        Assert.Equal(capability.Files.Length, capability.IdentityEvidence.Length);
        var identity = Assert.Single(capability.IdentityEvidence, evidence =>
            evidence.RepositoryPath == "Sample.cs");
        Assert.False(identity.OriginalIdentity.IsDirectory);
        Assert.Equal(1UL, identity.OriginalIdentity.LinkCount);
        Assert.False(identity.CandidateIdentity.IsDirectory);
        Assert.Equal(1UL, identity.CandidateIdentity.LinkCount);
        Assert.Equal(source.Bytes.Length, identity.CandidateIdentity.Length);
        Assert.NotEqual(identity.OriginalIdentity.FileId, identity.CandidateIdentity.FileId);
        Assert.NotNull(stagingRoot);
        Assert.False(Directory.Exists(stagingRoot));

        var acceptedBytes = source.Bytes;
        File.WriteAllBytes(fixture.SourcePath, acceptedBytes.ToArray());
        var replay = new DocumentationPatchEngine(
            () => fixture.StagingParent,
            null,
            null).Execute(fixture.ClassifiedSession, fixture.Request);

        Assert.Equal(DocumentationPatchExecutionStatus.Result, replay.Status);
        Assert.Equal(DocumentationPatchOutcome.Stale, replay.Result!.Outcome);
        Assert.Null(replay.AcceptedCandidate);
        Assert.Equal(acceptedBytes, source.Bytes);
    }

    [Fact]
    public void CandidateMutationAtTerminalCaptureReturnsCandidateStateAndNoCapability()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var fixture = EngineFixture.Create();
        string? stagingRoot = null;
        var engine = new DocumentationPatchEngine(
            () => fixture.StagingParent,
            (stage, root) =>
            {
                if (stage == DocumentationPatchApplicationStage.AfterSealBeforeReturn)
                {
                    stagingRoot = root;
                }
            },
            stage =>
            {
                if (stage == DocumentationPatchEngineStage.BeforeCandidateTerminalPass)
                {
                    File.AppendAllText(
                        Path.Join(stagingRoot!, "Sample.cs"),
                        " ",
                        new UTF8Encoding(false));
                }
            });

        var outcome = engine.Execute(fixture.ClassifiedSession, fixture.Request);

        AssertRootExecution(
            outcome,
            DocumentationPatchOutcome.Rejected,
            DocumentationPatchTargetStatus.Valid,
            "patch.rejected.candidate-state");
        Assert.Null(outcome.AcceptedCandidate);
    }

    [Fact]
    public void StableCandidatePremodificationAfterE1HandoffReturnsCandidateState()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var fixture = EngineFixture.Create();
        var finalOriginalRebindObserved = false;
        var engine = new DocumentationPatchEngine(
            () => fixture.StagingParent,
            (stage, root) =>
            {
                if (stage != DocumentationPatchApplicationStage.AfterSealBeforeReturn)
                {
                    return;
                }

                var candidatePath = Path.Join(Assert.IsType<string>(root), "Sample.cs");
                var bytes = File.ReadAllBytes(candidatePath);
                var marker = Encoding.UTF8.GetBytes("inheritdoc");
                var offset = bytes.AsSpan().IndexOf(marker);
                Assert.True(offset >= 0);
                using var stream = new FileStream(
                    candidatePath,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.Read);
                stream.Position = offset;
                stream.WriteByte((byte)'x');
                stream.Flush(flushToDisk: true);
                Assert.Equal(bytes.Length, stream.Length);
            },
            stage =>
            {
                if (stage == DocumentationPatchEngineStage.BeforeFinalOriginalRebind)
                {
                    finalOriginalRebindObserved = true;
                }
            });

        var outcome = engine.Execute(fixture.ClassifiedSession, fixture.Request);

        Assert.True(finalOriginalRebindObserved);
        AssertRootExecution(
            outcome,
            DocumentationPatchOutcome.Rejected,
            DocumentationPatchTargetStatus.Valid,
            "patch.rejected.candidate-state");
        Assert.Null(outcome.AcceptedCandidate);
    }

    [Fact]
    public void OriginalMutationBeforeE1SealReturnsRepositoryStateAndCleansCandidate()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var fixture = EngineFixture.Create();
        string? stagingRoot = null;
        var engine = new DocumentationPatchEngine(
            () => fixture.StagingParent,
            (stage, root) =>
            {
                stagingRoot = root ?? stagingRoot;
                if (stage == DocumentationPatchApplicationStage.BeforeOriginalRebind)
                {
                    File.AppendAllText(
                        fixture.SourcePath,
                        " ",
                        new UTF8Encoding(false));
                }
            },
            null);

        var outcome = engine.Execute(fixture.ClassifiedSession, fixture.Request);

        AssertRootExecution(
            outcome,
            DocumentationPatchOutcome.Stale,
            DocumentationPatchTargetStatus.NotEvaluated,
            "patch.stale.repository-state");
        Assert.Null(outcome.AcceptedCandidate);
        Assert.NotNull(stagingRoot);
        Assert.False(Directory.Exists(stagingRoot));
    }

    [Fact]
    public void OriginalMutationImmediatelyBeforeCommitReturnsRepositoryState()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var fixture = EngineFixture.Create();
        var engine = new DocumentationPatchEngine(
            () => fixture.StagingParent,
            null,
            stage =>
            {
                if (stage == DocumentationPatchEngineStage.BeforeFinalOriginalRebind)
                {
                    File.AppendAllText(
                        fixture.SourcePath,
                        " ",
                        new UTF8Encoding(false));
                }
            });

        var outcome = engine.Execute(fixture.ClassifiedSession, fixture.Request);

        AssertRootExecution(
            outcome,
            DocumentationPatchOutcome.Stale,
            DocumentationPatchTargetStatus.NotEvaluated,
            "patch.stale.repository-state");
        Assert.Null(outcome.AcceptedCandidate);
    }

    [Fact]
    public void SimultaneousCandidateAndRepositoryMutationGivesRepositoryStatePrecedence()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var fixture = EngineFixture.Create();
        string? stagingRoot = null;
        var engine = new DocumentationPatchEngine(
            () => fixture.StagingParent,
            (stage, root) =>
            {
                if (stage == DocumentationPatchApplicationStage.AfterSealBeforeReturn)
                {
                    stagingRoot = root;
                }
            },
            stage =>
            {
                if (stage == DocumentationPatchEngineStage.BeforeCandidateTerminalPass)
                {
                    File.AppendAllText(
                        Path.Join(stagingRoot!, "Sample.cs"),
                        " ",
                        new UTF8Encoding(false));
                }
                else if (stage == DocumentationPatchEngineStage.BeforeFinalOriginalRebind)
                {
                    File.AppendAllText(
                        fixture.SourcePath,
                        " ",
                        new UTF8Encoding(false));
                }
            });

        var outcome = engine.Execute(fixture.ClassifiedSession, fixture.Request);

        AssertRootExecution(
            outcome,
            DocumentationPatchOutcome.Stale,
            DocumentationPatchTargetStatus.NotEvaluated,
            "patch.stale.repository-state");
        Assert.Null(outcome.AcceptedCandidate);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("replaced-file")]
    [InlineData("hard-link")]
    [InlineData("symbolic-link")]
    [InlineData("replaced-root")]
    [InlineData("replaced-nested-directory")]
    [InlineData("symbolic-linked-directory")]
    public void CandidateInventoryIdentityAndTopologyChangesReturnCandidateState(
        string mutation)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var repositoryPath = mutation is "replaced-nested-directory"
            or "symbolic-linked-directory"
            ? "Nested/Sample.cs"
            : "Sample.cs";
        using var fixture = EngineFixture.Create(repositoryPath: repositoryPath);
        string? stagingRoot = null;
        var engine = new DocumentationPatchEngine(
            () => fixture.StagingParent,
            (stage, root) =>
            {
                if (stage == DocumentationPatchApplicationStage.AfterSealBeforeReturn)
                {
                    stagingRoot = root;
                }
            },
            stage =>
            {
                if (stage == DocumentationPatchEngineStage.BeforeCandidateTerminalPass)
                {
                    MutateCandidate(
                        Assert.IsType<string>(stagingRoot),
                        fixture.StagingParent,
                        repositoryPath,
                        mutation);
                }
            });

        var outcome = engine.Execute(fixture.ClassifiedSession, fixture.Request);

        AssertRootExecution(
            outcome,
            DocumentationPatchOutcome.Rejected,
            DocumentationPatchTargetStatus.Valid,
            "patch.rejected.candidate-state");
        Assert.Null(outcome.AcceptedCandidate);
    }

    [Theory]
    [InlineData("file", "before-e1-seal")]
    [InlineData("directory", "before-e1-seal")]
    [InlineData("file", "before-e2-commit")]
    [InlineData("directory", "before-e2-commit")]
    public void BoundedOriginalTopologyRejectionReturnsRepositoryState(
        string topology,
        string stageName)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var repositoryPath = topology == "directory"
            ? "Nested/Sample.cs"
            : "Sample.cs";
        using var fixture = EngineFixture.Create(repositoryPath: repositoryPath);
        var mutated = false;
        void Mutate()
        {
            if (mutated)
            {
                return;
            }

            mutated = true;
            ReplaceOriginalWithSymbolicAlias(fixture.SourcePath, topology);
        }

        var engine = new DocumentationPatchEngine(
            () => fixture.StagingParent,
            (stage, _) =>
            {
                if (stageName == "before-e1-seal"
                    && stage == DocumentationPatchApplicationStage.BeforeOriginalRebind)
                {
                    Mutate();
                }
            },
            stage =>
            {
                if (stageName == "before-e2-commit"
                    && stage == DocumentationPatchEngineStage.BeforeFinalOriginalRebind)
                {
                    Mutate();
                }
            });

        var outcome = engine.Execute(fixture.ClassifiedSession, fixture.Request);

        AssertRootExecution(
            outcome,
            DocumentationPatchOutcome.Stale,
            DocumentationPatchTargetStatus.NotEvaluated,
            "patch.stale.repository-state");
        Assert.Null(outcome.AcceptedCandidate);
    }

    [Fact]
    public void BoundedOriginalTopologyRejectionWinsOverCandidateMismatch()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var fixture = EngineFixture.Create();
        string? stagingRoot = null;
        var engine = new DocumentationPatchEngine(
            () => fixture.StagingParent,
            (stage, root) =>
            {
                if (stage == DocumentationPatchApplicationStage.AfterSealBeforeReturn)
                {
                    stagingRoot = root;
                }
            },
            stage =>
            {
                if (stage == DocumentationPatchEngineStage.BeforeCandidateTerminalPass)
                {
                    File.AppendAllText(
                        Path.Join(stagingRoot!, "Sample.cs"),
                        " ",
                        new UTF8Encoding(false));
                }
                else if (stage == DocumentationPatchEngineStage.BeforeFinalOriginalRebind)
                {
                    ReplaceOriginalWithSymbolicAlias(fixture.SourcePath, "file");
                }
            });

        var outcome = engine.Execute(fixture.ClassifiedSession, fixture.Request);

        AssertRootExecution(
            outcome,
            DocumentationPatchOutcome.Stale,
            DocumentationPatchTargetStatus.NotEvaluated,
            "patch.stale.repository-state");
        Assert.Null(outcome.AcceptedCandidate);
    }

    [Fact]
    public void CancellationIsNotConvertedIntoAResultOrHostFailure()
    {
        using var fixture = EngineFixture.Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var engine = new DocumentationPatchEngine(
            () => fixture.StagingParent,
            null,
            null);

        Assert.Throws<OperationCanceledException>(() =>
            engine.Execute(fixture.ClassifiedSession, fixture.Request, cancellation.Token));
    }

    [Theory]
    [InlineData("namespace N;\r\npublic class C\n{\r\n    public void M() { }\r\n}\r\n")]
    [InlineData("namespace N;\rpublic class C { public void M() { } }")]
    public void RepresentationFailureBeforeMaterializationIsAProductRejection(string source)
    {
        using var fixture = EngineFixture.Create(source);

        var outcome = new DocumentationPatchEngine(
            () => fixture.StagingParent,
            null,
            null).Execute(fixture.ClassifiedSession, fixture.Request);

        Assert.Equal(DocumentationPatchExecutionStatus.Result, outcome.Status);
        var result = Assert.IsType<DocumentationPatchValidationResult>(outcome.Result);
        Assert.Equal(DocumentationPatchOutcome.Rejected, result.Outcome);
        Assert.Equal(
            DocumentationPatchTargetStatus.Invalid,
            Assert.Single(result.Targets).Status);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("patch.rejected.unsafe-change", diagnostic.Code);
        Assert.Equal("block-1", diagnostic.BlockId);
        Assert.Null(outcome.AcceptedCandidate);
    }

    [Fact]
    public void CandidateMaterializationFailureIsANonResultHostFailure()
    {
        using var fixture = EngineFixture.Create();
        var engine = new DocumentationPatchEngine(
            () => fixture.SourcePath,
            null,
            null);

        var outcome = engine.Execute(fixture.ClassifiedSession, fixture.Request);

        Assert.Equal(DocumentationPatchExecutionStatus.HostFailure, outcome.Status);
        Assert.Equal("patch.host.environment-failure", outcome.FailureCode);
        Assert.Null(outcome.Result);
        Assert.Null(outcome.AcceptedCandidate);
    }

    private static void AssertRootExecution(
        DocumentationPatchExecutionOutcome outcome,
        DocumentationPatchOutcome expectedOutcome,
        DocumentationPatchTargetStatus expectedTargetStatus,
        string expectedCode)
    {
        Assert.Equal(DocumentationPatchExecutionStatus.Result, outcome.Status);
        var result = Assert.IsType<DocumentationPatchValidationResult>(outcome.Result);
        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.All(result.Targets, target => Assert.Equal(expectedTargetStatus, target.Status));
        Assert.Empty(result.ChangedFiles);
        Assert.Equal(0, result.ChangedDocumentationBlockCount);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(expectedCode, diagnostic.Code);
        Assert.Null(diagnostic.BlockId);
        Assert.Null(diagnostic.Path);
        Assert.Null(diagnostic.Pointer);
        Assert.Equal(
            DocumentationPatchInvariantStatus.Passed,
            Assert.Single(result.Invariants, invariant =>
                invariant.Id == "patch.invariant.fail-closed").Status);
        Assert.All(result.Invariants.Where(invariant =>
            invariant.Id != "patch.invariant.fail-closed"), invariant =>
            Assert.Equal(DocumentationPatchInvariantStatus.NotRun, invariant.Status));
    }

    private static void MutateCandidate(
        string stagingRoot,
        string stagingParent,
        string repositoryPath,
        string mutation)
    {
        var candidatePath = Path.Join(
            stagingRoot,
            repositoryPath.Replace('/', Path.DirectorySeparatorChar));
        switch (mutation)
        {
            case "missing":
                File.Delete(candidatePath);
                break;
            case "extra":
                File.WriteAllText(
                    Path.Join(stagingRoot, "Extra.cs"),
                    "// extra",
                    new UTF8Encoding(false));
                break;
            case "replaced-file":
                var replacementBytes = File.ReadAllBytes(candidatePath);
                File.Delete(candidatePath);
                File.WriteAllBytes(candidatePath, replacementBytes);
                break;
            case "hard-link":
                var hardLinkTarget = Path.Join(
                    stagingParent,
                    "hard-link-target-" + Guid.NewGuid().ToString("N"));
                File.WriteAllBytes(hardLinkTarget, File.ReadAllBytes(candidatePath));
                File.Delete(candidatePath);
                Assert.Equal(0, Link(hardLinkTarget, candidatePath));
                break;
            case "symbolic-link":
                var symbolicTarget = Path.Join(
                    stagingParent,
                    "symbolic-target-" + Guid.NewGuid().ToString("N"));
                File.WriteAllBytes(symbolicTarget, File.ReadAllBytes(candidatePath));
                File.Delete(candidatePath);
                File.CreateSymbolicLink(candidatePath, symbolicTarget);
                break;
            case "replaced-root":
                var movedRoot = stagingRoot + "-original";
                Directory.Move(stagingRoot, movedRoot);
                Directory.CreateDirectory(stagingRoot);
                break;
            case "replaced-nested-directory":
                ReplaceCandidateDirectory(stagingRoot, stagingParent, useSymbolicLink: false);
                break;
            case "symbolic-linked-directory":
                ReplaceCandidateDirectory(stagingRoot, stagingParent, useSymbolicLink: true);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }
    }

    private static void ReplaceCandidateDirectory(
        string stagingRoot,
        string stagingParent,
        bool useSymbolicLink)
    {
        var nested = Path.Join(stagingRoot, "Nested");
        var moved = Path.Join(
            stagingParent,
            "candidate-nested-original-" + Guid.NewGuid().ToString("N"));
        Directory.Move(nested, moved);
        if (useSymbolicLink)
        {
            Directory.CreateSymbolicLink(nested, moved);
            return;
        }

        Directory.CreateDirectory(nested);
        File.Copy(Path.Join(moved, "Sample.cs"), Path.Join(nested, "Sample.cs"));
    }

    private static void ReplaceOriginalWithSymbolicAlias(string sourcePath, string topology)
    {
        if (topology == "file")
        {
            var moved = sourcePath + ".original";
            File.Move(sourcePath, moved);
            File.CreateSymbolicLink(sourcePath, moved);
            return;
        }

        var directory = Path.GetDirectoryName(sourcePath)!;
        var movedDirectory = directory + ".original";
        Directory.Move(directory, movedDirectory);
        Directory.CreateSymbolicLink(directory, movedDirectory);
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(string existingPath, string newPath);

    private sealed record EngineTarget(
        string DocumentationCommentId,
        int DeclarationReferenceIndex = 0,
        bool RequireSupported = true,
        bool UseRelationSource = false,
        bool HydrateComponents = true);

    private sealed class EngineFixture : IDisposable
    {
        private static readonly ImmutableArray<MetadataReference> PlatformReferences =
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator)
                .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
                .ToImmutableArray();

        private readonly LoadedRepositorySession repository;

        private EngineFixture(
            string root,
            string sourcePath,
            string stagingParent,
            LoadedRepositorySession repository,
            ClassifiedRepositorySession classifiedSession,
            DocumentationPatchRequest request)
        {
            Root = root;
            SourcePath = sourcePath;
            StagingParent = stagingParent;
            this.repository = repository;
            ClassifiedSession = classifiedSession;
            Request = request;
        }

        public string Root { get; }

        public string SourcePath { get; }

        public string StagingParent { get; }

        public ClassifiedRepositorySession ClassifiedSession { get; }

        public DocumentationPatchRequest Request { get; }

        public static EngineFixture Create(
            string? source = null,
            IReadOnlyList<EngineTarget>? requestedTargets = null,
            string repositoryPath = "Sample.cs")
        {
            source ??=
                "namespace N;\npublic class C\n{\n    public void M() { }\n}\n";
            requestedTargets ??= [new EngineTarget("M:N.C.M")];
            var root = Path.Join(
                Path.GetTempPath(),
                "contract-scribe-patch-engine-source-" + Guid.NewGuid().ToString("N"));
            var stagingParent = Path.Join(
                Path.GetTempPath(),
                "contract-scribe-patch-engine-staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(stagingParent);
            var sourcePath = Path.Join(
                root,
                repositoryPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            var projectPath = Path.Join(root, "Fixture.csproj");
            File.WriteAllText(sourcePath, source, new UTF8Encoding(false));
            File.WriteAllText(projectPath, "<Project />", new UTF8Encoding(false));

            var workspace = new AdhocWorkspace();
            var projectId = ProjectId.CreateNewId();
            var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                "Fixture",
                "Fixture",
                LanguageNames.CSharp,
                filePath: projectPath,
                compilationOptions: new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary),
                parseOptions: new CSharpParseOptions(
                    LanguageVersion.Preview,
                    documentationMode: DocumentationMode.Diagnose),
                metadataReferences: PlatformReferences));
            var documentId = DocumentId.CreateNewId(projectId);
            solution = solution.AddDocument(DocumentInfo.Create(
                documentId,
                Path.GetFileName(repositoryPath),
                filePath: sourcePath,
                loader: TextLoader.From(TextAndVersion.Create(
                    SourceText.From(source, new UTF8Encoding(false, true)),
                    VersionStamp.Create(),
                    sourcePath))));
            Assert.True(workspace.TryApplyChanges(solution));
            var project = workspace.CurrentSolution.GetProject(projectId)!;
            var document = project.GetDocument(documentId)!;
            var tree = document.GetSyntaxTreeAsync().GetAwaiter().GetResult()!;
            var compilation = project.GetCompilationAsync().GetAwaiter().GetResult()!;
            var loaded = new LoadedProject(
                "Fixture.csproj",
                "net10.0",
                "fixture.net10.0",
                LoadedProjectRole.AuditRoot,
                [],
                project,
                compilation,
                new Dictionary<SyntaxTree, LoadedSourceTree>(ReferenceEqualityComparer.Instance)
                {
                    [tree] = new(
                        LoadedSourceKind.Repository,
                        repositoryPath,
                        new RepositoryPathResolver().PhysicalIdentity(root, sourcePath),
                        null),
                });
            Assert.True(RepositoryContextRef.TryParse(
                "repoctx-0123456789abcdef0123456789abcdef",
                out var repositoryContextRef));
            var repository = new LoadedRepositorySession(
                repositoryContextRef,
                root,
                "Fixture.csproj",
                new ToolchainIdentity("test", "test", "test", "test"),
                [loaded],
                [],
                workspace);
            repository.SealDocumentationPatchRepositoryPolicyForTests([stagingParent]);
            var classified = new SymbolClassifier().ClassifySession(
                repository,
                TargetProfile.ExternalApi);
            var selected = requestedTargets.Select((requested, index) =>
            {
                var symbolRef = requested.UseRelationSource
                    ? Assert.Single(
                        classified.Classification.ClassificationSet!.Relations,
                        relation => relation.RelationKind
                                == RelationKind.ExplicitInterfaceImplementation
                            && relation.SourceSymbolRef.DocumentationCommentId
                                == requested.DocumentationCommentId).SourceSymbolRef
                    : Assert.Single(
                        classified.Classification.ClassificationSet!.Targets,
                        candidate => candidate.SymbolRef.DocumentationCommentId
                                == requested.DocumentationCommentId
                            && (!requested.RequireSupported
                                || candidate.SupportStatus == SupportStatus.Supported)).SymbolRef;
                var symbol = Assert.Single(DocumentationCommentId.GetSymbolsForDeclarationId(
                    symbolRef.DocumentationCommentId,
                    compilation));
                var references = symbol.DeclaringSyntaxReferences;
                Assert.InRange(requested.DeclarationReferenceIndex, 0, references.Length - 1);
                return (SymbolRef: symbolRef,
                    Reference: references[requested.DeclarationReferenceIndex],
                    BlockId: $"block-{index + 1}",
                    requested.HydrateComponents);
            }).ToArray();
            var provisionalRequest = new DocumentationPatchRequest(
                new string('0', 64),
                new DocumentationPatchContext(
                    repositoryContextRef,
                    "Fixture.csproj",
                    TargetProfile.ExternalApi),
                [],
                selected.Select(item => new DocumentationPatchBlockRequest(
                        item.BlockId,
                        item.SymbolRef,
                        new DocumentationPatchRepositoryLocator(
                            repositoryPath,
                            Sha256(File.ReadAllBytes(sourcePath)),
                            DocumentationPatchRepositoryEncoding.Utf8,
                            DocumentationObservationInput.Span(
                                item.Reference.Span.Start,
                                item.Reference.Span.End)),
                        DocumentationPatchEditKind.Insert,
                        [],
                        new DocumentationPatchInheritDocContent(),
                        []))
                    .ToImmutableArray());
            var request = provisionalRequest;
            if (selected.All(item => item.HydrateComponents))
            {
                var declarationBatch = new DocumentationPatchDeclarationResolver().Resolve(
                    classified,
                    provisionalRequest);
                Assert.Null(declarationBatch.RootFailureCode);
                request = new DocumentationPatchRequest(
                new string('0', 64),
                new DocumentationPatchContext(
                    repositoryContextRef,
                    "Fixture.csproj",
                    TargetProfile.ExternalApi),
                [],
                provisionalRequest.Blocks.Select(block =>
                {
                    var declarationBlock = Assert.Single(
                        declarationBatch.Blocks,
                        candidate => candidate.BlockId == block.BlockId);
                    Assert.Empty(declarationBlock.Failures);
                    var declaration = Assert.IsType<DocumentationPatchResolvedDeclaration>(
                        declarationBlock.Declaration);
                    return new DocumentationPatchBlockRequest(
                        block.BlockId,
                        block.SymbolRef,
                        block.Locator,
                        block.EditKind,
                        declaration.ApplicableComponents.Select(component =>
                                new DocumentationPatchApplicableComponent(
                                    component.Kind,
                                    component.Identity,
                                    component.Name))
                            .ToImmutableArray(),
                        block.Content,
                        block.ProvenanceRefs);
                }).ToImmutableArray());
            }
            return new EngineFixture(
                root,
                sourcePath,
                stagingParent,
                repository,
                classified,
                request);
        }

        public static EngineFixture CreateGenerated(LoadedSourceKind sourceKind)
        {
            Assert.True(sourceKind is LoadedSourceKind.ToolGenerated
                or LoadedSourceKind.SourceGenerator);
            const string source =
                "namespace N; public static class Generated { public static void M() { } }";
            var root = Path.Join(
                Path.GetTempPath(),
                "contract-scribe-patch-engine-generated-" + Guid.NewGuid().ToString("N"));
            var stagingParent = Path.Join(
                Path.GetTempPath(),
                "contract-scribe-patch-engine-generated-staging-"
                + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(stagingParent);
            var sourcePath = Path.Join(root, "Generated.g.cs");
            var projectPath = Path.Join(root, "Fixture.csproj");
            File.WriteAllText(sourcePath, source, new UTF8Encoding(false));
            File.WriteAllText(projectPath, "<Project />", new UTF8Encoding(false));

            var workspace = new AdhocWorkspace();
            var projectId = ProjectId.CreateNewId();
            var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                "Fixture",
                "Fixture",
                LanguageNames.CSharp,
                filePath: projectPath,
                compilationOptions: new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary),
                parseOptions: new CSharpParseOptions(
                    LanguageVersion.Preview,
                    documentationMode: DocumentationMode.Diagnose),
                metadataReferences: PlatformReferences));
            var documentId = DocumentId.CreateNewId(projectId);
            solution = solution.AddDocument(DocumentInfo.Create(
                documentId,
                "Generated.g.cs",
                filePath: sourcePath,
                loader: TextLoader.From(TextAndVersion.Create(
                    SourceText.From(source, new UTF8Encoding(false, true)),
                    VersionStamp.Create(),
                    sourcePath))));
            Assert.True(workspace.TryApplyChanges(solution));
            var project = workspace.CurrentSolution.GetProject(projectId)!;
            var document = project.GetDocument(documentId)!;
            var tree = document.GetSyntaxTreeAsync().GetAwaiter().GetResult()!;
            var compilation = project.GetCompilationAsync().GetAwaiter().GetResult()!;
            var sourceBytes = Encoding.UTF8.GetBytes(source);
            var producerId = (sourceKind == LoadedSourceKind.SourceGenerator
                    ? "sgp."
                    : "tgp.")
                + new string('1', 64);
            var outputId = (sourceKind == LoadedSourceKind.SourceGenerator
                    ? "sgo."
                    : "tgo.")
                + new string('2', 64);
            var generatedFact = new GeneratedSourceFact(
                "Fixture.csproj",
                "fixture.net10.0",
                producerId,
                outputId,
                Sha256(sourceBytes),
                source);
            var loaded = new LoadedProject(
                "Fixture.csproj",
                "net10.0",
                "fixture.net10.0",
                LoadedProjectRole.AuditRoot,
                [],
                project,
                compilation,
                new Dictionary<SyntaxTree, LoadedSourceTree>(ReferenceEqualityComparer.Instance)
                {
                    [tree] = new(sourceKind, null, null, generatedFact),
                });
            Assert.True(RepositoryContextRef.TryParse(
                "repoctx-0123456789abcdef0123456789abcdef",
                out var repositoryContextRef));
            var repository = new LoadedRepositorySession(
                repositoryContextRef,
                root,
                "Fixture.csproj",
                new ToolchainIdentity("test", "test", "test", "test"),
                [loaded],
                [generatedFact],
                workspace);
            repository.SealDocumentationPatchRepositoryPolicyForTests([stagingParent]);
            var classified = new SymbolClassifier().ClassifySession(
                repository,
                TargetProfile.ExternalApi);
            var target = Assert.Single(
                classified.Classification.ClassificationSet!.Targets,
                candidate => candidate.SymbolRef.DocumentationCommentId == "M:N.Generated.M");
            var symbol = Assert.Single(DocumentationCommentId.GetSymbolsForDeclarationId(
                target.SymbolRef.DocumentationCommentId,
                compilation));
            var reference = Assert.Single(symbol.DeclaringSyntaxReferences);
            DocumentationPatchSourceLocator locator = sourceKind
                == LoadedSourceKind.SourceGenerator
                ? new DocumentationPatchSourceGeneratorLocator(
                    producerId,
                    outputId,
                    generatedFact.SourceSha256,
                    DocumentationObservationInput.Span(
                        reference.Span.Start,
                        reference.Span.End))
                : new DocumentationPatchToolGeneratedLocator(
                    producerId,
                    outputId,
                    generatedFact.SourceSha256,
                    DocumentationObservationInput.Span(
                        reference.Span.Start,
                        reference.Span.End));
            var request = new DocumentationPatchRequest(
                new string('0', 64),
                new DocumentationPatchContext(
                    repositoryContextRef,
                    "Fixture.csproj",
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
            return new EngineFixture(
                root,
                sourcePath,
                stagingParent,
                repository,
                classified,
                request);
        }

        public void Dispose()
        {
            repository.Dispose();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }

            if (Directory.Exists(StagingParent))
            {
                Directory.Delete(StagingParent, recursive: true);
            }
        }
    }

    private sealed class RealLoaderEngineFixture : IAsyncDisposable
    {
        private readonly LoaderFixture loaderFixture;

        private RealLoaderEngineFixture(
            LoaderFixture loaderFixture,
            LoadedRepositorySession repository,
            string stagingParent,
            ClassifiedRepositorySession classifiedSession,
            DocumentationPatchRequest request)
        {
            this.loaderFixture = loaderFixture;
            Repository = repository;
            StagingParent = stagingParent;
            ClassifiedSession = classifiedSession;
            Request = request;
        }

        public LoadedRepositorySession Repository { get; }

        public string StagingParent { get; }

        public ClassifiedRepositorySession ClassifiedSession { get; }

        public DocumentationPatchRequest Request { get; }

        public static async Task<RealLoaderEngineFixture> CreateAsync(
            bool enableDocumentationSensitiveGenerator,
            bool enableNoOutputToOutputGenerator,
            bool multiTarget = false,
            bool enableAdditionalDocumentationSensitiveGenerator = false,
            bool enableSelfObservingGenerator = false,
            bool enableStableGeneratorDiagnostic = false)
        {
            var generatorProperties = new StringBuilder();
            var compilerVisibleProperties = new StringBuilder();
            if (enableDocumentationSensitiveGenerator)
            {
                generatorProperties.AppendLine(
                    "    <ContractScribeTestGeneratorDocumentationSensitive>true</ContractScribeTestGeneratorDocumentationSensitive>");
                compilerVisibleProperties.AppendLine(
                    "    <CompilerVisibleProperty Include=\"ContractScribeTestGeneratorDocumentationSensitive\" />");
            }

            if (enableNoOutputToOutputGenerator)
            {
                generatorProperties.AppendLine(
                    "    <ContractScribeTestGeneratorNoOutputToOutput>true</ContractScribeTestGeneratorNoOutputToOutput>");
                compilerVisibleProperties.AppendLine(
                    "    <CompilerVisibleProperty Include=\"ContractScribeTestGeneratorNoOutputToOutput\" />");
            }

            if (enableAdditionalDocumentationSensitiveGenerator)
            {
                generatorProperties.AppendLine(
                    "    <ContractScribeTestGeneratorAdditionalDocumentationSensitive>true</ContractScribeTestGeneratorAdditionalDocumentationSensitive>");
                compilerVisibleProperties.AppendLine(
                    "    <CompilerVisibleProperty Include=\"ContractScribeTestGeneratorAdditionalDocumentationSensitive\" />");
            }

            if (enableStableGeneratorDiagnostic)
            {
                generatorProperties.AppendLine(
                    "    <ContractScribeTestGeneratorStableDiagnostic>true</ContractScribeTestGeneratorStableDiagnostic>");
                compilerVisibleProperties.AppendLine(
                    "    <CompilerVisibleProperty Include=\"ContractScribeTestGeneratorStableDiagnostic\" />");
            }

            var appProject = $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <DefineConstants>APP_CONTEXT</DefineConstants>
                {{generatorProperties}}  </PropertyGroup>
                  <ItemGroup>
                    <AdditionalFiles Include="Target.cs" Link="Logical/Input/Target-copy.cs" />
                    <EditorConfigFiles Include="Target.cs" Link="Logical/Config/Target-as-config.cs" />
                {{compilerVisibleProperties}}  </ItemGroup>
                  <Target Name="CreateDocumentationPatchTarget" BeforeTargets="BeforeBuild" Condition="!Exists('$(MSBuildProjectDirectory)/Target.cs')">
                    <WriteLinesToFile File="$(MSBuildProjectDirectory)/Target.cs" Lines="internal static class DocumentationPatchTargetSeed { }" Overwrite="true" />
                  </Target>
                </Project>
                """;
            const string libraryProject = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <DefineConstants>LIBRARY_CONTEXT</DefineConstants>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="../App/App.cs" Link="Logical/Library/App-linked.cs" />
                  </ItemGroup>
                </Project>
                """;
            var loaderFixture = await LoaderFixture.CreateAsync(
                appProject: appProject,
                libraryProject: libraryProject,
                withGenerator: true,
                selfObservingGenerator: enableSelfObservingGenerator);
            try
            {
                var source = multiTarget
                    ? "namespace N;\npublic class RealApi\n{\n    public void A() { }\n    public void B() { }\n}\n"
                    : "namespace N;\npublic class RealApi\n{\n    public void M() { }\n}\n";
                var sourcePath = Path.Join(loaderFixture.Root, "App", "Target.cs");
                await File.WriteAllTextAsync(sourcePath, source, new UTF8Encoding(false));
                await File.WriteAllTextAsync(
                    Path.Join(loaderFixture.Root, "App", "App.cs"),
                    "#if APP_CONTEXT\nnamespace Contexts; internal static class LinkedContext { internal const string Value = \"app\"; }\n#elif LIBRARY_CONTEXT\nnamespace Contexts; internal static class LinkedContext { internal const string Value = \"library\"; }\n#endif\n",
                    new UTF8Encoding(false));
                var load = await new RepositoryLoader().LoadAsync(
                    new RepositoryLoadRequest(
                        loaderFixture.Root,
                        loaderFixture.SolutionPath,
                        [new ToolGeneratedSourceInput(
                            "App/App.csproj",
                            "ContractScribe",
                            "DocumentationPatchFixture",
                            "RetainedToolGenerated",
                            "namespace Tooling; internal static class RetainedToolGenerated { internal const int Value = 1; }")]));
                if (load.Status != RepositoryLoadStatus.Success || load.Session is null)
                {
                    throw new InvalidOperationException(
                        $"{load.PrimaryFailure?.Stage}:{load.PrimaryFailure?.Code}");
                }

                var repository = load.Session;
                var stagingParent = Path.Join(
                    Path.GetTempPath(),
                    "contract-scribe-patch-real-loader-staging-"
                    + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(stagingParent);
                var classified = new SymbolClassifier().ClassifySession(
                    repository,
                    TargetProfile.ExternalApi);
                var targetProject = Assert.Single(repository.Projects, project =>
                    project.ProjectIdentity == "App/App.csproj");
                var documentationIds = multiTarget
                    ? new[] { "M:N.RealApi.A", "M:N.RealApi.B" }
                    : ["M:N.RealApi.M"];
                var targets = documentationIds.Select((documentationId, index) =>
                {
                    var target = Assert.Single(
                        classified.Classification.ClassificationSet!.Targets,
                            candidate => candidate.SymbolRef.DocumentationCommentId == documentationId
                                && candidate.SymbolRef.CompilationContextRef
                                == targetProject.CompilationContextRef
                                && candidate.SupportStatus == SupportStatus.Supported);
                    Assert.Equal(ClassificationOrigin.Source, target.Origin);
                    var symbol = Assert.Single(
                        DocumentationCommentId.GetSymbolsForDeclarationId(
                            target.SymbolRef.DocumentationCommentId,
                            targetProject.Compilation));
                    return (Target: target,
                        Reference: Assert.Single(symbol.DeclaringSyntaxReferences),
                        BlockId: $"block-{index + 1}");
                }).ToArray();
                var loadedSource = targetProject.SourceTrees[targets[0].Reference.SyntaxTree];
                var repositoryPath = Assert.IsType<string>(loadedSource.RepositoryPath);
                var bytes = await File.ReadAllBytesAsync(sourcePath);
                var request = new DocumentationPatchRequest(
                    new string('0', 64),
                    new DocumentationPatchContext(
                        repository.RepositoryContextRef,
                        repository.InputIdentity,
                        TargetProfile.ExternalApi),
                    [],
                    targets.Select(item => new DocumentationPatchBlockRequest(
                            item.BlockId,
                            item.Target.SymbolRef,
                            new DocumentationPatchRepositoryLocator(
                                repositoryPath,
                                Sha256(bytes),
                                DocumentationPatchRepositoryEncoding.Utf8,
                                DocumentationObservationInput.Span(
                                    item.Reference.Span.Start,
                                    item.Reference.Span.End)),
                            DocumentationPatchEditKind.Insert,
                            [],
                            new DocumentationPatchInheritDocContent(),
                            []))
                        .ToImmutableArray());
                var declarationBatch = new DocumentationPatchDeclarationResolver().Resolve(
                    classified,
                    request);
                Assert.Null(declarationBatch.RootFailureCode);
                Assert.All(declarationBatch.Blocks, block => Assert.Empty(block.Failures));
                return new RealLoaderEngineFixture(
                    loaderFixture,
                    repository,
                    stagingParent,
                    classified,
                    request);
            }
            catch
            {
                await loaderFixture.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Repository.DisposeAsync();
            await loaderFixture.DisposeAsync();
            if (Directory.Exists(StagingParent))
            {
                Directory.Delete(StagingParent, recursive: true);
            }
        }
    }
}
